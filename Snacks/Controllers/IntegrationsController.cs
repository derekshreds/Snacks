using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Configuration and live test-connection endpoints for third-party integrations
///     (Plex, Jellyfin, Sonarr, Radarr).
/// </summary>
[Route("api/integrations")]
[ApiController]
public sealed class IntegrationsController : ControllerBase
{
    private readonly IntegrationService _integrations;

    public IntegrationsController(IntegrationService integrations)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        _integrations = integrations;
    }

    /******************************************************************
     *  Integration Config
     ******************************************************************/

    /// <summary> Returns the current integration configuration. </summary>
    [HttpGet("config")]
    public IActionResult Get() => new JsonResult(_integrations.GetConfig());

    /// <summary> Saves updated integration configuration and persists it to disk. </summary>
    /// <param name="config"> The new integration configuration to apply. </param>
    [HttpPost("config")]
    public IActionResult Save([FromBody] IntegrationConfig config)
    {
        try
        {
            _integrations.SaveConfig(config ?? new IntegrationConfig());
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /******************************************************************
     *  Test Connections
     ******************************************************************/

    /// <summary> Tests connectivity to a Plex Media Server using the supplied credentials. </summary>
    /// <param name="req"> Base URL and token for the Plex instance. </param>
    [HttpPost("test/plex")]
    public async Task<IActionResult> TestPlex([FromBody] MediaServerIntegration req)
    {
        var (ok, msg) = await _integrations.TestPlexAsync(req?.BaseUrl ?? "", req?.Token ?? "");
        return new JsonResult(new { success = ok, message = msg });
    }

    /// <summary> Tests connectivity to a Jellyfin server using the supplied credentials. </summary>
    /// <param name="req"> Base URL and API key for the Jellyfin instance. </param>
    [HttpPost("test/jellyfin")]
    public async Task<IActionResult> TestJellyfin([FromBody] MediaServerIntegration req)
    {
        var (ok, msg) = await _integrations.TestJellyfinAsync(req?.BaseUrl ?? "", req?.Token ?? "");
        return new JsonResult(new { success = ok, message = msg });
    }

    /// <summary> Tests connectivity to a Sonarr instance using the supplied credentials. </summary>
    /// <param name="req"> Base URL and API key for the Sonarr instance. </param>
    [HttpPost("test/sonarr")]
    public async Task<IActionResult> TestSonarr([FromBody] ArrIntegration req)
    {
        var (ok, msg) = await _integrations.TestArrAsync(req?.BaseUrl ?? "", req?.ApiKey ?? "", "Sonarr");
        return new JsonResult(new { success = ok, message = msg });
    }

    /// <summary> Tests connectivity to a Radarr instance using the supplied credentials. </summary>
    /// <param name="req"> Base URL and API key for the Radarr instance. </param>
    [HttpPost("test/radarr")]
    public async Task<IActionResult> TestRadarr([FromBody] ArrIntegration req)
    {
        var (ok, msg) = await _integrations.TestArrAsync(req?.BaseUrl ?? "", req?.ApiKey ?? "", "Radarr");
        return new JsonResult(new { success = ok, message = msg });
    }

    /// <summary> Tests authentication against TheTVDB v4 with the supplied credentials. </summary>
    /// <param name="req"> API key (and optional subscriber PIN) for TVDB. </param>
    [HttpPost("test/tvdb")]
    public async Task<IActionResult> TestTvdb([FromBody] TvdbIntegration req)
    {
        var (ok, msg) = await _integrations.TestTvdbAsync(req?.ApiKey ?? "", req?.Pin);
        return new JsonResult(new { success = ok, message = msg });
    }

    /// <summary> Tests connectivity to TMDb with the supplied API key. </summary>
    /// <param name="req"> API key for TMDb. </param>
    [HttpPost("test/tmdb")]
    public async Task<IActionResult> TestTmdb([FromBody] TmdbIntegration req)
    {
        var (ok, msg) = await _integrations.TestTmdbAsync(req?.ApiKey ?? "");
        return new JsonResult(new { success = ok, message = msg });
    }
}
