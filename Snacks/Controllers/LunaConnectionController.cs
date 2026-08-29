using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

public sealed record ConnectLunaRequest(
    string BaseUrl,
    string Email,
    string Password,
    string? DeviceName);

[Route("api/integrations/luna")]
[ApiController]
public sealed class LunaConnectionController(LunaConnectionService connection) : ControllerBase
{
    [HttpGet("status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult Status() => Ok(connection.GetStatus());

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectLunaRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(await connection.ConnectAsync(
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

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken ct)
    {
        await connection.DisconnectAsync(ct);
        return Ok(connection.GetStatus());
    }
}
