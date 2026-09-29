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
    public string? RemoteStatus { get; set; }
    public string? RemoteStatusMessage { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }

    public List<QualityIssue> QualityIssues { get; set; } = [];
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
        Keywords ??= [];
        Images ??= [];
        TieredPrices ??= [];
        QualityIssues ??= [];
    }

    public ProductRecord Clone() => JsonSerializer.Deserialize<ProductRecord>(JsonSerializer.Serialize(this, JsonFile.Options), JsonFile.Options)!;

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
        Attributes = new Dictionary<string, string>(
            (draft.Attributes ?? new()).Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
                .ToDictionary(x => x.Key.Trim(), x => x.Value.Trim(), StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
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
    bool AiOptimize);

/// <summary>Reads both the current names and the previous release's (Draft/Ready/Published) without failing.</summary>
public sealed class PublishStateConverter : JsonConverter<PublishState>
{
    public override PublishState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(PublishState), number))
        {
            return (PublishState)number;
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
