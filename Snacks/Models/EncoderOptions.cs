namespace Snacks.Models;

/// <summary>
///     User-configurable encoding options for transcoding jobs.
///     These settings are applied to all files in a batch and are
///     cloned for each individual job to prevent mutation between items.
/// </summary>
public sealed class EncoderOptions
{
    /******************************************************************
     *  Core Video
     ******************************************************************/

    /// <summary> Output container format (e.g. "mkv", "mp4"). </summary>
    public string Format { get; set; } = "mkv";

    /// <summary> Logical codec name (e.g. "h265", "h264"). </summary>
    public string Codec { get; set; } = "h265";

    /// <summary> FFmpeg encoder identifier (e.g. "libx265", "h264_nvenc"). </summary>
    public string Encoder { get; set; } = "libx265";

    /// <summary> Target output bitrate in kilobits per second. </summary>
    public int TargetBitrate { get; set; } = 3500;

    /// <summary> When <see langword="true"/>, enforces the target bitrate strictly rather than using CRF/CQ mode. </summary>
    public bool StrictBitrate { get; set; } = false;

    /// <summary> Multiplier applied to <see cref="TargetBitrate"/> for 4K source material. </summary>
    public int FourKBitrateMultiplier { get; set; } = 4;

    /// <summary> When <see langword="true"/>, 4K files are skipped entirely. </summary>
    public bool Skip4K { get; set; } = false;

    /// <summary> FFmpeg quality preset string (e.g. "medium", "slow"). </summary>
    public string FfmpegQualityPreset { get; set; } = "medium";

    /******************************************************************
     *  Audio
     ******************************************************************/

    /// <summary> When <see langword="true"/>, all audio is downmixed to two channels. </summary>
    public bool TwoChannelAudio { get; set; } = false;

    /// <summary> Legacy: kept English audio only. Superseded by <see cref="AudioLanguagesToKeep"/>. </summary>
    public bool EnglishOnlyAudio { get; set; } = false;

    /// <summary> ISO 639-2 language codes for audio tracks to retain (e.g. ["en", "ja"]). </summary>
    public List<string> AudioLanguagesToKeep { get; set; } = new() { "en" };

    /// <summary> When <see langword="true"/>, always keeps the original-language audio track. </summary>
    public bool KeepOriginalLanguage { get; set; } = false;

    /// <summary> Provider used to look up the original language of a file (e.g. "None", "Sonarr"). </summary>
    public string OriginalLanguageProvider { get; set; } = "None";

    /// <summary> When <see langword="true"/>, processes audio tracks only and skips video re-encoding. </summary>
    public bool AudioOnlyMode { get; set; } = false;

    /// <summary> FFmpeg audio codec name (e.g. "aac", "ac3"). </summary>
    public string AudioCodec { get; set; } = "aac";

    /// <summary> Target audio bitrate in kilobits per second. </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /******************************************************************
     *  Subtitles
     ******************************************************************/

    /// <summary> Legacy: kept English subtitles only. Superseded by <see cref="SubtitleLanguagesToKeep"/>. </summary>
    public bool EnglishOnlySubtitles { get; set; } = false;

    /// <summary> ISO 639-2 language codes for subtitle tracks to retain. </summary>
    public List<string> SubtitleLanguagesToKeep { get; set; } = new() { "en" };

    /// <summary> When <see langword="true"/>, text subtitles are written to sidecar files instead of muxed. </summary>
    public bool ExtractSubtitlesToSidecar { get; set; } = false;

    /// <summary> Sidecar subtitle container format (e.g. "srt", "ass"). </summary>
    public string SidecarSubtitleFormat { get; set; } = "srt";

    /// <summary> When <see langword="true"/>, image-based subtitle tracks are OCR-converted to SRT. </summary>
    public bool ConvertImageSubtitlesToSrt { get; set; } = false;

    /******************************************************************
     *  Video Pipeline
     ******************************************************************/

    /// <summary> Downscale policy: "Never", "Always", or "IfLarger". </summary>
    public string DownscalePolicy { get; set; } = "Never";

    /// <summary> Target resolution for downscaling (e.g. "1080p", "720p"). </summary>
    public string DownscaleTarget { get; set; } = "1080p";

    /// <summary> When <see langword="true"/>, HDR content is tone-mapped to SDR before encoding. </summary>
    public bool TonemapHdrToSdr { get; set; } = false;

    /// <summary> When <see langword="true"/>, black border crop detection is applied before encoding. </summary>
    public bool RemoveBlackBorders { get; set; } = false;

    /******************************************************************
     *  Output and Scratch
     ******************************************************************/

    /// <summary> When <see langword="true"/>, the source file is deleted after a successful encode. </summary>
    public bool DeleteOriginalFile { get; set; } = false;

    /// <summary> When <see langword="true"/>, failed items are automatically re-queued once. </summary>
    public bool RetryOnFail { get; set; } = true;

    /// <summary> Percentage above target bitrate at which an already-efficient file is skipped. </summary>
    public int SkipPercentAboveTarget { get; set; } = 20;

    /// <summary> Optional output directory override. When <see langword="null"/>, output is written beside the source. </summary>
    public string? OutputDirectory { get; set; }

    /// <summary> Optional intermediate encode directory. When <see langword="null"/>, the system temp directory is used. </summary>
    public string? EncodeDirectory { get; set; }

    /// <summary> Hardware acceleration mode (e.g. "auto", "nvenc", "vaapi", "none"). </summary>
    public string HardwareAcceleration { get; set; } = "auto";

    /// <summary> Optional local scratch directory for cluster node temporary files. </summary>
    public string? LocalTranscodeScratchDirectory { get; set; }
}
