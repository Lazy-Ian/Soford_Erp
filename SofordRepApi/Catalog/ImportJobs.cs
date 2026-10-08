using System.Threading.Channels;
using ClosedXML.Excel;

public static class ImportModes
{
    public const string Upsert = "upsert";
    public const string CreateOnly = "create-only";
    public const string UpdateOnly = "update-only";
    public const string StockPrice = "stock-price";

    private static readonly HashSet<string> Values = new(StringComparer.OrdinalIgnoreCase) { Upsert, CreateOnly, UpdateOnly, StockPrice };
    private static readonly HashSet<string> StockPriceFields = new(StringComparer.OrdinalIgnoreCase) { "Currency", "Price", "TieredPrices", "Stock" };

    public static bool IsValid(string? mode) => mode is not null && Values.Contains(mode);

    public static IReadOnlySet<string> PresentFields(ImportRow row, string mode) =>
        mode == StockPrice ? row.Present.Where(StockPriceFields.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase) : row.Present;
}

public enum ImportJobStatus
{
    Previewed,
    Queued,
    Running,
    Completed,
    CompletedWithWarnings,
    Failed,
    Interrupted
}

public sealed record ImportPreviewItem(int RowNumber, string Key, string Action, string[] Fields, string? Message = null);

public sealed class ImportJobRecord
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string Mode { get; set; } = ImportModes.Upsert;
    public string CreatedBy { get; set; } = "";
    public string CreatedByName { get; set; } = "";
    public ImportJobStatus Status { get; set; } = ImportJobStatus.Previewed;
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int WarningCount { get; set; }
    public string[] Warnings { get; set; } = [];
    public string[] Headers { get; set; } = [];
    public string[] UnknownHeaders { get; set; } = [];
    public List<ImportPreviewItem> Preview { get; set; } = [];
    public string Message { get; set; } = "等待确认";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed record ImportPayloadRow(int RowNumber, ProductDraft Draft, string[] Present, string? RemoteProductId)
{
    public ImportRow ToRow() => new(RowNumber, Draft, Present.ToHashSet(StringComparer.OrdinalIgnoreCase), RemoteProductId);
}

public sealed class ImportJobPayload
{
    public List<ImportPayloadRow> Rows { get; set; } = [];
    public int ParsedSkipped { get; set; }
    public List<string> ParseWarnings { get; set; } = [];
    public List<string> PlanWarnings { get; set; } = [];
    public List<string> ExecutionWarnings { get; set; } = [];
}

public sealed record ImportPlan(int Created, int Updated, int Skipped, List<string> Warnings, List<ImportPreviewItem> Preview);

public sealed class ProductImportPlanner(ProductRepository products)
{
    private const int MaxPreviewItems = 200;

