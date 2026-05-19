# Soford ERP

Production-oriented ERP workspace for importing real product spreadsheets, checking Alibaba listing quality, and publishing in batches.

## Projects

- `SofordRepApi`: ASP.NET Core 8 API. Stores imported products in `App_Data/products.json`.
- `WebSofordRep`: React + Vite console.
- `QRSofordRep`: reserved for QR/mobile workflows.

## Local Run

```powershell
cd D:\project\web\Soford_Erp\SofordRepApi
dotnet run --urls http://localhost:5153

cd D:\project\web\Soford_Erp\WebSofordRep
npm install
npm run dev
```

Open `http://localhost:5173`.

## Alibaba Configuration

No publish request is simulated. If Alibaba settings are missing, the API blocks publishing and writes a fallback CSV export.

Set these values with environment variables, user secrets, or production configuration:

```powershell
$env:Alibaba__BaseUrl="https://..."
$env:Alibaba__ProductPublishPath="/..."
$env:Alibaba__AuthBaseUrl="https://openapi.alibaba.com/rest"
$env:Alibaba__AuthTokenCreatePath="/auth/token/create"
$env:Alibaba__AuthTokenRefreshPath="/auth/token/refresh"
$env:Alibaba__ApiVersion="2.0"
$env:Alibaba__DefaultRestMethod="alibaba.open.api.call"
$env:Alibaba__ProductPayloadParameter="product_payload"
$env:Alibaba__AppKey="..."
$env:Alibaba__AppSecret="..."
```

Use the real endpoint/path granted to the Alibaba.com Open Platform app. Alibaba's public docs and App Console make clear that apps, APIs and permissions are managed through the Open Platform console, and product publishing access depends on the permission granted to that app. Free-tier or test apps may not have product publishing permission; in that case use the built-in preflight and CSV export workflow until the app is approved.

After the seller authorization callback returns a real `code`, open the console and submit it in the Alibaba token panel. The backend calls `/auth/token/create`, stores the returned `access_token` and `refresh_token` under `SofordRepApi/App_Data/alibaba-token.json`, and uses that access token for publish calls.

OAuth callback is also available at:

`GET /api/integrations/alibaba/oauth/callback?code=...&state=...`

Set `Alibaba__OAuthSuccessRedirect` to your frontend URL after deployment.

## Alibaba API Registry

The backend exposes a signed generic Alibaba OpenAPI caller plus business wrappers. Configure the real method names granted in App Console:

```powershell
$env:Alibaba__Apis__category.tree__Method="..."
$env:Alibaba__Apis__category.attributes__Method="..."
$env:Alibaba__Apis__image.upload__Method="..."
$env:Alibaba__Apis__product.create__Method="..."
$env:Alibaba__Apis__product.update__Method="..."
$env:Alibaba__Apis__product.get__Method="..."
$env:Alibaba__Apis__product.offline__Method="..."
$env:Alibaba__Apis__product.delete__Method="..."
$env:Alibaba__Apis__order.search__Method="..."
$env:Alibaba__Apis__order.detail__Method="..."
$env:Alibaba__Apis__logistics.freightTemplates__Method="..."
$env:Alibaba__Apis__logistics.shipment.create__Method="..."
```

Available local wrappers:

- `GET /api/integrations/alibaba/apis`: show configured API registry and comments.
- `GET /api/integrations/alibaba/logs`: latest Alibaba API call logs.
- `POST /api/integrations/alibaba/call`: generic signed API call by `apiKey` and JSON payload.
- `POST /api/integrations/alibaba/catalog/categories/tree`
- `POST /api/integrations/alibaba/catalog/categories/attributes`
- `POST /api/integrations/alibaba/images/upload`
- `POST /api/integrations/alibaba/products/create`
- `POST /api/integrations/alibaba/products/update`
- `POST /api/integrations/alibaba/products/get`
- `POST /api/integrations/alibaba/products/offline`
- `POST /api/integrations/alibaba/products/delete`
- `POST /api/integrations/alibaba/orders/search`
- `POST /api/integrations/alibaba/orders/detail`
- `POST /api/integrations/alibaba/logistics/freight-templates`
- `POST /api/integrations/alibaba/logistics/shipments/create`

Unconfigured API methods return a clear 400 error and never simulate success.

## Publish Audit

- `GET /api/catalog/publish-jobs`: latest publish/preflight/fallback jobs.
- Alibaba API call logs are stored in `SofordRepApi/App_Data/alibaba-api-logs.json`.
- Publish jobs are stored in `SofordRepApi/App_Data/publish-jobs.json`.
- Product and token data stay under `App_Data`; this folder is intentionally ignored by git.

## Docker

```powershell
copy .env.example .env
# Fill .env with real Alibaba values.
docker compose up --build
```

The web container serves the React app and proxies `/api` to the API container.

## Import Columns

Supported `.xlsx` and `.csv` headers:

`Sku, Title, CategoryId, Currency, Price, MOQ, Stock, LeadTimeDays, MainImageUrl, DetailImageUrls, Keywords, Attributes, Description`

`DetailImageUrls` and `Keywords` accept semicolon-separated values. `Attributes` accepts JSON or `key:value;key:value`.
