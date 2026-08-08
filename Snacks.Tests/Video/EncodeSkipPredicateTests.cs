using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Video;

/// <summary>
///     The collection of small predicates that decide whether a file gets touched at all:
///     <c>IsDownscalePolicyActive</c>, <c>WouldDownscale</c>, <c>WillDownscaleBelow4K</c>,
///     <c>HasActiveFilter</c>, <c>HasMuxableWork</c>, <c>WouldEncodeBeNoOp</c>,
///     <c>MeetsBitrateTarget</c>, and <c>WouldSkipUnderOptions</c>. These drive scan-time
///     skip decisions and the on-save re-evaluation of previously-skipped rows.
/// </summary>
public sealed class EncodeSkipPredicateTests
{
    // =====================================================================
    //  IsDownscalePolicyActive — accepts "Always", "CapAtTarget", "IfLarger".
    // =====================================================================

    /// <summary>Rows: (policy string, expected active).</summary>
    public static IEnumerable<object[]> DownscalePolicyRows() => new[]
    {
        new object[] { "Always",      true  },
        new object[] { "CapAtTarget", true  },
        new object[] { "IfLarger",    true  },
        new object[] { "ALWAYS",      true  },   // case-insensitive
        new object[] { "Never",       false },
        new object[] { "",            false },
        new object[] { "garbage",     false },
    };

    [Theory]
    [MemberData(nameof(DownscalePolicyRows))]
    public void IsDownscalePolicyActive(string policy, bool expected)
    {
        TranscodingService.IsDownscalePolicyActive(policy).Should().Be(expected);
    }


    // =====================================================================
    //  WouldDownscale — combines policy + source height + target height.
    // =====================================================================

    /// <summary>Rows: (policy, source height, target string, expected).</summary>
    public static IEnumerable<object[]> WouldDownscaleRows() => new[]
    {
        // Policy off → never downscales.
        new object[] { "Never",       2160, "1080p", false },
        new object[] { "",            2160, "1080p", false },

        // "Always" downscales unconditionally (even when source ≤ target).
        new object[] { "Always",      720,  "1080p", true  },
        new object[] { "Always",      2160, "1080p", true  },

        // "IfLarger" / "CapAtTarget" only fire when source > target.
        new object[] { "IfLarger",    1080, "1080p", false },   // equal — no
        new object[] { "IfLarger",    720,  "1080p", false },   // smaller — no
        new object[] { "IfLarger",    2160, "1080p", true  },
        new object[] { "CapAtTarget", 2160, "720p",  true  },

        // Zero source height (probe failed) → never downscales.
        new object[] { "Always",      0,    "1080p", false },
        new object[] { "IfLarger",    0,    "1080p", false },
    };

    [Theory]
    [MemberData(nameof(WouldDownscaleRows))]
    public void WouldDownscale_combines_policy_and_heights(string policy, int sourceHeight, string target, bool expected)
    {
        var opts = new EncoderOptions { DownscalePolicy = policy, DownscaleTarget = target };
        TranscodingService.WouldDownscale(opts, sourceHeight).Should().Be(expected);
    }


    // =====================================================================
    //  WillDownscaleBelow4K — used to decide whether a 4K source still gets
    //  the 4K bitrate multiplier. True when the active downscale would land
    //  the output at ≤ 1440p (i.e., not 4K territory anymore).
    // =====================================================================

    /// <summary>Rows: (policy, target, expected).</summary>
    public static IEnumerable<object[]> WillDownscaleBelow4KRows() => new[]
    {
        // Policy off → false regardless of target.
        new object[] { "Never",    "1080p", false },

        // Policy on + target ≤ 1440p → true.
        new object[] { "Always",   "1440p", true  },
        new object[] { "Always",   "1080p", true  },
        new object[] { "Always",   "720p",  true  },
        new object[] { "IfLarger", "1080p", true  },

        // Policy on + target > 1440p → false (still 4K-ish).
        new object[] { "Always",   "2160p", false },
        new object[] { "Always",   "4K",    false },
    };

    [Theory]
    [MemberData(nameof(WillDownscaleBelow4KRows))]
    public void WillDownscaleBelow4K(string policy, string target, bool expected)
    {
        var opts = new EncoderOptions { DownscalePolicy = policy, DownscaleTarget = target };
        TranscodingService.WillDownscaleBelow4K(opts).Should().Be(expected);
    }


