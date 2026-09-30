using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

/// <summary>Suggested wording for a listing. Nothing is applied until a person accepts it in the editor.</summary>
public sealed record ListingSuggestion(string Title, string[] Keywords, string Notes);

public sealed class ListingAdvisorException(string message) : Exception(message);

/// <summary>
/// Asks Claude for a better English B2B title and buyer search keywords for one listing. Optional: enabled only when
/// an Anthropic API key is configured (Anthropic__ApiKey), so the ERP works the same without it.
/// </summary>
public sealed partial class ListingAdvisor(IConfiguration config, ILogger<ListingAdvisor> logger)
{
    private const string Model = "claude-opus-5-5";

    private const string Instructions = """
        You improve product listings on Alibaba.com, a B2B marketplace where overseas buyers (importers, wholesalers,
        brand owners, contractors) search in English for suppliers. Given one listing, propose:

        - title: a clear English title of at most 128 characters that a buyer would search for. Lead with the product
          type, then the attributes buyers filter on (material, size, thickness, standard/certification, application).
          Keep facts from the listing; never invent certifications, sizes or claims that the listing does not support.
          No brand-new superlatives, no keyword stuffing, no repeated words, no ALL CAPS, no Chinese.
        - keywords: 3 to 10 distinct buyer search phrases (2-5 words each, lower case), ordered by how likely a buyer
          is to type them. Cover synonyms and application-based searches, not just reorderings of the title.
        - notes: one or two short sentences in Simplified Chinese telling the seller what you changed and why.

        If the current title is already good, keep it (you may return it unchanged) and say so in notes.
        """;

    private AnthropicClient? _client;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    private string? ApiKey => config["Anthropic:ApiKey"] ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    public async Task<ListingSuggestion> SuggestAsync(ProductRecord product, CancellationToken cancellationToken = default)
    {
        if (!Enabled) throw new ListingAdvisorException("未配置 Anthropic API Key（Anthropic__ApiKey），AI 建议不可用。");

        var client = _client ??= new AnthropicClient { ApiKey = ApiKey };
        var parameters = BuildRequest(product);

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(parameters, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Listing suggestion for {Sku} failed", product.Sku);
            throw new ListingAdvisorException($"调用 AI 服务失败：{ex.Message}");
        }

        if (response.StopReason == "refusal") throw new ListingAdvisorException("AI 服务拒绝处理这个商品，请手动修改。");
        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return Parse(text);
    }

    public static MessageCreateParams BuildRequest(ProductRecord product) => new()
    {
        Model = Model,
        MaxTokens = 16000,
        // Rewriting one listing is routine work; medium keeps it quick without giving up quality.
        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium, Format = new BetaJsonOutputFormat { Schema = Schema } },
        // A declined request is re-served by the model the API picks for that refusal category.
        Betas = ["server-side-fallback-2026-07-01"],
        Fallbacks = new Default(),
        System = Instructions,
        Messages = [new() { Role = Role.User, Content = Describe(product) }]
    };

    public static ListingSuggestion Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var title = root.GetProperty("title").GetString()?.Trim() ?? "";
            var keywords = root.GetProperty("keywords").EnumerateArray()
                .Select(x => x.GetString()?.Trim() ?? "").Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
            if (title.Length == 0) throw new ListingAdvisorException("AI 没有给出标题建议。");
            // The schema cannot express a length limit the model always honours; never hand back an unpublishable title.
            if (title.Length > ProductQualityService.MaxTitleLength) title = title[..ProductQualityService.MaxTitleLength].TrimEnd();
            return new ListingSuggestion(title, keywords, notes);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ListingAdvisorException("AI 返回的内容无法识别，请重试。");
        }
    }

    /// <summary>The listing as plain text. Descriptions are imported as rich HTML; tags and styles carry no meaning here.</summary>
    public static string Describe(ProductRecord product)
    {
        // Decode entities before collapsing whitespace: &nbsp; becomes a non-breaking space.
        var description = Whitespace().Replace(WebUtility.HtmlDecode(Tags().Replace(StyleBlocks().Replace(product.Description, " "), " ")), " ").Trim();
        var lines = new List<string>
        {
            $"Current title: {product.Title}",
            $"Current keywords: {(product.Keywords.Length > 0 ? string.Join(" | ", product.Keywords) : "(none)")}",
            $"Category: {(product.CategoryPath.Length > 0 ? product.CategoryPath : product.CategoryName)}",
            $"Model number: {product.ModelNumber}",
            $"Attributes: {(product.Attributes.Count > 0 ? string.Join("; ", product.Attributes.Select(x => $"{x.Key}: {x.Value}")) : "(none)")}",
            $"Minimum order: {product.MinimumOrderQuantity} {product.Unit}"
        };
        if (description.Length > 0) lines.Add($"Description:\n{description}");
        return string.Join("\n", lines);
    }

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            title = new { type = "string", description = "Improved English title, at most 128 characters" },
            keywords = new { type = "array", items = new { type = "string" }, description = "3 to 10 buyer search phrases" },
            notes = new { type = "string", description = "What changed and why, in Simplified Chinese" }
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "title", "keywords", "notes" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false)
    };

    [GeneratedRegex(@"<(style|script)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StyleBlocks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
