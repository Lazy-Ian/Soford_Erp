using System.Text;
using System.Text.Json.Nodes;
using ClosedXML.Excel;

public class CatalogTests
{
    private static ProductRecord ValidProduct() => new()
    {
        Id = Guid.NewGuid(),
        Sku = "SF-001",
        Title = "Portable Wireless Bluetooth Speaker Waterproof IPX7",
        Description = "20W stereo sound, 12 hour battery, IPX7 waterproof, for outdoor use.",
        CategoryId = "201896803",
        Price = 12.5m,
        MinimumOrderQuantity = 100,
        Stock = 500,
        LeadTimeDays = 15,
        Images = ["https://example.com/a.jpg", "https://example.com/b.jpg"],
        Keywords = ["bluetooth speaker", "portable speaker", "waterproof speaker"],
        Attributes = new(StringComparer.OrdinalIgnoreCase) { ["Material"] = "ABS", ["Color"] = "Black", ["Place of Origin"] = "China" }
    };

    [Fact]
    public void Quality_ValidProductHasNoBlockers()
    {
        var product = ValidProduct();
        ProductQualityService.Apply(product, null);

        Assert.DoesNotContain(product.QualityIssues, x => x.Severity == QualitySeverity.Blocker);
        Assert.Equal(LocalState.Ready, product.LocalState);
    }

    [Fact]
    public void Quality_DoesNotTouchPublishState()
    {
        var product = ValidProduct();
        product.PublishState = PublishState.Online;
        product.Images = [];

        ProductQualityService.Apply(product, null);

        Assert.Equal(LocalState.Incomplete, product.LocalState);
        Assert.Equal(PublishState.Online, product.PublishState);
    }

    [Fact]
    public void Quality_EnforcesRequiredCategoryAttributesAndTierOrder()
    {
        var product = ValidProduct();
        product.TieredPrices = [new PriceTier(100, 10m), new PriceTier(500, 11m)];
        var attributes = new CategoryAttributeSet("201896803", DateTimeOffset.UtcNow,
            [new CategoryAttribute("1", "Brand Name", true, true, false, [])], []);

        var issues = ProductQualityService.Check(product, attributes);

        Assert.Contains(issues, x => x.Field == "attributes" && x.Message.Contains("Brand Name"));
        Assert.Contains(issues, x => x.Field == "tieredPrices");
    }

    [Fact]
    public void ListingMapper_BuildsProductInfoSchema()
    {
        var product = ValidProduct();
        product.WeightKg = 0.6m;
        product.LengthCm = 20;
        product.WidthCm = 10;
        product.HeightCm = 10;

        var payload = ListingMapper.BuildCreatePayload(product);
        var info = payload["product_info"]!.AsObject();

        Assert.Equal(product.Title, (string?)info["basic_info"]!["title"]);
        Assert.Equal("bluetooth speaker portable speaker waterproof speaker", (string?)info["basic_info"]!["keywords"]);
        Assert.Equal(2, info["basic_info"]!["product_image"]!.AsArray().Count);
        Assert.Equal(201896803L, (long)info["category_info"]!["category_id"]!);
        Assert.Equal("TIERED", (string?)info["trade_info"]!["price"]!["price_type"]);
        Assert.Equal("12.5", (string?)info["trade_info"]!["price"]!["tiered_price"]![0]!["price"]);
        Assert.Equal(100, (int)info["trade_info"]!["price"]!["tiered_price"]![0]!["quantity"]!);
        Assert.Equal("0.6", (string?)info["logistics_info"]!["weight"]);
        Assert.Null(payload["ai_optimization_config"]);
    }

    [Fact]
    public void ListingMapper_EnablesKeywordOptimizationWithoutKeywords()
    {
        var product = ValidProduct();
        product.Keywords = [];

        var payload = ListingMapper.BuildCreatePayload(product);

        Assert.True((bool)payload["ai_optimization_config"]!["keyword_optimization_enabled"]!);
        Assert.False((bool)payload["ai_optimization_config"]!["title_optimization_enabled"]!);
    }

    [Fact]
    public void ListingMapper_UpdateCarriesRemoteId()
    {
        var product = ValidProduct();
        product.RemoteProductId = "1601454338774";

        var payload = ListingMapper.BuildUpdatePayload(product);

        Assert.Equal(1601454338774L, (long)payload["product_info"]!["basic_info"]!["product_id"]!);
    }

    [Fact]
    public void Import_CsvWithTemplateHeadersQuotedNewlinesAndBadNumbers()
    {
        var csv = "Sku,Title,Description,Price,MOQ,LeadTimeDays,Images,Attributes,TieredPrices\n" +
                  "A1,Speaker,\"line one\nline two, with comma\",12.5,100,15,https://x/a.jpg;https://x/b.jpg,Material:ABS;Color:Black,100:12.5;500:11\n" +
                  ",No sku,,1,1,1,,,\n" +
                  "A2,Lamp,desc,abc,10,7,,,\n";

        var parsed = new ProductImportService().Parse(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "p.csv");

        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(1, parsed.Skipped);
        var first = parsed.Rows[0].Draft;
        Assert.Equal("line one\nline two, with comma", first.Description);
        Assert.Equal(15, first.LeadTimeDays);
        Assert.Equal(2, first.Images!.Length);
        Assert.Equal("ABS", first.Attributes!["Material"]);
        Assert.Equal(2, first.TieredPrices!.Count);
        // A2 sits on physical line 5 because A1's quoted description spans two lines.
        Assert.Contains(parsed.Warnings, x => x.Contains("第 5 行") && x.Contains("abc"));
    }

