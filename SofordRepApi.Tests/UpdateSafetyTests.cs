using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Updating an imported listing must never destroy content that lives only on Alibaba.</summary>
public class UpdateSafetyTests
{
    /// <summary>Answers each API path with a canned body and records which paths were called.</summary>
    private sealed class FakeGateway : HttpMessageHandler
    {
        public Dictionary<string, string> Bodies { get; } = [];
        public List<string> Calls { get; } = [];
        public List<string> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/rest", "");
            Calls.Add(path);
            Sent.Add(request.Content is null ? "" : Uri.UnescapeDataString(await request.Content.ReadAsStringAsync(cancellationToken)));
            var body = Bodies.TryGetValue(path, out var canned) ? canned : """{"code":"0"}""";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task<(ProductOperations Operations, ProductRepository Products, FakeGateway Gateway)> SetupAsync()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alibaba:AppKey"] = "k",
            ["Alibaba:AppSecret"] = "s"
        }).Build();
        var gateway = new FakeGateway();
        var transport = new AlibabaTransport(new Factory(gateway), new AlibabaApiLogStore(paths), TimeProvider.System, NullLogger<AlibabaTransport>.Instance);
        var store = new AlibabaAccountStore(paths, new EphemeralDataProtectionProvider(), TimeProvider.System, NullLogger<AlibabaAccountStore>.Instance);
        await store.UpdateAsync(accounts =>
        {
            accounts.Add(new AlibabaAccount
            {
                Id = Guid.NewGuid(), Name = "main", IsDefault = true,
                Token = new AlibabaTokenRecord { AccessToken = "at", RefreshToken = "rt", AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(9), RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(9) }
            });
            return true;
        });
        var tokens = new AlibabaTokenService(transport, store, config, TimeProvider.System);
        var client = new AlibabaClient(transport, tokens, config);
        var products = new ProductRepository(paths, TimeProvider.System);
        var quality = new ProductQualityService(new CategoryAttributeService(paths, TimeProvider.System));
        var operations = new ProductOperations(products, quality, client, new ProductLocks(), TimeProvider.System, tokens, NullLogger<ProductOperations>.Instance);
        return (operations, products, gateway);
    }

    private static ProductRecord Imported(DateTimeOffset importedAt) => new()
    {
        Id = Guid.NewGuid(),
        Sku = "MS-1",
        Title = "Portable Wireless Bluetooth Speaker Waterproof IPX7",
        Description = "20W stereo sound, 12 hour battery.",
        CategoryId = "201896803",
        Price = 5m,
        MinimumOrderQuantity = 10,
        Images = ["https://example.com/a.jpg"],
        Keywords = ["a", "b", "c"],
        Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Material"] = "ABS", ["Color"] = "Black", ["Brand"] = "X" },
        RemoteProductId = "1601",
        PublishState = PublishState.Online,
        PublishedContentAt = importedAt,
        ContentUpdatedAt = importedAt.AddMinutes(5)
    };

    private static string ListingJson(DateTimeOffset modified, int variants = 0) => JsonSerializer.Serialize(new
    {
        success = true,
        product_info = new
        {
            basic_info = new { product_id = 1601, title = "Title on Alibaba", description = "Rich description", status = "online", audit_status = "approved", last_modified_timestamp = modified.ToUnixTimeMilliseconds() },
            trade_info = new { moq = 20, unit = "Piece", price = new { price_type = "TIERED", currency = "USD", tiered_price = new[] { new { quantity = 20, price = "6" } } } },
            sku_info = Enumerable.Range(0, variants).Select(i => new { sku_code = $"V{i}", inventory = 1 }).ToArray()
        }
    });

    [Fact]
    public async Task Update_RefusedWhenAlibabaWasEditedAfterTheLocalCopy()
    {
        var (operations, products, gateway) = await SetupAsync();
        var importedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var product = await products.CreateAsync(Imported(importedAt));
        gateway.Bodies["/alibaba/icbu/product/get/v2"] = ListingJson(importedAt.AddDays(1));

        var result = await operations.PublishAsync(product.Id);

        Assert.False(result.Success);
        Assert.Contains("比本系统的副本新", result.Message);
        Assert.DoesNotContain("/alibaba/icbu/product/update/v2", gateway.Calls);
        Assert.Equal(PublishState.Online, (await products.GetAsync(product.Id))!.PublishState);
    }

    [Fact]
    public async Task Update_RefusedForListingsWithVariants()
    {
        var (operations, products, gateway) = await SetupAsync();
        var importedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var product = await products.CreateAsync(Imported(importedAt));
        gateway.Bodies["/alibaba/icbu/product/get/v2"] = ListingJson(importedAt.AddDays(-1), variants: 3);

        var result = await operations.PublishAsync(product.Id);

        Assert.False(result.Success);
        Assert.Contains("3 个规格", result.Message);
        Assert.DoesNotContain("/alibaba/icbu/product/update/v2", gateway.Calls);
    }

    [Fact]
    public async Task Update_SentWhenSafe_WithoutZeroStock()
    {
        var (operations, products, gateway) = await SetupAsync();
        var importedAt = DateTimeOffset.UtcNow.AddDays(-2);
        var product = await products.CreateAsync(Imported(importedAt));
        gateway.Bodies["/alibaba/icbu/product/get/v2"] = ListingJson(importedAt.AddDays(-1));

        var result = await operations.PublishAsync(product.Id);

        Assert.True(result.Success, result.Message);
        var sent = gateway.Sent[gateway.Calls.IndexOf("/alibaba/icbu/product/update/v2")];
        Assert.DoesNotContain("\"inventory\"", sent);
        Assert.NotNull((await products.GetAsync(product.Id))!.RemoteModifiedAt);
    }

    [Fact]
    public async Task Refresh_TakesAlibabaContentButKeepsTheSku()
    {
        var (operations, products, gateway) = await SetupAsync();
        var modified = DateTimeOffset.UtcNow.AddHours(-1);
        var product = Imported(DateTimeOffset.UtcNow.AddDays(-2));
        product.ContentUpdatedAt = product.PublishedContentAt;
        product = await products.CreateAsync(product);
        gateway.Bodies["/alibaba/icbu/product/get/v2"] = ListingJson(modified, variants: 2);

        var result = await operations.RefreshContentAsync(product.Id, discardLocalChanges: false);

        Assert.True(result.Success, result.Message);
        var after = (await products.GetAsync(product.Id))!;
        Assert.Equal("MS-1", after.Sku);
        Assert.Equal("Title on Alibaba", after.Title);
        Assert.Equal("Rich description", after.Description);
        Assert.Equal(2, after.RemoteSkuCount);
        Assert.Equal(modified.ToUnixTimeMilliseconds(), after.RemoteModifiedAt!.Value.ToUnixTimeMilliseconds());
        Assert.False(after.HasUnpublishedChanges);
    }

    [Fact]
    public async Task Refresh_KeepsUnpublishedLocalEditsUnlessDiscarding()
    {
        var (operations, products, gateway) = await SetupAsync();
        var product = await products.CreateAsync(Imported(DateTimeOffset.UtcNow.AddDays(-2)));
        gateway.Bodies["/alibaba/icbu/product/get/v2"] = ListingJson(DateTimeOffset.UtcNow);

        Assert.False((await operations.RefreshContentAsync(product.Id, discardLocalChanges: false)).Success);
        Assert.Equal("Portable Wireless Bluetooth Speaker Waterproof IPX7", (await products.GetAsync(product.Id))!.Title);

        Assert.True((await operations.RefreshContentAsync(product.Id, discardLocalChanges: true)).Success);
        Assert.Equal("Title on Alibaba", (await products.GetAsync(product.Id))!.Title);
    }
}
