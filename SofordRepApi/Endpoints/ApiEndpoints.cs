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
public sealed record AlibabaCallRequest(string ApiKey, JsonElement? Payload, Guid? AccountId = null);
public sealed record AccountUpdateRequest(string? Name, bool? IsDefault, string[]? OwnerAliIds);
public sealed record PublishRequest(Guid[]? ProductIds, Guid? AccountId)
{
    public Guid[] Ids => ProductIds?.Distinct().ToArray() ?? [];
}
public sealed record AssignAccountRequest(Guid[]? ProductIds, Guid? AccountId)
{
    public Guid[] Ids => ProductIds?.Distinct().ToArray() ?? [];
}
public sealed record UserRequest(string? Username, string? DisplayName, string? Password, string? Role, Guid[]? AccountIds, bool? Disabled);
public sealed record PasswordChangeRequest(string? Current, string? Next);

public static class ApiEndpoints
{
    private const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Store-wide settings, accounts, users and raw API access.</summary>
    public const string AdminPolicy = "admin";


    public static IResult Problem(string title, string? detail = null, int status = StatusCodes.Status400BadRequest) =>
        Results.Problem(title: title, detail: detail, statusCode: status);

    public static void MapAuthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow })).AllowAnonymous();

        api.MapGet("/auth/session", async (ClaimsPrincipal user, AccessService access) =>
            await access.CurrentAsync(user) is { } current
                ? Results.Ok(new AuthSessionResponse(true, current.Name, current.IsAdmin ? Roles.Admin : Roles.Operator, current.Id != CurrentUser.BuiltInAdminId))
                : Results.Ok(new AuthSessionResponse(false, null))).AllowAnonymous();

        api.MapPost("/auth/login", async (LoginRequest request, HttpContext http, IConfiguration config, IHostEnvironment env, LoginThrottle throttle, UserStore users, AuditLog audit) =>
        {
            var client = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (throttle.LockedFor(client) is { } wait)
            {
                return Problem("登录失败次数过多", $"请 {Math.Ceiling(wait.TotalMinutes)} 分钟后再试。", StatusCodes.Status429TooManyRequests);
            }

            // The administrator from the environment always works (first login, locked-out users); everyone else is in users.json.
            var username = request.Username?.Trim() ?? "";
            Claim[] claims;
            string displayName;
            string role;
            if (LocalAdminAuth.Verify(config, env, username, request.Password))
            {
                displayName = LocalAdminAuth.Username(config, env);
                role = Roles.Admin;
                claims = [new Claim(UserClaims.UserId, CurrentUser.BuiltInAdminId)];
            }
            else if (await users.VerifyAsync(username, request.Password ?? "") is { } user)
            {
                displayName = user.DisplayName;
                role = user.Role;
                claims = [new Claim(UserClaims.UserId, user.Id.ToString()), new Claim(UserClaims.Stamp, user.SecurityStamp)];
            }
            else
            {
                throttle.RecordFailure(client);
                return Problem("用户名或密码错误", status: StatusCodes.Status401Unauthorized);
            }

            throttle.Reset(client);
            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, displayName), new Claim(ClaimTypes.Role, role), .. claims],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IsPersistent = request.Remember,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(request.Remember ? 24 : 10)
            });
            await audit.RecordAsync(new CurrentUser(claims[0].Value, displayName, role == Roles.Admin, new HashSet<Guid>()), "登录", $"从 {client} 登录");
            return Results.Ok(new AuthSessionResponse(true, displayName, role, claims[0].Value != CurrentUser.BuiltInAdminId));
        }).AllowAnonymous();

        api.MapPost("/auth/password", async (PasswordChangeRequest request, ClaimsPrincipal principal, UserStore users, AuditLog audit, AccessService access) =>
        {
            if (!Guid.TryParse(UserClaims.Id(principal), out var id) || await users.FindAsync(id) is not { } user)
            {
                return Problem("该账号的密码在服务器配置文件中设置", "内置管理员请修改 Auth__AdminPassword 后重启服务。");
            }

            if (!PasswordHasher.Verify(request.Current ?? "", user.PasswordHash)) return Problem("当前密码不正确");
            if (PasswordHasher.Weakness(request.Next) is { } weak) return Problem("新密码太短", weak);
            await users.UpdateAsync(id, u =>
            {
                u.PasswordHash = PasswordHasher.Hash(request.Next!);
                u.SecurityStamp = Guid.NewGuid().ToString("N");
            });
            await audit.RecordAsync(await access.CurrentAsync(principal), "修改密码", "修改了自己的密码");
            return Results.Ok(new { Changed = true });
        });

        api.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new AuthSessionResponse(false, null));
        });

        api.MapGet("/system/diagnostics", async (SystemDiagnostics diagnostics, HttpContext http) => Results.Ok(await diagnostics.RunAsync(http)));

        api.MapGet("/system/automation", (AutomationState state) => Results.Ok(state.Current));

        api.MapPost("/system/automation/run", async (RemoteSyncWorker worker) => Results.Ok(await worker.RunOnceAsync())).RequireAuthorization(AdminPolicy);

        api.MapGet("/audit", async (ClaimsPrincipal principal, AccessService access, AuditLog audit, int take = 200) =>
        {
            var user = await access.CurrentAsync(principal);
            // Operators see their own history; admins see everyone's.
            return Results.Ok(await audit.LatestAsync(Math.Clamp(take, 1, 1000), user is { IsAdmin: true } ? null : user?.Id ?? "-"));
        });
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

        var admin = alibaba.MapGroup("").RequireAuthorization(AdminPolicy);

        admin.MapGet("/oauth/url", (HttpContext http, IConfiguration config, IDataProtectionProvider dataProtection) =>
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

        admin.MapPost("/token/create", async (AlibabaCodeRequest request, AlibabaTokenService tokens) =>
        {
            var code = ExtractCode(request.Code);
            if (string.IsNullOrWhiteSpace(code))
            {
                return Problem("请粘贴授权回调链接或其中的 code");
            }

            try
            {
                var (account, token) = await tokens.CreateFromCodeAsync(code);
                return Results.Ok(new { HasToken = true, token.AccessTokenExpiresAt, token.RefreshTokenExpiresAt, token.Account, AccountId = account.Id, AccountName = account.Name });
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

        admin.MapPost("/token/refresh", async (Guid? accountId, AlibabaTokenService tokens) =>
        {
            try
            {
                var token = await tokens.RefreshAsync(accountId);
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

        admin.MapDelete("/token", async (Guid? accountId, AlibabaTokenService tokens, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var target = accountId ?? AlibabaTokenService.DefaultAccount(await tokens.GetAccountsAsync())?.Id;
            if (target is not null) await tokens.ClearTokenAsync(target.Value);
            await audit.RecordAsync(await access.CurrentAsync(principal), "断开授权", $"断开账号 {target} 的授权");
            return Results.NoContent();
        });

        alibaba.MapGet("/accounts", async (AlibabaTokenService tokens, ProductRepository products) =>
        {
            var accounts = await tokens.GetAccountsAsync();
            var all = await products.GetAllAsync();
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(accounts.OrderByDescending(x => x.IsDefault).ThenByDescending(x => x.Token is not null).ThenBy(x => x.Name).Select(account => new
            {
                account.Id,
                account.Name,
                account.OwnerAliIds,
                account.IsDefault,
                Authorized = account.Token is not null && (account.Token.AccessTokenExpiresAt > now || account.Token.RefreshTokenExpiresAt > now),
                HasToken = account.Token is not null,
                Login = account.Token?.Account,
                account.Token?.AccessTokenExpiresAt,
                account.Token?.RefreshTokenExpiresAt,
                ProductCount = all.Count(p => account.Owns(p.OwnerAliId)),
                AssignedCount = all.Count(p => p.AccountId == account.Id)
            }));
        });

        admin.MapPut("/accounts/{id:guid}", async (Guid id, AccountUpdateRequest request, AlibabaTokenService tokens, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (await tokens.UpdateAccountAsync(id, request.Name, request.IsDefault, request.OwnerAliIds) is not { } account) return Results.NotFound();
            await audit.RecordAsync(await access.CurrentAsync(principal), "修改账号",
                request.Name is not null ? $"账号改名为「{account.Name}」" : request.IsDefault == true ? $"「{account.Name}」设为默认账号" : $"「{account.Name}」关联账号 ID {string.Join("、", account.OwnerAliIds)}");
            return Results.Ok(new { account.Id, account.Name, account.IsDefault, account.OwnerAliIds });
        });

        admin.MapGet("/apis", (IConfiguration config) =>
            Results.Ok(AlibabaSettings.FromConfiguration(config).Apis.Values.OrderBy(x => x.Area).ThenBy(x => x.Key)));

        admin.MapGet("/logs", async (AlibabaApiLogStore logs, int take = 100) => Results.Ok(await logs.GetLatestAsync(Math.Clamp(take, 1, 500))));

        // The workbench is for looking things up. Outside Development it cannot change listings or mint tokens:
        // those go through the product pages, which check quality and record the outcome on the product.
        admin.MapPost("/call", async (AlibabaCallRequest request, AlibabaClient client, IHostEnvironment env) =>
            !env.IsDevelopment() && !AlibabaSettings.ReadOnlyApis.Contains(request.ApiKey)
                ? Problem("该接口不能在调试台调用", $"{request.ApiKey} 会修改店铺数据或生成授权，只能在开发环境调试；请使用商品页面的对应功能。", StatusCodes.Status403Forbidden)
                : Results.Ok(await client.CallAsync(request.ApiKey, request.Payload is { ValueKind: JsonValueKind.Object } payload ? payload : null, accountId: request.AccountId)));

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

        // Imported Alibaba descriptions are rich HTML (~20 KB each, most of the file); the list never shows them,
        // so it leaves them out and the editor loads the full product by id.
        products.MapGet("/", async (ProductRepository repo, ClaimsPrincipal principal, AccessService access) =>
        {
            var visible = await access.VisibleAsync(principal);
            return Results.Ok((await repo.GetAllAsync()).Where(visible).OrderByDescending(x => x.UpdatedAt).Select(WithoutDescription));
        });

        products.MapGet("/{id:guid}", async (Guid id, ProductRepository repo, ClaimsPrincipal principal, AccessService access) =>
            await repo.GetAsync(id) is { } product && (await access.VisibleAsync(principal))(product) ? Results.Ok(product) : Results.NotFound());

        products.MapPost("/", async (ProductDraft draft, ProductRepository repo, ProductQualityService quality, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var user = await access.CurrentAsync(principal);
            var product = new ProductRecord { CreatedBy = user?.Id, UpdatedBy = user?.Id };
            product.ApplyDraft(draft);
            if (product.Sku.Length == 0) return Problem("SKU 不能为空");

            await quality.ApplyAsync(product);
            try
            {
                var created = await repo.CreateAsync(product);
                await audit.RecordAsync(user, "新建商品", created.Sku, 1);
                return Results.Created($"/api/catalog/products/{created.Id}", created);
            }
            catch (SkuConflictException ex)
            {
                return Problem("SKU 重复", ex.Message, StatusCodes.Status409Conflict);
            }
        });

        products.MapPut("/{id:guid}", async (Guid id, ProductDraft draft, ProductRepository repo, ProductQualityService quality, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(draft.Sku)) return Problem("SKU 不能为空");

            var preview = await repo.GetAsync(id);
            if (preview is null || !(await access.VisibleAsync(principal))(preview)) return Results.NotFound();
            var user = await access.CurrentAsync(principal);
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
                    p.UpdatedBy = user?.Id;
                });
                if (updated is not null) await audit.RecordAsync(user, "编辑商品", updated.Sku, 1);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (SkuConflictException ex)
            {
                return Problem("SKU 重复", ex.Message, StatusCodes.Status409Conflict);
            }
        });

        products.MapDelete("/{id:guid}", async (Guid id, ProductRepository repo, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var (allowed, denied) = await AllowedAsync([id], principal, access, repo);
            if (denied is not null) return denied;
            if (allowed.Length == 0 || await repo.DeleteManyAsync(allowed) == 0) return Results.NotFound();
            await audit.RecordAsync(await access.CurrentAsync(principal), "删除商品", "删除 1 个商品（仅本系统）", 1);
            return Results.NoContent();
        });

        products.MapPost("/batch-delete", async (IdsRequest request, ProductRepository repo, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var (allowed, denied) = await AllowedAsync(request.Ids, principal, access, repo);
            if (denied is not null) return denied;
            var deleted = await repo.DeleteManyAsync(allowed);
            await audit.RecordAsync(await access.CurrentAsync(principal), "删除商品", $"删除 {deleted} 个商品（仅本系统）", deleted);
            return Results.Ok(new { Deleted = deleted });
        });

        products.MapGet("/{id:guid}/listing-preview", async (Guid id, ProductRepository repo, ClaimsPrincipal principal, AccessService access) =>
        {
            var product = await repo.GetAsync(id);
            if (product is null || !(await access.VisibleAsync(principal))(product)) return Results.NotFound();
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

        // The overview page: what needs attention today, without downloading the whole catalog.
        catalog.MapGet("/summary", async (ProductRepository repo, AlibabaTokenService tokens, TimeProvider time, ClaimsPrincipal principal, AccessService access) =>
        {
            var visible = await access.VisibleAsync(principal);
            var user = await access.CurrentAsync(principal);
            var all = (await repo.GetAllAsync()).Where(visible).ToList();
            var accounts = (await tokens.GetAccountsAsync()).Where(x => user is { IsAdmin: true } || user?.AccountIds.Contains(x.Id) == true).ToList();
            var now = time.GetUtcNow();
            static bool Blocked(ProductRecord p) => p.QualityIssues.Any(x => x.Severity == QualitySeverity.Blocker);
            static object Item(ProductRecord p) => new { p.Id, p.Sku, p.Title, p.RemoteProductId, p.RemoteStatusMessage, Image = p.Images.FirstOrDefault(), p.OwnerAliId };

            var failed = all.Where(x => x.PublishState == PublishState.Failed).OrderByDescending(x => x.LastSyncedAt ?? x.UpdatedAt).ToList();
            var missing = all.Where(x => x.RemoteStatus == ProductRepository.RemoteMissingStatus).ToList();
            return Results.Ok(new
            {
                Total = all.Count,
                Online = all.Count(x => x.PublishState == PublishState.Online),
                Offline = all.Count(x => x.PublishState == PublishState.Offline && x.RemoteStatus != ProductRepository.RemoteMissingStatus),
                Pending = all.Count(x => x.PublishState is PublishState.Pending or PublishState.Publishing),
                Failed = failed.Count,
                NeedsContent = all.Count(x => !string.IsNullOrWhiteSpace(x.RemoteProductId) && Blocked(x)),
                UnpublishedChanges = all.Count(x => x.HasUnpublishedChanges),
                Drafts = all.Count(x => string.IsNullOrWhiteSpace(x.RemoteProductId)),
                Missing = missing.Count,
                FewKeywords = all.Count(x => !string.IsNullOrWhiteSpace(x.RemoteProductId) && x.Keywords.Length < 3),
                FailedItems = failed.Take(8).Select(Item),
                MissingItems = missing.Take(8).Select(Item),
                ExpiringAccounts = accounts
                    .Where(x => x.Token is not null && (x.Token.RefreshTokenExpiresAt > x.Token.AccessTokenExpiresAt ? x.Token.RefreshTokenExpiresAt : x.Token.AccessTokenExpiresAt) < now.AddDays(14))
                    .Select(x => new { x.Id, x.Name, ExpiresAt = x.Token!.RefreshTokenExpiresAt > x.Token.AccessTokenExpiresAt ? x.Token.RefreshTokenExpiresAt : x.Token.AccessTokenExpiresAt }),
                Accounts = accounts.Select(account =>
                {
                    var owned = all.Where(p => account.Owns(p.OwnerAliId)).ToList();
                    return new
                    {
                        account.Id,
                        account.Name,
                        Authorized = account.Token is not null,
                        Total = owned.Count,
                        Online = owned.Count(p => p.PublishState == PublishState.Online),
                        Offline = owned.Count(p => p.PublishState == PublishState.Offline),
                        Pending = owned.Count(p => p.PublishState is PublishState.Pending or PublishState.Publishing),
                        Failed = owned.Count(p => p.PublishState == PublishState.Failed)
                    };
                }).Where(x => x.Total > 0 || x.Authorized).OrderByDescending(x => x.Total)
            });
        });

        catalog.MapGet("/import/template.xlsx", () =>
            Results.File(ProductImportService.BuildTemplate(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "soford-商品导入模板.xlsx"));

        catalog.MapPost("/import", async ([FromForm] IFormFile file, ProductImportService importer, ProductRepository repo, ProductQualityService quality, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
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
            var unmatched = new List<string>();
            var user = await access.CurrentAsync(principal);
            var (created, updated) = await repo.ImportAsync(parsed.Rows, p => ProductQualityService.Apply(p, lookup(p.CategoryId)), unmatched,
                await access.VisibleAsync(principal), user?.Id);
            await audit.RecordAsync(user, "导入表格", $"{file.FileName}：新增 {created}，更新 {updated}", created + updated);
            return Results.Ok(new ImportResult(created, updated, parsed.Skipped + unmatched.Count, [.. unmatched, .. parsed.Warnings], parsed.Headers, parsed.UnknownHeaders));
        }).DisableAntiforgery();

        catalog.MapPost("/quality-check", async (IdsRequest request, ProductRepository repo, ProductQualityService quality, ClaimsPrincipal principal, AccessService access) =>
        {
            var (allowed, denied) = await AllowedAsync(request.Ids, principal, access, repo);
            if (denied is not null) return denied;
            var lookup = await quality.LoadCategoryLookupAsync();
            var checkedProducts = await repo.UpdateManyAsync(allowed, p => ProductQualityService.Apply(p, lookup(p.CategoryId)));
            return Results.Ok(new
            {
                Total = checkedProducts.Count,
                Ready = checkedProducts.Count(x => x.LocalState == LocalState.Ready)
            });
        });

        catalog.MapPost("/pull-from-alibaba", async (AlibabaCatalogSync sync, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            // Not tied to the request: reading 60+ pages takes over a minute, and a closed tab or proxy timeout must not
            // throw away pages already read (they are only saved at the end).
            var (result, error) = await sync.PullAsync(CancellationToken.None);
            if (result is not null) await audit.RecordAsync(await access.CurrentAsync(principal), "从 Alibaba 导入", $"读取 {result.Total} 个，新增 {result.Created}", result.Total);
            return result is not null ? Results.Ok(result) : Problem("从 Alibaba 导入失败", AlibabaErrors.Explain(error!));
        }).RequireAuthorization(AdminPolicy);


        catalog.MapPost("/assign-account", async (AssignAccountRequest request, AlibabaTokenService tokens, ProductRepository repo, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (request.AccountId is { } accountId && (await tokens.GetAccountsAsync()).All(x => x.Id != accountId))
            {
                return Problem("账号不存在");
            }

            var user = await access.CurrentAsync(principal);
            if (request.AccountId is { } target && user is { IsAdmin: false } && !user.AccountIds.Contains(target))
            {
                return Problem("只能指定你负责的账号", status: StatusCodes.Status403Forbidden);
            }

            var (allowed, denied) = await AllowedAsync(request.Ids, principal, access, repo);
            if (denied is not null) return denied;
            var changed = await repo.UpdateManyAsync(allowed, p => p.AccountId = request.AccountId);
            await audit.RecordAsync(user, "指定操作账号", request.AccountId is null ? "恢复为自动选择" : $"指定账号 {request.AccountId}", changed.Count);
            return Results.Ok(new { Updated = changed.Count });
        });

        catalog.MapPost("/publish", async (PublishRequest request, IConfiguration config, AlibabaTokenService tokens, PublishJobStore jobs, PublishQueue queue, ProductRepository repo, TimeProvider time,
            ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var (ids, denied) = await AllowedAsync(request.Ids, principal, access, repo);
            if (denied is not null) return denied;
            if (ids.Length == 0) return Problem("请先选择商品");
            var user = await access.CurrentAsync(principal);
            if (request.AccountId is { } picked && user is { IsAdmin: false } && !user.AccountIds.Contains(picked))
            {
                return Problem("只能用你负责的账号发布", status: StatusCodes.Status403Forbidden);
            }

            var settings = AlibabaSettings.FromConfiguration(config);
            if (!settings.HasCredentials) return Problem("Alibaba 未配置", "未配置 AppKey / AppSecret，无法发布。可先导出 CSV。");
            var (token, error) = await tokens.GetValidAccessTokenAsync(request.AccountId);
            if (token is null) return Problem("Alibaba 未授权", error);

            var selected = await repo.GetManyAsync(ids);
            if (selected.Count == 0) return Problem("选中的商品不存在");

            if (request.AccountId is { } chosen)
            {
                // The chosen account creates new listings and becomes their owner. Listings that already exist stay with
                // their owner: a sub-account's token cannot update another account's listing.
                await repo.UpdateManyAsync(selected.Where(x => string.IsNullOrWhiteSpace(x.RemoteProductId)).Select(x => x.Id), p => p.AccountId = chosen);
            }

            return await QueueJobAsync(JobKinds.Publish, selected.Select(x => x.Id).ToArray(), jobs, queue, time, user, audit);
        });

        // Batch Alibaba operations on hundreds of products take longer than a proxy will wait, so they run as background jobs too.
        foreach (var kind in new[] { JobKinds.Status, JobKinds.Inventory, JobKinds.Price, JobKinds.Online, JobKinds.Offline, JobKinds.Predict, JobKinds.Refresh, JobKinds.RefreshDiscard })
        {
            catalog.MapPost($"/jobs/{kind}", async (IdsRequest request, IConfiguration config, PublishJobStore jobs, PublishQueue queue, ProductRepository repo, TimeProvider time,
                ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
            {
                if (request.Ids.Length == 0) return Problem("请先选择商品");
                if (!AlibabaSettings.FromConfiguration(config).HasCredentials) return Problem("Alibaba 未配置", "未配置 AppKey / AppSecret。");
                var (existing, denied) = await AllowedAsync(request.Ids, principal, access, repo);
                if (denied is not null) return denied;
                return existing.Length == 0
                    ? Problem("选中的商品不存在")
                    : await QueueJobAsync(kind, existing, jobs, queue, time, await access.CurrentAsync(principal), audit);
            });
        }

        catalog.MapGet("/publish-jobs", async (PublishJobStore jobs, ClaimsPrincipal principal, AccessService access, int take = 50) =>
        {
            var user = await access.CurrentAsync(principal);
            var latest = await jobs.GetLatestAsync(200);
            return Results.Ok(latest.Where(x => user is { IsAdmin: true } || x.CreatedBy == user?.Id).Take(Math.Clamp(take, 1, 200)));
        });

        catalog.MapGet("/publish-jobs/{id:guid}", async (Guid id, PublishJobStore jobs, ClaimsPrincipal principal, AccessService access) =>
        {
            var user = await access.CurrentAsync(principal);
            return await jobs.GetAsync(id) is { } job && (user is { IsAdmin: true } || job.CreatedBy == user?.Id) ? Results.Ok(job) : Results.NotFound();
        });


        catalog.MapGet("/export/products.csv", async (ProductRepository repo, ExportService export, ClaimsPrincipal principal, AccessService access) =>
            CsvFile(export, (await repo.GetAllAsync()).Where(await access.VisibleAsync(principal))));

        // Selections are posted rather than put in the query string, which nginx caps at 8 KB (~220 ids).
        catalog.MapPost("/export/products.csv", async (IdsRequest request, ProductRepository repo, ExportService export, ClaimsPrincipal principal, AccessService access) =>
        {
            var visible = await access.VisibleAsync(principal);
            return CsvFile(export, (request.Ids.Length == 0 ? await repo.GetAllAsync() : await repo.GetManyAsync(request.Ids)).Where(visible));
        });
    }

    private static ProductRecord WithoutDescription(ProductRecord product)
    {
        product.Description = "";
        return product;
    }

    private static IResult CsvFile(ExportService export, IEnumerable<ProductRecord> products) =>
        Results.File(export.BuildCsv(products), "text/csv; charset=utf-8", $"soford-products-{DateTimeOffset.Now:yyyyMMddHHmmss}.csv");

    /// <summary>
    /// The selected products the caller may act on. Selecting someone else's product is refused outright rather than
    /// silently dropped, so a batch never does less than the user thinks.
    /// </summary>
    private static async Task<(Guid[] Ids, IResult? Denied)> AllowedAsync(Guid[] ids, ClaimsPrincipal principal, AccessService access, ProductRepository repo)
    {
        var visible = await access.VisibleAsync(principal);
        var found = await repo.GetManyAsync(ids);
        var foreign = found.Count(x => !visible(x));
        return foreign > 0
            ? ([], Problem("包含无权操作的商品", $"有 {foreign} 个选中的商品不属于你负责的账号。", StatusCodes.Status403Forbidden))
            : (found.Select(x => x.Id).ToArray(), null);
    }

    private static async Task<IResult> QueueJobAsync(string kind, Guid[] productIds, PublishJobStore jobs, PublishQueue queue, TimeProvider time, CurrentUser? user, AuditLog audit)
    {
        var job = new PublishJobRecord
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            CreatedBy = user?.Id,
            CreatedByName = user?.Name,
            ProductIds = productIds,
            Total = productIds.Length,
            Status = PublishJobStatus.Queued,
            Message = "排队中",
            CreatedAt = time.GetUtcNow()
        };
        await jobs.SaveAsync(job);
        await queue.EnqueueAsync(job.Id);
        await audit.RecordAsync(user, JobKinds.Labels.GetValueOrDefault(kind, kind), $"提交 {productIds.Length} 个商品", productIds.Length);
        return Results.Accepted($"/api/catalog/publish-jobs/{job.Id}", job);
    }

    public static void MapUserEndpoints(this RouteGroupBuilder api)
    {
        var users = api.MapGroup("/users").RequireAuthorization(AdminPolicy);
        static object View(UserRecord u) => new { u.Id, u.Username, u.DisplayName, u.Role, u.AccountIds, u.Disabled, u.CreatedAt };

        users.MapGet("/", async (UserStore store) => Results.Ok((await store.GetAllAsync()).OrderBy(x => x.CreatedAt).Select(View)));

        users.MapPost("/", async (UserRequest request, UserStore store, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username)) return Problem("用户名不能为空");
            if (PasswordHasher.Weakness(request.Password) is { } weak) return Problem("密码太短", weak);
            try
            {
                var user = await store.CreateAsync(request.Username, request.DisplayName ?? "", request.Password!, request.Role ?? Roles.Operator, request.AccountIds ?? []);
                await audit.RecordAsync(await access.CurrentAsync(principal), "新增用户", $"{user.DisplayName}（{user.Username}，{(user.Role == Roles.Admin ? "管理员" : "运营")}）");
                return Results.Ok(View(user));
            }
            catch (UsernameTakenException ex)
            {
                return Problem("用户名已存在", ex.Message, StatusCodes.Status409Conflict);
            }
        });

        users.MapPut("/{id:guid}", async (Guid id, UserRequest request, UserStore store, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (request.Password is not null && PasswordHasher.Weakness(request.Password) is { } weak) return Problem("密码太短", weak);
            var updated = await store.UpdateAsync(id, u =>
            {
                if (!string.IsNullOrWhiteSpace(request.DisplayName)) u.DisplayName = request.DisplayName.Trim();
                if (request.Role is Roles.Admin or Roles.Operator) u.Role = request.Role;
                if (request.AccountIds is not null) u.AccountIds = request.AccountIds.Distinct().ToList();
                if (request.Disabled is { } disabled) u.Disabled = disabled;
                if (request.Password is not null)
                {
                    u.PasswordHash = PasswordHasher.Hash(request.Password);
                    u.SecurityStamp = Guid.NewGuid().ToString("N");
                }
            });
            if (updated is null) return Results.NotFound();
            await audit.RecordAsync(await access.CurrentAsync(principal), "修改用户",
                $"{updated.DisplayName}{(request.Password is not null ? "：重置密码" : "")}{(request.Disabled == true ? "：停用" : request.Disabled == false ? "：启用" : "")}");
            return Results.Ok(View(updated));
        });

        users.MapDelete("/{id:guid}", async (Guid id, UserStore store, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            var user = await store.FindAsync(id);
            if (user is null || !await store.DeleteAsync(id)) return Results.NotFound();
            await audit.RecordAsync(await access.CurrentAsync(principal), "删除用户", user.DisplayName);
            return Results.NoContent();
        });
    }
}
