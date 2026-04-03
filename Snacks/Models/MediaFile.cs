namespace Snacks.Models;

/// <summary>
///     Persistent database entity representing a scanned media file.
///     Unlike <see cref="WorkItem"/> (which is in-memory and ephemeral),
///     MediaFile records survive application restarts and provide the
///     authoritative source of truth for file status across sessions.
///
///     Used for:
///     - Tracking which files have been scanned and their encoding status
///     - Detecting file changes (size/duration delta) to trigger re-encoding
///     - Persisting remote job assignments for crash recovery
///     - Preventing duplicate processing of already-completed files
/// </summary>
public sealed class MediaFile
{
    /// <summary> Primary key — auto-incrementing integer. </summary>
    public int Id { get; set; }

    /// <summary>
    ///     Absolute, normalized path to the media file.
    ///     Indexed uniquely to prevent duplicate entries.
    /// </summary>
    public string FilePath { get; set; } = "";

    /// <summary> Parent directory path — used for batch directory lookups. </summary>
    public string Directory { get; set; } = "";

    /// <summary> File name with extension (e.g., "Movie.mkv"). </summary>
    public string FileName { get; set; } = "";

    /// <summary>
    ///     File name without extension — used for duplicate detection
    ///     within the same directory (catches format changes like .avi → .mkv).
    /// </summary>
    public string BaseName { get; set; } = "";

    /// <summary>
    ///     File size in bytes at the time of last scan.
    ///     Used for change detection — a >10% delta indicates the file was replaced.
    /// </summary>
    public long FileSize { get; set; }

    /// <summary> Video bitrate in kbps. </summary>
    public long Bitrate { get; set; }

    /// <summary> Video codec name (e.g., "h264", "hevc", "av1"). </summary>
    public string Codec { get; set; } = "";

    /// <summary> Video width in pixels. </summary>
    public int Width { get; set; }

    /// <summary> Video height in pixels. </summary>
    public int Height { get; set; }

    /// <summary>
    ///     Pixel format string (e.g., "yuv420p", "yuv420p10le").
    ///     Used to determine if VAAPI needs p010 format for 10-bit content.
    /// </summary>
    public string? PixelFormat { get; set; }

    /// <summary>
    ///     Duration in seconds, as reported by ffprobe.
    ///     Used for change detection — a >30s delta indicates the file was replaced.
    /// </summary>
    public double Duration { get; set; }

    /// <summary> Whether the video stream is HEVC/H.265 encoded. </summary>
    public bool IsHevc { get; set; }

    /// <summary> Whether the video resolution exceeds 1920px width (4K/UHD detection). </summary>
    public bool Is4K { get; set; }

    /// <summary> Current processing status of this file. </summary>
    public MediaFileStatus Status { get; set; } = MediaFileStatus.Unseen;

    /// <summary> Number of local encoding failures for this file. </summary>
    public int FailureCount { get; set; }

    /// <summary> Last error message from a failed encoding attempt (max 2048 chars). </summary>
    public string? FailureReason { get; set; }

    /// <summary> UTC timestamp of the last scan/probe operation on this file. </summary>
    public DateTime? LastScannedAt { get; set; }

    /// <summary> UTC timestamp when encoding completed successfully. </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary> UTC timestamp when this record was first created. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     File modification time (UTC ticks) at the time of last scan.
    ///     Used alongside size/duration for change detection.
    /// </summary>
    public long FileMtime { get; set; }

    /// <summary>
    ///     The <see cref="WorkItem.Id"/> GUID used when this file was dispatched as a remote job.
    ///     Preserved across restarts so nodes can resume partial uploads under the same ID.
    ///     Only cleared on successful completion (not on failure) to enable retry resume.
    /// </summary>
    public string? RemoteWorkItemId { get; set; }

    /// <summary>
    ///     NodeId of the cluster node this file is assigned to.
    ///     Null when not assigned or after completion/failure.
    /// </summary>
    public string? AssignedNodeId { get; set; }

    /// <summary> Display name of the assigned node (for UI/reporting). </summary>
    public string? AssignedNodeName { get; set; }

    /// <summary> Current phase of the remote job: "Uploading", "Encoding", or "Downloading". </summary>
    public string? RemoteJobPhase { get; set; }

    /// <summary>
    ///     Number of remote encoding failures for this file.
    ///     After 3 failures, the job is marked as permanently failed.
    /// </summary>
    public int RemoteFailureCount { get; set; }

    /// <summary>
    ///     IP address of the assigned node — used for recovery reconnection
    ///     after master restart without needing node re-discovery.
    /// </summary>
    public string? AssignedNodeIp { get; set; }

    /// <summary> HTTP port of the assigned node — used for recovery reconnection. </summary>
    public int? AssignedNodePort { get; set; }
}

/// <summary> Processing statuses for <see cref="MediaFile"/>. </summary>
public enum MediaFileStatus
{
    /// <summary> File was seen during a scan but not yet queued for encoding. </summary>
    Unseen,

    /// <summary> File is in the encoding queue, waiting to be processed. </summary>
    Queued,

    /// <summary> File is currently being encoded (locally or remotely). </summary>
    Processing,

    /// <summary> Encoding completed successfully — output validated and placed. </summary>
    Completed,

    /// <summary> Encoding failed after all retry attempts. </summary>
    Failed,

    /// <summary> File was intentionally skipped (already target codec, 4K skip, etc.). </summary>
    Skipped,

    /// <summary> User cancelled encoding for this file. </summary>
    Cancelled
}
