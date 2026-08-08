using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Prometheus-format metrics at <c>/metrics</c> (text exposition format 0.0.4).
///     A months-long library conversion is an operations problem — queue depth,
///     encode throughput, bytes saved, and health/verification coverage belong in
///     Grafana, not a browser tab. Several of the health/verification counts are
///     unindexed whole-table scans, so the rendered body is cached for 15 seconds:
///     aggressive scrape intervals (and the endpoint being auth-exempt) cost at
///     most one recompute per window regardless of how often it's hit.
/// </summary>
[ApiController]
public sealed class MetricsController : ControllerBase
{
    /// <summary> Cached rendered body + when it was built (Interlocked-swapped tuple). </summary>
    private static (string Body, DateTime At)? _cache;
    private static readonly SemaphoreSlim _renderLock = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);
    private readonly TranscodingService      _transcodingService;
    private readonly MediaFileRepository     _mediaFileRepo;
    private readonly EncodeHistoryRepository _encodeHistoryRepo;
    private readonly AutoScanService         _autoScanService;

    public MetricsController(
        TranscodingService transcodingService,
        MediaFileRepository mediaFileRepo,
        EncodeHistoryRepository encodeHistoryRepo,
        AutoScanService autoScanService)
    {
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        ArgumentNullException.ThrowIfNull(encodeHistoryRepo);
        ArgumentNullException.ThrowIfNull(autoScanService);
        _transcodingService = transcodingService;
        _mediaFileRepo      = mediaFileRepo;
        _encodeHistoryRepo  = encodeHistoryRepo;
        _autoScanService    = autoScanService;
    }

    [HttpGet("/metrics")]
    public async Task<IActionResult> Get()
    {
        if (_cache is { } hit && DateTime.UtcNow - hit.At < CacheTtl)
            return Content(hit.Body, "text/plain; version=0.0.4; charset=utf-8");

        await _renderLock.WaitAsync();
        try
        {
            if (_cache is { } hit2 && DateTime.UtcNow - hit2.At < CacheTtl)
                return Content(hit2.Body, "text/plain; version=0.0.4; charset=utf-8");

            var body = await RenderAsync();
            _cache = (body, DateTime.UtcNow);
            return Content(body, "text/plain; version=0.0.4; charset=utf-8");
        }
        finally
        {
            _renderLock.Release();
        }
    }

    private async Task<string> RenderAsync()
    {
        var sb = new StringBuilder(4096);

        // Queue
        var (pending, processing, completedRecent, failedRecent, _) =
            await _transcodingService.GetWorkItemCountsAsync();
        Gauge(sb, "snacks_queue_pending", "Files waiting in the pending queue.", pending);
        Gauge(sb, "snacks_queue_processing", "Files actively encoding or transferring.", processing);
        Gauge(sb, "snacks_queue_completed_recent", "Recently completed items still in memory.", completedRecent);
        Gauge(sb, "snacks_queue_failed_recent", "Recently failed items still in memory.", failedRecent);

        // Lifetime encode ledger
        var summary = await _encodeHistoryRepo.GetSummaryAsync();
        Counter(sb, "snacks_encodes_total", "Completed encodes recorded in the history ledger.", summary.TotalEncodes);
        Counter(sb, "snacks_bytes_saved_total", "Total bytes saved across all recorded encodes.", summary.TotalBytesSaved);
        Counter(sb, "snacks_encode_seconds_total", "Total wall-clock seconds spent encoding.", summary.TotalEncodeSeconds);
        Counter(sb, "snacks_content_seconds_total", "Total seconds of media content encoded.", summary.TotalContentSeconds);

        // Per-node throughput (30-day window — labels stay bounded)
        var nodes = await _encodeHistoryRepo.GetNodeThroughputAsync(30);
        sb.AppendLine("# HELP snacks_node_bytes_saved_30d Bytes saved per node over the last 30 days.");
        sb.AppendLine("# TYPE snacks_node_bytes_saved_30d gauge");
        foreach (var n in nodes)
        {
            var label = !string.IsNullOrEmpty(n.Hostname) ? n.Hostname
                      : !string.IsNullOrEmpty(n.NodeId)   ? n.NodeId
                      : "unknown";
            sb.AppendLine($"snacks_node_bytes_saved_30d{{node=\"{Escape(label)}\"}} {Num(n.BytesSaved)}");
        }

        // Library composition + health
        var (noAudio, noVideo, noDuration, failedFiles, verifyFailed, totalIssues) =
            await _mediaFileRepo.GetHealthSummaryAsync();
        Gauge(sb, "snacks_health_issues_total", "Files flagged with any file-level health issue.", totalIssues);
        Gauge(sb, "snacks_health_no_audio", "Video files with zero audio tracks.", noAudio);
        Gauge(sb, "snacks_health_no_video", "Video files with no decodable video stream.", noVideo);
        Gauge(sb, "snacks_health_no_duration", "Files with zero/unknown duration.", noDuration);
        Gauge(sb, "snacks_health_failed_encodes", "Files whose encode permanently failed.", failedFiles);
        Gauge(sb, "snacks_health_verify_failed", "Files failing rolling deep-verification.", verifyFailed);

        var (verified, _, totalScanned) = await _mediaFileRepo.GetVerificationStatsAsync();
        Gauge(sb, "snacks_library_files", "Scanned files known to the library.", totalScanned);
        Gauge(sb, "snacks_verify_covered", "Files deep-verified at least once.", verified);

        // Scan
        var scan = _autoScanService.GetConfig();
        Gauge(sb, "snacks_scan_last_new_files", "Files queued by the most recent completed scan.", scan.LastScanNewFiles);
        if (scan.LastScanTime is { } lastScan)
            Gauge(sb, "snacks_scan_last_completed_timestamp_seconds",
                "Unix time of the most recent completed scan.",
                new DateTimeOffset(lastScan).ToUnixTimeSeconds());

        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string name, string help, double value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} gauge");
        sb.AppendLine($"{name} {Num(value)}");
    }

    private static void Counter(StringBuilder sb, string name, string help, double value)
    {
        sb.AppendLine($"# HELP {name} {help}");
        sb.AppendLine($"# TYPE {name} counter");
        sb.AppendLine($"{name} {Num(value)}");
    }

    private static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string label) =>
        label.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
}
