using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Gates the UI behind cookie-based login when <see cref="AuthConfig.Enabled"/> is true.
///     Login page, static files, health checks, and cluster (master↔node) traffic are always allowed.
///     Cluster nodes authenticate via the shared secret handled separately by ClusterAuthMiddleware.
/// </summary>
public sealed class AuthMiddleware
{
    private static readonly string[] AllowlistPrefixes =
    {
        "/Auth/",             // login form
        "/api/cluster/",      // inter-node RPC (secret-authenticated)
        "/api/health",        // liveness probe
        "/lib/", "/css/", "/js/", "/img/", "/favicon",
    };

    private readonly RequestDelegate _next;

    public AuthMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /******************************************************************
     *  Middleware Pipeline
     ******************************************************************/

    /// <summary>
    ///     Validates the session cookie against the active auth configuration. Unauthenticated
    ///     API requests receive HTTP 401; unauthenticated browser navigation is redirected to
    ///     the login page.
    /// </summary>
    /// <param name="ctx"> The current HTTP context. </param>
    /// <param name="auth"> The auth service resolved per-request from DI. </param>
    public async Task InvokeAsync(HttpContext ctx, AuthService auth)
    {
        if (!auth.IsAuthRequired())
        {
            await _next(ctx);
            return;
        }

        var path = ctx.Request.Path.Value ?? "";

        if (IsAllowlisted(path))
        {
            await _next(ctx);
            return;
        }

        var token = ctx.Request.Cookies[AuthService.CookieName];
        if (auth.ValidateToken(token, out _))
        {
            await _next(ctx);
            return;
        }

        // Browsers cannot attach an X-Api-Key header to iframe navigation. A separate,
        // read-only token keeps the embed usable without exposing a full-access API key
        // in a URL. It is accepted nowhere outside /iframe/*.
        if (path.StartsWith("/iframe/", StringComparison.OrdinalIgnoreCase)
            && auth.ValidateEmbedToken(ctx.Request.Query["embedToken"].FirstOrDefault()))
        {
            await _next(ctx);
            return;
        }

        // API key (X-Api-Key header, Bearer token, or ?apiKey= query string) — the
        // automation path for orchestrators and dashboards that can't do cookie login.
        // API routes only; browser navigation still goes through the login form.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && auth.ValidateApiKey(ExtractApiKey(ctx, path)))
        {
            await _next(ctx);
            return;
        }

        // API calls get 401; browser navigation gets redirected to login.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/iframe/", StringComparison.OrdinalIgnoreCase)
            || ctx.Request.Headers["X-Requested-With"] == "XMLHttpRequest"
            || ctx.Request.Headers.Accept.ToString().Contains("application/json"))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("Unauthorized");
            return;
        }

        ctx.Response.Redirect("/Auth/Login?returnUrl=" + Uri.EscapeDataString(path));
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    private static string? ExtractApiKey(HttpContext ctx, string path)
    {
        var headerKey = ctx.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(headerKey)) return headerKey;

        var authorization = ctx.Request.Headers.Authorization.FirstOrDefault();
        const string bearer = "Bearer ";
        if (authorization?.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) == true)
            return authorization[bearer.Length..];

        // URL credentials leak into browser history and proxy logs. Keep compatibility
        // only on the intentionally read-only dashboard surfaces; mutation endpoints
        // require a header, bearer token, or authenticated session.
        return IsReadOnlyIntegrationPath(path)
            ? ctx.Request.Query["apiKey"].FirstOrDefault()
            : null;
    }

    internal static bool IsReadOnlyIntegrationPath(string path) =>
        path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/api/v2/is-server-alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/api/v2/stats/get-pies", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/api/v2/get-nodes", StringComparison.OrdinalIgnoreCase)
        || string.Equals(path, "/api/v2/client/status-tables", StringComparison.OrdinalIgnoreCase);

    internal static bool IsAllowlisted(string path)
    {
        // Prometheus scrape — aggregate counters only, no file paths or credentials;
        // scrapers can't do cookie login. Exact match (not a prefix) so a future
        // "/metrics/..." management route can't silently ship unauthenticated.
        if (string.Equals(path, "/metrics", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var prefix in AllowlistPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // Static files (anything with an extension and not an /api/ call).
        if (path.Contains('.', StringComparison.Ordinal)
            && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
