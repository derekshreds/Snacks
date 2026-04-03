namespace Snacks.Models;

/// <summary>
///     Payload sent from master to a worker node to assign a transcoding job.
///     Contains all metadata needed by the node to encode the file:
///     source file info, encoding options, and integrity verification data.
/// </summary>
public sealed class JobAssignment
{
    /// <summary> Unique job identifier — matches the <see cref="WorkItem.Id"/> on the master. </summary>
    public string JobId { get; set; } = "";

    /// <summary> Display name of the source file (e.g., "Movie.mkv"). </summary>
    public string FileName { get; set; } = "";

    /// <summary> Expected file size in bytes — used to verify upload integrity. </summary>
    public long FileSize { get; set; }

    /// <summary>
    ///     Encoding options to apply during transcoding.
    ///     Cloned from the master's current settings at dispatch time.
    /// </summary>
    public EncoderOptions Options { get; set; } = new();

    /// <summary>
    ///     Full ffprobe analysis of the source file.
    ///     Passed to the node so it doesn't need to re-probe.
    /// </summary>
    public ProbeResult? Probe { get; set; }

    /// <summary> Duration in seconds — used for progress calculation. </summary>
    public double Duration { get; set; }

    /// <summary> Source video bitrate in kbps. </summary>
    public long Bitrate { get; set; }

    /// <summary> Whether the source is HEVC/H.265 encoded. </summary>
    public bool IsHevc { get; set; }

    /// <summary>
    ///     SHA256 hash of the source file for end-to-end integrity verification.
    ///     The node computes this after upload and rejects the job if it doesn't match,
    ///     protecting against network corruption that could produce invalid output.
    /// </summary>
    public string? SourceFileHash { get; set; }
}

/// <summary> Progress update sent from a worker node to the master during encoding. </summary>
public sealed class JobProgress
{
    /// <summary> Job identifier — matches the <see cref="JobAssignment.JobId"/>. </summary>
    public string JobId { get; set; } = "";

    /// <summary> Encoding progress percentage (0–100). </summary>
    public int Progress { get; set; }

    /// <summary>
    ///     Optional log line from FFmpeg output — forwarded to the master's UI.
    ///     Multiple lines may be batched and sent together.
    /// </summary>
    public string? LogLine { get; set; }

    /// <summary> Current job phase: "Uploading", "Encoding", or "Downloading". </summary>
    public string? Phase { get; set; }
}

/// <summary> Completion notification sent from a worker node to the master after encoding finishes. </summary>
public sealed class JobCompletion
{
    /// <summary> Job identifier — matches the <see cref="JobAssignment.JobId"/>. </summary>
    public string JobId { get; set; } = "";

    /// <summary> Whether encoding succeeded (true) or failed (false). </summary>
    public bool Success { get; set; }

    /// <summary> Output file size in bytes — reported by the node for validation. </summary>
    public long OutputFileSize { get; set; }

    /// <summary> Error message if encoding failed. </summary>
    public string? ErrorMessage { get; set; }

    /// <summary> Name of the output file (e.g., "Movie [snacks].mkv"). </summary>
    public string OutputFileName { get; set; } = "";
}
