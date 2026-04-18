using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Login / logout endpoints and the login-form view. Always reachable
///     regardless of <see cref="AuthService.IsAuthRequired"/>.
/// </summary>
public sealed class AuthController : Controller
{
    private readonly AuthService _auth;

    public AuthController(AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
    }

    /******************************************************************
     *  Login Form
     ******************************************************************/

    /// <summary> Renders the login form, optionally pre-loading a return URL. </summary>
    /// <param name="returnUrl"> The URL to redirect to after successful login. </param>
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl ?? "/";
        return View();
    }

    /// <summary>
    ///     Validates the submitted credentials. On success, issues a session cookie and
    ///     redirects to <paramref name="returnUrl"/>. On failure, re-renders the form
    ///     with an error message.
    /// </summary>
    /// <param name="username"> The submitted username. </param>
    /// <param name="password"> The submitted password. </param>
    /// <param name="returnUrl"> The URL to redirect to after a successful login. </param>
    [HttpPost]
    public IActionResult Login([FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl)
    {
        var cfg = _auth.GetConfig();
        if (string.IsNullOrEmpty(cfg.PasswordHash))
        {
            ViewData["Error"]     = "No password is configured. Delete auth.json to reset.";
            ViewData["ReturnUrl"] = returnUrl ?? "/";
            return View();
        }

        if (!string.Equals(cfg.Username, username?.Trim(), StringComparison.OrdinalIgnoreCase)
            || !_auth.VerifyPassword(password ?? ""))
        {
            ViewData["Error"]     = "Invalid username or password";
            ViewData["ReturnUrl"] = returnUrl ?? "/";
            return View();
        }

        var token = _auth.IssueToken(cfg.Username);
        Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure   = Request.IsHttps,
            Expires  = DateTimeOffset.UtcNow.AddDays(14),
        });

        return Redirect(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl);
    }

    /******************************************************************
     *  Logout
     ******************************************************************/

    /// <summary> Clears the session cookie and redirects to the login page. </summary>
    [HttpPost]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(AuthService.CookieName);
        return Redirect("/Auth/Login");
    }
}
