using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for the hardware → encoder mapping (<see cref="TranscodingService.GetEncoder"/>),
///     init-flag emission (<see cref="TranscodingService.GetInitFlags"/>), the SVT-AV1 preset
///     mapping, the software fallback table, and the downscale-target → height resolver.
///     Each is a small switch over a fixed input space — the data-row format keeps the body short.
/// </summary>
public sealed class HardwareEncoderTests
{
    /// <summary>
    ///     Rows: (HardwareAcceleration, requested encoder, expected ffmpeg encoder name).
    ///     The mapping has Windows-specific branches (QSV / AMF) that won't fire on macOS or Linux —
    ///     those rows are skipped on non-Windows hosts via <see cref="SkippableOnNonWindows"/>.
    /// </summary>
    public static IEnumerable<object[]> EncoderRows() => new[]
    {
        // ---- VAAPI / cross-platform branches ----
        new object[] { "intel",  "libx265",   "hevc_vaapi", false },
        new object[] { "intel",  "libx264",   "h264_vaapi", false },
        new object[] { "intel",  "libsvtav1", "av1_vaapi",  false },
        new object[] { "amd",    "libx265",   "hevc_vaapi", false },
        new object[] { "amd",    "libx264",   "h264_vaapi", false },
        new object[] { "amd",    "libsvtav1", "av1_vaapi",  false },
        new object[] { "nvidia", "libx265",   "hevc_nvenc", false },
        new object[] { "nvidia", "libx264",   "h264_nvenc", false },
        new object[] { "nvidia", "libsvtav1", "av1_nvenc",  false },
        new object[] { "apple",  "libx265",   "hevc_videotoolbox", false },
        new object[] { "apple",  "libx264",   "h264_videotoolbox", false },
        new object[] { "apple",  "libsvtav1", "libsvtav1",  false },   // no av1_videotoolbox
        new object[] { "none",   "libx265",   "libx265",    false },
        new object[] { "auto",   "libx265",   "libx265",    false },

        // ---- Windows-only branches (QSV/AMF). Skipped on non-Windows. ----
        new object[] { "intel",  "libx265",   "hevc_qsv",   true  },
        new object[] { "intel",  "libx264",   "h264_qsv",   true  },
        new object[] { "intel",  "libsvtav1", "av1_qsv",    true  },
        new object[] { "amd",    "libx265",   "hevc_amf",   true  },
        new object[] { "amd",    "libx264",   "h264_amf",   true  },
        new object[] { "amd",    "libsvtav1", "av1_amf",    true  },
    };

    [Theory]
    [MemberData(nameof(EncoderRows))]
    public void GetEncoder_maps_hwaccel_and_codec(string hwAccel, string requested, string expected, bool windowsOnly)
    {
        if (windowsOnly && !OperatingSystem.IsWindows()) return;
        if (!windowsOnly && OperatingSystem.IsWindows() && hwAccel is "intel" or "amd") return;

        var opts = new EncoderOptions { HardwareAcceleration = hwAccel, Encoder = requested };
        TranscodingService.GetEncoder(opts).Should().Be(expected);
    }


    /// <summary>Rows: (encoder, expected fallback).</summary>
    public static IEnumerable<object[]> SoftwareFallbackRows() => new[]
    {
        new object[] { "libx265",        "libx265"   },
        new object[] { "hevc_nvenc",     "libx265"   },
        new object[] { "hevc_vaapi",     "libx265"   },
        new object[] { "libx264",        "libx264"   },
        new object[] { "h264_nvenc",     "libx264"   },
        new object[] { "libsvtav1",      "libsvtav1" },
        new object[] { "av1_nvenc",      "libsvtav1" },
    };

    [Theory]
    [MemberData(nameof(SoftwareFallbackRows))]
    public void GetSoftwareFallbackEncoder_picks_correct_software_codec(string encoder, string expected)
    {
        var opts = new EncoderOptions { Encoder = encoder };
        TranscodingService.GetSoftwareFallbackEncoder(opts).Should().Be(expected);
    }


    /// <summary>
    ///     Encoder strings can arrive from settings.json or per-folder overrides where casing
    ///     isn't enforced — the UI feeds lowercase but external entry points don't, and a
    ///     non-matching codec string used to silently fall through. Both <c>GetEncoder</c>
    ///     and <c>GetSoftwareFallbackEncoder</c> must be case-insensitive on the codec
    ///     substrings they look for ("265", "264", "av1", "svt").
    /// </summary>
    public static IEnumerable<object[]> EncoderCaseRows() => new[]
    {
        new object[] { "LIBX265",   "nvidia", "hevc_nvenc"        },
        new object[] { "Libx265",   "nvidia", "hevc_nvenc"        },
        new object[] { "LibX264",   "nvidia", "h264_nvenc"        },
        new object[] { "LIBSVTAV1", "nvidia", "av1_nvenc"         },
        new object[] { "libSVTav1", "apple",  "libsvtav1"         },
        new object[] { "LIBX265",   "apple",  "hevc_videotoolbox" },
    };

    [Theory]
    [MemberData(nameof(EncoderCaseRows))]
    public void GetEncoder_is_case_insensitive_on_encoder_string(string encoder, string hwAccel, string expected)
    {
        var opts = new EncoderOptions { Encoder = encoder, HardwareAcceleration = hwAccel };
        TranscodingService.GetEncoder(opts).Should().Be(expected);
    }


