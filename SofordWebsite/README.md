# Soford 官网（外贸英文站）

纯静态网站，无需构建，可直接部署到任何静态托管（Nginx、OSS、Cloudflare Pages 等）。

| 文件 | 说明 |
|------|------|
| `index.html` | 首页：主视觉、产品线、热销款、应用场景、优势、合作流程、FAQ、询盘表单 |
| `products.html` | 产品目录：分类筛选、搜索、排序、产品详情弹窗（`#p=<商品ID>` 可直接分享） |
| `assets/site.js` | 联系方式配置（`SITE`）、产品线文案（`CATEGORIES`）和页面逻辑 |
| `assets/site.css` | 样式 |
| `data/products.js` | 由 ERP 商品导出的数据，**不要手改** |

## 从 ERP 更新商品

```powershell
node SofordWebsite/scripts/export-products.mjs
```

默认读取 `SofordRepApi/App_Data/products.json`，也可以传入其他路径（例如服务器上下载下来的文件）。
只导出 Alibaba 上状态为 online 的商品，按标题去重，并按标题关键词归入 5 条产品线
（规则见脚本里的 `CATEGORIES`）。

## 上线前需要确认

- `assets/site.js` 顶部 `SITE`：邮箱、电话、WhatsApp、工厂地址、Alibaba 店铺链接。
- 首页文案中的业务承诺（FAQ 的付款方式、样品政策、FOB 港口、"24 小时回复"等）按实际情况修改。
- 询盘表单直接向 `/public/inquiries` 提交，ERP 登录后在「询盘」查看、搜索、分页和处理；所有已登录用户共享询盘。表单无邮件自动发送功能，「用邮件回复」仍打开业务员的邮件客户端。
- 按 `deploy/nginx/soford-website.https.conf` 将官网的 `/public/inquiries` 反向代理至 API。生产配置必须包含官网来源，例如 `Website__AllowedOrigins__0=https://www.soford.cn`，并将官网域名加入 `AllowedHosts`。接口有按 IP 的频率限制和隐藏蜜罐字段。
- 商品图片直接引用 Alibaba CDN（alicdn.com）。

本地预览：

```powershell
python -m http.server 5180 --directory SofordWebsite
```

同时运行 API（`dotnet run --project SofordRepApi --urls http://localhost:5153`）和 ERP 前端（在 `WebSofordRep` 中执行 `npm run dev`）。官网从 `http://localhost:5180` 或 `http://127.0.0.1:5180` 访问时，会向 `http://localhost:5153/public/inquiries` 提交；其他域名使用同源 `/public/inquiries`。如更改端口或访问域名，同步修改 `SITE.inquiryEndpoint` 和 API 的 `Website:AllowedOrigins`。

提交成功后显示已接收并清空表单；失败保留输入，限流和无法连接时显示具体提示。超时表示无法确认是否已收件，应先确认，避免重复发送。

询盘保存于 API 数据目录的 `inquiries.json`，备份时与其他业务数据一起备份。目前最多保留 20,000 条，超出时淘汰最早记录；需要长期留存时先导出备份并规划归档。

检查官网脚本与表单回归行为（无需安装 npm 依赖）：

```powershell
node --check SofordWebsite/assets/site.js
node --check SofordWebsite/scripts/export-products.mjs
node --test SofordWebsite/tests/*.test.mjs
```

上线验收：服务器执行 `nginx -t`，重载后从官网提交一条测试询盘，在 ERP 中核对来源、产品和内容，再修改状态和备注验证保存。CI 已纳入官网脚本测试及 Nginx 配置语法检查。
