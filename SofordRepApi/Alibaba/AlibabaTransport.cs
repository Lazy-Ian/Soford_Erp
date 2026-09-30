using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>Low-level signed IOP call: sign, send, parse, audit log. Never throws for network or API errors.</summary>
public sealed class AlibabaTransport(IHttpClientFactory httpClientFactory, AlibabaApiLogStore logs, TimeProvider time, ILogger<AlibabaTransport> logger)
{
    public const string HttpClientName = "alibaba";

    private static readonly HashSet<string> SlowApis = new(StringComparer.OrdinalIgnoreCase) { "product.create", "product.update", "image.upload" };

    private static readonly HashSet<string> SecretParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "access_token", "refresh_token", "sign", "code", "app_secret"
    };

    public async Task<AlibabaApiResult> SendAsync(
        AlibabaSettings settings,
        string apiKey,
        string path,
        IReadOnlyDictionary<string, string> businessParameters,
        string? accessToken,
        AlibabaFile? file = null,
        CancellationToken cancellationToken = default,
        bool audit = true)
    {
        if (!settings.HasCredentials)
        {
            return AlibabaApiResult.Failure(apiKey, path, "NotConfigured", "未配置 Alibaba AppKey / AppSecret。");
        }

        var parameters = new Dictionary<string, string>(businessParameters, StringComparer.Ordinal)
        {
            ["app_key"] = settings.AppKey!,
            ["timestamp"] = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            ["sign_method"] = "sha256"
        };
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            parameters["access_token"] = accessToken;
        }

        parameters["sign"] = IopSigner.Sign(path, parameters, settings.AppSecret!);

        var endpoint = settings.GatewayUrl + "/" + path.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = BuildContent(parameters, file) };
        var stopwatch = Stopwatch.StartNew();
        AlibabaApiResult result;
        // Listing calls make Alibaba fetch every image, so they get far more time than lookups.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SlowApis.Contains(apiKey) ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(30));
        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var parsed = AlibabaResponseParser.Parse((int)response.StatusCode, body);
            result = new AlibabaApiResult(
                apiKey,
                path,
                parsed.Success,
                (int)response.StatusCode,
                parsed.Code,
                parsed.Message,
                parsed.RequestId,
                stopwatch.ElapsedMilliseconds,
                parsed.Json,
                parsed.Json is null ? AlibabaResponseParser.Truncate(body, 2000) : null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Alibaba API {ApiKey} network failure", apiKey);
            result = new AlibabaApiResult(apiKey, path, false, 0, "NetworkError",
                ex is TaskCanceledException ? "请求 Alibaba 超时。" : $"无法连接 Alibaba 网关：{ex.Message}",
                null, stopwatch.ElapsedMilliseconds, null, null);
        }

        if (!audit)
        {
            return result;
        }

        await logs.AppendAsync(new ApiCallLogRecord(
            Guid.NewGuid(),
            time.GetUtcNow(),
            apiKey,
            path,
            result.HttpStatus,
            result.Success,
            result.ErrorCode,
            result.Success ? "OK" : result.ErrorMessage ?? "",
            result.RequestId,
            result.DurationMs,
            SummarizeRequest(businessParameters, file),
            SummarizeResponse(apiKey, result)));
        return result;
    }

    private static HttpContent BuildContent(Dictionary<string, string> parameters, AlibabaFile? file)
    {
        if (file is null)
        {
            return new FormUrlEncodedContent(parameters);
        }

        // Quote names the way browsers do; .NET's default unquoted form trips some Java multipart parsers.
        var multipart = new MultipartFormDataContent();
        foreach (var (key, value) in parameters)
        {
            var part = new StringContent(value);
            part.Headers.ContentType = null;
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{key}\"" };
            multipart.Add(part);
        }

        var fileContent = new ByteArrayContent(file.Content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = $"\"{file.FieldName}\"",
            FileName = $"\"{file.FileName.Replace("\"", "")}\""
        };
        multipart.Add(fileContent);
        return multipart;
    }

    private static string SummarizeRequest(IReadOnlyDictionary<string, string> parameters, AlibabaFile? file)
    {
        var safe = parameters.ToDictionary(
            x => x.Key,
            x => SecretParameters.Contains(x.Key) ? "***" : AlibabaResponseParser.Truncate(x.Value, 1500));
        if (file is not null)
        {
            safe[file.FieldName] = $"<file {file.FileName}, {file.Content.Length} bytes>";
        }

        return JsonSerializer.Serialize(safe);
    }

    private static string SummarizeResponse(string apiKey, AlibabaApiResult result)
    {
        if (apiKey.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) && result.Success)
        {
            return "[token response hidden]";
        }

        return AlibabaResponseParser.Truncate(result.Json?.GetRawText() ?? result.RawBody, 4000);
    }
}

public sealed record ApiCallLogRecord(
    Guid Id,
    DateTimeOffset CreatedAt,
    string ApiKey,
    string Path,
    int HttpStatus,
    bool Success,
    string? ErrorCode,
    string Message,
    string? RequestId,
    long DurationMs,
    string? Request,
    string? Response);

public sealed class AlibabaApiLogStore(AppPaths paths)
{
    private const int MaxEntries = 1000;
    private readonly string _file = paths.File("alibaba-api-logs.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    // Kept in memory so each call only writes the file (several MB when full) instead of reading it back first.
    private List<ApiCallLogRecord>? _cache;

    public async Task AppendAsync(ApiCallLogRecord log)
    {
        await _lock.WaitAsync();
        try
        {
            var logs = await ReadAsync();
            logs.Add(log);
            if (logs.Count > MaxEntries)
            {
                logs.RemoveRange(0, logs.Count - MaxEntries);
            }

            await JsonFile.WriteAtomicAsync(_file, logs);
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
            return (await ReadAsync()).OrderByDescending(x => x.CreatedAt).Take(take).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ApiCallLogRecord>> ReadAsync()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = (await JsonFile.ReadAsync<List<ApiCallLogRecord>>(_file) ?? []).OrderBy(x => x.CreatedAt).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Old log format from the previous release, or a damaged file; logs are disposable.
            _cache = [];
        }

        return _cache;
    }
}
