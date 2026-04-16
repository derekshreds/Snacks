namespace Snacks.Models;

/// <summary>
///     Nullable overlay for <see cref="EncoderOptions"/>.
///     Any non-null property overrides the corresponding value in the base options.
///     Used for per-folder and per-node encoding settings.
/// </summary>
public sealed class EncoderOptionsOverride
{
    public string? Format { get; set; }
    public string? Codec { get; set; }
    public string? Encoder { get; set; }
    public int? TargetBitrate { get; set; }
    public bool? StrictBitrate { get; set; }
    public int? FourKBitrateMultiplier { get; set; }
    public bool? Skip4K { get; set; }
    public bool? TwoChannelAudio { get; set; }
    public bool? EnglishOnlyAudio { get; set; }
    public bool? EnglishOnlySubtitles { get; set; }
    public bool? DeleteOriginalFile { get; set; }
    public bool? RemoveBlackBorders { get; set; }
    public bool? RetryOnFail { get; set; }
    public string? OutputDirectory { get; set; }
    public string? EncodeDirectory { get; set; }
    public string? HardwareAcceleration { get; set; }
    public int? SkipPercentAboveTarget { get; set; }

    /// <summary>
    ///     Builds a final <see cref="EncoderOptions"/> by starting from <paramref name="baseOptions"/>
    ///     and applying non-null fields from <paramref name="folderOverride"/> then <paramref name="nodeOverride"/>.
    ///     Merge order: global → folder → node (most specific wins).
    /// </summary>
    public static EncoderOptions ApplyOverrides(
        EncoderOptions baseOptions,
        EncoderOptionsOverride? folderOverride,
        EncoderOptionsOverride? nodeOverride)
    {
        var result = new EncoderOptions
        {
            Format                 = baseOptions.Format,
            Codec                  = baseOptions.Codec,
            Encoder                = baseOptions.Encoder,
            TargetBitrate          = baseOptions.TargetBitrate,
            StrictBitrate          = baseOptions.StrictBitrate,
            FourKBitrateMultiplier = baseOptions.FourKBitrateMultiplier,
            Skip4K                 = baseOptions.Skip4K,
            TwoChannelAudio        = baseOptions.TwoChannelAudio,
            EnglishOnlyAudio       = baseOptions.EnglishOnlyAudio,
            EnglishOnlySubtitles   = baseOptions.EnglishOnlySubtitles,
            DeleteOriginalFile     = baseOptions.DeleteOriginalFile,
            RemoveBlackBorders     = baseOptions.RemoveBlackBorders,
            RetryOnFail            = baseOptions.RetryOnFail,
            OutputDirectory        = baseOptions.OutputDirectory,
            EncodeDirectory        = baseOptions.EncodeDirectory,
            HardwareAcceleration   = baseOptions.HardwareAcceleration,
            SkipPercentAboveTarget = baseOptions.SkipPercentAboveTarget,
        };

        Apply(result, folderOverride);
        Apply(result, nodeOverride);
        return result;
    }

    private static void Apply(EncoderOptions target, EncoderOptionsOverride? over)
    {
        if (over == null) return;

        if (over.Format != null)                  target.Format                 = over.Format;
        if (over.Codec != null)                   target.Codec                  = over.Codec;
        if (over.Encoder != null)                 target.Encoder                = over.Encoder;
        if (over.TargetBitrate.HasValue)          target.TargetBitrate          = over.TargetBitrate.Value;
        if (over.StrictBitrate.HasValue)          target.StrictBitrate          = over.StrictBitrate.Value;
        if (over.FourKBitrateMultiplier.HasValue) target.FourKBitrateMultiplier = over.FourKBitrateMultiplier.Value;
        if (over.Skip4K.HasValue)                 target.Skip4K                 = over.Skip4K.Value;
        if (over.TwoChannelAudio.HasValue)        target.TwoChannelAudio        = over.TwoChannelAudio.Value;
        if (over.EnglishOnlyAudio.HasValue)       target.EnglishOnlyAudio       = over.EnglishOnlyAudio.Value;
        if (over.EnglishOnlySubtitles.HasValue)   target.EnglishOnlySubtitles   = over.EnglishOnlySubtitles.Value;
        if (over.DeleteOriginalFile.HasValue)     target.DeleteOriginalFile     = over.DeleteOriginalFile.Value;
        if (over.RemoveBlackBorders.HasValue)     target.RemoveBlackBorders     = over.RemoveBlackBorders.Value;
        if (over.RetryOnFail.HasValue)            target.RetryOnFail            = over.RetryOnFail.Value;
        if (over.OutputDirectory != null)         target.OutputDirectory        = over.OutputDirectory;
        if (over.EncodeDirectory != null)         target.EncodeDirectory        = over.EncodeDirectory;
        if (over.HardwareAcceleration != null)    target.HardwareAcceleration   = over.HardwareAcceleration;
        if (over.SkipPercentAboveTarget.HasValue) target.SkipPercentAboveTarget = over.SkipPercentAboveTarget.Value;
    }
}
