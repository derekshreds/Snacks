using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for the smaller pure helpers that emit the per-encoder rate-control flag
///     fragments (<see cref="TranscodingService.GetForcedReencodeCompressionFlags"/>),
///     compute the FFmpeg <c>scale=</c> expression
///     (<see cref="TranscodingService.ComputeScaleExpr"/>), and decide whether VAAPI
///     hardware decode is supported for a given source codec
///     (<see cref="TranscodingService.CanVaapiDecode"/>).
/// </summary>
public sealed class RateControlAndScaleTests
{
    // =====================================================================
    //  GetForcedReencodeCompressionFlags — per-encoder rate-control branches.
    //
    //  Asserts via Contain on key flag fragments rather than full-string
    //  equality so reformatting (whitespace, ordering) doesn't break tests.
    // =====================================================================

    [Fact]
    public void Vaapi_path_uses_cqp_with_global_quality()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_vaapi", useVaapi: true, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-rc_mode CQP");
        flags.Should().Contain("-global_quality:v 25");
        // VAAPI doesn't drive bitrate via -b:v in this path (CQP is quality-based).
        flags.Should().NotContain("-b:v");
    }


    [Fact]
    public void Svtav1_path_uses_svtav1_params_with_5_percent_padded_target()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "libsvtav1", useVaapi: false, isSvtAv1: true,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-svtav1-params");
        flags.Should().Contain("rc=1");
        flags.Should().Contain("-b:v 3675k");   // 3500 * 1.05
    }


    [Fact]
    public void Nvenc_path_emits_vbr_with_lookahead_and_aq_flags()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_nvenc", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-rc vbr");
        flags.Should().Contain("-rc-lookahead 32");
        flags.Should().Contain("-spatial_aq 1");
        flags.Should().Contain("-temporal_aq 1");
        flags.Should().Contain("-b:v 3500k");
        flags.Should().Contain("-maxrate 4000k");
        flags.Should().Contain("-bufsize 8000k");   // 4000 * 2
    }


    [Fact]
    public void Nvenc_conservative_path_drops_temporal_aq()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_nvenc", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: true);

        flags.Should().Contain("-spatial_aq 1");
        flags.Should().NotContain("-temporal_aq");
    }


    [Theory]
    [InlineData("hevc_amf")]
    // av1_amf accepts the same RC surface as h264/hevc AMF (verified against
    // FFmpeg 7.1 amfenc_av1.c: both enforce_hrd and rc=vbr_peak are defined).
    [InlineData("av1_amf")]
    public void Amf_path_uses_vbr_peak_with_hrd_enforcement(string encoder)
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: encoder, useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-rc vbr_peak");
        flags.Should().Contain("-enforce_hrd 1");
        flags.Should().Contain("-b:v 3500k");
        flags.Should().Contain("-maxrate 4000k");
    }


    [Theory]
    [InlineData("h264_qsv")]
    // extbrc + look_ahead_depth are valid on hevc/av1 QSV too (verified against
    // FFmpeg 7.1 qsvenc_hevc.c/qsvenc_av1.c); the plain look_ahead flag is
    // h264-only and harmlessly ignored elsewhere.
    [InlineData("hevc_qsv")]
    [InlineData("av1_qsv")]
    public void Qsv_path_emits_lookahead_when_not_conservative(string encoder)
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: encoder, useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-extbrc 1");
        flags.Should().Contain("-look_ahead 1");
        flags.Should().Contain("-look_ahead_depth 40");
        flags.Should().Contain("-b:v 3500k");
    }


    [Fact]
    public void Qsv_conservative_path_drops_lookahead()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_qsv", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: true);

        flags.Should().NotContain("-look_ahead");
        flags.Should().NotContain("-extbrc");
        flags.Should().Contain("-b:v 3500k");
    }


    [Fact]
    public void Videotoolbox_path_emits_only_bitrate_triple()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_videotoolbox", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-b:v 3500k");
        flags.Should().Contain("-maxrate 4000k");
        flags.Should().Contain("-bufsize 8000k");
        // Apple has no rate-control mode flag — just the bitrate triple.
        flags.Should().NotContain("-rc ");
    }


    [Fact]
    public void Software_path_emits_minrate_maxrate_bufsize()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "libx265", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-b:v 3500k");
        flags.Should().Contain("-minrate 3300k");
        flags.Should().Contain("-maxrate 4000k");
        flags.Should().Contain("-bufsize 8000k");
    }


    // =====================================================================
    //  ComputeScaleExpr — emits the scale= filter expression for downscale,
    //  or null when downscaling shouldn't apply.
    // =====================================================================

    private static WorkItem MakeWorkItem(int height) => new()
    {
        Probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = height },
            },
        },
    };


    [Fact]
    public void ComputeScaleExpr_returns_null_when_policy_inactive()
    {
        var opts = new EncoderOptions { DownscalePolicy = "Never", DownscaleTarget = "1080p" };
        TranscodingService.ComputeScaleExpr(MakeWorkItem(2160), opts).Should().BeNull();
    }


    [Fact]
    public void ComputeScaleExpr_returns_null_when_source_height_unknown()
    {
        var opts = new EncoderOptions { DownscalePolicy = "Always", DownscaleTarget = "1080p" };
        TranscodingService.ComputeScaleExpr(MakeWorkItem(0), opts).Should().BeNull();
    }


    [Fact]
    public void ComputeScaleExpr_IfLarger_returns_null_when_source_at_or_below_target()
    {
        var opts = new EncoderOptions { DownscalePolicy = "IfLarger", DownscaleTarget = "1080p" };

        TranscodingService.ComputeScaleExpr(MakeWorkItem(1080), opts).Should().BeNull();
        TranscodingService.ComputeScaleExpr(MakeWorkItem(720),  opts).Should().BeNull();
    }


    [Theory]
    [InlineData("Always",      720,  "1080p", 1080)]
    [InlineData("Always",      2160, "1080p", 1080)]
    [InlineData("IfLarger",    2160, "1080p", 1080)]
    [InlineData("CapAtTarget", 2160, "720p",  720)]
    public void ComputeScaleExpr_emits_scale_expression_with_target_height(
        string policy, int sourceHeight, string target, int expectedH)
    {
        var opts = new EncoderOptions { DownscalePolicy = policy, DownscaleTarget = target };
        var expr = TranscodingService.ComputeScaleExpr(MakeWorkItem(sourceHeight), opts);

        expr.Should().NotBeNull();
        expr.Should().StartWith("scale=");
        expr.Should().Contain($"h={expectedH}");
        expr.Should().Contain("w=-2");        // preserve aspect ratio, even-width output
        expr.Should().Contain("flags=lanczos");
    }


    // =====================================================================
    //  CanVaapiDecode — VAAPI hardware decode is supported for h264, hevc,
    //  mpeg2video, vp8, vp9, and mjpeg on Elkhart Lake. Other codecs (av1,
    //  prores, etc.) fall through to software decode.
    // =====================================================================

    /// <summary>Rows: (source video codec, expected support).</summary>
    public static IEnumerable<object[]> VaapiDecodeRows() => new[]
    {
        new object[] { "h264",       true  },
        new object[] { "hevc",       true  },
        new object[] { "mpeg2video", true  },
        new object[] { "vp8",        true  },
        new object[] { "vp9",        true  },
        new object[] { "mjpeg",      true  },

        // Codecs not in the J6412 decode list
        new object[] { "av1",        false },
        new object[] { "prores",     false },
        new object[] { "vc1",        false },
        new object[] { "wmv3",       false },
        new object[] { "",           false },
    };

    [Theory]
    [MemberData(nameof(VaapiDecodeRows))]
    public void CanVaapiDecode(string codec, bool expected)
    {
        var probe = new ProbeResult
        {
            Streams = new[] { new Stream { Index = 0, CodecType = "video", CodecName = codec } },
        };
        TranscodingService.CanVaapiDecode(probe).Should().Be(expected);
    }


    [Fact]
    public void CanVaapiDecode_with_null_probe_returns_false()
    {
        TranscodingService.CanVaapiDecode(null).Should().BeFalse();
    }


    [Fact]
    public void CanVaapiDecode_with_no_video_stream_returns_false()
    {
        var probe = new ProbeResult { Streams = Array.Empty<Stream>() };
        TranscodingService.CanVaapiDecode(probe).Should().BeFalse();
    }


    // =====================================================================
    //  CanVaapiDecode with vainfo-detected capabilities — the parsed profile
    //  list overrides the Elkhart Lake baseline (e.g. AV1 decode on RDNA2+,
    //  no VP8 decode on radeonsi).
    // =====================================================================

    [Theory]
    [InlineData("av1",  true)]   // in detected set, not in baseline
    [InlineData("vp8",  false)]  // in baseline, not in detected set
    [InlineData("h264", true)]
    public void CanVaapiDecode_prefers_detected_capabilities(string codec, bool expected)
    {
        var amdRdna3 = new HashSet<string> { "h264", "hevc", "av1", "vp9", "mpeg2video", "mjpeg" };
        var probe = new ProbeResult
        {
            Streams = new[] { new Stream { Index = 0, CodecType = "video", CodecName = codec } },
        };
        TranscodingService.CanVaapiDecode(probe, amdRdna3).Should().Be(expected);
    }


    [Fact]
    public void ParseVaapiDecodeCodecs_reads_vld_entrypoints_only()
    {
        // Trimmed vainfo output shaped like radeonsi on RDNA3: AV1 has decode,
        // VP9 profile exists with decode, H264 has both decode and encode,
        // HEVC line with only EncSlice must NOT count as decodable.
        const string vainfo = """
            libva info: VA-API version 1.20.0
            vainfo: Supported profile and entrypoints
                  VAProfileH264Main               : VAEntrypointVLD
                  VAProfileH264Main               : VAEntrypointEncSlice
                  VAProfileHEVCMain               : VAEntrypointEncSlice
                  VAProfileAV1Profile0            : VAEntrypointVLD
                  VAProfileVP9Profile2            : VAEntrypointVLD
                  VAProfileJPEGBaseline           : VAEntrypointVLD
                  VAProfileNone                   : VAEntrypointVideoProc
            """;

        var codecs = TranscodingService.ParseVaapiDecodeCodecs(vainfo);

        codecs.Should().BeEquivalentTo(new[] { "h264", "av1", "vp9", "mjpeg" });
    }


    [Fact]
    public void ParseVaapiDecodeCodecs_returns_empty_for_error_output()
    {
        TranscodingService.ParseVaapiDecodeCodecs("libva error: failed to initialize display")
            .Should().BeEmpty();
    }


    // =====================================================================
    //  ComputeGop — ~2s keyframe interval from the probed frame rate.
    // =====================================================================

    [Theory]
    [InlineData(23.976, 48)]
    [InlineData(25.0,   50)]
    [InlineData(29.97,  60)]
    [InlineData(60.0,  120)]
    [InlineData(0.0,    48)]   // unknown rate → 24fps default
    [InlineData(5.0,    24)]   // implausibly low → floor of 24 frames
    [InlineData(1000.0, 48)]   // implausibly high → treated as unknown
    public void ComputeGop_targets_two_seconds(double fps, int expected)
    {
        TranscodingService.ComputeGop(fps > 0 ? fps : null).Should().Be(expected);
    }


    // =====================================================================
    //  ContainerCanCopySource — pins the MP4-friendly audio codec list so
    //  adding/removing a codec is detected directly.
    // =====================================================================

    /// <summary>Rows: (audio codec, MP4 copy supported).</summary>
    public static IEnumerable<object[]> Mp4CopyRows() => new[]
    {
        // MP4 can carry these without re-muxing
        new object[] { "aac",        true  },
        new object[] { "ac3",        true  },
        new object[] { "eac3",       true  },
        new object[] { "mp3",        true  },
        new object[] { "alac",       true  },
        new object[] { "AAC",        true  },   // case-insensitive

        // MP4 cannot stream-copy these — re-encode required
        new object[] { "truehd",     false },
        new object[] { "dts",        false },
        new object[] { "dtshd",      false },
        new object[] { "flac",       false },
        new object[] { "opus",       false },
        new object[] { "pcm_s16le",  false },
        new object[] { "vorbis",     false },
        new object[] { "",           false },
        new object[] { null!,        false },
    };

    [Theory]
    [MemberData(nameof(Mp4CopyRows))]
    public void ContainerCanCopySource_Mp4(string? codec, bool expected)
    {
        FfprobeService.ContainerCanCopySource(codec, "mp4").Should().Be(expected);
    }


    /// <summary>Rows: (audio codec, WebM copy supported).</summary>
    public static IEnumerable<object[]> WebmCopyRows() => new[]
    {
        // WebM only allows Opus + Vorbis audio.
        new object[] { "opus",       true  },
        new object[] { "vorbis",     true  },
        new object[] { "OPUS",       true  },   // case-insensitive

        // Everything else gets re-encoded.
        new object[] { "aac",        false },
        new object[] { "ac3",        false },
        new object[] { "eac3",       false },
        new object[] { "mp3",        false },
        new object[] { "flac",       false },
        new object[] { "truehd",     false },
        new object[] { "dts",        false },
        new object[] { "",           false },
        new object[] { null!,        false },
    };

    [Theory]
    [MemberData(nameof(WebmCopyRows))]
    public void ContainerCanCopySource_Webm(string? codec, bool expected)
    {
        FfprobeService.ContainerCanCopySource(codec, "webm").Should().Be(expected);
    }


    [Fact]
    public void ContainerCanCopySource_Matroska_is_permissive()
    {
        // Matroska is permissive — copies anything, including obscure codecs.
        FfprobeService.ContainerCanCopySource("truehd",    "mkv").Should().BeTrue();
        FfprobeService.ContainerCanCopySource("dts",       "mkv").Should().BeTrue();
        FfprobeService.ContainerCanCopySource("flac",      "mkv").Should().BeTrue();
        FfprobeService.ContainerCanCopySource("opus",      "mkv").Should().BeTrue();
        FfprobeService.ContainerCanCopySource("pcm_s16le", "mkv").Should().BeTrue();
    }


    // =====================================================================
    //  ComputeFixedFrameFilter — builds the scale+pad+format chain for
    //  device-specific presets (e.g. iPod Classic 640×480).
    // =====================================================================

    [Fact]
    public void ComputeFixedFrameFilter_returns_null_when_unset()
    {
        var opts = new EncoderOptions();
        TranscodingService.ComputeFixedFrameFilter(opts).Should().BeNull();
    }

    [Fact]
    public void ComputeFixedFrameFilter_returns_null_for_garbage()
    {
        var opts = new EncoderOptions { FixedFrameSize = "not-a-size" };
        TranscodingService.ComputeFixedFrameFilter(opts).Should().BeNull();
    }

    [Fact]
    public void ComputeFixedFrameFilter_builds_scale_pad_format_chain()
    {
        var opts = new EncoderOptions { FixedFrameSize = "640x480" };
        var filter = TranscodingService.ComputeFixedFrameFilter(opts);
        filter.Should().NotBeNull();
        filter.Should().Contain("scale=min(iw\\,640):min(ih\\,480):force_original_aspect_ratio=decrease");
        filter.Should().Contain("pad=640:480:(ow-iw)/2:(oh-ih)/2");
        filter.Should().Contain("format=yuv420p");
    }

    [Fact]
    public void ComputeFixedFrameFilter_rounds_odd_dimensions_down_to_even()
    {
        // yuv420p needs even dims — an odd hand-entered size must not produce a filter
        // ffmpeg rejects. 641x481 → 640x480.
        var opts = new EncoderOptions { FixedFrameSize = "641x481" };
        var filter = TranscodingService.ComputeFixedFrameFilter(opts);
        filter.Should().NotBeNull();
        filter.Should().Contain("min(iw\\,640):min(ih\\,480)");
        filter.Should().Contain("pad=640:480:");
    }

    [Fact]
    public void ComputeFixedFrameFilter_returns_null_when_a_dimension_rounds_to_zero()
    {
        // "1" rounds down to 0 — treat as unparseable rather than emit a 0-size pad.
        TranscodingService.ComputeFixedFrameFilter(new EncoderOptions { FixedFrameSize = "1x480" }).Should().BeNull();
    }


    // =====================================================================
    //  IsVideoProfileValidForEncoder — drops H.264-only profiles on HEVC
    //  (and any H.26x profile on AV1) so the command can't hard-fail.
    // =====================================================================

    [Theory]
    [InlineData("libx264", "baseline", true)]
    [InlineData("libx264", "high",     true)]
    [InlineData("libx264", "main",     true)]
    [InlineData("libx265", "main",     true)]
    [InlineData("libx265", "main10",   true)]
    [InlineData("libx265", "baseline", false)]  // H.264-only — would crash libx265
    [InlineData("libx265", "high",     false)]
    [InlineData("hevc_nvenc", "baseline", false)]
    [InlineData("libsvtav1", "main",   false)]  // AV1 takes no H.26x profile
    [InlineData("libx264", "",         true)]   // empty = nothing emitted = valid
    [InlineData("libx264", null,       true)]
    public void IsVideoProfileValidForEncoder_matches_codec(string encoder, string? profile, bool expected)
    {
        TranscodingService.IsVideoProfileValidForEncoder(encoder, profile).Should().Be(expected);
    }


    // =====================================================================
    //  ComputeFpsCapExpr — caps output frame rate for level-conformant
    //  device presets (e.g. iPod Classic H.264 Level 3.0 ≤ 30 fps).
    // =====================================================================

    private static WorkItem MakeWorkItemFps(string? frameRate) => new()
    {
        Probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream
                {
                    Index = 0, CodecType = "video", CodecName = "h264",
                    Width = 1920, Height = 1080,
                    AvgFrameRate = frameRate, RFrameRate = frameRate,
                },
            },
        },
    };

    [Fact]
    public void ComputeFpsCapExpr_returns_null_when_cap_disabled()
    {
        var opts = new EncoderOptions { MaxFrameRate = 0 };
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("60/1"), opts).Should().BeNull();
    }

    [Fact]
    public void ComputeFpsCapExpr_caps_when_source_exceeds_cap()
    {
        var opts = new EncoderOptions { MaxFrameRate = 30 };
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("60/1"), opts).Should().Be("fps=30");
    }

    [Fact]
    public void ComputeFpsCapExpr_returns_null_when_source_at_or_below_cap()
    {
        var opts = new EncoderOptions { MaxFrameRate = 30 };
        // 24000/1001 ≈ 23.976 fps and exact 25/30 are all under the cap → untouched.
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("24000/1001"), opts).Should().BeNull();
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("25/1"), opts).Should().BeNull();
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("30/1"), opts).Should().BeNull();
    }

    [Fact]
    public void ComputeFpsCapExpr_leaves_source_untouched_when_rate_unknown()
    {
        // `fps=N` would UPSAMPLE (duplicate frames) a slower source, so an unknown rate
        // must NOT be capped — leaving it is safer than risking a 24→30 fps inflation.
        var opts = new EncoderOptions { MaxFrameRate = 30 };
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps(null), opts).Should().BeNull();
        TranscodingService.ComputeFpsCapExpr(MakeWorkItemFps("0/0"), opts).Should().BeNull();
    }

    [Theory]
    [InlineData("30/1",      30.0)]
    [InlineData("24000/1001", 23.976)]
    [InlineData("60",        60.0)]
    [InlineData(null,        null)]
    [InlineData("",          null)]
    [InlineData("0/0",       null)]
    [InlineData("garbage",   null)]
    public void ParseFrameRate_handles_fraction_whole_and_junk(string? input, double? expected)
    {
        var actual = TranscodingService.ParseFrameRate(input);
        if (expected is null) actual.Should().BeNull();
        else actual.Should().BeApproximately(expected.Value, 0.001);
    }
}
