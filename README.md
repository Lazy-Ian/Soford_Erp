# Soford ERP

Alibaba.com 国际站（ICBU）商品发布工作台：导入/录入商品 → 质检 → 类目预测与属性补全 → 发布 → 状态回写 → 价格库存同步、上下架。

文档：[产品需求](docs/01-产品需求.md) · [技术方案](docs/02-技术方案.md) · [实施计划与验收](docs/03-实施计划.md) · [使用说明](USAGE.md)

## 项目结构

| 目录 | 说明 |
|------|------|
| `SofordRepApi` | ASP.NET Core 8 API。`Alibaba/` IOP 客户端与签名，`Catalog/` 商品、导入、质检，`Publishing/` 后台发布队列，`Endpoints/` 路由 |
| `SofordRepApi.Tests` | xUnit 单元测试（签名、响应解析、报文映射、导入、质检、仓储） |
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

3. 进入「店铺连接」，自检通过后授权 Alibaba 店铺（本地使用「粘贴回调链接」方式）。

## Alibaba 开放平台

- 协议：IOP，网关 `https://openapi-api.alibaba.com/rest`，HMAC-SHA256 签名（API 路径 + 排序参数），毫秒时间戳，`access_token` 参数。
- 所有接口路径已内置官方默认值（见 `Alibaba/AlibabaSettings.cs`），需要时可用 `Alibaba__Apis__{key}__Path` 覆盖。
- 授权回调：`/openapi/callback`，必须与 App Console 登记的地址一致（生产为 `https://erp.soford.cn/openapi/callback`）。
- Token 用 ASP.NET Data Protection 加密保存，过期前 5 分钟自动续期。
- 如果接口返回无权限，需在 App Console 为应用申请对应 API 权限包；错误码会原样显示在结果和 API 日志中。

## 测试

```powershell
dotnet test SofordRepApi.Tests
cd WebSofordRep; npm run lint; npm run build
```

## 部署

- Docker：`copy .env.example .env`，填写后 `docker compose up --build`（生产环境必须设置 `Auth__AdminUsername/Password`，否则 API 拒绝启动）。
- Ubuntu 无 Docker：`powershell -ExecutionPolicy Bypass -File .\publish-server.ps1` 生成 `soford-erp-server.zip`，按 [deploy/DEPLOY_UBUNTU.md](deploy/DEPLOY_UBUNTU.md) 部署。

## 导入列

模板可在系统中下载（`.xlsx`，含填写说明）。支持的表头（中英文均可）：

`Sku(商品编码)`、`Title(标题)`、`Description(描述)`、`Keywords(关键词)`、`BrandName(品牌)`、`ModelNumber(型号)`、`CategoryId(类目ID)`、`Attributes(属性，名称:值;…)`、`Currency(币种)`、`Price(价格)`、`TieredPrices(阶梯价，数量:价格;…)`、`MOQ(起订量)`、`Unit(单位)`、`Stock(库存)`、`LeadTimeDays(交期)`、`ShippingTemplateId(运费模板)`、`WeightKg(重量)`、`LengthCm/WidthCm/HeightCm(长宽高)`、`Images(图片，;分隔)`，兼容旧模板的 `MainImageUrl`、`DetailImageUrls`。
