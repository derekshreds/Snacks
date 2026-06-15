namespace Snacks.Models;

/// <summary>
///     Represents a single transcoding job in the processing queue.
///     WorkItems are created when files are added for encoding and track the job
///     through its entire lifecycle — from queue to completion or failure.
///     In a clustered environment, WorkItems can be dispatched to remote nodes
///     for distributed processing.
/// </summary>
public sealed class WorkItem
{
    /// <summary>
    ///     Unique identifier for this work item (GUID format).
    ///     When a job is re-dispatched after a failure, the same ID is reused
    ///     so that remote nodes can resume from partial uploads.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary> Display name of the file being processed (e.g., "Movie.mkv"). </summary>
    public string FileName { get; set; } = "";

    private string _path = "";

    /// <summary> Absolute path to the source file on the master's filesystem. </summary>
    public string Path
    {
        get => _path;
        set { _path = value; _normalizedPath = null; }
    }

    private string? _normalizedPath;

    /// <summary>
    ///     <see cref="Path"/> normalized via <c>Path.GetFullPath</c>, computed once and
    ///     cached (invalidated if <see cref="Path"/> changes). Duplicate-detection scans
    ///     used to call <c>GetFullPath</c> per item per lookup — O(n²) normalizations
    ///     across a large library rescan.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string NormalizedPath =>
        _normalizedPath ??= string.IsNullOrEmpty(_path) ? "" : System.IO.Path.GetFullPath(_path);

    /// <summary> File size in bytes of the source file. </summary>
    public long Size { get; set; } = 0;

    /// <summary>
    ///     File size in bytes of the encoded output. Populated when the job completes;
    ///     <see langword="null"/> while pending/processing or when the output was discarded
    ///     before a size could be read. Surfaced in the UI to show size reduction on completed cards.
    /// </summary>
    public long? OutputSize { get; set; }

    /// <summary>
    ///     Video bitrate in kbps, calculated from file size and duration.
    ///     Used for queue prioritization — higher bitrate files are processed first
    ///     to maximize perceived throughput.
    /// </summary>
    public long Bitrate { get; set; } = 0;

    /// <summary>
    ///     User-assigned queue priority. 0 for everything by default; "move to
    ///     front" sets it above the current maximum so the item dispatches next.
    ///     Sorts before <see cref="Bitrate"/> in every queue ordering.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    ///     When the file entered the queue (the DB row's CreatedAt, stamped at
    ///     hydration). The newest-first policy's tiebreaker — kept separate from
    ///     <see cref="CreatedAt"/>, which records when THIS in-memory object was
    ///     built and feeds encode-history timing fallbacks.
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary> Duration of the video in seconds. </summary>
    public double Length { get; set; } = 0;

    /// <summary>
    ///     Set when this item was enqueued by an explicit user action ("Process Item" /
    ///     "Process Directory"). Such items are treated as <see cref="EncodingMode.Hybrid"/>
    ///     at dispatch regardless of the global encoding mode — already-at-target files get a
    ///     video-copy mux pass (audio/subs re-applied, container normalized to the configured
    ///     <see cref="EncoderOptions.Format"/>) instead of being skipped, while above-target or
    ///     wrong-codec files still re-encode. Hydrated from <see cref="MediaFile.ForceMux"/>
    ///     so the intent survives the item falling out of the in-memory working window.
    /// </summary>
    public bool ForceMux { get; set; }

    /// <summary>
    ///     Whether the source video is already encoded in HEVC/H.265.
    ///     Used to determine if re-encoding is necessary.
    /// </summary>
    public bool IsHevc { get; set; } = false;

    /// <summary> Whether the source video is 4K (any video stream wider than 1920px). </summary>
    public bool Is4K { get; set; } = false;

    /// <summary>
    ///     Distinguishes video work from music (audio-only) work. Copied from the
    ///     scanned <see cref="MediaFile.Kind"/> at queue-time. The scheduler reads
    ///     this to route the job into <c>ConvertVideoAsync</c> or <c>ConvertMusicAsync</c>
    ///     and to acquire the appropriate slot pool (per-device for video, the
    ///     dedicated music semaphore for music).
    /// </summary>
    public MediaKind Kind { get; set; } = MediaKind.Video;

