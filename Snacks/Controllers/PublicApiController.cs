using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Public read-only API designed for consumption by external homelab dashboards
///     (Homarr, Glance, Dashy, …). Versioned at <c>/api/v1/</c> and modeled after the
///     Sonarr / Radarr conventions: <c>X-Api-Key</c> header or <c>?apiKey=</c> query
///     string for auth, a deterministic <c>/system/status</c> probe, and a Sonarr-style
///     <c>page / pageSize / records</c> queue envelope.
///
///     <para>This controller deliberately exposes <em>no write operations</em>. Cancel,
///     retry, and clear-failed live on the internal <c>/api/queue/*</c> surface — keeping
///     the public seam read-only sidesteps an entire class of authorization concerns.</para>
///
///     <para>Worker-node mode caveat: <c>/stats</c> and <c>/queue</c> return the
///     <em>local</em> view. Homarr clients should always be pointed at the master node
///     for cluster-wide totals. The <c>role</c> field on <c>/system/status</c> tells
///     callers which side of the wire they hit.</para>
/// </summary>
// Inherits Controller (not ControllerBase) so the iframe action can call View(...);
// every other action returns JsonResult / Json directly and is unaffected.
public sealed class PublicApiController : Controller
{
    private static readonly DateTime _processStartUtc = DateTime.UtcNow;

    private readonly TranscodingService      _transcoding;
    private readonly ClusterService          _cluster;
    private readonly EncodeHistoryRepository _history;
    private readonly MediaFileRepository     _mediaFiles;
    private readonly DashboardIntegrationService _dashboard;
    private readonly AuthService             _auth;

    public PublicApiController(
        TranscodingService transcoding,
        ClusterService cluster,
        EncodeHistoryRepository history,
        MediaFileRepository mediaFiles,
        DashboardIntegrationService dashboard,
        AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(transcoding);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(mediaFiles);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(auth);
        _transcoding = transcoding;
        _cluster     = cluster;
        _history     = history;
        _mediaFiles  = mediaFiles;
        _dashboard   = dashboard;
        _auth        = auth;
    }

    /******************************************************************
     *  System / connection test
     ******************************************************************/

    /// <summary>
    ///     Mirrors Sonarr's <c>/api/v3/system/status</c>. Used by Homarr's mandatory
    ///     connection test on integration save; also a fine human-readable
    ///     "who are you, what version" probe.
    /// </summary>
    [HttpGet("/api/v1/system/status")]
    public IActionResult Status()
    {
        var clusterConfig = _cluster.GetConfig();
        var role          = clusterConfig.Enabled ? clusterConfig.Role : "standalone";

        return new JsonResult(new
        {
            version        = ClusterDiscoveryService.ClusterVersion,
            instanceName   = string.IsNullOrWhiteSpace(clusterConfig.NodeName) ? "Snacks" : clusterConfig.NodeName,
            runtimeVersion = RuntimeInformation.FrameworkDescription,
            osName         = RuntimeInformation.OSDescription,
            role,
            nodeId         = clusterConfig.NodeId,
            startTime      = _processStartUtc,
            uptimeSec      = (long)(DateTime.UtcNow - _processStartUtc).TotalSeconds,
            isAuthEnabled  = _auth.IsAuthRequired(),
        });
    }

    /******************************************************************
     *  Stats — aggregate analytics for the dashboard widget's Statistics tab
     ******************************************************************/

