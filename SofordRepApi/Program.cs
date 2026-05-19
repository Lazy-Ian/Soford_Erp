using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:5173", "http://127.0.0.1:5173"])
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient<AlibabaPublisher>();
builder.Services.AddHttpClient<AlibabaAuthClient>();
builder.Services.AddHttpClient<AlibabaOpenApiClient>();
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<AlibabaTokenStore>();
builder.Services.AddSingleton<AlibabaApiLogStore>();
builder.Services.AddSingleton<PublishJobStore>();
builder.Services.AddSingleton<ProductImportService>();
builder.Services.AddSingleton<ProductQualityService>();
builder.Services.AddSingleton<ExportService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseCors("web");

var api = app.MapGroup("/api");

api.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

api.MapGet("/integrations/alibaba/status", async (IConfiguration config, AlibabaTokenStore tokenStore) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    var token = await tokenStore.GetAsync();
    return Results.Ok(new AlibabaIntegrationStatus(
        settings.HasAppCredentials,
        token is not null,
        token?.AccessTokenExpiresAt,
        token?.RefreshTokenExpiresAt,
        settings.BaseUrl,
        settings.ProductPublishPath,
        settings.AuthBaseUrl,
        settings.AuthTokenCreatePath,
        settings.IsConfigured
            ? "Alibaba Open Platform credentials, token, and publish endpoint are configured. Real API calls are enabled."
            : "Alibaba credentials, token, or publish endpoint are missing. Real publishing is blocked; CSV fallback export remains available.",
        [
            "Use a real approved Alibaba.com Open Platform app from App Console.",
            "Configure AppKey, AppSecret, API Base URL, and publish paths through appsettings or environment variables.",
            "Exchange the seller authorization code through /auth/token/create before calling token-protected APIs.",
            "If an API is not approved for the app, the system blocks real publish calls and keeps preflight plus CSV export available."
        ]));
});

api.MapPost("/integrations/alibaba/token/create", async (
    AlibabaAuthCodeRequest request,
    AlibabaAuthClient authClient,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(request.Code))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Authorization code is required" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    if (!settings.HasAppCredentials)
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba AppKey/AppSecret/AuthBaseUrl are required" });
    }

    var token = await authClient.CreateTokenAsync(settings, request.Code.Trim());
    await tokenStore.SaveAsync(token);
    return Results.Ok(AlibabaTokenResponse.FromRecord(token));
});

api.MapGet("/integrations/alibaba/oauth/callback", async (
    [FromQuery] string? code,
    [FromQuery] string? state,
    AlibabaAuthClient authClient,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba authorization code is missing" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    if (!settings.HasAppCredentials)
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba AppKey/AppSecret/AuthBaseUrl are required" });
    }

    var token = await authClient.CreateTokenAsync(settings, code.Trim());
    await tokenStore.SaveAsync(token);
    var redirect = string.IsNullOrWhiteSpace(config["Alibaba:OAuthSuccessRedirect"])
        ? $"http://localhost:5173/?alibabaAuth=success&state={Uri.EscapeDataString(state ?? "")}"
        : config["Alibaba:OAuthSuccessRedirect"]!;
    return Results.Redirect(redirect);
});

app.MapGet("/openapi/callback", async (
    [FromQuery] string? code,
    [FromQuery] string? state,
    AlibabaAuthClient authClient,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    if (string.IsNullOrWhiteSpace(code))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba authorization code is missing" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    if (!settings.HasAppCredentials)
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba AppKey/AppSecret/AuthBaseUrl are required" });
    }

    var token = await authClient.CreateTokenAsync(settings, code.Trim());
    await tokenStore.SaveAsync(token);
    var redirect = string.IsNullOrWhiteSpace(config["Alibaba:OAuthSuccessRedirect"])
        ? $"http://localhost:5173/?alibabaAuth=success&state={Uri.EscapeDataString(state ?? "")}"
        : config["Alibaba:OAuthSuccessRedirect"]!;
    return Results.Redirect(redirect);
});

api.MapPost("/integrations/alibaba/token/refresh", async (
    AlibabaAuthClient authClient,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    if (!settings.HasAppCredentials)
    {
        return Results.BadRequest(new ProblemDetails { Title = "Alibaba AppKey/AppSecret/AuthBaseUrl are required" });
    }

    var current = await tokenStore.GetAsync();
    if (current is null || string.IsNullOrWhiteSpace(current.RefreshToken))
    {
        return Results.BadRequest(new ProblemDetails { Title = "No refresh token is stored" });
    }

    var token = await authClient.RefreshTokenAsync(settings, current.RefreshToken);
    await tokenStore.SaveAsync(token);
    return Results.Ok(AlibabaTokenResponse.FromRecord(token));
});

api.MapGet("/integrations/alibaba/logs", async (AlibabaApiLogStore logs, int take = 100) =>
{
    var items = await logs.GetLatestAsync(Math.Clamp(take, 1, 500));
    return Results.Ok(items);
});

var alibaba = api.MapGroup("/integrations/alibaba");

alibaba.MapGet("/apis", (IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return Results.Ok(settings.ApiRegistry.Values.OrderBy(x => x.Area).ThenBy(x => x.Key));
});

alibaba.MapPost("/call", async (
    AlibabaCallRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    var token = await tokenStore.GetAsync();
    return await client.CallAsync(settings, token, request.ApiKey, request.Payload);
});

var alibabaCatalog = alibaba.MapGroup("/catalog");
alibabaCatalog.MapPost("/categories/tree", async (
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config,
    AlibabaCatalogTreeRequest request) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "category.tree", request);
});
alibabaCatalog.MapPost("/categories/attributes", async (
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config,
    AlibabaCategoryAttributesRequest request) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "category.attributes", request);
});

alibabaCatalog.MapPost("/categories/get", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "category.get", request.Payload);
});

alibabaCatalog.MapPost("/categories/predict", async (
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config,
    AlibabaCategoryPredictRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Product title is required" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "category.predict", request);
});

alibabaCatalog.MapPost("/categories/id-mapping", async (
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config,
    AlibabaCategoryIdMappingRequest request) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "category.idMapping", request);
});

var alibabaImages = alibaba.MapGroup("/images");
alibabaImages.MapPost("/photobank/groups", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "photobank.group.list", request.Payload);
});

alibabaImages.MapPost("/photobank/groups/operate", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "photobank.group.operate", request.Payload);
});

alibabaImages.MapPost("/photobank/list", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "photobank.list", request.Payload);
});

alibabaImages.MapPost("/upload", async (
    [FromForm] IFormFile file,
    [FromForm] string? folder,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    if (file.Length == 0)
    {
        return Results.BadRequest(new ProblemDetails { Title = "Image file is empty" });
    }

    await using var stream = file.OpenReadStream();
    using var memory = new MemoryStream();
    await stream.CopyToAsync(memory);

    var payload = new AlibabaImageUploadRequest(
        file.FileName,
        file.ContentType,
        Convert.ToBase64String(memory.ToArray()),
        folder);

    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "image.upload", payload);
}).DisableAntiforgery();

