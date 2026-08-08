using System.Globalization;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Encoder-family knowledge used by validation, the UI catalog, and command generation.
///     Unknown runtime encoders remain usable in Custom mode instead of being hidden.
/// </summary>
public static class VideoEncoderRegistry
{
    public sealed record Descriptor(
        string Family,
        string Codec,
        string QualityLabel,
        double QualityMin,
        double QualityMax,
        bool SupportsTypedQuality,
        bool SupportsQualityConstraints,
        string DeviceId,
        IReadOnlyList<string> Presets,
        IReadOnlyList<string> PixelFormats);

    /// <summary>
    ///     Every encoder with a typed adapter, offered for authoring even when no
    ///     connected slot advertises it yet. Exact-encoder profiles are portable:
    ///     they may be written on one box for a worker that joins later, so the
    ///     picker must not be limited to what happens to be detected right now.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownEncoders =
    [
        "libx264", "libx265", "libsvtav1", "libaom-av1", "librav1e",
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_qsv", "hevc_qsv", "av1_qsv",
        "h264_vaapi", "hevc_vaapi", "av1_vaapi",
        "h264_amf", "hevc_amf", "av1_amf",
        "h264_videotoolbox", "hevc_videotoolbox",
    ];

    private static readonly string[] SoftwarePresets = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    private static readonly string[] HardwarePresets = ["fast", "medium", "slow"];
    private static readonly string[] CommonPixelFormats = ["yuv420p", "yuv420p10le"];