    /// <summary>
    ///     Full ffprobe analysis of the source file, including stream details.
    ///     Used to build FFmpeg command lines and validate output. Populated lazily
    ///     when processing starts (every encode path re-probes when this is null)
    ///     and released once the item reaches a terminal state — at 10–30 KB per
    ///     probe, retaining it on thousands of queued/finished items was the
    ///     dominant cost of the multi-GB blowups reported on large library sweeps.
    /// </summary>
    /// <remarks>
    ///     Excluded from JSON on purpose: the queue API and SignalR broadcasts
    ///     serialize whole <see cref="WorkItem"/>s, and no client reads the probe.
    ///     Cluster dispatch sends probes via its own <c>JobMetadata.Probe</c>.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public ProbeResult? Probe { get; set; }

    private WorkItemStatus _status = WorkItemStatus.Pending;

    /// <summary>
    ///     Current lifecycle state of this work item.
    /// </summary>
    /// <remarks>
    ///     Terminal states (<see cref="WorkItemStatus.Cancelled"/>, <see cref="WorkItemStatus.Stopped"/>,
    ///     <see cref="WorkItemStatus.Completed"/>, <see cref="WorkItemStatus.Failed"/>) are <b>sticky</b>
    ///     against concurrent active-state assignments — once the user cancels (or the job completes),
    ///     any in-flight async handler that tries to flip the status back to <c>Uploading</c>,
    ///     <c>Downloading</c>, or <c>Processing</c> is ignored. This eliminates races between
    ///     <c>CancelWorkItemAsync</c> and progress-update loops or WAL transitions that were
    ///     already in flight when cancel fired. Legitimate retry/reset paths use <c>Pending</c>,
    ///     which is always accepted.
    /// </remarks>
    public WorkItemStatus Status
    {
        get => _status;
        set
        {
            if (IsTerminal(_status) && IsActive(value)) return;
            if (_status == value) return;
            _status = value;
            LastUpdatedAt = DateTime.UtcNow;
        }
    }

    private static bool IsTerminal(WorkItemStatus s) => s is WorkItemStatus.Cancelled
                                                          or WorkItemStatus.Stopped
                                                          or WorkItemStatus.Completed
                                                          or WorkItemStatus.Failed
                                                          or WorkItemStatus.NoSavings;
    private static bool IsActive(WorkItemStatus s)   => s is WorkItemStatus.Uploading
                                                          or WorkItemStatus.Downloading
                                                          or WorkItemStatus.Processing;

    private int _progress;

    /// <summary>
    ///     Encoding progress percentage (0–100).
    ///     For remote jobs, this reflects the node's reported progress.
    /// </summary>
    public int Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            LastUpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary> Human-readable error message if the job failed. </summary>
    public string? ErrorMessage { get; set; }

    /// <summary> UTC timestamp when this work item was created. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary> UTC timestamp when encoding began. Null if not yet started. </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary> UTC timestamp when encoding finished (success or failure). </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    ///     NodeId of the cluster node currently processing this job.
    ///     Null for locally processed jobs.
    /// </summary>
    public string? AssignedNodeId { get; set; }

    /// <summary> Display name of the assigned node (for UI purposes). </summary>
    public string? AssignedNodeName { get; set; }

    /// <summary>
    ///     Current phase of a remote job: "Uploading", "Encoding", or "Downloading".
    ///     Null for local jobs or when not in a remote phase.
    /// </summary>
    public string? RemoteJobPhase { get; set; }

    private int _transferProgress;

