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

    /// <summary> Returns the current notification configuration. </summary>
    [HttpGet("config")]
    public IActionResult Get() => new JsonResult(_notifications.GetConfig());

    /// <summary> Saves updated notification configuration and persists it to disk. </summary>
    /// <param name="config"> The new notification configuration to apply. </param>
    [HttpPost("config")]
    public IActionResult Save([FromBody] NotificationConfig config)
    {
        try
        {
            _notifications.SaveConfig(config ?? new NotificationConfig());
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
