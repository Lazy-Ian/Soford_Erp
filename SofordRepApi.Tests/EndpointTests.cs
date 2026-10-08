using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Runs the real HTTP pipeline (cookies, authorization policies, endpoint filters) against a temporary data folder,
/// in a non-Development environment so production startup checks apply too. Alibaba points at an unroutable address.
/// </summary>
public sealed class ErpFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "integration-test-password";
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Soford:DataPath", _dataPath);
        builder.UseSetting("Auth:AdminUsername", "boss");
        builder.UseSetting("Auth:AdminPassword", AdminPassword);
        builder.UseSetting("Soford:AutoSync:IntervalMinutes", "0");
        builder.UseSetting("Alibaba:GatewayUrl", "http://127.0.0.1:9/rest");
        builder.UseSetting("Alibaba:AppKey", "test-key");
        builder.UseSetting("Alibaba:AppSecret", "test-secret");
    }
}

public class EndpointTests : IClassFixture<ErpFactory>
{
    private readonly ErpFactory _factory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EndpointTests(ErpFactory factory) => _factory = factory;

    private async Task<HttpClient> LoginAsync(string username, string password)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password, remember = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    /// <summary>Two sub-accounts with one listing each, and an operator who is responsible for the first.</summary>
    private async Task<(HttpClient Admin, HttpClient Operator, Guid Mine, Guid Foreign, Guid OtherAccount, string OperatorId)> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var accounts = _factory.Services.GetRequiredService<AlibabaAccountStore>();
        var products = _factory.Services.GetRequiredService<ProductRepository>();
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        var ownerA = "A" + suffix;
        var ownerB = "B" + suffix;
        await accounts.UpdateAsync(list =>
        {
            list.Add(new AlibabaAccount { Id = accountA, Name = "张三的账号", OwnerAliIds = [ownerA] });
            list.Add(new AlibabaAccount { Id = accountB, Name = "李四的账号", OwnerAliIds = [ownerB] });
            return true;
        });
        var mine = await products.CreateAsync(new ProductRecord { Sku = "MINE-" + suffix, Title = "Mine", OwnerAliId = ownerA, RemoteProductId = "1" + suffix.GetHashCode() });
        var foreign = await products.CreateAsync(new ProductRecord { Sku = "FOREIGN-" + suffix, Title = "Foreign", OwnerAliId = ownerB, RemoteProductId = "2" + suffix.GetHashCode() });

