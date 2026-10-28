using System.Text.RegularExpressions;
using System.Security.Claims;

/// <summary>
/// Public website view of the ERP product store. The site reads this instead of a second exported copy.
/// Explicitly synchronized card/detail versions remain in the same product store.
/// </summary>
public sealed class WebsiteCatalog(ProductRepository products)
{
    private readonly object _gate = new();
    private long _generation = -1;
    private IReadOnlyList<WebsiteProduct> _items = [];

    public async Task<IReadOnlyList<WebsiteProduct>> ListAsync()
    {
        var generation = products.Generation;
        lock (_gate)
        {
            if (generation == _generation) return _items;
        }

        var items = WebsiteCatalogProjection.Project(await products.GetAllAsync());
        lock (_gate)
        {
            if (products.Generation == generation)
            {
                _items = items;
                _generation = generation;
            }
        }

        return items;
    }

    public async Task<string?> DescriptionAsync(string id)
    {
        var product = WebsiteCatalogProjection.Find(await products.GetAllAsync(), id);
        return product is null ? null : product.WebsitePublication?.Description ?? WebsiteCatalogProjection.SanitizeDetail(product.Description);
    }
}

public sealed record WebsiteProduct(
    string Id,
    string Title,
    string Category,
    string[] Images,
    decimal PriceMin,
    decimal PriceMax,
    string Currency,
    string Unit,
    int Moq,
    int LeadTimeDays,
    string[] Keywords,
    string[][] Specs)
{
    public WebsiteProduct Copy() => this with { Images = [.. Images], Keywords = [.. Keywords], Specs = Specs.Select(x => x.ToArray()).ToArray() };
}

public sealed record WebsitePublication(WebsiteProduct Product, string Description)
{
    public WebsitePublication Copy() => new(Product.Copy(), Description);
}

