using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Audio;

/// <summary>
///     Verification of the audio planner (FfprobeService.MapAudio). Each test maps to
///     a row in the plan's verification matrix or covers an edge that fell out during review.
///     Tests assert structural properties of the parsed flags rather than raw strings so
///     refactors that reshape whitespace or argument order don't break the suite.
/// </summary>
public sealed class AudioPlannerTests
{
    private readonly FfprobeService _svc = new();

    /// <summary>Sentinel to mean "don't override languages, use the default keep-list of [en]".</summary>
    private static readonly IReadOnlyList<string> DefaultLanguages = new[] { "en" };

    /// <summary>Sentinel to mean "explicitly pass null to MapAudio (i.e., keep all)".</summary>
    private static readonly IReadOnlyList<string>? KeepAll = null;

    /// <summary>
    ///     Runs the planner with a single <see cref="AudioOutputProfile"/> built inline.
    ///     Defaults to the keep-EN scenario; pass <c>languages: KeepAll</c> for the
    ///     "no filter" path or <c>languages: Array.Empty&lt;string&gt;()</c> for empty.
    /// </summary>
    private (IReadOnlyList<AudioFlagsParser.OutputStream> streams, IReadOnlyList<string> warnings) Plan(
        ProbeResult                   probe,
        bool                          preserve,
        IReadOnlyList<AudioOutputProfile>? outputs,
        bool                          isMatroska   = true,
        IReadOnlyList<string>?        languages    = null,
        bool                          languagesSet = false)
    {
        // Three call shapes:
        //   - Caller didn't touch `languages`              → default to ["en"]
        //   - Caller passed an explicit non-null list      → use that list
        //   - Caller wants to test the null/empty path     → set languagesSet:true
        //                                                    AND pass languages: null/[]
        var effective = (languages == null && !languagesSet) ? DefaultLanguages : languages;
        var flags = _svc.MapAudio(
            probe,
            effective,
            preserve,
            outputs,
            isMatroska,
            out var warnings);
        return (AudioFlagsParser.Parse(flags), warnings);
    }


