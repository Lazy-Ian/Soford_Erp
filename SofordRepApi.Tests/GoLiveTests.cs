using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>Regression tests for the go-live fixes: in-memory product cache, damaged files, account file recovery.</summary>
public class GoLiveTests
{
    private static AppPaths NewPaths() => new(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));

    private static ProductRecord Product(string sku) => new()
    {
        Id = Guid.NewGuid(),
        Sku = sku,
        Title = "Title " + sku,
        Keywords = ["a"],
        Images = ["https://example.com/a.jpg"],
        Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Color"] = "Black" },
        TieredPrices = [new PriceTier(10, 2m)]
    };

    [Fact]
    public async Task Repository_ReadsReturnCopiesThatCannotChangeTheStore()
    {
        var repo = new ProductRepository(NewPaths(), TimeProvider.System);
        var created = await repo.CreateAsync(Product("A"));

        var read = (await repo.GetAllAsync()).Single();
        read.Title = "changed";
        read.Keywords[0] = "changed";
        read.Attributes["Color"] = "changed";
        read.TieredPrices.Clear();
        created.Title = "changed by creator";

        var again = await repo.GetAsync(created.Id);
        Assert.Equal("Title A", again!.Title);
        Assert.Equal("a", again.Keywords[0]);
        Assert.Equal("Black", again.Attributes["Color"]);
        Assert.Single(again.TieredPrices);
    }

    [Fact]
    public async Task Repository_WritesReachTheFile()
    {
        var paths = NewPaths();
        var repo = new ProductRepository(paths, TimeProvider.System);
        var created = await repo.CreateAsync(Product("A"));
        await repo.UpdateAsync(created.Id, p => p.Stock = 42);

        var fresh = new ProductRepository(paths, TimeProvider.System);
        Assert.Equal(42, (await fresh.GetAsync(created.Id))!.Stock);
    }

    [Fact]
    public async Task Repository_FailedChangeDoesNotLeakIntoCache()
    {
        var repo = new ProductRepository(NewPaths(), TimeProvider.System);
        await repo.CreateAsync(Product("A"));
        var b = await repo.CreateAsync(Product("B"));

        // Renaming B to A applies the change in memory, then fails the uniqueness check.
        await Assert.ThrowsAsync<SkuConflictException>(() => repo.UpdateAsync(b.Id, p => p.Sku = "A"));

        Assert.Equal("B", (await repo.GetAsync(b.Id))!.Sku);
    }

    [Fact]
    public async Task EmptyDataFile_IsReportedInsteadOfTreatedAsNoProducts()
    {
        var paths = NewPaths();
        await File.WriteAllTextAsync(paths.File("products.json"), "");
        var repo = new ProductRepository(paths, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidDataException>(() => repo.GetAllAsync());
        // A write must not replace the damaged file with an empty list either.
        await Assert.ThrowsAsync<InvalidDataException>(() => repo.CreateAsync(Product("A")));
        Assert.Equal(0, new FileInfo(paths.File("products.json")).Length);
    }

    [Fact]
    public async Task AccountFile_UnreadableWithCurrentKeys_IsMovedAsideNotOverwritten()
    {
        var paths = NewPaths();
        var writer = new AlibabaAccountStore(paths, DataProtectionProvider.Create("keys-a"), TimeProvider.System, NullLogger<AlibabaAccountStore>.Instance);
        await writer.UpdateAsync(accounts =>
        {
            accounts.Add(new AlibabaAccount { Id = Guid.NewGuid(), Name = "张三", OwnerAliIds = ["1"] });
            return true;
        });
        var original = await File.ReadAllTextAsync(paths.File("alibaba-accounts.json"));

        var reader = new AlibabaAccountStore(paths, DataProtectionProvider.Create("keys-b"), TimeProvider.System, NullLogger<AlibabaAccountStore>.Instance);
        Assert.Empty(await reader.GetAllAsync());

        var aside = Directory.GetFiles(paths.DataPath, "alibaba-accounts.json.unreadable-*").Single();
        Assert.Equal(original, await File.ReadAllTextAsync(aside));
    }

    [Fact]
    public void Parser_TypedErrorWithoutCode_IsFailure()
    {
        // Real answer of product/status/get/v2 for a listing created in the seller backend.
        var (success, code, message, _, _) = AlibabaResponseParser.Parse(200, "{\"type\":\"ISP\",\"message\":\"Product not found.\",\"request_id\":\"1\"}");

        Assert.False(success);
        Assert.Equal("ISP", code);
        Assert.Equal("Product not found.", message);
    }

    [Theory]
    [InlineData("online", "approved", PublishState.Online)]
    [InlineData("offline", "rejected", PublishState.Failed)]
    [InlineData("offline", null, PublishState.Offline)]
    public void MapRemote_AuditStatusWinsOverOnlineStatus(string status, string? audit, PublishState expected) =>
        Assert.Equal(expected, ProductOperations.MapRemote(status, audit, PublishState.Pending).State);

    [Fact]
    public async Task Import_MatchesByAlibabaIdWhenSkusAreNotUnique()
    {
        var repo = new ProductRepository(NewPaths(), TimeProvider.System);
        var a = Product("MAT-1"); a.RemoteProductId = "111";
        var b = Product("MAT-1-222"); b.RemoteProductId = "222";
        await repo.CreateAsync(a);
        await repo.CreateAsync(b);
        var parsed = ProductImportService.ParseTable(
        [
            (1, ["Alibaba 商品 ID", "Title"]),
            (2, ["222", "New title for 222"]),
            (3, ["999", "Unknown listing"])
        ]);
        var warnings = new List<string>();

        var (created, updated) = await repo.ImportAsync(parsed.Rows, _ => { }, warnings);

        Assert.Equal(0, created);
        Assert.Equal(1, updated);
        Assert.Contains(warnings, w => w.Contains("999"));
        var all = await repo.GetAllAsync();
        Assert.Equal("New title for 222", all.Single(x => x.RemoteProductId == "222").Title);
        Assert.Equal("Title MAT-1", all.Single(x => x.RemoteProductId == "111").Title);
        Assert.Equal("MAT-1-222", all.Single(x => x.RemoteProductId == "222").Sku);
    }

    [Fact]
    public async Task MarkMissing_OnlyForOwnersTheReadCoveredAndNotInReview()
    {
        var repo = new ProductRepository(NewPaths(), TimeProvider.System);
        ProductRecord Linked(string sku, string id, string owner, PublishState state)
        {
            var p = Product(sku);
            p.RemoteProductId = id;
            p.OwnerAliId = owner;
            p.PublishState = state;
            return p;
        }

        await repo.CreateAsync(Linked("SEEN", "1", "A", PublishState.Online));
        await repo.CreateAsync(Linked("GONE", "2", "A", PublishState.Online));
        await repo.CreateAsync(Linked("OTHER-OWNER", "3", "B", PublishState.Online));
        await repo.CreateAsync(Linked("IN-REVIEW", "4", "A", PublishState.Pending));

        var marked = await repo.MarkMissingAsync(new HashSet<string> { "1" }, new HashSet<string> { "A" }, DateTimeOffset.UtcNow);

        Assert.Equal(1, marked);
        var all = await repo.GetAllAsync();
        Assert.Equal(ProductRepository.RemoteMissingStatus, all.Single(x => x.Sku == "GONE").RemoteStatus);
        Assert.Equal(PublishState.Offline, all.Single(x => x.Sku == "GONE").PublishState);
        Assert.Null(all.Single(x => x.Sku == "OTHER-OWNER").RemoteStatus);
        Assert.Equal(PublishState.Pending, all.Single(x => x.Sku == "IN-REVIEW").PublishState);
    }

    [Fact]
    public void PublishedListingWithoutDescription_IsNotBlockedAndKeepsAlibabasDetail()
    {
        var product = Product("A");
        product.RemoteProductId = "1601";
        product.CategoryId = "801299";
        product.Price = 5;
        product.MinimumOrderQuantity = 1;

        var issues = ProductQualityService.Check(product);
        var payload = ListingMapper.BuildUpdatePayload(product).ToJsonString();

        Assert.DoesNotContain(issues, x => x.Field == "description" && x.Severity == QualitySeverity.Blocker);
        Assert.DoesNotContain("\"description\"", payload);
        // A new listing still needs a description.
        product.RemoteProductId = null;
        Assert.Contains(ProductQualityService.Check(product), x => x.Field == "description" && x.Severity == QualitySeverity.Blocker);
    }

    [Fact]
    public void FromRemote_CountsVariantsInsideTradeInfo()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            { "basic_info": { "product_id": 7, "title": "T", "status": "online" },
              "trade_info": { "moq": 1, "sku_info": [ { "sku_id": 1 }, { "sku_id": 2 } ] } }
            """);

        Assert.Equal(2, AlibabaCatalogSync.FromRemote(document.RootElement, DateTimeOffset.UtcNow)!.RemoteSkuCount);
    }

    [Fact]
    public void FromRemote_SplitsKeywordsOnePerLine()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            { "basic_info": { "product_id": 7, "title": "T", "keywords": "non-toxic rubber floor mat\nrubber mat for playground\nrubber floor mat" } }
            """);

        Assert.Equal(["non-toxic rubber floor mat", "rubber mat for playground", "rubber floor mat"], AlibabaCatalogSync.FromRemote(document.RootElement, DateTimeOffset.UtcNow)!.Keywords);
    }
}
