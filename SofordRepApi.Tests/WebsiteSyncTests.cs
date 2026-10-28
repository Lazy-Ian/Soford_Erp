public class WebsiteSyncTests
{
    private static AppPaths Paths() => new(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
    private static ProductRecord Valid(string sku) => new()
    {
        Id = Guid.NewGuid(), Sku = sku, Title = "Rubber gym tile", Description = "<p>Technical details</p>",
        Images = ["https://cdn.example/image.jpg"], Price = 12m, MinimumOrderQuantity = 10,
    };

    [Fact]
    public async Task LegacyMigration_PreservesOldCatalog_AndPersistsSnapshotAcrossRestart()
    {
        var paths = Paths();
        var listed = Valid("LEGACY"); listed.RemoteStatus = "online"; listed.RemoteProductId = "123";
        var duplicate = Valid("DUPLICATE"); duplicate.RemoteStatus = "online"; duplicate.RemoteProductId = "456";
        var offline = Valid("OFFLINE"); offline.RemoteStatus = "offline";
        await JsonFile.WriteAtomicAsync(paths.File("products.json"), new[] { listed, duplicate, offline });
        var repo = new ProductRepository(paths, TimeProvider.System);
        var first = await repo.GetAllAsync();
        Assert.Single(WebsiteCatalogProjection.Project(first));
        Assert.Equal("系统迁移", first.Single(x => x.Id == listed.Id).WebsiteSyncedBy);
        Assert.Null(first.Single(x => x.Id == duplicate.Id).WebsitePublication);
        await repo.UpdateAsync(listed.Id, p => { p.Title = "Changed ERP title"; p.Description = "Changed draft"; });
        var restarted = new ProductRepository(paths, TimeProvider.System);
        var after = await restarted.GetAllAsync();
        Assert.Equal("Rubber Gym Tile", Assert.Single(WebsiteCatalogProjection.Project(after)).Title);
        var record = after.Single(x => x.Id == listed.Id);
        Assert.True(record.HasWebsiteChanges);
        record.WebsitePublication!.Product.Images[0] = "mutated";
        Assert.Equal("https://cdn.example/image.jpg", (await restarted.GetAsync(listed.Id))!.WebsitePublication!.Product.Images[0]);
    }

    [Fact]
    public async Task Sync_IsIndependentOfAlibaba_ReportsFailures_AndRetainsStableLinks()
    {
        var paths = Paths();
        var repo = new ProductRepository(paths, TimeProvider.System);
        var draft = await repo.CreateAsync(Valid("DRAFT"));
        var incomplete = Valid("INCOMPLETE"); incomplete.Description = "";
        var bad = await repo.CreateAsync(incomplete);
        var result = await repo.SyncWebsiteAsync([draft.Id, bad.Id, Guid.NewGuid()], false, "Admin");
        Assert.Equal(1, result.Succeeded); Assert.Equal(2, result.Failed);
        var published = (await repo.GetAsync(draft.Id))!;
        Assert.Equal(PublishState.NotPublished, published.PublishState);
        Assert.False(published.HasUnpublishedChanges);
        Assert.False(published.HasWebsiteChanges);
        var publicId = published.WebsitePublicId;
        Assert.Equal(draft.Id.ToString("D"), publicId);
        await repo.UpdateAsync(draft.Id, p => p.RemoteProductId = "999");
        await repo.SyncWebsiteAsync([draft.Id], true, "Admin");
        await repo.SyncWebsiteAsync([draft.Id], false, "Admin");
        Assert.Equal(publicId, (await repo.GetAsync(draft.Id))!.WebsitePublicId);
        var sameTitle = await repo.CreateAsync(Valid("ANOTHER-SPEC"));
        Assert.Equal(1, (await repo.SyncWebsiteAsync([sameTitle.Id], false, "Admin")).Succeeded);
        Assert.Equal(2, WebsiteCatalogProjection.Project(await repo.GetAllAsync()).Count);
    }

    [Fact]
    public async Task FailedWrite_DoesNotExposeUnpersistedWebsiteChanges()
    {
        var paths = Paths();
        var repo = new ProductRepository(paths, TimeProvider.System);
        var product = await repo.CreateAsync(Valid("DISK"));
        var file = paths.File("products.json");
        File.Move(file, file + ".saved");
        Directory.CreateDirectory(file);
        try
        {
            var error = await Record.ExceptionAsync(() => repo.SyncWebsiteAsync([product.Id], false, "Admin"));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a filesystem failure, got {error?.GetType().Name ?? "no error"}");
        }
        finally { Directory.Delete(file); File.Move(file + ".saved", file); }
        Assert.Null((await repo.GetAsync(product.Id))!.WebsitePublication);
    }
}
