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

    /// <summary>
    ///     Full ffprobe analysis of the source file, including stream details.
    ///     Used to build FFmpeg command lines and validate output.
    /// </summary>
    public ProbeResult? Probe { get; set; }

    /// <summary> Current lifecycle state of this work item. </summary>
    public WorkItemStatus Status { get; set; } = WorkItemStatus.Pending;

    /// <summary>
    ///     Encoding progress percentage (0–100).
    ///     For remote jobs, this reflects the node's reported progress.
    /// </summary>
    public int Progress { get; set; } = 0;

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

    /// <summary>
    ///     Transfer progress percentage (0–100) for uploads and downloads.
    ///     Separate from <see cref="Progress"/> which tracks encoding progress.
    /// </summary>
    public int TransferProgress { get; set; }

    /// <summary>
    ///     Number of times this job has failed on remote nodes.
    ///     After 3 failures, the job is marked as permanently failed.
    /// </summary>
    public int RemoteFailureCount { get; set; }

    /// <summary> Convenience property — <see langword="true" /> if this job is assigned to a remote node. </summary>
    public bool IsRemote => AssignedNodeId != null;
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
    Stopped
}
