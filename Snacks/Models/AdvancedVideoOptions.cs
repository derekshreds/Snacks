using System.Globalization;
using System.Text.Json.Serialization;

namespace Snacks.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvancedVideoAction
{
    UseSimpleSettings,
    TranscodeWithProfile,
    MuxOnly,
    Skip,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoEncoderSelectionMode
{
    Automatic,
    Explicit,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoRateControlMode
{
    Bitrate,
    Quality,
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoOutputRetention
{
    SmallerOnly,
    AlwaysKeep,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoRuleMatchMode
{
    All,
    Any,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoRuleField
{
    Codec,
    Width,
    Height,
    ResolutionClass,
    BitrateKbps,
    FileSizeBytes,
    DurationSeconds,
    PixelFormat,
    BitDepth,
    IsHdr,
    Is4K,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoRuleOperator
{
    Is,
    IsNot,
    In,
    NotIn,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Between,
    IsKnown,
    IsUnknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AdvancedVideoFolderPolicy
{
    Inherit,
    Simple,
    Profile,
}

/// <summary>
///     Opt-in video policy layer. The empty/default value is deliberately inert so an
///     existing settings file follows the exact legacy pipeline after upgrading.
/// </summary>
public sealed class AdvancedVideoOptions
{
    /// <summary>
    ///     Persisted schema version for the advanced-video block. Version 1 is the
    ///     initial public contract; older settings omit the entire block and still
    ///     deserialize to an inert version-1 instance with <see cref="Enabled"/> off.
    /// </summary>
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; }
    public List<VideoEncodingProfile> Profiles { get; set; } = new();
    public List<VideoRule> Rules { get; set; } = new();
    public AdvancedVideoAction DefaultAction { get; set; } = AdvancedVideoAction.UseSimpleSettings;
    public Guid? DefaultProfileId { get; set; }

    public AdvancedVideoOptions Clone() => new()
    {
        Version          = Version,
        Enabled          = Enabled,
        Profiles         = (Profiles ?? []).Where(p => p != null).Select(p => p.Clone()).ToList(),
        Rules            = (Rules ?? []).Where(r => r != null).Select(r => r.Clone()).ToList(),
        DefaultAction    = DefaultAction,
        DefaultProfileId = DefaultProfileId,
    };
}

/// <summary>A complete, reusable set of video-only output settings.</summary>
public sealed class VideoEncodingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New video profile";
    public string Codec { get; set; } = "h265";
    public VideoEncoderSelectionMode EncoderSelection { get; set; } = VideoEncoderSelectionMode.Automatic;
    public string? Encoder { get; set; }
    public string HardwareAcceleration { get; set; } = "auto";
    public VideoRateControlOptions RateControl { get; set; } = new();
    public string? Preset { get; set; } = "medium";
    public int Threads { get; set; }
    public string? PixelFormat { get; set; }
    public int GopSize { get; set; }
    public string? VideoProfile { get; set; }
    public string? VideoLevel { get; set; }
    public string DownscalePolicy { get; set; } = "Never";
    public string DownscaleTarget { get; set; } = "1080p";
    public string? FixedFrameSize { get; set; }
    public int MaxFrameRate { get; set; }
    public bool TonemapHdrToSdr { get; set; }
    public bool RemoveBlackBorders { get; set; }
    public List<string> AdditionalVideoFilters { get; set; } = new();
    public List<CustomVideoOption> CustomOptions { get; set; } = new();
    public VideoOutputRetention OutputRetention { get; set; } = VideoOutputRetention.SmallerOnly;

    public VideoEncodingProfile Clone() => new()
    {
        Id                     = Id,
        Name                   = Name,
        Codec                  = Codec,
        EncoderSelection       = EncoderSelection,
        Encoder                = Encoder,
        HardwareAcceleration   = HardwareAcceleration,
        RateControl            = RateControl?.Clone() ?? new VideoRateControlOptions(),
        Preset                 = Preset,
        Threads                = Threads,
        PixelFormat            = PixelFormat,
        GopSize                = GopSize,
        VideoProfile           = VideoProfile,
        VideoLevel             = VideoLevel,
        DownscalePolicy        = DownscalePolicy,
        DownscaleTarget        = DownscaleTarget,
        FixedFrameSize         = FixedFrameSize,
        MaxFrameRate           = MaxFrameRate,
        TonemapHdrToSdr        = TonemapHdrToSdr,
        RemoveBlackBorders     = RemoveBlackBorders,
        AdditionalVideoFilters = new(AdditionalVideoFilters ?? []),
        CustomOptions          = (CustomOptions ?? []).Where(o => o != null).Select(o => o.Clone()).ToList(),
        OutputRetention        = OutputRetention,
    };

    /// <summary>Creates a profile which is initially equivalent to the visible Simple form.</summary>
    public static VideoEncodingProfile FromSimple(string name, EncoderOptions options) => new()
    {
        Name                   = name,
        Codec                  = VideoSourceFacts.NormalizeCodec(options.Codec),
        EncoderSelection       = VideoEncoderSelectionMode.Automatic,
        HardwareAcceleration   = options.HardwareAcceleration,
        RateControl            = new VideoRateControlOptions
        {
            Mode          = VideoRateControlMode.Bitrate,
            TargetKbps    = options.TargetBitrate,
            StrictBitrate = options.StrictBitrate,
        },
        Preset                 = options.FfmpegQualityPreset,
        VideoProfile           = options.VideoProfile,
        VideoLevel             = options.VideoLevel,
        DownscalePolicy        = options.DownscalePolicy,
        DownscaleTarget        = options.DownscaleTarget,
        FixedFrameSize         = options.FixedFrameSize,
        MaxFrameRate           = options.MaxFrameRate,
        TonemapHdrToSdr        = options.TonemapHdrToSdr,
        RemoveBlackBorders     = options.RemoveBlackBorders,
        OutputRetention        = VideoOutputRetention.SmallerOnly,
    };
}

public sealed class VideoRateControlOptions
{
    public VideoRateControlMode Mode { get; set; } = VideoRateControlMode.Bitrate;
    public int TargetKbps { get; set; } = 3500;
    public int? MinKbps { get; set; }
    public int? MaxKbps { get; set; }
    public int? BufferKbits { get; set; }
    public bool StrictBitrate { get; set; }
    public double Quality { get; set; } = 35;

    public VideoRateControlOptions Clone() => (VideoRateControlOptions)MemberwiseClone();
}

/// <summary>
///     A custom FFmpeg option stored as an option plus literal value tokens. This shape
///     makes a positional output impossible and maps directly to ProcessStartInfo.ArgumentList.
/// </summary>
public sealed class CustomVideoOption
{
    public string Option { get; set; } = "";
    public List<string> Values { get; set; } = new();

    public CustomVideoOption Clone() => new() { Option = Option, Values = new(Values ?? []) };
}

public sealed class VideoRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New rule";
    public bool Enabled { get; set; } = true;
    public VideoRuleMatchMode Match { get; set; } = VideoRuleMatchMode.All;
    public List<VideoRuleCondition> Conditions { get; set; } = new();
    public AdvancedVideoAction Action { get; set; } = AdvancedVideoAction.TranscodeWithProfile;
    public Guid? ProfileId { get; set; }

    public VideoRule Clone() => new()
    {
        Id         = Id,
        Name       = Name,
        Enabled    = Enabled,
        Match      = Match,
        Conditions = (Conditions ?? []).Where(c => c != null).Select(c => c.Clone()).ToList(),
        Action     = Action,
        ProfileId  = ProfileId,
    };
}

public sealed class VideoRuleCondition
{
    public VideoRuleField Field { get; set; }
    public VideoRuleOperator Operator { get; set; } = VideoRuleOperator.Is;
    public List<string> Values { get; set; } = new();

    public VideoRuleCondition Clone() => new() { Field = Field, Operator = Operator, Values = new(Values ?? []) };
}

/// <summary>Normalized, serialization-friendly source properties consumed by the pure rule evaluator.</summary>
public sealed class VideoSourceFacts
{
    public string? Codec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? ResolutionClass { get; init; }
    public long? BitrateKbps { get; init; }
    public long? FileSizeBytes { get; init; }
    public double? DurationSeconds { get; init; }
    public string? PixelFormat { get; init; }
    public int? BitDepth { get; init; }
    public bool? IsHdr { get; init; }
    public bool? Is4K { get; init; }

    public static VideoSourceFacts From(MediaFile file) => Create(
        file.Codec,
        file.Width,
        file.Height,
        file.Bitrate,
        file.FileSize,
        file.Duration,
        file.PixelFormat,
        null,
        file.IsHdr,
        file.Is4K);

    public static VideoSourceFacts From(WorkItem item)
    {
        var video = item.Probe?.Streams.FirstOrDefault(s => s.CodecType == "video");
        return Create(
            video?.CodecName ?? item.SourceCodec ?? (item.IsHevc ? "hevc" : null),
            video?.Width ?? item.SourceWidth,
            video?.Height ?? item.SourceHeight,
            item.Bitrate,
            item.Size,
            item.Length,
            video?.PixFmt ?? item.SourcePixelFormat,
            ParsePositiveInt(video?.BitsPerRawSample),
            item.Probe == null ? item.SourceIsHdr : ProbeIsHdr(item.Probe),
            item.Is4K);
    }

    public static VideoSourceFacts From(ProbeResult probe, long fileSizeBytes, long bitrateKbps, double durationSeconds)
    {
        var video = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        return Create(
            video?.CodecName,
            video?.Width,
            video?.Height,
            bitrateKbps,
            fileSizeBytes,
            durationSeconds,
            video?.PixFmt,
            ParsePositiveInt(video?.BitsPerRawSample),
            ProbeIsHdr(probe),
            video == null ? null : video.Width > 1920);
    }

    private static VideoSourceFacts Create(
        string? codec, int? width, int? height, long? bitrate, long? size, double? duration,
        string? pixelFormat, int? explicitDepth, bool? hdr, bool? is4K)
    {
        width    = width > 0 ? width : null;
        height   = height > 0 ? height : null;
        bitrate  = bitrate > 0 ? bitrate : null;
        size     = size > 0 ? size : null;
        duration = duration > 0 ? duration : null;

        int? shortEdge = width.HasValue && height.HasValue ? Math.Min(width.Value, height.Value) : null;
        return new VideoSourceFacts
        {
            Codec           = string.IsNullOrWhiteSpace(codec) ? null : NormalizeCodec(codec),
            Width           = width,
            Height          = height,
            ResolutionClass = shortEdge switch
            {
                null     => null,
                < 720    => "sd",
                < 1080   => "720p",
                < 1440   => "1080p",
                < 2160   => "1440p",
                _        => "2160p+",
            },
            BitrateKbps    = bitrate,
            FileSizeBytes  = size,
            DurationSeconds = duration,
            PixelFormat    = string.IsNullOrWhiteSpace(pixelFormat) ? null : pixelFormat.Trim().ToLowerInvariant(),
            BitDepth       = explicitDepth ?? InferBitDepth(pixelFormat),
            IsHdr          = hdr,
            Is4K           = is4K ?? (width.HasValue && width.Value > 1920),
        };
    }

    public static string NormalizeCodec(string? codec) => (codec ?? "").Trim().ToLowerInvariant() switch
    {
        "hevc" or "h.265" or "h265" or "x265" => "h265",
        "avc" or "avc1" or "h.264" or "h264" or "x264" => "h264",
        "av01" or "av1" => "av1",
        var value => value,
    };

    private static int? InferBitDepth(string? pixelFormat)
    {
        if (string.IsNullOrWhiteSpace(pixelFormat)) return null;
        var normalized = pixelFormat.Trim();
        // Covers planar names (yuv420p10le, gbrp12le) and FFmpeg's
        // semi-planar hardware names (p010le/p210le/p410le, p016le, …).
        var match = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"p(?:[024])?(?<depth>9|10|12|14|16)(?:le|be)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // Packed/gray families put the component depth immediately before
            // endianness (gray10le, x2rgb10le, y210le). v210/v410 are the
            // historical exception and are always 10-bit.
            if (normalized.Equals("v210", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("v410", StringComparison.OrdinalIgnoreCase)) return 10;
            match = System.Text.RegularExpressions.Regex.Match(
                normalized,
                @"(?:gray|rgb|bgr|gbr|y)[a-z0-9_]*?(?<depth>9|10|12|14|16)(?:le|be)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return match.Success && int.TryParse(match.Groups["depth"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth)
            ? depth
            : 8;
    }

    private static int? ParsePositiveInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : null;

    private static bool ProbeIsHdr(ProbeResult probe) => probe.Streams.Any(stream =>
        stream.CodecType == "video" &&
        (string.Equals(stream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase)
         || string.Equals(stream.ColorTransfer, "arib-std-b67", StringComparison.OrdinalIgnoreCase)
         || string.Equals(stream.ColorPrimaries, "bt2020", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Serializable explanation of the advanced decision attached to local and remote jobs.</summary>
public sealed class VideoJobPlan
{
    public const int CurrentProtocolVersion = 1;

    public int ProtocolVersion { get; set; } = CurrentProtocolVersion;
    public AdvancedVideoAction Action { get; set; } = AdvancedVideoAction.UseSimpleSettings;
    public Guid? RuleId { get; set; }
    public string? RuleName { get; set; }
    public Guid? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public VideoEncodingProfile? Profile { get; set; }
    public string? ExplicitEncoder { get; set; }
    public VideoOutputRetention OutputRetention { get; set; } = VideoOutputRetention.SmallerOnly;
    public string? BlockingReason { get; set; }
    public List<string> Warnings { get; set; } = new();

    [JsonIgnore]
    // A matched rule is useful provenance, but a rule whose resolved action is
    // UseSimpleSettings still produces a legacy-compatible job. Keeping that job
    // eligible for protocol-v0 workers is part of the cluster compatibility contract.
    public bool IsAdvanced => Action != AdvancedVideoAction.UseSimpleSettings || Profile != null;

    public VideoJobPlan Clone() => new()
    {
        ProtocolVersion = ProtocolVersion,
        Action           = Action,
        RuleId           = RuleId,
        RuleName         = RuleName,
        ProfileId        = ProfileId,
        ProfileName      = ProfileName,
        Profile          = Profile?.Clone(),
        ExplicitEncoder  = ExplicitEncoder,
        OutputRetention  = OutputRetention,
        BlockingReason   = BlockingReason,
        Warnings         = new(Warnings ?? []),
    };
}

public sealed class VideoPolicyResolution
{
    public EncoderOptions Options { get; init; } = new();
    public VideoJobPlan Plan { get; init; } = new();
}