    public async Task<ImportPlan> PlanAsync(ImportParseResult parsed, string mode, Func<ProductRecord, bool> canEdit, string actor)
    {
        var all = await products.GetAllAsync();
        var bySku = all.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var byRemoteId = all.Where(x => !string.IsNullOrWhiteSpace(x.RemoteProductId)).GroupBy(x => x.RemoteProductId!)
            .ToDictionary(x => x.Key, x => x.First());
        var warnings = new List<string>(parsed.Warnings);
        var preview = new List<ImportPreviewItem>();
        int created = 0, updated = 0, skipped = parsed.Skipped;

        foreach (var row in parsed.Rows)
        {
            var sku = row.Draft.Sku?.Trim() ?? "";
            ProductRecord? existing;
            if (row.RemoteProductId is { } remoteId)
            {
                if (!byRemoteId.TryGetValue(remoteId, out existing))
                {
                    AddSkip(row, sku, $"本系统没有 Alibaba 商品 ID 为 {remoteId} 的商品，请先从 Alibaba 导入。", warnings, preview);
                    skipped++;
                    continue;
                }
            }
            else
            {
                bySku.TryGetValue(sku, out existing);
            }

            if (existing is not null && !canEdit(existing))
            {
                AddSkip(row, sku, "商品不属于你负责的账号。", warnings, preview);
                skipped++;
                continue;
            }

            if (existing is not null && mode == ImportModes.CreateOnly)
            {
                AddSkip(row, sku, "仅新增模式下不会修改已有商品。", warnings, preview);
                skipped++;
                continue;
            }

            if (existing is null && mode is ImportModes.UpdateOnly or ImportModes.StockPrice)
            {
                AddSkip(row, sku, mode == ImportModes.StockPrice ? "库存价格模式不会新建商品。" : "仅更新模式下找不到已有商品。", warnings, preview);
                skipped++;
                continue;
            }

            var present = ImportModes.PresentFields(row, mode);
            if (mode == ImportModes.StockPrice && present.Count == 0)
            {
                AddSkip(row, sku, "文件中没有库存或价格列。", warnings, preview);
                skipped++;
                continue;
            }

            if (existing is null)
            {
                var simulated = new ProductRecord { Id = Guid.NewGuid(), CreatedBy = actor, UpdatedBy = actor };
                simulated.ApplyDraft(row.Draft);
                all.Add(simulated);
                if (sku.Length > 0) bySku[sku] = simulated;
                created++;
                AddPreview(preview, new(row.RowNumber, sku, "Create", present.Order().ToArray()));
            }
            else
            {
                var changed = ChangedFields(existing, row.Draft, present);
                existing.ApplyImportedFields(row.Draft, present);
                updated++;
                AddPreview(preview, new(row.RowNumber, existing.Sku, "Update", changed));
            }
        }

        return new(created, updated, skipped, warnings, preview);
    }

    private static string[] ChangedFields(ProductRecord existing, ProductDraft draft, IReadOnlySet<string> present)
    {
        var after = existing.Copy();
        after.ApplyImportedFields(draft, present);
        return present.Where(field => FieldValue(existing, field) != FieldValue(after, field)).Order().ToArray();
    }

    private static string FieldValue(ProductRecord product, string field) => field switch
    {
        "Sku" => product.Sku,
        "Title" => product.Title,
        "Description" => product.Description,
        "Keywords" => string.Join('\n', product.Keywords),
        "BrandName" => product.BrandName,
        "ModelNumber" => product.ModelNumber,
        "Language" => product.Language,
        "CategoryId" => product.CategoryId,
        "CategoryName" => product.CategoryName,
        "Attributes" => string.Join('|', product.Attributes.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}")),
        "Currency" => product.Currency,
        "Price" => product.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "TieredPrices" => string.Join('|', product.TieredPrices.Select(x => $"{x.Quantity}:{x.Price}")),
        "MOQ" => product.MinimumOrderQuantity.ToString(),
        "Unit" => product.Unit,
        "Stock" => product.Stock.ToString(),
        "LeadTimeDays" => product.LeadTimeDays.ToString(),
        "ShippingTemplateId" => product.ShippingTemplateId,
        "WeightKg" => product.WeightKg?.ToString() ?? "",
        "LengthCm" => product.LengthCm?.ToString() ?? "",
        "WidthCm" => product.WidthCm?.ToString() ?? "",
        "HeightCm" => product.HeightCm?.ToString() ?? "",
        "Images" or "MainImageUrl" or "DetailImageUrls" => string.Join('|', product.Images),
        _ => ""
    };

    private static void AddSkip(ImportRow row, string sku, string message, List<string> warnings, List<ImportPreviewItem> preview)
    {
        var key = sku.Length > 0 ? sku : row.RemoteProductId ?? $"第 {row.RowNumber} 行";
        warnings.Add($"第 {row.RowNumber} 行（{key}）：{message}");
        AddPreview(preview, new(row.RowNumber, key, "Skip", [], message));
    }

    private static void AddPreview(List<ImportPreviewItem> preview, ImportPreviewItem item)
    {
        if (preview.Count < MaxPreviewItems) preview.Add(item);
    }
}