var alibabaProducts = alibaba.MapGroup("/products");
alibabaProducts.MapPost("/create", async (
    AlibabaProductPayloadRequest request,
    ProductRepository repo,
    ProductQualityService quality,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var product = await repo.GetAsync(request.ProductId);
    if (product is null)
    {
        return Results.NotFound();
    }

    product.QualityIssues = quality.Check(product);
    if (product.QualityIssues.Any(x => x.Severity == QualitySeverity.Blocker))
    {
        return Results.BadRequest(new ProblemDetails { Title = "Fix blocker quality issues before publishing" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    var payload = AlibabaProductPayload.FromProduct(product, request.Extra);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.create", payload);
});
alibabaProducts.MapPost("/update", async (
    AlibabaProductPayloadRequest request,
    ProductRepository repo,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var product = await repo.GetAsync(request.ProductId);
    if (product is null)
    {
        return Results.NotFound();
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    var payload = AlibabaProductPayload.FromProduct(product, request.Extra);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.update", payload);
});
alibabaProducts.MapPost("/search", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.search", request.Payload);
});

alibabaProducts.MapPost("/status", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.status", request.Payload);
});

alibabaProducts.MapPost("/status/update", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.status.update", request.Payload);
});

alibabaProducts.MapPost("/inventory/update", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.inventory.update", request.Payload);
});

alibabaProducts.MapPost("/price/update", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.price.update", request.Payload);
});

alibabaProducts.MapPost("/draft/delete", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.draft.delete", request.Payload);
});

alibabaProducts.MapPost("/get", async (
    AlibabaRemoteIdRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.get", request);
});
alibabaProducts.MapPost("/offline", async (
    AlibabaRemoteIdRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.offline", request);
});
alibabaProducts.MapPost("/delete", async (
    AlibabaRemoteIdRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.delete", request);
});

var alibabaOrders = alibaba.MapGroup("/orders");
alibabaOrders.MapPost("/search", async (
    AlibabaOrderSearchRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "order.search", request);
});
alibabaOrders.MapPost("/detail", async (
    AlibabaOrderDetailRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "order.detail", request);
});

var alibabaLogistics = alibaba.MapGroup("/logistics");
alibabaLogistics.MapPost("/freight-templates", async (
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config,
    AlibabaPagedRequest request) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "logistics.freightTemplates", request);
});
alibabaLogistics.MapPost("/shipping-templates", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "product.shippingTemplates", request.Payload);
});

alibabaLogistics.MapPost("/shipments/create", async (
    AlibabaShipmentCreateRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "logistics.shipment.create", request);
});

var alibabaVideos = alibaba.MapGroup("/videos");

alibabaVideos.MapPost("/query", async (
    AlibabaVideoQueryRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    if (request.CurrentPage <= 0 || request.PageSize <= 0)
    {
        return Results.BadRequest(new ProblemDetails { Title = "current_page and page_size must be greater than zero" });
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "video.query", request);
});

alibabaVideos.MapPost("/upload", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "video.upload", request.Payload);
});

alibabaVideos.MapPost("/upload/result", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "video.upload.result", request.Payload);
});

alibabaVideos.MapPost("/relation/product/main", async (
    AlibabaFlexiblePayloadRequest request,
    AlibabaOpenApiClient client,
    AlibabaTokenStore tokenStore,
    IConfiguration config) =>
{
    var settings = AlibabaSettings.FromConfiguration(config);
    return await client.CallAsync(settings, await tokenStore.GetAsync(), "video.relation.product.main", request.Payload);
});

var products = api.MapGroup("/catalog/products");

products.MapGet("/", async (ProductRepository repo) =>
{
    var items = await repo.GetAllAsync();
    return Results.Ok(items.OrderByDescending(x => x.UpdatedAt));
});

products.MapPost("/", async (ProductDraft draft, ProductRepository repo, ProductQualityService quality) =>
{
    var product = ProductRecord.FromDraft(draft);
    product.QualityIssues = quality.Check(product);
    await repo.UpsertAsync(product);
    return Results.Created($"/api/catalog/products/{product.Id}", product);
});

products.MapPut("/{id:guid}", async (Guid id, ProductDraft draft, ProductRepository repo, ProductQualityService quality) =>
{
    var existing = await repo.GetAsync(id);
    if (existing is null)
    {
        return Results.NotFound();
    }

    var product = ProductRecord.FromDraft(draft, id, existing.CreatedAt);
    product.QualityIssues = quality.Check(product);
    await repo.UpsertAsync(product);
    return Results.Ok(product);
});

products.MapDelete("/{id:guid}", async (Guid id, ProductRepository repo) =>
{
    var removed = await repo.DeleteAsync(id);
    return removed ? Results.NoContent() : Results.NotFound();
});

api.MapPost("/catalog/import", async Task<Results<Ok<ImportResult>, BadRequest<ProblemDetails>>> (
    [FromForm] IFormFile file,
    ProductImportService importer,
    ProductRepository repo,
    ProductQualityService quality) =>
{
    if (file.Length == 0)
    {
        return TypedResults.BadRequest(new ProblemDetails { Title = "Upload file is empty" });
    }

    await using var stream = file.OpenReadStream();
    var parsed = importer.Parse(stream, file.FileName);
    foreach (var item in parsed.Products)
    {
        item.QualityIssues = quality.Check(item);
    }

    await repo.BulkUpsertAsync(parsed.Products);
    return TypedResults.Ok(parsed);
}).DisableAntiforgery();

api.MapPost("/catalog/quality-check", async (PublishRequest request, ProductRepository repo, ProductQualityService quality) =>
{
    var selected = await repo.GetManyAsync(request.ProductIds);
    foreach (var item in selected)
    {
        item.QualityIssues = quality.Check(item);
        await repo.UpsertAsync(item);
    }

    return Results.Ok(new QualityBatchResult(selected.Count, selected.Sum(x => x.QualityIssues.Count), selected));
});

