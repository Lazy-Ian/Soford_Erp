using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

public class AdvisorTests
{
    private static ProductRecord Mat() => new()
    {
        Sku = "SFD002",
        Title = "6MM EPDM White Rubber Flooring",
        Description = "<div><style>.a{color:red}</style><p>Fire&nbsp;resistant <b>EPDM</b> granules</p></div>",
        CategoryPath = "Rubber & Plastics / Rubber Products",
        Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Material"] = "EPDM" },
        Keywords = ["rubber flooring"]
    };

    [Fact]
    public void Request_AsksForStructuredJsonWithFallbacks()
    {
        var body = ListingAdvisor.BuildRequest(Mat()).ToString();

        Assert.Contains("\"model\": \"claude-opus-5-5\"", body);
        Assert.Contains("\"fallbacks\": \"default\"", body);
        Assert.Contains("json_schema", body);
        Assert.Contains("server-side-fallback-2026-07-01", body);
    }

    [Fact]
    public void Describe_TurnsRichHtmlIntoPlainText()
    {
        var text = ListingAdvisor.Describe(Mat());

        Assert.Contains("Fire resistant EPDM granules", text);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("color:red", text);
        Assert.Contains("Material: EPDM", text);
    }

    [Fact]
    public void Parse_KeepsTitlesPublishable()
    {
        var suggestion = ListingAdvisor.Parse($$"""{"title":"{{new string('a', 150)}}","keywords":["epdm flooring","EPDM flooring","gym floor"],"notes":"加入了材质"}""");

        Assert.Equal(128, suggestion.Title.Length);
        Assert.Equal(["epdm flooring", "gym floor"], suggestion.Keywords);
        Assert.Throws<ListingAdvisorException>(() => ListingAdvisor.Parse("not json"));
    }

    [Fact]
    public void WithoutApiKey_TheFeatureIsOff()
    {
        var saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        try
        {
            var advisor = new ListingAdvisor(new ConfigurationBuilder().Build(), NullLogger<ListingAdvisor>.Instance);
            Assert.False(advisor.Enabled);
            Assert.ThrowsAsync<ListingAdvisorException>(() => advisor.SuggestAsync(Mat()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
        }
    }
}
