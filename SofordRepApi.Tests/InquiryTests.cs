using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

/// <summary>Same pipeline as <see cref="ErpFactory"/>, with a small inquiry limit so throttling is testable.</summary>
public sealed class InquiryFactory : WebApplicationFactory<Program>
{
    public const string WebsiteOrigin = "https://www.soford.test";
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Soford:DataPath", _dataPath);
        builder.UseSetting("Auth:AdminUsername", "boss");
        builder.UseSetting("Auth:AdminPassword", ErpFactory.AdminPassword);
        builder.UseSetting("Soford:AutoSync:IntervalMinutes", "0");
        builder.UseSetting("Alibaba:GatewayUrl", "http://127.0.0.1:9/rest");
        builder.UseSetting("Website:AllowedOrigins:0", WebsiteOrigin);
        builder.UseSetting("Website:InquiriesPerHourPerIp", "6");
    }
}

public class InquiryTests(InquiryFactory factory) : IClassFixture<InquiryFactory>
{
    private static object Valid(string email = "buyer@example.com") => new
    {
        name = "Jane Buyer",
        email,
        company = "Gym Supply Ltd",
        country = "Australia",
        product = "Rubber Gym Tiles",
        productId = "62400806712",
        quantity = "500 m²",
        message = "Please quote 500 m² of 20 mm tiles to Sydney.",
    };

    private async Task<HttpResponseMessage> SubmitAsync(object body, string? origin = InquiryFactory.WebsiteOrigin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/public/inquiries") { Content = JsonContent.Create(body) };
        if (origin is not null) request.Headers.Add("Origin", origin);
        return await factory.CreateClient().SendAsync(request);
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "boss", password = ErpFactory.AdminPassword, remember = false });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    // One test so the shared per-IP limit is spent in a known order.
    [Fact]
    public async Task WebsiteInquiry_IsStored_Validated_AndHandledInErp()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";

        var ok = await SubmitAsync(Valid(email));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(InquiryFactory.WebsiteOrigin, ok.Headers.GetValues("Access-Control-Allow-Origin").Single());

        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(new { name = "X", email = "not-an-email", message = "Hello there, quote please" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(new { name = "X", email = "x@example.com", message = "hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SubmitAsync(Valid(), origin: "https://evil.example")).StatusCode);

        // The honeypot answers like a success but nothing is stored.
        var bot = await SubmitAsync(new { name = "Bot", email = "bot@example.com", message = "Buy cheap followers now!!", website = "http://spam" });
        Assert.Equal(HttpStatusCode.OK, bot.StatusCode);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/inquiries")).StatusCode);

        var admin = await AdminAsync();
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/inquiries");
        var stored = list.EnumerateArray().Where(x => x.GetProperty("email").GetString() == email).ToList();
        var inquiry = Assert.Single(stored);
        Assert.DoesNotContain(list.EnumerateArray(), x => x.GetProperty("email").GetString() == "bot@example.com");
        Assert.Equal("New", inquiry.GetProperty("status").GetString());
        Assert.Equal("62400806712", inquiry.GetProperty("productId").GetString());

        var summary = await admin.GetFromJsonAsync<JsonElement>("/api/inquiries/summary");
        Assert.True(summary.GetProperty("new").GetInt32() >= 1);

        var id = inquiry.GetProperty("id").GetString();
        var updated = await admin.PutAsJsonAsync($"/api/inquiries/{id}", new { status = "Replied", note = "Sent price list" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Replied", body.GetProperty("status").GetString());
        Assert.Equal("Sent price list", body.GetProperty("note").GetString());
        Assert.Equal("boss", body.GetProperty("handledBy").GetString());

        var search = await admin.GetFromJsonAsync<JsonElement>($"/api/inquiries/search?status=Replied&q={Uri.EscapeDataString(email)}&page=999&pageSize=20");
        Assert.Equal(1, search.GetProperty("total").GetInt32());
        Assert.Equal(1, search.GetProperty("page").GetInt32());
        Assert.Equal(email, Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("email").GetString());
        Assert.True(search.GetProperty("counts").GetProperty("Replied").GetInt32() >= 1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/inquiries/search")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/inquiries/search?status=999")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/inquiries/{id}", new { status = 999 })).StatusCode);
        Assert.Equal("Replied", (await admin.GetFromJsonAsync<JsonElement>($"/api/inquiries/search?q={email}"))
            .GetProperty("items")[0].GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/inquiries/{id}", new { status = "Closed", note = "Stale overwrite" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/inquiries/{id}", new { status = "Closed", note = "Fresh update", expectedUpdatedAt = body.GetProperty("updatedAt").GetString() })).StatusCode);
        Assert.Equal("Fresh update", (await admin.GetFromJsonAsync<JsonElement>($"/api/inquiries/search?q={email}"))
            .GetProperty("items")[0].GetProperty("note").GetString());

        // 6 per hour: five requests above went through the limiter, the sixth is allowed, the seventh is not.
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(Valid())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SubmitAsync(Valid())).StatusCode);
    }
}
