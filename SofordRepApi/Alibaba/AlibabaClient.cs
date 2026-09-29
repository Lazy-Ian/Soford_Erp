using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

/// <summary>Business-level Alibaba caller: resolves the API from the registry and attaches a valid seller token.</summary>
public sealed class AlibabaClient(AlibabaTransport transport, AlibabaTokenService tokens, IConfiguration config)
{
    public async Task<AlibabaApiResult> CallAsync(string apiKey, object? payload, AlibabaFile? file = null, CancellationToken cancellationToken = default)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        if (!settings.Apis.TryGetValue(apiKey, out var api))
        {
            return AlibabaApiResult.Failure(apiKey, "", "UnknownApi", $"接口 {apiKey} 未登记。");
        }

        if (!api.IsConfigured)
        {
            return AlibabaApiResult.Failure(apiKey, "", "ApiNotConfigured", $"接口 {apiKey} 未配置路径（Alibaba:Apis:{apiKey}:Path）。");
        }

        if (!settings.HasCredentials)
        {
            return AlibabaApiResult.Failure(apiKey, api.Path, "NotConfigured", "未配置 Alibaba AppKey / AppSecret。");
        }

        Dictionary<string, string> parameters;
        try
        {
            parameters = IopParameters.From(payload);
        }
        catch (ArgumentException ex)
        {
            return AlibabaApiResult.Failure(apiKey, api.Path, "InvalidPayload", ex.Message);
        }

        string? accessToken = null;
        if (api.RequiresToken)
        {
            var (token, error) = await tokens.GetValidAccessTokenAsync();
            if (token is null)
            {
                return AlibabaApiResult.Failure(apiKey, api.Path, "NotAuthorized", error ?? "未授权。");
            }

            accessToken = token;
        }

        return await transport.SendAsync(settings, api.Key, api.Path, parameters, accessToken, file, cancellationToken);
    }
}

public sealed record AlibabaOAuthState(string State, string Username, DateTimeOffset CreatedAt);

public static class AlibabaOAuth
{
    private const string StateCookieName = "SofordErp.AlibabaOAuthState";
    private const string ProtectorPurpose = "SofordErp.AlibabaOAuthState.v1";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    public static string StartState(HttpContext http, IDataProtectionProvider dataProtection, ClaimsPrincipal user)
    {
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new AlibabaOAuthState(state, user.Identity?.Name ?? "", DateTimeOffset.UtcNow);
        var protectedState = dataProtection.CreateProtector(ProtectorPurpose).Protect(JsonSerializer.Serialize(payload));

        http.Response.Cookies.Append(StateCookieName, protectedState, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            IsEssential = true,
            MaxAge = StateLifetime
        });

        return state;
    }

    public static async Task<IResult> CompleteCallbackAsync(
        string? code,
        string? state,
        HttpContext http,
        AlibabaTokenService tokens,
        IConfiguration config,
        IDataProtectionProvider dataProtection,
        ILogger logger)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        if (http.User.Identity?.IsAuthenticated != true)
        {
            return Results.Redirect(BuildFrontendRedirect(settings, "unauthorized", "erp-login-required"));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Results.Redirect(BuildFrontendRedirect(settings, "failed", "missing-code"));
        }

        if (!ValidateState(http, dataProtection, state))
        {
            return Results.Redirect(BuildFrontendRedirect(settings, "failed", "invalid-state"));
        }

        try
        {
            await tokens.CreateFromCodeAsync(code);
            http.Response.Cookies.Delete(StateCookieName);
            return Results.Redirect(BuildFrontendRedirect(settings, "success", null));
        }
        catch (Exception ex) when (ex is AlibabaApiException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Alibaba OAuth token exchange failed");
            return Results.Redirect(BuildFrontendRedirect(settings, "failed", ex.Message));
        }
    }

    public static string BuildCallbackUrl(AlibabaSettings settings, HttpContext http) =>
        settings.OAuthCallbackUrl ?? $"{http.Request.Scheme}://{http.Request.Host.Value}/openapi/callback";

    public static string BuildAuthorizeUrl(AlibabaSettings settings, string callbackUrl, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = settings.AppKey ?? "",
            ["redirect_uri"] = callbackUrl,
            ["state"] = state,
            ["view"] = "web",
            ["sp"] = settings.OAuthSp
        };
        return $"{settings.OAuthAuthorizeUrl}?{string.Join("&", parameters.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"))}";
    }

    private static bool ValidateState(HttpContext http, IDataProtectionProvider dataProtection, string? state)
    {
        if (string.IsNullOrWhiteSpace(state)
            || !http.Request.Cookies.TryGetValue(StateCookieName, out var protectedState)
            || string.IsNullOrWhiteSpace(protectedState))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<AlibabaOAuthState>(dataProtection.CreateProtector(ProtectorPurpose).Unprotect(protectedState));
            return payload is not null
                && LocalAdminAuth.FixedTimeEquals(payload.State, state)
                && string.Equals(payload.Username, http.User.Identity?.Name ?? "", StringComparison.Ordinal)
                && DateTimeOffset.UtcNow - payload.CreatedAt <= StateLifetime;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private static string BuildFrontendRedirect(AlibabaSettings settings, string result, string? reason)
    {
        var baseUrl = settings.OAuthSuccessRedirect.Split('?', 2)[0];
        var query = $"alibabaAuth={Uri.EscapeDataString(result)}";
        if (!string.IsNullOrWhiteSpace(reason))
        {
            query += $"&reason={Uri.EscapeDataString(AlibabaResponseParser.Truncate(reason, 200))}";
        }

        return $"{baseUrl}?{query}";
    }
}
