public sealed class OptimizationTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
    public void Dispose() => Directory.Delete(_paths.DataPath, recursive: true);

    [Fact]
    public async Task InquiryStore_ReturnsIndependentCopies_AndPersistsUpdates()
    {
        var store = new InquiryStore(_paths);
        var input = new Inquiry { Id = Guid.NewGuid(), Name = "Buyer" };
        await store.AddAsync(input);
        input.Name = "Changed input";
        var read = Assert.Single(await store.LatestAsync(10));
        read.Name = "Changed read";
        var updated = await store.UpdateAsync(input.Id, x => x.Note = "Persisted");
        updated!.Name = "Changed result";
        var search = await store.SearchAsync(1, 20, null, null);
        search.Items[0].Name = "Changed page";

        Assert.Equal("Buyer", Assert.Single(await store.LatestAsync(10)).Name);
        var disk = Assert.Single(await new InquiryStore(_paths).LatestAsync(10));
        Assert.Equal("Buyer", disk.Name);
        Assert.Equal("Persisted", disk.Note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InquiryStore_FailedWritesDiscardTheUnpersistedCache(bool update)
    {
        var store = new InquiryStore(_paths);
        var inquiry = new Inquiry { Id = Guid.NewGuid(), Name = "Original" };
        await store.AddAsync(inquiry);
        var file = _paths.File("inquiries.json");
        // A directory in place of the destination makes atomic replacement fail on Windows and Linux.
        File.Move(file, file + ".saved");
        Directory.CreateDirectory(file);
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                if (update) await store.UpdateAsync(inquiry.Id, x => x.Status = InquiryStatus.Closed);
                else await store.AddAsync(new Inquiry { Id = Guid.NewGuid(), Name = "Not persisted" });
            });
            Assert.NotNull(error);
        }
        finally
        {
            Directory.Delete(file);
            File.Move(file + ".saved", file);
        }
        var remaining = Assert.Single(await store.LatestAsync(10));
        Assert.Equal("Original", remaining.Name);
        Assert.Equal(InquiryStatus.New, remaining.Status);
        Assert.Equal(1, await store.CountNewAsync());
    }

    [Fact]
    public async Task InquiryStore_FailedMutationDoesNotChangeStoredData()
    {
        var store = new InquiryStore(_paths);
        var inquiry = new Inquiry { Id = Guid.NewGuid(), Note = "Original" };
        await store.AddAsync(inquiry);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(inquiry.Id, x =>
        {
            x.Note = "Not saved";
            throw new InvalidOperationException("Failure during mutation");
        }));
        Assert.Equal("Original", Assert.Single(await store.LatestAsync(10)).Note);
    }

    [Fact]
    public async Task InquirySearch_FindsOlderRecords_Paginates_AndCountsAllStatuses()
    {
        var records = Enumerable.Range(0, 2105).Select(i => new Inquiry
        {
            Id = Guid.NewGuid(), Name = $"Buyer {i}", Note = i == 0 ? "Old Sydney inquiry" : "",
            Status = i % 2 == 0 ? InquiryStatus.New : InquiryStatus.Closed
        }).ToList();
        await JsonFile.WriteAtomicAsync(_paths.File("inquiries.json"), records);
        var store = new InquiryStore(_paths);
        var old = await store.SearchAsync(1, 20, InquiryStatus.New, "sYDney");
        Assert.Equal("Buyer 0", Assert.Single(old.Items).Name);
        Assert.Equal(1, old.Total);
        Assert.Equal(1053, old.Counts[InquiryStatus.New]);
        Assert.Equal(1052, old.Counts[InquiryStatus.Closed]);
        var second = await store.SearchAsync(2, 20, null, null);
        Assert.Equal("Buyer 2084", second.Items[0].Name);
        Assert.Equal(20, second.Items.Count);
        Assert.Equal(2105, second.Total);
        var clamped = await store.SearchAsync(int.MaxValue, int.MaxValue, null, null);
        Assert.Equal(22, clamped.Page);
        Assert.Equal(100, clamped.PageSize);
        Assert.Equal(5, clamped.Items.Count);
        var empty = await store.SearchAsync(-1, -1, InquiryStatus.Spam, null);
        Assert.Equal(1, empty.Page);
        Assert.Empty(empty.Items);
    }

    [Fact]
    public async Task ProductLookup_PreservesOrder_Deduplicates_AndIsolatesNestedFields()
    {
        var repo = new ProductRepository(_paths, TimeProvider.System);
        var first = await repo.CreateAsync(new ProductRecord { Sku = "A", Keywords = ["original"], Attributes = new() { ["Color"] = "Black" } });
        var second = await repo.CreateAsync(new ProductRecord { Sku = "B" });
        var items = await repo.GetManyAsync([second.Id, first.Id, second.Id, Guid.NewGuid()]);
        Assert.Equal(new[] { second.Id, first.Id }, items.Select(x => x.Id));
        items[1].Keywords[0] = "Changed batch";
        var single = (await repo.GetAsync(first.Id))!;
        Assert.Equal("original", single.Keywords[0]);
        single.Attributes["Color"] = "Changed single";
        Assert.Equal("Black", (await repo.GetAsync(first.Id))!.Attributes["Color"]);
        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
        Assert.Empty(await repo.GetManyAsync([]));
    }
}
