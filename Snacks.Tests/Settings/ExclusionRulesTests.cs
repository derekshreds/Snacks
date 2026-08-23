using FluentAssertions;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Settings;

public sealed class ExclusionRulesTests
{
    [Theory]
    [InlineData("Movie.REMUX.2160p.mkv", true)]
    [InlineData("movie.remux.1080p.mkv", true)]   // glob is case-insensitive
    [InlineData("Movie.WEB-DL.1080p.mkv", false)]
    public void Filename_glob_matches_anywhere_in_name(string filename, bool expected)
    {
        var rules = new ExclusionRules { FilenamePatterns = { "*REMUX*" } };
        rules.IsExcluded(filename, sizeBytes: null, resolutionLabel: null).Should().Be(expected);
    }

    [Fact]
    public void Question_mark_glob_matches_single_character()
    {
        var rules = new ExclusionRules { FilenamePatterns = { "track-?.flac" } };
        rules.IsExcluded("track-1.flac", null, null).Should().BeTrue();
        rules.IsExcluded("track-12.flac", null, null).Should().BeFalse();
    }

    [Fact]
    public void Blank_patterns_are_ignored()
    {
        var rules = new ExclusionRules { FilenamePatterns = { "", "   " } };
        rules.IsExcluded("anything.mp3", null, null).Should().BeFalse();
    }

    [Fact]
    public void Size_threshold_excludes_at_and_above_the_limit()
    {
        var rules = new ExclusionRules { MinSizeGBToSkip = 2 };
        rules.IsExcluded("a.mkv", 2L * 1024 * 1024 * 1024, null).Should().BeTrue();
        rules.IsExcluded("a.mkv", 2L * 1024 * 1024 * 1024 - 1, null).Should().BeFalse();
        rules.IsExcluded("a.mkv", sizeBytes: null, null).Should().BeFalse();
    }

    [Fact]
    public void Resolution_label_matches_case_insensitively()
    {
        var rules = new ExclusionRules { ExcludeResolutions = { "2160P" } };
        rules.IsExcluded("a.mkv", null, "2160p").Should().BeTrue();
        rules.IsExcluded("a.mkv", null, "1080p").Should().BeFalse();
    }

    [Fact]
    public void Null_resolution_label_skips_the_resolution_rung()
    {
        // The music path passes resolutionLabel: null — filename and size rungs
        // must still apply while the resolution rung degrades to a no-op.
        var rules = new ExclusionRules
        {
            FilenamePatterns   = { "*live*" },
            MinSizeGBToSkip    = 1,
            ExcludeResolutions = { "1080p" },
        };
        rules.IsExcluded("album-live-01.flac", 100, resolutionLabel: null).Should().BeTrue();
        rules.IsExcluded("album-01.flac", 2L * 1024 * 1024 * 1024, resolutionLabel: null).Should().BeTrue();
        rules.IsExcluded("album-01.flac", 100, resolutionLabel: null).Should().BeFalse();
    }

    [Theory]
    [InlineData(3840, 2160, "2160p")]
    [InlineData(1920, 1080, "1080p")]
    [InlineData(1920, 800, "1080p")]   // ultra-wide crop buckets by derived width
    [InlineData(1280, 720, "720p")]
    [InlineData(640, 480, "480p")]
    [InlineData(0, 1080, null)]
    public void ClassifyResolution_buckets_by_effective_height(int width, int height, string? expected)
    {
        ExclusionRules.ClassifyResolution(width, height).Should().Be(expected);
    }
}