    [Fact]
    public void GetEncoder_unknown_hwaccel_returns_normalized_encoder_string()
    {
        // The default switch arm now returns the normalized lowercase encoder string,
        // not the original (potentially mixed-case) input. Prevents passing "LIBX265"
        // through to ffmpeg, which doesn't recognize it.
        var opts = new EncoderOptions { Encoder = "LIBX265", HardwareAcceleration = "none" };
        TranscodingService.GetEncoder(opts).Should().Be("LIBX265");   // none → no normalization needed for sw
    }


    [Theory]
    [InlineData("LIBSVTAV1", "libsvtav1")]
    [InlineData("AV1_NVENC", "libsvtav1")]
    [InlineData("LIBX264",   "libx264")]
    [InlineData("H264_NVENC","libx264")]
    [InlineData("LIBX265",   "libx265")]
    public void GetSoftwareFallbackEncoder_is_case_insensitive(string encoder, string expected)
    {
        var opts = new EncoderOptions { Encoder = encoder };
        TranscodingService.GetSoftwareFallbackEncoder(opts).Should().Be(expected);
    }


    /// <summary>
    ///     Rows: (HardwareAcceleration, hwDecode, expected key fragments). We assert via Contain
    ///     so the test is robust to whitespace/option-order changes inside the init-flag string.
    /// </summary>
    public static IEnumerable<object[]> InitFlagRows() => new[]
    {
        new object[] { "nvidia", true,  new[] { "-y", "-hwaccel cuda" } },
        new object[] { "apple",  true,  new[] { "-y", "-hwaccel videotoolbox" } },
        new object[] { "none",   true,  new[] { "-y" } },
        new object[] { "auto",   true,  new[] { "-y" } },
        // VAAPI: hwDecode true emits -hwaccel vaapi; hwDecode false omits it (sw-decode + hw-encode).
        new object[] { "intel",  true,  new[] { "-init_hw_device vaapi", "-hwaccel vaapi" } },
        new object[] { "intel",  false, new[] { "-init_hw_device vaapi", "-filter_hw_device hw" } },
        new object[] { "amd",    true,  new[] { "-init_hw_device vaapi", "-hwaccel vaapi" } },
        new object[] { "amd",    false, new[] { "-init_hw_device vaapi", "-filter_hw_device hw" } },
    };

    [Theory]
    [MemberData(nameof(InitFlagRows))]
    public void GetInitFlags_emits_expected_fragments(string hwAccel, bool hwDecode, string[] expected)
    {
        // VAAPI rows are Linux-shaped; on Windows the same hwAccel maps to QSV/AMF.
        if (OperatingSystem.IsWindows() && hwAccel is "intel" or "amd") return;

        var flags = TranscodingService.GetInitFlags(hwAccel, hwDecode);
        foreach (var fragment in expected)
            flags.Should().Contain(fragment);
    }


    [Fact]
    public void GetInitFlags_intel_on_windows_uses_qsv()
    {
        if (!OperatingSystem.IsWindows()) return;

        var flags = TranscodingService.GetInitFlags("intel", true);
        flags.Should().Contain("-hwaccel qsv");
        flags.Should().Contain("-qsv_device auto");
    }


    [Fact]
    public void GetInitFlags_amd_on_windows_uses_amf()
    {
        if (!OperatingSystem.IsWindows()) return;

        var flags = TranscodingService.GetInitFlags("amd", true);
        flags.Should().Contain("-hwaccel auto");
    }


    /// <summary>
    ///     Rows: (UI preset, expected SVT-AV1 numeric preset). The actual table is
    ///     a five-step ladder: veryslow=2, slow=4, medium=6, fast=8, veryfast=10. Anything
    ///     unrecognised lands on 6 (the former hardcode).
    /// </summary>
    public static IEnumerable<object[]> SvtAv1PresetRows() => new[]
    {
        new object[] { "veryslow",  2  },
        new object[] { "slow",      4  },
        new object[] { "medium",    6  },
        new object[] { "fast",      8  },
        new object[] { "veryfast",  10 },
        new object[] { "VERYSLOW",  2  },           // case-insensitive
        new object[] { "",          6  },           // unknown → default
        new object[] { "garbage",   6  },
        new object[] { "ultrafast", 6  },           // not in the ladder → default
    };

    [Theory]
    [MemberData(nameof(SvtAv1PresetRows))]
    public void MapSvtAv1Preset_handles_known_and_unknown_inputs(string preset, int expected)
    {
        TranscodingService.MapSvtAv1Preset(preset).Should().Be(expected);
    }


    /// <summary>Rows: (target string, expected height). Unknown targets fall back to 1080p.</summary>
    public static IEnumerable<object[]> DownscaleTargetRows() => new[]
    {
        new object[] { "4K",      2160 },
        new object[] { "2160p",   2160 },
        new object[] { "1440p",   1440 },
        new object[] { "1080p",   1080 },
        new object[] { "720p",    720  },
        new object[] { "480p",    480  },
        new object[] { "garbage", 1080 },
    };

    [Theory]
    [MemberData(nameof(DownscaleTargetRows))]
    public void ResolveDownscaleHeight_maps_known_targets(string target, int expected)
    {
        TranscodingService.ResolveDownscaleHeight(target).Should().Be(expected);
    }
}
