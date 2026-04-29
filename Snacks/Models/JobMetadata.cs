namespace Snacks.Models;

/// <summary>
///     Payload sent from master to a worker node alongside a file upload.
///     Contains all metadata needed by the node to encode the file autonomously:
///     source file info, encoding options, and integrity verification data.
///
///     This replaces the two-phase commit (upload via headers → job offer) with a
///     single multipart request where the upload itself *is* the job assignment.
/// </summary>
public sealed class JobMetadata
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
    ///     Whether the source is 4K (any video stream wider than 1920px).
    ///     Determines which bitrate ceiling <c>MeetsBitrateTarget</c> uses when
    ///     deciding whether a Hybrid-mode job is eligible for a mux pass.
    /// </summary>
    public bool Is4K { get; set; }

    /// <summary>
    ///     SHA256 hash of the source file for end-to-end integrity verification.
    ///     The node computes this after upload and rejects the job if it doesn't match,
    ///     protecting against network corruption that could produce invalid output.
    /// </summary>
    public string? SourceFileHash { get; set; }

    /// <summary>
    ///     The <see cref="HardwareDevice.DeviceId"/> the master allocated this
    ///     job to (e.g. <c>"nvidia"</c>, <c>"intel"</c>, <c>"cpu"</c>). The
    ///     worker honors this by overriding <see cref="EncoderOptions.HardwareAcceleration"/>
    ///     so the encode lands on the requested slot's hardware.
    ///
    ///     <para>Null on requests from older masters that don't yet track
    ///     per-device slots; the worker falls back to its existing auto-detect
    ///     behaviour.</para>
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    ///     Effective slot capacity the master is using for the chosen device on
    ///     this worker (i.e. the user's per-node <c>MaxConcurrency</c> override
    ///     when set, otherwise the device's reported <c>DefaultConcurrency</c>).
    ///     The worker's slot pool grows to honor this so its safety-net cap
    ///     never silently rejects a job the master legitimately scheduled.
    ///
    ///     <para>Null on requests from older masters; the worker falls back to
    ///     <c>DefaultConcurrency</c> for sizing.</para>
    /// </summary>
    public int? DeviceMaxConcurrency { get; set; }
}
