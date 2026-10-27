using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

public class WebsiteCatalogProjectionTests
{
    [Fact]
    public void Project_KeepsOnlineListings_AndDropsDuplicates()
    {
        var listed = new ProductRecord
        {
            Sku = "A",
            Title = "hot selling gym rubber tile",
            RemoteStatus = "online",
            RemoteProductId = "1600123456789",
            Images = ["//sc04.alicdn.com/a.jpg"],
            Price = 12.5m,
            Currency = "CNY",
            LeadTimeDays = 3,
            Unit = "Piece",
            MinimumOrderQuantity = 10,
            Attributes = new(StringComparer.OrdinalIgnoreCase) { ["thickness"] = "20 mm", ["Material"] = "Rubber" },
            Description = "<p onclick=\"alert(1)\">Hello</p><script>alert(1)</script><img src=\"//cdn.example/a.jpg\">",
        };
        var duplicate = new ProductRecord
        {
            Sku = "B",
            Title = "Gym Rubber Tile",
            RemoteStatus = "online",
            RemoteProductId = "999",
            Images = ["https://cdn.example/b.jpg"],
        };
        var offline = new ProductRecord
        {
            Sku = "C",
            Title = "Artificial grass roll",
            RemoteStatus = "offline",
            RemoteProductId = "888",
            Images = ["https://cdn.example/c.jpg"],
        };
        var noImage = new ProductRecord
        {
            Sku = "D",
            Title = "EVA tatami mat",
            RemoteStatus = "online",
            RemoteProductId = "777",
            Images = [],
        };

        var items = WebsiteCatalogProjection.Project([listed, duplicate, offline, noImage]);

        var item = Assert.Single(items);
        Assert.Equal("1600123456789", item.Id);
        Assert.Equal("Gym Rubber Tile", item.Title);
        Assert.Equal("gym-tiles", item.Category);
        Assert.Equal("https://sc04.alicdn.com/a.jpg", item.Images[0]);
        Assert.Equal(12.5m, item.PriceMin);
        Assert.Equal("CNY", item.Currency);
        Assert.Equal(3, item.LeadTimeDays);
        Assert.Equal(10, item.Moq);
        Assert.Equal(["Material", "Rubber"], item.Specs[0]);
        Assert.Equal(["Thickness", "20 mm"], item.Specs[1]);
        Assert.Null(WebsiteCatalogProjection.Find([listed, duplicate], "999"));
        var html = WebsiteCatalogProjection.SanitizeDetail(listed.Description);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Hello</p>", html, StringComparison.Ordinal);
        Assert.Contains("src=\"https://cdn.example/a.jpg\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_AssignsCategoryFromTheCleanedTitle()
    {
        var grass = WebsiteCatalogProjection.Project([Online("Artificial Grass for Garden", "1")]);
        var eva = WebsiteCatalogProjection.Project([Online("cheap China factory EVA tatami", "2")]);

        Assert.Equal("artificial-grass", Assert.Single(grass).Category);
        Assert.Equal("Artificial Grass for Garden", Assert.Single(grass).Title);
        Assert.Equal("eva-mats", Assert.Single(eva).Category);
        Assert.Equal("EVA Tatami", Assert.Single(eva).Title);
    }

    private static ProductRecord Online(string title, string remoteId) => new()
    {
        Title = title,
        RemoteStatus = "online",
        RemoteProductId = remoteId,
        Images = ["https://cdn.example/a.jpg"],
    };
}

public class WebsiteCatalogEndpointTests(ErpFactory factory) : IClassFixture<ErpFactory>
{
    [Fact]
    public async Task PublicCatalog_FollowsEditsInTheErpStore()
    {
        var repo = factory.Services.GetRequiredService<ProductRepository>();
        var created = await repo.CreateAsync(new ProductRecord
        {
            Sku = "WEB-" + Guid.NewGuid().ToString("N")[..8],
            Title = "hot selling gym rubber tile",
            RemoteStatus = "online",
            RemoteProductId = "1600123456789",
            Images = ["//sc04.alicdn.com/a.jpg"],
            Price = 12.5m,
            MinimumOrderQuantity = 10,
            Description = "<p>Hello</p><script>alert(1)</script>",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/public/products");
        request.Headers.Add("Origin", "http://localhost:5180");
        var listResponse = await factory.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal("http://localhost:5180", listResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var list = await listResponse.Content.ReadFromJsonAsync<JsonElement>();
        var item = Assert.Single(list.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal("Gym Rubber Tile", item.GetProperty("title").GetString());

        var detail = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/public/products/1600123456789");
        Assert.Contains("Hello", detail.GetProperty("description").GetString());
        Assert.DoesNotContain("script", detail.GetProperty("description").GetString(), StringComparison.OrdinalIgnoreCase);

        await repo.UpdateAsync(created.Id, product =>
        {
            product.Title = "Outdoor playground paver";
            product.Price = 9m;
        });
        var edited = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/public/products");
        var updated = Assert.Single(edited.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal("Outdoor Playground Paver", updated.GetProperty("title").GetString());
        Assert.Equal("outdoor-playground", updated.GetProperty("category").GetString());
        Assert.Equal(9m, updated.GetProperty("priceMin").GetDecimal());

        await repo.UpdateAsync(created.Id, product => product.RemoteStatus = "offline");
        var hidden = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/public/products");
        Assert.DoesNotContain(hidden.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/public/products/1600123456789")).StatusCode);
    }
}
