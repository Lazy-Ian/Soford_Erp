using System.Collections.Concurrent;
using System.Text.Json;

/// <summary>
/// Serializes Alibaba operations per product so a status poll, a publish and a price sync never interleave
/// their read → call Alibaba → write steps and overwrite each other's results.
/// </summary>
public sealed class ProductLocks
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<IDisposable> AcquireAsync(IEnumerable<Guid> ids)
    {
        // A fixed order keeps multi-product operations from deadlocking each other.
        var ordered = ids.Distinct().OrderBy(x => x).Select(id => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1))).ToList();
        var taken = new List<SemaphoreSlim>();
        try
        {
            foreach (var semaphore in ordered)
            {
                await semaphore.WaitAsync();
                taken.Add(semaphore);
            }
        }
        catch
        {
            taken.ForEach(x => x.Release());
            throw;
        }

        return new Releaser(taken);
    }

    public Task<IDisposable> AcquireAsync(Guid id) => AcquireAsync([id]);

    private sealed class Releaser(List<SemaphoreSlim> held) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) held.ForEach(x => x.Release());
        }
    }
}

/// <summary>Per-product Alibaba operations. Each one persists the outcome on the product and never throws for API errors.</summary>
public sealed class ProductOperations(
    ProductRepository products,
    ProductQualityService quality,
    AlibabaClient alibaba,
    ProductLocks locks,
    TimeProvider time,
    AlibabaTokenService tokens,
    ILogger<ProductOperations> logger)
{
    /// <summary>The account whose authorization acts for this product (assigned, owner, or default).</summary>
    private async Task<Guid?> AccountForAsync(ProductRecord product) =>
        AlibabaTokenService.ResolveForProduct(await tokens.GetAccountsAsync(), product)?.Id;

    public async Task<OperationItemResult> PublishAsync(Guid productId)
    {
        using var _ = await locks.AcquireAsync(productId);
        var product = await products.GetAsync(productId);
        if (product is null)
        {
            return new(productId, "", false, "商品不存在或已删除。");
        }

        var issues = await quality.ApplyAsync(product);
        var blockers = issues.Where(x => x.Severity == QualitySeverity.Blocker).ToList();
        if (blockers.Count > 0)
        {
            await products.UpdateAsync(productId, p =>
            {
                p.QualityIssues = product.QualityIssues;
                p.LocalState = LocalState.Incomplete;
            });
            return new(productId, product.Sku, false, "质检未通过：" + string.Join("；", blockers.Select(x => x.Message.TrimEnd('。'))) + "。");
        }

        var previousState = product.PublishState;
        await products.UpdateAsync(productId, p =>
        {
            p.QualityIssues = product.QualityIssues;
            p.LocalState = LocalState.Ready;
            p.PublishState = PublishState.Publishing;
        });

        var progress = new PublishProgress();
        try
        {
            return await PublishCoreAsync(product, previousState, progress);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Publishing product {ProductId} failed unexpectedly", productId);
            if (progress.Committed)
            {
                // Alibaba accepted the listing and we saved it; only the follow-up status query failed.
                return new(productId, product.Sku, true, $"已提交到 Alibaba，但查询状态出错：{ex.Message}", progress.RemoteId, "publish");
            }

            // Never leave a product stuck in "Publishing" because of an unexpected error.
            await products.UpdateAsync(productId, p =>
            {
                p.PublishState = previousState == PublishState.Publishing ? PublishState.Failed : previousState;
                p.RemoteStatusMessage = $"发布出错：{ex.Message}";
            });
            return new(productId, product.Sku, false, $"发布出错：{ex.Message}", product.RemoteProductId, "publish");
        }
    }

    private sealed class PublishProgress
    {
        public bool Committed { get; set; }
        public string? RemoteId { get; set; }
    }

    private async Task<OperationItemResult> PublishCoreAsync(ProductRecord product, PublishState previousState, PublishProgress progress)
    {
        var productId = product.Id;

        // Guard against duplicate listings: only when an earlier create for THIS product ended without a known outcome
        // (timeout, crash before the ID was saved) do we look for the listing it may have created.
        if (string.IsNullOrWhiteSpace(product.RemoteProductId) && product.CreateAttemptedAt is { } earlierAttempt)
        {
            var existingId = await FindRemoteIdAsync(product, earlierAttempt);
            if (existingId is not null)
            {
                product.RemoteProductId = existingId;
                await products.UpdateAsync(productId, p => p.RemoteProductId = existingId);
            }
        }

        var isUpdate = !string.IsNullOrWhiteSpace(product.RemoteProductId);
        var action = isUpdate ? "update" : "create";
        var sentContent = product.ContentUpdatedAt;
        // New listings are owned by the account that creates them, so the chosen account matters here.
        var accountId = await AccountForAsync(product);
        var attemptAt = time.GetUtcNow();
        if (isUpdate && await CheckSafeToUpdateAsync(product, accountId) is { } refusal)
        {
            await products.UpdateAsync(productId, p =>
            {
                p.PublishState = RestorableState(previousState);
                p.RemoteStatusMessage = $"未更新：{refusal}";
            });
            return new(productId, product.Sku, false, refusal, product.RemoteProductId, action);
        }

        if (!isUpdate)
        {
            await products.UpdateAsync(productId, p => p.CreateAttemptedAt = attemptAt);
        }

        var result = isUpdate
            ? await alibaba.CallAsync("product.update", ListingMapper.BuildUpdatePayload(product), accountId: accountId)
            : await alibaba.CallAsync("product.create", ListingMapper.BuildCreatePayload(product), accountId: accountId);

        string? remoteId = isUpdate ? product.RemoteProductId : null;
        if (!isUpdate && result.Success) remoteId = ExtractProductId(result.Json);

        // A timed-out create may still have succeeded on Alibaba; look it up before calling it a failure.
        if (!isUpdate && (result.ErrorCode == "NetworkError" || (result.Success && string.IsNullOrWhiteSpace(remoteId))))
        {
            remoteId = await FindRemoteIdAsync(product, attemptAt);
            if (remoteId is null)
            {
                await products.UpdateAsync(productId, p =>
                {
                    p.PublishState = PublishState.Failed;
                    p.RemoteStatusMessage = result.Success
                        ? "Alibaba 返回成功但没有商品 ID，也未查到该商品，请在 API 日志中查看原始响应。"
                        : "网络异常，未能确认是否创建成功。重新发布时系统会先按型号查重，不会重复创建。";
                });
                return new(productId, product.Sku, false, result.Success ? "Alibaba 返回成功但没有商品 ID。" : AlibabaErrors.Explain(result), null, action);
            }
        }
        else if (!result.Success)
        {
            await products.UpdateAsync(productId, p =>
            {
                // A failed update leaves the existing Alibaba listing as it was.
                p.PublishState = isUpdate ? RestorableState(previousState) : PublishState.Failed;
                // Alibaba rejected the create outright, so there is no listing to look for next time.
                if (!isUpdate) p.CreateAttemptedAt = null;
                p.RemoteStatusMessage = $"{(isUpdate ? "更新" : "发布")}失败：{result.Describe()}";
            });
            return new(productId, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, action);
        }

        await products.UpdateAsync(productId, p =>
        {
            p.RemoteProductId = remoteId;
            p.PublishState = PublishState.Pending;
            p.RemoteStatus = "pending";
            p.RemoteStatusMessage = isUpdate ? "已提交更新，等待 Alibaba 审核。" : "已提交发布，等待 Alibaba 审核。";
            p.LastPublishedAt = time.GetUtcNow();
            // Edits saved while this call was in flight keep a newer ContentUpdatedAt and stay flagged as unpublished.
            p.PublishedContentAt = sentContent;
            p.CreateAttemptedAt = null;
            // Our own write is now the version on Alibaba; later edits elsewhere will carry a newer timestamp.
            p.RemoteModifiedAt = time.GetUtcNow();
        });
        progress.Committed = true;
        progress.RemoteId = remoteId;

        var status = await RefreshStatusCoreAsync(productId);
        var message = (isUpdate ? "更新成功" : "发布成功") + $"，Alibaba 商品 ID {remoteId}" + (status.Success ? $"，当前状态：{status.Message}" : "");
        return new(productId, product.Sku, true, message, remoteId, action);
    }

    /// <summary>
    /// An update replaces the whole listing, so refuse it when that would destroy something: variants this system
    /// cannot represent, or edits made on Alibaba after the local copy was taken. Returns the reason, or null if safe.
    /// </summary>
    private async Task<string?> CheckSafeToUpdateAsync(ProductRecord product, Guid? accountId)
    {
        var (remote, result) = await ReadRemoteAsync(product, accountId);
        if (remote is null) return $"无法读取 Alibaba 上的当前内容，为避免覆盖已停止更新：{AlibabaErrors.Explain(result)}";

        if (remote.RemoteSkuCount > 1)
        {
            return $"该商品在 Alibaba 上有 {remote.RemoteSkuCount} 个规格，本系统只维护单一价格和库存，整体更新会破坏规格。请在 Alibaba 后台修改，价格可用「同步价格」以外的方式调整。";
        }

        // The local content is based on the version we copied or wrote; the import time stands in for older records.
        var baseline = product.RemoteModifiedAt ?? product.PublishedContentAt;
        if (remote.RemoteModifiedAt is { } changedAt && (baseline is null || changedAt > baseline.Value.Add(ClockTolerance)))
        {
            return $"Alibaba 上的商品在 {changedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm} 被修改过，比本系统的副本新。请先「从 Alibaba 刷新」取回最新内容，再修改和发布。";
        }

        return null;
    }

    /// <summary>
    /// Listings with several variants keep price and stock per variant; writing one listing-level value would overwrite
    /// all of them. Checked live because the stored variant count may predate an edit on Alibaba.
    /// </summary>
    private async Task<string?> VariantRefusalAsync(ProductRecord product, Guid? accountId, string what)
    {
        var (remote, result) = await ReadRemoteAsync(product, accountId);
        if (remote is null) return $"无法读取 Alibaba 上的当前商品，为避免覆盖已停止同步{what}：{AlibabaErrors.Explain(result)}";
        if (remote.RemoteSkuCount != product.RemoteSkuCount) await products.UpdateAsync(product.Id, p => p.RemoteSkuCount = remote.RemoteSkuCount);
        return remote.RemoteSkuCount > 1
            ? $"该商品在 Alibaba 上有 {remote.RemoteSkuCount} 个规格，{what}按规格分别设置，统一同步会覆盖各规格的{what}。请在 Alibaba 后台修改。"
            : null;
    }

    // Alibaba's review may touch the timestamp right after our own write.
    private static readonly TimeSpan ClockTolerance = TimeSpan.FromMinutes(2);

    /// <summary>Reads the listing with product.get and maps it like an imported one.</summary>
    private async Task<(ProductRecord? Remote, AlibabaApiResult Result)> ReadRemoteAsync(ProductRecord product, Guid? accountId)
    {
        var result = await alibaba.CallAsync("product.get", new { product_id = product.RemoteProductId }, accountId: accountId);
        if (!result.Success || AlibabaResponseParser.Find(result.Json!.Value, "product_info") is not { ValueKind: JsonValueKind.Object } info)
        {
            return (null, result);
        }

        return (AlibabaCatalogSync.FromRemote(info, time.GetUtcNow()), result);
    }

    /// <summary>
    /// Replaces the local content with the listing on Alibaba. Local edits that were never published are kept
    /// (the product is skipped) unless <paramref name="discardLocalChanges"/> is set.
    /// </summary>
    public async Task<OperationItemResult> RefreshContentAsync(Guid productId, bool discardLocalChanges)
    {
        using var _ = await locks.AcquireAsync(productId);
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;
        if (product.HasUnpublishedChanges && !discardLocalChanges)
        {
            return new(productId, product.Sku, false, "有未发布的本地修改，已跳过。要放弃本地修改，请用「放弃修改并刷新」。", product.RemoteProductId, "refresh");
        }

        var (remote, result) = await ReadRemoteAsync(product, await AccountForAsync(product));
        if (remote is null) return new(productId, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, "refresh");

        var lookup = await quality.LoadCategoryLookupAsync();
        var now = time.GetUtcNow();
        await products.UpdateAsync(productId, p =>
        {
            p.ApplyRemoteContent(remote);
            p.RemoteStatusMessage = remote.RemoteStatusMessage?.Replace("从 Alibaba 导入", "已从 Alibaba 刷新");
            p.ContentRefreshedAt = now;
            p.ContentUpdatedAt = now;
            p.PublishedContentAt = now;
            p.LastSyncedAt = now;
            ProductQualityService.Apply(p, lookup(p.CategoryId));
        });
        var variants = remote.RemoteSkuCount > 1 ? $"，有 {remote.RemoteSkuCount} 个规格（不能在本系统整体更新）" : "";
        return new(productId, product.Sku, true, $"已取回 Alibaba 最新内容{variants}", product.RemoteProductId, "refresh");
    }

    private const int MaxLookupPages = 15;

    private static PublishState RestorableState(PublishState state) => state == PublishState.Publishing ? PublishState.Pending : state;

    /// <summary>
    /// Finds the listing a create attempt of this product may have produced: same model number, not already linked to
    /// another local product, and (when Alibaba reports it) created no earlier than the attempt. Older or foreign listings
    /// that merely share a model number are never adopted.
    /// </summary>
    private async Task<string?> FindRemoteIdAsync(ProductRecord product, DateTimeOffset attemptAt)
    {
        var modelNumber = ListingMapper.RemoteModelNumber(product);
        if (string.IsNullOrWhiteSpace(modelNumber)) return null;
        var accountId = await AccountForAsync(product);

        // Real stores reuse model numbers across dozens of listings and search results are not ordered by
        // creation time, so walk the pages (bounded) instead of trusting page 1.
        var items = new List<JsonElement>();
        for (var page = 1; page <= MaxLookupPages; page++)
        {
            var result = await alibaba.CallAsync("product.search", new { page_index = page, page_size = AlibabaCatalogSync.PageSize, model_number = modelNumber }, accountId: accountId);
            if (!result.Success || result.Json is null) return null;
            if (AlibabaResponseParser.Find(result.Json.Value, "product_info") is not { ValueKind: JsonValueKind.Array } pageItems || pageItems.GetArrayLength() == 0) break;
            items.AddRange(pageItems.EnumerateArray().Select(x => x.Clone()));
            var totalPages = int.TryParse(AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json.Value, "total_page")), out var parsed) ? parsed : 1;
            if (page >= totalPages) break;
        }

        var linkedElsewhere = (await products.GetAllAsync())
            .Where(x => x.Id != product.Id && !string.IsNullOrWhiteSpace(x.RemoteProductId))
            .Select(x => x.RemoteProductId!)
            .ToHashSet();
        var earliest = attemptAt.AddMinutes(-10).ToUnixTimeMilliseconds();

        var candidates = items
            .Select(item => item.TryGetProperty("basic_info", out var basic) ? basic : default)
            .Where(basic => string.Equals(AlibabaResponseParser.Text(basic, "model_number"), modelNumber, StringComparison.OrdinalIgnoreCase))
            .Select(basic => (Id: AlibabaResponseParser.Text(basic, "product_id"), Created: AlibabaResponseParser.ReadLong(basic, "create_timestamp")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !linkedElsewhere.Contains(x.Id!))
            // create_timestamp may be seconds or milliseconds; normalise to milliseconds. 0 means Alibaba did not report it.
            .Select(x => (x.Id, Created: x.Created is > 0 and < 100_000_000_000 ? x.Created * 1000 : x.Created))
            .Where(x => x.Created == 0 || x.Created >= earliest)
            .ToList();

        // Ambiguity is resolved by refusing to guess: two unlinked candidates means we cannot tell which is ours.
        return candidates.Count == 1 ? candidates[0].Id : null;
    }

    public async Task<OperationItemResult> RefreshStatusAsync(Guid productId)
    {
        using var _ = await locks.AcquireAsync(productId);
        return await RefreshStatusCoreAsync(productId);
    }

    private async Task<OperationItemResult> RefreshStatusCoreAsync(Guid productId)
    {
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        var accountId = await AccountForAsync(product);
        var result = await alibaba.CallAsync("product.status", ListingMapper.BuildStatusPayload(product), accountId: accountId);
        string status;
        string? description;
        PublishState state;
        string label;
        if (result.Success)
        {
            status = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "status"))?.ToLowerInvariant() ?? "";
            description = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "status_desc"));
            (state, label) = MapStatus(status, product.PublishState);
        }
        else
        {
            // status/get/v2 only knows listings submitted through the API ("Product not found." for everything created
            // in the seller backend), so fall back to the listing itself: its status and audit_status.
            var (remote, detail) = await ReadRemoteAsync(product, accountId);
            if (remote is null)
            {
                return new(productId, product.Sku, false, AlibabaErrors.Explain(detail.Success ? result : detail), product.RemoteProductId, "status");
            }

            status = remote.RemoteStatus ?? "";
            description = null;
            (state, label) = (remote.PublishState, remote.RemoteStatusMessage?.Replace("从 Alibaba 导入：", "") ?? "");
        }

        await products.UpdateAsync(productId, p =>
        {
            p.RemoteStatus = status;
            p.PublishState = state;
            p.RemoteStatusMessage = string.IsNullOrWhiteSpace(description) ? label : $"{label}：{description}";
            p.LastSyncedAt = time.GetUtcNow();
        });
        return new(productId, product.Sku, true, string.IsNullOrWhiteSpace(description) ? label : $"{label}（{description}）", product.RemoteProductId, "status");
    }

    public async Task<OperationItemResult> SyncInventoryAsync(Guid productId)
    {
        using var _ = await locks.AcquireAsync(productId);
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        if (product.Stock <= 0)
        {
            // Most B2B listings do not track stock; pushing 0 would mark a live listing as out of stock.
            return new(productId, product.Sku, false, "本地库存为 0，为避免把 Alibaba 上的库存清零，已跳过。请先在商品中填写实际库存。", product.RemoteProductId, "inventory");
        }

        var accountId = await AccountForAsync(product);
        if (await VariantRefusalAsync(product, accountId, "库存") is { } refusal) return new(productId, product.Sku, false, refusal, product.RemoteProductId, "inventory");
        var result = await alibaba.CallAsync("product.inventory.update", ListingMapper.BuildInventoryPayload(product), accountId: accountId);
        return await RecordSyncAsync(product, result, "inventory", $"库存已同步为 {product.Stock}");
    }

    public async Task<OperationItemResult> SyncPriceAsync(Guid productId)
    {
        using var _ = await locks.AcquireAsync(productId);
        var (product, error) = await LoadPublishedAsync(productId);
        if (product is null) return error!;

        if (!string.Equals(product.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return new(productId, product.Sku, false, "Alibaba 改价接口只支持 USD，请把币种改为 USD 后再同步。", product.RemoteProductId, "price");
        }

        var tiers = string.Join("，", ListingMapper.EffectiveTiers(product).Select(x => $"≥{x.Quantity}: ${x.Price}"));
        var accountId = await AccountForAsync(product);
        if (await VariantRefusalAsync(product, accountId, "价格") is { } refusal) return new(productId, product.Sku, false, refusal, product.RemoteProductId, "price");
        var result = await alibaba.CallAsync("product.price.update", ListingMapper.BuildPricePayload(product), accountId: accountId);
        return await RecordSyncAsync(product, result, "price", $"价格已同步（{tiers}）");
    }

    public async Task<List<OperationItemResult>> SetOnlineAsync(IReadOnlyList<Guid> productIds, bool online)
    {
        using var _ = await locks.AcquireAsync(productIds);
        var items = new List<OperationItemResult>();
        var eligible = new List<ProductRecord>();
        foreach (var id in productIds)
        {
            var (product, error) = await LoadPublishedAsync(id);
            if (product is null) items.Add(error!);
            else eligible.Add(product);
        }

        if (eligible.Count == 0) return items;

        var action = online ? "online" : "offline";
        // Sub-accounts may only change their own listings, so each account sends its own batch.
        var accounts = await tokens.GetAccountsAsync();
        foreach (var group in eligible.GroupBy(x => AlibabaTokenService.ResolveForProduct(accounts, x)?.Id))
        {
            items.AddRange(await SetOnlineForAccountAsync(group.ToList(), online, action, group.Key));
        }

        return items;
    }

    private async Task<List<OperationItemResult>> SetOnlineForAccountAsync(List<ProductRecord> eligible, bool online, string action, Guid? accountId)
    {
        var items = new List<OperationItemResult>();
        var result = await alibaba.CallAsync("product.status.update", ListingMapper.BuildOnlineOfflinePayload(eligible, online), accountId: accountId);
        var updatedIds = ReadIdList(result.Json, "updated_product_ids");
        var errorById = ReadStatusErrors(result.Json);
        foreach (var product in eligible)
        {
            var ok = updatedIds.Contains(product.RemoteProductId!) || (result.Success && updatedIds.Count == 0 && errorById.Count == 0);
            if (ok)
            {
                await products.UpdateAsync(product.Id, p =>
                {
                    p.PublishState = online ? PublishState.Online : PublishState.Offline;
                    p.RemoteStatus = action;
                    p.RemoteStatusMessage = online ? "已上架" : "已下架";
                    p.LastSyncedAt = time.GetUtcNow();
                });
                items.Add(new(product.Id, product.Sku, true, online ? "已上架" : "已下架", product.RemoteProductId, action));
            }
            else
            {
                var reason = errorById.TryGetValue(product.RemoteProductId!, out var message) ? message : AlibabaErrors.Explain(result);
                items.Add(new(product.Id, product.Sku, false, reason, product.RemoteProductId, action));
            }
        }

        return items;
    }

    public async Task<OperationItemResult> PredictCategoryAsync(Guid productId)
    {
        using var _ = await locks.AcquireAsync(productId);
        var product = await products.GetAsync(productId);
        if (product is null) return new(productId, "", false, "商品不存在。");
        if (string.IsNullOrWhiteSpace(product.Title)) return new(productId, product.Sku, false, "请先填写商品标题。");

        var result = await alibaba.CallAsync("category.predict", new
        {
            title = product.Title,
            description = string.IsNullOrWhiteSpace(product.Description) ? null : AlibabaResponseParser.Truncate(product.Description, 1000),
            image = product.Images.FirstOrDefault()
        });
        if (!result.Success) return new(productId, product.Sku, false, AlibabaErrors.Explain(result), null, "predict");

        var categoryId = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_id"));
        if (string.IsNullOrWhiteSpace(categoryId)) return new(productId, product.Sku, false, "Alibaba 未返回推荐类目。", null, "predict");

        var name = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_name")) ?? "";
        var path = AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(result.Json!.Value, "category_path")) ?? "";
        var lookup = await quality.LoadCategoryLookupAsync();
        await products.UpdateAsync(productId, p =>
        {
            p.CategoryId = categoryId;
            p.CategoryName = name;
            p.CategoryPath = path;
            p.ContentUpdatedAt = time.GetUtcNow();
            ProductQualityService.Apply(p, lookup(categoryId));
        });

        return new(productId, product.Sku, true, $"{categoryId} {(path.Length > 0 ? path : name)}", null, "predict");
    }

    private async Task<(ProductRecord? Product, OperationItemResult? Error)> LoadPublishedAsync(Guid productId)
    {
        var product = await products.GetAsync(productId);
        if (product is null) return (null, new(productId, "", false, "商品不存在。"));
        if (string.IsNullOrWhiteSpace(product.RemoteProductId) || !long.TryParse(product.RemoteProductId, out _))
        {
            return (null, new(productId, product.Sku, false, "该商品尚未发布到 Alibaba，请先发布。"));
        }

        if (product.PublishState == PublishState.Publishing)
        {
            return (null, new(productId, product.Sku, false, "该商品正在发布中，请稍后再试。", product.RemoteProductId));
        }

        return (product, null);
    }

    private async Task<OperationItemResult> RecordSyncAsync(ProductRecord product, AlibabaApiResult result, string action, string successMessage)
    {
        if (!result.Success) return new(product.Id, product.Sku, false, AlibabaErrors.Explain(result), product.RemoteProductId, action);

        await products.UpdateAsync(product.Id, p => p.LastSyncedAt = time.GetUtcNow());
        return new(product.Id, product.Sku, true, successMessage, product.RemoteProductId, action);
    }

    /// <summary>State of a listing read from product.get / product.search: a rejected audit wins over online/offline.</summary>
    public static (PublishState State, string Label) MapRemote(string status, string? auditStatus, PublishState current) =>
        auditStatus?.ToLowerInvariant() switch
        {
            "rejected" or "failed" => (PublishState.Failed, "审核未通过"),
            "auditing" or "pending" or "wait_audit" => (PublishState.Pending, "审核中"),
            _ => MapStatus(status, current)
        };

    public static (PublishState State, string Label) MapStatus(string status, PublishState current) => status switch
    {
        "online" => (PublishState.Online, "已上架"),
        "pending" => (PublishState.Pending, "审核中"),
        "draft" => (PublishState.Pending, "Alibaba 草稿"),
        "failed" => (PublishState.Failed, "审核未通过"),
        "offline" => (PublishState.Offline, "已下架"),
        "" => (current, "未知状态"),
        _ => (current, status)
    };

    public static string? ExtractProductId(JsonElement? json)
    {
        if (json is null) return null;
        if (json.Value.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            && AlibabaResponseParser.ScalarText(result.TryGetProperty("data", out var data) ? data : null) is { Length: > 0 } fromResult)
        {
            return fromResult;
        }

        return AlibabaResponseParser.ScalarText(AlibabaResponseParser.Find(json.Value, "product_id"));
    }

    private static HashSet<string> ReadIdList(JsonElement? json, string name)
    {
        var set = new HashSet<string>();
        if (json is not null && AlibabaResponseParser.Find(json.Value, name) is { ValueKind: JsonValueKind.Array } list)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (AlibabaResponseParser.ScalarText(item) is { } id) set.Add(id);
            }
        }

        return set;
    }

    private static Dictionary<string, string> ReadStatusErrors(JsonElement? json)
    {
        var result = new Dictionary<string, string>();
        if (json is null || AlibabaResponseParser.Find(json.Value, "errors") is not { ValueKind: JsonValueKind.Array } errors) return result;

        foreach (var error in errors.EnumerateArray())
        {
            var id = error.TryGetProperty("product_id", out var productId) ? AlibabaResponseParser.ScalarText(productId) : null;
            if (id is null) continue;
            var messages = error.TryGetProperty("errors", out var details) && details.ValueKind == JsonValueKind.Array
                ? details.EnumerateArray().Select(x => $"{AlibabaResponseParser.Text(x, "msg_code")} {AlibabaResponseParser.Text(x, "message")}".Trim())
                : [];
            result[id] = string.Join("；", messages);
        }

        return result;
    }
}

