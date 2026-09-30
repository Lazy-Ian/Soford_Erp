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

/// <summary>What a background job does with each product. Jobs from earlier releases have no kind and are publishes.</summary>
public static class JobKinds
{
    public const string Publish = "publish";
    public const string Status = "status";
    public const string Inventory = "inventory";
    public const string Price = "price";
    public const string Online = "online";
    public const string Offline = "offline";
    public const string Predict = "predict";
    public const string Refresh = "refresh";
    public const string RefreshDiscard = "refresh-discard";

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        [Publish] = "发布",
        [Status] = "同步状态",
        [Inventory] = "同步库存",
        [Price] = "同步价格",
        [Online] = "上架",
        [Offline] = "下架",
        [Predict] = "预测类目",
        [Refresh] = "从 Alibaba 刷新",
        [RefreshDiscard] = "放弃修改并刷新"
    };
}

public sealed class PublishJobRecord
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = JobKinds.Publish;
    public string? CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
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
    private const int MaxJobs = 200;
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
            job.Message = "服务重启导致任务中断，请对未完成的商品重新操作。";
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
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
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
    ProductQualityService quality,
    TimeProvider time,
    ILogger<PublishWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await jobs.MarkInterruptedAsync(time.GetUtcNow());
            await RecoverStuckProductsAsync();
            // Quality results are stored on the product; recompute them so rule changes in a release apply to everything.
            var lookup = await quality.LoadCategoryLookupAsync();
            await products.UpdateManyAsync((await products.GetAllAsync()).Select(x => x.Id), p => ProductQualityService.Apply(p, lookup(p.CategoryId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A damaged job file must not stop the host; new jobs still run.
            logger.LogError(ex, "Recovering interrupted jobs failed");
        }

        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(jobId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Job {JobId} failed", jobId);
                try
                {
                    var job = await jobs.GetAsync(jobId);
                    if (job is not null)
                    {
                        job.Status = PublishJobStatus.Failed;
                        job.Message = $"任务异常终止：{ex.Message}";
                        job.FinishedAt = time.GetUtcNow();
                        await jobs.SaveAsync(job);
                    }
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    logger.LogError(inner, "Recording failure of job {JobId} failed", jobId);
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
        var pending = job.ProductIds.Where(id => !done.Contains(id)).ToList();
        var lastSave = time.GetUtcNow();
        // Online/offline takes a list per call; everything else is one product per call.
        var size = job.Kind is JobKinds.Online or JobKinds.Offline ? OnlineChunkSize : 1;
        foreach (var chunk in pending.Chunk(size))
        {
            stoppingToken.ThrowIfCancellationRequested();
            List<OperationItemResult> items;
            try
            {
                items = await RunItemsAsync(job.Kind, chunk);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad product must not abort the rest of the batch.
                logger.LogError(ex, "{Kind} of {ProductIds} in job {JobId} failed", job.Kind, chunk, jobId);
                items = chunk.Select(id => new OperationItemResult(id, "", false, $"处理出错：{ex.Message}")).ToList();
            }

            items.ForEach(job.Add);
            // Saving after every item rewrites the job file hundreds of times on a large batch; a few seconds of lag is fine.
            if (time.GetUtcNow() - lastSave > SaveInterval)
            {
                await jobs.SaveAsync(job);
                lastSave = time.GetUtcNow();
            }
        }

        job.Status = job.Failed > 0 ? PublishJobStatus.CompletedWithErrors : PublishJobStatus.Completed;
        job.Message = $"完成：成功 {job.Succeeded}，失败 {job.Failed}。";
        job.FinishedAt = time.GetUtcNow();
        await jobs.SaveAsync(job);
    }

    private const int OnlineChunkSize = 20;
    private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(2);

    private async Task<List<OperationItemResult>> RunItemsAsync(string kind, Guid[] ids) => kind switch
    {
        JobKinds.Online => await operations.SetOnlineAsync(ids, true),
        JobKinds.Offline => await operations.SetOnlineAsync(ids, false),
        JobKinds.Status => [await operations.RefreshStatusAsync(ids[0])],
        JobKinds.Inventory => [await operations.SyncInventoryAsync(ids[0])],
        JobKinds.Price => [await operations.SyncPriceAsync(ids[0])],
        JobKinds.Predict => [await operations.PredictCategoryAsync(ids[0])],
        JobKinds.Refresh => [await operations.RefreshContentAsync(ids[0], discardLocalChanges: false)],
        JobKinds.RefreshDiscard => [await operations.RefreshContentAsync(ids[0], discardLocalChanges: true)],
        _ => [await operations.PublishAsync(ids[0])]
    };

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
