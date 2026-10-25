using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Gates the UI behind cookie-based login when <see cref="AuthConfig.Enabled"/> is true.
///     Login page, static files, health checks, and cluster (master↔node) traffic are always allowed.
///     Cluster nodes authenticate via the shared secret handled separately by ClusterAuthMiddleware.
///     When no login is configured, only local-network clients (or API-key callers) get through —
///     a port-forwarded or reverse-proxied instance must not hand anonymous internet visitors
///     full control. <see cref="AllowRemoteWithoutAuthVariable"/> opts out for deployments whose
///     upstream proxy already authenticates users.
/// </summary>
public sealed class AuthMiddleware
{
    public const string AllowRemoteWithoutAuthVariable = "SNACKS_ALLOW_REMOTE_WITHOUT_AUTH";

    private const string RemoteAccessRequiresLoginMessage =
        "Remote access requires a login. Snacks has no password configured, so it only accepts " +
        "requests from local network addresses. Open Snacks from your local network and enable " +
        "authentication under Settings > System > Security, or set " + AllowRemoteWithoutAuthVariable +
        "=true if an upstream proxy already authenticates users.";

    private static readonly string[] AllowlistPrefixes =
    {
        "/Auth/",             // login form
        "/api/cluster/",      // inter-node RPC (secret-authenticated)
        "/api/health",        // liveness probe
        "/lib/", "/css/", "/js/", "/img/", "/images/", "/webfonts/", "/docs/", "/favicon",
    };

    private static readonly bool AllowRemoteWithoutAuth =
        (Environment.GetEnvironmentVariable(AllowRemoteWithoutAuthVariable) ?? "")
            .Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    private static long _lastRemoteRejectionLog;

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
        var path = ctx.Request.Path.Value ?? "";

        if (!auth.IsAuthRequired())
        {
            if (IsAllowlisted(path)
                || AllowRemoteWithoutAuth
                || ClientNetwork.IsLocalClient(ctx)
                || (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    && auth.ValidateApiKey(ExtractApiKey(ctx, path)))
                || (path.StartsWith("/iframe/", StringComparison.OrdinalIgnoreCase)
                    && auth.ValidateEmbedToken(ctx.Request.Query["embedToken"].FirstOrDefault())))
            {
                await _next(ctx);
                return;
            }

            LogRemoteRejection(ctx, path);
            ctx.Response.StatusCode  = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(RemoteAccessRequiresLoginMessage);
            return;
        }

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

        // The published API contract (linked from the offline guide). It describes routes
        // that are already public in the source; exact match keeps it to this one document.
        if (string.Equals(path, "/openapi/v1.json", StringComparison.OrdinalIgnoreCase)) return true;

        // Static files are served by UseStaticFiles before this middleware runs, so only
        // these known asset prefixes need listing. Never allow by file extension: the
        // conventional route accepts a dotted {id}, which turned "/Home/Index/x.y" into
        // an unauthenticated route.
        foreach (var prefix in AllowlistPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary> Logs blocked remote access at most once a minute so a scanner can't flood the log. </summary>
    private static void LogRemoteRejection(HttpContext ctx, string path)
    {
        var now  = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRemoteRejectionLog);
        if (last != 0 && now - last < 60_000) return;
        if (Interlocked.CompareExchange(ref _lastRemoteRejectionLog, now, last) != last) return;

        Log.Warning(
            "Blocked {Path} from non-local client {Remote}: no login is configured. Enable authentication " +
            "under Settings > System > Security, or set {Variable}=true if an upstream proxy authenticates users",
            path, ctx.Connection.RemoteIpAddress, AllowRemoteWithoutAuthVariable);
    }
}