    // =====================================================================
    //  HasActiveFilter — black-borders | downscale | tonemap.
    // =====================================================================

    /// <summary>Rows: (removeBorders, downscalePolicy, downscaleTarget, sourceHeight, tonemap, isHdr, expected).</summary>
    public static IEnumerable<object[]> HasActiveFilterRows() => new[]
    {
        // Nothing on → false.
        new object[] { false, "Never", "1080p", 1080, false, false, false },

        // Black-border crop alone.
        new object[] { true,  "Never", "1080p", 1080, false, false, true  },

        // Downscale fires.
        new object[] { false, "Always",  "1080p", 1080, false, false, true  },
        new object[] { false, "IfLarger","1080p", 2160, false, false, true  },
        new object[] { false, "IfLarger","1080p",  720, false, false, false },

        // Tonemap requires HDR source.
        new object[] { false, "Never", "1080p", 1080, true,  true,  true  },
        new object[] { false, "Never", "1080p", 1080, true,  false, false },   // tonemap on, source SDR
        new object[] { false, "Never", "1080p", 1080, false, true,  false },   // HDR source, tonemap off
    };

    [Theory]
    [MemberData(nameof(HasActiveFilterRows))]
    public void HasActiveFilter_combines_filter_signals(
        bool   removeBorders,
        string downscalePolicy,
        string downscaleTarget,
        int    sourceHeight,
        bool   tonemap,
        bool   isHdr,
        bool   expected)
    {
        var opts = new EncoderOptions
        {
            RemoveBlackBorders = removeBorders,
            DownscalePolicy    = downscalePolicy,
            DownscaleTarget    = downscaleTarget,
            TonemapHdrToSdr    = tonemap,
        };
        TranscodingService.HasActiveFilter(opts, sourceHeight, isHdr).Should().Be(expected);
    }


    // =====================================================================
    //  HasMuxableWork — only fires under non-Transcode modes; respects the
    //  MuxStreams gate (Audio / Subtitles / Both).
    // =====================================================================

    private static AudioStreamSummary AudioSum(string codec, int channels, string lang = "eng") =>
        new() { CodecName = codec, Channels = channels, Language = lang };

    private static SubtitleStreamSummary SubSum(string codec, string lang = "eng") =>
        new() { CodecName = codec, Language = lang };

    [Fact]
    public void HasMuxableWork_in_Transcode_mode_is_always_false()
    {
        var opts = new EncoderOptions
        {
            EncodingMode = EncodingMode.Transcode,
            MuxStreams   = MuxStreams.Both,
            // Configure something that *would* be work in MuxOnly mode.
            AudioLanguagesToKeep = new() { "en" },
            PreserveOriginalAudio = true,
            AudioOutputs = new(),
        };
        var audio = new[] { AudioSum("ac3", 6, "eng"), AudioSum("ac3", 6, "fre") };
        var subs  = Array.Empty<SubtitleStreamSummary>();

        TranscodingService.HasMuxableWork(opts, audio, subs).Should().BeFalse();
    }


    [Fact]
    public void HasMuxableWork_MuxOnly_audio_branch_only_considers_audio()
    {
        var opts = new EncoderOptions
        {
            EncodingMode             = EncodingMode.MuxOnly,
            MuxStreams               = MuxStreams.Audio,
            AudioLanguagesToKeep     = new() { "en" },
            PreserveOriginalAudio    = true,
            AudioOutputs             = new(),
            SubtitleLanguagesToKeep  = new() { "en" },
        };
        var audio = new[] { AudioSum("ac3", 6, "eng") };           // no audio work
        var subs  = new[] { SubSum("subrip", "fre") };             // sub language drop — but we're in Audio-only mode

        TranscodingService.HasMuxableWork(opts, audio, subs).Should().BeFalse();
    }


    [Fact]
    public void HasMuxableWork_MuxOnly_subtitles_branch_only_considers_subs()
    {
        var opts = new EncoderOptions
        {
            EncodingMode            = EncodingMode.MuxOnly,
            MuxStreams              = MuxStreams.Subtitles,
            AudioLanguagesToKeep    = new() { "en" },
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            SubtitleLanguagesToKeep = new() { "en" },
        };
        var audio = new[] { AudioSum("ac3", 6, "fre") };           // language drop — but we're in Subtitles-only mode
        var subs  = new[] { SubSum("subrip", "eng") };             // no sub work

        TranscodingService.HasMuxableWork(opts, audio, subs).Should().BeFalse();
    }


