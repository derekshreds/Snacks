using FluentAssertions;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Tests for <see cref="VideoFilterBuilder.Emit"/> and the static HDR detector. Both are pure
///     functions over a small input space, so the suite is mostly driven by data rows.
/// </summary>
public sealed class VideoFilterTests
{
    /// <summary>
    ///     Rows: (cropExpr, tonemap, scaleExpr, useVaapi, canHwDecode, vaapiFormat, expected substring assertions).
    ///     <c>expectedFragments</c> are checked with <c>Contain</c> so the test is robust against
    ///     argument-order or whitespace shuffles inside the filter chain.
    /// </summary>
    public static IEnumerable<object?[]> EmitRows() => new[]
    {
        // No filters, no VAAPI → empty string.
        new object?[]
        {
            null, false, null, false, false, "nv12",
            new string[0], true /* expectEmpty */
        },

        // No filters, VAAPI without HW decode → just the upload terminator.
        new object?[]
        {
            null, false, null, true, false, "nv12",
            new[] { "-vf format=nv12|vaapi,hwupload" }, false
        },

        // No filters, VAAPI WITH HW decode → no -vf at all (frames already on GPU).
        new object?[]
        {
            null, false, null, true, true, "nv12",
            new string[0], true
        },

        // Crop only, software encode.
        new object?[]
        {
            "crop=1920:800:0:140", false, null, false, false, "nv12",
            new[] { "-vf crop=1920:800:0:140" }, false
        },

        // Crop + scale, software encode.
        new object?[]
        {
            "crop=1920:800:0:140", false, "scale=w=-2:h=720", false, false, "nv12",
            new[] { "crop=1920:800:0:140", "scale=w=-2:h=720" }, false
        },

        // Tonemap, software encode.
        new object?[]
        {
            null, true, null, false, false, "nv12",
            new[] { VideoFilterBuilder.TonemapSwChain }, false
        },

        // Crop + tonemap + scale + VAAPI hwupload terminator.
        new object?[]
        {
            "crop=1920:800:0:140", true, "scale=w=-2:h=720", true, false, "p010",
            new[] { "crop=1920:800:0:140", "scale=w=-2:h=720", "format=p010|vaapi,hwupload" }, false
        },
    };

    [Theory]
    [MemberData(nameof(EmitRows))]
    public void Emit_renders_expected_chain(
        string?  cropExpr,
        bool     tonemap,
        string?  scaleExpr,
        bool     useVaapi,
        bool     canHwDecode,
        string   vaapiFormat,
        string[] expectedFragments,
        bool     expectEmpty)
    {
        var result = VideoFilterBuilder.Emit(cropExpr, tonemap, scaleExpr, useVaapi, canHwDecode, vaapiFormat);

        if (expectEmpty)
        {
            result.Should().BeEmpty();
        }
        else
        {
            result.Should().StartWith("-vf ");
            foreach (var fragment in expectedFragments)
                result.Should().Contain(fragment);
        }
    }


    /// <summary>Rows: (color_transfer string, expected IsHdr).</summary>
    public static IEnumerable<object?[]> HdrTransferRows() => new[]
    {
        new object?[] { "smpte2084",    true  },   // PQ — HDR10/HDR10+/Dolby Vision
        new object?[] { "arib-std-b67", true  },  // HLG
        new object?[] { "bt709",        false },
        new object?[] { null,           false },
        new object?[] { "",             false },
        new object?[] { "SMPTE2084",    true  },   // case-insensitive
    };

    [Theory]
    [MemberData(nameof(HdrTransferRows))]
    public void IsHdr_detects_pq_and_hlg(string? colorTransfer, bool expected)
    {
        var probe = new ProbeBuilder()
            .Video(colorTransfer: colorTransfer)
            .Build();

        FfprobeService.IsHdr(probe).Should().Be(expected);
    }


    [Fact]
    public void IsHdr_returns_false_when_no_video_stream()
    {
        var probe = new ProbeBuilder()
            .Audio(codec: "aac", channels: 2, lang: "eng")
            .Build();

        FfprobeService.IsHdr(probe).Should().BeFalse();
    }
}
