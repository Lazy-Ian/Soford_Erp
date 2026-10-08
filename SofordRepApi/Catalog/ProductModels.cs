using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PriceTier(int Quantity, decimal Price);

/// <summary>Local data completeness, derived from quality checks.</summary>
public enum LocalState
{
    Incomplete,
    Ready
}

/// <summary>Where the product stands on Alibaba. Never overwritten by local quality checks.</summary>
public enum PublishState
{
    NotPublished,
    Publishing,
    Pending,
    Online,
    Offline,
    Failed
}

public enum QualitySeverity
{
    Info,
    Warning,
    Blocker
}

public sealed record QualityIssue(QualitySeverity Severity, string Field, string Message);

public sealed class ProductRecord
{
    public Guid Id { get; set; }
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string[] Keywords { get; set; } = [];
    public string BrandName { get; set; } = "";
    public string ModelNumber { get; set; } = "";
    public string Language { get; set; } = "en_US";

    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryPath { get; set; } = "";
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Currency { get; set; } = "USD";
    public decimal Price { get; set; }
    public List<PriceTier> TieredPrices { get; set; } = [];
    public int MinimumOrderQuantity { get; set; } = 1;
    public string Unit { get; set; } = "Piece";
    public int Stock { get; set; }

    public int LeadTimeDays { get; set; }
    public string ShippingTemplateId { get; set; } = "";
    public decimal? WeightKg { get; set; }
    public decimal? LengthCm { get; set; }
    public decimal? WidthCm { get; set; }
    public decimal? HeightCm { get; set; }

    public string[] Images { get; set; } = [];
    public bool AiOptimize { get; set; }

    public LocalState LocalState { get; set; }
    [JsonConverter(typeof(PublishStateConverter))]
    public PublishState PublishState { get; set; }
    public string? RemoteProductId { get; set; }

    /// <summary>Alibaba account (main or sub-account) responsible for the listing, from owner_ali_id.</summary>
    public string? OwnerAliId { get; set; }

