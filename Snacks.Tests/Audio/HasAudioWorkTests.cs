using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Audio;

/// <summary>
///     Mux-pass eligibility: <see cref="TranscodingService.HasAudioWork"/> returns false only when
///     the planner would emit nothing but copies of every kept source. These tests drive the
///     edge cases that decide between "skip the encode and remux" and "do the encode."
/// </summary>
public sealed class HasAudioWorkTests
{
    private static AudioStreamSummary Sum(string codec, int channels, string lang = "eng", string? title = null) =>
        new() { CodecName = codec, Channels = channels, Language = lang, Title = title };


    // ---------------------------------------------------------------------
    //  Empty inputs.
    // ---------------------------------------------------------------------

    [Fact]
    public void No_audio_streams_returns_false()
    {
        var opts = new EncoderOptions { PreserveOriginalAudio = true, AudioOutputs = new() };
        TranscodingService.HasAudioWork(opts, Array.Empty<AudioStreamSummary>()).Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  Language filter.
    // ---------------------------------------------------------------------

    [Fact]
    public void Language_filter_dropping_a_track_counts_as_work()
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };
        var streams = new[] { Sum("ac3", 6, "eng"), Sum("ac3", 6, "fre") };

        TranscodingService.HasAudioWork(opts, streams).Should().BeTrue();
    }


    [Fact]
    public void Language_filter_keeping_every_track_does_not_count_as_work()
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en", "fr" },
        };
        var streams = new[] { Sum("ac3", 6, "eng"), Sum("ac3", 6, "fre") };

        TranscodingService.HasAudioWork(opts, streams).Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  PreserveOriginalAudio.
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_off_with_no_extra_outputs_and_single_track_per_language_is_not_work()
    {
        // The empty-language safeguard in FfprobeService.MapAudio copies the
        // highest-channel kept track unchanged when no profiles produce output —
        // so a single-track-per-language source produces an output identical to
        // the input. Treating that as work would burn a no-op encode.
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };
        TranscodingService.HasAudioWork(opts, new[] { Sum("ac3", 6, "eng") }).Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  Profile-driven re-encode detection.
    // ---------------------------------------------------------------------

    /// <summary>Rows: (profile codec, profile layout, source codec, source channels, expected work?).</summary>
    public static IEnumerable<object[]> ProfileWorkRows() => new[]
    {
        // Profile codec+channels match a source exactly → dedup-to-copy → no work.
        new object[] { "ac3",  "5.1",    "ac3", 6, false },
        new object[] { "aac",  "Stereo", "aac", 2, false },
        // Profile differs → re-encode required → work.
        new object[] { "aac",  "Stereo", "ac3", 6, true  },
        new object[] { "opus", "5.1",    "ac3", 6, true  },
        new object[] { "ac3",  "Stereo", "ac3", 6, true  },   // same codec, different channels
        // Source layout (no -ac) matches any track of that codec.
        new object[] { "aac",  "Source", "aac", 2, false },
        new object[] { "aac",  "Source", "ac3", 6, true  },
    };

    [Theory]
    [MemberData(nameof(ProfileWorkRows))]
    public void Profile_dedup_decides_work(string profileCodec, string profileLayout, string sourceCodec, int sourceChannels, bool expected)
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new() { new AudioOutputProfile { Codec = profileCodec, Layout = profileLayout } },
            AudioLanguagesToKeep  = new() { "en" },
        };

        TranscodingService.HasAudioWork(opts, new[] { Sum(sourceCodec, sourceChannels, "eng") })
            .Should().Be(expected);
    }


    // ---------------------------------------------------------------------
    //  Commentary tracks must always be dropped — that's intended behavior
    //  baked into the planner. HasAudioWork has to agree, otherwise files
    //  with commentary tracks slip through the no-op skip gate
    //  (WouldEncodeBeNoOp returns true → file is marked Skipped → commentary
    //  stays in the source forever).
    // ---------------------------------------------------------------------

    [Fact]
    public void Commentary_track_is_audio_work_so_skip_gate_does_not_keep_it()
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };

        var streams = new[]
        {
            Sum("ac3", 6, "eng"),
            Sum("aac", 2, "eng", title: "Director's Commentary"),
        };

        TranscodingService.HasAudioWork(opts, streams).Should().BeTrue();
    }


    /// <summary>
    ///     The commentary detector matches loosely on the substring "comm" so real-world
    ///     titles like "Comm Track" or "Filmmaker Commentary" don't slip through. Pinning
    ///     the variants we know exist in user libraries.
    /// </summary>
    [Theory]
    [InlineData("Director's Commentary")]
    [InlineData("Filmmaker Commentary")]
    [InlineData("Comm Track")]
    [InlineData("commentary")]
    [InlineData("COMMENTARY")]
    [InlineData("Cast & Crew Comm")]
    public void Commentary_detection_matches_common_title_variants(string title)
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };

        TranscodingService.HasAudioWork(opts, new[] { Sum("aac", 2, "eng", title: title) })
            .Should().BeTrue();
    }


    [Fact]
    public void Non_commentary_tracks_with_titles_do_not_count_as_work()
    {
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new() { "en" },
        };

        // Title doesn't match "comm" → no work signal from commentary detection.
        TranscodingService.HasAudioWork(opts, new[] { Sum("ac3", 6, "eng", title: "Main Theatrical Mix") })
            .Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  Preserve=off interaction with AudioOutputs. The historical "Preserve=off
    //  ⇒ work" rule over-reported when the planner's empty-language safeguard
    //  (FfprobeService.MapAudio) would have copied the highest-channel kept
    //  track unchanged. These tests pin the corrected behavior:
    //    • non-empty AudioOutputs ⇒ work (drops or re-encodes)
    //    • empty AudioOutputs + 1 track per language ⇒ no work (safeguard copies)
    //    • empty AudioOutputs + multi-track per language ⇒ work (siblings dropped)
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_off_with_audio_outputs_is_work()
    {
        // The planner emits one encode (or codec-deduped copy) per profile and
        // drops everything else. Either way the output differs from the source.
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs          = new() { new AudioOutputProfile { Codec = "aac", Layout = "stereo", BitrateKbps = 192 } },
            AudioLanguagesToKeep  = new(),
        };

        TranscodingService.HasAudioWork(opts, new[] { Sum("ac3", 6, "eng") }).Should().BeTrue();
    }


    [Fact]
    public void Preserve_off_empty_outputs_single_track_per_language_is_not_work()
    {
        // Empty-language safeguard in MapAudio kicks in: highest-channel kept track
        // gets copied unchanged. Output == source for this case.
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new(),
        };

        TranscodingService.HasAudioWork(opts, new[] { Sum("ac3", 6, "eng") }).Should().BeFalse();
    }


    [Fact]
    public void Preserve_off_empty_outputs_multi_track_per_language_is_work()
    {
        // 5.1 + stereo English. Safeguard picks one (5.1) and drops the other.
        // That drop is a real change → work.
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new(),
        };

        TranscodingService.HasAudioWork(opts, new[]
        {
            Sum("ac3", 6, "eng"),
            Sum("aac", 2, "eng"),
        }).Should().BeTrue();
    }


    [Fact]
    public void Preserve_off_empty_outputs_multi_languages_each_single_is_not_work()
    {
        // One French + one English: each language bucket has a single track,
        // safeguard copies both. No drops.
        var opts = new EncoderOptions
        {
            PreserveOriginalAudio = false,
            AudioOutputs          = new(),
            AudioLanguagesToKeep  = new(),
        };

        TranscodingService.HasAudioWork(opts, new[]
        {
            Sum("ac3", 6, "eng"),
            Sum("ac3", 6, "fre"),
        }).Should().BeFalse();
    }
}
