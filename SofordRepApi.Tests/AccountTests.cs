using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

public class AccountTests
{
    private static (AlibabaAccountStore Store, AppPaths Paths, IDataProtectionProvider Protection) NewStore()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));
        var protection = new EphemeralDataProtectionProvider();
        return (new AlibabaAccountStore(paths, protection, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AlibabaAccountStore>.Instance), paths, protection);
    }

    private static AlibabaTokenRecord Token(string login) => new()
    {
        AccessToken = "at-" + login,
        RefreshToken = "rt",
        Account = login,
        AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
    };

    [Fact]
    public async Task LegacySingleTokenBecomesTheDefaultAccount()
    {
        var (store, paths, protection) = NewStore();
        var legacy = protection.CreateProtector("SofordErp.AlibabaToken.v1").Protect(JsonSerializer.Serialize(Token("boss@x.com"), JsonFile.Options));
        await File.WriteAllTextAsync(paths.File("alibaba-token.json"), JsonSerializer.Serialize(new { @protected = legacy }));

        var account = Assert.Single(await store.GetAllAsync());

        Assert.True(account.IsDefault);
        Assert.Equal("boss@x.com", account.Name);
        Assert.Equal("at-boss@x.com", account.Token!.AccessToken);
        Assert.False(File.Exists(paths.File("alibaba-token.json")));
        Assert.Single(await store.GetAllAsync());
    }

    [Fact]
    public void ResolveForProduct_AssignedThenOwnerThenDefault()
    {
        var main = new AlibabaAccount { Id = Guid.NewGuid(), Name = "main", IsDefault = true, Token = Token("main"), OwnerAliIds = ["1"] };
        var sub = new AlibabaAccount { Id = Guid.NewGuid(), Name = "sub", Token = Token("sub"), OwnerAliIds = ["2"] };
        var offline = new AlibabaAccount { Id = Guid.NewGuid(), Name = "not authorized", OwnerAliIds = ["3"] };
        var accounts = new[] { main, sub, offline };

        Assert.Equal(sub, AlibabaTokenService.ResolveForProduct(accounts, new ProductRecord { OwnerAliId = "2" }));
        Assert.Equal(main, AlibabaTokenService.ResolveForProduct(accounts, new ProductRecord { OwnerAliId = "3" }));
        Assert.Equal(sub, AlibabaTokenService.ResolveForProduct(accounts, new ProductRecord { OwnerAliId = "1", AccountId = sub.Id }));
        Assert.Equal(main, AlibabaTokenService.ResolveForProduct(accounts, new ProductRecord { AccountId = offline.Id }));
    }

    [Fact]
    public async Task OwnerPlaceholdersCanBeMergedIntoAnAuthorizedAccount()
    {
        var (store, _, _) = NewStore();
        var tokens = new AlibabaTokenService(null!, store, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
        var mainId = await store.UpdateAsync(accounts =>
        {
            var main = new AlibabaAccount { Id = Guid.NewGuid(), Name = "main", IsDefault = true, Token = Token("main") };
            accounts.Add(main);
            return main.Id;
        });

        await tokens.EnsureOwnerAccountsAsync(["100", "200", "200", null]);
        Assert.Equal(3, (await store.GetAllAsync()).Count);

        await tokens.UpdateAccountAsync(mainId, "老板", true, ["100"]);

        var accounts = await store.GetAllAsync();
        Assert.Equal(2, accounts.Count);
        var main = accounts.Single(x => x.Id == mainId);
        Assert.Equal("老板", main.Name);
        Assert.True(main.Owns("100"));
        Assert.Contains(accounts, x => x.Owns("200") && x.Token is null);
    }

    private sealed class CannedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private sealed class CannedFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new CannedHandler(body));
    }

    [Fact]
    public async Task ReauthorizingMainAccountMergesItsOwnerPlaceholder()
    {
        var (store, paths, _) = NewStore();
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Alibaba:AppKey"] = "k",
            ["Alibaba:AppSecret"] = "s"
        }).Build();
        var body = """{"code":"0","access_token":"new","refresh_token":"r","expires_in":3600,"refresh_expires_in":3600,"account":"boss@x.com","user_info":{"user_id":"777"}}""";
        var transport = new AlibabaTransport(new CannedFactory(body), new AlibabaApiLogStore(paths), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AlibabaTransport>.Instance);
        var tokens = new AlibabaTokenService(transport, store, config, TimeProvider.System);
        var mainId = await store.UpdateAsync(accounts =>
        {
            var main = new AlibabaAccount { Id = Guid.NewGuid(), Name = "老板", IsDefault = true, Token = Token("boss@x.com") };
            accounts.Add(main);
            return main.Id;
        });
        await tokens.EnsureOwnerAccountsAsync(["777", "888"]);

        var (account, _) = await tokens.CreateFromCodeAsync("code");

        Assert.Equal(mainId, account.Id);
        var accounts = await store.GetAllAsync();
        Assert.Equal(2, accounts.Count);
        var main = accounts.Single(x => x.Id == mainId);
        Assert.Equal("老板", main.Name);
        Assert.True(main.Owns("777"));
        Assert.Equal("new", main.Token!.AccessToken);
    }

    [Fact]
    public void TokenResponseCapturesUserIdentifiers()
    {
        using var document = JsonDocument.Parse("""
            {"access_token":"a","refresh_token":"r","expires_in":3600,"refresh_expires_in":3600,"account":"sub@x.com",
             "user_info":{"user_id":"2206887380455","seller_id":"998","loginId":"sub@x.com"}}
            """);

        var token = AlibabaTokenRecord.FromResponse(document.RootElement, DateTimeOffset.UtcNow);

        Assert.Equal(["2206887380455", "998"], token.Identifiers());
    }
}
