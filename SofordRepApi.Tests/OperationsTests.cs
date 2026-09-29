using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

public class OperationsTests
{
    private const string SearchItem = """
        {
          "basic_info": { "product_id": 1601234567890, "title": "Mini Speaker", "description": "desc", "keywords": "mini speaker, gift speaker",
            "product_images": [ { "image_url": "//sc04.alicdn.com/kf/a.jpg" } ], "status": "online", "model_number": "MS-1" },
          "category_info": { "category_id": 201896803, "category_name": "Electronics>Speakers",
            "attributes": [ { "attribute_name": "Color", "attribute_value": "Black" }, { "attribute_name": "Color", "attribute_value": "Red" } ] },
          "logistics_info": { "weight": "0.4", "dimension": { "length": "10", "width": "8", "height": "6" }, "tiered_lead_time": [ { "quantity": 1, "lead_time": "7" } ] },
          "trade_info": { "moq": 50, "unit": "Piece", "inventory": 300,
            "price": { "price_type": "TIERED", "currency": "USD", "tiered_price": [ { "quantity": 50, "price": "5.5" }, { "quantity": 500, "price": "4.9" } ] } }
        }
        """;

    [Fact]
    public void FromRemote_MapsSearchProductInfo()
    {
        using var document = JsonDocument.Parse(SearchItem);

        var product = AlibabaCatalogSync.FromRemote(document.RootElement, DateTimeOffset.UtcNow)!;

        Assert.Equal("MS-1", product.Sku);
        Assert.Equal("1601234567890", product.RemoteProductId);
        Assert.Equal(PublishState.Online, product.PublishState);
        Assert.Equal(["https://sc04.alicdn.com/kf/a.jpg"], product.Images);
        Assert.Equal(["mini speaker", "gift speaker"], product.Keywords);
        Assert.Equal("Black, Red", product.Attributes["Color"]);
        Assert.Equal(2, product.TieredPrices.Count);
        Assert.Equal(5.5m, product.Price);
        Assert.Equal(50, product.MinimumOrderQuantity);
        Assert.Equal(300, product.Stock);
        Assert.Equal(7, product.LeadTimeDays);
        Assert.Equal(0.4m, product.WeightKg);
        Assert.Equal(6m, product.HeightCm);
    }

    [Fact]
    public void FromRemote_RangePriceAndSkuFallbacks()
    {
        using var document = JsonDocument.Parse("""
            { "basic_info": { "product_id": 42, "title": "T", "status": "pending" },
              "trade_info": { "moq": 0, "price": { "price_type": "RANGE", "range_price": { "min_price": "3.2", "max_price": "9" } } },
              "sku_info": [ { "sku_code": "SKU-A", "inventory": 10 }, { "sku_code": "SKU-B", "inventory": 5 } ] }
            """);

        var product = AlibabaCatalogSync.FromRemote(document.RootElement, DateTimeOffset.UtcNow)!;

        Assert.Equal("SKU-A", product.Sku);
        Assert.Equal(3.2m, product.Price);
        Assert.Empty(product.TieredPrices);
        Assert.Equal(1, product.MinimumOrderQuantity);
        Assert.Equal(15, product.Stock);
        Assert.Equal(PublishState.Pending, product.PublishState);
    }

    [Fact]
    public void FromRemote_SkipsItemsWithoutId()
    {
        using var document = JsonDocument.Parse("""{ "basic_info": { "title": "no id" } }""");

        Assert.Null(AlibabaCatalogSync.FromRemote(document.RootElement, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MergeRemote_RefreshesLinksAndCreatesWithoutOverwritingLocalEdits()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        var repo = new ProductRepository(paths, TimeProvider.System);
        var linked = await repo.CreateAsync(new ProductRecord { Sku = "LOCAL-1", Title = "Local title", RemoteProductId = "100", PublishState = PublishState.Pending });
        var unlinked = await repo.CreateAsync(new ProductRecord { Sku = "MS-1", Title = "Mine" });
        await repo.CreateAsync(new ProductRecord { Sku = "TAKEN", RemoteProductId = "900" });

        var remote = new List<ProductRecord>
        {
            new() { Sku = "whatever", Title = "Remote title", RemoteProductId = "100", PublishState = PublishState.Online, RemoteStatus = "online" },
            new() { Sku = "MS-1", Title = "Remote", RemoteProductId = "200", PublishState = PublishState.Online },
            new() { Sku = "TAKEN", Title = "Clash", RemoteProductId = "300", PublishState = PublishState.Pending },
            new() { Sku = "NEW-1", Title = "Brand new", RemoteProductId = "400", PublishState = PublishState.Online }
        };

        var (created, linkedCount, refreshed) = await repo.MergeRemoteAsync(remote, _ => { }, DateTimeOffset.UtcNow);

        Assert.Equal((2, 1, 1), (created, linkedCount, refreshed));
        var all = await repo.GetAllAsync();
        var first = all.Single(x => x.Id == linked.Id);
        Assert.Equal("Local title", first.Title);
        Assert.Equal(PublishState.Online, first.PublishState);
        Assert.Equal("200", all.Single(x => x.Id == unlinked.Id).RemoteProductId);
        Assert.Contains(all, x => x.Sku == "TAKEN-300");
        Assert.Contains(all, x => x.Sku == "NEW-1" && x.RemoteProductId == "400");
    }

    [Fact]
    public void AuthorizationWarning_OnlyNearExpiry()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Assert.Null(ApiEndpoints.AuthorizationWarning(null, now));
        Assert.Null(ApiEndpoints.AuthorizationWarning(new AlibabaTokenRecord { AccessTokenExpiresAt = now.AddDays(1), RefreshTokenExpiresAt = now.AddDays(30) }, now));
        Assert.Contains("3 天", ApiEndpoints.AuthorizationWarning(new AlibabaTokenRecord { AccessTokenExpiresAt = now.AddDays(1), RefreshTokenExpiresAt = now.AddDays(2.5) }, now));
        Assert.Contains("已过期", ApiEndpoints.AuthorizationWarning(new AlibabaTokenRecord { AccessTokenExpiresAt = now.AddDays(-1), RefreshTokenExpiresAt = now.AddDays(-1) }, now));
    }

    [Theory]
    [InlineData("change-this-password", false)]
    [InlineData("short", false)]
    [InlineData("A-Strong-Passw0rd!", true)]
    public void ProductionRejectsPlaceholderPasswords(string password, bool accepted)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:AdminUsername"] = "admin",
            ["Auth:AdminPassword"] = password
        }).Build();
        var env = new TestEnvironment { EnvironmentName = Environments.Production };

        var exception = Record.Exception(() => LocalAdminAuth.ValidateConfiguration(config, env));

        Assert.Equal(accepted, exception is null);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
