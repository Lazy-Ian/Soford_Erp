using System.Text.Json;

public sealed record CategoryAttribute(string Id, string Name, bool Required, bool SupportCustomValue, bool SupportMultiValue, string[] Values);

public sealed record CategoryAttributeSet(string CategoryId, DateTimeOffset FetchedAt, List<CategoryAttribute> Attributes, List<CategoryAttribute> SaleAttributes);

/// <summary>Fetches and caches category attributes so quality checks can enforce required ones without calling Alibaba.</summary>
public sealed class CategoryAttributeService(AppPaths paths, TimeProvider time)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private readonly string _file = paths.File("category-attributes.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<CategoryAttributeSet?> GetCachedAsync(string categoryId)
    {
        var cache = await ReadAsync();
        return cache.TryGetValue(categoryId, out var set) ? set : null;
    }

    public Task<Dictionary<string, CategoryAttributeSet>> GetAllCachedAsync() => ReadAsync();

    public async Task<(CategoryAttributeSet? Set, AlibabaApiResult? Error)> GetAsync(AlibabaClient client, string categoryId, bool forceRefresh)
    {
        var cached = await GetCachedAsync(categoryId);
        if (!forceRefresh && cached is not null && time.GetUtcNow() - cached.FetchedAt < CacheLifetime)
        {
            return (cached, null);
        }

        if (!long.TryParse(categoryId, out var numericId))
        {
            return (null, AlibabaApiResult.Failure("category.attributes", "", "InvalidCategoryId", "类目 ID 必须是数字。"));
        }

        var result = await client.CallAsync("category.attributes", new { category_id = numericId });
        if (!result.Success || result.Json is null)
        {
            // Keep working with a stale cache if Alibaba is unavailable.
            return cached is not null ? (cached, null) : (null, result);
        }

        var set = Parse(categoryId, result.Json.Value, time.GetUtcNow());
        await _lock.WaitAsync();
        try
        {
            var cache = await ReadAsync();
            cache[categoryId] = set;
            await JsonFile.WriteAtomicAsync(_file, cache);
        }
        finally
        {
            _lock.Release();
        }

        return (set, null);
    }

    public static CategoryAttributeSet Parse(string categoryId, JsonElement root, DateTimeOffset fetchedAt) => new(
        categoryId,
        fetchedAt,
        ParseList(AlibabaResponseParser.Find(root, "category_attributes")),
        ParseList(AlibabaResponseParser.Find(root, "sale_attributes")));

    private static List<CategoryAttribute> ParseList(JsonElement? list)
    {
        if (list is not { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(item => new CategoryAttribute(
                AlibabaResponseParser.Text(item, "attribute_id") ?? "",
                AlibabaResponseParser.Text(item, "attribute_name") ?? "",
                AlibabaResponseParser.Text(item, "required") == "true",
                AlibabaResponseParser.Text(item, "support_custom_value") == "true",
                AlibabaResponseParser.Text(item, "support_multi_value") == "true",
                item.TryGetProperty("attribute_value_list", out var values) && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray().Select(v => AlibabaResponseParser.Text(v, "attribute_value_name") ?? "").Where(v => v.Length > 0).ToArray()
                    : []))
            .Where(x => x.Name.Length > 0)
            .ToList();
    }

    private async Task<Dictionary<string, CategoryAttributeSet>> ReadAsync()
    {
        try
        {
            return await JsonFile.ReadAsync<Dictionary<string, CategoryAttributeSet>>(_file) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            // Only a cache of Alibaba data; it is fetched again on demand.
            return new();
        }
    }
}
