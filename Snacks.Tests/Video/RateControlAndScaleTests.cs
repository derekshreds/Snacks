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
        flags.Should().Contain("-global_quality 25");
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


    [Fact]
    public void Amf_path_uses_vbr_peak_with_hrd_enforcement()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_amf", useVaapi: false, isSvtAv1: false,
            targetBitrate: "3500k", minBitrate: "3300k", maxBitrate: "4000k",
            useConservativeHwFlags: false);

        flags.Should().Contain("-rc vbr_peak");
        flags.Should().Contain("-enforce_hrd 1");
        flags.Should().Contain("-b:v 3500k");
        flags.Should().Contain("-maxrate 4000k");
    }


    [Fact]
    public void Qsv_path_emits_lookahead_when_not_conservative()
    {
        var flags = TranscodingService.GetForcedReencodeCompressionFlags(
            encoder: "hevc_qsv", useVaapi: false, isSvtAv1: false,
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
    //  ContainerCanCopySource — pins the MP4-friendly audio codec list so
    //  adding/removing a codec is detected directly.
    // =====================================================================

    /// <summary>Rows: (audio codec, MP4 copy supported).</summary>
    public static IEnumerable<object[]> ContainerCopyRows() => new[]
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
    [MemberData(nameof(ContainerCopyRows))]
    public void ContainerCanCopySource(string? codec, bool expected)
    {
        FfprobeService.ContainerCanCopySource(codec).Should().Be(expected);
    }
}
