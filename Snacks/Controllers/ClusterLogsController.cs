using Microsoft.AspNetCore.Mvc;

namespace Snacks.Controllers;

/// <summary>
///     Hosts the <c>/cluster-logs</c> page — a live tail of any node's
///     operations log with a download-as-zip button. Pure view route; the
///     data is served by <see cref="DiagnosticsController"/>, which already
///     handles the local-vs-remote dispatch.
/// </summary>
public sealed class ClusterLogsController : Controller
{
    [HttpGet("/cluster-logs")]
    public IActionResult Index() => View();
}