    /// <summary>Account explicitly chosen to publish/manage this product; null means the owner's or the default account.</summary>
    public Guid? AccountId { get; set; }
    public string? RemoteStatus { get; set; }
    public string? RemoteStatusMessage { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    /// <summary>When listing content last changed locally (edit/import), as opposed to status-only writes.</summary>
    public DateTimeOffset? ContentUpdatedAt { get; set; }

    /// <summary>ContentUpdatedAt of the version last sent to Alibaba successfully.</summary>
    public DateTimeOffset? PublishedContentAt { get; set; }

    /// <summary>
    /// Alibaba's last_modified_timestamp of the listing version the local content is based on (content copied from
    /// Alibaba, or our own write). A newer timestamp on Alibaba means someone edited the listing elsewhere.
    /// </summary>
    public DateTimeOffset? RemoteModifiedAt { get; set; }

    /// <summary>Variants (sku_info) on Alibaba. This system edits a single price and stock, so more than one cannot be updated safely.</summary>
    public int? RemoteSkuCount { get; set; }

    /// <summary>When the listing content was last copied from Alibaba.</summary>
    public DateTimeOffset? ContentRefreshedAt { get; set; }

    /// <summary>Set while a create call is in flight or ended without a known outcome; enables the duplicate check.</summary>
    public DateTimeOffset? CreateAttemptedAt { get; set; }

    public bool HasUnpublishedChanges =>
        !string.IsNullOrWhiteSpace(RemoteProductId) && ContentUpdatedAt is not null && ContentUpdatedAt != PublishedContentAt;

    public List<QualityIssue> QualityIssues { get; set; } = [];

    /// <summary>User id of whoever created the product here (imports from Alibaba have none).</summary>
    public string? CreatedBy { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Fields written by the previous release; migrated into Images on load and never written back.
    [JsonPropertyName("mainImageUrl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyMainImageUrl { get; set; }

    [JsonPropertyName("detailImageUrls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? LegacyDetailImageUrls { get; set; }

    [JsonPropertyName("lastPublishMessage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLastPublishMessage { get; set; }

    public void MigrateLegacyFields()
    {
        if (Images.Length == 0 && (!string.IsNullOrWhiteSpace(LegacyMainImageUrl) || LegacyDetailImageUrls is { Length: > 0 }))
        {
            Images = new[] { LegacyMainImageUrl ?? "" }.Concat(LegacyDetailImageUrls ?? [])
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        RemoteStatusMessage ??= LegacyLastPublishMessage;
        LegacyMainImageUrl = null;
        LegacyDetailImageUrls = null;
        LegacyLastPublishMessage = null;
        Attributes = new Dictionary<string, string>(Attributes ?? new(), StringComparer.OrdinalIgnoreCase);
        // Imports before keywords were split per line stored Alibaba's whole newline-separated list as one keyword.
        Keywords = (Keywords ?? []).SelectMany(x => x.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        Images ??= [];
        TieredPrices ??= [];
        QualityIssues ??= [];
    }

    /// <summary>Fast deep copy: strings and records are immutable, so only the collections need copying.</summary>
    public ProductRecord Copy()
    {
        var copy = (ProductRecord)MemberwiseClone();
        copy.Keywords = [.. Keywords];
        copy.Images = [.. Images];
        copy.Attributes = new Dictionary<string, string>(Attributes, StringComparer.OrdinalIgnoreCase);
        copy.TieredPrices = [.. TieredPrices];
        copy.QualityIssues = [.. QualityIssues];
        copy.LegacyDetailImageUrls = LegacyDetailImageUrls is null ? null : [.. LegacyDetailImageUrls];
        return copy;
    }

    /// <summary>Copies editable listing fields; identity, remote state and timestamps are kept.</summary>
    public void ApplyDraft(ProductDraft draft)
    {
        Sku = Clean(draft.Sku);
        Title = Clean(draft.Title);
        Description = Clean(draft.Description);
        Keywords = CleanList(draft.Keywords).Take(10).ToArray();
        BrandName = Clean(draft.BrandName);
        ModelNumber = Clean(draft.ModelNumber);
        Language = string.IsNullOrWhiteSpace(draft.Language) ? "en_US" : draft.Language.Trim();
        CategoryId = Clean(draft.CategoryId);
        CategoryName = Clean(draft.CategoryName);
        CategoryPath = Clean(draft.CategoryPath);
        Attributes = CleanAttributes(draft.Attributes);
        Currency = string.IsNullOrWhiteSpace(draft.Currency) ? "USD" : draft.Currency.Trim().ToUpperInvariant();
        Price = draft.Price;
        TieredPrices = (draft.TieredPrices ?? []).Where(x => x.Quantity > 0 || x.Price > 0).OrderBy(x => x.Quantity).ToList();
        MinimumOrderQuantity = draft.MinimumOrderQuantity;
        Unit = string.IsNullOrWhiteSpace(draft.Unit) ? "Piece" : draft.Unit.Trim();
        Stock = draft.Stock;
        LeadTimeDays = draft.LeadTimeDays;
        ShippingTemplateId = Clean(draft.ShippingTemplateId);
        WeightKg = draft.WeightKg;
        LengthCm = draft.LengthCm;
        WidthCm = draft.WidthCm;
        HeightCm = draft.HeightCm;
        Images = CleanList(draft.Images).ToArray();
        AiOptimize = draft.AiOptimize;
    }

    /// <summary>
    /// Import-time update of an existing product: only columns present in the sheet are copied,
    /// so a sheet with just Sku + Stock never blanks titles, images or prices.
    /// </summary>
    public void ApplyImportedFields(ProductDraft draft, IReadOnlySet<string> present)
    {
        var incoming = new ProductRecord();
        incoming.ApplyDraft(draft);
        bool Has(params string[] fields) => fields.Any(present.Contains);

        if (Has("Title")) Title = incoming.Title;
        if (Has("Description")) Description = incoming.Description;
        if (Has("Keywords")) Keywords = incoming.Keywords;
        if (Has("BrandName")) BrandName = incoming.BrandName;
        if (Has("ModelNumber")) ModelNumber = incoming.ModelNumber;
        if (Has("Language")) Language = incoming.Language;
        if (Has("CategoryId") && CategoryId != incoming.CategoryId)
        {
            CategoryId = incoming.CategoryId;
            // A new category invalidates the old name/path unless the sheet provides them too.
            if (!Has("CategoryName")) CategoryName = "";
            if (!Has("CategoryPath")) CategoryPath = "";
        }

        if (Has("CategoryName")) CategoryName = incoming.CategoryName;
        if (Has("CategoryPath")) CategoryPath = incoming.CategoryPath;
        if (Has("Attributes")) Attributes = incoming.Attributes;
        if (Has("Currency")) Currency = incoming.Currency;
        if (Has("Price"))
        {
            // Tiers take precedence over the unit price, so a sheet that sets only Price means "single price".
            if (!Has("TieredPrices") && Price != incoming.Price) TieredPrices = [];
            Price = incoming.Price;
        }

        if (Has("TieredPrices")) TieredPrices = incoming.TieredPrices;
        if (Has("MOQ")) MinimumOrderQuantity = incoming.MinimumOrderQuantity;
        if (Has("Unit")) Unit = incoming.Unit;
        if (Has("Stock")) Stock = incoming.Stock;
        if (Has("LeadTimeDays")) LeadTimeDays = incoming.LeadTimeDays;
        if (Has("ShippingTemplateId")) ShippingTemplateId = incoming.ShippingTemplateId;
        if (Has("WeightKg")) WeightKg = incoming.WeightKg;
        if (Has("LengthCm")) LengthCm = incoming.LengthCm;
        if (Has("WidthCm")) WidthCm = incoming.WidthCm;
        if (Has("HeightCm")) HeightCm = incoming.HeightCm;
        if (Has("Images", "MainImageUrl", "DetailImageUrls")) Images = incoming.Images;
    }

    /// <summary>Takes over the listing content read from Alibaba; identity, SKU, account and local timestamps are kept.</summary>
    public void ApplyRemoteContent(ProductRecord remote)
    {
        Title = remote.Title;
        Description = remote.Description;
        Keywords = remote.Keywords;
        ModelNumber = remote.ModelNumber;
        Language = remote.Language;
        Images = remote.Images;
        CategoryId = remote.CategoryId;
        CategoryPath = remote.CategoryPath;
        Attributes = remote.Attributes;
        Currency = remote.Currency;
        Price = remote.Price;
        TieredPrices = remote.TieredPrices;
        MinimumOrderQuantity = remote.MinimumOrderQuantity;
        Unit = remote.Unit;
        Stock = remote.Stock;
        LeadTimeDays = remote.LeadTimeDays;
        ShippingTemplateId = remote.ShippingTemplateId;
        WeightKg = remote.WeightKg;
        LengthCm = remote.LengthCm;
        WidthCm = remote.WidthCm;
        HeightCm = remote.HeightCm;
        OwnerAliId = remote.OwnerAliId ?? OwnerAliId;
        RemoteStatus = remote.RemoteStatus;
        PublishState = remote.PublishState;
        RemoteModifiedAt = remote.RemoteModifiedAt;
        RemoteSkuCount = remote.RemoteSkuCount;
    }

    /// <summary>Serialized listing content, used to tell real edits apart from no-op re-imports.</summary>
    public string ContentSignature() => JsonSerializer.Serialize(new
    {
        Sku, Title, Description, Keywords, BrandName, ModelNumber, Language, CategoryId, CategoryName, CategoryPath,
        Attributes = Attributes.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToArray(),
        Currency, Price, TieredPrices, MinimumOrderQuantity, Unit, Stock, LeadTimeDays, ShippingTemplateId,
        WeightKg, LengthCm, WidthCm, HeightCm, Images, AiOptimize
    });

    private static Dictionary<string, string> CleanAttributes(Dictionary<string, string>? attributes)
    {
        // Keys differing only by case or whitespace collapse; the last value wins.
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in attributes ?? new())
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                result[key.Trim()] = value.Trim();
            }
        }

        return result;
    }

    private static string Clean(string? value) => value?.Trim() ?? "";

    private static IEnumerable<string> CleanList(IEnumerable<string>? values) =>
        (values ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed record ProductDraft(
    string? Sku,
    string? Title,
    string? Description,
    string[]? Keywords,
    string? BrandName,
    string? ModelNumber,
    string? Language,
    string? CategoryId,
    string? CategoryName,
    string? CategoryPath,
    Dictionary<string, string>? Attributes,
    string? Currency,
    decimal Price,
    List<PriceTier>? TieredPrices,
    int MinimumOrderQuantity,
    string? Unit,
    int Stock,
    int LeadTimeDays,
    string? ShippingTemplateId,
    decimal? WeightKg,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    string[]? Images,
    bool AiOptimize,
    DateTimeOffset? ExpectedUpdatedAt = null);

/// <summary>Reads both the current names and the previous release's (Draft/Ready/Published) without failing.</summary>
public sealed class PublishStateConverter : JsonConverter<PublishState>
{
    public override PublishState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // This converter always writes names, so numbers only come from the previous release,
        // whose enum was Draft=0, Ready=1, Published=2, Failed=3.
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return number switch
            {
                2 => PublishState.Online,
                3 => PublishState.Failed,
                _ => PublishState.NotPublished
            };
        }

        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (Enum.TryParse<PublishState>(text, true, out var state))
        {
            return state;
        }

        return text?.ToLowerInvariant() switch
        {
            "published" => PublishState.Online,
            _ => PublishState.NotPublished
        };
    }

    public override void Write(Utf8JsonWriter writer, PublishState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
