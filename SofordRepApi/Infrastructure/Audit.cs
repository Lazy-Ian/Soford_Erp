/// <summary>One thing a person did: who, when, what, on how many products.</summary>
public sealed record AuditEntry(Guid Id, DateTimeOffset At, string UserId, string UserName, string Action, string Summary, int Count);

/// <summary>Operation history (audit-log.json), newest entries kept, held in memory after the first read.</summary>
public sealed class AuditLog(AppPaths paths, TimeProvider time)
{
    private const int MaxEntries = 5000;
    private readonly string _file = paths.File("audit-log.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AuditEntry>? _cache;

    public async Task RecordAsync(CurrentUser? user, string action, string summary, int count = 0)
    {
        await _lock.WaitAsync();
        try
        {
            var entries = await ReadAsync();
            entries.Add(new AuditEntry(Guid.NewGuid(), time.GetUtcNow(), user?.Id ?? "system", user?.Name ?? "系统", action, summary, count));
            if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            await JsonFile.WriteAtomicAsync(_file, entries);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<AuditEntry>> LatestAsync(int take, string? userId)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAsync()).Where(x => userId is null || x.UserId == userId).Reverse().Take(take).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<AuditEntry>> ReadAsync()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = await JsonFile.ReadAsync<List<AuditEntry>>(_file) ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            _cache = [];
        }

        return _cache;
    }
}