    public static Descriptor Describe(string encoder, string? configuredCodec = null)
    {
        var value = (encoder ?? "").Trim().ToLowerInvariant();
        var codec = EncoderCodec(value) ?? VideoSourceFacts.NormalizeCodec(configuredCodec);

        if (value == "libx264")
            return Known("x264", "h264", "CRF", 0, 51, "cpu", SoftwarePresets);
        if (value == "libx265")
            return Known("x265", "h265", "CRF", 0, 51, "cpu", SoftwarePresets);
        if (value is "libsvtav1" or "libsvt_av1")
            return Known("svt-av1", "av1", "CRF", 0, 63, "cpu", Enumerable.Range(0, 14).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        if (value is "libaom-av1" or "libaom_av1")
            return Known("libaom", "av1", "CQ/CRF", 0, 63, "cpu", Enumerable.Range(0, 9).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        if (value is "librav1e" or "rav1e")
            return Known("rav1e", "av1", "Quantizer", 0, 255, "cpu", Enumerable.Range(0, 11).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());

        if (value.EndsWith("_nvenc", StringComparison.Ordinal))
            return Known("nvenc", codec, "CQ", 0, 51, "nvidia", ["p1", "p2", "p3", "p4", "p5", "p6", "p7"]);
        if (value.EndsWith("_qsv", StringComparison.Ordinal))
            return Known("qsv", codec, "ICQ", 1, 51, "intel", HardwarePresets);
        if (value.EndsWith("_vaapi", StringComparison.Ordinal))
            return Known("vaapi", codec, "Global quality", 1, 51, InferVaapiVendor(value), []);
        if (value.EndsWith("_amf", StringComparison.Ordinal))
            return Known("amf", codec, "QP", 0, 51, "amd", ["speed", "balanced", "quality"]);
        if (value.EndsWith("_videotoolbox", StringComparison.Ordinal))
            return Known("videotoolbox", codec, "Quality", 0, 100, "apple", []);

        return new Descriptor("custom", codec, "Custom", 0, 0, false, false, InferDevice(value), [], []);
    }

    private static Descriptor Known(string family, string codec, string label, double min, double max, string device, IReadOnlyList<string> presets) =>
        new(family, codec, label, min, max, true,
            family is "x264" or "x265" or "svt-av1" or "libaom" or "nvenc",
            device, presets, CommonPixelFormats);

    public static string? EncoderCodec(string? encoder)
    {
        var value = (encoder ?? "").Trim().ToLowerInvariant();
        if (value.Contains("av1", StringComparison.Ordinal) || value.Contains("rav1e", StringComparison.Ordinal)) return "av1";
        if (value.Contains("265", StringComparison.Ordinal) || value.Contains("hevc", StringComparison.Ordinal)) return "h265";
        if (value.Contains("264", StringComparison.Ordinal) || value.Contains("avc", StringComparison.Ordinal)) return "h264";
        return null;
    }

    public static string DefaultSoftwareEncoder(string codec) => VideoSourceFacts.NormalizeCodec(codec) switch
    {
        "av1"  => "libsvtav1",
        "h264" => "libx264",
        _      => "libx265",
    };

    public static bool IsSoftware(string encoder) => Describe(encoder).DeviceId == "cpu";

    public static string InferDevice(string encoder)
    {
        var value = (encoder ?? "").ToLowerInvariant();
        if (value.Contains("nvenc")) return "nvidia";
        if (value.Contains("qsv")) return "intel";
        if (value.Contains("amf")) return "amd";
        if (value.Contains("videotoolbox")) return "apple";
        if (value.Contains("vaapi")) return InferVaapiVendor(value);
        return "cpu";
    }

    private static string InferVaapiVendor(string encoder) => encoder.Contains("amd", StringComparison.OrdinalIgnoreCase) ? "amd" : "intel";

    /// <summary>Builds only profile-owned video options. Mapping/audio/subtitle/output remain with the caller.</summary>
    public static IReadOnlyList<string> BuildProfileArguments(VideoEncodingProfile profile, string encoder)
    {
        var args = new List<string>();
        var descriptor = Describe(encoder, profile.Codec);
        var rc = profile.RateControl ?? new VideoRateControlOptions();

        switch (rc.Mode)
        {
            case VideoRateControlMode.Bitrate:
                AddBitrate(args, rc, descriptor);
                break;
            case VideoRateControlMode.Quality:
                AddQuality(args, rc, descriptor);
                break;
            case VideoRateControlMode.Custom:
                break;
        }

        if (!string.IsNullOrWhiteSpace(profile.Preset))
        {
            string? presetOption = descriptor.Family switch
            {
                "libaom" => "-cpu-used",
                "rav1e"  => "-speed",
                "amf"     => "-quality",
                "vaapi" or "videotoolbox" => null,
                _        => "-preset",
            };
            if (presetOption != null) Add(args, presetOption, profile.Preset.Trim());
        }
        if (profile.Threads > 0) Add(args, "-threads", profile.Threads.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(profile.PixelFormat)) Add(args, "-pix_fmt", profile.PixelFormat.Trim());
        if (profile.GopSize > 0) Add(args, "-g", profile.GopSize.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(profile.VideoProfile)) Add(args, "-profile:v", profile.VideoProfile.Trim());
        if (!string.IsNullOrWhiteSpace(profile.VideoLevel)) Add(args, "-level:v", profile.VideoLevel.Trim());

        foreach (var custom in profile.CustomOptions ?? [])
        {
            if (custom == null) continue;
            args.Add(custom.Option.Trim());
            args.AddRange(custom.Values ?? []);
        }
        return args;
    }

    private static void AddBitrate(List<string> args, VideoRateControlOptions rc, Descriptor descriptor)
    {
        Add(args, "-b:v", $"{rc.TargetKbps}k");
        int? min = rc.StrictBitrate ? rc.TargetKbps : rc.MinKbps;
        int? max = rc.StrictBitrate ? rc.TargetKbps : rc.MaxKbps;
        int? buffer = rc.BufferKbits ?? (rc.StrictBitrate ? rc.TargetKbps * 2 : null);
        if (min > 0) Add(args, "-minrate", $"{min}k");
        if (max > 0) Add(args, "-maxrate", $"{max}k");
        if (buffer > 0) Add(args, "-bufsize", $"{buffer}k");

        if (descriptor.Family == "nvenc") Add(args, "-rc", rc.StrictBitrate ? "cbr" : "vbr");
        else if (descriptor.Family == "vaapi") Add(args, "-rc_mode", rc.StrictBitrate ? "CBR" : "VBR");
        else if (descriptor.Family == "amf") Add(args, "-rc", rc.StrictBitrate ? "cbr" : "vbr_peak");
        else if (descriptor.Family == "svt-av1") Add(args, "-svtav1-params", rc.StrictBitrate ? "rc=2" : "rc=1");
    }

    private static void AddQuality(List<string> args, VideoRateControlOptions rc, Descriptor descriptor)
    {
        var q = rc.Quality.ToString("0.###", CultureInfo.InvariantCulture);
        switch (descriptor.Family)
        {
            case "libaom":
                Add(args, "-crf", q);
                Add(args, "-b:v", "0");
                break;
            case "rav1e":
                Add(args, "-qp", q);
                break;
            case "nvenc":
                Add(args, "-rc", "vbr");
                Add(args, "-cq", q);
                Add(args, "-b:v", "0");
                break;
            case "qsv":
                Add(args, "-global_quality", q);
                break;
            case "vaapi":
                Add(args, "-rc_mode", "CQP");
                Add(args, "-global_quality:v", q);
                break;
            case "amf":
                Add(args, "-rc", "cqp");
                Add(args, "-qp_i", q);
                Add(args, "-qp_p", q);
                break;
            case "videotoolbox":
                Add(args, "-q:v", q);
                break;
            default:
                Add(args, "-crf", q);
                break;
        }
        if (descriptor.SupportsQualityConstraints)
        {
            if (rc.MaxKbps > 0) Add(args, "-maxrate", $"{rc.MaxKbps}k");
            if (rc.BufferKbits > 0) Add(args, "-bufsize", $"{rc.BufferKbits}k");
        }
    }

    private static void Add(List<string> args, string option, string value)
    {
        args.Add(option);
        args.Add(value);
    }
}
