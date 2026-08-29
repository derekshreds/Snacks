using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary> Credentials for a one-time Luna sign-in; the password is never persisted. </summary>
/// <param name="BaseUrl"> The Luna service root (the official URL outside local testing). </param>
/// <param name="Email"> The Luna account email. </param>
/// <param name="Password"> The Luna account password; forwarded once, never stored. </param>
/// <param name="DeviceName"> Optional connector display name; defaults to the machine name. </param>
public sealed record ConnectLunaRequest(
    string BaseUrl,
    string Email,
    string Password,
    string? DeviceName);

/// <summary>
///     Connection lifecycle endpoints for the outbound Luna connector. Responses
///     carry only safe status metadata — access and refresh tokens never cross
///     this boundary.
/// </summary>
[Route("api/integrations/luna")]
[ApiController]
public sealed class LunaConnectionController : ControllerBase
{
    private readonly LunaConnectionService _connection;

    public LunaConnectionController(LunaConnectionService connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
    }

    /// <summary> Returns live connection health and the advertised capability list. </summary>
    [HttpGet("status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status() => Ok(_connection.GetStatus());

    /// <summary> Exchanges credentials once for a daemon-scoped Luna session. </summary>
    /// <param name="request"> Luna URL, email, password, and optional device name. </param>
    /// <param name="ct"> Cancels the sign-in round-trips when the client disconnects. </param>
    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectLunaRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await _connection.ConnectAsync(
                request.BaseUrl,
                request.Email,
                request.Password,
                request.DeviceName,
                ct));
        }
        catch (LunaConnectionException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "Snacks could not reach Luna." });
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, new { error = "The Luna connection timed out." });
        }
    }

    /// <summary> Revokes the scoped Luna session and removes its persisted credentials. </summary>
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        await _connection.DisconnectAsync(ct);
        return Ok(_connection.GetStatus());
    }
}
