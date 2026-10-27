using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

public sealed class WebsiteProfileFactory : WebApplicationFactory<Program>
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Soford:DataPath", _dataPath);
        builder.UseSetting("Auth:AdminUsername", "boss");
        builder.UseSetting("Auth:AdminPassword", ErpFactory.AdminPassword);
        builder.UseSetting("Soford:AutoSync:IntervalMinutes", "0");
        builder.UseSetting("Alibaba:GatewayUrl", "http://127.0.0.1:9/rest");
        builder.UseSetting("Website:AllowedOrigins:0", InquiryFactory.WebsiteOrigin);
    }
}

public class WebsiteProfileTests(WebsiteProfileFactory factory) : IClassFixture<WebsiteProfileFactory>
{
    private async Task<HttpClient> AdminAsync()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = ErpFactory.AdminPassword, remember = false });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static object Profile(params object[] contacts) => new
    {
        company = "Qingdao SoFord Import And Export Co., Ltd.",
        address = "No. 18 Harbor Road, Qingdao, Shandong, China",
        alibabaStore = "https://soford.en.alibaba.com",
        terms = new
        {
            payment = "30% T/T deposit.",
            sample = "",
            tradeTerms = "Trade terms are FOB Qingdao.",
            moq = "",
            leadTime = "Production takes 7–20 days after the deposit is received.",
            warranty = "",
        },
        contacts,
    };

    [Fact]
    public async Task PublicSite_UsesSavedContacts_AndRejectsBadInput()
    {
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/website/profile")).StatusCode);

        var before = new HttpRequestMessage(HttpMethod.Get, "/public/site");
        before.Headers.Add("Origin", InquiryFactory.WebsiteOrigin);
        var open = await anonymous.SendAsync(before);
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Equal(InquiryFactory.WebsiteOrigin, open.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var seeded = await open.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Wendy Wang", seeded.GetProperty("contacts")[0].GetProperty("name").GetString());
        Assert.Equal("", seeded.GetProperty("terms").GetProperty("payment").GetString());
        Assert.Equal("", seeded.GetProperty("alibabaStore").GetString());

        var admin = await AdminAsync();
        var missingName = Profile(new { name = "", email = "sales@soford.cn" });
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/website/profile", missingName)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/website/profile", new { company = "Soford", alibabaStore = "javascript:alert(1)", contacts = Array.Empty<object>() })).StatusCode);

        var savedResponse = await admin.PutAsJsonAsync("/api/website/profile", Profile(
            new { id = "b6e1c2a0-7f44-4c3a-9a1e-5d8e0a1c2b30", name = "Wendy Wang", title = "Trade Manager", email = "Wendy91715@foxmail.com", phone = "+86 185 6280 9257", whatsapp = "8618562809257", wechat = "+86 185 6280 9257", qq = "2929004105" },
            new { name = "Li Lei", title = "Sales", email = "lei@soford.cn", phone = "+86 138 0013 8000", whatsapp = "", wechat = "lilei-soford", qq = "" }));
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = await savedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, saved.GetProperty("contacts").GetArrayLength());
        Assert.Equal("Li Lei", saved.GetProperty("contacts")[1].GetProperty("name").GetString());
        Assert.NotEqual(Guid.Empty.ToString(), saved.GetProperty("contacts")[1].GetProperty("id").GetString());

        var after = await anonymous.GetFromJsonAsync<JsonElement>("/public/site");
        Assert.Equal("No. 18 Harbor Road, Qingdao, Shandong, China", after.GetProperty("address").GetString());
        Assert.Equal("https://soford.en.alibaba.com", after.GetProperty("alibabaStore").GetString());
        Assert.Equal("30% T/T deposit.", after.GetProperty("terms").GetProperty("payment").GetString());
        Assert.Equal("Li Lei", after.GetProperty("contacts")[1].GetProperty("name").GetString());

        var again = await admin.GetFromJsonAsync<JsonElement>("/api/website/profile");
        Assert.Equal("Li Lei", again.GetProperty("contacts")[1].GetProperty("name").GetString());
        Assert.Equal(1, again.GetProperty("revision").GetInt64());

        // Old editors (including a missing revision after the first save) cannot overwrite a newer profile.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/website/profile", seeded)).StatusCode);
        var current = await admin.GetFromJsonAsync<WebsiteProfile>("/api/website/profile");
        current!.Address = "Updated address";
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/website/profile", current)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/website/profile", current)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/users", new
        {
            username = "website-operator", displayName = "Operator", password = "operator-test-password", role = "Operator", accountIds = Array.Empty<Guid>(),
        })).StatusCode);
        var op = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/auth/login", new { username = "website-operator", password = "operator-test-password", remember = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/website/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync("/api/website/profile", current)).StatusCode);
        Assert.Equal("Updated address", (await anonymous.GetFromJsonAsync<WebsiteProfile>("/public/site"))!.Address);
    }
}
