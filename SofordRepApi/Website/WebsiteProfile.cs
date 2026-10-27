using System.Net.Mail;
using System.Security.Claims;
using System.Text.RegularExpressions;

/// <summary>Public company facts shown on the website. Edited in the ERP, stored as website-profile.json.</summary>
public sealed class WebsiteProfile
{
    public string Company { get; set; } = "";
    public string Address { get; set; } = "";
    public string AlibabaStore { get; set; } = "";
    public long Revision { get; set; }
    public WebsiteTerms Terms { get; set; } = new();
    public List<WebsiteContact> Contacts { get; set; } = [];

    /// <summary>Confirmed details used until someone saves. Terms stay empty so the site keeps its translated FAQ.</summary>
    public static WebsiteProfile Default() => new()
    {
        Company = "Qingdao SoFord Import And Export Co., Ltd.",
        Address = "Qingdao, Shandong, China",
        Contacts =
        [
            new WebsiteContact
            {
                Id = Guid.Parse("b6e1c2a0-7f44-4c3a-9a1e-5d8e0a1c2b30"),
                Name = "Wendy Wang",
                Title = "Trade Manager",
                Email = "Wendy91715@foxmail.com",
                Phone = "+86 185 6280 9257",
                Whatsapp = "8618562809257",
                Wechat = "+86 185 6280 9257",
                Qq = "2929004105",
            },
        ],
    };

    public WebsiteProfile Copy() => new()
    {
        Company = Company,
        Address = Address,
        AlibabaStore = AlibabaStore,
        Revision = Revision,
        Terms = Terms?.Copy() ?? new WebsiteTerms(),
        Contacts = Contacts?.Select(x => x.Copy()).ToList() ?? [],
    };
}

/// <summary>Sentences that replace the home-page FAQ when filled. Empty keeps the built-in translations.</summary>
public sealed class WebsiteTerms
{
    public string Payment { get; set; } = "";
    public string Sample { get; set; } = "";
    public string TradeTerms { get; set; } = "";
    public string Moq { get; set; } = "";
    public string LeadTime { get; set; } = "";
    public string Warranty { get; set; } = "";

    public WebsiteTerms Copy() => new()
    {
        Payment = Payment,
        Sample = Sample,
        TradeTerms = TradeTerms,
        Moq = Moq,
        LeadTime = LeadTime,
        Warranty = Warranty,
    };
}

public sealed class WebsiteContact
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Title { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Whatsapp { get; set; } = "";
    public string Wechat { get; set; } = "";
    public string Qq { get; set; } = "";

    public WebsiteContact Copy() => (WebsiteContact)MemberwiseClone();
}