api.MapPost("/catalog/publish", async (
    PublishRequest request,
    ProductRepository repo,
    ProductQualityService quality,
    ExportService export,
    AlibabaPublisher publisher,
    AlibabaTokenStore tokenStore,
    PublishJobStore jobStore,
    IConfiguration config) =>
{
    var selected = await repo.GetManyAsync(request.ProductIds);
    if (selected.Count == 0)
    {
        return Results.BadRequest(new ProblemDetails { Title = "No products selected" });
    }

    var job = PublishJobRecord.Create(request.ProductIds, request.PreflightOnly);
    await jobStore.AppendAsync(job);

    foreach (var item in selected)
    {
        item.QualityIssues = quality.Check(item);
        await repo.UpsertAsync(item);
    }

    var blocked = selected.Where(x => x.QualityIssues.Any(i => i.Severity == QualitySeverity.Blocker)).ToList();
    if (blocked.Count > 0 || request.PreflightOnly)
    {
        job.Status = blocked.Count > 0 ? PublishJobStatus.Blocked : PublishJobStatus.PreflightPassed;
        job.Message = blocked.Count > 0 ? "Blocked by quality issues." : "Preflight passed.";
        job.FinishedAt = DateTimeOffset.UtcNow;
        await jobStore.UpdateAsync(job);
        return Results.Ok(PublishBatchResult.Preflight(selected, blocked));
    }

    var settings = AlibabaSettings.FromConfiguration(config);
    var token = await tokenStore.GetAsync();
    if (!settings.IsConfigured || token is null)
    {
        var exportPath = await export.WriteAlibabaCsvAsync(selected);
        job.Status = PublishJobStatus.FallbackExported;
        job.Message = "Alibaba API is not configured or token is missing.";
        job.ExportPath = exportPath;
        job.FinishedAt = DateTimeOffset.UtcNow;
        await jobStore.UpdateAsync(job);
        return Results.Ok(PublishBatchResult.Fallback(selected, exportPath, "Alibaba API is not fully configured or token is missing. No publish request was sent."));
    }

    var results = new List<PublishItemResult>();
    foreach (var product in selected)
    {
        var result = await publisher.PublishAsync(product, settings, token.AccessToken);
        results.Add(result);
        job.Results.Add(result);
        product.PublishState = result.Success ? PublishState.Published : PublishState.Failed;
        product.LastPublishMessage = result.Message;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await repo.UpsertAsync(product);
    }

    job.Status = results.Any(x => !x.Success) ? PublishJobStatus.CompletedWithErrors : PublishJobStatus.Completed;
    job.Message = results.Any(x => !x.Success) ? "Publish completed with failed items." : "Publish completed.";
    job.FinishedAt = DateTimeOffset.UtcNow;
    await jobStore.UpdateAsync(job);
    return Results.Ok(PublishBatchResult.Completed(results));
});

api.MapGet("/catalog/export/alibaba.csv", async (ProductRepository repo, ExportService export) =>
{
    var selected = await repo.GetAllAsync();
    var bytes = export.BuildAlibabaCsv(selected);
    return Results.File(bytes, "text/csv; charset=utf-8", $"soford-alibaba-products-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
});

api.MapGet("/catalog/publish-jobs", async (PublishJobStore jobs, int take = 100) =>
{
    var items = await jobs.GetLatestAsync(Math.Clamp(take, 1, 500));
    return Results.Ok(items);
});

app.Run();

public sealed record ProductDraft(
    string Sku,
    string Title,
    string CategoryId,
    string Currency,
    decimal Price,
    int MinimumOrderQuantity,
    int Stock,
    int LeadTimeDays,
    string Description,
    string MainImageUrl,
    string[] DetailImageUrls,
    string[] Keywords,
    Dictionary<string, string> Attributes);

public sealed class ProductRecord
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal Price { get; set; }
    public int MinimumOrderQuantity { get; set; }
    public int Stock { get; set; }
    public int LeadTimeDays { get; set; }
    public string Description { get; set; } = "";
    public string MainImageUrl { get; set; } = "";
    public string[] DetailImageUrls { get; set; } = [];
    public string[] Keywords { get; set; } = [];
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public PublishState PublishState { get; set; } = PublishState.Draft;
    public string? LastPublishMessage { get; set; }
    public List<QualityIssue> QualityIssues { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public static ProductRecord FromDraft(ProductDraft draft, Guid? id = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Sku = draft.Sku.Trim(),
        Title = draft.Title.Trim(),
        CategoryId = draft.CategoryId.Trim(),
        Currency = string.IsNullOrWhiteSpace(draft.Currency) ? "USD" : draft.Currency.Trim().ToUpperInvariant(),
        Price = draft.Price,
        MinimumOrderQuantity = draft.MinimumOrderQuantity,
        Stock = draft.Stock,
        LeadTimeDays = draft.LeadTimeDays,
        Description = draft.Description.Trim(),
        MainImageUrl = draft.MainImageUrl.Trim(),
        DetailImageUrls = draft.DetailImageUrls.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        Keywords = draft.Keywords.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray(),
        Attributes = new Dictionary<string, string>(draft.Attributes.Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value)), StringComparer.OrdinalIgnoreCase),
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

public enum PublishState
{
    Draft,
    Ready,
    Published,
    Failed
}

public enum QualitySeverity
{
    Info,
    Warning,
    Blocker
}

public enum PublishJobStatus
{
    Running,
    PreflightPassed,
    Blocked,
    FallbackExported,
    Completed,
    CompletedWithErrors
}

public sealed record QualityIssue(QualitySeverity Severity, string Field, string Message);
public sealed record PublishRequest(Guid[] ProductIds, bool PreflightOnly = false);
public sealed record QualityBatchResult(int ProductCount, int IssueCount, IReadOnlyList<ProductRecord> Products);
public sealed record ImportResult(int Imported, int Skipped, string[] Headers, List<ProductRecord> Products, List<string> Warnings);
public sealed record ApiCallLogRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string ApiKey,
    string Method,
    string Endpoint,
    int StatusCode,
    bool Success,
    string Message,
    string TraceId);

public sealed class PublishJobRecord
{
    public Guid Id { get; set; }
    public Guid[] ProductIds { get; set; } = [];
    public bool PreflightOnly { get; set; }
    public PublishJobStatus Status { get; set; } = PublishJobStatus.Running;
    public string Message { get; set; } = "Running";
    public string? ExportPath { get; set; }
    public List<PublishItemResult> Results { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }

    public static PublishJobRecord Create(Guid[] productIds, bool preflightOnly) => new()
    {
        Id = Guid.NewGuid(),
        ProductIds = productIds,
        PreflightOnly = preflightOnly,
        CreatedAt = DateTimeOffset.UtcNow
    };
}

public sealed record AlibabaIntegrationStatus(
    bool HasAppCredentials,
    bool HasToken,
    DateTimeOffset? AccessTokenExpiresAt,
    DateTimeOffset? RefreshTokenExpiresAt,
    string? BaseUrl,
    string? ProductPublishPath,
    string? AuthBaseUrl,
    string? AuthTokenCreatePath,
    string Message,
    string[] Rules)
{
    public bool IsConfigured => HasAppCredentials && HasToken && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ProductPublishPath);
}

public sealed record AlibabaAuthCodeRequest(string Code);
public sealed record AlibabaTokenResponse(bool HasToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt)
{
    public static AlibabaTokenResponse FromRecord(AlibabaTokenRecord record) => new(true, record.AccessTokenExpiresAt, record.RefreshTokenExpiresAt);
}

public sealed record AlibabaCallRequest(string ApiKey, object Payload);
public sealed record AlibabaFlexiblePayloadRequest(Dictionary<string, object>? Payload = null);
public sealed record AlibabaCatalogTreeRequest(string? ParentCategoryId = null, int Level = 1);
public sealed record AlibabaCategoryAttributesRequest(string CategoryId);
public sealed record AlibabaCategoryPredictRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("title")] string Title,
    [property: System.Text.Json.Serialization.JsonPropertyName("description")] string? Description = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("image")] string? Image = null);
public sealed record AlibabaCategoryIdMappingRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("category_id")] string? CategoryId = null);
public sealed record AlibabaImageUploadRequest(string FileName, string ContentType, string Base64Content, string? Folder);
public sealed record AlibabaProductPayloadRequest(Guid ProductId, Dictionary<string, object>? Extra = null);
public sealed record AlibabaRemoteIdRequest(string RemoteProductId);
public sealed record AlibabaPagedRequest(int Page = 1, int PageSize = 50);
public sealed record AlibabaOrderSearchRequest(DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo, string? Status, int Page = 1, int PageSize = 50);
public sealed record AlibabaOrderDetailRequest(string OrderId);
public sealed record AlibabaShipmentCreateRequest(string OrderId, string CarrierCode, string TrackingNumber, Dictionary<string, object>? Extra = null);
public sealed record AlibabaVideoQueryRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("current_page")] int CurrentPage = 1,
    [property: System.Text.Json.Serialization.JsonPropertyName("page_size")] int PageSize = 10,
    [property: System.Text.Json.Serialization.JsonPropertyName("video_id")] long? VideoId = null);

public sealed record AlibabaApiDefinition(
    string Key,
    string Area,
    string Method,
    string PayloadParameter,
    bool RequiresToken,
    string Description,
    string Comment);

public sealed record AlibabaApiResult(
    string ApiKey,
    string Method,
    string Endpoint,
    bool Success,
    int StatusCode,
    string RawBody,
    JsonElement? Json);

public sealed record AlibabaProductPayload(
    string Sku,
    string Title,
    string CategoryId,
    string Currency,
    decimal Price,
    int MinimumOrderQuantity,
    int Stock,
    int LeadTimeDays,
    string Description,
    string MainImageUrl,
    string[] DetailImageUrls,
    string[] Keywords,
    Dictionary<string, string> Attributes,
    Dictionary<string, object>? Extra)
{
    public static AlibabaProductPayload FromProduct(ProductRecord product, Dictionary<string, object>? extra) => new(
        product.Sku,
        product.Title,
        product.CategoryId,
        product.Currency,
        product.Price,
        product.MinimumOrderQuantity,
        product.Stock,
        product.LeadTimeDays,
        product.Description,
        product.MainImageUrl,
        product.DetailImageUrls,
        product.Keywords,
        product.Attributes,
        extra);
}

public sealed record PublishItemResult(Guid ProductId, string Sku, bool Success, string Message, string? RemoteId);

public sealed record PublishBatchResult(string Status, int ProductCount, int BlockedCount, string Message, string? ExportPath, IReadOnlyList<ProductRecord>? Products, IReadOnlyList<PublishItemResult>? Results)
{
    public static PublishBatchResult Preflight(IReadOnlyList<ProductRecord> products, IReadOnlyList<ProductRecord> blocked) =>
        new("Preflight", products.Count, blocked.Count, blocked.Count == 0 ? "Products are ready for real publish." : "Fix blocker issues before publishing.", null, products, null);

    public static PublishBatchResult Fallback(IReadOnlyList<ProductRecord> products, string exportPath, string message) =>
        new("FallbackExported", products.Count, 0, message, exportPath, products, null);

    public static PublishBatchResult Completed(IReadOnlyList<PublishItemResult> results) =>
        new("Completed", results.Count, results.Count(x => !x.Success), "Real Alibaba publish calls completed.", null, null, results);
}

public sealed class ProductQualityService
{
    public List<QualityIssue> Check(ProductRecord product)
    {
        var issues = new List<QualityIssue>();
        Required(product.Sku, "sku", "SKU is required.");
        Required(product.Title, "title", "English product title is required.");
        Required(product.CategoryId, "categoryId", "Alibaba category ID is required.");
        Required(product.Description, "description", "Product description is required.");

        if (product.Title.Length is > 0 and < 25)
        {
            issues.Add(new(QualitySeverity.Warning, "title", "Title is short; include material, use case, model or core differentiator."));
        }

        if (product.Title.Length > 128)
        {
            issues.Add(new(QualitySeverity.Blocker, "title", "Title exceeds 128 characters."));
        }

        if (product.Price <= 0)
        {
            issues.Add(new(QualitySeverity.Blocker, "price", "Price must be greater than zero."));
        }

        if (product.MinimumOrderQuantity <= 0)
        {
            issues.Add(new(QualitySeverity.Blocker, "minimumOrderQuantity", "MOQ must be greater than zero."));
        }

        if (product.Stock < 0)
        {
            issues.Add(new(QualitySeverity.Blocker, "stock", "Stock cannot be negative."));
        }

        if (product.LeadTimeDays <= 0)
        {
            issues.Add(new(QualitySeverity.Warning, "leadTimeDays", "Lead time should be provided for buyer expectation."));
        }

        if (!IsHttpUrl(product.MainImageUrl))
        {
            issues.Add(new(QualitySeverity.Blocker, "mainImageUrl", "Main image must be a public http/https URL."));
        }

        if (product.DetailImageUrls.Any(x => !IsHttpUrl(x)))
        {
            issues.Add(new(QualitySeverity.Warning, "detailImageUrls", "Detail image URLs should be public http/https URLs."));
        }

        if (product.Keywords.Length < 3)
        {
            issues.Add(new(QualitySeverity.Warning, "keywords", "Add at least three buyer-search keywords."));
        }

        if (product.Attributes.Count < 3)
        {
            issues.Add(new(QualitySeverity.Warning, "attributes", "Add key attributes such as material, size, color, certification or model."));
        }

        product.PublishState = issues.Any(x => x.Severity == QualitySeverity.Blocker) ? PublishState.Draft : PublishState.Ready;
        return issues;

        void Required(string? value, string field, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new(QualitySeverity.Blocker, field, message));
            }
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

public sealed class ProductRepository(IWebHostEnvironment env)
{
    private readonly string _file = Path.Combine(env.ContentRootPath, "App_Data", "products.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<List<ProductRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadUnsafeAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProductRecord?> GetAsync(Guid id) => (await GetAllAsync()).FirstOrDefault(x => x.Id == id);

    public async Task<List<ProductRecord>> GetManyAsync(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return (await GetAllAsync()).Where(x => set.Contains(x.Id)).ToList();
    }

    public async Task UpsertAsync(ProductRecord product)
    {
        await BulkUpsertAsync([product]);
    }

    public async Task BulkUpsertAsync(IEnumerable<ProductRecord> products)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadUnsafeAsync();
            foreach (var product in products)
            {
                var existing = all.FindIndex(x => x.Sku.Equals(product.Sku, StringComparison.OrdinalIgnoreCase) || x.Id == product.Id);
                product.UpdatedAt = DateTimeOffset.UtcNow;
                if (existing >= 0)
                {
                    product.Id = all[existing].Id;
                    product.CreatedAt = all[existing].CreatedAt;
                    all[existing] = product;
                }
                else
                {
                    all.Add(product);
                }
            }

            await WriteUnsafeAsync(all);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteAsync(Guid id)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadUnsafeAsync();
            var removed = all.RemoveAll(x => x.Id == id) > 0;
            if (removed)
            {
                await WriteUnsafeAsync(all);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ProductRecord>> ReadUnsafeAsync()
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        await using var stream = File.OpenRead(_file);
        return await JsonSerializer.DeserializeAsync<List<ProductRecord>>(stream, JsonOptions) ?? [];
    }

    private async Task WriteUnsafeAsync(List<ProductRecord> products)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        await using var stream = File.Create(_file);
        await JsonSerializer.SerializeAsync(stream, products, JsonOptions);
    }
}

public sealed class ProductImportService
{
    private static readonly Dictionary<string, string[]> HeaderMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sku"] = ["sku", "product code", "item code", "model"],
        ["Title"] = ["title", "product title", "english title"],
        ["CategoryId"] = ["categoryid", "category id", "category"],
        ["Currency"] = ["currency"],
        ["Price"] = ["price", "unit price"],
        ["MinimumOrderQuantity"] = ["moq", "minimum order quantity"],
        ["Stock"] = ["stock", "inventory"],
        ["LeadTimeDays"] = ["leadtime", "lead time days"],
        ["Description"] = ["description", "product description", "details"],
        ["MainImageUrl"] = ["main image", "mainimageurl", "main image url"],
        ["DetailImageUrls"] = ["detail images", "detailimageurls", "detail image urls"],
        ["Keywords"] = ["keywords"],
        ["Attributes"] = ["attributes"]
    };

