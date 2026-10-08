# Soford ERP Ubuntu 部署（无 Docker）

目标服务器：Ubuntu 22.04 x64，公网 IP `39.106.188.160`，主域名 `erp.soford.cn`（DNS 需先解析到该 IP）。主域名等待 ICP 接入或证书修复期间，生产备用入口为 `https://stepnex.cn/erp/`。

组成：Nginx（静态前端 + `/api`、`/openapi/callback` 反向代理）→ systemd 服务 `soford-erp-api`（`127.0.0.1:5153`）→ 数据目录 `/opt/soford-erp/app_data`。

## 一键部署 / 升级（Windows 上执行）

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy-upload.ps1 -Email you@soford.cn
```

脚本会：构建前后端 → 打包 `soford-erp-server.zip` → 上传 → 在服务器执行 `install-ubuntu.sh`。安装脚本可重复执行，每次升级用同一条命令：

1. 安装 nginx、certbot、ASP.NET Core 8 运行时（已安装则跳过）。
2. **先备份**现有数据，再替换程序文件。
3. 首次安装时从模板生成 `/opt/soford-erp/env/soford-api.env`；已存在则保留。
4. 安装每日备份定时器（03:30，保留最近 14 份，位于 `/opt/soford-erp/backups`）。
5. 有证书用 HTTPS 配置；没有证书且提供了 `-Email` 时自动申请 Let's Encrypt 证书（自动续期），失败则保持 HTTP。
6. 管理员密码不合格（少于 10 位，或为模板值、admin123 等常见值）时**不启动** API，并提示先修改；启动后等待健康检查通过。
7. 备份失败时恢复启动旧版本并中止升级，不会让服务停在半路。

## 首次部署后必须配置

```bash
ssh root@39.106.188.160
sudo nano /opt/soford-erp/env/soford-api.env
sudo systemctl restart soford-erp-api
```

| 键 | 说明 |
|----|------|
| `Auth__AdminPassword` | 至少 10 位的强密码（模板值或过短会拒绝启动） |
| `Alibaba__AppKey` / `Alibaba__AppSecret` | App Console 中的应用凭证 |
| `Alibaba__OAuthCallbackUrl` | 必须与 App Console 登记的回调地址一致；当前线上使用 `https://stepnex.cn/erp/openapi/callback` |
| `Soford__AutoSync__IntervalMinutes` | 可选，自动同步间隔（默认 30 分钟，0 关闭） |
| `Soford__AutoSync__PullHours` | 可选，每隔多少小时自动从 Alibaba 读取一次全部商品（默认 24，0 关闭） |
| `Anthropic__ApiKey` | 可选，填写后编辑器出现「AI 建议标题和关键词」（使用 Claude，按调用量计费；留空则不显示） |

然后浏览器打开当前可用入口 `https://stepnex.cn/erp/` →「店铺连接」自检 → 授权店铺。

## 备用入口 stepnex.cn/erp

stepnex.cn 的 nginx 站点同时服务博客等其他应用，ERP 只占用 `/erp` 前缀。配置见 [nginx/snippets/stepnex-erp.locations.conf](nginx/snippets/stepnex-erp.locations.conf)（文件开头有安装说明，CI 会对它做 `nginx -t`）：

1. 复制到 `/etc/nginx/snippets/`，在 stepnex.cn 的 443 server 块里 `include`；80 端口块里把 `/erp` 重定向到 HTTPS。
2. `soford-api.env` 中 `AllowedHosts` 包含 `stepnex.cn`，回调与跳转地址使用 `https://stepnex.cn/erp/...`（模板已按此填写）；App Console 登记的回调地址同步修改。
3. 前端构建使用相对路径，同一份产物可在 `/` 或 `/erp/` 下运行，无需重新构建。

注意：ERP 与同域名的其他网站属于同一来源，跨站请求防护无法区分它们。主域名备案完成后应尽快切回 `erp.soford.cn`（改回上面两个地址和 App Console 回调）。

## 手动步骤（不使用 deploy-upload.ps1 时）

```powershell
powershell -ExecutionPolicy Bypass -File .\publish-server.ps1
scp soford-erp-server.zip root@39.106.188.160:~/
scp deploy/install-ubuntu.sh root@39.106.188.160:~/install-soford-erp.sh
ssh -t root@39.106.188.160 "bash ~/install-soford-erp.sh erp.soford.cn you@soford.cn"
```

## 运维

```bash
sudo systemctl status soford-erp-api          # 状态
sudo journalctl -u soford-erp-api -f          # 日志
curl http://127.0.0.1:5153/api/health         # 健康检查
sudo systemctl start soford-erp-backup        # 立即备份
ls -lh /opt/soford-erp/backups                # 备份列表
```

恢复备份：

```bash
sudo systemctl stop soford-erp-api
sudo tar -xzf /opt/soford-erp/backups/soford-erp-<时间>.tar.gz -C /opt/soford-erp
sudo chown -R soford:soford /opt/soford-erp/app_data
sudo systemctl start soford-erp-api
```

注意：`app_data/data-protection-keys` 与 `alibaba-accounts.json` 必须一起保留，缺少密钥则 Token 无法解密，只能重新授权（系统会把解不开的账号文件改名为 `alibaba-accounts.json.unreadable-时间` 保留，找回密钥后可改回原名）。

### 运行账号与沙箱

API 以独立的系统账号 `soford` 运行（安装脚本自动创建），nginx 和服务器上的其他网站（`www-data`）读不到 Token、密钥和配置文件。systemd 还限制它只能写 `/opt/soford-erp/app_data`，程序目录和系统其他部分都是只读的。

如果升级后服务起不来，安装脚本会自动回滚到上一版的程序和服务配置；查看原因：`sudo journalctl -u soford-erp-api -n 50`。

### 异地备份

每日备份默认只保存在本机 `/opt/soford-erp/backups`，磁盘损坏会一起丢失。建议开启异地备份：

```bash
sudo cp /opt/soford-erp/bin/backup-offsite.example.sh /opt/soford-erp/env/backup-offsite.sh
sudo chmod 700 /opt/soford-erp/env/backup-offsite.sh
sudo nano /opt/soford-erp/env/backup-offsite.sh   # 按注释选择 ossutil（阿里云 OSS）或 rclone，并删掉最后的 exit 1
sudo systemctl start soford-erp-backup            # 试一次
sudo journalctl -u soford-erp-backup -n 20        # 应看到 Off-site copy done.
```

备份包里有 Token、数据保护密钥和配置文件（含 AppSecret、管理员密码），目标存储必须是私有的。建议每季度演练一次从异地备份恢复。
