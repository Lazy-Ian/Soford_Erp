using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public sealed class AlibabaTokenRecord
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
    public DateTimeOffset RefreshTokenExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Account { get; set; }
    public string? AccountId { get; set; }
    public string? Country { get; set; }

    public static AlibabaTokenRecord FromResponse(JsonElement root, DateTimeOffset now)
    {
        var accessToken = AlibabaResponseParser.Text(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Alibaba token response did not include access_token.");
        }

        var expiresIn = AlibabaResponseParser.ReadLong(root, "expires_in");
        var refreshExpiresIn = AlibabaResponseParser.ReadLong(root, "refresh_expires_in");
        return new AlibabaTokenRecord
        {
            AccessToken = accessToken,
            RefreshToken = AlibabaResponseParser.Text(root, "refresh_token") ?? "",
            // Missing expires_in should not force a refresh on every call; assume one day and let the API tell us otherwise.
            AccessTokenExpiresAt = now.AddSeconds(expiresIn > 0 ? expiresIn : 86400),
            RefreshTokenExpiresAt = now.AddSeconds(Math.Max(refreshExpiresIn, 0)),
            CreatedAt = now,
            Account = AlibabaResponseParser.Text(root, "account"),
            AccountId = AlibabaResponseParser.Text(root, "account_id"),
            Country = AlibabaResponseParser.Text(root, "country")
        };
    }
}

/// <summary>Stores the seller token encrypted with ASP.NET Data Protection.</summary>
public sealed class AlibabaTokenStore(AppPaths paths, IDataProtectionProvider dataProtection)
{
    private readonly string _file = paths.File("alibaba-token.json");
    private readonly IDataProtector _protector = dataProtection.CreateProtector("SofordErp.AlibabaToken.v1");

    private sealed record ProtectedToken(string Protected);

    public async Task<AlibabaTokenRecord?> GetAsync()
    {
        if (!File.Exists(_file))
        {
            return null;
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_file));
        if (document.RootElement.TryGetProperty("protected", out var protectedValue))
        {
            try
            {
                return JsonSerializer.Deserialize<AlibabaTokenRecord>(_protector.Unprotect(protectedValue.GetString() ?? ""), JsonFile.Options);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Keys were rotated or lost; the seller has to authorize again.
                return null;
            }
        }

        // Plain-text file written by the previous release: read once, then re-save encrypted.
        var legacy = document.RootElement.Deserialize<AlibabaTokenRecord>(JsonFile.Options);
        if (legacy is not null && !string.IsNullOrWhiteSpace(legacy.AccessToken))
        {
            await SaveAsync(legacy);
            return legacy;
        }

        return null;
    }

    public Task SaveAsync(AlibabaTokenRecord token) =>
        JsonFile.WriteAtomicAsync(_file, new ProtectedToken(_protector.Protect(JsonSerializer.Serialize(token, JsonFile.Options))));

    public Task ClearAsync()
    {
        File.Delete(_file);
        return Task.CompletedTask;
    }
}

public sealed class AlibabaTokenService(AlibabaTransport transport, AlibabaTokenStore store, IConfiguration config, TimeProvider time)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public Task<AlibabaTokenRecord?> GetAsync() => store.GetAsync();

    public async Task<AlibabaTokenRecord> CreateFromCodeAsync(string code)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        var api = settings.Apis["auth.token.create"];
        var result = await transport.SendAsync(settings, api.Key, api.Path, new Dictionary<string, string> { ["code"] = code.Trim() }, null);
        return await SaveResultAsync(result);
    }

    public async Task<AlibabaTokenRecord> RefreshAsync()
    {
        await _refreshLock.WaitAsync();
        try
        {
            return await RefreshUnsafeAsync(await store.GetAsync());
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Returns a usable access token, refreshing it when it is about to expire. Refreshes are serialized.</summary>
    public async Task<(string? AccessToken, string? Error)> GetValidAccessTokenAsync()
    {
        var token = await store.GetAsync();
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return (null, "尚未授权 Alibaba 店铺，请先在「店铺连接」完成授权。");
        }

        if (token.AccessTokenExpiresAt > time.GetUtcNow() + RefreshMargin)
        {
            return (token.AccessToken, null);
        }

        await _refreshLock.WaitAsync();
        try
        {
            // Another request may have refreshed while we waited.
            token = await store.GetAsync();
            if (token is not null && token.AccessTokenExpiresAt > time.GetUtcNow() + RefreshMargin)
            {
                return (token.AccessToken, null);
            }

            var refreshed = await RefreshUnsafeAsync(token);
            return (refreshed.AccessToken, null);
        }
        catch (Exception ex) when (ex is AlibabaApiException or InvalidOperationException)
        {
            return (null, $"Alibaba 授权已过期且自动续期失败，请重新授权。（{ex.Message}）");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<AlibabaTokenRecord> RefreshUnsafeAsync(AlibabaTokenRecord? current)
    {
        if (current is null || string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("没有可用的 refresh_token，请重新授权。");
        }

        if (current.RefreshTokenExpiresAt <= time.GetUtcNow())
        {
            throw new InvalidOperationException("refresh_token 已过期，请重新授权。");
        }

        var settings = AlibabaSettings.FromConfiguration(config);
        var api = settings.Apis["auth.token.refresh"];
        var result = await transport.SendAsync(settings, api.Key, api.Path, new Dictionary<string, string> { ["refresh_token"] = current.RefreshToken }, null);
        return await SaveResultAsync(result, current);
    }

    private async Task<AlibabaTokenRecord> SaveResultAsync(AlibabaApiResult result, AlibabaTokenRecord? previous = null)
    {
        if (!result.Success || result.Json is null)
        {
            throw new AlibabaApiException(result);
        }

        var token = AlibabaTokenRecord.FromResponse(result.Json.Value, time.GetUtcNow());
        if (previous is not null && string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            token.RefreshToken = previous.RefreshToken;
            token.RefreshTokenExpiresAt = previous.RefreshTokenExpiresAt;
        }

        token.Account ??= previous?.Account;
        await store.SaveAsync(token);
        return token;
    }
}
