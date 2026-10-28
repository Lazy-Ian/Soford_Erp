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
    public async Task PublicCatalog_ChangesOnlyAfterExplicitSync_AndProtectsLastGoodPublication()
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
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/website/catalog/sync", new { productIds = new[] { created.Id } })).StatusCode);
        var admin = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = ErpFactory.AdminPassword, remember = false })).StatusCode);
        var before = await anonymous.GetFromJsonAsync<JsonElement>("/public/products");
        Assert.DoesNotContain(before.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        var firstSync = await admin.PostAsJsonAsync("/api/website/catalog/sync", new { productIds = new[] { created.Id } });
        Assert.Equal(HttpStatusCode.OK, firstSync.StatusCode);
        Assert.Equal(1, (await firstSync.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("succeeded").GetInt32());
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
            product.Description = "<p>Updated details</p>";
            product.RemoteStatus = "offline";
        });
        var edited = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/public/products");
        var updated = Assert.Single(edited.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal("Gym Rubber Tile", updated.GetProperty("title").GetString());
        Assert.Equal(12.5m, updated.GetProperty("priceMin").GetDecimal());
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/catalog/products");
        var row = rows.EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == created.Id);
        Assert.True(row.GetProperty("hasWebsiteChanges").GetBoolean());
        Assert.Equal("", row.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("websitePublication").ValueKind);
        Assert.Equal("1600123456789", row.GetProperty("websitePublicId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/website/catalog/sync", new { productIds = new[] { created.Id } })).StatusCode);
        var synced = await anonymous.GetFromJsonAsync<JsonElement>("/public/products");
        var published = synced.EnumerateArray().Single(x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal("Outdoor Playground Paver", published.GetProperty("title").GetString());
        Assert.Equal(9m, published.GetProperty("priceMin").GetDecimal());
        Assert.Contains("Updated details", (await anonymous.GetFromJsonAsync<JsonElement>("/public/products/1600123456789")).GetProperty("description").GetString());

        await repo.UpdateAsync(created.Id, product => product.Description = "");
        var invalid = await admin.PostAsJsonAsync("/api/website/catalog/sync", new { productIds = new[] { created.Id } });
        Assert.Equal(1, (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("failed").GetInt32());
        Assert.Contains("Updated details", (await anonymous.GetFromJsonAsync<JsonElement>("/public/products/1600123456789")).GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/website/catalog/unpublish", new { productIds = new[] { created.Id } })).StatusCode);
        var hidden = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/public/products");
        Assert.DoesNotContain(hidden.EnumerateArray(), x => x.GetProperty("id").GetString() == "1600123456789");
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/public/products/1600123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/website/catalog/sync", new { productIds = Array.Empty<Guid>() })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/users", new { username = "web-op", password = "operator-password", role = "Operator", accountIds = Array.Empty<Guid>() })).StatusCode);
        var op = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/auth/login", new { username = "web-op", password = "operator-password", remember = false })).StatusCode);
        foreach (var action in new[] { "sync", "unpublish" })
            Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/website/catalog/{action}", new { productIds = new[] { created.Id } })).StatusCode);
    }
}
