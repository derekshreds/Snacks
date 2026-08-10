using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Read-only compatibility surface for Homarr's Tdarr media-transcoding adapter.
///     This is intentionally the small subset Homarr consumes, not a general Tdarr API.
/// </summary>
[ApiController]
public sealed class TdarrCompatibilityController : ControllerBase
{
    private readonly DashboardIntegrationService _dashboard;
    private readonly TranscodingService _transcoding;
    private readonly ClusterService _cluster;
    private readonly EncodeHistoryRepository _history;
    private readonly MediaFileRepository _mediaFiles;

    public TdarrCompatibilityController(
        DashboardIntegrationService dashboard,
        TranscodingService transcoding,
        ClusterService cluster,
        EncodeHistoryRepository history,
        MediaFileRepository mediaFiles)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(transcoding);
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(mediaFiles);
        _dashboard   = dashboard;
        _transcoding = transcoding;
        _cluster     = cluster;
        _history     = history;
        _mediaFiles  = mediaFiles;
    }

    /// <summary>Connection probe used when a Tdarr integration is saved in Homarr.</summary>
    [HttpPost("/api/v2/is-server-alive")]
    public IActionResult IsServerAlive() => Ok(new
    {
        isServerAlive = true,
        product       = "Snacks",
        version       = ClusterDiscoveryService.ClusterVersion,
    });

    /// <summary>Returns the exact statistics envelope parsed by Homarr's Tdarr adapter.</summary>
    [HttpPost("/api/v2/stats/get-pies")]
    public async Task<ActionResult<TdarrStatisticsResponse>> GetPies()
    {
        var summary   = await _history.GetSummaryAsync();
        var codecMix  = await _history.GetCodecMixAsync(days: 365, MediaKind.Video);
        var audioCodecMix = await _history.GetCodecMixAsync(days: 365, MediaKind.Music);
        var counts    = await _transcoding.GetWorkItemCountsAsync();
        var totalFiles = await _mediaFiles.CountAllAsync();
        var failed    = await _mediaFiles.CountByStatusAsync(MediaFileStatus.Failed);

        var successful = Math.Max(0, summary.TotalEncodes - summary.NoSavingsEncodes);
        return Ok(new TdarrStatisticsResponse
        {
            PieStats = new TdarrPieStats
            {
                TotalFiles            = totalFiles,
                TotalTranscodeCount   = summary.TotalEncodes,
                SizeDiff              = summary.TotalBytesSaved / 1_000_000_000d,
                TotalHealthCheckCount = 0,
                Status = new TdarrStatusGroups
                {
                    Transcode = NonZeroSegments(
                        new("Transcode success", successful),
                        new("No savings", summary.NoSavingsEncodes),
                        new("Queued", counts.Pending),
                        new("Transcoding", counts.Processing),
                        new("Transcode error", failed)),
                    Healthcheck = [],
                },
                Video = new TdarrVideoStatistics
                {
                    Codecs = codecMix
                        .Select(codec => new TdarrPieSegment(
                            string.IsNullOrWhiteSpace(codec.Codec) ? "unknown" : codec.Codec,
                            codec.Encodes))
                        .ToList(),
                    Containers = [],
                    Resolutions = NonZeroSegments(
                        new("4K", summary.FourKEncodes),
                        new("Non-4K", Math.Max(0, summary.VideoEncodes - summary.FourKEncodes))),
                },
                Audio = new TdarrAudioStatistics
                {
                    Codecs = audioCodecMix
                        .Select(codec => new TdarrPieSegment(
                            string.IsNullOrWhiteSpace(codec.Codec) ? "unknown" : codec.Codec,
                            codec.Encodes))
                        .ToList(),
                    Containers = [],
                },
            },
        });
    }

    /// <summary>Returns local and cluster jobs using Tdarr's node/worker dictionary shape.</summary>
    [HttpGet("/api/v2/get-nodes")]
    public ActionResult<Dictionary<string, TdarrNode>> GetNodes()
    {
        var config = _cluster.GetConfig();
        var localId = string.IsNullOrWhiteSpace(config.NodeId) ? "snacks" : config.NodeId;
        var nodes = new Dictionary<string, TdarrNode>(StringComparer.Ordinal)
        {
            [localId] = ToTdarrNode(
                localId,
                string.IsNullOrWhiteSpace(config.NodeName) ? "Snacks" : config.NodeName,
                !config.LocalEncodingEnabled,
                _cluster.GetEnrichedSelfActiveJobs()),
        };

        // Cluster discovery may include the local process. The synthetic local row above
        // is authoritative and prevents Homarr from rendering it twice.
        foreach (var node in _cluster.GetNodes().Where(node => node.NodeId != config.NodeId))
        {
            var nodeId = string.IsNullOrWhiteSpace(node.NodeId) ? Guid.NewGuid().ToString("N") : node.NodeId;
            nodes[nodeId] = ToTdarrNode(
                nodeId,
                string.IsNullOrWhiteSpace(node.Hostname) ? nodeId : node.Hostname,
                node.IsPaused || node.OffSchedule,
                node.ActiveJobs);
        }

        return Ok(nodes);
    }

    /// <summary>
    ///     Returns Tdarr table1 (active + pending transcodes). Snacks has no distinct
    ///     health-check queue, so table4 is a valid empty page.
    /// </summary>
    [HttpPost("/api/v2/client/status-tables")]
    public async Task<ActionResult<TdarrStatusTableResponse>> GetStatusTable(
        [FromBody] TdarrStatusTableRequest? request)
    {
        var data = request?.Data;
        if (!string.Equals(data?.Opts?.Table, "table1", StringComparison.Ordinal))
            return Ok(new TdarrStatusTableResponse());

        // The table check above proves data is present; keep that invariant explicit
        // for nullable analysis and for future changes to the request contract.
        data ??= new TdarrStatusTableRequestData();
        var start = Math.Max(0, data.Start);
        var pageSize = Math.Clamp(data.PageSize, 1, 100);
        var page = await _dashboard.GetCurrentQueuePageAsync(start, pageSize);
        return Ok(new TdarrStatusTableResponse
        {
            Array = page.Records.Select(ToTdarrStatusRow).ToList(),
            TotalCount = page.Total,
        });
    }

    internal static TdarrNode ToTdarrNode(
        string nodeId,
        string nodeName,
        bool paused,
        IEnumerable<ActiveJobInfo> jobs)
    {
        var workers = new Dictionary<string, TdarrWorker>(StringComparer.Ordinal);
        foreach (var job in jobs)
        {
            var workerId = string.IsNullOrWhiteSpace(job.JobId)
                ? $"{nodeId}-worker-{workers.Count + 1}"
                : job.JobId;
            workers[workerId] = new TdarrWorker
            {
                Id                       = workerId,
                File                     = job.FileName ?? "",
                Fps                      = 0,
                Percentage               = job.Progress,
                Eta                      = "0:00:00",
                Job                      = new TdarrWorkerJob { Type = "transcode" },
                Status                   = job.Phase ?? "Encoding",
                LastPluginDetails        = new TdarrPluginDetails { Number = job.Phase ?? "Encoding" },
                OriginalFileSizeInGbytes = 0,
                EstimatedSize            = 0,
                OutputFileSizeInGbytes   = 0,
                WorkerType               = string.IsNullOrWhiteSpace(job.DeviceId) ? "cpu" : job.DeviceId,
            };
        }

        return new TdarrNode
        {
            Id = nodeId,
            NodeName = nodeName,
            NodePaused = paused,
            Workers = workers,
        };
    }

    internal static TdarrStatusTableRow ToTdarrStatusRow(DashboardQueueItem item) => new()
    {
        Id                     = item.Id,
        HealthCheck            = "",
        TranscodeDecisionMaker = item.Decision,
        File                   = item.FilePath,
        FileSize               = item.SizeBytes / 1_000_000d,
        Container              = item.Container,
        VideoCodecName         = item.VideoCodec,
        VideoResolution        = item.VideoResolution,
    };

    internal static List<TdarrPieSegment> NonZeroSegments(params TdarrPieSegment[] segments) =>
        segments.Where(segment => segment.Value > 0).ToList();
}

