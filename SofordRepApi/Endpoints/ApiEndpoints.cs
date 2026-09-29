using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

public sealed record IdsRequest(Guid[]? ProductIds)
{
    /// <summary>Never null: a missing or null productIds is treated as an empty selection.</summary>
    public Guid[] Ids => ProductIds?.Distinct().ToArray() ?? [];
}
public sealed record AlibabaCodeRequest(string Code);
public sealed record AlibabaCallRequest(string ApiKey, JsonElement? Payload);

public static class ApiEndpoints
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    public static IResult Problem(string title, string? detail = null, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(title: title, detail: detail, statusCode: status);

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow })).AllowAnonymous();

        api.MapGet("/auth/session", (ClaimsPrincipal user) =>
            Results.Ok(new AuthSessionResponse(user.Identity?.IsAuthenticated == true, user.Identity?.Name))).AllowAnonymous();

        api.MapPost("/auth/login", async (LoginRequest request, HttpContext http, IConfiguration config, IHostEnvironment env, LoginThrottle throttle) =>
        {
            var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.LockedFor(client) is { } wait)
            {
                return Problem("登录失败次数过多", $"请 {Math.Ceiling(wait.TotalMinutes)} 分钟后再试。", StatusCodes.Status429TooManyRequests);
            }

            if (!LocalAdminAuth.Verify(config, env, request.Username?.Trim(), request.Password))
            {
                throttle.RecordFailure(client);
                return Problem("用户名或密码错误", status: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset(client);
            var username = LocalAdminAuth.Username(config, env);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, username), new Claim(ClaimTypes.Role, "Admin")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = request.Remember,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(request.Remember ? 24 : 10)
            });
            return Results.Ok(new AuthSessionResponse(true, username));
        }).AllowAnonymous();

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new AuthSessionResponse(false, null));
        });

        api.MapGet("/system/diagnostics", async (SystemDiagnostics diagnostics, HttpContext http) => Results.Ok(await diagnostics.RunAsync(http)));

        api.MapGet("/system/automation", (AutomationState state) => Results.Ok(state.Current));

        api.MapPost("/system/automation/run", async (RemoteSyncWorker worker) => Results.Ok(await worker.RunOnceAsync()));
    }

    public static void MapAlibabaEndpoints(this RouteGroupBuilder api)
    {
        var alibaba = api.MapGroup("/integrations/alibaba");

        alibaba.MapGet("/status", async (IConfiguration config, AlibabaTokenService tokens, HttpContext http) =>
        {
            var settings = AlibabaSettings.FromConfiguration(config);
            var token = await tokens.GetAsync();
            return Results.Ok(new
            {
                Warning = AuthorizationWarning(token, DateTimeOffset.UtcNow),
                settings.HasCredentials,
                HasToken = token is not null,
                token?.AccessTokenExpiresAt,
                token?.RefreshTokenExpiresAt,
                token?.Account,
                settings.GatewayUrl,
                CallbackUrl = AlibabaOAuth.BuildCallbackUrl(settings, http),
                Ready = settings.HasCredentials && token is not null && (token.AccessTokenExpiresAt > DateTimeOffset.UtcNow || token.RefreshTokenExpiresAt > DateTimeOffset.UtcNow)
            });
        });

        alibaba.MapGet("/oauth/url", (HttpContext http, IConfiguration config, IDataProtectionProvider dataProtection) =>
        {
            var settings = AlibabaSettings.FromConfiguration(config);
            if (string.IsNullOrWhiteSpace(settings.AppKey))
            {
                return Problem("未配置 Alibaba AppKey");
            }

            var callbackUrl = AlibabaOAuth.BuildCallbackUrl(settings, http);
            var state = AlibabaOAuth.StartState(http, dataProtection, http.User);
            return Results.Ok(new { Url = AlibabaOAuth.BuildAuthorizeUrl(settings, callbackUrl, state), CallbackUrl = callbackUrl });
        });

        alibaba.MapGet("/oauth/callback", CallbackAsync);

        alibaba.MapPost("/token/create", async (AlibabaCodeRequest request, AlibabaTokenService tokens) =>
        {
            var code = ExtractCode(request.Code);
            if (string.IsNullOrWhiteSpace(code))
            {
                return Problem("请粘贴授权回调链接或其中的 code");
            }

            try
            {
                var token = await tokens.CreateFromCodeAsync(code);
                return Results.Ok(new { HasToken = true, token.AccessTokenExpiresAt, token.RefreshTokenExpiresAt, token.Account });
            }
            catch (AlibabaApiException ex)
            {
                return Problem("换取 Token 失败", AlibabaErrors.Explain(ex.Result) + (ex.Result.ErrorCode == "InvalidCode" ? "。code 只能使用一次且 30 分钟内有效，请重新授权。" : ""));
            }
            catch (InvalidOperationException ex)
            {
                return Problem("换取 Token 失败", ex.Message);
            }
        });

        alibaba.MapPost("/token/refresh", async (AlibabaTokenService tokens) =>
        {
            try
            {
                var token = await tokens.RefreshAsync();
                return Results.Ok(new { HasToken = true, token.AccessTokenExpiresAt, token.RefreshTokenExpiresAt, token.Account });
            }
            catch (AlibabaApiException ex)
            {
                return Problem("刷新 Token 失败", AlibabaErrors.Explain(ex.Result));
            }
            catch (InvalidOperationException ex)
            {
                return Problem("刷新 Token 失败", ex.Message);
            }
        });

        alibaba.MapDelete("/token", async (AlibabaTokenStore store) =>
        {
            await store.ClearAsync();
            return Results.NoContent();
        });

        alibaba.MapGet("/apis", (IConfiguration config) =>
            Results.Ok(AlibabaSettings.FromConfiguration(config).Apis.Values.OrderBy(x => x.Area).ThenBy(x => x.Key)));

        alibaba.MapGet("/logs", async (AlibabaApiLogStore logs, int take = 100) => Results.Ok(await logs.GetLatestAsync(Math.Clamp(take, 1, 500))));

        alibaba.MapPost("/call", async (AlibabaCallRequest request, AlibabaClient client) =>
            Results.Ok(await client.CallAsync(request.ApiKey, request.Payload is { ValueKind: JsonValueKind.Object } payload ? payload : null)));

        alibaba.MapGet("/categories/{categoryId}/attributes", async (string categoryId, bool? refresh, CategoryAttributeService categories, AlibabaClient client) =>
        {
            var (set, error) = await categories.GetAsync(client, categoryId.Trim(), refresh == true);
            return set is not null ? Results.Ok(set) : Problem("获取类目属性失败", AlibabaErrors.Explain(error!));
        });

        alibaba.MapPost("/images/upload", async ([FromForm] IFormFile file, [FromForm] string? groupId, AlibabaClient client) =>
        {
            if (file.Length == 0) return Problem("图片为空");
            if (file.Length > MaxImageBytes) return Problem("图片不能超过 10MB");
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Problem("只能上传图片文件");

            using var memory = new MemoryStream();
            await file.CopyToAsync(memory);
            var payload = new Dictionary<string, object?> { ["file_name"] = file.FileName };
            if (!string.IsNullOrWhiteSpace(groupId)) payload["group_id"] = groupId.Trim();

            var result = await client.CallAsync("image.upload", payload, new AlibabaFile("image_bytes", file.FileName, file.ContentType, memory.ToArray()));
            if (!result.Success) return Problem("图片上传失败", AlibabaErrors.Explain(result));

            var url = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "photobank_url"));
            if (string.IsNullOrWhiteSpace(url)) return Problem("图片上传失败", "Alibaba 未返回图片地址，请在 API 日志中查看响应。");
            if (url.StartsWith("//")) url = "https:" + url;
            return Results.Ok(new { Url = url, FileId = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "file_id")) });
        }).DisableAntiforgery();
    }

    public static Task<IResult> CallbackAsync(
        [FromQuery] string? code,
        [FromQuery] string? state,
        HttpContext http,
        AlibabaTokenService tokens,
        IConfiguration config,
        IDataProtectionProvider dataProtection,
        ILoggerFactory loggers) =>
        AlibabaOAuth.CompleteCallbackAsync(code, state, http, tokens, config, dataProtection, loggers.CreateLogger("AlibabaOAuth"));

    /// <summary>Warns before the seller authorization lapses; refresh tokens cannot be renewed, only re-authorized.</summary>
    public static string? AuthorizationWarning(AlibabaTokenRecord? token, DateTimeOffset now)
    {
        if (token is null) return null;
        var accessValid = token.AccessTokenExpiresAt > now;
        var refreshValid = token.RefreshTokenExpiresAt > now;
        if (!accessValid && !refreshValid) return "Alibaba 店铺授权已过期，发布和同步已停止，请重新授权。";

        // The session ends when the later of the two expires, since the access token can be renewed until the refresh token lapses.
        var sessionEnds = refreshValid && token.RefreshTokenExpiresAt > token.AccessTokenExpiresAt ? token.RefreshTokenExpiresAt : token.AccessTokenExpiresAt;
        var left = sessionEnds - now;
        return left < TimeSpan.FromDays(7)
            ? $"Alibaba 店铺授权将在 {Math.Max(1, (int)Math.Ceiling(left.TotalDays))} 天内到期（{sessionEnds.ToLocalTime():yyyy-MM-dd HH:mm}），请尽快重新授权。"
            : null;
    }

    /// <summary>Accepts either a bare code or the whole callback URL the seller was redirected to.</summary>
    public static string ExtractCode(string? input)
    {
        var value = input?.Trim() ?? "";
        var queryStart = value.IndexOf('?');
        if (queryStart < 0 && !value.Contains("code=", StringComparison.Ordinal))
        {
            return value;
        }

        var query = queryStart >= 0 ? value[(queryStart + 1)..] : value;
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == "code")
            {
                return Uri.UnescapeDataString(pair[1].Split('#')[0]);
            }
        }

        return "";
    }

    public static void MapCatalogEndpoints(this RouteGroupBuilder api)
    {
        var products = api.MapGroup("/catalog/products");

        products.MapGet("/", async (ProductRepository repo) => Results.Ok((await repo.GetAllAsync()).OrderByDescending(x => x.UpdatedAt)));

        products.MapGet("/{id:guid}", async (Guid id, ProductRepository repo) =>
            await repo.GetAsync(id) is { } product ? Results.Ok(product) : Results.NotFound());

        products.MapPost("/", async (ProductDraft draft, ProductRepository repo, ProductQualityService quality) =>
        {
            var product = new ProductRecord();
            product.ApplyDraft(draft);
            if (product.Sku.Length == 0) return Problem("SKU 不能为空");

            await quality.ApplyAsync(product);
            try
            {
                var created = await repo.CreateAsync(product);
                return Results.Created($"/api/catalog/products/{created.Id}", created);
            }
            catch (SkuConflictException ex)
            {
                return Problem("SKU 重复", ex.Message, StatusCodes.Status409Conflict);
            }
        });

        products.MapPut("/{id:guid}", async (Guid id, ProductDraft draft, ProductRepository repo, ProductQualityService quality) =>
        {
            if (string.IsNullOrWhiteSpace(draft.Sku)) return Problem("SKU 不能为空");

            var preview = await repo.GetAsync(id);
            if (preview is null) return Results.NotFound();
            preview.ApplyDraft(draft);
            await quality.ApplyAsync(preview);
            try
            {
                var updated = await repo.UpdateAsync(id, p =>
                {
                    p.ApplyDraft(draft);
                    p.ContentUpdatedAt = DateTimeOffset.UtcNow;
                    p.QualityIssues = preview.QualityIssues;
                    p.LocalState = preview.LocalState;
                });
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (SkuConflictException ex)
            {
                return Problem("SKU 重复", ex.Message, StatusCodes.Status409Conflict);
            }
        });

        products.MapDelete("/{id:guid}", async (Guid id, ProductRepository repo) =>
            await repo.DeleteManyAsync([id]) > 0 ? Results.NoContent() : Results.NotFound());

        products.MapPost("/batch-delete", async (IdsRequest request, ProductRepository repo) =>
            Results.Ok(new { Deleted = await repo.DeleteManyAsync(request.Ids) }));

        products.MapGet("/{id:guid}/listing-preview", async (Guid id, ProductRepository repo) =>
        {
            var product = await repo.GetAsync(id);
            if (product is null) return Results.NotFound();
            try
            {
                var payload = string.IsNullOrWhiteSpace(product.RemoteProductId)
                    ? ListingMapper.BuildCreatePayload(product)
                    : ListingMapper.BuildUpdatePayload(product);
                return Results.Ok(new { Api = string.IsNullOrWhiteSpace(product.RemoteProductId) ? "product.create" : "product.update", Payload = payload });
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return Problem("无法生成发布报文", ex.Message);
            }
        });

        var catalog = api.MapGroup("/catalog");

        catalog.MapGet("/import/template.xlsx", () =>
            Results.File(ProductImportService.BuildTemplate(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "soford-商品导入模板.xlsx"));

        catalog.MapPost("/import", async ([FromForm] IFormFile file, ProductImportService importer, ProductRepository repo, ProductQualityService quality) =>
        {
            if (file.Length == 0) return Problem("上传的文件为空");

            ImportParseResult parsed;
            try
            {
                await using var stream = file.OpenReadStream();
                parsed = importer.Parse(stream, file.FileName);
            }
            catch (ImportFormatException ex)
            {
                return Problem("导入失败", ex.Message);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or OverflowException or InvalidOperationException)
            {
                return Problem("导入失败", $"无法读取文件：{ex.Message}");
            }

            var lookup = await quality.LoadCategoryLookupAsync();
            var (created, updated) = await repo.ImportAsync(parsed.Rows, p => ProductQualityService.Apply(p, lookup(p.CategoryId)));
            return Results.Ok(new ImportResult(created, updated, parsed.Skipped, parsed.Warnings, parsed.Headers, parsed.UnknownHeaders));
        }).DisableAntiforgery();

        catalog.MapPost("/quality-check", async (IdsRequest request, ProductRepository repo, ProductQualityService quality) =>
        {
            var lookup = await quality.LoadCategoryLookupAsync();
            var checkedProducts = await repo.UpdateManyAsync(request.Ids, p => ProductQualityService.Apply(p, lookup(p.CategoryId)));
            return Results.Ok(new
            {
                Total = checkedProducts.Count,
                Ready = checkedProducts.Count(x => x.LocalState == LocalState.Ready),
                Products = checkedProducts
            });
        });

        catalog.MapPost("/pull-from-alibaba", async (AlibabaCatalogSync sync, CancellationToken cancellationToken) =>
        {
            var (result, error) = await sync.PullAsync(cancellationToken);
            return result is not null ? Results.Ok(result) : Problem("从 Alibaba 导入失败", AlibabaErrors.Explain(error!));
        });

        catalog.MapPost("/predict-category", (IdsRequest request, ProductOperations operations) =>
            RunEachAsync(request.Ids, operations.PredictCategoryAsync));

        catalog.MapPost("/publish", async (IdsRequest request, IConfiguration config, AlibabaTokenService tokens, PublishJobStore jobs, PublishQueue queue, ProductRepository repo, TimeProvider time) =>
        {
            var ids = request.Ids;
            if (ids.Length == 0) return Problem("请先选择商品");

            var settings = AlibabaSettings.FromConfiguration(config);
            if (!settings.HasCredentials) return Problem("Alibaba 未配置", "未配置 AppKey / AppSecret，无法发布。可先导出 CSV。");
            var (token, error) = await tokens.GetValidAccessTokenAsync();
            if (token is null) return Problem("Alibaba 未授权", error);

            var existing = (await repo.GetManyAsync(ids)).Select(x => x.Id).ToArray();
            if (existing.Length == 0) return Problem("选中的商品不存在");

            var job = new PublishJobRecord
            {
                Id = Guid.NewGuid(),
                ProductIds = existing,
                Total = existing.Length,
                Status = PublishJobStatus.Queued,
                Message = "排队中",
                CreatedAt = time.GetUtcNow()
            };
            await jobs.SaveAsync(job);
            await queue.EnqueueAsync(job.Id);
            return Results.Accepted($"/api/catalog/publish-jobs/{job.Id}", job);
        });

        catalog.MapGet("/publish-jobs", async (PublishJobStore jobs, int take = 50) => Results.Ok(await jobs.GetLatestAsync(Math.Clamp(take, 1, 200))));

        catalog.MapGet("/publish-jobs/{id:guid}", async (Guid id, PublishJobStore jobs) =>
            await jobs.GetAsync(id) is { } job ? Results.Ok(job) : Results.NotFound());

        catalog.MapPost("/sync/status", (IdsRequest request, ProductOperations operations) => RunEachAsync(request.Ids, operations.RefreshStatusAsync));
        catalog.MapPost("/sync/inventory", (IdsRequest request, ProductOperations operations) => RunEachAsync(request.Ids, operations.SyncInventoryAsync));
        catalog.MapPost("/sync/price", (IdsRequest request, ProductOperations operations) => RunEachAsync(request.Ids, operations.SyncPriceAsync));
        catalog.MapPost("/online", async (IdsRequest request, ProductOperations operations) =>
            Results.Ok(BatchResult.From(await operations.SetOnlineAsync(request.Ids, true))));
        catalog.MapPost("/offline", async (IdsRequest request, ProductOperations operations) =>
            Results.Ok(BatchResult.From(await operations.SetOnlineAsync(request.Ids, false))));

        catalog.MapGet("/export/products.csv", async (ProductRepository repo, ExportService export) =>
            CsvFile(export, await repo.GetAllAsync()));

        // Selections are posted rather than put in the query string, which nginx caps at 8 KB (~220 ids).
        catalog.MapPost("/export/products.csv", async (IdsRequest request, ProductRepository repo, ExportService export) =>
            CsvFile(export, request.Ids.Length == 0 ? await repo.GetAllAsync() : await repo.GetManyAsync(request.Ids)));
    }

    private static IResult CsvFile(ExportService export, IEnumerable<ProductRecord> products) =>
        Results.File(export.BuildCsv(products), "text/csv; charset=utf-8", $"soford-products-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv");

    private static async Task<IResult> RunEachAsync(Guid[] ids, Func<Guid, Task<OperationItemResult>> operation)
    {
        var items = new List<OperationItemResult>();
        foreach (var id in ids.Distinct())
        {
            items.Add(await operation(id));
        }

        return Results.Ok(BatchResult.From(items));
    }
}
