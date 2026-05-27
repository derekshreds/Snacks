namespace Snacks.Models;

/// <summary>
///     A named, shareable bundle of encoder settings - a "quick setup" the user can
///     apply in one click. Built-ins ship with Snacks and are read-only; custom
///     policies are user-created and can be edited, exported, and shared.
///
///     Strategically (per the PM brief), policies are the wedge that reframes Snacks
///     from "configurable transcoder" to "policies as a product". The built-in names
///     are use-case-led ("Plex-Safe HEVC", "Make-It-Small Keep-It-Good") rather than
///     feature-led, and each carries a short tagline plus a plain-English list of
///     outcomes so a non-expert can pick confidently without learning what HEVC means.
/// </summary>
public sealed class Policy
{
    /// <summary> Stable identifier. Built-ins use fixed slugs (e.g. <c>builtin-plex-safe-hevc</c>); custom policies use generated GUIDs. </summary>
    public string Id { get; set; } = "";

    /// <summary> Human-readable name shown in the picker (e.g. "Plex-Safe HEVC"). </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     One-line use-case pitch shown under the picker. Should answer "who is this for?"
    ///     and "what is the trade-off?" in ~12 words.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Plain-English outcome lines shown to the user as the primary explanation of
    ///     what this policy does. Each entry is a single complete phrase that describes
    ///     a consequence in terms a non-expert understands ("Files end up roughly half
    ///     the size", not "Targets 50% bitrate reduction"). 3-6 lines per policy.
    /// </summary>
    public List<string> OutcomeBullets { get; set; } = new();

    /// <summary>
    ///     When <see langword="true"/>, the picker surfaces a "Recommended" affordance
    ///     for this policy. Per the YouTube/Plex pattern, exactly one policy carries
    ///     the recommendation; never more than one.
    /// </summary>
    public bool Recommended { get; set; }

    /// <summary> When <see langword="true"/>, the policy is shipped with Snacks and cannot be edited or deleted. </summary>
    public bool BuiltIn { get; set; }

    /// <summary> UTC creation timestamp. </summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary> UTC last-update timestamp. </summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary> The encoder options snapshot this policy applies. </summary>
    public EncoderOptions Options { get; set; } = new();

    public Policy Clone() => new()
    {
        Id             = Id,
        Name           = Name,
        Description    = Description,
        OutcomeBullets = new List<string>(OutcomeBullets),
        Recommended    = Recommended,
        BuiltIn        = BuiltIn,
        CreatedUtc     = CreatedUtc,
        UpdatedUtc     = UpdatedUtc,
        Options        = Options.Clone(),
    };

    /// <summary>
    ///     Returns the seed list of built-in policies. Called on first load and on
    ///     every load to top up missing built-ins after a Snacks upgrade. Order here
    ///     is the order they appear in the UI.
    ///
    ///     V1 scope: five built-ins covering the home-media use cases the PM brief
    ///     calls out by name. Long-tail device presets (Mobile, WebM/AV1, Signage,
    ///     Creator Multi-Variant) belong in user-contributed packs once the community
    ///     tap registry ships in Q2.
    /// </summary>
    public static IReadOnlyList<Policy> BuiltIns()
    {
        // Fixed epoch so re-seeding produces byte-identical JSON on every load.
        // Without this, every read would rewrite policies.json with fresh UtcNow
        // values and churn the .bak indefinitely.
        var epoch = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return new[]
        {
            new Policy
            {
                Id             = "builtin-plex-safe-hevc",
                Name           = "Plex-Safe HEVC",
                Description    = "The Snacks default. Smaller files, plays anywhere.",
                Recommended    = true,
                BuiltIn        = true,
                CreatedUtc     = epoch,
                UpdatedUtc     = epoch,
                OutcomeBullets = new()
                {
                    "Files end up roughly half the size",
                    "Plays on phones, TVs, browsers",
                    "Keeps the original soundtrack",
                    "Keeps subtitles as-is",
                    "Uses your graphics card when available",
                },
                Options = new EncoderOptions(),
            },

            new Policy
            {
                Id             = "builtin-make-it-small",
                Name           = "Make-It-Small, Keep-It-Good",
                Description    = "Smallest files at watchable quality.",
                BuiltIn        = true,
                CreatedUtc     = epoch,
                UpdatedUtc     = epoch,
                OutcomeBullets = new()
                {
                    "Files shrink as small as practical",
                    "Drops extra audio languages and surround tracks",
                    "Trims black bars from letterboxed video",
                    "Flattens HDR for normal screens",
                    "Some fine detail is lost",
                },
                Options = new EncoderOptions
                {
                    Format                 = "mkv",
                    Codec                  = "h265",
                    Encoder                = "libx265",
                    TargetBitrate          = 1800,
                    FourKBitrateMultiplier = 3,
                    StrictBitrate          = false,
                    FfmpegQualityPreset    = "slow",
                    TonemapHdrToSdr        = true,
                    RemoveBlackBorders     = true,
                    SkipPercentAboveTarget = 15,
                    PreserveOriginalAudio  = false,
                    AudioOutputs = new()
                    {
                        new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 128 },
                    },
                    AudioLanguagesToKeep       = new() { "en" },
                    SubtitleLanguagesToKeep    = new() { "en" },
                    ConvertImageSubtitlesToSrt = true,
                    ExcludeSdhSubtitles        = true,
                },
            },

            new Policy
            {
                Id             = "builtin-archive-master",
                Name           = "Archive Master",
                Description    = "Keep every track. Future-proof your library.",
                BuiltIn        = true,
                CreatedUtc     = epoch,
                UpdatedUtc     = epoch,
                OutcomeBullets = new()
                {
                    "Picture quality is preserved",
                    "Every audio and subtitle track stays",
                    "HDR metadata is kept intact",
                    "Files stay large",
                    "Encoding takes longer",
                },
                Options = new EncoderOptions
                {
                    Format                  = "mkv",
                    Codec                   = "h265",
                    Encoder                 = "libx265",
                    TargetBitrate           = 6000,
                    FourKBitrateMultiplier  = 3,
                    StrictBitrate           = false,
                    FfmpegQualityPreset     = "slow",
                    SkipPercentAboveTarget  = 0,
                    TonemapHdrToSdr         = false,
                    PreserveOriginalAudio   = true,
                    AudioOutputs            = new(),
                    AudioLanguagesToKeep    = new(),
                    SubtitleLanguagesToKeep = new(),
                    ExcludeSdhSubtitles     = false,
                },
            },

            new Policy
            {
                Id             = "builtin-old-ipod",
                Name           = "Old iPod (240p)",
                Description    = "Plays on an iPod 5G / Classic. Tiny everything.",
                BuiltIn        = true,
                CreatedUtc     = epoch,
                UpdatedUtc     = epoch,
                OutcomeBullets = new()
                {
                    "Shrinks video to 320x240",
                    "Locks the format to old-style MP4",
                    "Uses stereo audio only",
                    "Drops subtitles (iPod can't show them)",
                    "Files become very small",
                },
                Options = new EncoderOptions
                {
                    // iPod 5G/Classic spec (Apple SP3/SP19): MP4 + H.264 Baseline
                    // level 3.0 + no B-frames + CAVLC + yuv420p. Without these the
                    // hardware decoder rejects the stream regardless of resolution.
                    Format                       = "mp4",
                    Codec                        = "h264",
                    Encoder                      = "libx264",
                    H264Profile                  = "baseline",
                    H264Level                    = "3.0",
                    TargetBitrate                = 1200,
                    FourKBitrateMultiplier       = 2,
                    FfmpegQualityPreset          = "medium",
                    DownscalePolicy              = "CapAtTarget",
                    DownscaleTarget              = "240p",
                    TonemapHdrToSdr              = true,
                    PreserveOriginalAudio        = false,
                    AudioOutputs = new()
                    {
                        new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 128 },
                    },
                    AudioLanguagesToKeep         = new() { "en" },
                    SubtitleLanguagesToKeep      = new(),
                    ExtractSubtitlesToSidecar    = false,
                    ConvertImageSubtitlesToSrt   = false,
                    PassThroughImageSubtitlesMkv = false,
                },
            },

