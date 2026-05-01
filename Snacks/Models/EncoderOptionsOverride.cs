namespace Snacks.Models;

/// <summary>
///     Nullable overlay for <see cref="EncoderOptions"/>.
///     Any non-null property overrides the corresponding value in the base options.
///     Used for per-folder and per-node encoding settings.
/// </summary>
public sealed class EncoderOptionsOverride
{
    /******************************************************************
     *  Core Video
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.Format"/> when non-<see langword="null"/>. </summary>
    public string? Format { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.Codec"/> when non-<see langword="null"/>. </summary>
    public string? Codec { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.Encoder"/> when non-<see langword="null"/>. </summary>
    public string? Encoder { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.TargetBitrate"/> when non-<see langword="null"/>. </summary>
    public int? TargetBitrate { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.StrictBitrate"/> when non-<see langword="null"/>. </summary>
    public bool? StrictBitrate { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.FourKBitrateMultiplier"/> when non-<see langword="null"/>. </summary>
    public int? FourKBitrateMultiplier { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.Skip4K"/> when non-<see langword="null"/>. </summary>
    public bool? Skip4K { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.HardwareAcceleration"/> when non-<see langword="null"/>. </summary>
    public string? HardwareAcceleration { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.SkipPercentAboveTarget"/> when non-<see langword="null"/>. </summary>
    public int? SkipPercentAboveTarget { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.FfmpegQualityPreset"/> when non-<see langword="null"/>. </summary>
    public string? FfmpegQualityPreset { get; set; }

    /******************************************************************
     *  Audio
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.TwoChannelAudio"/> when non-<see langword="null"/>. </summary>
    public bool? TwoChannelAudio { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.AudioLanguagesToKeep"/> when non-<see langword="null"/>. </summary>
    public List<string>? AudioLanguagesToKeep { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.KeepOriginalLanguage"/> when non-<see langword="null"/>. </summary>
    public bool? KeepOriginalLanguage { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.OriginalLanguageProvider"/> when non-<see langword="null"/>. </summary>
    public string? OriginalLanguageProvider { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.AudioCodec"/> when non-<see langword="null"/>. </summary>
    public string? AudioCodec { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.AudioBitrateKbps"/> when non-<see langword="null"/>. </summary>
    public int? AudioBitrateKbps { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.PreserveOriginalAudio"/> when non-<see langword="null"/>. </summary>
    public bool? PreserveOriginalAudio { get; set; }

    /// <summary>
    ///     Overrides <see cref="EncoderOptions.AudioOutputs"/> when non-<see langword="null"/>.
    ///     A non-null value fully replaces the base list (no merging).
    /// </summary>
    public List<AudioOutputProfile>? AudioOutputs { get; set; }

    /******************************************************************
     *  Encoding Mode
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.EncodingMode"/> when non-<see langword="null"/>. </summary>
    public EncodingMode? EncodingMode { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.MuxStreams"/> when non-<see langword="null"/>. </summary>
    public MuxStreams? MuxStreams { get; set; }

    /******************************************************************
     *  Subtitles
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.SubtitleLanguagesToKeep"/> when non-<see langword="null"/>. </summary>
    public List<string>? SubtitleLanguagesToKeep { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.ExtractSubtitlesToSidecar"/> when non-<see langword="null"/>. </summary>
    public bool? ExtractSubtitlesToSidecar { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.SidecarSubtitleFormat"/> when non-<see langword="null"/>. </summary>
    public string? SidecarSubtitleFormat { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.ConvertImageSubtitlesToSrt"/> when non-<see langword="null"/>. </summary>
    public bool? ConvertImageSubtitlesToSrt { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.PassThroughImageSubtitlesMkv"/> when non-<see langword="null"/>. </summary>
    public bool? PassThroughImageSubtitlesMkv { get; set; }

    /******************************************************************
     *  Video Pipeline
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.DownscalePolicy"/> when non-<see langword="null"/>. </summary>
    public string? DownscalePolicy { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.DownscaleTarget"/> when non-<see langword="null"/>. </summary>
    public string? DownscaleTarget { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.TonemapHdrToSdr"/> when non-<see langword="null"/>. </summary>
    public bool? TonemapHdrToSdr { get; set; }

    /******************************************************************
     *  Output and Scratch
     ******************************************************************/

