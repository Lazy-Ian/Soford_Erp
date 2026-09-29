public sealed record AutomationStatus(
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset? LastRunAt,
    DateTimeOffset? NextRunAt,
    int Checked,
    int Changed,
    string? LastMessage);

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
    ProductRepository products,
    ProductOperations operations,
    AlibabaTokenService tokens,
    AutomationState state,
    TimeProvider time,
    ILogger<RemoteSyncWorker> logger) : BackgroundService
{
    private const int MaxProductsPerRun = 200;
    private static readonly TimeSpan TokenRenewWindow = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public int IntervalMinutes => int.TryParse(config["Soford:AutoSync:IntervalMinutes"], out var minutes) ? Math.Max(minutes, 0) : 30;

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
            var status = new AutomationStatus(interval > 0, interval, time.GetUtcNow(), interval > 0 ? time.GetUtcNow().AddMinutes(interval) : null, checkedCount, changed, message);
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
        var token = await tokens.GetAsync();
        if (!settings.HasCredentials || token is null)
        {
            return (0, 0, "店铺未连接，跳过自动同步。");
        }

        var now = time.GetUtcNow();
        var notes = new List<string>();
        if (token.AccessTokenExpiresAt - now < TokenRenewWindow && token.RefreshTokenExpiresAt > now)
        {
            try
            {
                await tokens.RefreshAsync();
                notes.Add("已提前续期 Token");
            }
            catch (Exception ex) when (ex is AlibabaApiException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Proactive Alibaba token refresh failed");
                notes.Add($"Token 续期失败：{ex.Message}");
            }
        }

        var pending = (await products.GetAllAsync())
            .Where(x => x.PublishState == PublishState.Pending && !string.IsNullOrWhiteSpace(x.RemoteProductId))
            .OrderBy(x => x.LastSyncedAt ?? DateTimeOffset.MinValue)
            .Take(MaxProductsPerRun)
            .ToList();

        var changed = 0;
        var failed = 0;
        foreach (var product in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await operations.RefreshStatusAsync(product.Id);
            if (!result.Success)
            {
                failed++;
                // An authorization problem will fail every product; stop and surface it once.
                if (result.Message.Contains("授权", StringComparison.Ordinal))
                {
                    notes.Add(result.Message);
                    break;
                }

                continue;
            }

            var after = await products.GetAsync(product.Id);
            if (after is not null && after.PublishState != product.PublishState) changed++;
        }

        notes.Insert(0, pending.Count == 0 ? "没有审核中的商品" : $"检查 {pending.Count} 个审核中的商品，状态变化 {changed} 个" + (failed > 0 ? $"，失败 {failed} 个" : ""));
        return (pending.Count, changed, string.Join("；", notes) + "。");
    }
}
