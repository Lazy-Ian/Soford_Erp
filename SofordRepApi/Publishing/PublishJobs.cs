using System.Threading.Channels;

public enum PublishJobStatus
{
    Queued,
    Running,
    Completed,
    CompletedWithErrors,
    Failed,
    Interrupted
}

public sealed record OperationItemResult(Guid ProductId, string Sku, bool Success, string Message, string? RemoteProductId = null, string? Action = null);

public sealed record BatchResult(int Total, int Succeeded, int Failed, List<OperationItemResult> Items)
{
    public static BatchResult From(List<OperationItemResult> items) => new(items.Count, items.Count(x => x.Success), items.Count(x => !x.Success), items);
}

public sealed class PublishJobRecord
{
    public Guid Id { get; set; }
    public Guid[] ProductIds { get; set; } = [];
    public PublishJobStatus Status { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string Message { get; set; } = "";
    public List<OperationItemResult> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public void Add(OperationItemResult item)
    {
        Items.Add(item);
        Processed++;
        if (item.Success) Succeeded++; else Failed++;
    }
}

public sealed class PublishJobStore(AppPaths paths)
{
    private const int MaxJobs = 500;
    private readonly string _file = paths.File("publish-jobs.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public Task SaveAsync(PublishJobRecord job) => WriteAsync(jobs =>
    {
        var index = jobs.FindIndex(x => x.Id == job.Id);
        if (index >= 0) jobs[index] = job; else jobs.Add(job);
    });

    public async Task<PublishJobRecord?> GetAsync(Guid id) => (await ReadLockedAsync()).FirstOrDefault(x => x.Id == id);

    public async Task<List<PublishJobRecord>> GetLatestAsync(int take) =>
        (await ReadLockedAsync()).OrderByDescending(x => x.CreatedAt).Take(take).ToList();

    /// <summary>Jobs that were queued or running when the process stopped cannot resume; mark them so the user can retry.</summary>
    public Task MarkInterruptedAsync(DateTimeOffset now) => WriteAsync(jobs =>
    {
        foreach (var job in jobs.Where(x => x.Status is PublishJobStatus.Queued or PublishJobStatus.Running))
        {
            job.Status = PublishJobStatus.Interrupted;
            job.Message = "服务重启导致任务中断，请重新发布未完成的商品。";
            job.FinishedAt = now;
        }
    });

    private async Task WriteAsync(Action<List<PublishJobRecord>> change)
    {
        await _lock.WaitAsync();
        try
        {
            var jobs = await ReadAsync();
            change(jobs);
            if (jobs.Count > MaxJobs)
            {
                jobs = jobs.OrderByDescending(x => x.CreatedAt).Take(MaxJobs).OrderBy(x => x.CreatedAt).ToList();
            }

            await JsonFile.WriteAtomicAsync(_file, jobs);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PublishJobRecord>> ReadLockedAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<PublishJobRecord>> ReadAsync()
    {
        try
        {
            return await JsonFile.ReadAsync<List<PublishJobRecord>>(_file) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            // Job history from the previous release has an incompatible shape; history is not critical.
            return [];
        }
    }
}

public sealed class PublishQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid jobId) => _channel.Writer.WriteAsync(jobId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class PublishWorker(
    PublishQueue queue,
    PublishJobStore jobs,
    ProductRepository products,
    ProductOperations operations,
    TimeProvider time,
    ILogger<PublishWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await jobs.MarkInterruptedAsync(time.GetUtcNow());
        await RecoverStuckProductsAsync();

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Publish job {JobId} failed", jobId);
                var job = await jobs.GetAsync(jobId);
                if (job is not null)
                {
                    job.Status = PublishJobStatus.Failed;
                    job.Message = $"任务异常终止：{ex.Message}";
                    job.FinishedAt = time.GetUtcNow();
                    await jobs.SaveAsync(job);
                }
            }
        }
    }

    private async Task RunAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = await jobs.GetAsync(jobId);
        if (job is null || job.Status != PublishJobStatus.Queued)
        {
            return;
        }

        job.Status = PublishJobStatus.Running;
        job.StartedAt = time.GetUtcNow();
        await jobs.SaveAsync(job);

        var done = job.Items.Select(x => x.ProductId).ToHashSet();
        foreach (var productId in job.ProductIds.Where(id => !done.Contains(id)))
        {
            stoppingToken.ThrowIfCancellationRequested();
            job.Add(await operations.PublishAsync(productId));
            await jobs.SaveAsync(job);
        }

        job.Status = job.Failed > 0 ? PublishJobStatus.CompletedWithErrors : PublishJobStatus.Completed;
        job.Message = $"完成：成功 {job.Succeeded}，失败 {job.Failed}。";
        job.FinishedAt = time.GetUtcNow();
        await jobs.SaveAsync(job);
    }

    private async Task RecoverStuckProductsAsync()
    {
        foreach (var product in (await products.GetAllAsync()).Where(x => x.PublishState == PublishState.Publishing))
        {
            await products.UpdateAsync(product.Id, p =>
            {
                p.PublishState = string.IsNullOrWhiteSpace(p.RemoteProductId) ? PublishState.Failed : PublishState.Pending;
                p.RemoteStatusMessage = "发布过程被中断，请重新发布或查询状态。";
            });
        }
    }
}
