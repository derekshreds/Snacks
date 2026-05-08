namespace Snacks.Models;

/// <summary>
///     User-configurable encoding options for music (audio-only) files.
///     Nested inside <see cref="EncoderOptions"/> as the <c>Music</c> property.
///     Music encoding shares the queue, scheduler shell, cluster dispatcher,
///     and analytics with video — but the encoder pipeline is separate
///     (<c>ConvertMusicAsync</c>) and the slot pool is independent so music
///     never competes with GPU video slots.
/// </summary>
public sealed class MusicEncoderOptions
{
    /******************************************************************
     *  Output container / codec
     ******************************************************************/

    /// <summary> Output container format. Supported: <c>"mp3"</c>, <c>"m4a"</c>, <c>"ogg"</c>, <c>"opus"</c>, <c>"flac"</c>. </summary>
    public string Format { get; set; } = "m4a";

    /// <summary>
    ///     FFmpeg encoder identifier. Must be compatible with <see cref="Format"/>:
    ///     <list type="bullet">
    ///         <item><c>mp3</c> → <c>libmp3lame</c></item>
    ///         <item><c>m4a</c> → <c>aac</c></item>
    ///         <item><c>ogg</c> → <c>libvorbis</c></item>
    ///         <item><c>opus</c> → <c>libopus</c></item>
    ///         <item><c>flac</c> → <c>flac</c> (lossless)</item>
    ///     </list>
    /// </summary>
    public string Codec { get; set; } = "aac";

    /// <summary> Target output bitrate in kbps. Ignored when <see cref="Codec"/> is <c>flac</c> (lossless). </summary>
    public int BitrateKbps { get; set; } = 192;

    /// <summary>
    ///     Optional VBR quality value. When non-<see langword="null"/>, replaces the bitrate
    ///     flag with <c>-q:a</c>. Codec-specific scale: 0–9 for libmp3lame (lower is better),
    ///     0–10 for libvorbis (higher is better). <see langword="null"/> means CBR/ABR via bitrate.
    /// </summary>
    public int? VbrQuality { get; set; }

    /******************************************************************
     *  Sample rate / channels
     ******************************************************************/

    /// <summary>
    ///     Sample-rate policy. <c>"Source"</c> emits no flag (preserve source rate).
    ///     <c>"44100"</c> or <c>"48000"</c> resamples to that rate via <c>-ar</c>.
    /// </summary>
    public string SampleRatePolicy { get; set; } = "Source";

    /// <summary>
    ///     Channel-layout policy. <c>"Source"</c> emits no flag. <c>"Mono"</c> = <c>-ac 1</c>.
    ///     <c>"Stereo"</c> = <c>-ac 2</c>.
    /// </summary>
    public string ChannelPolicy { get; set; } = "Source";

    /******************************************************************
     *  Skip ladder
     ******************************************************************/

    /// <summary>
    ///     When <see langword="true"/>, files already encoded in the target codec are
    ///     skipped if their bitrate is within <see cref="BitrateMatchTolerancePct"/>
    ///     of the target. Lossy → lossless conversions are always skipped regardless
    ///     of this flag (no quality recovery is possible).
    /// </summary>
    public bool SkipIfAlreadyTargetCodec { get; set; } = true;

    /// <summary> Bitrate match tolerance percentage for the skip ladder. </summary>
    public int BitrateMatchTolerancePct { get; set; } = 15;

    /******************************************************************
     *  Output behavior
     ******************************************************************/

    /// <summary> When <see langword="true"/>, the source file is deleted after a successful encode. </summary>
    public bool DeleteOriginalFile { get; set; } = false;

    /// <summary> Optional output directory override. When <see langword="null"/>, output is written beside the source. </summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    ///     When <see langword="true"/>, source metadata (artist, title, album, year, ...)
    ///     and embedded cover art are copied through to the output via <c>-map_metadata 0</c>
    ///     and a video-stream copy of the cover-art track. Defaults to <see langword="true"/>.
    /// </summary>
    public bool CopyMetadataAndArt { get; set; } = true;

    /******************************************************************
     *  Concurrency
     ******************************************************************/

    /// <summary>
    ///     Maximum number of music encodes running simultaneously on the master.
    ///     Independent of the per-device video slot pool — music is CPU-only and
    ///     should not compete with GPU video encodes for hardware slots.
    /// </summary>
    public int MasterMusicConcurrency { get; set; } = 2;

    /******************************************************************
     *  Cluster
     ******************************************************************/

    /// <summary>
    ///     When <see langword="true"/>, music jobs are eligible for dispatch to
    ///     cluster worker nodes (subject to each node's <c>SupportsMusic</c>
    ///     capability). When <see langword="false"/>, music encodes always run
    ///     on the master regardless of the cluster role.
    /// </summary>
    public bool DispatchToCluster { get; set; } = true;

    /******************************************************************
     *  Cloning
     ******************************************************************/

    /// <summary> Deep copy of this options instance. </summary>
    public MusicEncoderOptions Clone() => new()
    {
        Format                    = Format,
        Codec                     = Codec,
        BitrateKbps               = BitrateKbps,
        VbrQuality                = VbrQuality,
        SampleRatePolicy          = SampleRatePolicy,
        ChannelPolicy             = ChannelPolicy,
        SkipIfAlreadyTargetCodec  = SkipIfAlreadyTargetCodec,
        BitrateMatchTolerancePct  = BitrateMatchTolerancePct,
        DeleteOriginalFile        = DeleteOriginalFile,
        OutputDirectory           = OutputDirectory,
        CopyMetadataAndArt        = CopyMetadataAndArt,
        MasterMusicConcurrency    = MasterMusicConcurrency,
        DispatchToCluster         = DispatchToCluster,
    };
}