    /// <summary>
    ///     Lifetime + current-queue stats normalized to the shape Homarr's
    ///     <c>MediaTranscodingIntegration</c> expects. <c>healthCheck*</c> arrays
    ///     are intentionally empty — Snacks has no "health check" concept distinct
    ///     from a normal encode. Container, audio codec, and audio container arrays
    ///     remain empty because Snacks does not currently retain container history;
    ///     video and music codec history are reported separately.
    /// </summary>
    [HttpGet("/api/v1/stats")]
    public async Task<IActionResult> Stats()
    {
        var summary  = await _history.GetSummaryAsync();
        var codecMix = await _history.GetCodecMixAsync(days: 365, MediaKind.Video);
        var audioCodecMix = await _history.GetCodecMixAsync(days: 365, MediaKind.Music);
        var counts    = await _transcoding.GetWorkItemCountsAsync();
        var totalFiles = await _mediaFiles.CountAllAsync();
        var failed    = await _mediaFiles.CountByStatusAsync(MediaFileStatus.Failed);

        return new JsonResult(new
        {
            totalFiles,
            totalTranscoded    = summary.TotalEncodes,
            totalHealthChecked = 0,
            bytesSaved         = summary.TotalBytesSaved,

            transcodeStatus = new[]
            {
                new { name = "Pending",    value = counts.Pending },
                new { name = "Processing", value = counts.Processing },
                new { name = "Completed",  value = summary.TotalEncodes },
                new { name = "Failed",     value = failed },
            },
            healthCheckStatus = Array.Empty<object>(),

            video = new
            {
                codecs = codecMix.Select(c => new
                {
                    name  = string.IsNullOrEmpty(c.Codec) ? "unknown" : c.Codec,
                    value = c.Encodes,
                }).ToArray(),
                containers  = Array.Empty<object>(),
                resolutions = new[]
                {
                    new { name = "4K",      value = summary.FourKEncodes },
                    new { name = "<=1080p", value = Math.Max(0, summary.VideoEncodes - summary.FourKEncodes) },
                },
            },

            audio = new
            {
                codecs     = audioCodecMix.Select(c => new
                {
                    name  = string.IsNullOrEmpty(c.Codec) ? "unknown" : c.Codec,
                    value = c.Encodes,
                }).ToArray(),
                containers = Array.Empty<object>(),
            },
        });
    }

    /******************************************************************
     *  Queue — paginated, Sonarr-style envelope
     ******************************************************************/

