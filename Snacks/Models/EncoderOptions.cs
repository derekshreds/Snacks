using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     Which non-video operations are eligible for a video-copy "mux pass".
///     Off disables the feature. Audio/Subtitles gate one stream type;
///     Both covers either. Drives skip-bypass and video-copy decisions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MuxMode
{
    Off,
    Audio,
    Subtitles,
    Both,
}

/// <summary>
///     Whether a configured <see cref="MuxMode"/> only triggers for files that
///     already meet bitrate targets (<c>TargetMatchOnly</c>) or applies
///     universally so every file gets a video-copy pass (<c>AllFiles</c>).
///     <c>TargetMatchOnly</c> is the typical choice; <c>AllFiles</c> is for
///     workflows that always want a remux regardless of source bitrate.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MuxScope
{
    TargetMatchOnly,
    AllFiles,
}

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

    /// <summary> ISO 639-1 2-letter codes for audio tracks to retain (e.g. ["en", "ja"]). Empty keeps all tracks. </summary>
    public List<string> AudioLanguagesToKeep { get; set; } = new() { "en" };

    /// <summary> When <see langword="true"/>, always keeps the original-language audio track. </summary>
    public bool KeepOriginalLanguage { get; set; } = false;

    /// <summary> Provider used to look up the original language of a file (e.g. "None", "Sonarr"). </summary>
    public string OriginalLanguageProvider { get; set; } = "None";

    /// <summary> FFmpeg audio codec name (e.g. "copy", "aac", "ac3"). </summary>
    public string AudioCodec { get; set; } = "copy";

    /// <summary> Target audio bitrate in kilobits per second. </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /******************************************************************
     *  Mux Pass
     ******************************************************************/

    /// <summary>
    ///     Selects which non-video operations warrant a video-copy "mux pass".
    ///     <see cref="MuxMode.Off"/> disables the feature entirely — files already
    ///     at or below target bitrate are skipped even when audio/subtitle settings
    ///     differ.
    /// </summary>
    public MuxMode MuxMode { get; set; } = MuxMode.Off;

    /// <summary>
    ///     Scope of <see cref="MuxMode"/>. <see cref="MuxScope.TargetMatchOnly"/>
    ///     limits the mux pass to files that already meet bitrate targets;
    ///     <see cref="MuxScope.AllFiles"/> forces video-copy on every file.
    /// </summary>
    public MuxScope MuxScope { get; set; } = MuxScope.TargetMatchOnly;

    /******************************************************************
     *  Subtitles
     ******************************************************************/

    /// <summary> ISO 639-1 2-letter codes for subtitle tracks to retain. Empty keeps all tracks. </summary>
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

    /******************************************************************
     *  Cloning
     ******************************************************************/

    /// <summary>
    ///     Deep copy of this options instance. The two language lists are
    ///     cloned so per-item mutation can't bleed back into shared state.
    /// </summary>
    public EncoderOptions Clone() => new()
    {
        Format                     = Format,
        Codec                      = Codec,
        Encoder                    = Encoder,
        TargetBitrate              = TargetBitrate,
        StrictBitrate              = StrictBitrate,
        FourKBitrateMultiplier     = FourKBitrateMultiplier,
        Skip4K                     = Skip4K,
        FfmpegQualityPreset        = FfmpegQualityPreset,
        TwoChannelAudio            = TwoChannelAudio,
        AudioLanguagesToKeep       = new List<string>(AudioLanguagesToKeep),
        KeepOriginalLanguage       = KeepOriginalLanguage,
        OriginalLanguageProvider   = OriginalLanguageProvider,
        AudioCodec                 = AudioCodec,
        AudioBitrateKbps           = AudioBitrateKbps,
        MuxMode                    = MuxMode,
        MuxScope                   = MuxScope,
        SubtitleLanguagesToKeep    = new List<string>(SubtitleLanguagesToKeep),
        ExtractSubtitlesToSidecar  = ExtractSubtitlesToSidecar,
        SidecarSubtitleFormat      = SidecarSubtitleFormat,
        ConvertImageSubtitlesToSrt = ConvertImageSubtitlesToSrt,
        DownscalePolicy            = DownscalePolicy,
        DownscaleTarget            = DownscaleTarget,
        TonemapHdrToSdr            = TonemapHdrToSdr,
        RemoveBlackBorders         = RemoveBlackBorders,
        DeleteOriginalFile         = DeleteOriginalFile,
        RetryOnFail                = RetryOnFail,
        SkipPercentAboveTarget     = SkipPercentAboveTarget,
        OutputDirectory            = OutputDirectory,
        EncodeDirectory            = EncodeDirectory,
        HardwareAcceleration       = HardwareAcceleration,
    };
}
