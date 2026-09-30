public sealed class SkuConflictException(string sku) : Exception($"SKU '{sku}' 已存在。")
{
    public string Sku { get; } = sku;
}

/// <summary>
/// JSON-file product store, kept in memory after the first read (the file is tens of MB once Alibaba descriptions
/// are imported). Every read returns fresh copies, so callers never share mutable state.
/// </summary>
public sealed class ProductRepository(AppPaths paths, TimeProvider time)
{
    private readonly string _file = paths.File("products.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<ProductRecord>? _cache;

    public async Task<List<ProductRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadUnsafeAsync()).Select(x => x.Copy()).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ProductRecord?> GetAsync(Guid id) => (await GetAllAsync()).FirstOrDefault(x => x.Id == id);

    public async Task<List<ProductRecord>> GetManyAsync(IEnumerable<Guid> ids)
    {
        var order = ids.Distinct().Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        return (await GetAllAsync()).Where(x => order.ContainsKey(x.Id)).OrderBy(x => order[x.Id]).ToList();
    }

    public Task<ProductRecord> CreateAsync(ProductRecord product) => WriteAsync(all =>
    {
        EnsureUniqueSku(all, product.Sku, product.Id);
        product.Id = product.Id == Guid.Empty ? Guid.NewGuid() : product.Id;
        product.CreatedAt = time.GetUtcNow();
        product.UpdatedAt = product.CreatedAt;
        product.ContentUpdatedAt ??= product.CreatedAt;
        // Store a copy: the caller keeps its instance, and the cache must not change behind the lock.
        all.Add(product.Copy());
        return product;
    });

    /// <summary>Applies a mutation to the stored product under the lock. Returns null when it no longer exists.</summary>
    public Task<ProductRecord?> UpdateAsync(Guid id, Action<ProductRecord> mutate) => WriteAsync(all =>
    {
        var product = all.FirstOrDefault(x => x.Id == id);
        if (product is null)
        {
            return null;
        }

        var skuBefore = product.Sku;
        mutate(product);
        // Only a changed SKU needs the uniqueness check, so status writes never fail on legacy duplicates.
        if (!product.Sku.Equals(skuBefore, StringComparison.OrdinalIgnoreCase))
        {
            EnsureUniqueSku(all, product.Sku, product.Id);
        }

        product.UpdatedAt = time.GetUtcNow();
        return product;
    });

    public Task<List<ProductRecord>> UpdateManyAsync(IEnumerable<Guid> ids, Action<ProductRecord> mutate)
    {
        var set = ids.ToHashSet();
        return WriteAsync(all =>
        {
            var changed = all.Where(x => set.Contains(x.Id)).ToList();
            foreach (var product in changed)
            {
                mutate(product);
                product.UpdatedAt = time.GetUtcNow();
            }

            return changed.Select(x => x.Copy()).ToList();
        });
    }