    /// <summary>
    ///     Transfer progress percentage (0–100) for uploads and downloads.
    ///     Separate from <see cref="Progress"/> which tracks encoding progress.
    /// </summary>
    public int TransferProgress
    {
        get => _transferProgress;
        set
        {
            _transferProgress = value;
            LastUpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    ///     Number of times this job has failed on remote nodes.
    ///     After 3 failures, the job is marked as permanently failed.
    /// </summary>
    public int RemoteFailureCount { get; set; }

    /// <summary>
    ///     UTC timestamp of the last activity on this work item — bumped on every status change,
    ///     progress update, transfer-progress update, log line, and remote progress packet.
    ///     Used by the stuck-item watchdog and the per-job watchdog to detect items that have
    ///     gone silent and need rescuing.
    /// </summary>
    /// <remarks> In-memory only; deliberately not persisted to the database. </remarks>
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary> Bumps <see cref="LastUpdatedAt"/> to mark the item as having recent activity. </summary>
    public void Touch() => LastUpdatedAt = DateTime.UtcNow;

    /// <summary> Convenience property — <see langword="true" /> if this job is assigned to a remote node. </summary>
    public bool IsRemote => AssignedNodeId != null;

    /// <summary>
    ///     The hardware device family (<c>"nvidia"</c>, <c>"intel"</c>,
    ///     <c>"cpu"</c>, ...) the master picked when scheduling this job.
    ///     Captured at dispatch time so the encode-history ledger and the
    ///     dashboard's per-device analytics can attribute the work even
    ///     after the slot has been released. Null until scheduled.
    /// </summary>
    public string? DispatchedDeviceId { get; set; }

    /// <summary>
    ///     Whether the encode used <c>-c:v copy</c> (mux pass or HEVC-at-target-bitrate copy).
    ///     Set by <c>ConvertVideoAsync</c> when the output is kept; null otherwise. The cluster
    ///     completion path forwards this to the master so its keep/delete recompute uses the
    ///     worker's actual flag instead of recomputing only the mux-pass branch (which would
    ///     miss the HEVC-at-target case).
    /// </summary>
    public bool? OutputUsedVideoCopy { get; set; }

    /// <summary>
    ///     Set to <see langword="true"/> by <c>ConvertVideoAsync</c> when the encoder produced an
    ///     output but the keep predicate decided to discard it (output ≥ source, not a remux,
    ///     not a configured audio-output growth case). The local <c>ProcessQueueAsync</c>
    ///     completion path reads this to decide whether to write <c>MediaFileStatus.NoSavings</c>
    ///     vs <c>MediaFileStatus.Completed</c> — without it, a discarded output would land in
    ///     Completed and look identical to a real successful encode.
    /// </summary>
    /// <remarks>In-memory only; deliberately not persisted (the DB carries the outcome via Status).</remarks>
    public bool LastEncodeProducedNoSavings { get; set; }
}

/// <summary> Lifecycle states for a <see cref="WorkItem"/>. </summary>
public enum WorkItemStatus
{
    /// <summary> Job is queued and waiting to be processed. </summary>
    Pending,

    /// <summary> Job is actively being encoded (locally or on a remote node). </summary>
    Processing,

    /// <summary> Job completed successfully — output file validated and placed. </summary>
    Completed,

    /// <summary> Job failed after all retry attempts. </summary>
    Failed,

    /// <summary>
    ///     Job was explicitly cancelled by the user.
    ///     Will NOT be reprocessed unless manually reset.
    /// </summary>
    Cancelled,

    /// <summary>
    ///     Job was stopped by the user — removed from queue but can be reprocessed later.
    /// </summary>
    Stopped,

    /// <summary> Source file is being uploaded to a remote worker node. </summary>
    Uploading,

    /// <summary> Encoded output is being downloaded from a remote worker node. </summary>
    Downloading,

    /// <summary>
    ///     Encoding finished but the output wasn't kept (no savings, not a remux, not user-configured
    ///     growth). Surfaced as a distinct queue-tile state — "No savings — encoded but didn't
    ///     shrink" — instead of overloading <see cref="Completed"/> like the prior code did, which
    ///     produced the confusing "tile says Completed but the DB says Skipped and Re-evaluate
    ///     re-queues it" experience. Mirrors <c>MediaFileStatus.NoSavings</c>.
    /// </summary>
    NoSavings
}