public sealed class ImportJobStore(AppPaths paths, TimeProvider time)
{
    private const int MaxJobs = 50;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly string _directory = paths.File("import-jobs");
    private readonly string _indexFile = paths.File("import-jobs", "index.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task SaveNewAsync(ImportJobRecord job, ImportJobPayload payload)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(_directory);
            await JsonFile.WriteAtomicAsync(PayloadFile(job.Id), payload, JsonFile.Compact);
            var jobs = await ReadUnsafeAsync();
            jobs.Add(job);
            await TrimAndWriteUnsafeAsync(jobs);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ImportJobRecord?> GetAsync(Guid id) => (await GetLatestAsync(MaxJobs)).FirstOrDefault(x => x.Id == id);

    public async Task<List<ImportJobRecord>> GetLatestAsync(int take)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadUnsafeAsync()).OrderByDescending(x => x.CreatedAt).Take(take).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ImportJobPayload?> GetPayloadAsync(Guid id)
    {
        await _lock.WaitAsync();
        try { return await JsonFile.ReadAsync<ImportJobPayload>(PayloadFile(id)); }
        finally { _lock.Release(); }
    }

    public async Task SavePayloadAsync(Guid id, ImportJobPayload payload)
    {
        await _lock.WaitAsync();
        try { await JsonFile.WriteAtomicAsync(PayloadFile(id), payload, JsonFile.Compact); }
        finally { _lock.Release(); }
    }

    public async Task<ImportJobRecord?> UpdateAsync(Guid id, Action<ImportJobRecord> change)
    {
        await _lock.WaitAsync();
        try
        {
            var jobs = await ReadUnsafeAsync();
            var job = jobs.FirstOrDefault(x => x.Id == id);
            if (job is null) return null;
            change(job);
            await TrimAndWriteUnsafeAsync(jobs);
            return job;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<Guid>> RecoverAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var jobs = await ReadUnsafeAsync();
            foreach (var job in jobs.Where(x => x.Status == ImportJobStatus.Running))
            {
                job.Status = ImportJobStatus.Interrupted;
                job.Message = "服务重启导致任务中断，可以重新确认执行。";
                job.FinishedAt = time.GetUtcNow();
            }

            await TrimAndWriteUnsafeAsync(jobs);
            return jobs.Where(x => x.Status == ImportJobStatus.Queued).Select(x => x.Id).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public byte[] BuildErrorWorkbook(ImportJobRecord job, ImportJobPayload payload)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = "File"; summary.Cell(1, 2).Value = Safe(job.FileName);
        summary.Cell(2, 1).Value = "Status"; summary.Cell(2, 2).Value = job.Status.ToString();
        summary.Cell(3, 1).Value = "Created"; summary.Cell(3, 2).Value = job.Created;
        summary.Cell(4, 1).Value = "Updated"; summary.Cell(4, 2).Value = job.Updated;
        summary.Cell(5, 1).Value = "Skipped"; summary.Cell(5, 2).Value = job.Skipped;
        summary.Columns().AdjustToContents();

        var issues = workbook.AddWorksheet("Issues");
        issues.Cell(1, 1).Value = "No.";
        issues.Cell(1, 2).Value = "Issue";
        var warnings = payload.ParseWarnings.Concat(payload.PlanWarnings).Concat(payload.ExecutionWarnings).Distinct().ToList();
        for (var i = 0; i < warnings.Count; i++)
        {
            issues.Cell(i + 2, 1).Value = i + 1;
            issues.Cell(i + 2, 2).Value = Safe(warnings[i]);
        }

        issues.Column(1).Width = 10;
        issues.Column(2).Width = 120;
        issues.SheetView.FreezeRows(1);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task<List<ImportJobRecord>> ReadUnsafeAsync() => await JsonFile.ReadAsync<List<ImportJobRecord>>(_indexFile) ?? [];

    private async Task TrimAndWriteUnsafeAsync(List<ImportJobRecord> jobs)
    {
        var cutoff = time.GetUtcNow() - Retention;
        var removed = jobs.Where(x => x.CreatedAt < cutoff).Concat(jobs.OrderByDescending(x => x.CreatedAt).Skip(MaxJobs)).DistinctBy(x => x.Id).ToList();
        jobs.RemoveAll(x => removed.Any(old => old.Id == x.Id));
        foreach (var old in removed)
        {
            try { File.Delete(PayloadFile(old.Id)); } catch (IOException) { }
        }

        await JsonFile.WriteAtomicAsync(_indexFile, jobs.OrderBy(x => x.CreatedAt).ToList());
    }

    private string PayloadFile(Guid id) => Path.Combine(_directory, $"{id:N}.json");
    private static string Safe(string value) => value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
}

public sealed class ImportQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();
    public ValueTask EnqueueAsync(Guid id) => _channel.Writer.WriteAsync(id);
    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) => _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class ImportWorker(
    ImportQueue queue,
    ImportJobStore jobs,
    ProductRepository products,
    ProductQualityService quality,
    AccessService access,
    AuditLog audit,
    TimeProvider time,
    ILogger<ImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            foreach (var id in await jobs.RecoverAsync()) await queue.EnqueueAsync(id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Recovering import jobs failed; new jobs can still run");
        }

        await foreach (var id in queue.ReadAllAsync(stoppingToken))
        {
            try { await RunAsync(id, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Import job {JobId} failed", id);
                try
                {
                    await jobs.UpdateAsync(id, job =>
                    {
                        job.Status = ImportJobStatus.Failed;
                        job.Message = $"导入失败：{ex.Message}";
                        job.FinishedAt = time.GetUtcNow();
                    });
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    logger.LogError(inner, "Recording failure of import job {JobId} failed", id);
                }
            }
        }
    }

    private async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id);
        if (job is null || job.Status != ImportJobStatus.Queued) return;
        var payload = await jobs.GetPayloadAsync(id) ?? throw new InvalidDataException("导入暂存数据不存在。");
        var user = await access.FindAsync(job.CreatedBy);
        if (user is null) throw new InvalidOperationException("提交导入的用户已停用或删除。");

