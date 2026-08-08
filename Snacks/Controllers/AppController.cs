using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>Application liveness and host lifecycle operations.</summary>
[ApiController]
public sealed class AppController : ControllerBase
{
    private readonly TranscodingService _transcodingService;

    public AppController(TranscodingService transcodingService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        _transcodingService = transcodingService;
    }

    /// <summary>Returns a JSON liveness response indicating the application is running.</summary>
    [HttpGet("api/health")]
    public IActionResult Health() => Ok(new
    {
        status = "healthy",
        timestamp = DateTime.UtcNow,
        version = AppVersion.Current,
    });

    /// <summary>
    ///     Stops active encoding, clears the process queue, and exits so the
    ///     desktop or container host can restart Snacks.
    /// </summary>
    [HttpPost("api/restart")]
    public IActionResult Restart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500);
            await _transcodingService.StopAndClearQueue();
            Environment.Exit(0);
        });

        return Ok(new { success = true, message = "Restarting..." });
    }
}
