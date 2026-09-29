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
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
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
builder.Services.AddAuthorization();

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddHttpClient("diagnostics");
builder.Services.AddHttpClient(AlibabaTransport.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<AlibabaTransport>();
builder.Services.AddSingleton<AlibabaApiLogStore>();
builder.Services.AddSingleton<AlibabaTokenStore>();
builder.Services.AddSingleton<AlibabaTokenService>();
builder.Services.AddSingleton<AlibabaClient>();
builder.Services.AddSingleton<CategoryAttributeService>();
builder.Services.AddSingleton<ProductRepository>();
builder.Services.AddSingleton<ProductQualityService>();
builder.Services.AddSingleton<ProductImportService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<ProductOperations>();
builder.Services.AddTransient<SystemDiagnostics>();
builder.Services.AddSingleton<PublishJobStore>();
builder.Services.AddSingleton<PublishQueue>();
builder.Services.AddHostedService<PublishWorker>();

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

// The seller is redirected here by Alibaba; it must match the callback URL registered in App Console.
app.MapGet("/openapi/callback", ApiEndpoints.CallbackAsync);

app.Run();
