public sealed class SkuConflictException(string sku) : Exception($"SKU '{sku}' 已存在。")
{
    public string Sku { get; } = sku;
}

/// <summary>JSON-file product store. Every read returns fresh copies, so callers never share mutable state.</summary>
public sealed class ProductRepository(AppPaths paths, TimeProvider time)
{
    private readonly string _file = paths.File("products.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<ProductRecord>> GetAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await ReadUnsafeAsync();
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
        all.Add(product);
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

            return changed.Select(x => x.Clone()).ToList();
        });
    }

    /// <summary>Upserts imported rows by SKU. Existing products keep their identity and Alibaba state.</summary>
    public Task<(int Created, int Updated)> ImportAsync(IEnumerable<ImportRow> rows, Action<ProductRecord> afterApply) => WriteAsync(all =>
    {
        var created = 0;
        var updated = 0;
        var bySku = all.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            var draft = row.Draft;
            var sku = draft.Sku?.Trim() ?? "";
            if (bySku.TryGetValue(sku, out var existing))
            {
                var before = existing.ContentSignature();
                existing.ApplyImportedFields(draft, row.Present);
                afterApply(existing);
                existing.UpdatedAt = now;
                // Stock-only sheets are routine; only real content changes should flag a published product as unsynced.
                if (existing.ContentSignature() != before) existing.ContentUpdatedAt = now;
                updated++;
                continue;
            }

            var product = new ProductRecord { Id = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now, ContentUpdatedAt = now };
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
            all.Add(incoming);
            created++;
        }

        return (created, linked, refreshed);
    });

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
            var result = change(all);
            await JsonFile.WriteAtomicAsync(_file, all);
            return result is ProductRecord product ? (T)(object)product.Clone() : result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<ProductRecord>> ReadUnsafeAsync()
    {
        var items = await JsonFile.ReadAsync<List<ProductRecord>>(_file) ?? [];
        foreach (var item in items)
        {
            item.MigrateLegacyFields();
        }

        return items;
    }
}