    public ImportResult Parse(Stream stream, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".xlsx" => ParseXlsx(stream),
            ".csv" => ParseCsv(stream),
            _ => throw new InvalidOperationException("Only .xlsx and .csv imports are supported.")
        };
    }

    private ImportResult ParseXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();
        var rows = sheet.RangeUsed()?.RowsUsed().ToList() ?? [];
        if (rows.Count <= 1)
        {
            return new ImportResult(0, 0, [], [], ["The worksheet contains no product rows."]);
        }

        var headers = rows[0].Cells().Select(x => x.GetString().Trim()).ToArray();
        var products = new List<ProductRecord>();
        var warnings = new List<string>();
        for (var i = 1; i < rows.Count; i++)
        {
            var values = rows[i].Cells(1, headers.Length).Select(x => x.GetString()).ToArray();
            AddRow(headers, values, i + 1, products, warnings);
        }

        return new ImportResult(products.Count, warnings.Count(x => x.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)), headers, products, warnings);
    }

    private ImportResult ParseCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var lines = new List<string[]>();
        while (!reader.EndOfStream)
        {
            lines.Add(SplitCsvLine(reader.ReadLine() ?? ""));
        }

        if (lines.Count <= 1)
        {
            return new ImportResult(0, 0, [], [], ["The CSV contains no product rows."]);
        }

        var headers = lines[0].Select(x => x.Trim()).ToArray();
        var products = new List<ProductRecord>();
        var warnings = new List<string>();
        for (var i = 1; i < lines.Count; i++)
        {
            AddRow(headers, lines[i], i + 1, products, warnings);
        }

        return new ImportResult(products.Count, warnings.Count(x => x.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase)), headers, products, warnings);
    }

    private static void AddRow(string[] headers, string[] values, int rowNumber, List<ProductRecord> products, List<string> warnings)
    {
        string Get(string key)
        {
            var aliases = HeaderMap[key];
            for (var i = 0; i < headers.Length; i++)
            {
                if (aliases.Any(alias => string.Equals(headers[i], alias, StringComparison.OrdinalIgnoreCase)))
                {
                    return i < values.Length ? values[i].Trim() : "";
                }
            }

            return "";
        }

        var sku = Get("Sku");
        var title = Get("Title");
        if (string.IsNullOrWhiteSpace(sku) && string.IsNullOrWhiteSpace(title))
        {
            warnings.Add($"Skipped row {rowNumber}: SKU and title are both empty.");
            return;
        }

        products.Add(ProductRecord.FromDraft(new ProductDraft(
            sku,
            title,
            Get("CategoryId"),
            Get("Currency"),
            ParseDecimal(Get("Price")),
            ParseInt(Get("MinimumOrderQuantity")),
            ParseInt(Get("Stock")),
            ParseInt(Get("LeadTimeDays")),
            Get("Description"),
            Get("MainImageUrl"),
            SplitList(Get("DetailImageUrls")),
            SplitList(Get("Keywords")),
            ParseAttributes(Get("Attributes")))));
    }

    private static decimal ParseDecimal(string value) =>
        decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string[] SplitList(string value) =>
        value.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Dictionary<string, string> ParseAttributes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(value);
            if (parsed is not null)
            {
                return new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            // Fall through to key:value parser.
        }

        return value
            .Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split([':', '='], 2, StringSplitOptions.TrimEntries))
            .Where(pair => pair.Length == 2 && pair[0].Length > 0 && pair[1].Length > 0)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
    }

    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                current.Append('"');
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result.ToArray();
    }
}

public sealed class ExportService(IWebHostEnvironment env)
{
    public byte[] BuildAlibabaCsv(IEnumerable<ProductRecord> products)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Sku,Title,CategoryId,Currency,Price,MOQ,Stock,LeadTimeDays,MainImageUrl,DetailImageUrls,Keywords,Attributes,Description");
        foreach (var item in products)
        {
            var values = new[]
            {
                item.Sku,
                item.Title,
                item.CategoryId,
                item.Currency,
                item.Price.ToString(CultureInfo.InvariantCulture),
                item.MinimumOrderQuantity.ToString(CultureInfo.InvariantCulture),
                item.Stock.ToString(CultureInfo.InvariantCulture),
                item.LeadTimeDays.ToString(CultureInfo.InvariantCulture),
                item.MainImageUrl,
                string.Join(';', item.DetailImageUrls),
                string.Join(';', item.Keywords),
                JsonSerializer.Serialize(item.Attributes),
                item.Description
            };
            builder.AppendLine(string.Join(',', values.Select(Escape)));
        }

        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    public async Task<string> WriteAlibabaCsvAsync(IEnumerable<ProductRecord> products)
    {
        var directory = Path.Combine(env.ContentRootPath, "App_Data", "exports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"alibaba-products-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
        await File.WriteAllBytesAsync(path, BuildAlibabaCsv(products));
        return path;
    }

    private static string Escape(string value)
    {
        var safe = value ?? "";
        return safe.Contains('"') || safe.Contains(',') || safe.Contains('\n')
            ? $"\"{safe.Replace("\"", "\"\"")}\""
            : safe;
    }
}

public sealed class AlibabaTokenStore(IWebHostEnvironment env)
{
    private readonly string _file = Path.Combine(env.ContentRootPath, "App_Data", "alibaba-token.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<AlibabaTokenRecord?> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(_file))
            {
                return null;
            }

            await using var stream = File.OpenRead(_file);
            return await JsonSerializer.DeserializeAsync<AlibabaTokenRecord>(stream, JsonOptions);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(AlibabaTokenRecord token)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            await using var stream = File.Create(_file);
            await JsonSerializer.SerializeAsync(stream, token, JsonOptions);
        }
        finally
        {
            _lock.Release();
        }
    }
}

