using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Master-only HTTP surface for <see cref="NetworkingSettings"/>. The
///     UI's Networking tab calls these endpoints to load and save transfer
///     concurrency / bandwidth caps.
/// </summary>
[Route("api/networking")]
[ApiController]
public sealed class NetworkingController : ControllerBase
{
    private readonly NetworkingSettingsService _settings;

    public NetworkingController(NetworkingSettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary> Returns the current networking settings. </summary>
    [HttpGet]
    public IActionResult Get() => new JsonResult(_settings.Get());

    /// <summary> Persists new networking settings. Validation errors return 400. </summary>
    [HttpPost]
    public IActionResult Save([FromBody] NetworkingSettings value)
    {
        if (value == null) return BadRequest(new { error = "Body required" });
        try
        {
            _settings.Save(value);
            return Ok(new { saved = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Networking: save failed: {ex.Message}");
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
