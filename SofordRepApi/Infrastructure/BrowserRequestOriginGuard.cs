/// <summary>
/// CORS controls whether a browser may read a response, but it does not stop a credentialed form POST from executing.
/// Reject unsafe browser requests from origins other than this host or the explicitly configured development frontend.
/// Requests without browser origin metadata (CLI, health agents, server-to-server clients) remain supported.
/// </summary>
public sealed class BrowserRequestOriginGuard(RequestDelegate next, IConfiguration configuration)
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };
    private readonly HashSet<string> _allowedOrigins = (configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173", "http://127.0.0.1:5173"])
        .Select(Normalize)
        .Where(x => x.Length > 0)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api") && !SafeMethods.Contains(context.Request.Method))
        {
            var origin = Normalize(context.Request.Headers.Origin.ToString());
            var sameOrigin = Normalize($"{context.Request.Scheme}://{context.Request.Host}");
            var crossSite = context.Request.Headers["Sec-Fetch-Site"].ToString().Equals("cross-site", StringComparison.OrdinalIgnoreCase);
            if ((origin.Length > 0 && !origin.Equals(sameOrigin, StringComparison.OrdinalIgnoreCase) && !_allowedOrigins.Contains(origin))
                || (origin.Length == 0 && crossSite))
            {
                await Results.Problem(
                    title: "已阻止跨站请求",
                    detail: "请从 Soford ERP 页面发起此操作。",
                    statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static string Normalize(string? origin) => origin?.Trim().TrimEnd('/') ?? "";
}
