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

/// <summary>Locks a client out for a few minutes after repeated failed logins.</summary>
public sealed class LoginThrottle(TimeProvider time)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _state = new();

    public TimeSpan? LockedFor(string client)
    {
        if (_state.TryGetValue(client, out var entry) && entry.LockedUntil > time.GetUtcNow())
        {
            return entry.LockedUntil - time.GetUtcNow();
        }

        return null;
    }

    public void RecordFailure(string client)
    {
        _state.AddOrUpdate(
            client,
            _ => (1, DateTimeOffset.MinValue),
            (_, entry) =>
            {
                if (entry.LockedUntil > time.GetUtcNow())
                {
                    return entry;
                }

                var failures = entry.Failures + 1;
                return failures >= MaxFailures ? (0, time.GetUtcNow() + LockDuration) : (failures, DateTimeOffset.MinValue);
            });
    }

    public void Reset(string client) => _state.TryRemove(client, out _);
}