    /// <summary>
    /// Upserts imported rows: by Alibaba product ID when the row has one (store SKUs are often not unique on Alibaba),
    /// otherwise by SKU. Existing products keep their identity and Alibaba state.
    /// </summary>
    /// <param name="warnings">Receives rows that were skipped because their Alibaba ID is unknown here.</param>
    /// <param name="canEdit">Existing products the importing user may change; others are skipped with a warning.</param>
    /// <param name="actor">Recorded as creator/editor.</param>
    public Task<(int Created, int Updated)> ImportAsync(IEnumerable<ImportRow> rows, Action<ProductRecord> afterApply, List<string>? warnings = null,
        Func<ProductRecord, bool>? canEdit = null, string? actor = null) => WriteAsync(all =>
    {
        var created = 0;
        var updated = 0;
        var bySku = all.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var byRemoteId = all.Where(x => !string.IsNullOrWhiteSpace(x.RemoteProductId)).GroupBy(x => x.RemoteProductId!)
            .ToDictionary(x => x.Key, x => x.First());
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            var draft = row.Draft;
            var sku = draft.Sku?.Trim() ?? "";
            ProductRecord? existing;
            if (row.RemoteProductId is { } remoteId)
            {
                if (!byRemoteId.TryGetValue(remoteId, out existing))
                {
                    warnings?.Add($"第 {row.RowNumber} 行：本系统没有 Alibaba 商品 ID 为 {remoteId} 的商品，已跳过。请先「从 Alibaba 导入」。");
                    continue;
                }
            }
            else
            {
                bySku.TryGetValue(sku, out existing);
            }

            if (existing is not null && canEdit is not null && !canEdit(existing))
            {
                warnings?.Add($"第 {row.RowNumber} 行：商品 {existing.Sku} 不属于你负责的账号，已跳过。");
                continue;
            }

            if (existing is not null)
            {
                var before = existing.ContentSignature();
                existing.UpdatedBy = actor ?? existing.UpdatedBy;
                existing.ApplyImportedFields(draft, row.Present);
                afterApply(existing);
                existing.UpdatedAt = now;
                // Stock-only sheets are routine; only real content changes should flag a published product as unsynced.
                if (existing.ContentSignature() != before) existing.ContentUpdatedAt = now;
                updated++;
                continue;
            }

            var product = new ProductRecord { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now, ContentUpdatedAt = now, CreatedBy = actor, UpdatedBy = actor };
            product.ApplyDraft(draft);
            afterApply(product);
            all.Add(product);
            bySku[sku] = product;
            created++;
        }

        return (created, updated);
    });

    /// <summary>
    /// Merges listings pulled from Alibaba. Already-linked products only get their remote status refreshed (local edits win);
    /// an unlinked local product with the same SKU is linked; everything else becomes a new local product.
    /// </summary>
    /// <param name="snapshotAt">When the remote data was read; products published or synced after that keep their newer state.</param>
    /// <param name="noLink">Remote IDs that must never be linked to an existing local product by SKU/model number.</param>
    public Task<(int Created, int Linked, int Refreshed)> MergeRemoteAsync(IEnumerable<ProductRecord> remote, Action<ProductRecord> onCreated, DateTimeOffset snapshotAt, IReadOnlySet<string>? noLink = null) => WriteAsync(all =>
    {
        int created = 0, linked = 0, refreshed = 0;
        var now = time.GetUtcNow();
        foreach (var incoming in remote)
        {
            var existing = all.FirstOrDefault(x => x.RemoteProductId == incoming.RemoteProductId)
                ?? (noLink?.Contains(incoming.RemoteProductId!) == true ? null : UniqueUnlinkedMatch(all, incoming));
            if (existing is not null)
            {
                if (existing.RemoteProductId == incoming.RemoteProductId) refreshed++; else linked++;
                if (existing.PublishState == PublishState.Publishing || existing.LastPublishedAt > snapshotAt || existing.LastSyncedAt > snapshotAt)
                {
                    continue;
                }

                existing.RemoteProductId = incoming.RemoteProductId;
                existing.OwnerAliId = incoming.OwnerAliId ?? existing.OwnerAliId;
                existing.RemoteStatus = incoming.RemoteStatus;
                existing.PublishState = incoming.PublishState;
                existing.RemoteStatusMessage = incoming.RemoteStatusMessage;
                // Only a fact about the listing; RemoteModifiedAt stays the version the local content is based on.
                existing.RemoteSkuCount = incoming.RemoteSkuCount;
                existing.LastSyncedAt = now;
                existing.UpdatedAt = now;
                continue;
            }

            if (all.Any(x => x.Sku.Equals(incoming.Sku, StringComparison.OrdinalIgnoreCase)))
            {
                incoming.Sku = $"{incoming.Sku}-{incoming.RemoteProductId}";
            }

            incoming.Id = Guid.NewGuid();
            incoming.CreatedAt = now;
            incoming.UpdatedAt = now;
            incoming.ContentUpdatedAt = now;
            incoming.PublishedContentAt = now;
            onCreated(incoming);
            all.Add(incoming.Copy());
            created++;
        }

        return (created, linked, refreshed);
    });

    /// <summary>
    /// Marks linked products whose listing no longer appears on Alibaba. Listings still in review (search may not
    /// return them) and anything touched after the snapshot are left alone.
    /// </summary>
    public Task<int> MarkMissingAsync(IReadOnlySet<string> seenRemoteIds, IReadOnlySet<string> coveredOwners, DateTimeOffset snapshotAt) => WriteAsync(all =>
    {
        var now = time.GetUtcNow();
        var marked = 0;
        foreach (var product in all.Where(x => !string.IsNullOrWhiteSpace(x.RemoteProductId) && !seenRemoteIds.Contains(x.RemoteProductId!)))
        {
            if (product.OwnerAliId is null || !coveredOwners.Contains(product.OwnerAliId)) continue;
            if (product.PublishState is PublishState.Publishing or PublishState.Pending) continue;
            if (product.LastPublishedAt > snapshotAt.AddDays(-7) || product.LastSyncedAt > snapshotAt) continue;
            if (product.RemoteStatus == RemoteMissingStatus) continue;

            product.RemoteStatus = RemoteMissingStatus;
            product.PublishState = PublishState.Offline;
            product.RemoteStatusMessage = "Alibaba 上已找不到该商品（可能已被删除）。如确认已删除，可在本系统中删除。";
            product.LastSyncedAt = now;
            product.UpdatedAt = now;
            marked++;
        }

        return marked;
    });

    public const string RemoteMissingStatus = "missing";

    public Task<int> DeleteManyAsync(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return WriteAsync(all => all.RemoveAll(x => set.Contains(x.Id)));
    }

    /// <summary>Links only when exactly one unlinked local product matches; ambiguous matches become new products instead.</summary>
    private static ProductRecord? UniqueUnlinkedMatch(List<ProductRecord> all, ProductRecord incoming)
    {
        var matches = all.Where(x => string.IsNullOrWhiteSpace(x.RemoteProductId) && Matches(x, incoming)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    // Listings we create carry the SKU as model_number when no model number is set, so either can identify the local product.
    private static bool Matches(ProductRecord local, ProductRecord remote) =>
        remote.ModelNumber.Length > 0
            ? local.Sku.Equals(remote.ModelNumber, StringComparison.OrdinalIgnoreCase) || local.ModelNumber.Equals(remote.ModelNumber, StringComparison.OrdinalIgnoreCase)
            : local.Sku.Equals(remote.Sku, StringComparison.OrdinalIgnoreCase);

    private static void EnsureUniqueSku(List<ProductRecord> all, string sku, Guid id)
    {
        if (sku.Length > 0 && all.Any(x => x.Id != id && x.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SkuConflictException(sku);
        }
    }

    private async Task<T> WriteAsync<T>(Func<List<ProductRecord>, T> change)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadUnsafeAsync();
            try
            {
                var result = change(all);
                await JsonFile.WriteAtomicAsync(_file, all, JsonFile.Compact);
                return result is ProductRecord product ? (T)(object)product.Copy() : result;
            }
            catch
            {
                // The change may have been applied in memory only; reload from disk next time.
                _cache = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ProductRecord>> ReadUnsafeAsync()
    {
        if (_cache is not null) return _cache;

        var items = await JsonFile.ReadAsync<List<ProductRecord>>(_file) ?? [];
        foreach (var item in items)
        {
            item.MigrateLegacyFields();
        }

        return _cache = items;
    }
}