public sealed class AlibabaApiLogStore(IWebHostEnvironment env)
{
    private readonly string _file = Path.Combine(env.ContentRootPath, "App_Data", "alibaba-api-logs.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task AppendAsync(ApiCallLogRecord log)
    {
        await _lock.WaitAsync();
        try
        {
            var logs = await ReadUnsafeAsync();
            logs.Add(log);
            await WriteUnsafeAsync(logs.OrderByDescending(x => x.CreatedAt).Take(1000).OrderBy(x => x.CreatedAt).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ApiCallLogRecord>> GetLatestAsync(int take)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadUnsafeAsync()).OrderByDescending(x => x.CreatedAt).Take(take).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ApiCallLogRecord>> ReadUnsafeAsync()
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        await using var stream = File.OpenRead(_file);
        return await JsonSerializer.DeserializeAsync<List<ApiCallLogRecord>>(stream, JsonOptions) ?? [];
    }

    private async Task WriteUnsafeAsync(List<ApiCallLogRecord> logs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        await using var stream = File.Create(_file);
        await JsonSerializer.SerializeAsync(stream, logs, JsonOptions);
    }
}

public sealed class PublishJobStore(IWebHostEnvironment env)
{
    private readonly string _file = Path.Combine(env.ContentRootPath, "App_Data", "publish-jobs.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task AppendAsync(PublishJobRecord job)
    {
        await _lock.WaitAsync();
        try
        {
            var jobs = await ReadUnsafeAsync();
            jobs.Add(job);
            await WriteUnsafeAsync(jobs);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(PublishJobRecord job)
    {
        await _lock.WaitAsync();
        try
        {
            var jobs = await ReadUnsafeAsync();
            var index = jobs.FindIndex(x => x.Id == job.Id);
            if (index >= 0)
            {
                jobs[index] = job;
            }
            else
            {
                jobs.Add(job);
            }

            await WriteUnsafeAsync(jobs.OrderByDescending(x => x.CreatedAt).Take(1000).OrderBy(x => x.CreatedAt).ToList());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<PublishJobRecord>> GetLatestAsync(int take)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadUnsafeAsync()).OrderByDescending(x => x.CreatedAt).Take(take).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PublishJobRecord>> ReadUnsafeAsync()
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        await using var stream = File.OpenRead(_file);
        return await JsonSerializer.DeserializeAsync<List<PublishJobRecord>>(stream, JsonOptions) ?? [];
    }

    private async Task WriteUnsafeAsync(List<PublishJobRecord> jobs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        await using var stream = File.Create(_file);
        await JsonSerializer.SerializeAsync(stream, jobs, JsonOptions);
    }
}

public sealed class AlibabaOpenApiClient(
    HttpClient httpClient,
    ILogger<AlibabaOpenApiClient> logger,
    AlibabaApiLogStore logStore,
    AlibabaAuthClient authClient,
    AlibabaTokenStore tokenStore)
{
    public async Task<IResult> CallAsync(AlibabaSettings settings, AlibabaTokenRecord? token, string apiKey, object? payload)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return Results.BadRequest(new ProblemDetails { Title = "Alibaba BaseUrl is required" });
        }

        if (!settings.HasAppCredentials)
        {
            return Results.BadRequest(new ProblemDetails { Title = "Alibaba AppKey/AppSecret/AuthBaseUrl are required" });
        }

        if (!settings.ApiRegistry.TryGetValue(apiKey, out var definition))
        {
            return Results.BadRequest(new ProblemDetails { Title = $"Alibaba API key '{apiKey}' is not registered" });
        }

        if (string.IsNullOrWhiteSpace(definition.Method))
        {
            return Results.BadRequest(new ProblemDetails
            {
                Title = $"Alibaba API '{apiKey}' method is not configured",
                Detail = "Set Alibaba:Apis:{key}:Method to the exact method/path granted in Alibaba App Console."
            });
        }

        if (definition.RequiresToken && string.IsNullOrWhiteSpace(token?.AccessToken))
        {
            return Results.BadRequest(new ProblemDetails { Title = "Alibaba access token is required" });
        }

        if (definition.RequiresToken && token!.AccessTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            if (string.IsNullOrWhiteSpace(token.RefreshToken) || token.RefreshTokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "Alibaba access token is expired or about to expire",
                    Detail = "Refresh token is missing or expired. Re-authorize the seller account."
                });
            }

            token = await authClient.RefreshTokenAsync(settings, token.RefreshToken);
            await tokenStore.SaveAsync(token);
        }

        var result = await SendAsync(settings, token, definition, payload);
        return Results.Ok(result);
    }

    public async Task<AlibabaApiResult> SendAsync(AlibabaSettings settings, AlibabaTokenRecord? token, AlibabaApiDefinition definition, object? payload)
    {
        var endpoint = BuildEndpoint(settings.BaseUrl!, definition.Method);
        var isPathApi = definition.Method.Contains('/');
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_key"] = settings.AppKey!,
            ["timestamp"] = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["format"] = "json",
            ["v"] = settings.ApiVersion,
            ["sign_method"] = "md5"
        };

        if (isPathApi)
        {
            AddPayloadParameters(parameters, payload);
        }
        else
        {
            parameters["method"] = definition.Method;
            parameters[definition.PayloadParameter] = JsonSerializer.Serialize(payload ?? new { }, JsonOptions);
        }

        if (definition.RequiresToken)
        {
            parameters["session"] = token!.AccessToken;
        }

        parameters["sign"] = Sign(parameters, settings.AppSecret!);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };
        var traceId = Guid.NewGuid().ToString("N");
        request.Headers.TryAddWithoutValidation("X-Soford-Trace-Id", traceId);

        using var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        JsonElement? json = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            logger.LogInformation("Alibaba API {ApiKey} returned non-JSON body.", definition.Key);
        }

        var result = new AlibabaApiResult(
            definition.Key,
            definition.Method,
            endpoint.ToString(),
            response.IsSuccessStatusCode,
            (int)response.StatusCode,
            body,
            json);
        await logStore.AppendAsync(new ApiCallLogRecord(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            definition.Key,
            definition.Method,
            endpoint.ToString(),
            (int)response.StatusCode,
            response.IsSuccessStatusCode,
            response.IsSuccessStatusCode ? "OK" : TrimForLog(body),
            traceId));
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static Uri BuildEndpoint(string baseUrl, string method)
    {
        if (!method.Contains('/'))
        {
            return new Uri(baseUrl);
        }

        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), method.TrimStart('/'));
    }

    internal static void AddPayloadParameters(SortedDictionary<string, string> parameters, object? payload)
    {
        if (payload is null)
        {
            return;
        }

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, JsonOptions));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            parameters["value"] = document.RootElement.GetRawText();
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                parameters[property.Name] = value;
            }
        }
    }

    public static string Sign(SortedDictionary<string, string> parameters, string secret)
    {
        var raw = new StringBuilder(secret);
        foreach (var (key, value) in parameters.Where(x => !string.Equals(x.Key, "sign", StringComparison.OrdinalIgnoreCase)))
        {
            raw.Append(key).Append(value);
        }
        raw.Append(secret);

        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(raw.ToString())));
    }

    private static string TrimForLog(string value) => value.Length <= 500 ? value : value[..500];
}

