using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Alibaba IOP request signing: HEX_UPPER(HMAC_SHA256(secret, apiPath + k1v1k2v2...)) over ordinal-sorted params.</summary>
public static class IopSigner
{
    public static string Sign(string apiPath, IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var raw = new StringBuilder(apiPath);
        foreach (var (key, value) in parameters
                     .Where(x => !string.Equals(x.Key, "sign", StringComparison.Ordinal))
                     .OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            raw.Append(key).Append(value);
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString())));
    }
}

public static class IopParameters
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Flattens a payload object into IOP form parameters: top-level scalars as text,
    /// nested objects/arrays as JSON strings (the gateway expects e.g. product_info={...}).
    /// </summary>
    public static Dictionary<string, string> From(object? payload)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (payload is null)
        {
            return result;
        }

        var element = payload is JsonElement json ? json : JsonSerializer.SerializeToElement(payload, JsonOptions);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("API payload must be a JSON object.");
        }

        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => property.Value.GetRawText()
            };

            if (!string.IsNullOrEmpty(value))
            {
                result[property.Name] = value;
            }
        }

        return result;
    }
}

public sealed record AlibabaFile(string FieldName, string FileName, string ContentType, byte[] Content);

public sealed record AlibabaApiResult(
    string ApiKey,
    string Path,
    bool Success,
    int HttpStatus,
    string? ErrorCode,
    string? ErrorMessage,
    string? RequestId,
    long DurationMs,
    JsonElement? Json,
    string? RawBody)
{
    public string Describe() => Success
        ? "OK"
        : string.Join(" - ", new[] { ErrorCode, ErrorMessage }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public static AlibabaApiResult Failure(string apiKey, string path, string code, string message) =>
        new(apiKey, path, false, 0, code, message, null, 0, null, null);
}

public sealed class AlibabaApiException(AlibabaApiResult result) : Exception($"Alibaba {result.ApiKey}: {result.Describe()}")
{
    public AlibabaApiResult Result { get; } = result;
}

/// <summary>
/// Alibaba reports most failures with HTTP 200. Gateway errors carry a top-level code other than "0"
/// (IncompleteSignature, IllegalAccessToken, ...); business errors carry success=false with msg_code/message.
/// </summary>
public static class AlibabaResponseParser
{
    public static (bool Success, string? Code, string? Message, string? RequestId, JsonElement? Json) Parse(int httpStatus, string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var ok = httpStatus is >= 200 and < 300;
            return (false, ok ? "NonJsonResponse" : $"HTTP{httpStatus}", Truncate(body, 300), null, null);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (false, "UnexpectedResponse", Truncate(body, 300), null, root);
        }

        var requestId = Text(root, "request_id");
        var code = Text(root, "code");
        if (!string.IsNullOrEmpty(code) && code != "0")
        {
            return (false, code, Text(root, "message") ?? Text(root, "msg"), requestId, root);
        }

        // Gateway/service errors without a code, e.g. {"type":"ISP","message":"Product not found."}. Successful
        // responses never carry a top-level "type".
        if (Text(root, "type") is { Length: > 0 } errorType && Text(root, "message") is { Length: > 0 } errorMessage)
        {
            return (false, errorType, errorMessage, requestId, root);
        }

        if (TryBusinessFailure(root, out var failure))
        {
            return (false, failure.Code, failure.Message, requestId, root);
        }

        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && TryBusinessFailure(result, out failure))
        {
            return (false, failure.Code, failure.Message, requestId, root);
        }

        if (httpStatus is < 200 or >= 300)
        {
            return (false, $"HTTP{httpStatus}", Text(root, "message") ?? Truncate(body, 300), requestId, root);
        }

        return (true, null, null, requestId, root);
    }

    private static bool TryBusinessFailure(JsonElement node, out (string? Code, string? Message) failure)
    {
        failure = default;
        if (node.TryGetProperty("success", out var success)
            && (success.ValueKind == JsonValueKind.False || success.ValueKind == JsonValueKind.String && success.GetString() == "false"))
        {
            failure = (Text(node, "msg_code") ?? Text(node, "error_code") ?? "BusinessError", Text(node, "message") ?? Text(node, "error_msg"));
            return true;
        }

        var errorCode = Text(node, "error_code");
        if (!string.IsNullOrEmpty(errorCode) && errorCode != "0")
        {
            failure = (errorCode, Text(node, "error_msg") ?? Text(node, "message"));
            return true;
        }

        return false;
    }

    public static string? Text(JsonElement node, string name)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    /// <summary>Depth-first search for the first property with the given name.</summary>
    public static JsonElement? Find(JsonElement node, string name)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty(name, out var direct))
                {
                    return direct;
                }

                foreach (var property in node.EnumerateObject())
                {
                    if (Find(property.Value, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    if (Find(item, name) is { } nested)
                    {
                        return nested;
                    }
                }

                break;
        }

        return null;
    }

    public static string? ScalarText(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString(),
        JsonValueKind.Number => value.Value.GetRawText(),
        _ => null
    };

    public static long ReadLong(JsonElement node, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Text(node, name);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return 0;
    }

    public static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" : value.Length <= max ? value : value[..max] + "…";
}
