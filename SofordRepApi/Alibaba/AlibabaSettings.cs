public sealed record AlibabaApiDefinition(string Key, string Area, string Path, bool RequiresToken, string Description)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Path);
}

/// <summary>Alibaba.com (ICBU) Open Platform settings. The platform uses the IOP protocol on a single REST gateway.</summary>
public sealed record AlibabaSettings(
    string GatewayUrl,
    string? AppKey,
    string? AppSecret,
    string OAuthAuthorizeUrl,
    string? OAuthCallbackUrl,
    string OAuthSuccessRedirect,
    string OAuthSp,
    IReadOnlyDictionary<string, AlibabaApiDefinition> Apis)
{
    public const string DefaultGatewayUrl = "https://openapi-api.alibaba.com/rest";

    /// <summary>Seller consent page of the ICBU open platform (the legacy oauth.alibaba.com rejects these app keys).</summary>
    public const string DefaultAuthorizeUrl = "https://openapi-auth.alibaba.com/oauth/authorize";

    public bool HasCredentials => !string.IsNullOrWhiteSpace(AppKey) && !string.IsNullOrWhiteSpace(AppSecret);

    public static AlibabaSettings FromConfiguration(IConfiguration config)
    {
        // "BaseUrl" is the historical name; "GatewayUrl" wins when both are present.
        var gateway = FirstNonEmpty(config["Alibaba:GatewayUrl"], config["Alibaba:BaseUrl"]) ?? DefaultGatewayUrl;
        return new(
            gateway.TrimEnd('/'),
            config["Alibaba:AppKey"]?.Trim(),
            config["Alibaba:AppSecret"]?.Trim(),
            NormalizeAuthorizeUrl(FirstNonEmpty(config["Alibaba:OAuthAuthorizeUrl"])),
            FirstNonEmpty(config["Alibaba:OAuthCallbackUrl"]),
            FirstNonEmpty(config["Alibaba:OAuthSuccessRedirect"]) ?? "http://localhost:5173/",
            FirstNonEmpty(config["Alibaba:OAuthSp"]) ?? "icbu",
            BuildRegistry(config));
    }

    private static IReadOnlyDictionary<string, AlibabaApiDefinition> BuildRegistry(IConfiguration config)
    {
        var result = new Dictionary<string, AlibabaApiDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var api in DefaultApis)
        {
            var path = FirstNonEmpty(config[$"Alibaba:Apis:{api.Key}:Path"], config[$"Alibaba:Apis:{api.Key}:Method"]);
            result[api.Key] = api with
            {
                Path = path ?? api.Path,
                RequiresToken = bool.TryParse(config[$"Alibaba:Apis:{api.Key}:RequiresToken"], out var parsed) ? parsed : api.RequiresToken
            };
        }

        return result;
    }

    // Old configs point at the TOP-era endpoint, which answers "appkey不存在" for open-platform apps.
    private static string NormalizeAuthorizeUrl(string? configured) =>
        configured is null || configured.Contains("oauth.alibaba.com", StringComparison.OrdinalIgnoreCase)
            ? DefaultAuthorizeUrl
            : configured;

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    public static readonly IReadOnlyList<AlibabaApiDefinition> DefaultApis =
    [
        new("auth.token.create", "auth", "/auth/token/create", false, "用授权码换取 access_token"),
        new("auth.token.refresh", "auth", "/auth/token/refresh", false, "刷新 access_token"),
        new("category.get", "catalog", "/alibaba/icbu/category/get/v2", true, "查询类目信息"),
        new("category.attributes", "catalog", "/alibaba/icbu/category/attribute/get/v2", true, "查询类目属性"),
        new("category.idMapping", "catalog", "/alibaba/icbu/category/id/mapping", true, "类目 ID 映射"),
        new("category.predict", "catalog", "/alibaba/icbu/category/predict/v2", true, "类目预测（title 必填）"),
        new("photobank.group.list", "media", "/icbu/product/photobank/group/list", true, "图片银行分组列表"),
        new("photobank.group.operate", "media", "/icbu/product/photobank/group/operate", true, "新建/修改图片银行分组"),
        new("photobank.list", "media", "/icbu/product/photobank/list", true, "图片银行图片列表"),
        new("image.upload", "media", "/alibaba/icbu/photobank/upload", true, "上传图片到图片银行"),
        new("video.query", "media", "/alibaba/icbu/video/query", true, "查询视频（current_page, page_size）"),
        new("video.relation.product.main", "media", "/alibaba/icbu/video/relation/product/main", true, "设置商品主视频"),
        new("product.create", "product", "/alibaba/icbu/product/listing/v2", true, "发布商品"),
        new("product.update", "product", "/alibaba/icbu/product/update/v2", true, "编辑商品"),
        new("product.get", "product", "/alibaba/icbu/product/get/v2", true, "查询商品详情（product_id）"),
        new("product.search", "product", "/alibaba/icbu/product/search/v2", true, "查询商品列表"),
        new("product.status", "product", "/alibaba/icbu/product/status/get/v2", true, "查询上架状态（product_id）"),
        new("product.status.update", "product", "/alibaba/icbu/product/batch/update/status", true, "上下架（product_id_list, action=online|offline）"),
        new("product.inventory.update", "product", "/icbu/product/edit-inventory", true, "修改库存（product_id, inventory）"),
        new("product.price.update", "product", "/icbu/product/edit-price", true, "修改价格（product_id, price）"),
        new("product.delete", "product", "/alibaba/icbu/product/delete", true, "删除商品"),
        new("product.shippingTemplates", "logistics", "/alibaba/icbu/product/list/shipping/templates", true, "查询运费模板"),
        new("order.search", "order", "", true, "查询订单（需在配置中填写 Path）"),
        new("order.detail", "order", "", true, "订单详情（需在配置中填写 Path）")
    ];
}