public sealed class AlibabaAuthClient(HttpClient httpClient, ILogger<AlibabaAuthClient> logger)
{
    public Task<AlibabaTokenRecord> CreateTokenAsync(AlibabaSettings settings, string code)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["code"] = code
        };

        return SendTokenRequestAsync(settings, settings.AuthTokenCreatePath, parameters);
    }

    public Task<AlibabaTokenRecord> RefreshTokenAsync(AlibabaSettings settings, string refreshToken)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["refresh_token"] = refreshToken
        };

        return SendTokenRequestAsync(settings, settings.AuthTokenRefreshPath, parameters);
    }

    private async Task<AlibabaTokenRecord> SendTokenRequestAsync(AlibabaSettings settings, string path, SortedDictionary<string, string> parameters)
    {
        var endpoint = BuildEndpoint(settings.AuthBaseUrl!, path);
        parameters["app_key"] = settings.AppKey!;
        parameters["timestamp"] = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        parameters["format"] = "json";
        parameters["v"] = settings.ApiVersion;
        parameters["sign_method"] = "md5";
        parameters["sign"] = AlibabaOpenApiClient.Sign(parameters, settings.AppSecret!);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(parameters)
        };

        using var response = await httpClient.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Alibaba token request failed with {Status}: {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Alibaba token API returned {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("code", out var errorCode) && errorCode.ValueKind != JsonValueKind.Null && errorCode.GetRawText() != "\"0\"")
        {
            var message = root.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : body;
            throw new InvalidOperationException($"Alibaba token API error {errorCode}: {message}");
        }

        return AlibabaTokenRecord.FromJson(root, body);
    }

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Uri(baseUrl);
        }

        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), path.TrimStart('/'));
    }
}

public sealed class AlibabaTokenRecord
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string RawResponse { get; set; } = "";

    public static AlibabaTokenRecord FromJson(JsonElement root, string rawResponse)
    {
        var accessToken = ReadString(root, "access_token", "accessToken");
        var refreshToken = ReadString(root, "refresh_token", "refreshToken");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Alibaba token API response did not include access_token.");
        }

        var now = DateTimeOffset.UtcNow;
        return new AlibabaTokenRecord
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            AccessTokenExpiresAt = now.AddSeconds(ReadLong(root, "expires_in", "expiresIn", "expire_in", "expireIn")),
            RefreshTokenExpiresAt = now.AddSeconds(ReadLong(root, "refresh_expires_in", "refreshExpiresIn", "refresh_token_timeout")),
            CreatedAt = now,
            RawResponse = rawResponse
        };
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText().Trim('"');
            }
        }

        return "";
    }

    private static long ReadLong(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return 0;
    }
}

