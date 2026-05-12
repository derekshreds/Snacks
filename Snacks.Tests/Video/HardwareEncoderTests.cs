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


    /// <summary>
    ///     Linux Intel with the QSV preference set picks QSV encoders instead of VAAPI.
    ///     Uses the pure overload so this runs on any host — VAAPI vs QSV here depends
    ///     on detection, not the test process's OS.
    /// </summary>
    [Theory]
    [InlineData("libx265",   "hevc_qsv")]
    [InlineData("libx264",   "h264_qsv")]
    [InlineData("libsvtav1", "av1_qsv")]
    public void GetEncoder_intel_on_linux_with_qsv_picks_qsv_variant(string requested, string expected)
    {
        var opts = new EncoderOptions { HardwareAcceleration = "intel", Encoder = requested };
        TranscodingService.GetEncoder(opts, isWindows: false, linuxIntelQsv: true).Should().Be(expected);
    }


    /// <summary>
    ///     Linux Intel without QSV (probe failed → backend stayed VAAPI) keeps the VAAPI
    ///     encoders. Regression guard for the existing VAAPI fallback path.
    /// </summary>
    [Theory]
    [InlineData("libx265",   "hevc_vaapi")]
    [InlineData("libx264",   "h264_vaapi")]
    [InlineData("libsvtav1", "av1_vaapi")]
    public void GetEncoder_intel_on_linux_without_qsv_picks_vaapi_variant(string requested, string expected)
    {
        var opts = new EncoderOptions { HardwareAcceleration = "intel", Encoder = requested };
        TranscodingService.GetEncoder(opts, isWindows: false, linuxIntelQsv: false).Should().Be(expected);
    }


    /// <summary>
    ///     Linux QSV preference must not bleed into AMD or NVIDIA paths — only Intel
    ///     swaps backend. Catches a regression where the QSV gate accidentally widens.
    /// </summary>
    [Theory]
    [InlineData("amd",    "libx265",   "hevc_vaapi")]
    [InlineData("amd",    "libx264",   "h264_vaapi")]
    [InlineData("amd",    "libsvtav1", "av1_vaapi")]
    [InlineData("nvidia", "libx265",   "hevc_nvenc")]
    public void GetEncoder_linux_qsv_does_not_affect_non_intel(string hwAccel, string requested, string expected)
    {
        var opts = new EncoderOptions { HardwareAcceleration = hwAccel, Encoder = requested };
        TranscodingService.GetEncoder(opts, isWindows: false, linuxIntelQsv: true).Should().Be(expected);
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
    ///     Path-aware overload: when a render-node path is supplied, the four VAAPI
    ///     branches must use it instead of the legacy renderD128 default. Covers the
    ///     hybrid-laptop scenario where the iGPU lands on /dev/dri/renderD129 because
    ///     the NVIDIA card claimed renderD128.
    /// </summary>
    [Theory]
    [InlineData("intel", true)]
    [InlineData("intel", false)]
    [InlineData("amd",   true)]
    [InlineData("amd",   false)]
    public void GetInitFlags_uses_supplied_device_path(string hwAccel, bool hwDecode)
    {
        // isWindows: false — VAAPI branches only fire on Linux. Force the OS gate so
        // this test runs identically on every CI host.
        var flags = TranscodingService.GetInitFlags(hwAccel, "/dev/dri/renderD129", isWindows: false, hwDecode);
        flags.Should().Contain("vaapi=hw:/dev/dri/renderD129");
        flags.Should().NotContain("vaapi=hw:/dev/dri/renderD128");
    }


    /// <summary>
    ///     Path-aware overload with a null path must produce the same flag string the
    ///     legacy single-arg overload always has. Regression guard for single-GPU
    ///     hosts — they should be byte-identical to the pre-fix output.
    /// </summary>
    [Theory]
    [InlineData("intel", true)]
    [InlineData("intel", false)]
    [InlineData("amd",   true)]
    [InlineData("amd",   false)]
    public void GetInitFlags_null_path_falls_back_to_renderD128(string hwAccel, bool hwDecode)
    {
        var withNull   = TranscodingService.GetInitFlags(hwAccel, devicePath: null, isWindows: false, hwDecode);
        var legacyFlag = TranscodingService.GetInitFlags(hwAccel, isWindows: false, hwDecode);
        withNull.Should().Be(legacyFlag);
        withNull.Should().Contain("vaapi=hw:/dev/dri/renderD128");
    }


    /// <summary>
    ///     Linux Intel QSV with hw-decode capable input uses the full QSV pipeline on the
    ///     detected render node — not the legacy renderD128 default — and never falls
    ///     through to the VAAPI branch.
    /// </summary>
    [Fact]
    public void GetInitFlags_intel_on_linux_with_qsv_uses_qsv_pipeline()
    {
        var flags = TranscodingService.GetInitFlags("intel", "/dev/dri/renderD129", isWindows: false, linuxIntelQsv: true, hwDecode: true);
        flags.Should().Contain("-hwaccel qsv");
        flags.Should().Contain("-hwaccel_output_format qsv");
        flags.Should().Contain("-qsv_device /dev/dri/renderD129");
        flags.Should().NotContain("vaapi");
    }


    /// <summary>
    ///     Linux Intel QSV with software-decoded input still needs a QSV device for the
    ///     encoder. The canonical FFmpeg form derives QSV from a VAAPI base on the same
    ///     render node, so the encoder finds a QSV context even though decode runs in
    ///     software.
    /// </summary>
    [Fact]
    public void GetInitFlags_intel_on_linux_with_qsv_and_sw_decode_derives_qsv_from_vaapi()
    {
        var flags = TranscodingService.GetInitFlags("intel", "/dev/dri/renderD129", isWindows: false, linuxIntelQsv: true, hwDecode: false);
        flags.Should().Contain("vaapi=va:/dev/dri/renderD129");
        flags.Should().Contain("qsv=hw@va");
        flags.Should().Contain("-filter_hw_device hw");
        flags.Should().NotContain("-hwaccel qsv");
    }


    /// <summary>
    ///     Linux QSV preference is Intel-only. AMD jobs on Linux must still pick the
    ///     VAAPI pipeline regardless of the QSV flag, since the flag describes Intel's
    ///     backend choice — not a global Linux preference.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GetInitFlags_linux_qsv_does_not_affect_amd(bool hwDecode)
    {
        var flags = TranscodingService.GetInitFlags("amd", "/dev/dri/renderD128", isWindows: false, linuxIntelQsv: true, hwDecode);
        flags.Should().Contain("vaapi=hw:/dev/dri/renderD128");
        flags.Should().NotContain("-hwaccel qsv");
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


    /// <summary>
    ///     Rows: (source codec, expected cuvid flag). The cuvid mapping forces NVDEC for
    ///     codecs FFmpeg's cuvid family supports; everything else (unknown/unsupported)
    ///     returns empty so the caller falls back to <c>-hwaccel cuda</c>'s auto-attach.
    /// </summary>
    public static IEnumerable<object[]> CuvidDecoderRows() => new[]
    {
        new object[] { "h264",       "-c:v h264_cuvid"  },
        new object[] { "hevc",       "-c:v hevc_cuvid"  },
        new object[] { "av1",        "-c:v av1_cuvid"   },
        new object[] { "vp9",        "-c:v vp9_cuvid"   },
        new object[] { "vp8",        "-c:v vp8_cuvid"   },
        new object[] { "vc1",        "-c:v vc1_cuvid"   },
        new object[] { "mpeg2video", "-c:v mpeg2_cuvid" },
        new object[] { "mpeg4",      "-c:v mpeg4_cuvid" },
        new object[] { "mjpeg",      "-c:v mjpeg_cuvid" },
        // Case-insensitive — codec strings can come back from ffprobe in any case.
        new object[] { "HEVC",       "-c:v hevc_cuvid"  },
        new object[] { "H264",       "-c:v h264_cuvid"  },
        // Unknown / unsupported codecs return empty so the caller leaves the existing
        // -hwaccel cuda init flags alone.
        new object[] { "prores",     ""                  },
        new object[] { "ffv1",       ""                  },
        new object[] { "h264_mvc",   ""                  },   // 3D Blu-ray — not a cuvid input
        new object[] { "",           ""                  },
        new object[] { null!,        ""                  },
    };

    [Theory]
    [MemberData(nameof(CuvidDecoderRows))]
    public void GetNvidiaInputDecoder_maps_codec_to_cuvid_flag(string? sourceCodec, string expected)
    {
        TranscodingService.GetNvidiaInputDecoder(sourceCodec).Should().Be(expected);
    }
}