    [Fact]
    public void HasMuxableWork_MuxOnly_Both_fires_for_either_leg()
    {
        var opts = new EncoderOptions
        {
            EncodingMode            = EncodingMode.MuxOnly,
            MuxStreams              = MuxStreams.Both,
            AudioLanguagesToKeep    = new() { "en" },
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            SubtitleLanguagesToKeep = new() { "en" },
        };
        var audio = new[] { AudioSum("ac3", 6, "eng") };           // no work
        var subs  = new[] { SubSum("subrip", "fre") };             // language drop

        TranscodingService.HasMuxableWork(opts, audio, subs).Should().BeTrue();
    }


    // =====================================================================
    //  WouldEncodeBeNoOp — bitrate copy eligibility, no filter, no audio/sub work.
    // =====================================================================

    [Fact]
    public void WouldEncodeBeNoOp_returns_true_for_under_target_hevc_with_no_other_work()
    {
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:         3000,
            isHevc:          true,
            sourceHeight:    1080,
            isHdr:           false,
            audioStreams:    new[] { AudioSum("ac3", 6, "eng") },
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeTrue();
    }


    [Fact]
    public void WouldEncodeBeNoOp_false_when_source_is_h264()
    {
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:         3000,
            isHevc:          false,            // H.264 source — copy not eligible
            sourceHeight:    1080,
            isHdr:           false,
            audioStreams:    new[] { AudioSum("ac3", 6, "eng") },
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeFalse();
    }


    [Fact]
    public void WouldEncodeBeNoOp_false_when_bitrate_at_or_above_target_plus_700()
    {
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:         4500,             // above target + 700 → encode is needed
            isHevc:          true,
            sourceHeight:    1080,
            isHdr:           false,
            audioStreams:    new[] { AudioSum("ac3", 6, "eng") },
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeFalse();
    }


    [Fact]
    public void WouldEncodeBeNoOp_false_when_a_filter_would_fire()
    {
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            RemoveBlackBorders    = true,      // active filter
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:         3000,
            isHevc:          true,
            sourceHeight:    1080,
            isHdr:           false,
            audioStreams:    Array.Empty<AudioStreamSummary>(),
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeFalse();
    }


    [Fact]
    public void WouldEncodeBeNoOp_false_when_a_commentary_track_is_present()
    {
        // Regression guard for the skip-ladder: a file already at the bitrate target
        // that contains a commentary track must NOT be marked no-op. The planner always
        // drops commentary, so the encode is meaningful even when nothing else changes.
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:      3000,
            isHevc:       true,
            sourceHeight: 1080,
            isHdr:        false,
            audioStreams: new[]
            {
                AudioSum("ac3", 6, "eng"),
                new AudioStreamSummary { CodecName = "aac", Channels = 2, Language = "eng", Title = "Director's Commentary" },
            },
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeFalse();
    }


    [Fact]
    public void WouldEncodeBeNoOp_false_when_audio_work_is_pending()
    {
        // Preserve=off + a profile that doesn't dedup against the source → real audio work.
        // Pre-fix the "Preserve=off ⇒ work" rule fired on empty AudioOutputs too, which
        // over-reported single-track-per-language safeguard cases as work.
        var opts = new EncoderOptions
        {
            TargetBitrate         = 3500,
            PreserveOriginalAudio = false,
            AudioOutputs          = new()
            {
                new AudioOutputProfile { Codec = "aac", Layout = "stereo", BitrateKbps = 192 },
            },
            AudioLanguagesToKeep  = new() { "en" },
        };

        TranscodingService.WouldEncodeBeNoOp(
            opts,
            bitrate:         3000,
            isHevc:          true,
            sourceHeight:    1080,
            isHdr:           false,
            audioStreams:    new[] { AudioSum("ac3", 6, "eng") },
            subtitleStreams: Array.Empty<SubtitleStreamSummary>())
        .Should().BeFalse();
    }


    // =====================================================================
    //  MeetsBitrateTarget — codec match + bitrate ≤ ceiling × skipMultiplier.
    // =====================================================================