public sealed class TdarrStatisticsResponse
{
    [JsonPropertyName("pieStats")]
    public TdarrPieStats PieStats { get; init; } = new();
}

public sealed class TdarrPieStats
{
    [JsonPropertyName("totalFiles")]
    public int TotalFiles { get; init; }

    [JsonPropertyName("totalTranscodeCount")]
    public int TotalTranscodeCount { get; init; }

    [JsonPropertyName("sizeDiff")]
    public double SizeDiff { get; init; }

    [JsonPropertyName("totalHealthCheckCount")]
    public int TotalHealthCheckCount { get; init; }

    [JsonPropertyName("status")]
    public TdarrStatusGroups Status { get; init; } = new();

    [JsonPropertyName("video")]
    public TdarrVideoStatistics Video { get; init; } = new();

    [JsonPropertyName("audio")]
    public TdarrAudioStatistics Audio { get; init; } = new();
}

public sealed class TdarrStatusGroups
{
    [JsonPropertyName("transcode")]
    public List<TdarrPieSegment> Transcode { get; init; } = [];

    [JsonPropertyName("healthcheck")]
    public List<TdarrPieSegment> Healthcheck { get; init; } = [];
}

public sealed class TdarrVideoStatistics
{
    [JsonPropertyName("codecs")]
    public List<TdarrPieSegment> Codecs { get; init; } = [];

