using System.Security.Claims;
using System.Security.Cryptography;

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
}

/// <summary>
/// A person who signs in. Operators work on the listings of the Alibaba accounts assigned to them (typically their own
/// sub-account); admins see everything and manage accounts, users and integrations.
/// </summary>
public sealed class UserRecord
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Operator;
    public List<Guid> AccountIds { get; set; } = [];
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Changes when the password changes; sessions issued before it are signed out.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>Who is making the request, resolved fresh from the user store so role and account changes apply at once.</summary>
public sealed record CurrentUser(string Id, string Name, bool IsAdmin, IReadOnlySet<Guid> AccountIds)
{
    /// <summary>The administrator configured in the environment (Auth__AdminUsername); it has no stored record.</summary>
    public const string BuiltInAdminId = "admin";
}

public static class UserClaims
{
    public const string UserId = "soford:uid";
    public const string Stamp = "soford:stamp";

    public static string? Id(ClaimsPrincipal principal) => principal.FindFirstValue(UserId);
}

public static class PasswordHasher
{
    private const int Iterations = 100_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"v1${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v1" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Same rule as the environment admin password: at least 10 characters.</summary>
    public static string? Weakness(string? password) =>
        string.IsNullOrWhiteSpace(password) || password.Length < 10 ? "密码至少 10 个字符。" : null;
}

public sealed class UsernameTakenException(string username) : Exception($"用户名「{username}」已存在。");

/// <summary>users.json, kept in memory after the first read.</summary>
public sealed class UserStore(AppPaths paths, TimeProvider time)
{
    private readonly string _file = paths.File("users.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<UserRecord>? _cache;

    public async Task<List<UserRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAsync()).Select(Copy).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<UserRecord?> FindAsync(Guid id) => (await GetAllAsync()).FirstOrDefault(x => x.Id == id);

    public async Task<UserRecord?> VerifyAsync(string username, string password)
    {
        var user = (await GetAllAsync()).FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        return user is { Disabled: false } && PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    public Task<UserRecord> CreateAsync(string username, string displayName, string password, string role, IEnumerable<Guid> accountIds) => WriteAsync(users =>
    {
        username = username.Trim();
        if (users.Any(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))) throw new UsernameTakenException(username);
        var user = new UserRecord
        {
            Id = Guid.NewGuid(),
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            Role = role == Roles.Admin ? Roles.Admin : Roles.Operator,
            AccountIds = accountIds.Distinct().ToList(),
            CreatedAt = time.GetUtcNow()
        };
        users.Add(user);
        return Copy(user);
    });

    public Task<UserRecord?> UpdateAsync(Guid id, Action<UserRecord> change) => WriteAsync(users =>
    {
        var user = users.FirstOrDefault(x => x.Id == id);
        if (user is null) return null;
        change(user);
        return Copy(user);
    });

    public Task<bool> DeleteAsync(Guid id) => WriteAsync(users => users.RemoveAll(x => x.Id == id) > 0);

    private async Task<T> WriteAsync<T>(Func<List<UserRecord>, T> change)
    {
        await _lock.WaitAsync();
        try
        {
            var users = await ReadAsync();
            try
            {
                var result = change(users);
                await JsonFile.WriteAtomicAsync(_file, users);
                return result;
            }
            catch
            {
                _cache = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<UserRecord>> ReadAsync() => _cache ??= await JsonFile.ReadAsync<List<UserRecord>>(_file) ?? [];

    private static UserRecord Copy(UserRecord user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        DisplayName = user.DisplayName,
        PasswordHash = user.PasswordHash,
        Role = user.Role,
        AccountIds = [.. user.AccountIds],
        Disabled = user.Disabled,
        CreatedAt = user.CreatedAt,
        SecurityStamp = user.SecurityStamp
    };
}

/// <summary>Resolves the caller and decides which products they may see and change.</summary>
public sealed class AccessService(UserStore users, AlibabaTokenService tokens, IConfiguration config, IHostEnvironment env)
{
    public async Task<CurrentUser?> CurrentAsync(ClaimsPrincipal principal)
    {
        var id = UserClaims.Id(principal);
        if (id is null) return null;
        if (id == CurrentUser.BuiltInAdminId) return new(id, LocalAdminAuth.Username(config, env), true, new HashSet<Guid>());
        if (!Guid.TryParse(id, out var guid) || await users.FindAsync(guid) is not { Disabled: false } user) return null;
        return new(id, user.DisplayName, user.Role == Roles.Admin, user.AccountIds.ToHashSet());
    }

    /// <summary>
    /// Operators see listings owned by or assigned to their accounts, plus products they created themselves.
    /// Admins see everything.
    /// </summary>
    public async Task<Func<ProductRecord, bool>> VisibleAsync(ClaimsPrincipal principal)
    {
        var user = await CurrentAsync(principal);
        if (user is null) return _ => false;
        if (user.IsAdmin) return _ => true;

        var owned = (await tokens.GetAccountsAsync()).Where(x => user.AccountIds.Contains(x.Id)).ToList();
        return product =>
            (product.AccountId is { } assigned && user.AccountIds.Contains(assigned))
            || owned.Any(account => account.Owns(product.OwnerAliId))
            || product.CreatedBy == user.Id;
    }
}
