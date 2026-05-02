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

    /// <summary> Absolute path to the source file on the master's filesystem. </summary>
    public string Path { get; set; } = "";

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

    /// <summary> Duration of the video in seconds. </summary>
    public double Length { get; set; } = 0;

    /// <summary>
    ///     Whether the source video is already encoded in HEVC/H.265.
    ///     Used to determine if re-encoding is necessary.
    /// </summary>
    public bool IsHevc { get; set; } = false;

    /// <summary> Whether the source video is 4K (any video stream wider than 1920px). </summary>
    public bool Is4K { get; set; } = false;

    /// <summary>
    ///     Full ffprobe analysis of the source file, including stream details.
    ///     Used to build FFmpeg command lines and validate output.
    /// </summary>
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
