using System.Globalization;
using System.Text.Json;

public sealed record PullResult(int Total, int Created, int Linked, int Refreshed, int Pages, List<string> Warnings);

/// <summary>Brings listings that already exist on Alibaba into the local catalog so they can be managed here.</summary>
public sealed class AlibabaCatalogSync(AlibabaClient alibaba, ProductRepository products, ProductQualityService quality, TimeProvider time)
{
    public const int PageSize = 20;
    private const int MaxPages = 100;

    public async Task<(PullResult? Result, AlibabaApiResult? Error)> PullAsync(CancellationToken cancellationToken = default)
    {
        var snapshotAt = time.GetUtcNow();
        var remote = new List<ProductRecord>();
        var warnings = new List<string>();
        var pages = 0;
        var totalPages = 1;
        for (var page = 1; page <= totalPages && page <= MaxPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await alibaba.CallAsync("product.search", new { page_index = page, page_size = PageSize }, cancellationToken: cancellationToken);
            if (!result.Success || result.Json is null)
            {
                if (page == 1) return (null, result);
                warnings.Add($"第 {page} 页读取失败：{AlibabaErrors.Explain(result)}，已停止。");
                break;
            }

            pages++;
            var root = result.Json.Value;
            if (AlibabaResponseParser.Find(root, "total_page") is { } total && int.TryParse(AlibabaResponseParser.ScalarText(total), out var parsedTotal))
            {
                totalPages = parsedTotal;
            }

            var list = AlibabaResponseParser.Find(root, "product_info");
            if (list is not { ValueKind: JsonValueKind.Array } items || items.GetArrayLength() == 0) break;
            foreach (var item in items.EnumerateArray())
            {
                var mapped = FromRemote(item, time.GetUtcNow());
                if (mapped is not null) remote.Add(mapped);
            }
        }

        if (totalPages > MaxPages) warnings.Add($"商品超过 {MaxPages * PageSize} 个，只导入了前 {MaxPages} 页。");

        var lookup = await quality.LoadCategoryLookupAsync();
        var (created, linked, refreshed) = await products.MergeRemoteAsync(remote, p => ProductQualityService.Apply(p, lookup(p.CategoryId)), snapshotAt);
        return (new PullResult(remote.Count, created, linked, refreshed, pages, warnings), null);
    }