        var admin = await LoginAsync("boss", ErpFactory.AdminPassword);
        var created = await admin.PostAsJsonAsync("/api/users", new { username = "zhang" + suffix, displayName = "张三", password = "operator-password", role = "Operator", accountIds = new[] { accountA } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var operatorId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var op = await LoginAsync("zhang" + suffix, "operator-password");
        return (admin, op, mine.Id, foreign.Id, accountB, operatorId);
    }

    [Fact]
    public async Task Anonymous_OnlyReachesHealthAndSession()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/catalog/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/catalog/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/catalog/products/batch-delete", new { productIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = "wrong-password", remember = false });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CrossSiteWritesAreRejectedButConfiguredFrontendIsAllowed()
    {
        var blocked = _factory.CreateClient();
        blocked.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        var denied = await blocked.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = ErpFactory.AdminPassword, remember = false });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var allowed = _factory.CreateClient();
        allowed.DefaultRequestHeaders.Add("Origin", "http://localhost:5173");
        var accepted = await allowed.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = ErpFactory.AdminPassword, remember = false });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task ImageUploadRejectsSpoofedContentTypeBeforeCallingAlibaba()
    {
        var admin = await LoginAsync("boss", ErpFactory.AdminPassword);
        using var form = new MultipartFormDataContent();
        var fake = new ByteArrayContent("this is not a png"u8.ToArray());
        fake.Headers.ContentType = new("image/png");
        form.Add(fake, "file", "fake.png");

        var response = await admin.PostAsync("/api/integrations/alibaba/images/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("图片内容无效", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ImportPreviewWritesNothingUntilConfirmedThenRunsInBackground()
    {
        var admin = await LoginAsync("boss", ErpFactory.AdminPassword);
        var sku = "IMPORT-" + Guid.NewGuid().ToString("N")[..8];
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes($"Sku,Title,Price,Stock,Unknown\n{sku},Imported,12.5,20,x\n")), "file", "products.csv");
        form.Add(new StringContent(ImportModes.Upsert), "mode");

        var previewResponse = await admin.PostAsync("/api/catalog/import/preview", form);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<JsonElement>())!;
        var id = preview.GetProperty("id").GetGuid();
        Assert.Equal("Previewed", preview.GetProperty("status").GetString());
        Assert.Equal(1, preview.GetProperty("created").GetInt32());
        Assert.DoesNotContain(await _factory.Services.GetRequiredService<ProductRepository>().GetAllAsync(), x => x.Sku == sku);

        var commit = await admin.PostAsync($"/api/catalog/import-jobs/{id}/commit", null);
        Assert.Equal(HttpStatusCode.Accepted, commit.StatusCode);

        JsonElement finished = default;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            finished = (await admin.GetFromJsonAsync<JsonElement>($"/api/catalog/import-jobs/{id}"))!;
            if (finished.GetProperty("status").GetString() is "Completed" or "CompletedWithWarnings") break;
            await Task.Delay(25);
        }

        Assert.Contains(finished.GetProperty("status").GetString(), new[] { "Completed", "CompletedWithWarnings" });
        Assert.Contains(await _factory.Services.GetRequiredService<ProductRepository>().GetAllAsync(), x => x.Sku == sku && x.Stock == 20);

        var report = await admin.GetAsync($"/api/catalog/import-jobs/{id}/errors.xlsx");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", report.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Operator_SeesAndChangesOnlyTheirAccountsProducts()
    {
        var (_, op, mine, foreign, otherAccount, _) = await SeedAsync();

        var list = await op.GetFromJsonAsync<JsonElement[]>("/api/catalog/products", Json);
        Assert.Contains(list!, x => x.GetProperty("id").GetGuid() == mine);
        Assert.DoesNotContain(list!, x => x.GetProperty("id").GetGuid() == foreign);

        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync($"/api/catalog/products/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync($"/api/catalog/products/{foreign}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.DeleteAsync($"/api/catalog/products/{foreign}")).StatusCode);

        // A batch that includes someone else's product is refused as a whole.
        var batch = await op.PostAsJsonAsync("/api/catalog/products/batch-delete", new { productIds = new[] { mine, foreign } });
        Assert.Equal(HttpStatusCode.Forbidden, batch.StatusCode);
        var products = _factory.Services.GetRequiredService<ProductRepository>();
        Assert.NotNull(await products.GetAsync(mine));
        Assert.NotNull(await products.GetAsync(foreign));

        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/catalog/jobs/price", new { productIds = new[] { foreign } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/catalog/publish", new { productIds = new[] { foreign } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/catalog/assign-account", new { productIds = new[] { mine }, accountId = otherAccount })).StatusCode);
    }

    [Fact]
    public async Task Operator_CannotUseAdminEndpoints()
    {
        var (_, op, _, _, _, _) = await SeedAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/catalog/pull-from-alibaba", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/integrations/alibaba/call", new { apiKey = "product.search" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/integrations/alibaba/logs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.DeleteAsync("/api/integrations/alibaba/token")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/system/automation/run", null)).StatusCode);
    }

    [Fact]
    public async Task DisablingAUser_EndsTheirSessionImmediately()
    {
        var (admin, op, _, _, _, operatorId) = await SeedAsync();
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/catalog/summary")).StatusCode);

        var disabled = await admin.PutAsJsonAsync($"/api/users/{operatorId}", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await op.GetAsync("/api/catalog/summary")).StatusCode);
    }

    [Fact]
    public async Task Actions_AreRecordedInTheAuditLog()
    {
        var (admin, op, mine, _, _, _) = await SeedAsync();
        await op.PostAsJsonAsync("/api/catalog/products/batch-delete", new { productIds = new[] { mine } });

        var entries = await admin.GetFromJsonAsync<JsonElement[]>("/api/audit?take=50", Json);
        Assert.Contains(entries!, x => x.GetProperty("action").GetString() == "删除商品" && x.GetProperty("userName").GetString() == "张三");
        Assert.Contains(entries!, x => x.GetProperty("action").GetString() == "新增用户");

        // Operators only see their own history.
        var own = await op.GetFromJsonAsync<JsonElement[]>("/api/audit?take=50", Json);
        Assert.All(own!, x => Assert.Equal("张三", x.GetProperty("userName").GetString()));
    }
}
