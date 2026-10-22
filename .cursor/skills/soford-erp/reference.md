# Soford ERP — 参考

## 架构

```
浏览器 WebSofordRep (Vite → /api, /openapi)
        │ Cookie 会话
        ▼
SofordRepApi (Minimal API)
  ├─ Alibaba/     IOP 客户端、签名、多账号 Token、ListingMapper
  ├─ Catalog/     ProductRecord、导入队列、质检、导出、AI 建议
  ├─ Publishing/  PublishQueue/Worker、RemoteSync、ProductOperations、ProductLocks
  ├─ Website/     公开询盘 + 登录后询盘管理
  └─ Infrastructure/ AppPaths 原子写、Users、Audit、限流、Origin Guard
        │
        ▼
App_Data/*.json  + data-protection-keys/
        │
        ▼
Alibaba ICBU OpenAPI  https://openapi-api.alibaba.com/rest
```

官网 `SofordWebsite` 静态托管；`/public/inquiries` 由 Nginx 反代到 API。

## IOP 要点

- 签名：`HEX_UPPER(HMAC_SHA256(secret, apiPath + 排序后 k1v1k2v2…))`，排除 `sign` 与文件参数
- 公共参数：`app_key`、`timestamp`（UTC 毫秒）、`sign_method=sha256`、`access_token`、`sign`
- 对象/数组业务参数序列化为 JSON 字符串；图片 `multipart` 不参与签名
- 错误：顶层 `code != "0"` 为网关错误；`success=false` 为业务错误。HTTP 200 也可能失败
- Token：过期前约 5 分钟刷新；多账号各自授权，加密落盘
- 路径默认值在 `AlibabaSettings`；可用 `Alibaba__Apis__{key}__Path` 覆盖

## 商品状态

| 维度 | 枚举 | 含义 |
|------|------|------|
| `LocalState` | Incomplete / Ready | 本地质检；有 Blocker → Incomplete |
| `PublishState` | NotPublished / Publishing / Pending / Online / Offline / Failed | Alibaba 侧；质检不得改写 |

批量操作 API 统一返回 `{ items: [{ productId, sku, success, message }] }`。

## 关键后台任务

- **发布**：`POST /api/catalog/publish` → Channel → `PublishWorker`；有 `RemoteProductId` 走 update，否则 listing
- **导入**：preview → commit → `ImportWorker`；模式：新增并更新 / 仅新增 / 仅更新 / 只更新库存价格
- **自动同步**：`RemoteSyncWorker`（状态等）；管理员可 `POST /api/system/automation/run`
- 服务重启：Queued/Running 任务标 Interrupted，Publishing 商品回退

## 权限与角色

| 角色 | 能力 |
|------|------|
| Admin | 全部商品、店铺授权、用户、从 Alibaba 导入、API 调试 |
| 运营 | 仅负责账号的商品 + 自己新建的；无店铺连接/用户/API 工具 |

内置管理员 ID：`CurrentUser.BuiltInAdminId`；密码变更/停用通过 `SecurityStamp` 立即失效会话。

## 主要 API 分组

| 前缀 | 说明 |
|------|------|
| `/api/auth/*` | 登录/登出/会话/改密 |
| `/api/system/*` | diagnostics、automation、features |
| `/api/integrations/alibaba/*` | 授权、账号、日志、调试 call、类目属性、图片上传 |
| `/api/catalog/*` | 商品、导入、质检、发布、同步、导出 |
| `/api/users/*` | 用户管理（Admin） |
| `/api/inquiries*` | 询盘列表/搜索/摘要/更新（需登录） |
| `/api/audit` | 操作记录 |
| `/public/inquiries` | 官网提交（匿名 + 限流） |
| `/openapi/callback` | OAuth 回调 |

## 前端约定

- `api.ts`：`credentials: 'include'`；401 → 登录
- `App.tsx`：导航与角色显隐
- UI 中文；危险操作二次确认；批量结果弹窗；任务轮询约 2s
- 共享组件在 `components/ui.tsx`

## 官网

- `SITE` / `CATEGORIES` 在 `assets/site.js`
- 导出：只导出 Alibaba `online` 商品，按标题去重并归入产品线
- 本地询盘：从 `localhost:5180` 提交到 `http://localhost:5153/public/inquiries`；生产用同源路径 + `Website__AllowedOrigins`
- 询盘最多约 20,000 条，超出淘汰最早记录

## 配置键（常见）

| 键 | 用途 |
|----|------|
| `Alibaba__AppKey` / `Alibaba__AppSecret` | IOP 应用 |
| `Alibaba__BaseUrl` | 默认 `https://openapi-api.alibaba.com/rest` |
| `Auth__AdminUsername` / `Auth__AdminPassword` | 生产必填 |
| `Soford__DataPath` | 数据目录覆盖 |
| `Cors__AllowedOrigins__*` | ERP 前端 |
| `Website__AllowedOrigins__*` | 官网询盘 CORS |
| `Anthropic__ApiKey` | 可选 AI 标题/关键词建议 |

开发环境 API 自动合并仓库根与项目目录 `.env`（不覆盖已有环境变量；忽略 `ASPNETCORE_*`）。

## 部署要点

- 生产入口当前：`https://stepnex.cn/erp/`；回调 `https://stepnex.cn/erp/openapi/callback`（ICP 后可切回 `erp.soford.cn`）
- 官网 Nginx 模板：`deploy/nginx/soford-website.https.conf`（`/public/inquiries` 反代）
- 发布包：`publish-server.ps1` → `soford-erp-server.zip`；说明见 `deploy/DEPLOY_UBUNTU.md`

## 测试落点

| 文件 | 覆盖 |
|------|------|
| `AlibabaProtocolTests.cs` | 签名、响应解析 |
| `CatalogTests.cs` / `UpdateSafetyTests.cs` | 导入、映射、更新安全 |
| `EndpointTests.cs` / `UserTests.cs` / `ThrottleTests.cs` | 权限与登录防护 |
| `InquiryTests.cs` / `OptimizationTests.cs` | 询盘与仓储隔离 |
| `SofordWebsite/tests/*.test.mjs` | 官网询盘前端行为 |
