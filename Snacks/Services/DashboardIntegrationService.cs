using Snacks.Data;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Database-backed read model shared by the public Snacks API, the Homarr iframe,
///     and the Tdarr-compatible API consumed by Homarr's media-transcoding widget.
///     Pending rows come from SQLite (the authoritative queue); only active and recent
///     terminal items come from the bounded in-memory registry.
/// </summary>
public sealed class DashboardIntegrationService
{
    private readonly TranscodingService _transcoding;
    private readonly MediaFileRepository _mediaFiles;

    public DashboardIntegrationService(
        TranscodingService transcoding,
        MediaFileRepository mediaFiles)
    {
        ArgumentNullException.ThrowIfNull(transcoding);
        ArgumentNullException.ThrowIfNull(mediaFiles);
        _transcoding = transcoding;
        _mediaFiles  = mediaFiles;
    }

    /// <summary>Current active + pending queue, without terminal history.</summary>
    public Task<DashboardQueuePage> GetCurrentQueuePageAsync(long skip, int take) =>
        GetQueuePageAsync(skip, take, includeTerminal: false);

    /// <summary>Active + pending queue followed by the recent in-memory terminal history.</summary>
    public Task<DashboardQueuePage> GetQueueWithRecentHistoryPageAsync(long skip, int take) =>
        GetQueuePageAsync(skip, take, includeTerminal: true);

    private async Task<DashboardQueuePage> GetQueuePageAsync(long skip, int take, bool includeTerminal)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 0, 100);

        var memory = _transcoding.GetAllWorkItems();
        var active = memory
            .Where(item => IsActive(item.Status))
            .OrderBy(item => ActivePriority(item.Status))
            .ThenByDescending(item => item.Bitrate)
            .ThenBy(item => item.CreatedAt)
            .ToList();
        var terminal = includeTerminal
            ? memory
                .Where(item => item.Status != WorkItemStatus.Pending && !IsActive(item.Status))
                .OrderBy(item => TerminalPriority(item.Status))
                .ThenByDescending(item => item.CompletedAt ?? item.LastUpdatedAt)
                .ToList()
            : [];

        var activePaths = active
            .Select(item => item.NormalizedPath)
            .Where(path => path.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var records = new List<DashboardQueueItem>(take);
        if (take > 0 && skip < active.Count)
        {
            records.AddRange(active
                .Skip((int)skip)
                .Take(take)
                .Select(FromWorkItem));
        }

        var pendingSkipLong = Math.Max(0, skip - active.Count);
        var pendingSkip = pendingSkipLong > int.MaxValue ? int.MaxValue : (int)pendingSkipLong;
        var pendingTake = Math.Max(0, take - records.Count);
        var (pendingRows, pendingTotal) = await _mediaFiles.GetQueuedPageAsync(
            pendingSkip,
            pendingTake,
            _transcoding.QueueNewestFirst,
            activePaths);

        if (pendingTake > 0)
            records.AddRange(pendingRows.Select(FromMediaFile));

        if (includeTerminal && records.Count < take)
        {
            var terminalSkipLong = Math.Max(0, skip - active.Count - pendingTotal);
            if (terminalSkipLong < terminal.Count)
            {
                records.AddRange(terminal
                    .Skip((int)terminalSkipLong)
                    .Take(take - records.Count)
                    .Select(FromWorkItem));
            }
        }

        var total = active.Count + pendingTotal + (includeTerminal ? terminal.Count : 0);
        return new DashboardQueuePage(records, total);
    }

    internal static DashboardQueueItem FromWorkItem(WorkItem item)
    {
        var probeVideo = item.Probe?.Streams.FirstOrDefault(stream => stream.CodecType == "video");
        var width  = probeVideo?.Width  ?? item.SourceWidth;
        var height = probeVideo?.Height ?? item.SourceHeight;
        var codec  = probeVideo?.CodecName ?? item.SourceCodec;
        return new DashboardQueueItem
        {
            Id              = item.Id,
            FileName        = item.FileName,
            FilePath        = item.Path,
            SizeBytes       = item.Size,
            Container       = ContainerFromPath(item.Path),
            VideoCodec      = NormalizeSourceCodec(codec, item.IsHevc),
            VideoResolution = FormatResolution(width, height, item.Is4K),
            Decision        = MapDecision(item.Status),
            Progress        = item.Progress,
        };
    }

    internal static DashboardQueueItem FromMediaFile(MediaFile file) => new()
    {
        Id              = $"mf-{file.Id}",
        FileName        = file.FileName,
        FilePath        = file.FilePath,
        SizeBytes       = file.FileSize,
        Container       = ContainerFromPath(file.FilePath),
        VideoCodec      = NormalizeSourceCodec(file.Codec, file.IsHevc),
        VideoResolution = FormatResolution(file.Width, file.Height, file.Is4K),
        Decision        = "Queued",
        Progress        = 0,
    };

    internal static string NormalizeSourceCodec(string? sourceCodec, bool isHevc)
    {
        var normalized = VideoSourceFacts.NormalizeCodec(sourceCodec);
        if (normalized.Length > 0) return normalized;
        return isHevc ? "h265" : "unknown";
    }

    internal static string FormatResolution(int width, int height, bool is4K)
    {
        if (is4K) return "4K";
        var shortEdge = width > 0 && height > 0 ? Math.Min(width, height) : Math.Max(width, height);
        return shortEdge switch
        {
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1080 => "1080p",
            >= 720  => "720p",
            > 0     => "SD",
            _       => "Unknown",
        };
    }

    internal static string MapDecision(WorkItemStatus status) => status switch
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
        _                          => status.ToString(),
    };

    private static string ContainerFromPath(string path) =>
        Path.GetExtension(path)?.TrimStart('.').ToLowerInvariant() ?? "";

    private static bool IsActive(WorkItemStatus status) => status is
        WorkItemStatus.Processing or WorkItemStatus.Uploading or WorkItemStatus.Downloading;

    private static int ActivePriority(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Processing  => 0,
        WorkItemStatus.Uploading   => 1,
        WorkItemStatus.Downloading => 2,
        _                          => 3,
    };

    private static int TerminalPriority(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Completed => 0,
        WorkItemStatus.NoSavings => 1,
        WorkItemStatus.Failed    => 2,
        WorkItemStatus.Cancelled => 3,
        WorkItemStatus.Stopped   => 3,
        _                        => 4,
    };
}

public sealed record DashboardQueuePage(IReadOnlyList<DashboardQueueItem> Records, int Total);

public sealed class DashboardQueueItem
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Container { get; init; } = "";
    public string VideoCodec { get; init; } = "unknown";
    public string VideoResolution { get; init; } = "Unknown";
    public string Decision { get; init; } = "Queued";
    public int Progress { get; init; }
}
