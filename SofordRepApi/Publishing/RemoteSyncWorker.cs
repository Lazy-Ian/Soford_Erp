public sealed record AutomationStatus(
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    int Checked,
    int Changed,
    string? LastMessage,
    DateTimeOffset? LastPullAt = null);

/// <summary>Survives restarts so a deploy does not trigger a full catalog read every time.</summary>
public sealed record AutomationMemory(DateTimeOffset? LastPullAt);

/// <summary>Shared state of the background sync so the UI can show when it last ran and what it did.</summary>
public sealed class AutomationState
{
    private readonly object _gate = new();
    private AutomationStatus _status = new(false, 0, null, null, 0, 0, null);

    public AutomationStatus Current
    {
        get { lock (_gate) return _status; }
    }

    public void Set(AutomationStatus status)
    {
        lock (_gate) _status = status;
    }
}

/// <summary>
/// Periodically keeps Alibaba state fresh without anyone clicking: renews the token ahead of expiry and
/// re-queries products that are waiting for Alibaba review.
/// </summary>
public sealed class RemoteSyncWorker(
    IConfiguration config,
    AppPaths paths,
    ProductRepository products,
    ProductOperations operations,
    AlibabaCatalogSync catalog,
    AlibabaTokenService tokens,
    AutomationState state,
    TimeProvider time,
    ILogger<RemoteSyncWorker> logger) : BackgroundService
{
    private const int MaxProductsPerRun = 200;
    private const int MaxRefreshesPerRun = 100;
    private static readonly TimeSpan TokenRenewWindow = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // Failed checks do not update LastSyncedAt, so remember attempts here; otherwise a few hundred permanently
    // failing products would be first in line every run and starve everything else.
    private readonly Dictionary<Guid, DateTimeOffset> _lastAttempt = [];

    public int IntervalMinutes => int.TryParse(config["Soford:AutoSync:IntervalMinutes"], out var minutes) ? Math.Max(minutes, 0) : 30;

    /// <summary>Hours between full reads of the Alibaba catalog (new listings, deleted listings, owners); 0 turns it off.</summary>
    public int PullHours => int.TryParse(config["Soford:AutoSync:PullHours"], out var hours) ? Math.Max(hours, 0) : 24;

    private string MemoryFile => paths.File("automation.json");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = IntervalMinutes;
        state.Set(new(interval > 0, interval, null, interval > 0 ? time.GetUtcNow().AddMinutes(1) : null, 0, 0, interval > 0 ? null : "已关闭（Soford:AutoSync:IntervalMinutes=0）"));
        if (interval == 0) return;

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unhandled exception in a BackgroundService stops the whole host in .NET 8; log and retry next cycle.
                    logger.LogError(ex, "Automatic Alibaba sync failed");
                    state.Set(state.Current with
                    {
                        LastRunAt = time.GetUtcNow(),
                        NextRunAt = time.GetUtcNow().AddMinutes(interval),
                        LastMessage = $"自动同步出错：{ex.Message}"
                    });
                }

                await Task.Delay(TimeSpan.FromMinutes(interval), time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async Task<AutomationStatus> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            var interval = IntervalMinutes;
            var (checkedCount, changed, message) = await SyncAsync(cancellationToken);
            var memory = await ReadMemoryAsync();
            var status = new AutomationStatus(interval > 0, interval, time.GetUtcNow(), interval > 0 ? time.GetUtcNow().AddMinutes(interval) : null, checkedCount, changed, message, memory.LastPullAt);
            state.Set(status);
            return status;
        }
        finally
        {
            _runLock.Release();
        }
    }

    private async Task<(int Checked, int Changed, string Message)> SyncAsync(CancellationToken cancellationToken)
    {
        var settings = AlibabaSettings.FromConfiguration(config);
        var accounts = await tokens.GetAccountsAsync();
        if (!settings.HasCredentials || accounts.All(x => x.Token is null))
        {
            return (0, 0, "店铺未连接，跳过自动同步。");
        }

        var now = time.GetUtcNow();
        var notes = new List<string>();
        foreach (var account in accounts.Where(x => x.Token is not null))
        {
            var accountToken = account.Token!;
            if (accountToken.AccessTokenExpiresAt - now >= TokenRenewWindow || accountToken.RefreshTokenExpiresAt <= now)
            {
                continue;
            }

            try
            {
                await tokens.RefreshAsync(account.Id);
                notes.Add($"已提前续期「{account.Name}」的 Token");
            }
            catch (Exception ex) when (ex is AlibabaApiException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Proactive Alibaba token refresh failed for {Account}", account.Name);
                notes.Add($"「{account.Name}」Token 续期失败：{ex.Message}");
            }
        }

        await PullIfDueAsync(notes, cancellationToken);

        var waiting = (await products.GetAllAsync())
            .Where(x => x.PublishState == PublishState.Pending && !string.IsNullOrWhiteSpace(x.RemoteProductId))
            .ToList();
        var waitingIds = waiting.Select(x => x.Id).ToHashSet();
        foreach (var id in _lastAttempt.Keys.Where(id => !waitingIds.Contains(id)).ToList()) _lastAttempt.Remove(id);

        var pending = waiting
            .OrderBy(LastTouched)
            .Take(MaxProductsPerRun)
            .ToList();

        // Tokens may have been renewed above.
        accounts = await tokens.GetAccountsAsync();
        var changed = 0;
        var failed = 0;
        var brokenAccounts = new HashSet<Guid?>();
        foreach (var product in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accountId = AlibabaTokenService.ResolveForProduct(accounts, product)?.Id;
            if (brokenAccounts.Contains(accountId)) continue;

            _lastAttempt[product.Id] = time.GetUtcNow();
            var result = await operations.RefreshStatusAsync(product.Id);
            if (!result.Success)
            {
                failed++;
                // An authorization problem fails every product of that account; skip its other products and report it once.
                if (result.Message.Contains("授权", StringComparison.Ordinal) && brokenAccounts.Add(accountId))
                {
                    notes.Add(result.Message);
                }

                continue;
            }

            var after = await products.GetAsync(product.Id);
            if (after is not null && after.PublishState != product.PublishState) changed++;
        }

        await RefreshIncompleteAsync(notes, cancellationToken);

        notes.Insert(0, pending.Count == 0 ? "没有审核中的商品" : $"检查 {pending.Count} 个审核中的商品，状态变化 {changed} 个" + (failed > 0 ? $"，失败 {failed} 个" : ""));
        return (pending.Count, changed, string.Join("；", notes) + "。");
    }

    /// <summary>Reads the whole catalog once a day: new listings, removed listings and owner accounts stay current.</summary>
    private async Task PullIfDueAsync(List<string> notes, CancellationToken cancellationToken)
    {
        var memory = await ReadMemoryAsync();
        if (PullHours == 0 || memory.LastPullAt > time.GetUtcNow().AddHours(-PullHours)) return;

        var (result, error) = await catalog.PullAsync(cancellationToken);
        if (result is null)
        {
            notes.Add($"自动从 Alibaba 导入失败：{AlibabaErrors.Explain(error!)}");
            return;
        }

        await JsonFile.WriteAtomicAsync(MemoryFile, new AutomationMemory(time.GetUtcNow()));
        var parts = new List<string> { $"已从 Alibaba 读取 {result.Total} 个商品" };
        if (result.Created > 0) parts.Add($"新增 {result.Created} 个");
        if (result.Missing > 0) parts.Add($"{result.Missing} 个在 Alibaba 上已找不到");
        notes.Add(string.Join("，", parts));
    }

    /// <summary>Listings imported without their description (search omits it) get their content fetched a batch at a time.</summary>
    private async Task RefreshIncompleteAsync(List<string> notes, CancellationToken cancellationToken)
    {
        var candidates = (await products.GetAllAsync())
            .Where(x => !string.IsNullOrWhiteSpace(x.RemoteProductId) && x.ContentRefreshedAt is null && x.Description.Length == 0
                && !x.HasUnpublishedChanges && x.PublishState != PublishState.Publishing && x.RemoteStatus != ProductRepository.RemoteMissingStatus)
            .Take(MaxRefreshesPerRun)
            .ToList();
        if (candidates.Count == 0) return;

        var done = 0;
        foreach (var product in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((await operations.RefreshContentAsync(product.Id, discardLocalChanges: false)).Success) done++;
        }

        notes.Add($"补全 {done}/{candidates.Count} 个商品的内容");
    }

    private async Task<AutomationMemory> ReadMemoryAsync()
    {
        try
        {
            return await JsonFile.ReadAsync<AutomationMemory>(MemoryFile) ?? new(null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            return new(null);
        }
    }

    private DateTimeOffset LastTouched(ProductRecord product)
    {
        var synced = product.LastSyncedAt ?? DateTimeOffset.MinValue;
        return _lastAttempt.TryGetValue(product.Id, out var attempted) && attempted > synced ? attempted : synced;
    }
}
