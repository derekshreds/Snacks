namespace Snacks.Models;

/// <summary>
///     User-configurable encoding options for transcoding jobs.
///     These settings are applied to all files in a batch and are
///     cloned for each individual job to prevent mutation between items.
/// </summary>
public sealed class EncoderOptions
{
    /// <summary>
    ///     Output container format: "mkv" or "mp4".
    ///     Default: "mkv" (better subtitle support, fewer muxing issues).
    /// </summary>
    public string Format { get; set; } = "mkv";

    /// <summary>
    ///     Target video codec family: "h265", "h264", or "av1".
    ///     Used for skip detection (already target codec).
    /// </summary>
    public string Codec { get; set; } = "h265";

    /// <summary>
    ///     Specific FFmpeg encoder name (e.g., "libx265", "hevc_vaapi", "av1_nvenc").
    ///     May be overridden at runtime by hardware acceleration detection.
    /// </summary>
    public string Encoder { get; set; } = "libx265";

    /// <summary> Target video bitrate in kbps. Default: 3500 kbps (good quality for 1080p). </summary>
    public int TargetBitrate { get; set; } = 3500;

    /// <summary>
    ///     Whether to enforce exact target bitrate (no min/max range).
    ///     When false, a bitrate range is used for better quality.
    /// </summary>
    public bool StrictBitrate { get; set; } = false;

    /// <summary>
    ///     Multiplier applied to target bitrate for 4K content.
    ///     Range: 2–8x. Default: 4x (14,000 kbps for 4K).
    /// </summary>
    public int FourKBitrateMultiplier { get; set; } = 4;

    /// <summary>
    ///     Whether to skip 4K videos entirely.
    ///     Useful when the target hardware can't handle 4K encoding efficiently.
    /// </summary>
    public bool Skip4K { get; set; } = false;

    /// <summary>
    ///     Whether to downmix audio to 2-channel stereo.
    ///     When false, original channel layout is preserved.
    /// </summary>
    public bool TwoChannelAudio { get; set; } = false;

    /// <summary>
    ///     Whether to keep only English audio tracks.
    ///     When false, all audio tracks are preserved.
    /// </summary>
    public bool EnglishOnlyAudio { get; set; } = false;

    /// <summary>
    ///     Whether to keep only English subtitle tracks.
    ///     When false, all subtitle tracks are preserved.
    /// </summary>
    public bool EnglishOnlySubtitles { get; set; } = false;

    /// <summary>
    ///     Whether to delete the original source file after successful encoding.
    ///     When true, the transcoded file replaces the original.
    ///     Default: false (keep both files).
    /// </summary>
    public bool DeleteOriginalFile { get; set; } = false;

    /// <summary>
    ///     Whether to detect and crop black borders.
    ///     Adds a crop filter to the FFmpeg command.
    /// </summary>
    public bool RemoveBlackBorders { get; set; } = false;

    /// <summary>
    ///     Whether to retry with software encoding if hardware encoding fails.
    ///     Default: true (recommended for reliability).
    /// </summary>
    public bool RetryOnFail { get; set; } = true;

    /// <summary>
    ///     Directory to place output files.
    ///     If set, transcoded files are moved here after encoding.
    /// </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    ///     Temporary directory for encoding work files.
    ///     If set, encoding happens here and files are moved to OutputDirectory after.
    /// </summary>
    public string? EncodeDirectory { get; set; }

    /// <summary>
    ///     Hardware acceleration mode: "auto", "intel", "amd", "nvidia", or "none".
    ///     "auto" detects available hardware at runtime.
    /// </summary>
    public string HardwareAcceleration { get; set; } = "auto";

    /// <summary>
    ///     Skip files already in the target codec if their bitrate is within this
    ///     percentage above the target. Default: 20 (skip if within 20% above target).
    ///     Set to 0 to only skip files at or below target. Set to 100 to skip files
    ///     up to 2x target.
    /// </summary>
    public int SkipPercentAboveTarget { get; set; } = 20;
}
