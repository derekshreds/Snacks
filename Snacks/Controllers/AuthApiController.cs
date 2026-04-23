using Microsoft.AspNetCore.Mvc;
using Snacks.Models.Requests;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     JSON API for managing the control-panel auth config. The login-form
///     endpoints live on <see cref="AuthController"/>.
/// </summary>
[Route("api/auth")]
[ApiController]
public sealed class AuthApiController : ControllerBase
{
    private readonly AuthService _auth;

    public AuthApiController(AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(auth);
        _auth = auth;
    }

    /******************************************************************
     *  Auth Config
     ******************************************************************/

    /// <summary> Returns the public auth configuration (enabled flag, username, password presence). </summary>
    [HttpGet("config")]
    public IActionResult Get() => new JsonResult(_auth.GetPublicConfig());

    /// <summary>
    ///     Saves updated auth configuration. Enabling auth requires a username and, if no
    ///     password hash is already stored, a plain-text password to hash.
    /// </summary>
    /// <param name="req"> The new auth configuration to apply. </param>
    [HttpPost("config")]
    public IActionResult Save([FromBody] SaveAuthRequest req)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(req);
            if (req.Enabled && string.IsNullOrWhiteSpace(req.Username))
                return new JsonResult(new { success = false, error = "Username required to enable auth" });

            var existing = _auth.GetConfig();
            if (req.Enabled && string.IsNullOrEmpty(existing.PasswordHash) && string.IsNullOrEmpty(req.Password))
                return new JsonResult(new { success = false, error = "Password required to enable auth" });

            _auth.UpdateConfig(req.Enabled, req.Username, req.Password);

            // Force a fresh login whenever the config changes: drop whatever session
            // cookie the caller had so they have to re-authenticate under the new
            // settings. Disabling already rotates the server-side secret; clearing the
            // cookie as well keeps the client and server in sync.
            Response.Cookies.Delete(AuthService.CookieName);

            return new JsonResult(new { success = true, authRequired = _auth.IsAuthRequired() });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }
}