/// <summary>Human-readable explanations for the Alibaba error codes operators meet most often.</summary>
public static class AlibabaErrors
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NotConfigured"] = "未配置 AppKey / AppSecret",
        ["NotAuthorized"] = "店铺未授权或授权已失效",
        ["IllegalAccessToken"] = "授权已失效，请在「店铺连接」重新授权",
        ["IncompleteSignature"] = "签名不正确，请检查 AppSecret",
        ["InvalidAppKey"] = "AppKey 无效",
        ["AppCallLimit"] = "调用频率超限，请稍后重试",
        ["InsufficientPermission"] = "应用没有该接口权限，请在 App Console 申请",
        ["InsufficientIsvPermissions"] = "应用没有该接口权限，请在 App Console 申请",
        ["NetworkError"] = "网络异常，无法连接 Alibaba",
        ["ISP"] = "Alibaba 服务返回错误",
        ["ISV"] = "请求参数或权限有误",
        ["ServiceTimeout"] = "Alibaba 服务超时，请稍后重试",
        ["B_PRODUCT_PARAM_INVALID"] = "商品参数不合法",
        ["B_TITLE_NOT_FOUND"] = "缺少标题",
        ["B_PRICE_ALL_NULL"] = "价格为空",
        ["B_PRICE_SEQUENCE_INVALID"] = "阶梯价顺序不正确（数量递增、价格递减）",
        ["B_PRICE_SCALE_INVALID"] = "价格小数位超过两位",
        ["B_CONTAINS_INVALID_IMAGE"] = "图片链接无效",
        ["B_IMAGE_UPLOAD_FAILED"] = "图片全部上传失败，请检查图片链接是否可公开访问",
        ["B_KEYWORD_NOT_FOUND"] = "缺少关键词"
    };

    public static string Explain(AlibabaApiResult result)
    {
        if (result.Success) return "OK";
        var hint = result.ErrorCode is not null && Known.TryGetValue(result.ErrorCode, out var text) ? text : null;
        var detail = string.Join(" ", new[] { result.ErrorCode, result.ErrorMessage }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return hint is null ? detail : $"{hint}（{detail}）";
    }
}