    /// <summary>
    ///     Returns the transcode queue using Sonarr's <c>page / pageSize / records</c>
    ///     envelope. Active items sort to the top, then queued, then completed,
    ///     mirroring the internal queue UI ordering.
    /// </summary>
    /// <param name="page"> 1-indexed page number. Clamped to ≥ 1. </param>
    /// <param name="pageSize"> Items per page. Clamped to 1–100. </param>
    [HttpGet("/api/v1/queue")]
    public async Task<IActionResult> Queue([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var skip = (long)(page - 1) * pageSize;
        var result = await _dashboard.GetQueueWithRecentHistoryPageAsync(skip, pageSize);

        return new JsonResult(new
        {
            page,
            pageSize,
            total = result.Total,
            records = result.Records.Select(MapQueueRecord).ToArray(),
        });
    }

    private static object MapQueueRecord(DashboardQueueItem item) => new
    {
        id                = item.Id,
        file              = item.FileName,
        sizeBytes         = item.SizeBytes,
        container         = item.Container,
        videoCodec        = item.VideoCodec,
        videoResolution   = item.VideoResolution,
        healthCheck       = (string?)null,
        transcodeDecision = item.Decision,
    };

    /******************************************************************
     *  Workers — per-node breakdown for the Workers tab
     ******************************************************************/

    /// <summary>
    ///     Lists every node the master knows about, including a synthetic "self"
    ///     entry for the local process so standalone deployments still render a
    ///     workers tab. Each worker's active-job snapshot comes from the most
    ///     recent heartbeat.
    /// </summary>
    [HttpGet("/api/v1/workers")]
    public IActionResult Workers()
    {
        var clusterConfig = _cluster.GetConfig();
        var remoteNodes   = _cluster.GetNodes().Where(node => node.NodeId != clusterConfig.NodeId);

        var selfNode = new
        {
            id      = clusterConfig.NodeId,
            name    = string.IsNullOrWhiteSpace(clusterConfig.NodeName) ? "Snacks" : clusterConfig.NodeName,
            paused  = !_cluster.GetConfig().LocalEncodingEnabled,
            workers = _cluster.GetEnrichedSelfActiveJobs().Select(MapActiveJob).ToArray(),
        };

        var nodes = new List<object> { selfNode };
        nodes.AddRange(remoteNodes.Select(n => (object)new
        {
            id      = n.NodeId,
            name    = string.IsNullOrWhiteSpace(n.Hostname) ? n.NodeId : n.Hostname,
            paused  = n.IsPaused,
            workers = n.ActiveJobs.Select(MapActiveJob).ToArray(),
        }));

        return new JsonResult(new { nodes = nodes.ToArray() });
    }

    private static object MapActiveJob(ActiveJobInfo j) => new
    {
        id                  = j.JobId,
        file                = j.FileName ?? "",
        fps                 = 0,
        percentage          = j.Progress,
        etaSeconds          = 0,
        status              = j.Phase ?? "Encoding",
        jobType             = "transcode",
        workerType          = j.DeviceId,
        originalSizeBytes   = 0L,
        estimatedSizeBytes  = 0L,
        outputSizeBytes     = 0L,
    };

    /******************************************************************
     *  Iframe page — embed-friendly HTML tile
     ******************************************************************/

    /// <summary>
    ///     Renders a compact Homarr-tile-shaped HTML page suitable for embedding via
    ///     Homarr's iframe widget. Sits at <c>/iframe/homarr</c> so the embed URL
    ///     remains human-readable. A scoped <c>?embedToken=</c> grants access to this
    ///     read-only page when login is enabled, while CSP <c>frame-ancestors</c> limits
    ///     which configured origins may embed it. Data is server-rendered into the page.
    /// </summary>
    [HttpGet("/iframe/homarr")]
    public async Task<IActionResult> HomarrIframe(
        [FromQuery] string theme   = "dark",
        [FromQuery] string tab     = "stats",
        [FromQuery] int    limit   = 10,
        [FromQuery] int    refresh = 30,
        [FromQuery] string? embedToken = null)
    {
        Response.Headers["Content-Security-Policy"] = $"frame-ancestors {_auth.GetIframeFrameAncestors()}";
        Response.Headers["Cache-Control"] = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        // We deliberately do NOT set X-Frame-Options — frame-ancestors is strictly
        // more flexible and modern browsers honor it correctly.

        var clusterConfig = _cluster.GetConfig();
        var summary       = await _history.GetSummaryAsync();
        var savingsDaily  = await _history.GetSavingsOverTimeAsync(14);
        var counts        = await _transcoding.GetWorkItemCountsAsync();
        var failed        = await _mediaFiles.CountByStatusAsync(MediaFileStatus.Failed);
        var normalizedTab   = NormalizeTab(tab);
        var clampedLimit    = Math.Clamp(limit, 1, 30);
        var clampedRefresh  = refresh <= 0 ? 0 : Math.Clamp(refresh, 10, 3600);
        var currentQueue = await _dashboard.GetCurrentQueuePageAsync(0, clampedLimit);
        var queueRecords = currentQueue.Records
            .Select(item => new HomarrIframeQueueRow
            {
                FileName  = item.FileName,
                Status    = item.Decision,
                Progress  = item.Progress,
                SizeBytes = item.SizeBytes,
            })
            .ToList();

        var workersRows = BuildIframeWorkers(clusterConfig);

        var model = new HomarrIframeModel
        {
            Theme        = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
            Tab          = normalizedTab,
            Limit        = clampedLimit,
            Refresh      = clampedRefresh,
            EmbedToken   = embedToken ?? "",
            Version      = ClusterDiscoveryService.ClusterVersion,
            InstanceName = string.IsNullOrWhiteSpace(clusterConfig.NodeName) ? "Snacks" : clusterConfig.NodeName,

            TotalFiles  = summary.TotalEncodes,
            BytesSaved  = summary.TotalBytesSaved,
            FourKCount  = summary.FourKEncodes,
            Pending     = counts.Pending,
            Processing  = counts.Processing,
            Failed      = failed,

            Queue         = queueRecords,
            Workers       = workersRows,
            SavingsSeries = savingsDaily.Select(d => d.BytesSaved).ToList(),
        };
        return View("~/Views/Homarr/Index.cshtml", model);
    }

    private List<HomarrIframeWorkerRow> BuildIframeWorkers(ClusterConfig clusterConfig)
    {
        var rows = new List<HomarrIframeWorkerRow>
        {
            new()
            {
                NodeName = string.IsNullOrWhiteSpace(clusterConfig.NodeName) ? "Snacks" : clusterConfig.NodeName,
                Paused   = !clusterConfig.LocalEncodingEnabled,
                Jobs     = _cluster.GetEnrichedSelfActiveJobs()
                    .Select(j => new HomarrIframeJobRow
                    {
                        FileName = j.FileName ?? "",
                        Device   = j.DeviceId,
                        Progress = j.Progress,
                        Phase    = j.Phase ?? "Encoding",
                    }).ToList(),
            }
        };

        // The discovery registry can contain this instance itself (loopback
        // discovery); the local row above already covers it.
        rows.AddRange(_cluster.GetNodes()
            .Where(n => n.NodeId != clusterConfig.NodeId)
            .Select(n => new HomarrIframeWorkerRow
            {
                NodeName = string.IsNullOrWhiteSpace(n.Hostname) ? n.NodeId : n.Hostname,
                Paused   = n.IsPaused,
                Jobs     = n.ActiveJobs.Select(j => new HomarrIframeJobRow
                {
                    FileName = j.FileName ?? "",
                    Device   = j.DeviceId,
                    Progress = j.Progress,
                    Phase    = j.Phase ?? "Encoding",
                }).ToList(),
            }));

        return rows;
    }

    private static string NormalizeTab(string tab) => tab?.ToLowerInvariant() switch
    {
        "queue"   => "queue",
        "workers" => "workers",
        _         => "stats",
    };
}

/// <summary> View model for <see cref="PublicApiController.HomarrIframe"/>. </summary>
public sealed class HomarrIframeModel
{
    public string Theme        { get; set; } = "dark";
    public string Tab          { get; set; } = "stats";
    public int    Limit        { get; set; } = 10;

