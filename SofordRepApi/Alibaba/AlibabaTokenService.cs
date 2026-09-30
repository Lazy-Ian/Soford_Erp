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
    public string? UserId { get; set; }
    public string? SellerId { get; set; }

    /// <summary>Every identifier Alibaba returned for the authorizing user; one of them is the listings' owner_ali_id.</summary>
    public IEnumerable<string> Identifiers() =>
        new[] { UserId, SellerId, AccountId }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!);

    public static AlibabaTokenRecord FromResponse(JsonElement root, DateTimeOffset now)
    {
        var accessToken = AlibabaResponseParser.Text(root, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("Alibaba token response did not include access_token.");
        }

        var expiresIn = AlibabaResponseParser.ReadLong(root, "expires_in");
        var refreshExpiresIn = AlibabaResponseParser.ReadLong(root, "refresh_expires_in");
        var userInfo = root.TryGetProperty("user_info", out var info) && info.ValueKind == JsonValueKind.Object ? info : default;
        return new AlibabaTokenRecord
        {
            AccessToken = accessToken,
            RefreshToken = AlibabaResponseParser.Text(root, "refresh_token") ?? "",
            // Missing expires_in should not force a refresh on every call; assume one day and let the API tell us otherwise.
            AccessTokenExpiresAt = now.AddSeconds(expiresIn > 0 ? expiresIn : 86400),
            RefreshTokenExpiresAt = now.AddSeconds(Math.Max(refreshExpiresIn, 0)),
            CreatedAt = now,
            Account = AlibabaResponseParser.Text(root, "account") ?? AlibabaResponseParser.Text(userInfo, "loginId"),
            AccountId = AlibabaResponseParser.Text(root, "account_id"),
            Country = AlibabaResponseParser.Text(root, "country"),
            UserId = AlibabaResponseParser.Text(userInfo, "user_id"),
            SellerId = AlibabaResponseParser.Text(userInfo, "seller_id")
        };
    }
}

/// <summary>
/// An Alibaba.com account (main or sub-account) that owns listings. It may or may not be authorized:
/// accounts are created automatically for every owner_ali_id seen when pulling, so products can be grouped
/// by account before anyone has logged in with it.
/// </summary>
public sealed class AlibabaAccount
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> OwnerAliIds { get; set; } = [];
    public bool IsDefault { get; set; }
    public AlibabaTokenRecord? Token { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool Owns(string? ownerAliId) =>
        !string.IsNullOrWhiteSpace(ownerAliId) && OwnerAliIds.Contains(ownerAliId, StringComparer.Ordinal);
}

/// <summary>Stores all accounts (including their tokens) encrypted with ASP.NET Data Protection.</summary>
public sealed class AlibabaAccountStore(AppPaths paths, IDataProtectionProvider dataProtection, TimeProvider time, ILogger<AlibabaAccountStore> logger)
{
    private readonly ILogger _logger = logger;
    private readonly string _file = paths.File("alibaba-accounts.json");
    private readonly string _legacyTokenFile = paths.File("alibaba-token.json");
    private readonly IDataProtector _protector = dataProtection.CreateProtector("SofordErp.AlibabaAccounts.v1");
    private readonly IDataProtector _legacyProtector = dataProtection.CreateProtector("SofordErp.AlibabaToken.v1");
    private readonly SemaphoreSlim _lock = new(1, 1);

    private sealed record ProtectedPayload(string Protected);

    public async Task<List<AlibabaAccount>> GetAllAsync()
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

    public async Task<T> UpdateAsync<T>(Func<List<AlibabaAccount>, T> change)
    {
        await _lock.WaitAsync();
        try
        {
            var accounts = await ReadUnsafeAsync();
            var result = change(accounts);
            await WriteUnsafeAsync(accounts);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<AlibabaAccount>> ReadUnsafeAsync()
    {
        if (!File.Exists(_file))
        {
            return await MigrateLegacyTokenAsync();
        }

        var payload = await JsonFile.ReadAsync<ProtectedPayload>(_file);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Protected))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<AlibabaAccount>>(_protector.Unprotect(payload.Protected), JsonFile.Options) ?? [];
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // The data-protection keys were lost or replaced (e.g. a restore without app_data/data-protection-keys).
            // Move the file aside instead of letting the next write overwrite it: with the old keys restored it
            // can be put back, and account names and owner ids are not lost.
            var aside = $"{_file}.unreadable-{time.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(_file, aside);
            _logger.LogError("alibaba-accounts.json could not be decrypted and was moved to {File}; accounts must be authorized again or the old data-protection keys restored.", aside);
            return [];
        }
    }

    private Task WriteUnsafeAsync(List<AlibabaAccount> accounts) =>
        JsonFile.WriteAtomicAsync(_file, new ProtectedPayload(_protector.Protect(JsonSerializer.Serialize(accounts, JsonFile.Options))));

    /// <summary>The previous release kept a single token (encrypted or plain) in alibaba-token.json; it becomes the default account.</summary>
    private async Task<List<AlibabaAccount>> MigrateLegacyTokenAsync()
    {
        if (!File.Exists(_legacyTokenFile))
        {
            return [];
        }

        AlibabaTokenRecord? token = null;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(_legacyTokenFile));
            token = document.RootElement.TryGetProperty("protected", out var protectedValue)
                ? JsonSerializer.Deserialize<AlibabaTokenRecord>(_legacyProtector.Unprotect(protectedValue.GetString() ?? ""), JsonFile.Options)
                : document.RootElement.Deserialize<AlibabaTokenRecord>(JsonFile.Options);
        }
        catch (Exception ex) when (ex is JsonException or System.Security.Cryptography.CryptographicException)
        {
            token = null;
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return [];
        }

        var accounts = new List<AlibabaAccount>
        {
            new()
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(token.Account) ? "主账号" : token.Account!,
                OwnerAliIds = token.Identifiers().Distinct().ToList(),
                IsDefault = true,
                Token = token,
                CreatedAt = time.GetUtcNow()
            }
        };
        await WriteUnsafeAsync(accounts);
        File.Move(_legacyTokenFile, _legacyTokenFile + ".migrated", overwrite: true);
        return accounts;
    }
}

