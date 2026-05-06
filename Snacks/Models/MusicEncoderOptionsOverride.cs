namespace Snacks.Models;

/// <summary>
///     Nullable overlay for <see cref="MusicEncoderOptions"/>. Any non-null
///     property overrides the corresponding value in the base music options.
///     Used for per-folder and per-node music encoding settings, e.g. a
///     "Lossless" folder targeting flac alongside an "Audiobooks" folder
///     targeting opus 64 kbps.
/// </summary>
public sealed class MusicEncoderOptionsOverride
{
    /// <summary> Overrides <see cref="MusicEncoderOptions.Format"/> when non-<see langword="null"/>. </summary>
    public string? Format { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.Codec"/> when non-<see langword="null"/>. </summary>
    public string? Codec { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.BitrateKbps"/> when non-<see langword="null"/>. </summary>
    public int? BitrateKbps { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.VbrQuality"/> when non-<see langword="null"/>. </summary>
    public int? VbrQuality { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.SampleRatePolicy"/> when non-<see langword="null"/>. </summary>
    public string? SampleRatePolicy { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.ChannelPolicy"/> when non-<see langword="null"/>. </summary>
    public string? ChannelPolicy { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.SkipIfAlreadyTargetCodec"/> when non-<see langword="null"/>. </summary>
    public bool? SkipIfAlreadyTargetCodec { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.BitrateMatchTolerancePct"/> when non-<see langword="null"/>. </summary>
    public int? BitrateMatchTolerancePct { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.DeleteOriginalFile"/> when non-<see langword="null"/>. </summary>
    public bool? DeleteOriginalFile { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.OutputDirectory"/> when non-<see langword="null"/>. </summary>
    public string? OutputDirectory { get; set; }

    /// <summary> Overrides <see cref="MusicEncoderOptions.CopyMetadataAndArt"/> when non-<see langword="null"/>. </summary>
    public bool? CopyMetadataAndArt { get; set; }

    /// <summary>
    ///     Layers any non-null overrides onto the given music options in place.
    ///     Concurrency and cluster-dispatch knobs are intentionally not overridable
    ///     per-folder — they are master/global concerns.
    /// </summary>
    public void ApplyTo(MusicEncoderOptions target)
    {
        if (Format != null)                       target.Format                    = Format;
        if (Codec != null)                        target.Codec                     = Codec;
        if (BitrateKbps.HasValue)                 target.BitrateKbps               = BitrateKbps.Value;
        if (VbrQuality.HasValue)                  target.VbrQuality                = VbrQuality.Value;
        if (SampleRatePolicy != null)             target.SampleRatePolicy          = SampleRatePolicy;
        if (ChannelPolicy != null)                target.ChannelPolicy             = ChannelPolicy;
        if (SkipIfAlreadyTargetCodec.HasValue)    target.SkipIfAlreadyTargetCodec  = SkipIfAlreadyTargetCodec.Value;
        if (BitrateMatchTolerancePct.HasValue)    target.BitrateMatchTolerancePct  = BitrateMatchTolerancePct.Value;
        if (DeleteOriginalFile.HasValue)          target.DeleteOriginalFile        = DeleteOriginalFile.Value;
        if (OutputDirectory != null)              target.OutputDirectory           = OutputDirectory;
        if (CopyMetadataAndArt.HasValue)          target.CopyMetadataAndArt        = CopyMetadataAndArt.Value;
    }
}
