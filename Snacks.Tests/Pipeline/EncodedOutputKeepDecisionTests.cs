using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Covers <see cref="TranscodingService.ShouldKeepEncodedOutput"/> and
///     <see cref="TranscodingService.IsMuxPass"/>. The cluster <c>HandleRemoteCompletion</c>
///     path used to inline its own keep/delete predicate that diverged from the local one
///     (missing the <c>videoCopy</c>/mux-pass exemption), which silently deleted remote
///     mux-pass outputs whose size happened to land at-or-above the source. The shared
///     helper exists so the two paths can't drift apart again.
/// </summary>
public sealed class EncodedOutputKeepDecisionTests
{
    private static WorkItem MakeMuxableWorkItem(long sourceSize = 1_000_000_000, bool isHevc = true, long bitrate = 3000, bool is4K = false)
    {
        var probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream { Index = 0, CodecType = "video",    CodecName = isHevc ? "hevc" : "h264", Width = is4K ? 3840 : 1920, Height = is4K ? 2160 : 1080 },
                new Stream { Index = 1, CodecType = "audio",    CodecName = "ac3",  Channels = 6,    Tags = new Tags { Language = "eng", Title = "Surround" } },
                new Stream { Index = 2, CodecType = "audio",    CodecName = "aac",  Channels = 2,    Tags = new Tags { Language = "eng", Title = "Stereo Commentary" } },
                new Stream { Index = 3, CodecType = "subtitle", CodecName = "subrip", Tags = new Tags { Language = "eng" } },
            },
        };
        return new WorkItem { Size = sourceSize, Bitrate = bitrate, IsHevc = isHevc, Is4K = is4K, Probe = probe };
    }

    // =====================================================================
    //  ShouldKeepEncodedOutput
    // =====================================================================

    [Fact]
    public void Keeps_when_savings_positive()
    {
        var item = MakeMuxableWorkItem(sourceSize: 1_000_000_000);
        var opts = new EncoderOptions { Encoder = "libx265", EncodingMode = EncodingMode.Transcode };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, 800_000_000);

        keep.Should().BeTrue();
        reason.Should().Be("savings");
    }

    [Fact]
    public void Keeps_remux_with_no_savings_when_local_path_says_videoCopy()
    {
        // Local path knows directly that ffmpeg ran with -c:v copy.
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions { Encoder = "libx265", EncodingMode = EncodingMode.MuxOnly };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, item.Size, videoCopyHint: true);

        keep.Should().BeTrue();
        reason.Should().Be("remux");
    }

    [Fact]
    public void Keeps_cluster_remux_with_no_savings_via_isMuxPass_fallback()
    {
        // Cluster path passes videoCopyHint=null. Helper recomputes IsMuxPass from
        // EncodingMode + HasMuxableWork + (MuxOnly || MeetsBitrateTarget). For a
        // MuxOnly mode with muxable work, this should return remux.
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions
        {
            Encoder       = "libx265",
            EncodingMode  = EncodingMode.MuxOnly,
            MuxStreams    = MuxStreams.Both,
            // Drop the commentary track via title; that's "muxable work".
            AudioLanguagesToKeep = new() { "eng" },
        };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, item.Size + 1024);

        keep.Should().BeTrue();
        reason.Should().Be("remux");
    }

    [Fact]
    public void Keeps_when_user_configured_audio_outputs_grow_file()
    {
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions
        {
            Encoder       = "libx265",
            EncodingMode  = EncodingMode.Transcode,
            AudioOutputs  = new() { new AudioOutputProfile { Codec = "ac3", Layout = "5.1", BitrateKbps = 640 } },
        };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, item.Size + 100_000_000);

        keep.Should().BeTrue();
        reason.Should().Be("configured audio outputs");
    }

    [Fact]
    public void Drops_transcode_with_no_savings_no_growth_no_muxpass()
    {
        // Transcode mode → IsMuxPass is unconditionally false, so the keep predicate
        // collapses to (savings>0 || userConfiguredGrowth). Neither holds here.
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions
        {
            Encoder       = "libx265",
            EncodingMode  = EncodingMode.Transcode,
        };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, item.Size + 1024);

        keep.Should().BeFalse();
        reason.Should().Be("no savings");
    }

    [Fact]
    public void Cluster_path_keeps_at_target_hevc_remux_with_no_savings()
    {
        // The user's exact scenario: HEVC at-target, remux to drop tracks, output ends up
        // ~equal in size to source. Pre-fix: cluster deleted output and marked Skipped.
        // Post-fix: cluster recognises the mux-pass and keeps the output.
        var item = MakeMuxableWorkItem(sourceSize: 5_000_000_000, isHevc: true, bitrate: 3000);
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            EncodingMode           = EncodingMode.Hybrid,
            MuxStreams             = MuxStreams.Both,
            TargetBitrate          = 3500,
            SkipPercentAboveTarget = 20,
            AudioLanguagesToKeep   = new() { "eng" },
        };

        var (keep, reason) = TranscodingService.ShouldKeepEncodedOutput(opts, item, item.Size, item.Size);

        keep.Should().BeTrue();
        reason.Should().Be("remux");
    }

    // =====================================================================
    //  IsMuxPass — the predicate that backs the videoCopyHint=null path.
    // =====================================================================

    [Fact]
    public void IsMuxPass_false_when_mode_is_transcode()
    {
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions { Encoder = "libx265", EncodingMode = EncodingMode.Transcode, MuxStreams = MuxStreams.Both };

        TranscodingService.IsMuxPass(opts, item).Should().BeFalse();
    }

    [Fact]
    public void IsMuxPass_true_in_MuxOnly_when_there_is_muxable_work()
    {
        var item = MakeMuxableWorkItem();
        var opts = new EncoderOptions
        {
            Encoder              = "libx265",
            EncodingMode         = EncodingMode.MuxOnly,
            MuxStreams           = MuxStreams.Both,
            AudioLanguagesToKeep = new() { "eng" },   // commentary still triggers HasAudioWork
        };

        TranscodingService.IsMuxPass(opts, item).Should().BeTrue();
    }

    [Fact]
    public void IsMuxPass_true_in_Hybrid_when_at_target_and_muxable()
    {
        var item = MakeMuxableWorkItem(isHevc: true, bitrate: 3000);
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            EncodingMode           = EncodingMode.Hybrid,
            MuxStreams             = MuxStreams.Both,
            TargetBitrate          = 3500,
            SkipPercentAboveTarget = 20,
            AudioLanguagesToKeep   = new() { "eng" },
        };

        TranscodingService.IsMuxPass(opts, item).Should().BeTrue();
    }

    [Fact]
    public void IsMuxPass_false_in_Hybrid_when_above_target_bitrate()
    {
        var item = MakeMuxableWorkItem(isHevc: true, bitrate: 9000);
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            EncodingMode           = EncodingMode.Hybrid,
            MuxStreams             = MuxStreams.Both,
            TargetBitrate          = 3500,
            SkipPercentAboveTarget = 20,
            AudioLanguagesToKeep   = new() { "eng" },
        };

        TranscodingService.IsMuxPass(opts, item).Should().BeFalse();
    }

    [Fact]
    public void IsMuxPass_false_when_probe_is_null()
    {
        var item = new WorkItem { Size = 1_000_000_000, Bitrate = 3000, IsHevc = true, Probe = null };
        var opts = new EncoderOptions { Encoder = "libx265", EncodingMode = EncodingMode.MuxOnly, MuxStreams = MuxStreams.Both };

        TranscodingService.IsMuxPass(opts, item).Should().BeFalse();
    }
}
