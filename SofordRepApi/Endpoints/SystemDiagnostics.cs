public sealed record DiagnosticCheck(string Key, string Title, string Status, string Message, string? Fix = null);

/// <summary>Step-by-step connection self-check shown on the 「店铺连接」 page.</summary>
public sealed class SystemDiagnostics(
    IConfiguration config,
    IHostEnvironment env,
    AppPaths paths,
    AlibabaTransport transport,
    AlibabaTokenService tokens,
    AlibabaClient client,
    IHttpClientFactory httpClientFactory,
    TimeProvider time)
{
    private const string Ok = "ok";
    private const string Warn = "warn";
    private const string Fail = "fail";

    public async Task<List<DiagnosticCheck>> RunAsync(HttpContext http)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        var checks = new List<DiagnosticCheck>
        {
            settings.HasCredentials
                ? new("credentials", "AppKey / AppSecret", Ok, $"AppKey {Mask(settings.AppKey!)} 已配置。")
                : new("credentials", "AppKey / AppSecret", Fail, "未配置 AppKey 或 AppSecret。",
                    "在 .env（本地）或 /opt/soford-erp/env/soford-api.env（服务器）中设置 Alibaba__AppKey 和 Alibaba__AppSecret，然后重启 API。")
        };

        checks.Add(await CheckGatewayAsync(settings));
        if (settings.HasCredentials && checks[^1].Status == Ok)
        {
            checks.Add(await CheckSignatureAsync(settings));
        }

        checks.Add(await CheckTokenAsync());
        if (checks[^1].Status == Ok)
        {
            checks.Add(await CheckApiAccessAsync());
        }

        var callback = AlibabaOAuth.BuildCallbackUrl(settings, http);
        var isLocal = Uri.TryCreate(callback, UriKind.Absolute, out var callbackUri) && callbackUri.IsLoopback;
        checks.Add(new("callback", "授权回调地址", callback.StartsWith("https://") && !isLocal ? Ok : Warn,
            callback,
            isLocal
                ? "Alibaba 只接受 App Console 登记的回调地址，localhost 会被拒绝。请设置 Alibaba__OAuthCallbackUrl=https://erp.soford.cn/openapi/callback，授权后用「粘贴回调链接」方式完成。"
                : "该地址必须与 Alibaba App Console 中登记的回调地址完全一致；本地环境授权后可用「粘贴回调链接」方式完成。"));
        checks.Add(CheckDataPath());
        return checks;
    }

    private async Task<DiagnosticCheck> CheckGatewayAsync(AlibabaSettings settings)
    {
        const string title = "Alibaba 网关";
        try
        {
            var http = httpClientFactory.CreateClient("diagnostics");
            http.Timeout = TimeSpan.FromSeconds(15);
            using var response = await http.PostAsync(settings.GatewayUrl + "/auth/token/create", new FormUrlEncodedContent([]));
            var body = await response.Content.ReadAsStringAsync();
            var parsed = AlibabaResponseParser.Parse((int)response.StatusCode, body);
            if (parsed.Json is not null && parsed.Code is not null)
            {
                return new("gateway", title, Ok, $"{settings.GatewayUrl} 可访问。");
            }

            return new("gateway", title, Fail, $"{settings.GatewayUrl} 返回 HTTP {(int)response.StatusCode}，不是 Alibaba 网关。",
                $"把 Alibaba__BaseUrl 改为 {AlibabaSettings.DefaultGatewayUrl}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new("gateway", title, Fail, $"无法连接 {settings.GatewayUrl}：{ex.Message}", "检查服务器网络 / 防火墙 / DNS。");
        }
    }

    private async Task<DiagnosticCheck> CheckSignatureAsync(AlibabaSettings settings)
    {
        const string title = "签名校验";
        var api = settings.Apis["auth.token.create"];
        // A made-up code gets past signature verification and fails with InvalidCode, proving AppKey + AppSecret are right.
        var result = await transport.SendAsync(settings, "diagnostics.signature", api.Path,
            new Dictionary<string, string> { ["code"] = "soford_diagnostic_check" }, null, audit: false);
        return result.ErrorCode switch
        {
            "InvalidCode" => new("signature", title, Ok, "Alibaba 已接受签名，AppKey 与 AppSecret 正确。"),
            "IncompleteSignature" => new("signature", title, Fail, "签名被拒绝，AppSecret 不正确。", "到 App Console 复制正确的 App Secret。"),
            "InvalidAppKey" or "AppKeyNotExist" or "InvalidApiKey" => new("signature", title, Fail, $"AppKey 无效：{result.Describe()}", "到 App Console 核对 App Key。"),
            _ => new("signature", title, Warn, $"未能确认签名：{result.Describe()}")
        };
    }

    private async Task<DiagnosticCheck> CheckTokenAsync()
    {
        const string title = "店铺授权";
        var token = await tokens.GetAsync();
        if (token is null)
        {
            return new("token", title, Fail, "尚未授权。", "点击「授权 Alibaba 店铺」，或粘贴回调链接中的 code。");
        }

        var now = time.GetUtcNow();
        var account = string.IsNullOrWhiteSpace(token.Account) ? "" : $"账号 {token.Account}，";
        if (token.AccessTokenExpiresAt > now)
        {
            return new("token", title, Ok, $"{account}access_token 有效至 {token.AccessTokenExpiresAt.ToLocalTime():yyyy-MM-dd HH:mm}。");
        }

        return token.RefreshTokenExpiresAt > now
            ? new("token", title, Warn, $"{account}access_token 已过期，调用时会自动续期。")
            : new("token", title, Fail, $"{account}授权已过期。", "重新授权 Alibaba 店铺。");
    }

    private async Task<DiagnosticCheck> CheckApiAccessAsync()
    {
        const string title = "接口权限";
        var result = await client.CallAsync("category.predict", new { title = "portable bluetooth speaker" });
        if (result.Success)
        {
            return new("api", title, Ok, "授权有效，接口调用成功（类目预测）。");
        }

        return result.ErrorCode is "IllegalAccessToken" or "NotAuthorized"
            ? new("api", title, Fail, AlibabaErrors.Explain(result), "重新授权 Alibaba 店铺。")
            : new("api", title, Warn, AlibabaErrors.Explain(result), "如提示无权限，请在 App Console 为应用申请对应接口。");
    }

    private DiagnosticCheck CheckDataPath()
    {
        try
        {
            var probe = paths.File($".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new("storage", "数据目录", Ok, paths.DataPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("storage", "数据目录", Fail, $"{paths.DataPath} 不可写：{ex.Message}", "检查目录权限（服务器上应属于 www-data）。");
        }
    }

    private static string Mask(string value) => value.Length <= 4 ? "****" : value[..2] + new string('*', value.Length - 4) + value[^2..];
}