    /// <summary>Maps one search/v2 product_info entry to a local product. Returns null when it has no product id.</summary>
    public static ProductRecord? FromRemote(JsonElement item, DateTimeOffset now)
    {
        var basic = Child(item, "basic_info");
        var remoteId = AlibabaResponseParser.Text(basic, "product_id");
        if (string.IsNullOrWhiteSpace(remoteId)) return null;

        var category = Child(item, "category_info");
        var trade = Child(item, "trade_info");
        var logistics = Child(item, "logistics_info");
        var skus = item.TryGetProperty("sku_info", out var skuList) && skuList.ValueKind == JsonValueKind.Array ? skuList : default;
        var firstSkuCode = skus.ValueKind == JsonValueKind.Array
            ? skus.EnumerateArray().Select(x => AlibabaResponseParser.Text(x, "sku_code")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
            : null;
        var modelNumber = AlibabaResponseParser.Text(basic, "model_number") ?? "";

        var product = new ProductRecord
        {
            Sku = FirstNonEmpty(modelNumber, firstSkuCode) ?? $"ALI-{remoteId}",
            Title = AlibabaResponseParser.Text(basic, "title") ?? "",
            Description = AlibabaResponseParser.Text(basic, "description") ?? "",
            Keywords = SplitKeywords(AlibabaResponseParser.Text(basic, "keywords")),
            ModelNumber = modelNumber,
            Language = AlibabaResponseParser.Text(basic, "language") ?? "en_US",
            Images = ReadArray(basic, "product_images").Select(x => NormalizeUrl(AlibabaResponseParser.Text(x, "image_url"))).Where(x => x.Length > 0).ToArray(),
            CategoryId = AlibabaResponseParser.Text(category, "category_id") ?? "",
            CategoryPath = AlibabaResponseParser.Text(category, "category_name") ?? "",
            Attributes = ReadArray(category, "attributes")
                .Select(x => (Name: AlibabaResponseParser.Text(x, "attribute_name"), Value: AlibabaResponseParser.Text(x, "attribute_value")))
                .Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Value))
                .GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(x => x.Value)), StringComparer.OrdinalIgnoreCase),
            MinimumOrderQuantity = (int)AlibabaResponseParser.ReadLong(trade, "moq"),
            Unit = AlibabaResponseParser.Text(trade, "unit") ?? "Piece",
            Stock = (int)(Decimal(AlibabaResponseParser.Text(trade, "inventory")) ?? 0),
            ShippingTemplateId = AlibabaResponseParser.Text(logistics, "shipping_template_id") ?? "",
            WeightKg = Decimal(AlibabaResponseParser.Text(logistics, "weight")),
            RemoteProductId = remoteId,
            RemoteStatus = AlibabaResponseParser.Text(basic, "status")?.ToLowerInvariant(),
            LastSyncedAt = now
        };

        var dimension = Child(logistics, "dimension");
        product.LengthCm = Decimal(AlibabaResponseParser.Text(dimension, "length"));
        product.WidthCm = Decimal(AlibabaResponseParser.Text(dimension, "width"));
        product.HeightCm = Decimal(AlibabaResponseParser.Text(dimension, "height"));
        product.LeadTimeDays = (int)(ReadArray(logistics, "tiered_lead_time").Select(x => Decimal(AlibabaResponseParser.Text(x, "lead_time"))).FirstOrDefault() ?? 0);

        var price = Child(trade, "price");
        product.Currency = AlibabaResponseParser.Text(price, "currency") ?? "USD";
        product.TieredPrices = ReadArray(price, "tiered_price")
            .Select(x => new PriceTier((int)AlibabaResponseParser.ReadLong(x, "quantity"), Decimal(AlibabaResponseParser.Text(x, "price")) ?? 0))
            .Where(x => x.Quantity > 0 && x.Price > 0)
            .OrderBy(x => x.Quantity)
            .ToList();
        var range = Child(price, "range_price");
        var skuPrices = skus.ValueKind == JsonValueKind.Array
            ? skus.EnumerateArray().Select(x => Decimal(AlibabaResponseParser.Text(Child(x, "sku_price"), "price"))).Where(x => x > 0).Select(x => x!.Value).ToList()
            : [];
        product.Price = product.TieredPrices.FirstOrDefault()?.Price
            ?? Decimal(AlibabaResponseParser.Text(range, "min_price"))
            ?? (skuPrices.Count > 0 ? skuPrices.Min() : 0);
        if (product.TieredPrices.Count == 1) product.TieredPrices = [];
        if (product.MinimumOrderQuantity <= 0) product.MinimumOrderQuantity = 1;
        if (product.Stock == 0 && skus.ValueKind == JsonValueKind.Array)
        {
            product.Stock = (int)skus.EnumerateArray().Sum(x => AlibabaResponseParser.ReadLong(x, "inventory"));
        }

        var (state, label) = ProductOperations.MapStatus(product.RemoteStatus ?? "", PublishState.Pending);
        product.PublishState = state;
        product.RemoteStatusMessage = $"从 Alibaba 导入：{label}";
        return product;
    }

    private static JsonElement Child(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var child) ? child : default;

    private static IEnumerable<JsonElement> ReadArray(JsonElement node, string name) =>
        Child(node, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    private static decimal? Decimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static string NormalizeUrl(string? url) =>
        string.IsNullOrWhiteSpace(url) ? "" : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url.Trim();

    private static string[] SplitKeywords(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ';', '，', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10).ToArray();
}
