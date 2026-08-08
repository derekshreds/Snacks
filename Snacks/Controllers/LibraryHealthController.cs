using Microsoft.AspNetCore.Mvc;

namespace Snacks.Controllers;

/// <summary>
///     Hosts the <c>/library-health</c> page — file-level issues across the
///     scanned library (files without audio, no decodable video, zero duration,
///     failed encodes) plus on-demand deep verification. Pure view route; data
///     comes from <see cref="LibraryController"/>'s <c>/api/library/health</c>.
/// </summary>
public sealed class LibraryHealthController : Controller
{
    [HttpGet("/library-health")]
    public IActionResult Index() => View();
}