    /// <summary> Overrides <see cref="EncoderOptions.DeleteOriginalFile"/> when non-<see langword="null"/>. </summary>
    public bool? DeleteOriginalFile { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.RemoveBlackBorders"/> when non-<see langword="null"/>. </summary>
    public bool? RemoveBlackBorders { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.RetryOnFail"/> when non-<see langword="null"/>. </summary>
    public bool? RetryOnFail { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.OutputDirectory"/> when non-<see langword="null"/>. </summary>
    public string? OutputDirectory { get; set; }

    /// <summary> Overrides <see cref="EncoderOptions.EncodeDirectory"/> when non-<see langword="null"/>. </summary>
    public string? EncodeDirectory { get; set; }

    /******************************************************************
     *  Override Application
     ******************************************************************/

    /// <summary>
    ///     Produces a new <see cref="EncoderOptions"/> instance by cloning
    ///     <paramref name="baseOptions"/> then layering <paramref name="folderOverride"/>
    ///     and <paramref name="nodeOverride"/> in order.
    /// </summary>
    /// <param name="baseOptions"> The global encoder options to use as the base. </param>
    /// <param name="folderOverride"> Per-folder overrides to apply first, or <see langword="null"/>. </param>
    /// <param name="nodeOverride"> Per-node overrides to apply second, or <see langword="null"/>. </param>
    public static EncoderOptions ApplyOverrides(
        EncoderOptions baseOptions,
        EncoderOptionsOverride? folderOverride,
        EncoderOptionsOverride? nodeOverride)
    {
        var result = baseOptions.Clone();
        Apply(result, folderOverride);
        Apply(result, nodeOverride);
        return result;
    }

    private static void Apply(EncoderOptions target, EncoderOptionsOverride? over)
    {
        if (over == null) return;

        if (over.Format != null)                      target.Format                     = over.Format;
        if (over.Codec != null)                       target.Codec                      = over.Codec;
        if (over.Encoder != null)                     target.Encoder                    = over.Encoder;
        if (over.TargetBitrate.HasValue)              target.TargetBitrate              = over.TargetBitrate.Value;
        if (over.StrictBitrate.HasValue)              target.StrictBitrate              = over.StrictBitrate.Value;
        if (over.FourKBitrateMultiplier.HasValue)     target.FourKBitrateMultiplier     = over.FourKBitrateMultiplier.Value;
        if (over.Skip4K.HasValue)                     target.Skip4K                     = over.Skip4K.Value;
        if (over.TwoChannelAudio.HasValue)            target.TwoChannelAudio            = over.TwoChannelAudio.Value;
        if (over.DeleteOriginalFile.HasValue)         target.DeleteOriginalFile         = over.DeleteOriginalFile.Value;
        if (over.RemoveBlackBorders.HasValue)         target.RemoveBlackBorders         = over.RemoveBlackBorders.Value;
        if (over.RetryOnFail.HasValue)                target.RetryOnFail                = over.RetryOnFail.Value;
        if (over.OutputDirectory != null)             target.OutputDirectory            = over.OutputDirectory;
        if (over.EncodeDirectory != null)             target.EncodeDirectory            = over.EncodeDirectory;
        if (over.HardwareAcceleration != null)        target.HardwareAcceleration       = over.HardwareAcceleration;
        if (over.SkipPercentAboveTarget.HasValue)     target.SkipPercentAboveTarget     = over.SkipPercentAboveTarget.Value;
        if (over.AudioLanguagesToKeep != null)        target.AudioLanguagesToKeep       = over.AudioLanguagesToKeep;
        if (over.KeepOriginalLanguage.HasValue)       target.KeepOriginalLanguage       = over.KeepOriginalLanguage.Value;
        if (over.OriginalLanguageProvider != null)    target.OriginalLanguageProvider   = over.OriginalLanguageProvider;
        // Audio: a non-null AudioOutputs/PreserveOriginalAudio override is the new shape and
        // wins outright. Otherwise, if a legacy AudioCodec/Bitrate/TwoChannel override is
        // present, re-run the legacy migration so the override actually takes effect — without
        // this, legacy overrides would write into AudioCodec but the planner only reads the
        // new AudioOutputs list and would silently ignore them.
        bool legacyAudioTouched = over.AudioCodec != null
                                 || over.AudioBitrateKbps.HasValue
                                 || over.TwoChannelAudio.HasValue;
        bool newAudioTouched    = over.AudioOutputs != null
                                 || over.PreserveOriginalAudio.HasValue;

        if (over.AudioCodec != null)                  target.AudioCodec                 = over.AudioCodec;
        if (over.AudioBitrateKbps.HasValue)           target.AudioBitrateKbps           = over.AudioBitrateKbps.Value;
        if (over.PreserveOriginalAudio.HasValue)      target.PreserveOriginalAudio      = over.PreserveOriginalAudio.Value;
        if (over.AudioOutputs != null)                target.AudioOutputs               = over.AudioOutputs.Select(p => p.Clone()).ToList();

        if (legacyAudioTouched && !newAudioTouched)
        {
            target.AudioOutputs = new();
            target.ApplyLegacyAudioMigration();
        }
        if (over.EncodingMode.HasValue)               target.EncodingMode               = over.EncodingMode.Value;
        if (over.MuxStreams.HasValue)                 target.MuxStreams                 = over.MuxStreams.Value;
        if (over.SubtitleLanguagesToKeep != null)     target.SubtitleLanguagesToKeep    = over.SubtitleLanguagesToKeep;
        if (over.ExtractSubtitlesToSidecar.HasValue)  target.ExtractSubtitlesToSidecar  = over.ExtractSubtitlesToSidecar.Value;
        if (over.SidecarSubtitleFormat != null)       target.SidecarSubtitleFormat      = over.SidecarSubtitleFormat;
        if (over.ConvertImageSubtitlesToSrt.HasValue)   target.ConvertImageSubtitlesToSrt   = over.ConvertImageSubtitlesToSrt.Value;
        if (over.PassThroughImageSubtitlesMkv.HasValue) target.PassThroughImageSubtitlesMkv = over.PassThroughImageSubtitlesMkv.Value;
        if (over.DownscalePolicy != null)             target.DownscalePolicy            = over.DownscalePolicy;
        if (over.DownscaleTarget != null)             target.DownscaleTarget            = over.DownscaleTarget;
        if (over.TonemapHdrToSdr.HasValue)            target.TonemapHdrToSdr            = over.TonemapHdrToSdr.Value;
        if (over.FfmpegQualityPreset != null)         target.FfmpegQualityPreset        = over.FfmpegQualityPreset;
    }
}
