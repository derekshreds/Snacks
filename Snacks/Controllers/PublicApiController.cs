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
    private readonly AuthService             _auth;

    public PublicApiController(
        TranscodingService transcoding,
        ClusterService cluster,
        EncodeHistoryRepository history,
        AuthService auth)
    {
        ArgumentNullException.ThrowIfNull(transcoding);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(auth);
        _transcoding = transcoding;
        _cluster     = cluster;
        _history     = history;
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
    ///     are empty until Snacks's encode-history schema is extended to track them.
    /// </summary>
    [HttpGet("/api/v1/stats")]
    public async Task<IActionResult> Stats()
    {
        var summary  = await _history.GetSummaryAsync();
        var codecMix = await _history.GetCodecMixAsync(days: 365);

        var workItems  = _transcoding.GetAllWorkItems();
        var pending    = workItems.Count(w => w.Status == WorkItemStatus.Pending);
        var processing = workItems.Count(w => w.Status is WorkItemStatus.Processing
                                                or WorkItemStatus.Uploading or WorkItemStatus.Downloading);
        var failed     = workItems.Count(w => w.Status == WorkItemStatus.Failed);

        return new JsonResult(new
        {
            totalFiles         = summary.TotalEncodes,
            totalTranscoded    = summary.TotalEncodes,
            totalHealthChecked = 0,
            bytesSaved         = summary.TotalBytesSaved,

            transcodeStatus = new[]
            {
                new { name = "Pending",    value = pending },
                new { name = "Processing", value = processing },
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
                    new { name = "<=1080p", value = Math.Max(0, summary.TotalEncodes - summary.FourKEncodes) },
                },
            },

            audio = new
            {
                codecs     = Array.Empty<object>(),
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
    public IActionResult Queue([FromQuery] int page = 1, [FromQuery] int pageSize = 10)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var all = _transcoding.GetAllWorkItems();
        all.Sort((a, b) =>
        {
            int Priority(WorkItem w) => w.Status switch
            {
                WorkItemStatus.Processing  => 0,
                WorkItemStatus.Uploading   => 0,
                WorkItemStatus.Downloading => 0,
                WorkItemStatus.Pending     => 1,
                WorkItemStatus.Completed   => 2,
                WorkItemStatus.NoSavings   => 2,
                WorkItemStatus.Failed      => 3,
                WorkItemStatus.Cancelled   => 4,
                WorkItemStatus.Stopped     => 4,
                _                          => 5,
            };
            var cmp = Priority(a).CompareTo(Priority(b));
            return cmp != 0 ? cmp : b.Bitrate.CompareTo(a.Bitrate);
        });

        var total = all.Count;
        var skip  = (page - 1) * pageSize;
        var slice = all.Skip(skip).Take(pageSize);

        return new JsonResult(new
        {
            page,
            pageSize,
            total,
            records = slice.Select(MapQueueRecord).ToArray(),
        });
    }

    private static object MapQueueRecord(WorkItem w)
    {
        var ext = Path.GetExtension(w.Path)?.TrimStart('.').ToLowerInvariant() ?? "";
        return new
        {
            id                = w.Id,
            file              = w.FileName,
            sizeBytes         = w.Size,
            container         = ext,
            videoCodec        = w.IsHevc ? "hevc" : "h264",
            videoResolution   = w.Is4K   ? "4K"   : "<=1080p",
            healthCheck       = (string?)null,
            transcodeDecision = MapDecision(w.Status),
        };
    }

    private static string MapDecision(WorkItemStatus s) => s switch
    {
        WorkItemStatus.Pending     => "Queued",
        WorkItemStatus.Processing  => "Processing",
        WorkItemStatus.Uploading   => "Processing",
        WorkItemStatus.Downloading => "Processing",
        WorkItemStatus.Completed   => "Completed",
        WorkItemStatus.NoSavings   => "No Savings",
        WorkItemStatus.Failed      => "Failed",
        WorkItemStatus.Cancelled   => "Cancelled",
        WorkItemStatus.Stopped     => "Stopped",
        _                          => s.ToString(),
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
        var remoteNodes   = _cluster.GetNodes();

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
    ///     remains human-readable. Allowlist is enforced via CSP <c>frame-ancestors</c>.
    ///     Data is server-rendered into the page — the iframe itself bypasses auth
    ///     (browsers can't send <c>X-Api-Key</c> on a frame load), but follow-up
    ///     <c>fetch()</c> calls from the iframe JS would hit the API-key gate, so we
    ///     bake the snapshot in.
    /// </summary>
    [HttpGet("/iframe/homarr")]
    public async Task<IActionResult> HomarrIframe(
        [FromQuery] string theme = "dark",
        [FromQuery] string tab   = "stats",
        [FromQuery] int    limit = 10)
    {
        Response.Headers["Content-Security-Policy"] = $"frame-ancestors {_auth.GetIframeFrameAncestors()}";
        // We deliberately do NOT set X-Frame-Options — frame-ancestors is strictly
        // more flexible and modern browsers honor it correctly.

        var clusterConfig = _cluster.GetConfig();
        var summary       = await _history.GetSummaryAsync();
        var workItems     = _transcoding.GetAllWorkItems();
        var normalizedTab = NormalizeTab(tab);
        var clampedLimit  = Math.Clamp(limit, 1, 30);

        var pending    = workItems.Count(w => w.Status == WorkItemStatus.Pending);
        var processing = workItems.Count(w => w.Status is WorkItemStatus.Processing
                                                or WorkItemStatus.Uploading or WorkItemStatus.Downloading);
        var failed     = workItems.Count(w => w.Status == WorkItemStatus.Failed);

        var queueRecords = workItems
            .OrderBy(w => w.Status switch
            {
                WorkItemStatus.Processing  => 0,
                WorkItemStatus.Uploading   => 0,
                WorkItemStatus.Downloading => 0,
                WorkItemStatus.Pending     => 1,
                _                          => 2,
            })
            .ThenByDescending(w => w.Bitrate)
            .Take(clampedLimit)
            .Select(w => new HomarrIframeQueueRow
            {
                FileName  = w.FileName,
                Status    = MapDecision(w.Status),
                Progress  = w.Progress,
                SizeBytes = w.Size,
            })
            .ToList();

        var workersRows = BuildIframeWorkers(clusterConfig);

        var model = new HomarrIframeModel
        {
            Theme        = string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark",
            Tab          = normalizedTab,
            Limit        = clampedLimit,
            Version      = ClusterDiscoveryService.ClusterVersion,
            InstanceName = string.IsNullOrWhiteSpace(clusterConfig.NodeName) ? "Snacks" : clusterConfig.NodeName,

            TotalFiles  = summary.TotalEncodes,
            BytesSaved  = summary.TotalBytesSaved,
            FourKCount  = summary.FourKEncodes,
            Pending     = pending,
            Processing  = processing,
            Failed      = failed,

            Queue   = queueRecords,
            Workers = workersRows,
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

        rows.AddRange(_cluster.GetNodes().Select(n => new HomarrIframeWorkerRow
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
