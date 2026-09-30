using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Local runs read the same .env docker compose uses, so AppKey/AppSecret only live in one place.
    builder.Configuration.AddDotEnvDefaults(
        Path.Combine(builder.Environment.ContentRootPath, ".env"),
        Path.Combine(builder.Environment.ContentRootPath, "..", ".env"));
}

var paths = AppPaths.FromConfiguration(builder.Configuration, builder.Environment);

builder.Services.AddCors(options =>
{
    options.AddPolicy("web", policy =>
        policy.WithOrigins(
                builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:5173", "http://127.0.0.1:5173"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddDataProtection()
    .SetApplicationName("SofordErp")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.File("data-protection-keys")));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // Trust forwarded headers only from the local nginx and private (docker) networks, so a client that reaches the
    // API directly cannot fake its IP and dodge the login throttle.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    foreach (var network in new[] { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" })
    {
        var parts = network.Split('/');
        options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1])));
    }

    options.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SofordErp.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(10);
        // Disabled users, deleted users, changed passwords and changed roles end existing sessions right away.
        options.Events.OnValidatePrincipal = async context =>
        {
            var id = context.Principal is null ? null : UserClaims.Id(context.Principal);
            if (id == CurrentUser.BuiltInAdminId) return;
            var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var user = Guid.TryParse(id, out var guid) ? await users.FindAsync(guid) : null;
            if (user is null || user.Disabled
                || context.Principal!.FindFirstValue(UserClaims.Stamp) != user.SecurityStamp
                || !context.Principal!.IsInRole(user.Role))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.AddPolicy(ApiEndpoints.AdminPolicy, policy => policy.RequireRole(Roles.Admin)));

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ListingAdvisor>();
builder.Services.AddHttpClient("diagnostics");
// Per-call timeouts are applied in AlibabaTransport; this is only an outer safety net.
builder.Services.AddHttpClient(AlibabaTransport.HttpClientName, client => client.Timeout = TimeSpan.FromMinutes(5));
builder.Services.AddSingleton<AlibabaTransport>();
builder.Services.AddSingleton<AlibabaApiLogStore>();
builder.Services.AddSingleton<AlibabaAccountStore>();
builder.Services.AddSingleton<AlibabaTokenService>();
builder.Services.AddSingleton<AlibabaClient>();
builder.Services.AddSingleton<CategoryAttributeService>();
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<ProductQualityService>();
builder.Services.AddSingleton<ProductImportService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<ProductLocks>();
builder.Services.AddSingleton<ProductOperations>();
builder.Services.AddTransient<SystemDiagnostics>();
builder.Services.AddSingleton<PublishJobStore>();
builder.Services.AddSingleton<PublishQueue>();
builder.Services.AddHostedService<PublishWorker>();
builder.Services.AddSingleton<AlibabaCatalogSync>();
builder.Services.AddSingleton<AutomationState>();
builder.Services.AddSingleton<RemoteSyncWorker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RemoteSyncWorker>());

var app = builder.Build();

LocalAdminAuth.ValidateConfiguration(app.Configuration, app.Environment);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseCors("web");
app.UseAuthentication();
app.UseAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuthEndpoints();
api.MapAlibabaEndpoints();
api.MapCatalogEndpoints();
api.MapUserEndpoints();

// The seller is redirected here by Alibaba; it must match the callback URL registered in App Console.
app.MapGet("/openapi/callback", ApiEndpoints.CallbackAsync);

app.Run();

/// <summary>Visible to the integration tests (WebApplicationFactory&lt;Program&gt;).</summary>
public partial class Program;
