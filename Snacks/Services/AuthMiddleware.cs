using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Gates the UI behind cookie-based login when <see cref="AuthConfig.Enabled"/> is true.
///     Login page, static files, SignalR hub, and cluster (master↔node) traffic are always allowed.
///     Cluster nodes authenticate via the shared secret handled separately by ClusterAuthMiddleware.
/// </summary>
public sealed class AuthMiddleware
{
    private static readonly string[] AllowlistPrefixes =
    {
        "/Auth/",             // login form
        "/transcodingHub",    // SignalR
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

        // API calls get 401; browser navigation gets redirected to login.
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
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

    private static bool IsAllowlisted(string path)
    {
        foreach (var prefix in AllowlistPrefixes)
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        // Static files (anything with an extension and not an /api/ call).
        if (path.Contains('.', StringComparison.Ordinal)
            && !path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