public sealed record AlibabaSettings(
    string? BaseUrl,
    string? ProductPublishPath,
    string? AuthBaseUrl,
    string AuthTokenCreatePath,
    string AuthTokenRefreshPath,
    string ApiVersion,
    string DefaultRestMethod,
    string? AppKey,
    string? AppSecret,
    string ProductPayloadParameter,
    IReadOnlyDictionary<string, AlibabaApiDefinition> ApiRegistry)
{
    public bool HasAppCredentials =>
        !string.IsNullOrWhiteSpace(AuthBaseUrl)
        && !string.IsNullOrWhiteSpace(AppKey)
        && !string.IsNullOrWhiteSpace(AppSecret);

    public bool IsConfigured =>
        HasAppCredentials
        &&
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(ProductPublishPath)
        && !string.IsNullOrWhiteSpace(AppKey)
        && !string.IsNullOrWhiteSpace(AppSecret);

    public static AlibabaSettings FromConfiguration(IConfiguration config) => new(
        config["Alibaba:BaseUrl"],
        config["Alibaba:ProductPublishPath"],
        config["Alibaba:AuthBaseUrl"],
        string.IsNullOrWhiteSpace(config["Alibaba:AuthTokenCreatePath"]) ? "/auth/token/create" : config["Alibaba:AuthTokenCreatePath"]!,
        string.IsNullOrWhiteSpace(config["Alibaba:AuthTokenRefreshPath"]) ? "/auth/token/refresh" : config["Alibaba:AuthTokenRefreshPath"]!,
        string.IsNullOrWhiteSpace(config["Alibaba:ApiVersion"]) ? "2.0" : config["Alibaba:ApiVersion"]!,
        string.IsNullOrWhiteSpace(config["Alibaba:DefaultRestMethod"]) ? "alibaba.open.api.call" : config["Alibaba:DefaultRestMethod"]!,
        config["Alibaba:AppKey"],
        config["Alibaba:AppSecret"],
        string.IsNullOrWhiteSpace(config["Alibaba:ProductPayloadParameter"]) ? "product_payload" : config["Alibaba:ProductPayloadParameter"]!,
        BuildApiRegistry(config));

    private static IReadOnlyDictionary<string, AlibabaApiDefinition> BuildApiRegistry(IConfiguration config)
    {
        var defaults = DefaultApis(config);
        var result = new Dictionary<string, AlibabaApiDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in defaults)
        {
            var method = config[$"Alibaba:Apis:{api.Key}:Method"];
            var payloadParameter = config[$"Alibaba:Apis:{api.Key}:PayloadParameter"];
            var requiresToken = config[$"Alibaba:Apis:{api.Key}:RequiresToken"];
            result[api.Key] = api with
            {
                Method = string.IsNullOrWhiteSpace(method) ? api.Method : method!,
                PayloadParameter = string.IsNullOrWhiteSpace(payloadParameter) ? api.PayloadParameter : payloadParameter!,
                RequiresToken = bool.TryParse(requiresToken, out var parsed) ? parsed : api.RequiresToken
            };
        }

        return result;
    }

    private static IReadOnlyList<AlibabaApiDefinition> DefaultApis(IConfiguration config)
    {
        var productPublishPath = config["Alibaba:ProductPublishPath"] ?? "";
        return
        [
            new("auth.token.create", "auth", "/auth/token/create", "auth_payload", false, "Create access_token and refresh_token", "Dedicated backend route: /api/integrations/alibaba/token/create."),
            new("auth.token.refresh", "auth", "/auth/token/refresh", "auth_payload", false, "Refresh access_token", "Dedicated backend route: /api/integrations/alibaba/token/refresh."),
            new("category.tree", "catalog", "/icbu/product/category/get", "category_payload", true, "Query product categories", "Official path=/icbu/product/category/get."),
            new("category.attributes", "catalog", "/alibaba/icbu/category/attribute/get/v2", "category_payload", true, "Query category attributes", "Official path=/alibaba/icbu/category/attribute/get/v2."),
            new("category.get", "catalog", "/alibaba/icbu/category/get/v2", "category_payload", true, "Get category information", "Official path=/alibaba/icbu/category/get/v2."),
            new("category.idMapping", "catalog", "/alibaba/icbu/category/id/mapping", "category_payload", true, "Map category IDs", "Official path=/alibaba/icbu/category/id/mapping."),
            new("category.predict", "catalog", "/alibaba/icbu/category/predict/v2", "category_payload", true, "Predict product category", "Required: title. Optional: description, image."),
            new("photobank.group.list", "media", "/icbu/product/photobank/group/list", "image_payload", true, "List image bank groups", "Official path=/icbu/product/photobank/group/list."),
            new("photobank.group.operate", "media", "/icbu/product/photobank/group/operate", "image_payload", true, "Create or update image bank groups", "Official path=/icbu/product/photobank/group/operate."),
            new("photobank.list", "media", "/icbu/product/photobank/list", "image_payload", true, "List image bank assets", "Official path=/icbu/product/photobank/list."),
            new("image.upload", "media", "/alibaba/icbu/photobank/upload", "image_payload", true, "Upload product image", "Official path=/alibaba/icbu/photobank/upload."),
            new("video.query", "media", "/alibaba/icbu/video/query", "video_payload", true, "Query seller videos", "Required: current_page, page_size. Optional: video_id."),
            new("video.upload", "media", "/alibaba/icbu/video/upload", "video_payload", true, "Upload video", "Official path=/alibaba/icbu/video/upload."),
            new("video.upload.result", "media", "/alibaba/icbu/video/upload/result", "video_payload", true, "Query video upload result", "Official path=/alibaba/icbu/video/upload/result."),
            new("video.relation.product.main", "media", "/alibaba/icbu/video/relation/product/main", "video_payload", true, "Set product main video", "Official path=/alibaba/icbu/video/relation/product/main."),
            new("product.create", "product", string.IsNullOrWhiteSpace(productPublishPath) ? "/alibaba/icbu/product/listing/v2" : productPublishPath, string.IsNullOrWhiteSpace(config["Alibaba:ProductPayloadParameter"]) ? "product_payload" : config["Alibaba:ProductPayloadParameter"]!, true, "Create product listing", "Official path=/alibaba/icbu/product/listing/v2."),
            new("product.update", "product", "/alibaba/icbu/product/update/v2", "product_payload", true, "Update product", "Official path=/alibaba/icbu/product/update/v2."),
            new("product.get", "product", "/alibaba/icbu/product/get/v2", "product_payload", true, "Query product detail", "Official path=/alibaba/icbu/product/get/v2."),
            new("product.search", "product", "/alibaba/icbu/product/search/v2", "product_payload", true, "Query product list", "Official path=/alibaba/icbu/product/search/v2."),
            new("product.status", "product", "/alibaba/icbu/product/status/get/v2", "product_payload", true, "Query product listing status", "Official path=/alibaba/icbu/product/status/get/v2."),
            new("product.status.update", "product", "/alibaba/icbu/product/batch/update/status", "product_payload", true, "Change listing status", "Official path=/alibaba/icbu/product/batch/update/status."),
            new("product.inventory.update", "product", "/icbu/product/edit-inventory", "product_payload", true, "Edit product inventory", "Official path=/icbu/product/edit-inventory."),
            new("product.price.update", "product", "/icbu/product/edit-price", "product_payload", true, "Edit product price", "Official path=/icbu/product/edit-price."),
            new("product.draft.delete", "product", "/alibaba/icbu/draft/delete", "product_payload", true, "Delete draft", "Official path=/alibaba/icbu/draft/delete."),
            new("product.offline", "product", "/alibaba/icbu/product/batch/update/status", "product_payload", true, "Offline product", "Uses status update endpoint."),
            new("product.delete", "product", "/alibaba/icbu/product/delete", "product_payload", true, "Delete product", "Official path=/alibaba/icbu/product/delete."),
            new("product.shippingTemplates", "logistics", "/alibaba/icbu/product/list/shipping/templates", "logistics_payload", true, "Query shipping templates", "Official path=/alibaba/icbu/product/list/shipping/templates."),
            new("order.search", "order", "", "order_payload", true, "Query orders", "Configure Alibaba:Apis:order.search:Method."),
            new("order.detail", "order", "", "order_payload", true, "Query order detail", "Configure Alibaba:Apis:order.detail:Method."),
            new("logistics.freightTemplates", "logistics", "", "logistics_payload", true, "Query freight templates", "Configure Alibaba:Apis:logistics.freightTemplates:Method."),
            new("logistics.shipment.create", "logistics", "", "logistics_payload", true, "Create shipment", "Configure Alibaba:Apis:logistics.shipment.create:Method.")
        ];
    }
}
public sealed class AlibabaPublisher(HttpClient httpClient, ILogger<AlibabaPublisher> logger)
{
    public async Task<PublishItemResult> PublishAsync(ProductRecord product, AlibabaSettings settings, string accessToken)
    {
        if (!settings.IsConfigured)
        {
            return new(product.Id, product.Sku, false, "Alibaba settings are incomplete. Publish was not attempted.", null);
        }

        var endpoint = settings.ProductPublishPath!.Contains('/')
            ? new Uri(new Uri(settings.BaseUrl!.TrimEnd('/') + "/"), settings.ProductPublishPath.TrimStart('/'))
            : new Uri(settings.BaseUrl!);
        var method = settings.ProductPublishPath.Contains('/') ? "soford.product.publish" : settings.ProductPublishPath;
        var payload = JsonSerializer.Serialize(new
        {
            product.Sku,
            product.Title,
            product.CategoryId,
            product.Currency,
            product.Price,
            moq = product.MinimumOrderQuantity,
            product.Stock,
            product.LeadTimeDays,
            product.Description,
            product.MainImageUrl,
            product.DetailImageUrls,
            product.Keywords,
            product.Attributes
        });
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["method"] = method,
            ["app_key"] = settings.AppKey!,
            ["session"] = accessToken,
            ["timestamp"] = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("China Standard Time")).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["format"] = "json",
            ["v"] = settings.ApiVersion,
            ["sign_method"] = "md5",
            [settings.ProductPayloadParameter] = payload
        };
        parameters["sign"] = AlibabaOpenApiClient.Sign(parameters, settings.AppSecret!);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation("X-Soford-Trace-Id", product.Id.ToString("N"));
        request.Content = new FormUrlEncodedContent(parameters);

        try
        {
            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Alibaba publish failed for {Sku} with {Status}: {Body}", product.Sku, response.StatusCode, body);
                return new(product.Id, product.Sku, false, $"Alibaba API returned {(int)response.StatusCode}: {body}", null);
            }

            string? remoteId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("id", out var id))
                {
                    remoteId = id.GetString();
                }
                else if (doc.RootElement.TryGetProperty("productId", out var productId))
                {
                    remoteId = productId.GetString();
                }
            }
            catch (JsonException)
            {
                // Non-JSON success responses are accepted but kept auditable in the message.
            }

            return new(product.Id, product.Sku, true, "Published through configured Alibaba endpoint.", remoteId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alibaba publish exception for {Sku}", product.Sku);
            return new(product.Id, product.Sku, false, ex.Message, null);
        }
    }

}


