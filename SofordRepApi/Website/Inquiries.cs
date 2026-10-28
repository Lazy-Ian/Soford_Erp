using System.Net.Mail;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

public enum InquiryStatus { New, Replied, Closed, Spam }

/// <summary>A buyer's message from the public website's quote form.</summary>
public sealed class Inquiry
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Company { get; set; } = "";
    public string Country { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Product { get; set; } = "";
    /// <summary>Stable public website product id when the buyer started from a product page.</summary>
    public string ProductId { get; set; } = "";
    public string Quantity { get; set; } = "";
    public string Message { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string SourceIp { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public InquiryStatus Status { get; set; } = InquiryStatus.New;
    public string Note { get; set; } = "";
    public string? HandledBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public Inquiry Copy() => (Inquiry)MemberwiseClone();
}

/// <summary>`Website` is a honeypot: hidden on the form, so only bots fill it in.</summary>
public sealed record InquiryRequest(
    string? Name, string? Email, string? Company, string? Country, string? Phone,
    string? Product, string? ProductId, string? Quantity, string? Message, string? PageUrl, string? Website);

public sealed record InquiryUpdateRequest(InquiryStatus? Status, string? Note, DateTimeOffset? ExpectedUpdatedAt = null);
public sealed record InquiryPage(List<Inquiry> Items, int Total, int Page, int PageSize, Dictionary<InquiryStatus, int> Counts);

/// <summary>Website inquiries (inquiries.json), held in memory after the first read.</summary>
public sealed class InquiryStore(AppPaths paths)
{
    private const int MaxEntries = 20000;
    private readonly string _file = paths.File("inquiries.json");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<Inquiry>? _cache;

    public async Task AddAsync(Inquiry inquiry)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync();
            try
            {
                all.Add(inquiry.Copy());
                if (all.Count > MaxEntries) all.RemoveRange(0, all.Count - MaxEntries);
                await JsonFile.WriteAtomicAsync(_file, all);
            }
            catch
            {
                _cache = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<Inquiry>> LatestAsync(int take)
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAsync()).AsEnumerable().Reverse().Take(take).Select(x => x.Copy()).ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> CountNewAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return (await ReadAsync()).Count(x => x.Status == InquiryStatus.New);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<InquiryPage> SearchAsync(int page, int pageSize, InquiryStatus? status, string? query)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync();
            var counts = Enum.GetValues<InquiryStatus>().ToDictionary(s => s, s => all.Count(x => x.Status == s));
            var key = query?.Trim() ?? "";
            var matches = all.AsEnumerable().Reverse()
                .Where(x => status is null || x.Status == status)
                .Where(x => key.Length == 0 || new[] { x.Name, x.Email, x.Company, x.Country, x.Phone, x.Product, x.ProductId, x.Quantity, x.Message, x.Note }
                    .Any(v => v.Contains(key, StringComparison.OrdinalIgnoreCase))).ToList();
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(matches.Count / (double)pageSize)));
            return new InquiryPage(matches.Skip((page - 1) * pageSize).Take(pageSize).Select(x => x.Copy()).ToList(), matches.Count, page, pageSize, counts);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task<Inquiry?> UpdateAsync(Guid id, Action<Inquiry> change) => UpdateAsync(id, change, null, false);

    public async Task<Inquiry?> UpdateAsync(Guid id, Action<Inquiry> change, DateTimeOffset? expectedUpdatedAt, bool checkVersion = true)
    {
        await _lock.WaitAsync();
        try
        {
            var all = await ReadAsync();
            if (all.FirstOrDefault(x => x.Id == id) is not { } inquiry) return null;
            if (checkVersion && inquiry.UpdatedAt != expectedUpdatedAt) throw new WebsiteConcurrencyException();
            try
            {
                change(inquiry);
                await JsonFile.WriteAtomicAsync(_file, all);
                return inquiry.Copy();
            }
            catch
            {
                _cache = null;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<Inquiry>> ReadAsync() => _cache ??= await JsonFile.ReadAsync<List<Inquiry>>(_file) ?? [];
}

public static class InquiryEndpoints
{
    public const string CorsPolicy = "website";
    public const string RateLimitPolicy = "inquiry";
    private static readonly string[] DefaultOrigins = ["http://localhost:5180", "http://127.0.0.1:5180"];

    public static string[] AllowedOrigins(IConfiguration config) =>
        config.GetSection("Website:AllowedOrigins").Get<string[]>() ?? DefaultOrigins;

    /// <summary>CORS for the public website and a per-IP limit so the open endpoint cannot be used to flood the inbox.</summary>
    public static IServiceCollection AddWebsiteInquiries(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<InquiryStore>();
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
            policy.WithOrigins(AllowedOrigins(config)).WithMethods("GET", "HEAD", "POST").WithHeaders("Content-Type")));
        var perHour = config.GetValue("Website:InquiriesPerHourPerIp", 10);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perHour, Window = TimeSpan.FromHours(1) }));
        });
        return services;
    }

    public static void MapInquiryEndpoints(this WebApplication app, RouteGroupBuilder api)
    {
        // Public: the website posts here without a login. Lives outside /api so the ERP's cookie auth and
        // cross-site guard don't apply; the website's own origins are allowed instead.
        app.MapPost("/public/inquiries", SubmitAsync)
            .AllowAnonymous()
            .RequireCors(CorsPolicy)
            .RequireRateLimiting(RateLimitPolicy);

        // Inquiries are sales leads, so every signed-in user (admins and operators) handles them.
        var inquiries = api.MapGroup("/inquiries");
        inquiries.MapGet("/", async (InquiryStore store, int take = 500) => Results.Ok(await store.LatestAsync(Math.Clamp(take, 1, 5000))));
        inquiries.MapGet("/search", async (InquiryStore store, int page = 1, int pageSize = 50, string? status = null, string? q = null) =>
        {
            InquiryStatus? filter = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<InquiryStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                    return ApiEndpoints.Problem("询盘状态无效");
                filter = parsed;
            }
            if (q?.Length > 200) return ApiEndpoints.Problem("搜索内容最多 200 个字符");
            return Results.Ok(await store.SearchAsync(page, pageSize, filter, q));
        });
        inquiries.MapGet("/summary", async (InquiryStore store) => Results.Ok(new { New = await store.CountNewAsync() }));
        inquiries.MapPut("/{id:guid}", async (Guid id, InquiryUpdateRequest request, InquiryStore store, TimeProvider time, ClaimsPrincipal principal, AccessService access, AuditLog audit) =>
        {
            if (request.Status is { } requestedStatus && !Enum.IsDefined(requestedStatus))
                return ApiEndpoints.Problem("询盘状态无效");
            var user = await access.CurrentAsync(principal);
            Inquiry? updated;
            try { updated = await store.UpdateAsync(id, x =>
            {
                if (request.Status is { } status) x.Status = status;
                if (request.Note is not null) x.Note = Clip(request.Note, 2000);
                x.HandledBy = user?.Name;
                var now = time.GetUtcNow();
                x.UpdatedAt = x.UpdatedAt is { } previous && now <= previous ? previous.AddTicks(1) : now;
            }, request.ExpectedUpdatedAt); }
            catch (WebsiteConcurrencyException) { return ApiEndpoints.Problem("询盘已被修改", "请关闭后重新打开最新询盘，再保存修改。", StatusCodes.Status409Conflict); }
            if (updated is null) return ApiEndpoints.Problem("询盘不存在", status: StatusCodes.Status404NotFound);
            await audit.RecordAsync(user, "处理询盘", $"{updated.Name} <{updated.Email}>：{updated.Status}");
            return Results.Ok(updated);
        });
    }

    internal static async Task<IResult> SubmitAsync(InquiryRequest request, HttpContext http, IConfiguration config, InquiryStore store, TimeProvider time, ILoggerFactory logs)
    {
        // CORS only hides the response; reject foreign browser origins before anything is stored.
        var origin = http.Request.Headers.Origin.ToString().TrimEnd('/');
        if (origin.Length > 0 && !AllowedOrigins(config).Any(x => x.TrimEnd('/').Equals(origin, StringComparison.OrdinalIgnoreCase)))
        {
            return ApiEndpoints.Problem("Forbidden", "This form can only be submitted from the Soford website.", StatusCodes.Status403Forbidden);
        }

        if (!string.IsNullOrWhiteSpace(request.Website))
        {
            // Pretend success so the bot learns nothing.
            return Results.Ok(new { Received = true });
        }

        if (Validate(request) is { } error) return ApiEndpoints.Problem("Please check the form", error);

        var inquiry = new Inquiry
        {
            Id = Guid.NewGuid(),
            CreatedAt = time.GetUtcNow(),
            Name = Clip(request.Name, 100),
            Email = Clip(request.Email, 200),
            Company = Clip(request.Company, 200),
            Country = Clip(request.Country, 100),
            Phone = Clip(request.Phone, 50),
            Product = Clip(request.Product, 300),
            ProductId = Clip(request.ProductId, 50),
            Quantity = Clip(request.Quantity, 100),
            Message = Clip(request.Message, 5000),
            PageUrl = Clip(request.PageUrl, 500),
            SourceIp = http.Connection.RemoteIpAddress?.ToString() ?? "",
            UserAgent = Clip(http.Request.Headers.UserAgent.ToString(), 300),
        };
        await store.AddAsync(inquiry);
        logs.CreateLogger("Inquiries").LogInformation("New website inquiry {Id} from {Email}", inquiry.Id, inquiry.Email);
        return Results.Ok(new { Received = true });
    }

    private static string? Validate(InquiryRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Please enter your name.";
        if (string.IsNullOrWhiteSpace(r.Email) || r.Email.Length > 200 || !MailAddress.TryCreate(r.Email.Trim(), out var mail) || !mail.Host.Contains('.'))
        {
            return "Please enter a valid email address.";
        }

        if (string.IsNullOrWhiteSpace(r.Message) || r.Message.Trim().Length < 10) return "Please tell us a little more about what you need (at least 10 characters).";
        if (r.Message.Length > 5000) return "The message is too long (5,000 characters at most).";
        return null;
    }

    private static string Clip(string? value, int max)
    {
        var text = value?.Trim() ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