    [Fact]
    public void Import_ChineseHeadersAndGbkEncoding()
    {
        var csv = "商品编码,标题,价格,起订量,交期(天),主图,详情图\nC1,音箱,9.9,50,10,https://x/m.jpg,https://x/d.jpg\n";
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding("GB18030").GetBytes(csv);

        var parsed = new ProductImportService().Parse(new MemoryStream(bytes), "p.csv");

        var draft = Assert.Single(parsed.Rows).Draft;
        Assert.Equal("C1", draft.Sku);
        Assert.Equal("音箱", draft.Title);
        Assert.Equal(50, draft.MinimumOrderQuantity);
        Assert.Equal(10, draft.LeadTimeDays);
        Assert.Equal(["https://x/m.jpg", "https://x/d.jpg"], draft.Images);
    }

    [Fact]
    public void Import_XlsxTemplateRoundTrip()
    {
        var template = ProductImportService.BuildTemplate();
        using var workbook = new XLWorkbook(new MemoryStream(template));
        var sheet = workbook.Worksheet("Products");
        var columns = ProductImportService.TemplateColumns;
        for (var i = 0; i < columns.Length; i++)
        {
            sheet.Cell(2, i + 1).Value = columns[i].Example;
        }

        sheet.Cell(2, Array.FindIndex(columns, x => x.Header == "Price") + 1).Value = 12.5;
        using var output = new MemoryStream();
        workbook.SaveAs(output);

        var parsed = new ProductImportService().Parse(new MemoryStream(output.ToArray()), "t.xlsx");

        var draft = Assert.Single(parsed.Rows).Draft;
        Assert.Equal("SF-SPK-001", draft.Sku);
        Assert.Equal(12.5m, draft.Price);
        Assert.Equal(15, draft.LeadTimeDays);
        Assert.Equal(0.6m, draft.WeightKg);
        Assert.Empty(parsed.UnknownHeaders);
    }

    [Fact]
    public async Task Repository_RejectsDuplicateSkuAndImportKeepsRemoteState()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        var repo = new ProductRepository(paths, TimeProvider.System);
        var created = await repo.CreateAsync(ValidProduct());
        await repo.UpdateAsync(created.Id, p =>
        {
            p.RemoteProductId = "123";
            p.PublishState = PublishState.Online;
        });

        await Assert.ThrowsAsync<SkuConflictException>(() => repo.CreateAsync(new ProductRecord { Sku = "sf-001" }));

        var other = await repo.CreateAsync(new ProductRecord { Sku = "SF-002" });
        await Assert.ThrowsAsync<SkuConflictException>(() => repo.UpdateAsync(other.Id, p => p.Sku = "SF-001"));

        var draft = new ProductDraft("SF-001", "New title", "", [], "", "", "", "", "", "", new(), "USD", 20m, [], 10, "", 1, 1, "", null, null, null, null, [], false);
        var (createdCount, updatedCount) = await repo.ImportAsync([(draft, _ => { })]);

        Assert.Equal((0, 1), (createdCount, updatedCount));
        var reloaded = (await repo.GetAsync(created.Id))!;
        Assert.Equal("New title", reloaded.Title);
        Assert.Equal("123", reloaded.RemoteProductId);
        Assert.Equal(PublishState.Online, reloaded.PublishState);
        Assert.Equal(2, (await repo.GetAllAsync()).Count);
    }

    [Fact]
    public async Task Repository_MigratesPreviousReleaseData()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        await File.WriteAllTextAsync(paths.File("products.json"),
            "[{\"id\":\"" + Guid.NewGuid() + "\",\"sku\":\"OLD\",\"publishState\":\"Published\",\"mainImageUrl\":\"https://x/m.jpg\",\"detailImageUrls\":[\"https://x/d.jpg\"],\"attributes\":{}}," +
            "{\"id\":\"" + Guid.NewGuid() + "\",\"sku\":\"OLD2\",\"publishState\":\"Ready\",\"attributes\":{}}]");

        var all = await new ProductRepository(paths, TimeProvider.System).GetAllAsync();

        Assert.Equal(PublishState.Online, all[0].PublishState);
        Assert.Equal(["https://x/m.jpg", "https://x/d.jpg"], all[0].Images);
        Assert.Equal(PublishState.NotPublished, all[1].PublishState);
    }

    [Fact]
    public void StatusMapping_CoversAlibabaStatuses()
    {
        Assert.Equal(PublishState.Online, ProductOperations.MapStatus("online", PublishState.Pending).State);
        Assert.Equal(PublishState.Pending, ProductOperations.MapStatus("pending", PublishState.Online).State);
        Assert.Equal(PublishState.Failed, ProductOperations.MapStatus("failed", PublishState.Pending).State);
        Assert.Equal(PublishState.Online, ProductOperations.MapStatus("", PublishState.Online).State);
    }
}
