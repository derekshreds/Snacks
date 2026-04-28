namespace Snacks.Models;

/// <summary>
///     Single-file dry-run prediction returned by the directory analyze endpoint. Mirrors what
///     <see cref="Snacks.Services.TranscodingService.AddFileAsync"/> would do under the supplied
///     <see cref="EncoderOptions"/>, without writing to the DB or queueing any work.
/// </summary>
public sealed class FileAnalysisResult
{
    /// <summary> Absolute file path on disk. </summary>
    public string FilePath { get; set; } = "";

    /// <summary> File name component of <see cref="FilePath"/> for display. </summary>
    public string FileName { get; set; } = "";

    /// <summary> File size in bytes. </summary>
    public long SizeBytes { get; set; }

    /// <summary> Source video codec (e.g. <c>h264</c>, <c>hevc</c>, <c>av1</c>). </summary>
    public string Codec { get; set; } = "";

    /// <summary> Source video bitrate in kbps (total when probe-only, video-only when DB-cached). </summary>
    public long BitrateKbps { get; set; }

    /// <summary> Source video width in pixels. </summary>
    public int Width { get; set; }

    /// <summary> Source video height in pixels. </summary>
    public int Height { get; set; }

    /// <summary> Source video duration in seconds. </summary>
    public double Duration { get; set; }

    /// <summary> Whether the source qualifies as 4K (width &gt; 1920). </summary>
    public bool Is4K { get; set; }

    /// <summary>
    ///     Predicted decision label. One of:
    ///     <c>Queue</c>, <c>Shrink</c>, <c>Copy</c>, <c>Mux</c>, <c>Skip</c>, <c>Excluded</c>,
    ///     <c>AlreadyCompleted</c>, <c>AlreadyFailed</c>, <c>AlreadyCancelled</c>,
    ///     <c>AlreadySkipped</c>, <c>Error</c>.
    /// </summary>
    public string Decision { get; set; } = "";

    /// <summary> Human-readable explanation surfaced in the UI tooltip. </summary>
    public string Reason { get; set; } = "";

    /// <summary>
    ///     For <c>Queue</c>/<c>Shrink</c>/<c>Copy</c>: the predicted encode target in kbps
    ///     (0 for <c>Copy</c> since no re-encode happens). Unset for skip/done/error rows.
    /// </summary>
    public int EncodeTargetKbps { get; set; }

    /// <summary>
    ///     <see langword="true"/> when the file sits within the borderline window above the
    ///     skip ceiling — the real run's video-only bitrate remeasurement could flip the
    ///     Queue/Skip decision once audio/subtitles are subtracted.
    /// </summary>
    public bool Borderline { get; set; }
}