    // ---------------------------------------------------------------------
    //  Matrix row 1: single source, fan-out preserves + adds variants.
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_with_two_extra_outputs_emits_three_streams()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: true,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac",  Layout = "Stereo", BitrateKbps = 192 },
                new AudioOutputProfile { Codec = "opus", Layout = "5.1",    BitrateKbps = 256 },
            });

        warnings.Should().BeEmpty();
        streams.Should().HaveCount(3);

        // Copies come first, in source-index order. Source #1 is the AC3 5.1.
        streams[0].Codec.Should().Be("copy");
        streams[0].SourceIndex.Should().Be(1);

        // Re-encodes follow in profile order.
        streams[1].Codec.Should().Be("aac");
        streams[1].Channels.Should().Be(2);
        streams[1].BitrateKbps.Should().Be(192);

        streams[2].Codec.Should().Be("libopus");
        streams[2].Channels.Should().Be(6);
        streams[2].BitrateKbps.Should().Be(256);
        streams[2].OpusVbr.Should().BeTrue();
    }


    // ---------------------------------------------------------------------
    //  Matrix row 2: dedup-to-copy when source already matches the profile.
    // ---------------------------------------------------------------------

    [Fact]
    public void Profile_matching_source_dedupes_to_copy()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: true,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1", BitrateKbps = 448 },
            });

        // Preserve + the dedup'd profile both target source #1 — only one stream emitted.
        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("copy");
        streams[0].SourceIndex.Should().Be(1);
        warnings.Should().BeEmpty();
    }


    // ---------------------------------------------------------------------
    //  Matrix row 3: empty-language safeguard. No output is satisfiable but
    //  the language must not vanish.
    // ---------------------------------------------------------------------

    [Fact]
    public void Unsatisfiable_outputs_force_safeguard_passthrough_with_warning()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "aac", channels: 2, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1", BitrateKbps = 448 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("copy");
        streams[0].SourceIndex.Should().Be(1);

        warnings.Should().Contain(w => w.Contains("skipped") && w.Contains("ac3"));
        warnings.Should().Contain(w => w.Contains("no satisfiable output profiles"));
    }


    // ---------------------------------------------------------------------
    //  Matrix row 4: best-source selection — 7.1 source, two outputs.
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_off_drops_source_and_emits_only_requested_outputs()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "aac", channels: 8, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 0 },
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1",    BitrateKbps = 0 },
            });

        warnings.Should().BeEmpty();
        streams.Should().HaveCount(2);

        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(2);
        streams[0].SourceIndex.Should().Be(1);

        streams[1].Codec.Should().Be("ac3");
        streams[1].Channels.Should().Be(6);
        streams[1].SourceIndex.Should().Be(1);
    }


    // ---------------------------------------------------------------------
    //  Matrix row 5: dedup picks the right source when several exist.
    //  AAC stereo + TrueHD 5.1; outputs ask for AAC stereo + AC3 5.1.
    //  Expectation: AAC stereo dedupes to the AAC stereo source; AC3 5.1
    //  re-encodes from the TrueHD source (best 5.1+ candidate).
    // ---------------------------------------------------------------------

    [Fact]
    public void Multi_source_dedup_picks_the_matching_source_and_truehd_for_re_encode()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "aac",    channels: 2, lang: "eng")
            .Audio(codec: "truehd", channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1",    BitrateKbps = 448 },
            });

        streams.Should().HaveCount(2);

        // AAC stereo dedupes against source #1 (AAC stereo) — emitted as copy first.
        streams[0].Codec.Should().Be("copy");
        streams[0].SourceIndex.Should().Be(1);

        // AC3 5.1 re-encodes from source #2 (TrueHD 5.1) — best 5.1+ source.
        streams[1].Codec.Should().Be("ac3");
        streams[1].SourceIndex.Should().Be(2);
        streams[1].Channels.Should().Be(6);
    }


    // ---------------------------------------------------------------------
    //  Matrix row 6: container fallback. Opus is illegal in MP4, falls
    //  back to AAC and logs a warning.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mp4_with_opus_profile_falls_back_to_aac_and_warns()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            isMatroska: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "opus", Layout = "5.1", BitrateKbps = 256 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(6);

        warnings.Should().Contain(w => w.Contains("opus") && w.Contains("MP4"));
    }


    // ---------------------------------------------------------------------
    //  Matrix row 7: codec channel-cap clamp. AC3 max is 6 channels; the
    //  planner clamps a 7.1 request to 5.1 and warns.
    // ---------------------------------------------------------------------

    [Fact]
    public void Ac3_seven_one_request_clamps_to_five_one()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "aac", channels: 8, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "ac3", Layout = "7.1", BitrateKbps = 448 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("ac3");
        streams[0].Channels.Should().Be(6);

        warnings.Should().Contain(w => w.Contains("ac3") && w.Contains("clamping"));
    }


    // ---------------------------------------------------------------------
    //  Matrix row 8: TrueHD passthrough when preserving + no outputs.
    // ---------------------------------------------------------------------

    [Fact]
    public void Truehd_source_with_preserve_only_passes_through()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "truehd", channels: 8, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe, preserve: true, outputs: null);

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("copy");
        warnings.Should().BeEmpty();
    }


    // ---------------------------------------------------------------------
    //  Matrix row 9: TrueHD with Preserve=off + AAC stereo output. The
    //  TrueHD source is dropped; AAC stereo is encoded from it.
    // ---------------------------------------------------------------------

    [Fact]
    public void Truehd_re_encoded_to_aac_stereo_when_preserve_off()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "truehd", channels: 8, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(2);
        streams[0].SourceIndex.Should().Be(1);
    }


    // ---------------------------------------------------------------------
    //  Matrix row 11: mux-pass eligibility — Preserve=on + no outputs +
    //  no language drops should produce only copies.
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_only_emits_only_copies()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "aac", channels: 2, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe, preserve: true, outputs: null);

        streams.Should().HaveCount(2);
        streams.Should().OnlyContain(s => s.Codec == "copy");
        warnings.Should().BeEmpty();
    }


    // ---------------------------------------------------------------------
    //  Matrix row 13: multi-language safeguard. EN gets the dedup, JA gets
    //  the safeguard pass-through, neither language vanishes.
    // ---------------------------------------------------------------------

    [Fact]
    public void Per_language_safeguard_keeps_each_language_alive()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "aac", channels: 2, lang: "jpn")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            languages: new[] { "en", "ja" },
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1", BitrateKbps = 448 },
            });

        streams.Should().HaveCount(2);
        // EN: AC3 5.1 dedupes against source #1.
        streams.Should().ContainSingle(s => s.SourceIndex == 1 && s.Codec == "copy");
        // JA: AC3 5.1 unsatisfiable (only stereo source) → safeguard copies #2 through.
        streams.Should().ContainSingle(s => s.SourceIndex == 2 && s.Codec == "copy");

        warnings.Should().Contain(w => w.Contains("ja") || w.Contains("jpn"));
    }


    // ---------------------------------------------------------------------
    //  Codec → encoder mapping table. One row per supported codec.
    // ---------------------------------------------------------------------

    public static IEnumerable<object[]> CodecEncoderRows() => new[]
    {
        new object[] { "aac",  "aac",      192 },
        new object[] { "ac3",  "ac3",      448 },
        new object[] { "eac3", "eac3",     384 },
        new object[] { "opus", "libopus",  192 },
    };

    [Theory]
    [MemberData(nameof(CodecEncoderRows))]
    public void Codec_to_encoder_table_emits_the_expected_encoder_and_default_bitrate(
        string codec,
        string expectedEncoder,
        int    expectedDefaultBitrate)
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "flac", channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = codec, Layout = "5.1", BitrateKbps = 0 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be(expectedEncoder);
        streams[0].BitrateKbps.Should().Be(expectedDefaultBitrate);
    }


    // ---------------------------------------------------------------------
    //  Layout → channel-count mapping. One row per supported layout.
    // ---------------------------------------------------------------------

    public static IEnumerable<object[]> LayoutChannelRows() => new[]
    {
        new object[] { "Mono",   1 },
        new object[] { "Stereo", 2 },
        new object[] { "5.1",    6 },
        new object[] { "7.1",    8 },
    };

    [Theory]
    [MemberData(nameof(LayoutChannelRows))]
    public void Layout_emits_matching_channel_count(string layout, int expectedChannels)
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "flac", channels: 8, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = layout, BitrateKbps = 0 },
            });

        streams.Should().ContainSingle();
        streams[0].Channels.Should().Be(expectedChannels);
    }


    [Fact]
    public void Source_layout_inherits_source_channel_count()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "flac", channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Source", BitrateKbps = 0 },
            });

        streams.Should().ContainSingle();
        // "Source" layout falls back to the source's actual channel count (6 for 5.1 FLAC).
        // The planner emits an explicit -ac flag rather than relying on FFmpeg's default,
        // so the output channel count is deterministic regardless of the source codec.
        streams[0].Channels.Should().Be(6);
    }


    // ---------------------------------------------------------------------
    //  Commentary tracks are filtered before the planner ever sees them, so
    //  they should never appear in the output.
    // ---------------------------------------------------------------------

    [Fact]
    public void Commentary_tracks_are_excluded()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng", title: "Director Commentary")
            .Audio(codec: "aac", channels: 2, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe, preserve: true, outputs: null);

        streams.Should().HaveCount(1);
        streams[0].SourceIndex.Should().Be(2);
    }


    // ---------------------------------------------------------------------
    //  Empty source set → empty output, no warnings, no exceptions.
    // ---------------------------------------------------------------------

    [Fact]
    public void No_audio_streams_returns_empty()
    {
        var probe = new ProbeBuilder().Video().Build();
        var (streams, warnings) = Plan(probe, preserve: true, outputs: null);
        streams.Should().BeEmpty();
        warnings.Should().BeEmpty();
    }


    // ---------------------------------------------------------------------
    //  Language filter: drops un-kept languages entirely.
    // ---------------------------------------------------------------------

    [Fact]
    public void Language_filter_drops_un_kept_languages()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "fre")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: true,
            outputs: null,
            languages: new[] { "en" });

        streams.Should().ContainSingle();
        streams[0].SourceIndex.Should().Be(1);
    }


    // ---------------------------------------------------------------------
    //  Unknown codec falls back to AAC.
    // ---------------------------------------------------------------------

    // ---------------------------------------------------------------------
    //  MP4 + source codec the container can't carry → forced re-encode.
    //  The TrueHD source can't be copied into MP4 (only aac/ac3/eac3/mp3/alac
    //  can), so Preserve=on + MP4 emits an AAC re-encode at the source's
    //  channel count instead of a -c:a copy line.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mp4_preserve_on_uncopyable_source_emits_aac_re_encode_with_warning()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "truehd", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: true,
            outputs: null,
            isMatroska: false);

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(6);
        warnings.Should().Contain(w => w.Contains("truehd") && w.Contains("MP4"));
    }


    // ---------------------------------------------------------------------
    //  Preserve=off + empty output list → safeguard pass-through to keep
    //  the language alive, with a warning.
    // ---------------------------------------------------------------------

    [Fact]
    public void Preserve_off_with_empty_outputs_falls_through_to_safeguard()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            outputs: Array.Empty<AudioOutputProfile>());

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("copy");
        warnings.Should().Contain(w => w.Contains("no satisfiable output profiles"));
    }


    // ---------------------------------------------------------------------
    //  Tiebreak: multiple sources of the same codec but different channel
    //  counts. A 5.1 EAC3 output target should pick the 5.1 source over
    //  the stereo source, even if the stereo source comes first.
    // ---------------------------------------------------------------------

    [Fact]
    public void Re_encode_picks_highest_channel_source_when_codec_is_the_same()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 2, lang: "eng")     // stereo first
            .Audio(codec: "ac3", channels: 6, lang: "eng")     // 5.1 second
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "eac3", Layout = "5.1", BitrateKbps = 384 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("eac3");
        streams[0].Channels.Should().Be(6);
        // Source #2 is the 5.1 AC3 — that's the source the re-encode pulls from.
        streams[0].SourceIndex.Should().Be(2);
    }


    // ---------------------------------------------------------------------
    //  Tiebreak when channel counts match: prefer lossless > lossy as the
    //  re-encode source. FLAC source beats AAC source for an EAC3 5.1 target.
    // ---------------------------------------------------------------------

    [Fact]
    public void Re_encode_prefers_lossless_source_at_equal_channel_count()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "aac",  channels: 6, lang: "eng")    // lossy 5.1
            .Audio(codec: "flac", channels: 6, lang: "eng")    // lossless 5.1
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "eac3", Layout = "5.1", BitrateKbps = 384 },
            });

        streams.Should().ContainSingle();
        streams[0].SourceIndex.Should().Be(2);  // FLAC source
    }


    // ---------------------------------------------------------------------
    //  Empty / null language keep-list — the bucket-by-source-language path
    //  that covers the "keep all" case used by mux-pass code (passes
    //  `new List<string>()` to mean keep everything).
    // ---------------------------------------------------------------------

    [Fact]
    public void Null_language_list_buckets_by_source_language_tag()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "fre")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: true,
            outputs: null,
            languages: KeepAll, languagesSet: true);   // null → keep all, bucket per source language tag

        // Both source tracks survive — one bucket per language.
        streams.Should().HaveCount(2);
        streams.Should().OnlyContain(s => s.Codec == "copy");
    }


    [Fact]
    public void Empty_language_list_buckets_by_source_language_tag()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "fre")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: true,
            outputs: null,
            languages: Array.Empty<string>(), languagesSet: true);

        streams.Should().HaveCount(2);
    }


    [Fact]
    public void Source_with_null_language_tag_buckets_under_und()
    {
        // The bucket-by-source-language path falls back to "und" for missing tags;
        // such a track must still pass through under preserve.
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: null!)
            .Build();

        var (streams, warnings) = Plan(probe, preserve: true, outputs: null,
            languages: KeepAll, languagesSet: true);

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("copy");
        warnings.Should().BeEmpty();
    }


    [Fact]
    public void No_keep_list_with_per_language_outputs_emits_one_set_per_bucket()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "jpn")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
            },
            languages: KeepAll, languagesSet: true);

        // Each language bucket runs the profile → 2 AAC stereo encodes,
        // one from the EN source and one from the JA source.
        streams.Should().HaveCount(2);
        streams.Should().OnlyContain(s => s.Codec == "aac" && s.Channels == 2);
        streams.Select(s => s.SourceIndex).Should().BeEquivalentTo(new[] { 1, 2 });
    }


    // ---------------------------------------------------------------------
    //  Whitespace / case normalization on profile.Codec and profile.Layout.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(" aac ", "STEREO")]
    [InlineData("AAC",   "stereo")]
    [InlineData("  AAC", "Stereo ")]
    public void Profile_fields_are_normalized_case_and_whitespace(string codec, string layout)
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[] { new AudioOutputProfile { Codec = codec, Layout = layout, BitrateKbps = 0 } });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(2);
    }


    [Fact]
    public void Profile_with_null_or_empty_codec_is_dropped()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        // Null/whitespace-only profiles are filtered out; only the AAC profile survives.
        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = null!,   Layout = "Stereo" },
                new AudioOutputProfile { Codec = "  ",    Layout = "Stereo" },
                new AudioOutputProfile { Codec = "aac",   Layout = "Stereo", BitrateKbps = 192 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
    }


    // ---------------------------------------------------------------------
    //  Identical profiles produce two distinct output streams. Pinning the
    //  current (non-deduplicating) behavior so a future change is intentional.
    // ---------------------------------------------------------------------

    [Fact]
    public void Identical_profiles_emit_two_streams()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var p = new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 };
        var (streams, _) = Plan(probe, preserve: false, outputs: new[] { p, p });

        streams.Should().HaveCount(2);
        streams.Should().OnlyContain(s => s.Codec == "aac" && s.Channels == 2);
    }


    // ---------------------------------------------------------------------
    //  Source-codec quality table coverage. The lossless / DTS / lossy
    //  arms tiebreak the "best source for re-encode" pick when channel
    //  counts match.
    // ---------------------------------------------------------------------

    /// <summary>
    ///     Rows: (lossy source codec, "better" source codec, output profile codec).
    ///     The output codec is chosen per row so it matches NEITHER source — that way
    ///     dedup-to-copy can't short-circuit and the picker actually has to choose.
    /// </summary>
    public static IEnumerable<object[]> QualityTiebreakRows() => new[]
    {
        // Lossless > everything
        new object[] { "aac",  "flac",   "opus" },
        new object[] { "ac3",  "truehd", "aac"  },
        // DTS > AC3/EAC3
        new object[] { "ac3",  "dts",    "aac"  },
        new object[] { "eac3", "dtshd",  "aac"  },
        // EAC3 / AC3 > AAC / Opus / MP3
        new object[] { "aac",  "eac3",   "opus" },
        new object[] { "opus", "ac3",    "aac"  },
    };

    [Theory]
    [MemberData(nameof(QualityTiebreakRows))]
    public void Re_encode_source_picker_prefers_higher_quality_at_equal_channels(
        string lossy, string better, string outputCodec)
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: lossy,  channels: 6, lang: "eng")    // listed first
            .Audio(codec: better, channels: 6, lang: "eng")    // higher quality
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[] { new AudioOutputProfile { Codec = outputCodec, Layout = "5.1" } });

        streams.Should().ContainSingle();
        streams[0].SourceIndex.Should().Be(2);   // the higher-quality source
    }


    [Fact]
    public void Source_codec_outside_quality_table_still_works_as_fallback_source()
    {
        // Vorbis isn't in the explicit quality table — the picker falls into the lossy
        // bucket (quality 1). With only a vorbis source, it's still a valid re-encode source.
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "vorbis", channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[] { new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 } });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        streams[0].Channels.Should().Be(2);
    }


    [Fact]
    public void Source_codec_with_unknown_quality_loses_tiebreak_to_known_codec()
    {
        // PCM_F32LE isn't in the quality table → quality 0 (default arm).
        // FLAC is quality 4. Picker should prefer FLAC.
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "pcm_f32le", channels: 6, lang: "eng")
            .Audio(codec: "flac",      channels: 6, lang: "eng")
            .Build();

        var (streams, _) = Plan(probe,
            preserve: false,
            outputs: new[] { new AudioOutputProfile { Codec = "eac3", Layout = "5.1" } });

        streams.Should().ContainSingle();
        streams[0].SourceIndex.Should().Be(2);
    }


    [Fact]
    public void Unknown_codec_falls_back_to_aac_with_warning()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Audio(codec: "flac", channels: 6, lang: "eng")
            .Build();

        var (streams, warnings) = Plan(probe,
            preserve: false,
            outputs: new[]
            {
                new AudioOutputProfile { Codec = "wma", Layout = "Stereo", BitrateKbps = 192 },
            });

        streams.Should().ContainSingle();
        streams[0].Codec.Should().Be("aac");
        warnings.Should().Contain(w => w.Contains("wma") && w.Contains("AAC"));
    }
}
