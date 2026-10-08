# Soford ERP

[![CI](https://github.com/Lazy-Ian/Soford_Erp/actions/workflows/ci.yml/badge.svg)](https://github.com/Lazy-Ian/Soford_Erp/actions/workflows/ci.yml)

Alibaba.com 国际站（ICBU）店铺的商品管理工作台，面向主账号和多个子账号（业务员）：

- **概览**：待处理事项（审核未通过、授权到期、Alibaba 上已删除、内容不完整、关键词不足）和各账号统计。
- **商品**：从 Alibaba 导入全部商品（每天自动），表格导入/导出，编辑、质检、类目预测与属性补全。
- **发布与维护**：新建/更新、状态同步、价格库存同步、上下架，全部在后台任务中执行；更新前检查线上是否被改过、是否有多个规格，避免覆盖 Alibaba 上的内容。
- **多人使用**：管理员/运营角色，运营只能看到和操作自己负责账号的商品；操作记录。
- **可选 AI 建议**：配置 Anthropic API Key 后，为商品生成英文标题和买家搜索词建议，人工确认后才采用。

文档：[产品需求](docs/01-产品需求.md) · [技术方案](docs/02-技术方案.md) · [实施计划与验收](docs/03-实施计划.md) · [使用说明](USAGE.md)

## 项目结构

| 目录 | 说明 |
|------|------|
| `SofordRepApi` | ASP.NET Core 8 API。`Alibaba/` IOP 客户端、签名、多账号授权，`Catalog/` 商品、导入、质检、AI 建议，`Publishing/` 后台任务与自动同步，`Infrastructure/` 存储、用户、操作记录，`Endpoints/` 路由 |
| `SofordRepApi.Tests` | xUnit 单元测试与接口集成测试（签名、响应解析、报文映射、更新安全检查、导入、质检、权限、登录防护） |
| `WebSofordRep` | React + Vite 前端（中文界面） |
| `deploy/` | Ubuntu 无 Docker 部署（Nginx + systemd） |

数据保存在 `App_Data`（或 `Soford__DataPath`）：`products.json`、`publish-jobs.json`、`alibaba-accounts.json`（各账号授权，加密）、`users.json`、`audit-log.json`、`automation.json`、`alibaba-api-logs.json`、`category-attributes.json`，以及加密用的 `data-protection-keys/`。

## 本地运行

1. 在仓库根目录创建 `.env`（可复制 `.env.example`），填入 `Alibaba__AppKey`、`Alibaba__AppSecret`。
   开发环境下 API 会自动读取这个文件，无需再设置环境变量。
2. 启动：

```powershell
powershell -ExecutionPolicy Bypass -File .\start-local.ps1
```

或分别启动：

```powershell
cd SofordRepApi; dotnet run --urls http://localhost:5153
cd WebSofordRep; npm install; npm run dev
```

打开 `http://localhost:5173`，默认账号 `admin` / `admin123`（仅开发环境；可用 `Auth__AdminUsername`、`Auth__AdminPassword` 覆盖）。

3. 进入「店铺连接」，自检通过后授权 Alibaba 店铺（本地使用「粘贴回调链接」方式），把主账号设为默认账号，再在商品页「从 Alibaba 导入」。
4. 在「高级 → 用户管理」为业务员新增登录并指定负责的账号。

## Alibaba 开放平台

- 协议：IOP，网关 `https://openapi-api.alibaba.com/rest`，HMAC-SHA256 签名（API 路径 + 排序参数），毫秒时间戳，`access_token` 参数。
- 所有接口路径已内置官方默认值（见 `Alibaba/AlibabaSettings.cs`），需要时可用 `Alibaba__Apis__{key}__Path` 覆盖。
- 授权回调：`/openapi/callback`，必须与 App Console 登记的地址一致。当前生产回调为 `https://stepnex.cn/erp/openapi/callback`；原域名完成 ICP 接入并续证后可切回 `https://erp.soford.cn/openapi/callback`。
- 每个账号（主账号、子账号）各自授权；Token 用 ASP.NET Data Protection 加密保存，过期前自动续期。
- 只读接口遇到超时、限流会自动重试；写入接口从不重试。
- 如果接口返回无权限，需在 App Console 为应用申请对应 API 权限包；错误码会原样显示在结果和 API 日志中。

## 测试

```powershell
dotnet test SofordRepApi.Tests
cd WebSofordRep; npm run lint; npm run build
```

每次推送到 `main` 和每个 Pull Request，GitHub Actions 会自动运行上述检查，并检查部署脚本、systemd 和 nginx 配置。依赖更新由 Dependabot 每周提出。

## 部署

- Docker：`copy .env.example .env`，填写后 `docker compose up --build`（生产环境必须设置 `Auth__AdminUsername/Password`，否则 API 拒绝启动）。
- Ubuntu 无 Docker：`powershell -ExecutionPolicy Bypass -File .\publish-server.ps1` 生成 `soford-erp-server.zip`，按 [deploy/DEPLOY_UBUNTU.md](deploy/DEPLOY_UBUNTU.md) 部署。

## 导入列

模板可在系统中下载（`.xlsx`，含填写说明）。上传后先预检、人工确认，再由后台任务写入并自动质检；支持新增并更新、仅新增、仅更新、只更新库存价格四种模式，警告可下载为 Excel。支持的表头（中英文均可）：

`RemoteProductId(Alibaba 商品ID，有则按它匹配)`、`Sku(商品编码)`、`Title(标题)`、`Description(描述)`、`Keywords(关键词)`、`BrandName(品牌)`、`ModelNumber(型号)`、`CategoryId(类目ID)`、`Attributes(属性，名称:值;…)`、`Currency(币种)`、`Price(价格)`、`TieredPrices(阶梯价，数量:价格;…)`、`MOQ(起订量)`、`Unit(单位)`、`Stock(库存)`、`LeadTimeDays(交期)`、`ShippingTemplateId(运费模板)`、`WeightKg(重量)`、`LengthCm/WidthCm/HeightCm(长宽高)`、`Images(图片，;分隔)`，兼容旧模板的 `MainImageUrl`、`DetailImageUrls`。
