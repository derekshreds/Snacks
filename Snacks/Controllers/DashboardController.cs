using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     MVC routes for the analytics dashboard. Renders <c>/dashboard</c>
///     (the page) and serves its data over <c>/api/dashboard/*</c>.
///     The page itself is a single dark-themed HTML view that hand-rolls
///     SVG charts; this controller only supplies aggregations.
///
///     <para>On masters and standalone instances, aggregations come from
///     the local <see cref="EncodeHistoryRepository"/>. On workers attached
///     to a cluster, the local ledger is empty by design — every completed
///     encode is recorded on the master only — so the worker proxies each
///     request to the master's <c>/api/cluster/dashboard/*</c> mirror over
///     the cluster shared-secret channel and streams the response back
///     verbatim. The dashboard's frontend never has to know which side it's
///     talking to.</para>
/// </summary>
public sealed class DashboardController : Controller
{
    private readonly EncodeHistoryRepository      _history;
    private readonly ClusterService               _cluster;
    private readonly IHubContext<TranscodingHub>  _hub;

    public DashboardController(
        EncodeHistoryRepository history,
        ClusterService cluster,
        IHubContext<TranscodingHub> hub)
    {
        _history = history;
        _cluster = cluster;
        _hub     = hub;
    }

    /// <summary> Renders the dashboard view at <c>/dashboard</c>. </summary>
    [HttpGet("/dashboard")]
    public IActionResult Index() => View();

    /// <summary> Lifetime totals for the hero strip. Cheap query — single GROUP BY. </summary>
    [HttpGet("/api/dashboard/summary")]
    public async Task<IActionResult> Summary()
    {
        if (_cluster.IsNodeMode)
            return await ProxyAsync("dashboard/summary");
        return Ok(await _history.GetSummaryAsync());
    }

    /// <summary>
    ///     Daily savings rollup for the time-series chart, with empty-day
    ///     backfill so the x-axis is continuous. Defaults to the last 30
    ///     days; valid range 1–365.
    /// </summary>
    [HttpGet("/api/dashboard/savings-over-time")]
    public async Task<IActionResult> SavingsOverTime([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/savings-over-time?days={days}");
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
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/device-utilization?days={days}");
        return Ok(await _history.GetDeviceUtilizationAsync(days));
    }

    /// <summary> Output codec mix donut data. </summary>
    [HttpGet("/api/dashboard/codec-mix")]
    public async Task<IActionResult> CodecMix([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/codec-mix?days={days}");
        return Ok(await _history.GetCodecMixAsync(days));
    }

    /// <summary> Per-node throughput leaderboard. </summary>
    [HttpGet("/api/dashboard/node-throughput")]
    public async Task<IActionResult> NodeThroughput([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/node-throughput?days={days}");
        return Ok(await _history.GetNodeThroughputAsync(days));
    }

    /// <summary> Most recent N completed encodes for the activity table. </summary>
    [HttpGet("/api/dashboard/recent")]
    public async Task<IActionResult> Recent([FromQuery] int limit = 25)
    {
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/recent?limit={limit}");
        return Ok(await _history.GetRecentAsync(limit));
    }

    /// <summary> Top compression wins for the leaderboard. </summary>
    [HttpGet("/api/dashboard/top-savings")]
    public async Task<IActionResult> TopSavings([FromQuery] int limit = 10, [FromQuery] int days = 365)
    {
        limit = Math.Clamp(limit, 1, 100);
        days  = Math.Clamp(days, 1, 365);
        if (_cluster.IsNodeMode)
            return await ProxyAsync($"dashboard/top-savings?limit={limit}&days={days}");
        return Ok(await _history.GetTopSavingsAsync(limit, days));
    }

    /// <summary>
    ///     Wipes the encode-history ledger after the user explicitly confirms
    ///     in the Advanced settings panel. On a worker, the request is
    ///     proxied to the master (the worker's own ledger is empty by design);
    ///     on a master/standalone the deletion runs locally and a SignalR
    ///     <c>EncodeHistoryCleared</c> broadcast tells every connected client
    ///     to refresh its dashboard view.
    /// </summary>
    [HttpDelete("/api/dashboard/history")]
    public async Task<IActionResult> ClearHistory()
    {
        if (_cluster.IsNodeMode)
            return await ProxyDeleteAsync("dashboard/history");

        var deleted = await _history.ClearAllAsync();
        await _hub.Clients.All.SendAsync("EncodeHistoryCleared");
        return Ok(new { success = true, deleted });
    }

    /// <summary>
    ///     Worker-side proxy. Forwards a dashboard request to the master's
    ///     <c>/api/cluster/dashboard/*</c> mirror using the cluster shared
    ///     secret and streams the JSON response back to the calling browser.
    ///     Falls back to an empty payload (200) when the master is not yet
    ///     reachable so the dashboard renders zeroes instead of erroring.
    /// </summary>
    private async Task<IActionResult> ProxyAsync(string clusterPath)
    {
        var masterUrl = _cluster.ResolveMasterUrl();
        if (string.IsNullOrEmpty(masterUrl))
        {
            // No master yet — return an empty array so the chart renderer
            // can still draw an empty state instead of throwing.
            return Content("[]", "application/json");
        }

        try
        {
            var client = _cluster.CreateClusterClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.GetAsync($"{masterUrl}/api/cluster/{clusterPath}");
            var body  = await resp.Content.ReadAsStringAsync();
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            Response.StatusCode = (int)resp.StatusCode;
            return Content(body, ctype);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Dashboard: proxy to master failed for /api/cluster/{clusterPath}: {ex.Message}");
            return Content("[]", "application/json");
        }
    }

    /// <summary>
    ///     DELETE counterpart of <see cref="ProxyAsync"/>. Used to forward the
    ///     "clear dashboard data" request from a worker to the master.
    /// </summary>
    private async Task<IActionResult> ProxyDeleteAsync(string clusterPath)
    {
        var masterUrl = _cluster.ResolveMasterUrl();
        if (string.IsNullOrEmpty(masterUrl))
            return StatusCode(503, new { success = false, error = "No master reachable" });

        try
        {
            var client = _cluster.CreateClusterClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await client.DeleteAsync($"{masterUrl}/api/cluster/{clusterPath}");
            var body  = await resp.Content.ReadAsStringAsync();
            var ctype = resp.Content.Headers.ContentType?.ToString() ?? "application/json";
            Response.StatusCode = (int)resp.StatusCode;
            return Content(body, ctype);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Dashboard: proxy DELETE to master failed for /api/cluster/{clusterPath}: {ex.Message}");
            return StatusCode(502, new { success = false, error = ex.Message });
        }
    }
}
