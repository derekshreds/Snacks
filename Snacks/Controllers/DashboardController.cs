using Microsoft.AspNetCore.Mvc;
using Snacks.Data;

namespace Snacks.Controllers;

/// <summary>
///     MVC routes for the analytics dashboard. Renders <c>/dashboard</c>
///     (the page) and serves its data over <c>/api/dashboard/*</c>.
///     The page itself is a single dark-themed HTML view that hand-rolls
///     SVG charts; this controller only supplies aggregations from the
///     <see cref="EncodeHistoryRepository"/> ledger.
/// </summary>
public sealed class DashboardController : Controller
{
    private readonly EncodeHistoryRepository _history;

    public DashboardController(EncodeHistoryRepository history)
    {
        _history = history;
    }

    /// <summary> Renders the dashboard view at <c>/dashboard</c>. </summary>
    [HttpGet("/dashboard")]
    public IActionResult Index() => View();

    /// <summary>
    ///     Lifetime totals for the hero strip. Cheap query — single GROUP BY.
    /// </summary>
    [HttpGet("/api/dashboard/summary")]
    public async Task<IActionResult> Summary()
        => Ok(await _history.GetSummaryAsync());

    /// <summary>
    ///     Daily savings rollup for the time-series chart, with empty-day
    ///     backfill so the x-axis is continuous. Defaults to the last 30
    ///     days; valid range 1–365.
    /// </summary>
    [HttpGet("/api/dashboard/savings-over-time")]
    public async Task<IActionResult> SavingsOverTime([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _history.GetSavingsOverTimeAsync(days));
    }

    /// <summary>
    ///     Per-device totals for the device utilization stripe. 30-day window
    ///     by default.
    /// </summary>
    [HttpGet("/api/dashboard/device-utilization")]
    public async Task<IActionResult> DeviceUtilization([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _history.GetDeviceUtilizationAsync(days));
    }

    /// <summary> Output codec mix donut data. </summary>
    [HttpGet("/api/dashboard/codec-mix")]
    public async Task<IActionResult> CodecMix([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _history.GetCodecMixAsync(days));
    }

    /// <summary> Per-node throughput leaderboard. </summary>
    [HttpGet("/api/dashboard/node-throughput")]
    public async Task<IActionResult> NodeThroughput([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        return Ok(await _history.GetNodeThroughputAsync(days));
    }

    /// <summary> Most recent N completed encodes for the activity table. </summary>
    [HttpGet("/api/dashboard/recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 25)
        => Ok(await _history.GetRecentAsync(limit));

    /// <summary> Top compression wins for the leaderboard. </summary>
    [HttpGet("/api/dashboard/top-savings")]
    public async Task<IActionResult> TopSavings([FromQuery] int limit = 10, [FromQuery] int days = 365)
    {
        limit = Math.Clamp(limit, 1, 100);
        days  = Math.Clamp(days, 1, 365);
        return Ok(await _history.GetTopSavingsAsync(limit, days));
    }
}