/// <summary>Website profile (website-profile.json). A missing file reads as <see cref="WebsiteProfile.Default"/> and is not written until save.</summary>
public sealed class WebsiteProfileStore(AppPaths paths)
{
    private readonly string _file = paths.File("website-profile.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WebsiteProfile? _cache;

    public async Task<WebsiteProfile> GetAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAsync()).Copy();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<WebsiteProfile> SaveAsync(WebsiteProfile profile)
    {
        await _lock.WaitAsync();
        try
        {
            var previous = await ReadAsync();
            if (profile.Revision != previous.Revision) throw new WebsiteConcurrencyException();
            var next = profile.Copy();
            next.Revision = checked(previous.Revision + 1);
            try
            {
                await JsonFile.WriteAtomicAsync(_file, next);
                _cache = next;
                return next.Copy();
            }
            catch
            {
                _cache = previous;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<WebsiteProfile> ReadAsync()
    {
        if (_cache is not null) return _cache;
        var stored = await JsonFile.ReadAsync<WebsiteProfile>(_file);
        if (stored is null) stored = WebsiteProfile.Default();
        stored.Terms ??= new WebsiteTerms();
        stored.Contacts ??= [];
        _cache = stored;
        return _cache;
    }
}

public static class WebsiteProfileEndpoints
{
    public const int MaxContacts = 12;
    private static readonly Regex PlaceholderPhone = new(@"(?:^|\s)0{3,}[\s-]*0{3,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void MapWebsiteProfile(this WebApplication app, RouteGroupBuilder api)
    {
        app.MapGet("/public/site", async (HttpContext http, WebsiteProfileStore store) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            return Results.Json(await store.GetAsync());
        }).AllowAnonymous().RequireCors(InquiryEndpoints.CorsPolicy);

        var profile = api.MapGroup("/website/profile").RequireAuthorization(ApiEndpoints.AdminPolicy);
        profile.MapGet("/", async (WebsiteProfileStore store) => Results.Ok(await store.GetAsync()));
        profile.MapPut("/", async (WebsiteProfile request, WebsiteProfileStore store, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (Normalize(request) is { } error) return ApiEndpoints.Problem(error);
            WebsiteProfile saved;
            try { saved = await store.SaveAsync(request); }
            catch (WebsiteConcurrencyException) { return ApiEndpoints.Problem("官网资料已被修改", "请重新加载最新资料后再保存。", StatusCodes.Status409Conflict); }
            var user = await access.CurrentAsync(principal);
            await audit.RecordAsync(user, "修改官网资料", $"{saved.Company}，{saved.Contacts.Count} 位联系人");
            return Results.Ok(saved);
        });
    }

    /// <summary>Returns an error message, or null after the profile is trimmed and ready to store.</summary>
    public static string? Normalize(WebsiteProfile profile)
    {
        if (profile.Contacts is null) return "请提供联系人列表";
        if (profile.Contacts.Count > MaxContacts) return $"联系人最多 {MaxContacts} 位";

        profile.Company = Clip(profile.Company, 200);
        profile.Address = Clip(profile.Address, 300);
        profile.AlibabaStore = Clip(profile.AlibabaStore, 300);
        if (profile.AlibabaStore.Length > 0 && !IsHttpUrl(profile.AlibabaStore))
            return "国际站店铺链接需要以 http:// 或 https:// 开头";

        profile.Terms ??= new WebsiteTerms();
        profile.Terms.Payment = Clip(profile.Terms.Payment, 800);
        profile.Terms.Sample = Clip(profile.Terms.Sample, 800);
        profile.Terms.TradeTerms = Clip(profile.Terms.TradeTerms, 800);
        profile.Terms.Moq = Clip(profile.Terms.Moq, 800);
        profile.Terms.LeadTime = Clip(profile.Terms.LeadTime, 800);
        profile.Terms.Warranty = Clip(profile.Terms.Warranty, 800);

        var kept = new List<WebsiteContact>();
        var seen = new HashSet<Guid>();
        foreach (var contact in profile.Contacts)
        {
            contact.Name = Clip(contact.Name, 80);
            contact.Title = Clip(contact.Title, 80);
            contact.Email = Clip(contact.Email, 200);
            contact.Phone = Clip(contact.Phone, 40);
            contact.Whatsapp = Clip(contact.Whatsapp, 24);
            contact.Wechat = Clip(contact.Wechat, 40);
            contact.Qq = Clip(contact.Qq, 12);
            if (IsBlank(contact)) continue;
            if (contact.Name.Length == 0) return "每位联系人都要填写姓名";
            if (contact.Email.Length > 0 && !MailAddress.TryCreate(contact.Email, out _))
                return $"「{contact.Name}」的邮箱格式不正确";
            var whatsappDigits = Digits(contact.Whatsapp);
            if (PlaceholderPhone.IsMatch(contact.Phone) || (whatsappDigits.Length > 0 && whatsappDigits.All(c => c == '0')))
                return $"「{contact.Name}」的电话或 WhatsApp 不是有效号码";
            if (contact.Whatsapp.Length > 0 && (whatsappDigits.Length < 8 || whatsappDigits.Length > 15))
                return $"「{contact.Name}」的 WhatsApp 需要带国家码的手机号";
            if (contact.Qq.Length > 0 && !Regex.IsMatch(contact.Qq, @"^\d{5,12}$"))
                return $"「{contact.Name}」的 QQ 需要 5 到 12 位数字";
            if (contact.Id == Guid.Empty || !seen.Add(contact.Id)) contact.Id = Guid.NewGuid();
            kept.Add(contact);
        }

        profile.Contacts = kept;
        return null;
    }

    private static bool IsBlank(WebsiteContact contact) =>
        contact.Name.Length == 0 && contact.Title.Length == 0 && contact.Email.Length == 0
        && contact.Phone.Length == 0 && contact.Whatsapp.Length == 0 && contact.Wechat.Length == 0 && contact.Qq.Length == 0;

    private static string Clip(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max];
    }

    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());

    private static bool IsHttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);
    }
}

public sealed class WebsiteConcurrencyException : Exception;