    private static WorkItem MakeWorkItem(long bitrate, bool isHevc, bool is4K, int width = 1920)
    {
        var probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream
                {
                    Index     = 0,
                    CodecType = "video",
                    CodecName = isHevc ? "hevc" : "h264",
                    Width     = width,
                    Height    = 1080,
                },
            },
        };
        return new WorkItem
        {
            Bitrate = bitrate,
            IsHevc  = isHevc,
            Is4K    = is4K,
            Probe   = probe,
        };
    }


    [Fact]
    public void MeetsBitrateTarget_false_when_bitrate_unknown()
    {
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, SkipPercentAboveTarget = 20 };
        var item = MakeWorkItem(bitrate: 0, isHevc: true, is4K: false);

        TranscodingService.MeetsBitrateTarget(item, opts).Should().BeFalse();
    }


    [Fact]
    public void MeetsBitrateTarget_false_when_codec_does_not_match_target()
    {
        // Target HEVC (libx265), but source is H.264 → not at target codec.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, SkipPercentAboveTarget = 20 };
        var item = MakeWorkItem(bitrate: 3000, isHevc: false, is4K: false);

        TranscodingService.MeetsBitrateTarget(item, opts).Should().BeFalse();
    }


    /// <summary>
    ///     Rows: (bitrate, target, skipPercent, expected). Target HEVC, source HEVC, non-4K.
    ///     Ceiling = target × (1 + skipPercent/100); the work item meets target iff bitrate ≤ ceiling.
    /// </summary>
    public static IEnumerable<object[]> MeetsBitrateRows() => new[]
    {
        new object[] { 3000L, 3500, 20, true  },     // under ceiling 4200 → meets
        new object[] { 4200L, 3500, 20, true  },     // exactly at ceiling 4200 → meets
        new object[] { 4500L, 3500, 20, false },     // over ceiling → does not meet
        new object[] { 3500L, 3500, 0,  true  },     // 0% slack: ceiling = target
        new object[] { 3501L, 3500, 0,  false },
        new object[] { 7000L, 3500, 100, true  },    // 100% slack: ceiling = 2× target
    };

    [Theory]
    [MemberData(nameof(MeetsBitrateRows))]
    public void MeetsBitrateTarget_respects_skip_percent(long bitrate, int target, int skipPct, bool expected)
    {
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            TargetBitrate          = target,
            SkipPercentAboveTarget = skipPct,
        };
        var item = MakeWorkItem(bitrate, isHevc: true, is4K: false);

        TranscodingService.MeetsBitrateTarget(item, opts).Should().Be(expected);
    }


    /// <summary>
    ///     Regression: with an H.264 target, "already at target codec" used to be computed
    ///     as <c>!isHevc</c>, which misclassified MPEG-2 / VC-1 / VP9 / XviD sources as
    ///     "already H.264" — a 2000 kbps MPEG-2 file was skipped (or hybrid-mux video-copied)
    ///     instead of converted. The predicate must match the actual source codec.
    /// </summary>
    [Theory]
    [InlineData("h264", true)]   // genuinely H.264 → meets target
    [InlineData("avc", true)]    // alias
    [InlineData("mpeg2video", false)]
    [InlineData("vc1", false)]
    [InlineData("vp9", false)]
    [InlineData("mpeg4", false)]
    public void MeetsBitrateTarget_h264_target_matches_actual_source_codec(string sourceCodec, bool expected)
    {
        var opts = new EncoderOptions { Encoder = "libx264", TargetBitrate = 3500, SkipPercentAboveTarget = 20 };
        var item = new WorkItem
        {
            Bitrate = 2000,
            IsHevc  = false,
            Is4K    = false,
            Probe   = new ProbeResult
            {
                Streams = new[]
                {
                    new Stream { Index = 0, CodecType = "video", CodecName = sourceCodec, Width = 1920, Height = 1080 },
                },
            },
        };

        TranscodingService.MeetsBitrateTarget(item, opts).Should().Be(expected);
    }

    [Fact]
    public void MeetsBitrateTarget_applies_4K_multiplier()
    {
        // 4K HEVC target: 3500 × 4 = 14000 ceiling, +20% slack = 16800. A 15000 kbps source meets target.
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            TargetBitrate          = 3500,
            FourKBitrateMultiplier = 4,
            SkipPercentAboveTarget = 20,
        };
        var item = MakeWorkItem(bitrate: 15000, isHevc: true, is4K: true, width: 3840);

        TranscodingService.MeetsBitrateTarget(item, opts).Should().BeTrue();
    }


    [Fact]
    public void MeetsBitrateTarget_av1_target_against_av1_source()
    {
        var opts = new EncoderOptions
        {
            Encoder                = "libsvtav1",
            TargetBitrate          = 2500,
            SkipPercentAboveTarget = 20,
        };
        var probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream { Index = 0, CodecType = "video", CodecName = "av1", Width = 1920, Height = 1080 }
            },
        };
        var item = new WorkItem { Bitrate = 2000, IsHevc = false, Is4K = false, Probe = probe };

        TranscodingService.MeetsBitrateTarget(item, opts).Should().BeTrue();
    }


    // =====================================================================
    //  WouldSkipUnderOptions — drives the on-save re-evaluation of skipped
    //  rows. Pure function over MediaFile fields + EncoderOptions.
    // =====================================================================

    private static MediaFile MakeMediaFile(
        long  bitrate,
        bool  isHevc,
        bool  is4K,
        int   height         = 1080,
        string? codec        = null,
        string? audioStreams = null,
        string? subStreams   = null,
        bool  isHdr          = false,
        string? originalLanguage = null) => new()
    {
        Bitrate          = bitrate,
        IsHevc           = isHevc,
        IsHdr            = isHdr,
        Is4K             = is4K,
        Height           = height,
        Codec            = codec ?? (isHevc ? "hevc" : "h264"),
        AudioStreams     = audioStreams,
        SubtitleStreams  = subStreams,
        OriginalLanguage = originalLanguage,
    };


    [Fact]
    public void WouldSkipUnderOptions_4K_source_with_skip4K_returns_true()
    {
        var opts = new EncoderOptions { Skip4K = true, Encoder = "libx265" };
        var mf   = MakeMediaFile(bitrate: 50000, isHevc: true, is4K: true);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_muxable_work_unskips()
    {
        // Mux-pass settings + a sub language drop → file is no longer a skip candidate.
        var subStreams = "[{\"l\":\"eng\",\"c\":\"subrip\"},{\"l\":\"fre\",\"c\":\"subrip\"}]";
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            EncodingMode            = EncodingMode.MuxOnly,
            MuxStreams              = MuxStreams.Subtitles,
            SubtitleLanguagesToKeep = new() { "en" },
        };
        var mf = MakeMediaFile(bitrate: 3000, isHevc: true, is4K: false, subStreams: subStreams);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeFalse();
    }


    [Fact]
    public void WouldSkipUnderOptions_MuxOnly_with_no_muxable_work_skips()
    {
        var opts = new EncoderOptions
        {
            Encoder      = "libx265",
            EncodingMode = EncodingMode.MuxOnly,
            MuxStreams   = MuxStreams.Both,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 3000, isHevc: true, is4K: false);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    /// <summary>
    ///     Regression: an H.264 target used to treat any non-HEVC source as "already at
    ///     target codec" — a low-bitrate MPEG-2 file was skipped instead of converted.
    /// </summary>
    [Theory]
    [InlineData("h264", true)]
    [InlineData("mpeg2video", false)]
    [InlineData("vp9", false)]
    public void WouldSkipUnderOptions_h264_target_requires_h264_source(string sourceCodec, bool expected)
    {
        var opts = new EncoderOptions
        {
            Encoder                 = "libx264",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 2000, isHevc: false, is4K: false, codec: sourceCodec);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().Be(expected);
    }


    [Fact]
    public void WouldSkipUnderOptions_codec_mismatch_returns_false()
    {
        var opts = new EncoderOptions
        {
            Encoder       = "libx265",
            TargetBitrate = 3500,
            EncodingMode  = EncodingMode.Transcode,
        };
        // Source is H.264 — does NOT match the HEVC target → not skip-eligible.
        var mf = MakeMediaFile(bitrate: 3000, isHevc: false, is4K: false);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeFalse();
    }


    [Fact]
    public void WouldSkipUnderOptions_under_ceiling_skips()
    {
        var opts = new EncoderOptions
        {
            Encoder                = "libx265",
            TargetBitrate          = 3500,
            SkipPercentAboveTarget = 20,
            EncodingMode           = EncodingMode.Transcode,
        };
        // HEVC source at 4000 kbps, ceiling 4200 → under → skip.
        var mf = MakeMediaFile(bitrate: 4000, isHevc: true, is4K: false);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_over_ceiling_no_op_path_skips()
    {
        // HEVC over the skip ceiling but under target+700 with no audio/sub/filter work →
        // hits the no-op rung and still skips. Replicates the AddFileAsync skip ladder.
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 0,           // ceiling exactly = target
            EncodingMode            = EncodingMode.Transcode,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 3700, isHevc: true, is4K: false);

        // Bitrate 3700 > ceiling 3500 → fails the cheap skip; but 3700 < 3500+700=4200,
        // HEVC, no filter or audio/sub work → no-op rung returns true.
        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_av1_source_against_av1_target_skips_when_under_ceiling()
    {
        var opts = new EncoderOptions
        {
            Encoder                = "libsvtav1",
            TargetBitrate          = 2500,
            SkipPercentAboveTarget = 20,
            EncodingMode           = EncodingMode.Transcode,
        };
        var mf = MakeMediaFile(bitrate: 2000, isHevc: false, is4K: false, codec: "av1");

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_no_op_gate_uses_cached_IsHdr_for_tonemap()
    {
        // HDR HEVC source just over the bitrate ceiling but under target+700 — the no-op
        // rung previously hard-coded isHdr=false and would mis-skip when TonemapHdrToSdr
        // was on. With the cached MediaFile.IsHdr in play, HasActiveFilter sees the
        // active tonemap → no skip.
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 0,
            EncodingMode            = EncodingMode.Transcode,
            TonemapHdrToSdr         = true,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
        };
        var mf = MakeMediaFile(bitrate: 3700, isHevc: true, is4K: false, isHdr: true);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeFalse();
    }


    [Fact]
    public void WouldSkipUnderOptions_no_op_gate_skips_when_cached_IsHdr_is_false()
    {
        // Same bitrate window as above, but the cached row is SDR — tonemap can't fire
        // even though it's enabled. No-op rung correctly returns true.
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 0,
            EncodingMode            = EncodingMode.Transcode,
            TonemapHdrToSdr         = true,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
        };
        var mf = MakeMediaFile(bitrate: 3700, isHevc: true, is4K: false, isHdr: false);

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_KeepOriginalLanguage_merges_cached_value_before_predicate()
    {
        // Japanese-only audio source, English in keep-list, KeepOriginalLanguage on with
        // OriginalLanguage cached as "ja". Without the merge the predicate would see
        // "Japanese gets dropped → muxable work" and refuse to skip; with the merge the
        // keep list effectively becomes [en, ja] and the file is at-target → skip.
        var audioStreams = "[{\"l\":\"jpn\",\"c\":\"ac3\",\"ch\":6}]";
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Transcode,
            KeepOriginalLanguage    = true,
            AudioLanguagesToKeep    = new() { "en" },
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
        };
        var mf = MakeMediaFile(
            bitrate: 4000, isHevc: true, is4K: false,
            audioStreams: audioStreams,
            originalLanguage: "ja");

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    // =====================================================================
    //  NeedsContainerChange — source extension vs configured output Format.
    //  Only consulted on the force-mux ("Process Item"/"Process Directory")
    //  path; mp4 and m4v are treated as the same container.
    // =====================================================================

    /// <summary>Rows: (format, sourcePath, expected).</summary>
    public static IEnumerable<object[]> ContainerChangeRows() => new[]
    {
        new object[] { "mp4",  "/x/movie.mkv",  true  },   // mkv → mp4
        new object[] { "mkv",  "/x/movie.mkv",  false },   // already mkv
        new object[] { "mp4",  "/x/movie.mp4",  false },   // already mp4
        new object[] { "mp4",  "/x/movie.m4v",  false },   // m4v ≡ mp4
        new object[] { "mkv",  "/x/movie.m4v",  true  },   // m4v → mkv
        new object[] { "webm", "/x/movie.mkv",  true  },
        new object[] { "MP4",  "/x/MOVIE.MKV",  true  },   // case-insensitive
        new object[] { "mkv",  "/x/noext",      false },   // no extension → can't tell
        new object[] { "",     "/x/movie.mkv",  false },   // no target → no change
    };

    [Theory]
    [MemberData(nameof(ContainerChangeRows))]
    public void NeedsContainerChange_compares_extension_to_format(string format, string sourcePath, bool expected)
    {
        var opts = new EncoderOptions { Format = format };
        TranscodingService.NeedsContainerChange(opts, sourcePath).Should().Be(expected);
    }


    // =====================================================================
    //  WouldSkipUnderOptions force-mux arm — a container change counts as
    //  work for force-mux items (and only for them).
    // =====================================================================

    /// <summary>
    ///     A force-mux item at the bitrate target with matching streams but the wrong
    ///     container must NOT be skipped — it still gets a remux into the target container.
    /// </summary>
    [Fact]
    public void WouldSkipUnderOptions_forceMux_container_change_unskips()
    {
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            Format                  = "mp4",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Hybrid,   // dispatch upgrades force-mux to Hybrid
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 3000, isHevc: true, is4K: false);
        mf.FilePath = "/lib/movie.mkv";   // mkv source, mp4 target → container change

        TranscodingService.WouldSkipUnderOptions(mf, opts, forceMux: true).Should().BeFalse();
    }


    /// <summary>
    ///     A force-mux item that's already in the target container with nothing else to do
    ///     is still skipped — the action only processes files that need work.
    /// </summary>
    [Fact]
    public void WouldSkipUnderOptions_forceMux_no_container_change_and_no_work_skips()
    {
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            Format                  = "mkv",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Hybrid,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 3000, isHevc: true, is4K: false);
        mf.FilePath = "/lib/movie.mkv";   // mkv source, mkv target → no container change

        TranscodingService.WouldSkipUnderOptions(mf, opts, forceMux: true).Should().BeTrue();
    }


    /// <summary>
    ///     The container change is ignored on the normal (non-force-mux) path, so global
    ///     behavior is unchanged: an at-target file in the "wrong" container still skips.
    /// </summary>
    [Fact]
    public void WouldSkipUnderOptions_without_forceMux_ignores_container_change()
    {
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            Format                  = "mp4",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Transcode,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 3000, isHevc: true, is4K: false);
        mf.FilePath = "/lib/movie.mkv";   // container differs, but forceMux defaults to false

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    // =====================================================================
    //  IsMuxPass — a force-mux container change drives a video-copy mux pass
    //  for an at-target file even with no audio/subtitle work.
    // =====================================================================

    private static WorkItem MakeMuxWorkItem(string sourcePath, long bitrate, bool isHevc, bool forceMux)
    {
        var probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream
                {
                    Index     = 0,
                    CodecType = "video",
                    CodecName = isHevc ? "hevc" : "h264",
                    Width     = 1920,
                    Height    = 1080,
                },
            },
        };
        return new WorkItem
        {
            Path     = sourcePath,
            Bitrate  = bitrate,
            IsHevc   = isHevc,
            Is4K     = false,
            ForceMux = forceMux,
            Probe    = probe,
        };
    }

    private static EncoderOptions MuxOpts(string format, EncodingMode mode = EncodingMode.Hybrid) => new()
    {
        Encoder                 = "libx265",
        Format                  = format,
        TargetBitrate           = 3500,
        SkipPercentAboveTarget  = 20,
        EncodingMode            = mode,
        PreserveOriginalAudio   = true,
        AudioOutputs            = new(),
        AudioLanguagesToKeep    = new(),
        SubtitleLanguagesToKeep = new(),
    };

    [Fact]
    public void IsMuxPass_true_for_forceMux_at_target_with_container_change()
    {
        // HEVC at target, no audio/sub work, but mkv→mp4 container change + ForceMux → mux pass.
        var item = MakeMuxWorkItem("/lib/movie.mkv", bitrate: 3000, isHevc: true, forceMux: true);
        TranscodingService.IsMuxPass(MuxOpts("mp4"), item).Should().BeTrue();
    }

    [Fact]
    public void IsMuxPass_false_without_forceMux_even_with_container_change()
    {
        var item = MakeMuxWorkItem("/lib/movie.mkv", bitrate: 3000, isHevc: true, forceMux: false);
        TranscodingService.IsMuxPass(MuxOpts("mp4"), item).Should().BeFalse();
    }

    [Fact]
    public void IsMuxPass_false_for_forceMux_when_container_already_matches()
    {
        var item = MakeMuxWorkItem("/lib/movie.mkv", bitrate: 3000, isHevc: true, forceMux: true);
        TranscodingService.IsMuxPass(MuxOpts("mkv"), item).Should().BeFalse();
    }

    [Fact]
    public void IsMuxPass_false_for_forceMux_container_change_when_not_at_target_codec()
    {
        // H.264 source against an HEVC target — not copy-eligible, so it re-encodes (not a mux
        // pass) even with a container change. MeetsBitrateTarget gates the mux pass off.
        var item = MakeMuxWorkItem("/lib/movie.mkv", bitrate: 3000, isHevc: false, forceMux: true);
        TranscodingService.IsMuxPass(MuxOpts("mp4"), item).Should().BeFalse();
    }


    // =====================================================================
    //  SourceCodecMeetsTarget — codec-efficiency hierarchy (AV1 > HEVC >
    //  H.264 > legacy). A source already as efficient as the target is
    //  "already at target" so it's never re-encoded into a worse codec.
    // =====================================================================

    /// <summary>Rows: (srcAv1, srcHevc, srcH264, targetHevc, targetAv1, expected).</summary>
    [Theory]
    // Target HEVC: AV1 + HEVC sources are already good; H.264 / legacy convert.
    [InlineData(true,  false, false, true,  false, true)]   // AV1  → HEVC target  (the reported bug)
    [InlineData(false, true,  false, true,  false, true)]   // HEVC → HEVC target
    [InlineData(false, false, true,  true,  false, false)]  // H264 → HEVC target  → convert
    [InlineData(false, false, false, true,  false, false)]  // legacy → HEVC target → convert
    // Target AV1: only AV1 is already good.
    [InlineData(true,  false, false, false, true,  true)]   // AV1  → AV1 target
    [InlineData(false, true,  false, false, true,  false)]  // HEVC → AV1 target → convert
    // Target H.264: AV1 / HEVC / H264 are all ≥ H.264; legacy still converts.
    [InlineData(true,  false, false, false, false, true)]   // AV1  → H264 target → don't upsize
    [InlineData(false, true,  false, false, false, true)]   // HEVC → H264 target → don't upsize
    [InlineData(false, false, true,  false, false, true)]   // H264 → H264 target
    [InlineData(false, false, false, false, false, false)]  // legacy → H264 target → convert
    public void SourceCodecMeetsTarget_follows_efficiency_hierarchy(
        bool srcAv1, bool srcHevc, bool srcH264, bool targetHevc, bool targetAv1, bool expected)
    {
        TranscodingService.SourceCodecMeetsTarget(srcAv1, srcHevc, srcH264, targetHevc, targetAv1)
            .Should().Be(expected);
    }


    [Fact]
    public void WouldSkipUnderOptions_av1_source_under_hevc_target_skips()
    {
        // The reported bug: an AV1 file well under the HEVC bitrate target was "shrunk" to
        // HEVC (source × 0.7) instead of being left alone. AV1 ≥ HEVC, so it must skip.
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Transcode,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 97, isHevc: false, is4K: false, codec: "av1");

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_hevc_source_under_h264_target_skips()
    {
        // Don't upsize: an HEVC source under an H.264 target is already more efficient.
        var opts = new EncoderOptions
        {
            Encoder                 = "libx264",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Transcode,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 2000, isHevc: true, is4K: false, codec: "hevc");

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeTrue();
    }


    [Fact]
    public void WouldSkipUnderOptions_h264_source_under_hevc_target_still_converts()
    {
        // H.264 is less efficient than the HEVC target → still re-encoded (not skipped).
        var opts = new EncoderOptions
        {
            Encoder                 = "libx265",
            TargetBitrate           = 3500,
            SkipPercentAboveTarget  = 20,
            EncodingMode            = EncodingMode.Transcode,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new(),
            SubtitleLanguagesToKeep = new(),
        };
        var mf = MakeMediaFile(bitrate: 2000, isHevc: false, is4K: false, codec: "h264");

        TranscodingService.WouldSkipUnderOptions(mf, opts).Should().BeFalse();
    }


    [Fact]
    public void MeetsBitrateTarget_av1_source_under_hevc_target_is_at_target()
    {
        // AV1 under the HEVC target counts as already-at-target, so a Hybrid pass would
        // copy the AV1 video rather than re-encode it to HEVC.
        var opts = new EncoderOptions { Encoder = "libx265", TargetBitrate = 3500, SkipPercentAboveTarget = 20 };
        var probe = new ProbeResult
        {
            Streams = new[]
            {
                new Stream { Index = 0, CodecType = "video", CodecName = "av1", Width = 1920, Height = 1080 },
            },
        };
        var item = new WorkItem { Bitrate = 97, IsHevc = false, Is4K = false, Probe = probe };

        TranscodingService.MeetsBitrateTarget(item, opts).Should().BeTrue();
    }
}