        await jobs.UpdateAsync(id, current =>
        {
            current.Status = ImportJobStatus.Running;
            current.StartedAt = time.GetUtcNow();
            current.FinishedAt = null;
            current.Message = "正在导入并自动质检";
        });

        cancellationToken.ThrowIfCancellationRequested();
        var executionWarnings = new List<string>();
        var lookup = await quality.LoadCategoryLookupAsync();
        var visible = await access.VisibleAsync(user);
        var result = await products.ImportAsync(payload.Rows.Select(x => x.ToRow()), p => ProductQualityService.Apply(p, lookup(p.CategoryId)), executionWarnings,
            visible, user.Id, job.Mode);
        payload.ExecutionWarnings = executionWarnings;
        await jobs.SavePayloadAsync(id, payload);
        var warningCount = payload.ParseWarnings.Count + executionWarnings.Count;
        await jobs.UpdateAsync(id, current =>
        {
            current.Status = warningCount > 0 ? ImportJobStatus.CompletedWithWarnings : ImportJobStatus.Completed;
            current.Processed = current.Total;
            current.Created = result.Created;
            current.Updated = result.Updated;
            current.Skipped = payload.ParsedSkipped + executionWarnings.Count;
            current.WarningCount = warningCount;
            current.Warnings = payload.ParseWarnings.Concat(executionWarnings).Take(200).ToArray();
            current.Message = $"完成：新增 {result.Created}，更新 {result.Updated}，跳过 {current.Skipped}。";
            current.FinishedAt = time.GetUtcNow();
        });
        try
        {
            await audit.RecordAsync(user, "导入表格", $"{job.FileName}：新增 {result.Created}，更新 {result.Updated}，跳过 {payload.ParsedSkipped + executionWarnings.Count}", result.Created + result.Updated);
        }
        catch (Exception ex)
        {
            // The durable import result is authoritative; an audit-log write failure must not make a successful import retryable.
            logger.LogError(ex, "Recording audit entry for import job {JobId} failed", id);
        }
    }
}