            new Policy
            {
                Id             = "builtin-clean-up-tracks",
                Name           = "Clean Up Tracks",
                Description    = "Strip unwanted audio/subs. Never re-encode the picture.",
                BuiltIn        = true,
                CreatedUtc     = epoch,
                UpdatedUtc     = epoch,
                OutcomeBullets = new()
                {
                    "Picture quality stays identical",
                    "Finishes in seconds, not hours",
                    "Strips unwanted audio languages",
                    "Strips unwanted subtitle tracks",
                    "Useful for cleanup, not shrinking",
                },
                Options = new EncoderOptions
                {
                    EncodingMode          = EncodingMode.MuxOnly,
                    MuxStreams            = MuxStreams.Both,
                    PreserveOriginalAudio = true,
                    AudioOutputs          = new(),
                },
            },
        };
    }
}

/// <summary>
///     On-disk shape for <c>config/policies.json</c> and the export-file shape.
///     Wraps the list with a <see cref="SchemaVersion"/> so future field renames
///     can migrate cleanly the way <see cref="EncoderOptions.ApplyLegacyAudioMigration"/>
///     does today.
/// </summary>
public sealed class PolicyDocument
{
    /// <summary> Current schema version. Bump when the shape changes in a non-additive way. </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary> Schema version of this document. Imports with a version higher than <see cref="CurrentSchemaVersion"/> are rejected. </summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    ///     Id of the policy most recently applied to <c>settings.json</c>. The active-policy
    ///     query uses this to distinguish "user applied Plex-Safe HEVC and is still on it" from
    ///     "user applied Plex-Safe HEVC and then tweaked the bitrate" - in both cases the
    ///     active policy is Plex-Safe HEVC, but the second is marked as modified.
    /// </summary>
    public string? LastAppliedPolicyId { get; set; }

    /// <summary> Every policy in the document - built-ins plus custom. </summary>
    public List<Policy> Policies { get; set; } = new();
}

/// <summary>
///     Result of the active-policy query. Tells the UI which policy (if any) the
///     current <c>settings.json</c> matches, and whether the user has tweaked anything
///     since it was applied.
/// </summary>
public sealed class ActivePolicyResult
{
    /// <summary> Id of the active policy, or <see langword="null"/> when settings don't match any policy. </summary>
    public string? Id { get; set; }

    /// <summary> Display name of the active policy. <c>"Custom"</c> when <see cref="Id"/> is null. </summary>
    public string Name { get; set; } = "Custom";

    /// <summary>
    ///     <see langword="true"/> when the last-applied policy exists but the current settings
    ///     have diverged from it (user tweaked something on another tab).
    /// </summary>
    public bool Modified { get; set; }
}
