using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for <see cref="TranscodingService.CalculateBitrates"/>, the function that
///     decides target/min/max bitrates and whether to copy the video stream. The branches
///     interact in ways that aren't obvious from the code (4K vs HD, low-bitrate H.264 vs
///     "above target" path, HEVC-copy threshold, source-bitrate cap, downscale-below-4K
///     skipping the multiplier), so the matrix is laid out explicitly.
/// </summary>
public sealed class CalculateBitratesTests
{
    private static WorkItem MakeWorkItem(long bitrate, bool isHevc, int width = 1920) => new()
    {
        Bitrate = bitrate,
        IsHevc  = isHevc,
        Probe   = new ProbeResult
        {
            Streams = new[]
            {
                new Stream
                {
                    Index     = 0,
                    CodecType = "video",
                    CodecName = isHevc ? "hevc" : "h264",
                    Width     = width,
                    Height    = width >= 3840 ? 2160 : 1080,
                },
            },
        },
    };


    // =====================================================================
    //  HEVC copy: below target+700 → videoCopy=true, no re-encode.
    //  Black-borders disables the copy because it forces a re-encode.
    // =====================================================================

    [Fact]
    public void Hevc_under_target_plus_700_returns_videoCopy_true()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 3000, isHevc: true);

        var (target, _, _, copy) = TranscodingService.CalculateBitrates(item, opts);

        copy.Should().BeTrue();
        target.Should().EndWith("k");
    }


    [Fact]
    public void Hevc_under_target_with_RemoveBlackBorders_disables_copy()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, RemoveBlackBorders = true };
        var item = MakeWorkItem(bitrate: 3000, isHevc: true);

        var (_, _, _, copy) = TranscodingService.CalculateBitrates(item, opts);
        copy.Should().BeFalse();
    }


    [Fact]
    public void Hevc_far_above_target_returns_videoCopy_false()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 8000, isHevc: true);

        var (_, _, _, copy) = TranscodingService.CalculateBitrates(item, opts);
        copy.Should().BeFalse();
    }


    // =====================================================================
    //  H.264 low-bitrate compression path: source < target+700 → 70% target,
    //  60% min, 80% max. videoCopy stays false (only HEVC sources can copy).
    // =====================================================================

    [Fact]
    public void H264_below_target_plus_700_compresses_to_70_percent_of_source()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 3000, isHevc: false);

        var (target, min, max, copy) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("2100k");   // 3000 * 0.7
        min.Should().Be("1800k");      // 3000 * 0.6
        max.Should().Be("2400k");      // 3000 * 0.8
        copy.Should().BeFalse();
    }


    // =====================================================================
    //  Above-target path: cap at min(TargetBitrate, source bitrate).
    //  Never encode higher than the source.
    // =====================================================================

    [Fact]
    public void Above_target_path_caps_at_user_target_when_source_is_higher()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 8000, isHevc: false);

        var (target, min, max, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");
        min.Should().Be("3300k");      // target - 200
        max.Should().Be("4000k");      // target + 500
    }


    [Fact]
    public void Source_bitrate_caps_target_when_source_is_lower_than_user_target()
    {
        // Pathological case: HEVC source above target+700 (so we skip the copy gate)
        // but still under user TargetBitrate. Bitrate is capped to source so we never
        // re-encode larger.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 10000 };
        var item = MakeWorkItem(bitrate: 6000, isHevc: false);

        var (target, _, _, _) = TranscodingService.CalculateBitrates(item, opts);

        // 6000 < 10000+700 so we land in the H.264-low-bitrate compression path,
        // emitting 70% of source. (This documents the actual code path, not what
        // a naive reader might assume from "Source bitrate caps target".)
        target.Should().Be("4200k");
    }


    [Fact]
    public void Zero_source_bitrate_on_h264_falls_through_to_user_target()
    {
        // The low-bitrate compression branches gate on Bitrate>0 — a probe that
        // returned no source bitrate would otherwise emit `-b:v 0k`, which ffmpeg
        // refuses. With Bitrate=0 the function lands in the above-target branch
        // and uses the user's TargetBitrate as-is.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 0, isHevc: false);

        var (target, min, max, copy) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");
        min.Should().Be("3300k");      // target - 200
        max.Should().Be("4000k");      // target + 500
        copy.Should().BeFalse();       // not HEVC → no copy
    }


    [Fact]
    public void Zero_source_bitrate_on_hevc_uses_user_target_and_copies()
    {
        // HEVC + unknown bitrate: above-target branch emits the user target, and
        // the videoCopy gate fires (Bitrate < target+700 is satisfied at 0 < 4200).
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500 };
        var item = MakeWorkItem(bitrate: 0, isHevc: true);

        var (target, _, _, copy) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");
        copy.Should().BeTrue();
    }


    [Fact]
    public void Zero_source_bitrate_on_4K_h264_uses_4K_multiplier_not_zero()
    {
        // 4K H.264 with no probed bitrate: the low-bitrate-4K-H.264 compression
        // branch is gated on Bitrate>0, so we land on the multiplier path.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, FourKBitrateMultiplier = 4 };
        var item = MakeWorkItem(bitrate: 0, isHevc: false, width: 3840);

        var (target, _, _, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("14000k");  // 3500 * 4 — full 4K budget
    }


    // =====================================================================
    //  StrictBitrate: target = min = max = user TargetBitrate, no compression.
    // =====================================================================

    [Fact]
    public void Strict_bitrate_pins_all_three_to_target()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, StrictBitrate = true };
        var item = MakeWorkItem(bitrate: 8000, isHevc: false);

        var (target, min, max, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");
        min.Should().Be("3500k");
        max.Should().Be("3500k");
    }


    // =====================================================================
    //  4K branch: TargetBitrate × FourKBitrateMultiplier, with -200/+500 spread.
    // =====================================================================

    [Fact]
    public void Four_K_source_uses_target_times_multiplier()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, FourKBitrateMultiplier = 4 };
        var item = MakeWorkItem(bitrate: 30000, isHevc: false, width: 3840);

        var (target, min, max, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("14000k");   // 3500 * 4
        min.Should().Be("13800k");      // - 200
        max.Should().Be("14500k");      // + 500
    }


    [Fact]
    public void Four_K_multiplier_is_clamped_to_2_8_range()
    {
        var optsLow = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, FourKBitrateMultiplier = 1 };  // clamps up to 2
        var optsHigh = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, FourKBitrateMultiplier = 99 }; // clamps down to 8
        var item = MakeWorkItem(bitrate: 30000, isHevc: false, width: 3840);

        var (targetLow, _, _, _)  = TranscodingService.CalculateBitrates(item, optsLow);
        var (targetHigh, _, _, _) = TranscodingService.CalculateBitrates(item, optsHigh);

        targetLow.Should().Be("7000k");   // 3500 * 2
        targetHigh.Should().Be("28000k"); // 3500 * 8
    }


    [Fact]
    public void Four_K_low_bitrate_h264_takes_compression_path_instead_of_multiplier()
    {
        // Low-bitrate 4K H.264 (e.g. compressed library file) → 70% compression of source,
        // not the 4K-multiplier path. This branch only applies for non-HEVC sources whose
        // source bitrate is already below the multiplied target.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, FourKBitrateMultiplier = 4 };
        var item = MakeWorkItem(bitrate: 5000, isHevc: false, width: 3840);

        var (target, min, max, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");   // 5000 * 0.7
        min.Should().Be("3000k");      // 5000 * 0.6
        max.Should().Be("4000k");      // 5000 * 0.8
    }


    [Fact]
    public void Four_K_with_downscale_below_4K_uses_user_target_not_multiplier()
    {
        // 4K → 1080p downscale: bitrate budget should match the 1080p target, not 4K's.
        // The WillDownscaleBelow4K predicate gates the multiplier path off.
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            TargetBitrate          = 3500,
            FourKBitrateMultiplier = 4,
            DownscalePolicy        = "Always",
            DownscaleTarget        = "1080p",
        };
        var item = MakeWorkItem(bitrate: 30000, isHevc: false, width: 3840);

        var (target, _, _, _) = TranscodingService.CalculateBitrates(item, opts);

        target.Should().Be("3500k");   // 1080p target, NOT 14000k
    }
}
