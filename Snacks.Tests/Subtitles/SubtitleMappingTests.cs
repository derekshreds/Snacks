using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Subtitles;

/// <summary>
///     Tests for <see cref="FfprobeService.MapSub"/> and <see cref="FfprobeService.SelectSidecarStreams"/>.
///     Subtitle handling has three independent gates (container, language filter, bitmap drop)
///     and each combination is exercised below.
/// </summary>
public sealed class SubtitleMappingTests
{
    private readonly FfprobeService _svc = new();

    [Fact]
    public void Mp4_always_strips_subs()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Build();

        _svc.MapSub(probe, new[] { "en" }, container: "mp4").Should().Be("-sn");
    }


    [Fact]
    public void Mkv_text_subs_are_kept()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Build();

        var flags = _svc.MapSub(probe, new[] { "en" }, container: "mkv");
        flags.Should().Contain("-map 0:1");
        flags.Should().Contain("-c:s copy");
    }


    [Fact]
    public void Mkv_drops_bitmap_subs_by_default()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "hdmv_pgs_subtitle", lang: "eng")
            .Build();

        _svc.MapSub(probe, new[] { "en" }, container: "mkv").Should().Be("-sn");
    }


    [Fact]
    public void Mkv_includes_bitmap_subs_when_passthrough_is_on()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "hdmv_pgs_subtitle", lang: "eng")
            .Build();

        var flags = _svc.MapSub(probe, new[] { "en" }, container: "mkv", includeBitmaps: true);
        flags.Should().Contain("-map 0:1");
        flags.Should().Contain("-c:s copy");
    }


    [Fact]
    public void Empty_language_filter_keeps_all_text_subs()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Subtitle(codec: "subrip", lang: "fre")
            .Build();

        var flags = _svc.MapSub(probe, languagesToKeep: null, container: "mkv");
        flags.Should().Contain("-map 0:1");
        flags.Should().Contain("-map 0:2");
    }


    [Fact]
    public void Language_filter_drops_un_kept_subs()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Subtitle(codec: "subrip", lang: "fre")
            .Build();

        var flags = _svc.MapSub(probe, new[] { "en" }, container: "mkv");
        flags.Should().Contain("-map 0:1");
        flags.Should().NotContain("-map 0:2");
    }


    /// <summary>Rows: (codec, isBitmap).</summary>
    public static IEnumerable<object[]> CodecBitmapRows() => new[]
    {
        new object[] { "subrip",            false },
        new object[] { "ass",               false },
        new object[] { "mov_text",          false },
        new object[] { "hdmv_pgs_subtitle", true  },
        new object[] { "pgssub",            true  },
        new object[] { "dvd_subtitle",      true  },
        new object[] { "dvb_subtitle",      true  },
        new object[] { "xsub",              true  },
    };

    [Theory]
    [MemberData(nameof(CodecBitmapRows))]
    public void Sidecar_selection_classifies_codec_correctly(string codec, bool isBitmap)
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: codec, lang: "eng")
            .Build();

        var picks = _svc.SelectSidecarStreams(probe, new[] { "en" }, includeBitmaps: true);

        picks.Should().ContainSingle();
        picks[0].IsBitmap.Should().Be(isBitmap);
    }


    [Fact]
    public void Sidecar_selection_excludes_bitmaps_when_not_opted_in()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip",            lang: "eng")
            .Subtitle(codec: "hdmv_pgs_subtitle", lang: "eng")
            .Build();

        var picks = _svc.SelectSidecarStreams(probe, new[] { "en" }, includeBitmaps: false);

        picks.Should().ContainSingle();
        picks[0].CodecName.Should().Be("subrip");
    }


    [Fact]
    public void Reorders_kept_subs_by_preference()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")   // index 1
            .Subtitle(codec: "subrip", lang: "fre")   // index 2
            .Build();

        var flags = _svc.MapSub(probe, new[] { "fr", "en" }, container: "mkv");
        var fr = flags.IndexOf("-map 0:2", System.StringComparison.Ordinal);
        var en = flags.IndexOf("-map 0:1", System.StringComparison.Ordinal);

        fr.Should().BeGreaterOrEqualTo(0);
        en.Should().BeGreaterOrEqualTo(0);
        fr.Should().BeLessThan(en, because: "French is the higher-priority preference");
    }


    [Fact]
    public void Excludes_sdh_by_disposition_flag()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")                              // index 1
            .Subtitle(codec: "subrip", lang: "eng", hearingImpaired: true)       // index 2
            .Build();

        var flags = _svc.MapSub(probe, new[] { "en" }, container: "mkv", excludeSdh: true);
        flags.Should().Contain("-map 0:1");
        flags.Should().NotContain("-map 0:2");
    }


    [Fact]
    public void Excludes_sdh_by_title_inference()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng", title: "English")            // index 1
            .Subtitle(codec: "subrip", lang: "eng", title: "English [SDH]")      // index 2
            .Build();

        var flags = _svc.MapSub(probe, new[] { "en" }, container: "mkv", excludeSdh: true);
        flags.Should().Contain("-map 0:1");
        flags.Should().NotContain("-map 0:2");
    }


    [Fact]
    public void AutoSetDefault_flags_first_kept_sub_as_default()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Subtitle(codec: "subrip", lang: "fre")
            .Build();

        var flags = _svc.MapSub(probe, new[] { "fr", "en" }, container: "mkv", autoSetDefault: true);
        flags.Should().Contain("-disposition:s:0 default");
        flags.Should().Contain("-disposition:s:1 0");
    }


    [Fact]
    public void SelectSidecarStreams_drops_sdh_when_opted_in()
    {
        var probe = new ProbeBuilder()
            .Video()
            .Subtitle(codec: "subrip", lang: "eng")
            .Subtitle(codec: "subrip", lang: "eng", hearingImpaired: true)
            .Build();

        var picks = _svc.SelectSidecarStreams(probe, new[] { "en" }, includeBitmaps: true, excludeSdh: true);

        picks.Should().ContainSingle();
        picks[0].StreamIndex.Should().Be(1);
    }
}