    /// <summary> Auto-reload interval in seconds; 0 disables. Clamped to [10, 3600]. </summary>
    public int    Refresh      { get; set; } = 30;
    /// <summary>Scoped credential retained across the iframe's server-rendered tab links.</summary>
    public string EmbedToken   { get; set; } = "";
    public string Version      { get; set; } = "";
    public string InstanceName { get; set; } = "Snacks";

    public int  TotalFiles { get; set; }
    public long BytesSaved { get; set; }
    public int  FourKCount { get; set; }
    public int  Pending    { get; set; }
    public int  Processing { get; set; }
    public int  Failed     { get; set; }

    public List<HomarrIframeQueueRow>  Queue   { get; set; } = new();
    public List<HomarrIframeWorkerRow> Workers { get; set; } = new();

    /// <summary> Bytes saved per day, oldest→newest (14 entries, zero-filled), for the sparkline. </summary>
    public List<long> SavingsSeries { get; set; } = new();

    /// <summary>Builds a same-page tab link without dropping the scoped iframe credential.</summary>
    public string GetTabHref(string tab)
    {
        var href = $"?theme={Theme}&tab={tab}&limit={Limit}&refresh={Refresh}";
        if (!string.IsNullOrEmpty(EmbedToken))
            href += $"&embedToken={Uri.EscapeDataString(EmbedToken)}";
        return href;
    }
}

/// <summary> One queue row rendered in the iframe's Queue tab. </summary>
public sealed class HomarrIframeQueueRow
{
    public string FileName  { get; set; } = "";
    public string Status    { get; set; } = "";
    public int    Progress  { get; set; }
    public long   SizeBytes { get; set; }
}

/// <summary> One node card rendered in the iframe's Workers tab. </summary>
public sealed class HomarrIframeWorkerRow
{
    public string NodeName { get; set; } = "";
    public bool   Paused   { get; set; }
    public List<HomarrIframeJobRow> Jobs { get; set; } = new();
}

/// <summary> One job line rendered inside a node card in the Workers tab. </summary>
public sealed class HomarrIframeJobRow
{
    public string FileName { get; set; } = "";
    public string Device   { get; set; } = "";
    public int    Progress { get; set; }
    public string Phase    { get; set; } = "Encoding";
}