    [JsonPropertyName("containers")]
    public List<TdarrPieSegment> Containers { get; init; } = [];

    [JsonPropertyName("resolutions")]
    public List<TdarrPieSegment> Resolutions { get; init; } = [];
}

public sealed class TdarrAudioStatistics
{
    [JsonPropertyName("codecs")]
    public List<TdarrPieSegment> Codecs { get; init; } = [];

    [JsonPropertyName("containers")]
    public List<TdarrPieSegment> Containers { get; init; } = [];
}

public sealed record TdarrPieSegment(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] int Value);

public sealed class TdarrNode
{
    [JsonPropertyName("_id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("nodeName")]
    public string NodeName { get; init; } = "";

    [JsonPropertyName("nodePaused")]
    public bool NodePaused { get; init; }

    [JsonPropertyName("workers")]
    public Dictionary<string, TdarrWorker> Workers { get; init; } = [];
}

public sealed class TdarrWorker
{
    [JsonPropertyName("_id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("fps")]
    public double Fps { get; init; }

    [JsonPropertyName("percentage")]
    public double Percentage { get; init; }

    [JsonPropertyName("ETA")]
    public string Eta { get; init; } = "0:00:00";

    [JsonPropertyName("job")]
    public TdarrWorkerJob Job { get; init; } = new();

    [JsonPropertyName("status")]
    public string Status { get; init; } = "Encoding";

    [JsonPropertyName("lastPluginDetails")]
    public TdarrPluginDetails LastPluginDetails { get; init; } = new();

    [JsonPropertyName("originalfileSizeInGbytes")]
    public double OriginalFileSizeInGbytes { get; init; }

    [JsonPropertyName("estSize")]
    public double EstimatedSize { get; init; }

    [JsonPropertyName("outputFileSizeInGbytes")]
    public double OutputFileSizeInGbytes { get; init; }

    [JsonPropertyName("workerType")]
    public string WorkerType { get; init; } = "cpu";
}

public sealed class TdarrWorkerJob
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "transcode";
}

public sealed class TdarrPluginDetails
{
    [JsonPropertyName("number")]
    public string Number { get; init; } = "";
}

public sealed class TdarrStatusTableRequest
{
    [JsonPropertyName("data")]
    public TdarrStatusTableRequestData? Data { get; init; }
}

public sealed class TdarrStatusTableRequestData
{
    [JsonPropertyName("start")]
    public int Start { get; init; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; init; } = 10;

    [JsonPropertyName("opts")]
    public TdarrStatusTableOptions? Opts { get; init; }
}

public sealed class TdarrStatusTableOptions
{
    [JsonPropertyName("table")]
    public string Table { get; init; } = "";
}

public sealed class TdarrStatusTableResponse
{
    [JsonPropertyName("array")]
    public List<TdarrStatusTableRow> Array { get; init; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; init; }
}

public sealed class TdarrStatusTableRow
{
    [JsonPropertyName("_id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("HealthCheck")]
    public string HealthCheck { get; init; } = "";

    [JsonPropertyName("TranscodeDecisionMaker")]
    public string TranscodeDecisionMaker { get; init; } = "";

    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("file_size")]
    public double FileSize { get; init; }

    [JsonPropertyName("container")]
    public string Container { get; init; } = "";

    [JsonPropertyName("video_codec_name")]
    public string VideoCodecName { get; init; } = "unknown";

    [JsonPropertyName("video_resolution")]
    public string VideoResolution { get; init; } = "Unknown";
}
