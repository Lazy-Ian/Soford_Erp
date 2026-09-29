using System.Text;

/// <summary>Regression tests for defects found in code review.</summary>
public class ReviewFixTests
{
    private static ProductRepository NewRepository(out AppPaths paths)
    {
        paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        return new ProductRepository(paths, TimeProvider.System);
    }

    [Fact]
    public async Task PartialImport_OnlyUpdatesColumnsPresentInTheSheet()
    {
        var repo = NewRepository(out _);
        var existing = await repo.CreateAsync(new ProductRecord
        {
            Sku = "A1", Title = "Keep me", Description = "Keep desc", Price = 12.5m, CategoryName = "Speakers",
            Images = ["https://x/a.jpg"], AiOptimize = true, Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Color"] = "Black" }
        });

        var parsed = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes("Sku,Stock\nA1,777\n")), "stock.csv");
        await repo.ImportAsync(parsed.Rows, _ => { });

        var after = (await repo.GetAsync(existing.Id))!;
        Assert.Equal(777, after.Stock);
        Assert.Equal("Keep me", after.Title);
        Assert.Equal("Keep desc", after.Description);
        Assert.Equal(12.5m, after.Price);
        Assert.Equal("Speakers", after.CategoryName);
        Assert.Equal(["https://x/a.jpg"], after.Images);
        Assert.True(after.AiOptimize);
        Assert.Equal("Black", after.Attributes["Color"]);
    }

    [Fact]
    public async Task NoOpImportDoesNotFlagPublishedProductAsChanged()
    {
        var repo = NewRepository(out _);
        var created = await repo.CreateAsync(new ProductRecord { Sku = "A1", Stock = 5, RemoteProductId = "1" });
        await repo.UpdateAsync(created.Id, p => p.PublishedContentAt = p.ContentUpdatedAt);

        var same = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes("Sku,Stock\nA1,5\n")), "p.csv");
        await repo.ImportAsync(same.Rows, _ => { });
        Assert.False((await repo.GetAsync(created.Id))!.HasUnpublishedChanges);

        var changed = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes("Sku,Stock\nA1,6\n")), "p.csv");
        await repo.ImportAsync(changed.Rows, _ => { });
        Assert.True((await repo.GetAsync(created.Id))!.HasUnpublishedChanges);
    }

    [Fact]
    public void PartialImportKeepsRelatedFieldsConsistent()
    {
        var product = new ProductRecord
        {
            Sku = "A1", CategoryId = "1", CategoryName = "Old", CategoryPath = "Old > Path", Price = 10,
            TieredPrices = [new PriceTier(10, 10), new PriceTier(100, 9)]
        };
        var parsed = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes("Sku,CategoryId,Price\nA1,2,8\n")), "p.csv");

        product.ApplyImportedFields(parsed.Rows[0].Draft, parsed.Rows[0].Present);

        Assert.Equal("2", product.CategoryId);
        Assert.Equal("", product.CategoryName);
        Assert.Equal("", product.CategoryPath);
        Assert.Equal(8, product.Price);
        Assert.Empty(product.TieredPrices);
    }

    [Fact]
    public async Task PullDoesNotLinkWhenSeveralLocalProductsShareTheModelNumber()
    {
        var repo = NewRepository(out _);
        await repo.CreateAsync(new ProductRecord { Sku = "A-RED", ModelNumber = "A" });
        await repo.CreateAsync(new ProductRecord { Sku = "A-BLU", ModelNumber = "A" });

        var (created, linked, _) = await repo.MergeRemoteAsync(
            [new ProductRecord { Sku = "A", ModelNumber = "A", RemoteProductId = "9", PublishState = PublishState.Online }], _ => { }, DateTimeOffset.UtcNow);

        Assert.Equal((1, 0), (created, linked));
        Assert.All((await repo.GetAllAsync()).Where(x => x.Sku.StartsWith("A-")), x => Assert.Null(x.RemoteProductId));
    }

    [Fact]
    public void Import_HugeNumbersBecomeWarningsNotCrashes()
    {
        var parsed = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes("Sku,Stock,MOQ\nA1,30000000000,5\n")), "p.csv");

        var row = Assert.Single(parsed.Rows);
        Assert.Equal(0, row.Draft.Stock);
        Assert.Contains(parsed.Warnings, x => x.Contains("超出范围"));
    }

    [Fact]
    public void DuplicateAttributeKeysCollapseInsteadOfThrowing()
    {
        var product = new ProductRecord();
        var draft = new ProductDraft("S", "T", "", [], "", "", "", "", "", "",
            new Dictionary<string, string> { ["Color"] = "Black", ["color "] = "Red" }, "USD", 1, [], 1, "", 0, 0, "", null, null, null, null, [], false);

        product.ApplyDraft(draft);

        Assert.Equal("Red", Assert.Single(product.Attributes).Value);
    }

    [Theory]
    [InlineData(0, PublishState.NotPublished)]
    [InlineData(1, PublishState.NotPublished)]
    [InlineData(2, PublishState.Online)]
    [InlineData(3, PublishState.Failed)]
    public async Task LegacyNumericPublishStatesMapToTheOldMeaning(int legacy, PublishState expected)
    {
        var repo = NewRepository(out var paths);
        await File.WriteAllTextAsync(paths.File("products.json"), $"[{{\"id\":\"{Guid.NewGuid()}\",\"sku\":\"OLD\",\"publishState\":{legacy},\"attributes\":{{}}}}]");

        Assert.Equal(expected, Assert.Single(await repo.GetAllAsync()).PublishState);
    }

    [Fact]
    public async Task StatusWritesSucceedEvenWithLegacyDuplicateSkus()
    {
        var repo = NewRepository(out var paths);
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(paths.File("products.json"),
            $"[{{\"id\":\"{id}\",\"sku\":\"DUP\",\"attributes\":{{}}}},{{\"id\":\"{Guid.NewGuid()}\",\"sku\":\"dup\",\"attributes\":{{}}}}]");

        var updated = await repo.UpdateAsync(id, p => p.PublishState = PublishState.Pending);

        Assert.Equal(PublishState.Pending, updated!.PublishState);
    }

    [Fact]
    public void UnpublishedChangesFlagTracksContentVersusPublishedVersion()
    {
        var now = DateTimeOffset.UtcNow;
        var product = new ProductRecord { RemoteProductId = "1", ContentUpdatedAt = now, PublishedContentAt = now };
        Assert.False(product.HasUnpublishedChanges);

        product.ContentUpdatedAt = now.AddMinutes(1);
        Assert.True(product.HasUnpublishedChanges);
    }

    [Fact]
    public void ListingSendsSkuAsModelNumberWhenNoneIsSet()
    {
        var product = new ProductRecord { Sku = "SKU-9", Title = "t", Images = ["https://x/a.jpg"] };

        Assert.Equal("SKU-9", (string?)ListingMapper.BuildCreatePayload(product)["product_info"]!["basic_info"]!["model_number"]);

        product.ModelNumber = "M-1";
        Assert.Equal("M-1", (string?)ListingMapper.BuildCreatePayload(product)["product_info"]!["basic_info"]!["model_number"]);
    }

    [Fact]
    public void InvalidRemoteIdIsAClearErrorNotAnOverflow()
    {
        var product = new ProductRecord { Sku = "S", RemoteProductId = "99999999999999999999999" };

        var error = Assert.Throws<InvalidOperationException>(() => ListingMapper.BuildUpdatePayload(product));
        Assert.Contains("无效", error.Message);
    }

    [Fact]
    public void IdsRequestToleratesMissingIds()
    {
        Assert.Empty(new IdsRequest(null).Ids);
        var id = Guid.NewGuid();
        Assert.Equal([id], new IdsRequest([id, id]).Ids);
    }

    [Fact]
    public async Task ProductLocksSerializeOverlappingOperations()
    {
        var locks = new ProductLocks();
        var id = Guid.NewGuid();
        var first = await locks.AcquireAsync(id);
        var second = locks.AcquireAsync([id, Guid.NewGuid()]);

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second).Dispose();
    }
}
