using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

public sealed record LoginRequest(string Username, string Password, bool Remember);
/// <param name="CanChangePassword">False for the built-in administrator, whose password lives in the server configuration.</param>
public sealed record AuthSessionResponse(bool Authenticated, string? Username, string? Role = null, bool CanChangePassword = false);

public static class LocalAdminAuth
{
    private const string DevelopmentUsername = "admin";
    private const string DevelopmentPassword = "admin123";

    public static string Username(IConfiguration config, IHostEnvironment env) =>
        string.IsNullOrWhiteSpace(config["Auth:AdminUsername"])
            ? env.IsDevelopment() ? DevelopmentUsername : ""
            : config["Auth:AdminUsername"]!;

    public static void ValidateConfiguration(IConfiguration config, IHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(config["Auth:AdminUsername"])
            || string.IsNullOrWhiteSpace(config["Auth:AdminPassword"]))
        {
            throw new InvalidOperationException("Set Auth__AdminUsername and Auth__AdminPassword before running outside Development.");
        }

        var password = config["Auth:AdminPassword"]!;
        if (password.Length < 10 || PlaceholderPasswords.Contains(password))
        {
            throw new InvalidOperationException("Auth__AdminPassword is a template placeholder or shorter than 10 characters; set a strong password before running outside Development.");
        }
    }

    private static readonly HashSet<string> PlaceholderPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        DevelopmentPassword,
        "change-this-password",
        "change-this-before-running-outside-development",
        "password",
        "admin"
    };

    public static bool Verify(IConfiguration config, IHostEnvironment env, string? username, string? password)
    {
        var expectedUsername = Username(config, env);
        var expectedPassword = string.IsNullOrWhiteSpace(config["Auth:AdminPassword"])
            ? env.IsDevelopment() ? DevelopmentPassword : ""
            : config["Auth:AdminPassword"]!;

        return expectedUsername.Length > 0
            && FixedTimeEquals(username ?? "", expectedUsername)
            && FixedTimeEquals(password ?? "", expectedPassword);
    }

    public static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

/// <summary>
/// Slows down password guessing: a client IP is locked for 5 minutes after 5 failures, and a username for 15 minutes
/// after 10 failures from anywhere (so switching IPs does not help). Stale entries are dropped so memory stays bounded.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    private sealed record Rule(int MaxFailures, TimeSpan LockDuration);

    private static readonly Rule ClientRule = new(5, TimeSpan.FromMinutes(5));
    private static readonly Rule UserRule = new(10, TimeSpan.FromMinutes(15));
    private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);
    private const int PruneAbove = 1000;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil, DateTimeOffset LastFailure)> _state = new();

    public TimeSpan? LockedFor(string client, string? username = null)
    {
        var now = time.GetUtcNow();
        var until = new[] { ClientKey(client), UserKey(username) }
            .Where(key => key is not null && _state.TryGetValue(key, out var entry) && entry.LockedUntil > now)
            .Select(key => _state[key!].LockedUntil)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();
        return until > now ? until - now : null;
    }

    public void RecordFailure(string client, string? username = null)
    {
        Record(ClientKey(client), ClientRule);
        if (UserKey(username) is { } userKey) Record(userKey, UserRule);
        if (_state.Count > PruneAbove) Prune();
    }

    public void Reset(string client, string? username = null)
    {
        _state.TryRemove(ClientKey(client), out _);
        if (UserKey(username) is { } userKey) _state.TryRemove(userKey, out _);
    }

    private void Record(string key, Rule rule)
    {
        var now = time.GetUtcNow();
        _state.AddOrUpdate(
            key,
            _ => (1, DateTimeOffset.MinValue, now),
            (_, entry) =>
            {
                if (entry.LockedUntil > now) return entry;
                // Failures long ago do not count towards a new lock.
                var failures = (now - entry.LastFailure > ForgetAfter ? 0 : entry.Failures) + 1;
                return failures >= rule.MaxFailures ? (0, now + rule.LockDuration, now) : (failures, DateTimeOffset.MinValue, now);
            });
    }

    private void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _state)
        {
            if (entry.LockedUntil <= now && now - entry.LastFailure > ForgetAfter) _state.TryRemove(key, out _);
        }
    }

    private static string ClientKey(string client) => "ip:" + client;

    private static string? UserKey(string? username) =>
        string.IsNullOrWhiteSpace(username) ? null : "user:" + username.Trim().ToLowerInvariant();
}
