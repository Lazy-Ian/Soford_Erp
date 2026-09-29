# Soford ERP Ubuntu 部署（无 Docker）

目标服务器：Ubuntu 22.04 x64，公网 IP `39.106.188.160`，域名 `erp.soford.cn`（DNS 需先解析到该 IP）。

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
| `Alibaba__OAuthCallbackUrl` | 必须与 App Console 登记的回调地址一致：`https://erp.soford.cn/openapi/callback` |
| `Soford__AutoSync__IntervalMinutes` | 可选，自动同步间隔（默认 30 分钟，0 关闭） |

然后浏览器打开 `https://erp.soford.cn` →「店铺连接」自检 → 授权店铺。

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
sudo chown -R www-data:www-data /opt/soford-erp/app_data
sudo systemctl start soford-erp-api
```

注意：`app_data/data-protection-keys` 与 `alibaba-token.json` 必须一起保留，缺少密钥则 Token 无法解密，只能重新授权。
