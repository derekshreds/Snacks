using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Renders the single-page MVC view and exposes a couple of app-lifecycle endpoints
///     (health, restart). All JSON data APIs live on dedicated attribute-routed controllers
///     under <c>/api/</c>.
/// </summary>
public sealed class HomeController : Controller
{
    private readonly TranscodingService _transcodingService;

    public HomeController(TranscodingService transcodingService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        _transcodingService = transcodingService;
    }

    /******************************************************************
     *  View Actions
     ******************************************************************/

    /// <summary>
    ///     Renders the main application view. The queue itself is loaded by the
    ///     frontend through the paginated <c>/api/queue/items</c> endpoint — the
    ///     view doesn't declare a model, so materializing the full work-item list
    ///     here was pure allocation (hundreds of MB per page load on big sweeps).
    /// </summary>
    public IActionResult Index() => View();

    /// <summary> Renders the error view. </summary>
    public IActionResult Error() => View();

    /******************************************************************
     *  App Lifecycle
     ******************************************************************/

    /// <summary> Returns a JSON liveness response indicating the application is running. </summary>
    [HttpGet("api/health")]
    public IActionResult Health() => Json(new
    {
        status    = "healthy",
        timestamp = DateTime.UtcNow,
        version   = "2.13.1",
    });

    /// <summary>
    ///     Stops the active encode, cleans up partial output, then exits the process.
    ///     In Electron mode, the host detects the clean exit and relaunches the application.
    /// </summary>
    [HttpPost("api/restart")]
    public IActionResult Restart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(500); // allow HTTP response to complete before exiting
            await _transcodingService.StopAndClearQueue();
            Environment.Exit(0);
        });
        return Json(new { success = true, message = "Restarting..." });
    }
}