public static class WebsiteCatalogProjection
{
    private static readonly Regex PublicIdPattern = new("^[A-Za-z0-9_-]{1,80}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DedupeKey = new(@"\W+", RegexOptions.ECMAScript | RegexOptions.Compiled);
    private static readonly Regex SlashSpace = new(@"\s*/\s*", RegexOptions.Compiled);
    private static readonly Regex SpaceBeforePunct = new(@"\s+([,.])", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex Edge = new(@"^[\s,/&-]+|[\s,/&-]+$", RegexOptions.Compiled);
    private static readonly Regex UpperRun = new("[A-Z]{2}", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new("<!--[\\s\\S]*?-->", RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlBlock = new("<(?:script|style|iframe|object|embed|link|meta|form|input|button|textarea|select|svg|video|audio|noscript)\\b[^>]*>[\\s\\S]*?<\\/(?:script|style|iframe|object|embed|link|meta|form|input|button|textarea|select|svg|video|audio|noscript)>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlEmpty = new("<(?:script|style|iframe|object|embed|link|meta|form|input|button|textarea|select|svg|video|audio|noscript)\\b[^>]*\\/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlEvent = new("\\s+on[a-z]+\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlScriptUrl = new("\\s+(?:href|src)\\s*=\\s*(\"|')\\s*(?:javascript:|data:)[^\"']*\\1", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlProtocolRelative = new("(\\s(?:src|href)\\s*=\\s*[\"'])//", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    private static readonly (string Id, Regex Test)[] Categories =
    [
        ("artificial-grass", new Regex("grass|turf|lawn", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("eva-mats", new Regex(@"\beva\b|tatami|taekwondo|judo|karate|martial", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled)),
        ("outdoor-playground", new Regex("playground|dog ?bone|paver|brick|outdoor|granule|track", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("rubber-rolls", new Regex(@"\brolls?\b|rolled|stopper", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled)),
        ("gym-tiles", new Regex(".", RegexOptions.Compiled)),
    ];

    private static readonly string[][] SpecFields =
    [
        ["Material"], ["Thickness"], ["Size"], ["Width", "Roll Width"], ["Length"], ["Color"],
        ["Pile height"], ["Density"], ["Gauge"], ["Dtex"], ["Backing"], ["Hardness"],
        ["Application", "Usage"], ["Feature", "Function", "Advantage"], ["Fire protection"],
        ["Certificate", "Certification"], ["Warranty"], ["Packing", "Package"], ["Delivery Time"],
    ];

    private static readonly Regex[] TitleNoise =
    [
        new(@"\b(19|20)\d{2}\b", RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\b(hot|best)[ -]?sell(ing|er)?\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\b(new )?popular\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bcheap( price)?\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\b(china|chinese)( factory| manufacturer| suppliers?)?\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bfactory( direct| price| supply| customized)?\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bmanufacturer\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bwholesale\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bat best price\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bfrom china\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bmade in shandong\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\b(soford|sfd|sofod)( sfd)?\d*\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
        new(@"\bused\b", RegexOptions.IgnoreCase | RegexOptions.ECMAScript | RegexOptions.Compiled),
    ];

    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "or", "for", "of", "with", "in", "on", "to", "by", "the", "x",
    };

    public static IReadOnlyList<WebsiteProduct> Project(IEnumerable<ProductRecord> source) =>
        Select(source).Select(x => x.WebsitePublication?.Product.Copy() ?? ToProduct(x)).ToList();

    public static WebsitePublication Snapshot(ProductRecord product) => new(ToProduct(product), SanitizeDetail(product.Description));

    public static void InitializeLegacy(List<ProductRecord> products, DateTimeOffset now)
    {
        var legacy = products.Where(x => x.WebsiteSyncVersion is null).ToList();
        var visible = Select(legacy).Select(x => x.Id).ToHashSet();
        foreach (var product in legacy)
        {
            if (visible.Contains(product.Id))
            {
                product.WebsitePublication = Snapshot(product);
                product.WebsiteStableId = product.WebsitePublication.Product.Id;
                product.WebsiteSyncedAt = now;
                product.WebsiteSyncedBy = "系统迁移";
                product.WebsiteSourceHash = product.WebsiteContentHash();
            }
            product.WebsiteSyncVersion = 1;
        }
    }

    public static string? Validate(ProductRecord product)
    {
        if (string.IsNullOrWhiteSpace(CleanTitle(product.Title ?? ""))) return "请先填写商品标题";
        if (product.Images.Length == 0 || product.Images.Any(x => !Uri.TryCreate(ImageUrl(x), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) return "请先补全有效的商品图片";
        var detail = SanitizeDetail(product.Description);
        if (string.IsNullOrWhiteSpace(detail) || (!Regex.IsMatch(detail, @"<img\b", RegexOptions.IgnoreCase) && string.IsNullOrWhiteSpace(Regex.Replace(detail, "<[^>]*>|&nbsp;", "", RegexOptions.IgnoreCase)))) return "详情为空，请先编辑描述或拉取详情";
        if (product.MinimumOrderQuantity <= 0) return "起订量必须大于 0";
        if ((product.TieredPrices.Count == 0 && product.Price <= 0) || product.TieredPrices.Any(x => x.Price <= 0 || x.Quantity <= 0)) return "请先填写有效价格";
        if (!Regex.IsMatch(product.Currency, "^[A-Z]{3}$")) return "币种需要三位大写代码";
        if (string.IsNullOrWhiteSpace(product.Unit) || product.Unit.Length > 80 || product.Unit.IndexOfAny(['<', '>']) >= 0) return "请填写有效销售单位";
        return null;
    }

    public static ProductRecord? Find(IEnumerable<ProductRecord> source, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !PublicIdPattern.IsMatch(id)) return null;
        return Select(source).FirstOrDefault(product => PublicId(product) == id);
    }

    public static string SanitizeDetail(string? html)
    {
        try
        {
            var source = HtmlComment.Replace(html ?? "", "");
            string previous;
            do
            {
                previous = source;
                source = HtmlBlock.Replace(source, "");
            }
            while (!string.Equals(source, previous, StringComparison.Ordinal));

            source = HtmlEmpty.Replace(source, "");
            source = HtmlEvent.Replace(source, "");
            source = HtmlScriptUrl.Replace(source, "");
            source = HtmlProtocolRelative.Replace(source, "$1https://");
            return source.Trim();
        }
        catch (RegexMatchTimeoutException)
        {
            return "";
        }
    }

    private static IEnumerable<ProductRecord> Select(IEnumerable<ProductRecord> source)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var product in source)
        {
            if (product.WebsiteSyncVersion is not null)
            {
                if (product.WebsitePublication is not null) yield return product;
                continue;
            }
            if (!string.Equals(product.RemoteStatus, "online", StringComparison.OrdinalIgnoreCase)) continue;
            if (product.Images is not { Length: > 0 }) continue;
            var key = DedupeKey.Replace(CleanTitle(Whitespace.Replace(product.Title ?? "", " ").Trim()).ToLowerInvariant(), " ").Trim();
            if (!seen.Add(key)) continue;
            yield return product;
        }
    }

    private static WebsiteProduct ToProduct(ProductRecord product)
    {
        var title = CleanTitle(Whitespace.Replace(product.Title ?? "", " ").Trim());
        var prices = (product.TieredPrices ?? []).Select(tier => tier.Price).Where(price => price > 0).ToList();
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in product.Attributes ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name)) attrs[name] = (value ?? "").Trim();
        }

        var specs = new List<string[]>();
        foreach (var names in SpecFields)
        {
            string? value = null;
            foreach (var name in names)
            {
                if (attrs.TryGetValue(name, out var found) && found.Length > 0)
                {
                    value = found;
                    break;
                }
            }

            if (value is not null) specs.Add([names[0], value]);
        }

        return new WebsiteProduct(
            PublicId(product),
            title,
            CategoryOf(title),
            (product.Images ?? []).Take(6).Select(ImageUrl).ToArray(),
            prices.Count > 0 ? prices.Min() : product.Price,
            prices.Count > 0 ? prices.Max() : product.Price,
            product.Currency,
            product.Unit,
            product.MinimumOrderQuantity,
            product.LeadTimeDays,
            (product.Keywords ?? []).SelectMany(keyword => keyword.Split(['\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Take(6).ToArray(),
            specs.ToArray());
    }

    public static string PublicId(ProductRecord product) =>
        product.WebsiteStableId ?? product.WebsitePublication?.Product.Id ?? (string.IsNullOrWhiteSpace(product.RemoteProductId) ? product.Id.ToString("D") : product.RemoteProductId.Trim());

    private static string CategoryOf(string title)
    {
        foreach (var (id, test) in Categories)
        {
            if (test.IsMatch(title)) return id;
        }

        return "gym-tiles";
    }

    private static string ImageUrl(string url)
    {
        var value = url ?? "";
        return value.StartsWith("//", StringComparison.Ordinal) ? "https:" + value : value;
    }

    internal static string CleanTitle(string title)
    {
        var text = title;
        foreach (var pattern in TitleNoise) text = pattern.Replace(text, " ");
        text = SlashSpace.Replace(text, "/");
        text = SpaceBeforePunct.Replace(text, "$1");
        text = MultiSpace.Replace(text, " ");
        text = Edge.Replace(text, "");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Any(c => c is >= '0' and <= '9') || (word == word.ToUpperInvariant() && UpperRun.IsMatch(word))) continue;
            var lower = word.ToLowerInvariant();
            words[i] = i > 0 && SmallWords.Contains(lower)
                ? lower
                : char.ToUpperInvariant(lower[0]) + lower[1..];
        }

        return string.Join(' ', words);
    }
}

public static class WebsiteCatalogEndpoints
{
    public static void MapWebsiteCatalog(this WebApplication app, RouteGroupBuilder api)
    {
        var management = api.MapGroup("/website/catalog").RequireAuthorization(ApiEndpoints.AdminPolicy);
        foreach (var action in new[] { "sync", "unpublish" })
        {
            var remove = action == "unpublish";
            management.MapPost($"/{action}", async (IdsRequest request, ProductRepository products, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
            {
                if (request.Ids.Length is < 1 or > 5000) return ApiEndpoints.Problem("请选择 1 到 5000 个商品");
                var user = await access.CurrentAsync(principal);
                var result = await products.SyncWebsiteAsync(request.Ids, remove, user!.Name);
                await audit.RecordAsync(user, remove ? "官网下架" : "同步商品到官网", $"成功 {result.Succeeded}，未完成 {result.Failed}", result.Succeeded);
                return Results.Ok(result);
            });
        }
        app.MapGet("/public/products", async (HttpContext http, WebsiteCatalog catalog) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Json(await catalog.ListAsync());
        }).AllowAnonymous().RequireCors(InquiryEndpoints.CorsPolicy);

        app.MapGet("/public/products/{id}", async (string id, HttpContext http, WebsiteCatalog catalog) =>
        {
            var description = await catalog.DescriptionAsync(id);
            if (description is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Json(new { id, description });
        }).AllowAnonymous().RequireCors(InquiryEndpoints.CorsPolicy);
    }
}
