using Microsoft.AspNetCore.Mvc;

namespace Snacks.Controllers;

/// <summary>
///     Renders the single-page MVC view and exposes a couple of app-lifecycle endpoints
///     (health, restart). All JSON data APIs live on dedicated attribute-routed controllers
///     under <c>/api/</c>.
/// </summary>
public sealed class HomeController : Controller
{
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

}