public sealed record AccountToken(Guid AccountId, string AccountName, string AccessToken);

public sealed class AlibabaTokenService(AlibabaTransport transport, AlibabaAccountStore store, IConfiguration config, TimeProvider time)
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public Task<List<AlibabaAccount>> GetAccountsAsync() => store.GetAllAsync();

    /// <summary>The default authorized account: the one marked default, otherwise the first with a token.</summary>
    public static AlibabaAccount? DefaultAccount(IEnumerable<AlibabaAccount> accounts)
    {
        var authorized = accounts.Where(x => x.Token is not null).ToList();
        return authorized.FirstOrDefault(x => x.IsDefault) ?? authorized.FirstOrDefault();
    }

    /// <summary>Token of the given account, or of the default account when no id is given.</summary>
    public async Task<AlibabaTokenRecord?> GetAsync(Guid? accountId = null)
    {
        var accounts = await store.GetAllAsync();
        return (accountId is null ? DefaultAccount(accounts) : accounts.FirstOrDefault(x => x.Id == accountId))?.Token;
    }

    /// <summary>
    /// Which account acts for a product: an explicitly assigned account, else the authorized account that owns the listing,
    /// else the default account (the main account can manage every listing of the store).
    /// </summary>
    public static AlibabaAccount? ResolveForProduct(IReadOnlyList<AlibabaAccount> accounts, ProductRecord product)
    {
        var authorized = accounts.Where(x => x.Token is not null).ToList();
        return authorized.FirstOrDefault(x => x.Id == product.AccountId)
            ?? authorized.FirstOrDefault(x => x.Owns(product.OwnerAliId))
            ?? DefaultAccount(authorized);
    }

    public async Task<(AlibabaAccount Account, AlibabaTokenRecord Token)> CreateFromCodeAsync(string code)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        var api = settings.Apis["auth.token.create"];
        var result = await transport.SendAsync(settings, api.Key, api.Path, new Dictionary<string, string> { ["code"] = code.Trim() }, null);
        var token = ParseResult(result, null);

        var account = await store.UpdateAsync(accounts =>
        {
            var ids = token.Identifiers().ToList();
            var byId = accounts.FirstOrDefault(x => ids.Any(x.Owns));
            var byLogin = accounts.FirstOrDefault(x => x.Token?.Account is { } login && string.Equals(login, token.Account, StringComparison.OrdinalIgnoreCase));
            var account = byLogin ?? byId;
            if (byId is not null && byLogin is not null && byId != byLogin)
            {
                // Re-authorizing an account that was authorized before its id was known: fold the placeholder
                // created from owner_ali_id into it, keeping the existing name and default flag.
                byLogin.OwnerAliIds = byLogin.OwnerAliIds.Concat(byId.OwnerAliIds).Distinct(StringComparer.Ordinal).ToList();
                if (byId.Token is null) accounts.Remove(byId);
            }

            if (account is null)
            {
                account = new AlibabaAccount { Id = Guid.NewGuid(), CreatedAt = time.GetUtcNow() };
                accounts.Add(account);
            }

            account.Token = token;
            account.OwnerAliIds = account.OwnerAliIds.Concat(ids).Distinct(StringComparer.Ordinal).ToList();
            if (string.IsNullOrWhiteSpace(account.Name) || account.Name.StartsWith("账号 ", StringComparison.Ordinal))
            {
                account.Name = token.Account ?? account.Name;
            }

            if (!accounts.Any(x => x.IsDefault && x.Token is not null))
            {
                accounts.ForEach(x => x.IsDefault = x == account);
            }

            return account;
        });
        return (account, token);
    }

    public async Task<AlibabaTokenRecord> RefreshAsync(Guid? accountId = null)
    {
        await _refreshLock.WaitAsync();
        try
        {
            var accounts = await store.GetAllAsync();
            var account = accountId is null ? DefaultAccount(accounts) : accounts.FirstOrDefault(x => x.Id == accountId);
            if (account is null)
            {
                throw new InvalidOperationException("没有已授权的 Alibaba 账号。");
            }

            return await RefreshUnsafeAsync(account);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Returns a usable access token for the account (or default account), refreshing it when about to expire.</summary>
    public async Task<(AccountToken? Token, string? Error)> GetValidAccessTokenAsync(Guid? accountId = null)
    {
        var accounts = await store.GetAllAsync();
        var account = accountId is null ? DefaultAccount(accounts) : accounts.FirstOrDefault(x => x.Id == accountId);
        if (account?.Token is null || string.IsNullOrWhiteSpace(account.Token.AccessToken))
        {
            return (null, accountId is null
                ? "尚未授权 Alibaba 店铺，请先在「店铺连接」完成授权。"
                : $"账号「{account?.Name ?? accountId.ToString()}」尚未授权，请在「店铺连接」用该账号登录授权。");
        }

        if (account.Token.AccessTokenExpiresAt > time.GetUtcNow() + RefreshMargin)
        {
            return (new AccountToken(account.Id, account.Name, account.Token.AccessToken), null);
        }

        await _refreshLock.WaitAsync();
        try
        {
            // Another request may have refreshed while we waited.
            var fresh = (await store.GetAllAsync()).FirstOrDefault(x => x.Id == account.Id);
            if (fresh?.Token is not null && fresh.Token.AccessTokenExpiresAt > time.GetUtcNow() + RefreshMargin)
            {
                return (new AccountToken(fresh.Id, fresh.Name, fresh.Token.AccessToken), null);
            }

            var refreshed = await RefreshUnsafeAsync(fresh ?? account);
            return (new AccountToken(account.Id, account.Name, refreshed.AccessToken), null);
        }
        catch (Exception ex) when (ex is AlibabaApiException or InvalidOperationException)
        {
            return (null, $"账号「{account.Name}」授权已过期且自动续期失败，请重新授权。（{ex.Message}）");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public Task ClearTokenAsync(Guid accountId) => store.UpdateAsync(accounts =>
    {
        var account = accounts.FirstOrDefault(x => x.Id == accountId);
        if (account is not null) account.Token = null;
        return true;
    });

    /// <summary>Creates placeholder accounts for owner ids seen on Alibaba so products can be grouped and named before authorization.</summary>
    public Task EnsureOwnerAccountsAsync(IEnumerable<string?> ownerAliIds) => store.UpdateAsync(accounts =>
    {
        foreach (var ownerId in ownerAliIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct())
        {
            if (!accounts.Any(x => x.Owns(ownerId)))
            {
                accounts.Add(new AlibabaAccount { Id = Guid.NewGuid(), Name = $"账号 {ownerId}", OwnerAliIds = [ownerId!], CreatedAt = time.GetUtcNow() });
            }
        }

        return true;
    });

    /// <summary>Renames an account, sets it as default and/or links owner ids. Linking an id moves it off any other account.</summary>
    public Task<AlibabaAccount?> UpdateAccountAsync(Guid accountId, string? name, bool? isDefault, IReadOnlyList<string>? ownerAliIds) => store.UpdateAsync(accounts =>
    {
        var account = accounts.FirstOrDefault(x => x.Id == accountId);
        if (account is null) return null;

        if (!string.IsNullOrWhiteSpace(name)) account.Name = name.Trim();
        if (ownerAliIds is not null)
        {
            var ids = ownerAliIds.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().ToList();
            foreach (var other in accounts.Where(x => x != account))
            {
                other.OwnerAliIds.RemoveAll(ids.Contains);
            }

            account.OwnerAliIds = ids;
            // Placeholder accounts left without any id and without a token are just noise now.
            accounts.RemoveAll(x => x != account && x.Token is null && x.OwnerAliIds.Count == 0);
        }

        if (isDefault == true)
        {
            accounts.ForEach(x => x.IsDefault = x == account);
        }

        return account;
    });

    private async Task<AlibabaTokenRecord> RefreshUnsafeAsync(AlibabaAccount account)
    {
        var current = account.Token;
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
        var token = ParseResult(result, current);
        await store.UpdateAsync(accounts =>
        {
            var stored = accounts.FirstOrDefault(x => x.Id == account.Id);
            if (stored is not null) stored.Token = token;
            return true;
        });
        return token;
    }

    private AlibabaTokenRecord ParseResult(AlibabaApiResult result, AlibabaTokenRecord? previous)
    {
        if (!result.Success || result.Json is null)
        {
            throw new AlibabaApiException(result);
        }

        var token = AlibabaTokenRecord.FromResponse(result.Json.Value, time.GetUtcNow());
        if (previous is not null)
        {
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                token.RefreshToken = previous.RefreshToken;
                token.RefreshTokenExpiresAt = previous.RefreshTokenExpiresAt;
            }

            token.Account ??= previous.Account;
            token.AccountId ??= previous.AccountId;
            token.UserId ??= previous.UserId;
            token.SellerId ??= previous.SellerId;
        }

        return token;
    }
}
