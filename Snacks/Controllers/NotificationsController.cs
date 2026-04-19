using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Configuration and test endpoints for outbound notifications (webhooks, ntfy, Apprise).
/// </summary>
[Route("api/notifications")]
[ApiController]
public sealed class NotificationsController : ControllerBase
{
    private readonly NotificationService _notifications;

    public NotificationsController(NotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        _notifications = notifications;
    }

    /******************************************************************
     *  Notification Config
     ******************************************************************/

    /// <summary>
    ///     Returns the current notification configuration. Secrets are never echoed
    ///     to the client; each destination carries a <c>hasSecret</c> boolean instead
    ///     so the UI can render a "secret set" indicator without exposing the value.
    /// </summary>
    [HttpGet("config")]
    public IActionResult Get()
    {
        var cfg = _notifications.GetConfig();
        var safeDestinations = cfg.Destinations.Select(d => new
        {
            d.Url,
            d.Name,
            d.Type,
            d.Enabled,
            HasSecret = !string.IsNullOrEmpty(d.Secret)
        });
        return new JsonResult(new
        {
            Destinations = safeDestinations,
            cfg.Events
        });
    }

    /// <summary>
    ///     Saves updated notification configuration and persists it to disk. When an
    ///     incoming destination omits <c>Secret</c> (null or empty) but a stored
    ///     secret exists on the matching existing destination, the stored value is
    ///     preserved — otherwise a simple round-trip through the UI would wipe it.
    ///     Matching is by <c>Url</c> (case-insensitive), which is the only stable key.
    /// </summary>
    /// <param name="config"> The new notification configuration to apply. </param>
    [HttpPost("config")]
    public IActionResult Save([FromBody] NotificationConfig config)
    {
        try
        {
            var incoming = config ?? new NotificationConfig();
            var existing = _notifications.GetConfig();

            foreach (var dest in incoming.Destinations)
            {
                if (string.IsNullOrEmpty(dest.Secret))
                {
                    var match = existing.Destinations
                        .FirstOrDefault(e => string.Equals(e.Url, dest.Url, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !string.IsNullOrEmpty(match.Secret))
                        dest.Secret = match.Secret;
                }
            }

            _notifications.SaveConfig(incoming);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Dispatches a one-shot test notification to a single destination. Temporarily
    ///     swaps the active config to isolate the destination from the user's real setup.
    /// </summary>
    /// <param name="dest"> The destination to test. </param>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] NotificationDestination dest)
    {
        if (dest == null || string.IsNullOrWhiteSpace(dest.Url))
            return BadRequest("URL required");

        var saved = _notifications.GetConfig();
        try
        {
            _notifications.SaveConfig(new NotificationConfig
            {
                Destinations = new List<NotificationDestination> { dest },
                Events = new NotificationEventToggles
                {
                    EncodeStarted   = true,
                    EncodeCompleted = true,
                    EncodeFailed    = true,
                    ScanCompleted   = true,
                    NodeOffline     = true,
                    NodeOnline      = true,
                }
            });
            await _notifications.NotifyEncodeCompletedAsync("(test notification)", null);
            return new JsonResult(new { success = true, message = "Test dispatched" });
        }
        catch (Exception ex)
        {
            return new JsonResult(new { success = false, error = ex.Message });
        }
        finally
        {
            _notifications.SaveConfig(saved);
        }
    }
}
