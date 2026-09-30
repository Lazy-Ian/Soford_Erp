using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

public class UserTests
{
    private static AppPaths NewPaths() => new(Path.Combine(Path.GetTempPath(), "soford-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void PasswordHash_VerifiesOnlyTheRightPassword()
    {
        var hash = PasswordHasher.Hash("correct horse battery");

        Assert.True(PasswordHasher.Verify("correct horse battery", hash));
        Assert.False(PasswordHasher.Verify("correct horse batterY", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("correct horse battery"));
        Assert.NotNull(PasswordHasher.Weakness("short"));
    }

    [Fact]
    public async Task Store_RejectsDisabledUsersAndDuplicateNames()
    {
        var store = new UserStore(NewPaths(), TimeProvider.System);
        var user = await store.CreateAsync("Zhang", "张三", "0123456789", Roles.Operator, []);

        Assert.NotNull(await store.VerifyAsync("zhang", "0123456789"));
        Assert.Null(await store.VerifyAsync("zhang", "wrong-password"));
        await Assert.ThrowsAsync<UsernameTakenException>(() => store.CreateAsync("ZHANG", "", "0123456789", Roles.Operator, []));

        await store.UpdateAsync(user.Id, u => u.Disabled = true);
        Assert.Null(await store.VerifyAsync("zhang", "0123456789"));
    }

    [Fact]
    public async Task Operators_SeeOnlyTheirAccountsListingsAndTheirOwnDrafts()
    {
        var paths = NewPaths();
        var config = new ConfigurationBuilder().Build();
        var accountStore = new AlibabaAccountStore(paths, new EphemeralDataProtectionProvider(), TimeProvider.System, NullLogger<AlibabaAccountStore>.Instance);
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        await accountStore.UpdateAsync(accounts =>
        {
            accounts.Add(new AlibabaAccount { Id = mine, Name = "张三", OwnerAliIds = ["100"] });
            accounts.Add(new AlibabaAccount { Id = other, Name = "李四", OwnerAliIds = ["200"] });
            return true;
        });
        var tokens = new AlibabaTokenService(null!, accountStore, config, TimeProvider.System);
        var users = new UserStore(paths, TimeProvider.System);
        var zhang = await users.CreateAsync("zhang", "张三", "0123456789", Roles.Operator, [mine]);
        var boss = await users.CreateAsync("boss", "老板", "0123456789", Roles.Admin, []);
        var access = new AccessService(users, tokens, config, new HostingEnvironment { EnvironmentName = "Production" });

        ClaimsPrincipal As(UserRecord user) => new(new ClaimsIdentity([new Claim(UserClaims.UserId, user.Id.ToString())], "test"));
        var owned = new ProductRecord { OwnerAliId = "100" };
        var foreign = new ProductRecord { OwnerAliId = "200" };
        var assigned = new ProductRecord { OwnerAliId = "200", AccountId = mine };
        var ownDraft = new ProductRecord { CreatedBy = zhang.Id.ToString() };
        var someoneElsesDraft = new ProductRecord { CreatedBy = boss.Id.ToString() };

        var visible = await access.VisibleAsync(As(zhang));
        Assert.True(visible(owned));
        Assert.False(visible(foreign));
        Assert.True(visible(assigned));
        Assert.True(visible(ownDraft));
        Assert.False(visible(someoneElsesDraft));

        var all = await access.VisibleAsync(As(boss));
        Assert.True(all(foreign) && all(someoneElsesDraft));

        await users.UpdateAsync(zhang.Id, u => u.Disabled = true);
        Assert.False((await access.VisibleAsync(As(zhang)))(owned));
    }
}
