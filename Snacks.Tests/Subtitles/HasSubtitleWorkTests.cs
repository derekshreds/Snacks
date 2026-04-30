using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Subtitles;

/// <summary>
///     Mux-pass eligibility for the subtitle leg. Mirror of <c>HasAudioWorkTests</c>: the
///     three independent gates are language drop, sidecar extraction, and OCR-of-bitmaps.
///     Tests drive each in isolation plus their interactions.
/// </summary>
public sealed class HasSubtitleWorkTests
{
    private static SubtitleStreamSummary Sum(string codec, string lang = "eng", string? title = null) =>
        new() { CodecName = codec, Language = lang, Title = title };

    private static EncoderOptions Opts(
        IEnumerable<string>? keep = null,
        bool sidecar = false,
        bool ocr     = false) => new()
    {
        SubtitleLanguagesToKeep    = keep?.ToList() ?? new(),
        ExtractSubtitlesToSidecar  = sidecar,
        ConvertImageSubtitlesToSrt = ocr,
    };


    [Fact]
    public void No_subtitle_streams_returns_false()
    {
        TranscodingService.HasSubtitleWork(Opts(), Array.Empty<SubtitleStreamSummary>())
            .Should().BeFalse();
    }


    [Fact]
    public void Default_options_with_text_subs_is_no_work()
    {
        var subs = new[] { Sum("subrip", "eng") };
        TranscodingService.HasSubtitleWork(Opts(), subs).Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  Language filter.
    // ---------------------------------------------------------------------

    [Fact]
    public void Language_filter_dropping_a_track_is_work()
    {
        var subs = new[] { Sum("subrip", "eng"), Sum("subrip", "fre") };
        TranscodingService.HasSubtitleWork(Opts(keep: new[] { "en" }), subs).Should().BeTrue();
    }


    [Fact]
    public void Language_filter_keeping_every_track_is_not_work()
    {
        var subs = new[] { Sum("subrip", "eng"), Sum("subrip", "fre") };
        TranscodingService.HasSubtitleWork(Opts(keep: new[] { "en", "fr" }), subs).Should().BeFalse();
    }


    // ---------------------------------------------------------------------
    //  Sidecar extraction. Drives "any text or OCR-able track present" → work.
    // ---------------------------------------------------------------------

    [Fact]
    public void Sidecar_with_a_text_track_is_work()
    {
        TranscodingService.HasSubtitleWork(
            Opts(sidecar: true),
            new[] { Sum("subrip", "eng") })
        .Should().BeTrue();
    }


    [Fact]
    public void Sidecar_with_a_bitmap_track_is_still_work()
    {
        // Sidecar handles bitmap tracks via the OCR-to-SRT path, so any sub track
        // present (text or bitmap) should trigger work when sidecar is on.
        TranscodingService.HasSubtitleWork(
            Opts(sidecar: true),
            new[] { Sum("hdmv_pgs_subtitle", "eng") })
        .Should().BeTrue();
    }


    // ---------------------------------------------------------------------
    //  OCR-only path: must actually have a bitmap track to produce work.
    // ---------------------------------------------------------------------

    /// <summary>Rows: (sub codec, expected work).</summary>
    public static IEnumerable<object[]> OcrCodecRows() => new[]
    {
        // Bitmap codecs trigger work under OCR.
        new object[] { "hdmv_pgs_subtitle", true  },
        new object[] { "pgssub",            true  },
        new object[] { "dvd_subtitle",      true  },
        new object[] { "dvb_subtitle",      true  },
        new object[] { "xsub",              true  },

        // Text codecs are unaffected by OCR.
        new object[] { "subrip",            false },
        new object[] { "ass",               false },
        new object[] { "mov_text",          false },
    };

    [Theory]
    [MemberData(nameof(OcrCodecRows))]
    public void Ocr_only_triggers_work_when_a_bitmap_track_is_present(string codec, bool expected)
    {
        TranscodingService.HasSubtitleWork(
            Opts(ocr: true),
            new[] { Sum(codec, "eng") })
        .Should().Be(expected);
    }


    // ---------------------------------------------------------------------
    //  Language filter narrows the OCR scope. A bitmap French track behind
    //  an English-only filter should not trigger work.
    // ---------------------------------------------------------------------

    [Fact]
    public void Language_filter_narrows_ocr_scope()
    {
        var subs = new[]
        {
            Sum("subrip",            "eng"),
            Sum("hdmv_pgs_subtitle", "fre"),
        };

        // English-only keep: the French PGS gets filtered out before the OCR check.
        // After the filter the survivor is a text English track → no OCR work needed.
        TranscodingService.HasSubtitleWork(
            Opts(keep: new[] { "en" }, ocr: true),
            subs)
        .Should().BeTrue();   // language filter alone drops the French track → work
    }


    [Fact]
    public void Language_filter_keeping_only_text_tracks_in_ocr_mode_is_not_work()
    {
        var subs = new[]
        {
            Sum("subrip", "eng"),
            Sum("subrip", "fre"),
        };

        // No drops, OCR active but no bitmap tracks present → nothing for OCR to do.
        TranscodingService.HasSubtitleWork(
            Opts(keep: new[] { "en", "fr" }, ocr: true),
            subs)
        .Should().BeFalse();
    }
}
