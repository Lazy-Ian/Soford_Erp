using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>Maps a local product onto Alibaba's product_info schema (listing/v2 and update/v2).</summary>
public static class ListingMapper
{
    public static JsonObject BuildCreatePayload(ProductRecord product)
    {
        var payload = new JsonObject { ["product_info"] = BuildProductInfo(product, remoteProductId: null) };
        var keywordsMissing = product.Keywords.Length == 0;
        if (product.AiOptimize || keywordsMissing)
        {
            // Alibaba requires keyword optimization when no keywords are supplied.
            payload["ai_optimization_config"] = new JsonObject
            {
                ["title_optimization_enabled"] = product.AiOptimize,
                ["description_optimization_enabled"] = product.AiOptimize,
                ["keyword_optimization_enabled"] = product.AiOptimize || keywordsMissing
            };
        }

        return payload;
    }

    public static JsonObject BuildUpdatePayload(ProductRecord product)
    {
        var info = BuildProductInfo(product, product.RemoteProductId);
        // Most listings do not track stock locally (0); sending it would empty the stock of a live listing.
        if (product.Stock <= 0) ((JsonObject)info["trade_info"]!).Remove("inventory");
        // No local description means Alibaba never gave us one (detail-editor listings); keep theirs.
        if (string.IsNullOrWhiteSpace(product.Description)) ((JsonObject)info["basic_info"]!).Remove("description");
        return new() { ["product_info"] = info };
    }

    public static JsonObject BuildInventoryPayload(ProductRecord product) => new()
    {
        ["product_id"] = RemoteId(product),
        ["inventory"] = product.Stock
    };

    public static JsonObject BuildPricePayload(ProductRecord product) => new()
    {
        ["product_id"] = RemoteId(product),
        ["price"] = BuildPrice(product)
    };

    public static JsonObject BuildStatusPayload(ProductRecord product) => new() { ["product_id"] = RemoteId(product) };

    public static JsonObject BuildOnlineOfflinePayload(IEnumerable<ProductRecord> products, bool online) => new()
    {
        ["product_id_list"] = new JsonArray(products.Select(x => (JsonNode)RemoteId(x)).ToArray()),
        ["action"] = online ? "online" : "offline"
    };

    public static List<PriceTier> EffectiveTiers(ProductRecord product) =>
        product.TieredPrices.Count > 0
            ? product.TieredPrices.OrderBy(x => x.Quantity).ToList()
            : [new PriceTier(Math.Max(product.MinimumOrderQuantity, 1), product.Price)];

    private static JsonObject BuildProductInfo(ProductRecord product, string? remoteProductId)
    {
        var basic = new JsonObject
        {
            ["title"] = product.Title,
            ["description"] = product.Description,
            ["language"] = string.IsNullOrWhiteSpace(product.Language) ? "en_US" : product.Language,
            ["product_image"] = new JsonArray(product.Images.Take(ProductQualityService.MaxImages)
                .Select(url => (JsonNode)new JsonObject { ["image_url"] = url }).ToArray())
        };
        if (remoteProductId is not null) basic["product_id"] = ParseRemoteId(remoteProductId, product.Sku);
        // One keyword per line, the way Alibaba returns them: most keywords are phrases, so spaces cannot separate them.
        if (product.Keywords.Length > 0) basic["keywords"] = string.Join('\n', product.Keywords);
        if (RemoteModelNumber(product) is { Length: > 0 } modelNumber) basic["model_number"] = modelNumber;
        if (product.BrandName.Length > 0) basic["brand_name"] = product.BrandName;

        var category = new JsonObject
        {
            ["attributes"] = new JsonArray(product.Attributes
                .Select(x => (JsonNode)new JsonObject { ["attribute_name"] = x.Key, ["attribute_value"] = x.Value }).ToArray())
        };
        if (long.TryParse(product.CategoryId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var categoryId)) category["category_id"] = categoryId;
        if (product.CategoryName.Length > 0) category["category_name"] = product.CategoryName;

        var trade = new JsonObject
        {
            ["price"] = BuildPrice(product),
            ["inventory"] = product.Stock,
            ["moq"] = product.MinimumOrderQuantity,
            ["unit"] = string.IsNullOrWhiteSpace(product.Unit) ? "Piece" : product.Unit
        };

        var logistics = new JsonObject();
        if (product.ShippingTemplateId.Length > 0) logistics["shipping_template_id"] = product.ShippingTemplateId;
        if (product.LeadTimeDays > 0)
        {
            logistics["tiered_lead_time"] = new JsonArray(new JsonObject
            {
                ["quantity"] = Math.Max(product.MinimumOrderQuantity, 1),
                ["lead_time"] = product.LeadTimeDays
            });
        }

        if (product.WeightKg is > 0) logistics["weight"] = Format(product.WeightKg.Value);
        if (product.LengthCm is > 0 && product.WidthCm is > 0 && product.HeightCm is > 0)
        {
            logistics["dimension"] = new JsonObject
            {
                ["length"] = Format(product.LengthCm.Value),
                ["width"] = Format(product.WidthCm.Value),
                ["height"] = Format(product.HeightCm.Value)
            };
        }

        var info = new JsonObject { ["basic_info"] = basic, ["category_info"] = category, ["trade_info"] = trade };
        if (logistics.Count > 0) info["logistics_info"] = logistics;
        return info;
    }

    private static JsonObject BuildPrice(ProductRecord product) => new()
    {
        ["price_type"] = "TIERED",
        ["currency"] = string.IsNullOrWhiteSpace(product.Currency) ? "USD" : product.Currency,
        ["tiered_price"] = new JsonArray(EffectiveTiers(product)
            .Select(x => (JsonNode)new JsonObject { ["quantity"] = x.Quantity, ["price"] = Format(x.Price) }).ToArray())
    };

    /// <summary>
    /// The model number sent to Alibaba. Falls back to the SKU so every listing we create can be found again
    /// (duplicate protection after a timed-out create, and linking when pulling from Alibaba).
    /// </summary>
    public static string RemoteModelNumber(ProductRecord product) => product.ModelNumber.Length > 0 ? product.ModelNumber : product.Sku;

    private static long RemoteId(ProductRecord product) => ParseRemoteId(product.RemoteProductId, product.Sku);

    private static long ParseRemoteId(string? remoteProductId, string sku) =>
        long.TryParse(remoteProductId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException($"商品 {sku} 的 Alibaba 商品 ID「{remoteProductId}」无效或缺失。");

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
