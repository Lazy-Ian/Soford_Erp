---
name: soford-erp
description: >-
  Guides development on the Soford ERP monorepo (Alibaba ICBU product workbench):
  ASP.NET Core 8 API, React admin, static English website, JSON file storage,
  IOP signing, publish/import jobs, and website inquiries. Use when working in
  Soford_Erp, SofordRepApi, WebSofordRep, SofordWebsite, deploy/, or docs/, or
  when the user mentions Soford, Alibaba ICBU, 询盘, 发布, 质检, or ERP.
---

# Soford ERP

Alibaba.com 国际站（ICBU）商品管理工作台 + 英文官网询盘。改代码前先读本 skill；细节见 [reference.md](reference.md)。

## 仓库地图

| 路径 | 职责 |
|------|------|
| `SofordRepApi/` | ASP.NET Core 8 Minimal API：`Alibaba/`、`Catalog/`、`Publishing/`、`Infrastructure/`、`Endpoints/`、`Website/` |
| `SofordRepApi.Tests/` | xUnit（签名、映射、导入、质检、权限、询盘、更新安全） |
| `WebSofordRep/` | React 19 + Vite 管理端（中文 UI） |
| `SofordWebsite/` | 纯静态英文官网；商品目录读 ERP 的 `products.json`（`/public/products`），不要另存一份 |
| `deploy/` | Ubuntu Nginx + systemd（无 Docker 生产路径） |
| `docs/` | PRD / 技术方案 / 实施 / 优化计划 |

数据目录：`App_Data` 或 `Soford__DataPath`（`products.json`、`publish-jobs.json`、`alibaba-accounts.json` 加密、`users.json`、`audit-log.json`、`inquiries.json`、`data-protection-keys/`）。

## 工作原则

1. **小改动、对齐现有风格**：Minimal API 扩展方法、单例 JSON 仓储、前端 `api.ts` + pages。不引入数据库或大框架，除非用户明确要求。
2. **双状态勿混淆**：`LocalState`（质检 Incomplete/Ready）与 `PublishState`（远端发布态）互不覆盖。
3. **读必复制**：`ProductRepository` / 询盘仓储返回的是副本；禁止把缓存对象直接交给调用方修改。写盘失败要能回到持久化数据。
4. **Alibaba 写操作安全**：更新前检查线上是否被改过、多规格等（见 `UpdateSafetyTests` / `ProductOperations`）；写入接口**不重试**；只读可重试。用 `ProductLocks` 串行化同一商品的发布/同步。
5. **权限**：Cookie 会话；`Admin` vs 运营（`AccessService` 按负责账号过滤）。生产必须配置 `Auth__AdminUsername/Password`。
6. **密钥**：`.env` 含 AppKey/Secret，勿提交；Token 用 Data Protection 加密。日志脱敏 `access_token` / `sign` / `image_bytes`。
7. **官网询盘**：公开 `POST /public/inquiries`（限流 + 蜜罐）；ERP `GET/PUT /api/inquiries*`。勿编造真实联系方式或业务承诺。

## 常见任务路由

| 任务 | 优先查看 |
|------|----------|
| IOP 签名 / 客户端 | `Alibaba/IopProtocol.cs`, `AlibabaClient.cs`, `AlibabaSettings.cs` |
| 商品 CRUD / 导入 / 质检 | `Catalog/*`, `Endpoints/ApiEndpoints.cs` |
| 发布 / 同步 / 上下架 | `Publishing/*` |
| 用户 / 审计 / 限流 | `Infrastructure/*` |
| 询盘 API | `Website/Inquiries.cs` |
| 管理端页面 | `WebSofordRep/src/pages/*`, `api.ts` |
| 官网表单 / 商品目录 / 联系人 | `SofordWebsite/assets/site.js`, `SofordRepApi/Website/WebsiteCatalog.cs`, `Website/WebsiteProfile.cs` |
| 部署 | `deploy/DEPLOY_UBUNTU.md`, `deploy/nginx/*` |

## 本地与验证

```powershell
# 一键本地
powershell -ExecutionPolicy Bypass -File .\start-local.ps1

# API / 前端分启
dotnet run --project SofordRepApi --urls http://localhost:5153
cd WebSofordRep; npm run dev   # http://localhost:5173

# 官网预览（询盘需 API + Website:AllowedOrigins）
python -m http.server 5180 --directory SofordWebsite
```

改动后按触及面跑：

```powershell
dotnet build SofordRepApi -c Release -warnaserror
dotnet test SofordRepApi.Tests -c Release
cd WebSofordRep; npm run lint; npm run build
node --check SofordWebsite/assets/site.js
node --check SofordWebsite/scripts/detail-html.mjs
node --test SofordWebsite/tests/*.test.mjs
```

CI（`.github/workflows/ci.yml`）覆盖上述 + deploy shell/systemd/nginx。

## 硬性边界

- 不要对生产 `App_Data` 做破坏性操作；验收用隔离目录（如 `.test-results/`），且勿把测试数据当业务数据提交。
- 不要在未授权/无用户确认时调用真实 Alibaba **写入**接口。
- 官网联系人、地址、国际站链接和 FAQ 文案在 ERP「官网」页维护（`website-profile.json`）。`site.js` 里的 `SITE` 只是接口不可用时的兜底，占位勿当真实数据写死上线。
- 官网商品与 ERP 共用 `products.json`。页面读 `/public/products`，不要再手改或导出 `data/products.js`。

## 文档索引

- 需求：`docs/01-产品需求.md`
- 架构与 IOP：`docs/02-技术方案.md`
- 验收：`docs/03-实施计划.md`、`docs/04-优化计划.md`
- 路线图：`docs/05-下一阶段计划.md`；官网资料：`docs/06-官网上线资料清单.md`
- 使用：`USAGE.md`；官网：`SofordWebsite/README.md`

模块与接口细节 → [reference.md](reference.md)
