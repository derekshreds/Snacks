using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     Top-level encoding strategy.
///     <list type="bullet">
///         <item><see cref="Transcode"/> — re-encode video per the video settings; audio and subtitles per their tabs.</item>
///         <item><see cref="Hybrid"/> — files already at the bitrate target get a video-copy mux pass (audio/subs still processed); above-target files get a full re-encode.</item>
///         <item><see cref="MuxOnly"/> — never re-encode video. Files with muxable audio/subtitle work get a mux pass; files without muxable work are skipped entirely.</item>
///     </list>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EncodingMode
{
    Transcode,
    Hybrid,
    MuxOnly,
}

/// <summary>
///     Which non-video stream types a mux pass is allowed to touch. Streams outside
///     the selected type are always stream-copied through untouched — their tab settings
///     are ignored during the mux pass. Only consulted when <see cref="EncoderOptions.EncodingMode"/>
///     is not <see cref="EncodingMode.Transcode"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MuxStreams
{
    Both,
    Audio,
    Subtitles,
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

    /// <summary> Output container format ("mkv", "mp4", or "webm"). WebM coerces the video codec to AV1 and audio to Opus per the WebM spec. </summary>
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

    /// <summary>
    ///     H.264/H.265 profile (e.g. "baseline", "main", "high"). Empty/whitespace
    ///     uses the encoder's default. Only emitted for software encoders (libx264/libx265)
    ///     since hardware encoders have varying profile value sets.
    /// </summary>
    public string? VideoProfile { get; set; }

    /// <summary>
    ///     H.264/H.265 level as a decimal string (e.g. "1.3", "3.0", "4.0").
    ///     Empty/whitespace uses the encoder's default.
    /// </summary>
    public string? VideoLevel { get; set; }

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

    /// <summary>
    ///     Legacy single-codec selector. Kept on the model so old <c>settings.json</c>
    ///     files load without errors and so <see cref="ApplyLegacyAudioMigration"/>
    ///     can translate them into the new <see cref="AudioOutputs"/> shape. The planner
    ///     no longer reads this directly.
    /// </summary>
    public string AudioCodec { get; set; } = "copy";

    /// <summary>
    ///     Legacy single-bitrate field. Carried forward into the migrated
    ///     <see cref="AudioOutputs"/> entry when the legacy fields are populated.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>
    ///     When <see langword="true"/>, every kept source audio track is passed through
    ///     with <c>-c:a copy</c> alongside any encoded variants in <see cref="AudioOutputs"/>.
    ///     Defaults to <see langword="true"/> so out-of-the-box behavior is "remux, don't re-encode".
    /// </summary>
    public bool PreserveOriginalAudio { get; set; } = true;

    /// <summary>
    ///     Encoded audio variants to emit per kept language, on top of any preserved
    ///     source tracks. Empty list means "no additional outputs" — combined with
    ///     <see cref="PreserveOriginalAudio"/><c>=true</c> that reproduces the legacy
    ///     "copy everything" behavior.
    /// </summary>
    public List<AudioOutputProfile> AudioOutputs { get; set; } = new();

    /******************************************************************
     *  Encoding Mode
     ******************************************************************/

    /// <summary>
    ///     Top-level strategy. <see cref="EncodingMode.Transcode"/> re-encodes video
    ///     as usual. <see cref="EncodingMode.Hybrid"/> mux-passes at-target files and
    ///     transcodes above-target files. <see cref="EncodingMode.MuxOnly"/> never
    ///     re-encodes video; files with no muxable work are skipped.
    /// </summary>
    public EncodingMode EncodingMode { get; set; } = EncodingMode.Transcode;

    /// <summary>
    ///     Which stream types a mux pass is allowed to touch. Ignored when
    ///     <see cref="EncodingMode"/> is <see cref="EncodingMode.Transcode"/>.
    /// </summary>
    public MuxStreams MuxStreams { get; set; } = MuxStreams.Both;

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

    /// <summary>
    ///     When <see langword="true"/>, image-based subtitles (PGS/VOBSUB/DVB) are passed through
    ///     into MKV outputs alongside any text-based and OCR'd tracks. MP4 always strips them
    ///     regardless of this flag, since the format does not officially support PGS muxing.
    /// </summary>
    public bool PassThroughImageSubtitlesMkv { get; set; } = false;

    /// <summary>
    ///     When <see langword="true"/>, drops subtitle tracks tagged as hearing-impaired —
    ///     either via ffprobe's <c>disposition.hearing_impaired</c> flag or a title containing
    ///     "SDH", "CC", "Hearing Impaired", "HI", or "HoH" (case-insensitive, word-boundary
    ///     aware). Applies to muxed subtitles and sidecar extraction.
    /// </summary>
    public bool ExcludeSdhSubtitles { get; set; } = false;

    /// <summary>
    ///     When <see langword="true"/>, the first kept audio and subtitle output stream is
    ///     flagged as the default track and the others within each type have their default
    ///     flag cleared. Combined with the per-language priority order, this makes the
    ///     top-preference language auto-play in players that honor disposition.
    /// </summary>
    public bool AutoSetDefaultTrack { get; set; } = false;

    /******************************************************************
     *  Video Pipeline
     ******************************************************************/

    /// <summary>
    ///     Downscale policy: "Never", "CapAtTarget", or "Always" (the values the UI writes).
    ///     The legacy alias "IfLarger" is still accepted by the backend and mapped to
    ///     "CapAtTarget" on settings restore.
    /// </summary>
    public string DownscalePolicy { get; set; } = "Never";

    /// <summary> Target resolution for downscaling (e.g. "1080p", "720p"). </summary>
    public string DownscaleTarget { get; set; } = "1080p";

    /// <summary>
    ///     When set (e.g. "640x480"), scales and pads to this exact frame size with
    ///     letterboxing and forces <c>yuv420p</c> pixel format. Overrides the normal
    ///     downscale policy/target. Used by device-specific presets (e.g. iPod Classic
    ///     requires a strict 640×480 frame).
    /// </summary>
    public string? FixedFrameSize { get; set; }

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

    /// <summary>
    ///     Number of days to retain per-job FFmpeg log files in <c>{workdir}/logs/</c>.
    ///     <c>0</c> disables the sweep (keep forever). The Serilog rolling app log
    ///     (<c>snacks-*.log</c>) is unaffected — it has its own 7-day / 10MB cap.
    /// </summary>
    public int EncodingLogRetentionDays { get; set; } = 7;

    /// <summary>
    ///     Daily budget for the rolling deep-verifier (ffmpeg decode samples per file,
    ///     oldest-verified first). <c>0</c> (default) disables it. Spread evenly across
    ///     hourly ticks so the I/O cost is a trickle, not a nightly storm.
    /// </summary>
    public int VerifyFilesPerDay { get; set; } = 0;

    /// <summary>
    ///     When <see langword="true"/>, the pending-queue tiebreaker (after user/folder
    ///     priority) is recency — newest files first — instead of bitrate descending.
    ///     The right setting for "convert new downloads before the backlog".
    /// </summary>
    public bool QueueNewestFirst { get; set; } = false;

    /// <summary> Optional output directory override. When <see langword="null"/>, output is written beside the source. </summary>
    public string? OutputDirectory { get; set; }

    /// <summary> Optional intermediate encode directory. When <see langword="null"/>, the system temp directory is used. </summary>
    public string? EncodeDirectory { get; set; }

    /// <summary>
    ///     Hardware acceleration mode: "auto", "intel", "amd", "nvidia", "apple", or "none"
    ///     (the values the UI writes). Legacy aliases ("nvenc", "vaapi", "qsv", "amf") are
    ///     mapped to these on settings restore.
    /// </summary>
    public string HardwareAcceleration { get; set; } = "auto";

    /// <summary>
    ///     Linux VAAPI device node path for the GPU that should service this job
    ///     (e.g. <c>/dev/dri/renderD128</c>, <c>/dev/dri/renderD129</c>). Resolved at
    ///     dispatch from the chosen <see cref="HardwareDevice.DevicePath"/>. <see langword="null"/>
    ///     on Windows/macOS, for NVIDIA encodes, and for the CPU/software path —
    ///     ffmpeg's init flags fall back to the legacy <c>renderD128</c> default in
    ///     those cases. Not user-configurable; not persisted to settings.json — this
    ///     is per-dispatch ephemeral state.
    /// </summary>
    public string? HardwareDevicePath { get; set; }

    /******************************************************************
     *  Music (audio-only files)
     ******************************************************************/

    /// <summary>
    ///     Settings for music (audio-only) file transcoding. Music encoding shares the
    ///     queue, scheduler, cluster dispatcher, and analytics with video, but has its
    ///     own encoder pipeline (<c>ConvertMusicAsync</c>) and an independent slot pool
    ///     so it never competes with GPU video slots. Pre-pivot <c>settings.json</c>
    ///     files load with this initialized to defaults.
    /// </summary>
    public MusicEncoderOptions Music { get; set; } = new();

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
        VideoProfile               = VideoProfile,
        VideoLevel                 = VideoLevel,
        TwoChannelAudio            = TwoChannelAudio,
        AudioLanguagesToKeep       = new List<string>(AudioLanguagesToKeep),
        KeepOriginalLanguage       = KeepOriginalLanguage,
        OriginalLanguageProvider   = OriginalLanguageProvider,
        AudioCodec                 = AudioCodec,
        AudioBitrateKbps           = AudioBitrateKbps,
        PreserveOriginalAudio      = PreserveOriginalAudio,
        AudioOutputs               = AudioOutputs.Select(p => p.Clone()).ToList(),
        EncodingMode               = EncodingMode,
        MuxStreams                 = MuxStreams,
        SubtitleLanguagesToKeep    = new List<string>(SubtitleLanguagesToKeep),
        ExtractSubtitlesToSidecar  = ExtractSubtitlesToSidecar,
        SidecarSubtitleFormat      = SidecarSubtitleFormat,
        ConvertImageSubtitlesToSrt = ConvertImageSubtitlesToSrt,
        PassThroughImageSubtitlesMkv = PassThroughImageSubtitlesMkv,
        ExcludeSdhSubtitles        = ExcludeSdhSubtitles,
        AutoSetDefaultTrack        = AutoSetDefaultTrack,
        DownscalePolicy            = DownscalePolicy,
        DownscaleTarget            = DownscaleTarget,
        FixedFrameSize             = FixedFrameSize,
        TonemapHdrToSdr            = TonemapHdrToSdr,
        RemoveBlackBorders         = RemoveBlackBorders,
        DeleteOriginalFile         = DeleteOriginalFile,
        RetryOnFail                = RetryOnFail,
        SkipPercentAboveTarget     = SkipPercentAboveTarget,
        EncodingLogRetentionDays   = EncodingLogRetentionDays,
        VerifyFilesPerDay          = VerifyFilesPerDay,
        QueueNewestFirst           = QueueNewestFirst,
        OutputDirectory            = OutputDirectory,
        EncodeDirectory            = EncodeDirectory,
        HardwareAcceleration       = HardwareAcceleration,
        HardwareDevicePath         = HardwareDevicePath,
        Music                      = Music.Clone(),
    };

    /******************************************************************
     *  Legacy audio migration
     ******************************************************************/

    /// <summary>
    ///     Translates the legacy <see cref="AudioCodec"/> + <see cref="AudioBitrateKbps"/> +
    ///     <see cref="TwoChannelAudio"/> trio into the new <see cref="PreserveOriginalAudio"/> +
    ///     <see cref="AudioOutputs"/> shape. Idempotent: runs only when <see cref="AudioOutputs"/>
    ///     is empty AND the legacy fields look populated, so newer configs are left alone.
    ///     Mapping:
    ///     <list type="bullet">
    ///         <item><c>AudioCodec="copy"</c> → <c>Preserve=true, Outputs=[]</c></item>
    ///         <item><c>AudioCodec=X (X!=copy), TwoChannelAudio=true</c> → <c>Preserve=false, Outputs=[{X, "Stereo", AudioBitrateKbps}]</c></item>
    ///         <item><c>AudioCodec=X (X!=copy), TwoChannelAudio=false</c> → <c>Preserve=false, Outputs=[{X, "Source", AudioBitrateKbps}]</c></item>
    ///     </list>
    /// </summary>
    public void ApplyLegacyAudioMigration()
    {
        // Already migrated — leave the new shape alone.
        if (AudioOutputs is { Count: > 0 }) return;

        var legacy = (AudioCodec ?? "").Trim().ToLowerInvariant();

        if (legacy == "copy" || string.IsNullOrEmpty(legacy))
        {
            // Default / "copy" config — preserve originals, no extra outputs.
            PreserveOriginalAudio = true;
            AudioOutputs          = new();
            return;
        }

        // Legacy non-copy codec — emit a single output that reproduces the prior behavior.
        PreserveOriginalAudio = false;
        AudioOutputs = new()
        {
            new AudioOutputProfile
            {
                Codec       = legacy,
                Layout      = TwoChannelAudio ? "Stereo" : "Source",
                BitrateKbps = AudioBitrateKbps > 0 ? AudioBitrateKbps : 0,
            }
        };
    }
}
