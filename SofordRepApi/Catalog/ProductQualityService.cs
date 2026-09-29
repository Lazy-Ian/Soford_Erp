using System.Text.RegularExpressions;

public sealed partial class ProductQualityService(CategoryAttributeService categories)
{
    public const int MaxImages = 6;

    /// <summary>Runs all checks, stores the issues and derives LocalState. Alibaba-side state is left untouched.</summary>
    public async Task<List<QualityIssue>> ApplyAsync(ProductRecord product)
    {
        var cached = string.IsNullOrWhiteSpace(product.CategoryId) ? null : await categories.GetCachedAsync(product.CategoryId);
        Apply(product, cached);
        return product.QualityIssues;
    }

    public async Task<Func<string, CategoryAttributeSet?>> LoadCategoryLookupAsync()
    {
        var all = await categories.GetAllCachedAsync();
        return categoryId => all.TryGetValue(categoryId, out var set) ? set : null;
    }

    public static void Apply(ProductRecord product, CategoryAttributeSet? categoryAttributes)
    {
        product.QualityIssues = Check(product, categoryAttributes);
        product.LocalState = product.QualityIssues.Any(x => x.Severity == QualitySeverity.Blocker) ? LocalState.Incomplete : LocalState.Ready;
    }

    public static List<QualityIssue> Check(ProductRecord product, CategoryAttributeSet? categoryAttributes = null)
    {
        var issues = new List<QualityIssue>();
        void Blocker(string field, string message) => issues.Add(new(QualitySeverity.Blocker, field, message));
        void Warning(string field, string message) => issues.Add(new(QualitySeverity.Warning, field, message));

        if (string.IsNullOrWhiteSpace(product.Sku)) Blocker("sku", "SKU 不能为空。");

        if (string.IsNullOrWhiteSpace(product.Title)) Blocker("title", "商品标题不能为空。");
        else if (product.Title.Length > 128) Blocker("title", $"标题超过 128 个字符（当前 {product.Title.Length}）。");
        else if (product.Title.Length < 25) Warning("title", "标题偏短，建议包含材质、用途、型号或核心卖点。");
        if (NonAscii().IsMatch(product.Title)) Warning("title", "标题含非英文字符，Alibaba 会自动翻译，建议直接使用英文。");

        if (string.IsNullOrWhiteSpace(product.Description)) Blocker("description", "商品描述不能为空。");
        else if (product.Description.Length < 50) Warning("description", "描述过短，建议补充功能、规格、售后等信息。");

        if (string.IsNullOrWhiteSpace(product.CategoryId)) Blocker("categoryId", "缺少 Alibaba 类目 ID，可使用「预测类目」自动填写。");
        else if (!long.TryParse(product.CategoryId, out _)) Blocker("categoryId", "类目 ID 必须是数字（Alibaba 叶子类目 ID）。");

        if (product.TieredPrices.Count == 0)
        {
            if (product.Price <= 0) Blocker("price", "价格必须大于 0。");
            else if (decimal.Round(product.Price, 2) != product.Price) Blocker("price", "价格最多保留两位小数。");
        }
        else
        {
            CheckTiers(product.TieredPrices, Blocker);
        }

        if (!string.Equals(product.Currency, "USD", StringComparison.OrdinalIgnoreCase))
            Warning("currency", "Alibaba 会把非 USD 价格自动换算为 USD，且改价接口只支持 USD。");

        if (product.MinimumOrderQuantity <= 0) Blocker("minimumOrderQuantity", "起订量必须大于 0。");
        if (product.Stock < 0) Blocker("stock", "库存不能为负数。");
        if (string.IsNullOrWhiteSpace(product.Unit)) Warning("unit", "未填写销售单位，将默认使用 Piece。");
        if (product.LeadTimeDays <= 0) Warning("leadTimeDays", "建议填写交期（天）。");

        if (product.Images.Length == 0) Blocker("images", "至少需要一张商品图片（第一张为主图）。");
        if (product.Images.Any(x => !IsHttpUrl(x))) Blocker("images", "图片必须是可公开访问的 http/https 地址。");
        if (product.Images.Length > MaxImages) Warning("images", $"Alibaba 最多支持 {MaxImages} 张图片，超出部分不会发布。");

        if (product.Keywords.Length == 0) Warning("keywords", "未填写关键词，发布时将开启 Alibaba 关键词智能优化。");
        else if (product.Keywords.Length < 3) Warning("keywords", "建议至少填写 3 个买家搜索关键词。");

        if (categoryAttributes is not null)
        {
            foreach (var attribute in categoryAttributes.Attributes.Where(x => x.Required))
            {
                if (!product.Attributes.TryGetValue(attribute.Name, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    Blocker("attributes", $"类目必填属性「{attribute.Name}」未填写。");
                }
            }
        }
        else if (product.Attributes.Count < 3)
        {
            Warning("attributes", "建议补充材质、尺寸、颜色、认证、型号等关键属性（可在编辑页加载类目属性）。");
        }

        return issues;
    }

    private static void CheckTiers(List<PriceTier> tiers, Action<string, string> blocker)
    {
        if (tiers.Any(x => x.Quantity <= 0 || x.Price <= 0))
        {
            blocker("tieredPrices", "阶梯价的数量和价格都必须大于 0。");
            return;
        }

        if (tiers.Any(x => decimal.Round(x.Price, 2) != x.Price))
        {
            blocker("tieredPrices", "阶梯价最多保留两位小数。");
        }

        for (var i = 1; i < tiers.Count; i++)
        {
            if (tiers[i].Quantity <= tiers[i - 1].Quantity || tiers[i].Price >= tiers[i - 1].Price)
            {
                blocker("tieredPrices", "阶梯价须按数量递增、价格递减，且不能重复。");
                return;
            }
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex(@"[^\x00-\x7F]")]
    private static partial Regex NonAscii();
}
