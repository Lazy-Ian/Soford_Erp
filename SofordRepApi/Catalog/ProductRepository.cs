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

        mutate(product);
        EnsureUniqueSku(all, product.Sku, product.Id);
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
    public Task<(int Created, int Updated)> ImportAsync(IEnumerable<(ProductDraft Draft, Action<ProductRecord> AfterApply)> rows) => WriteAsync(all =>
    {
        var created = 0;
        var updated = 0;
        var bySku = all.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (draft, afterApply) in rows)
        {
            var sku = draft.Sku?.Trim() ?? "";
            if (bySku.TryGetValue(sku, out var existing))
            {
                existing.ApplyDraft(draft);
                afterApply(existing);
                existing.UpdatedAt = time.GetUtcNow();
                updated++;
                continue;
            }

            var product = new ProductRecord { Id = Guid.NewGuid(), CreatedAt = time.GetUtcNow(), UpdatedAt = time.GetUtcNow() };
            product.ApplyDraft(draft);
            afterApply(product);
            all.Add(product);
            bySku[sku] = product;
            created++;
        }

        return (created, updated);
    });

    public Task<int> DeleteManyAsync(IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return WriteAsync(all => all.RemoveAll(x => set.Contains(x.Id)));
    }

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
