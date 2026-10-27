# Soford 官网（外贸英文站）

发布更新（2026-10-27）：R1–R8 实现修复已提交；代码版本 e9de0e8 的 CI、官网/ERP/API 部署、登录和询盘闭环验收通过（后端 120、官网 15、ERP lint/build）。当前状态以 [09 发布记录](../docs/09-发布记录-2026-10-27.md) 为准，以下旧问题描述保留为检查基线。

静态页面，无需构建。商品目录不单独保存，打开页面时向 ERP 读取同一份 `products.json`。

2026-10-27：官网 13 项、后端 120 项测试及 ERP lint/build 通过；[检查记录 R1–R8](../docs/08-官网与ERP检查-2026-10-27.md) 仍待修复。当前工作区与线上部署尚未验收，执行路线见 [下一阶段计划](../docs/05-下一阶段计划.md)。

| 文件 | 说明 |
|------|------|
| `index.html` | 首页：主视觉、产品线、热销款、应用场景、优势、合作流程、FAQ、询盘表单 |
| `products.html` | 产品目录：分类筛选、搜索、排序。卡片进入 `product.html?id=` |
| `product.html` | 商品独立页：图库、规格、从 ERP 同步的详情正文、询盘。从目录打开时用新窗口，不改当前页地址 |
| `assets/site.js` | 页面逻辑、产品线文案（`CATEGORIES`）。联系人和公司资料优先读 ERP 的 `/public/site`，`SITE` 只在接口不可用时兜底 |
| `assets/i18n.js` | 界面语言。页头下拉切换，记在本机；商品标题仍用 ERP 原文 |
| `assets/i18n-extra.js` | 外贸常用语：西班牙语、葡萄牙语、法语、德语、俄语、阿拉伯语 |
| `assets/site.css` | 样式 |

## 商品数据

官网和 ERP 用同一份商品库：`SofordRepApi/App_Data/products.json`（服务器上若设置了 `Soford__DataPath`，则是该目录下的 `products.json`）。页面打开时请求 `/public/products`，商品详情请求 `/public/products/{id}`。在 ERP 里改完并保存后，刷新官网即可看到。仓库里不再保存第二份商品目录。

只展示 Alibaba 状态为 online、且有图片的商品。标题会去掉批发用语并按关键词归入 5 条产品线，同标题只保留第一条。

当前没有官网独立发布开关；“有未发布修改”只表示尚未更新 Alibaba，不阻止官网读取新内容。2026-10-27 本地样本的 638 条带图在线商品投影为 623 条，15 条被标题去重隐藏；这是本地检查快照，不能作为线上统计。人工分类、隐藏原因和重复关系管理为待办。

当前价格统一加美元符号，DTO 不含币种；详情交期只读 Delivery Time 属性，缺值固定显示 7–20 天，没有使用 leadTimeDays。币种和交期一致性按 R2/R3 修复，不应把当前显示方式当成最终数据契约。

Nginx 需要把 `/public/products`（stepnex 路径是 `/soford/public/products`）反代到 API，和询盘一样。本地预览也要先启动 API。

## 上线前需要确认

- 公司名、地址、国际站链接、多位联系人，以及首页付款 / 样品 / 起订 / 交期 / 贸易条款 / 质保文案，在 ERP「官网」页修改并保存。未保存过时接口返回现有默认联系人；业务说明留空则继续用网站自带的多语言文案。
- 首页业务承诺的内置译文仍是：30% 电汇定金、样品费首单抵扣、青岛 FOB、核对后报价、上述起订量与交期、SGS / REACH 及 6–8 年质保。`claimsVerified` 为 `true`。
- 询盘表单直接向 `/public/inquiries` 提交，ERP 登录后在「询盘」查看、搜索、分页和处理；所有已登录用户共享询盘。表单无邮件自动发送功能，「用邮件回复」仍打开业务员的邮件客户端。
- 当前路径部署入口配置为 `https://stepnex.cn/soford/`，使用 `deploy/nginx/snippets/stepnex-soford-website.locations.conf`，将 `/soford/public/products`（含商品 ID 子路径）、`/soford/public/site` 和 `/soford/public/inquiries` 代理到 API 的 `/public/*`。`Website__AllowedOrigins` 填 `https://stepnex.cn`（不带路径），`AllowedHosts` 包含实际域名；如用 www 则另加来源和主机名。询盘有按 IP 限流及蜜罐。
- `deploy/nginx/soford-website.https.conf` 是独立域名模板；域名方案需另行确认，路径部署说明目前把 `www.soford.cn` 保留给国际站。本轮未验证线上实际部署。
- 商品图片直接引用 Alibaba CDN（alicdn.com）。

本地预览：

```powershell
python -m http.server 5180 --directory SofordWebsite
```

同时运行 API（`dotnet run --project SofordRepApi --urls http://localhost:5153`）和 ERP 前端（在 `WebSofordRep` 中执行 `npm run dev`）。本地 5180 端口下，商品、资料和询盘走 `http://localhost:5153/public/*`；`/soford/` 路径部署走同源 `/soford/public/*`；独立域名根路径部署走同源 `/public/*`。如改端口或主机名，同步检查 site.js 的接口地址计算及 API 的 `Website:AllowedOrigins`。

提交成功后显示已接收并清空表单；失败保留输入，限流和无法连接时显示具体提示。超时表示无法确认是否已收件，应先确认，避免重复发送。

询盘保存于 API 数据目录的 `inquiries.json`，官网资料保存于 `website-profile.json`，两者都要随业务目录备份并演练恢复。询盘当前最多 20,000 条，超出淘汰最早记录，包括尚未处理的记录；当前没有内置导出/归档，容量提醒、导出及归档为待办，实施前保留文件备份。

## 已知限制（待修复）

- R1：商品单位等普通字段必须统一转义，不能直接插入 HTML。
- R4/R5：当前运营也能修改官网资料；FAQ 覆盖只影响对应回答，公司名、首屏和其他业务区块尚未全站统一。
- R6：官网资料与询盘缺少版本冲突检查，多人保存可能覆盖。
- R7：带 lang 参数的链接会覆盖语言切换选择；标题、详情、规格值及 ERP 覆盖 FAQ 不会自动翻译。
- R8：资料页缺少未保存提醒、加载错误重试及保存期间的编辑保护。

详细复现和验收见检查记录；官网可见/发布、商品分类与推荐管理、询盘协作、SEO 为后续能力。

检查官网脚本与表单回归行为（无需安装 npm 依赖）：

```powershell
node --check SofordWebsite/assets/site.js
node --check SofordWebsite/scripts/detail-html.mjs
node --test SofordWebsite/tests/*.test.mjs
```

上线验收：修复上述问题并确认最终提交 CI 后，在服务器执行 `nginx -t`；逐一核对目录、单商品详情、公司资料接口和静态页。再提交明确标记的测试询盘，在 ERP 核对来源、产品、内容、状态、备注与角标，记录测试数据处理方式。现有部署脚本遗漏 public/site 验收，且部分请求失败被忽略，脚本退出 0 不能代替完整验收。步骤见 [Ubuntu 部署说明](../deploy/DEPLOY_UBUNTU.md)。
