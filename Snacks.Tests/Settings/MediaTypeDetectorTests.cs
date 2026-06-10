using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Pins title/year extraction used for TMDb/TVDB lookups. The year must be
///     recovered from BOTH common naming conventions — the Arr-standard
///     "Title (Year)" form used to lose its year because trailing parens were
///     stripped before the year regex ran, which crippled disambiguation for
///     remakes (1982 Tron vs 2010 TRON: Legacy).
/// </summary>
public sealed class MediaTypeDetectorTests
{
    [Theory]
    [InlineData("/movies/TRON Legacy (2010).mkv",            "TRON Legacy",          2010)]
    [InlineData("/movies/TRON.Legacy.2010.1080p.BluRay.mkv", "TRON Legacy",          2010)]
    [InlineData("/movies/Blade Runner 2049 (2017).mkv",      "Blade Runner 2049",    2017)]
    [InlineData("/movies/2001 A Space Odyssey (1968).mkv",   "2001 A Space Odyssey", 1968)]
    public void ExtractMovieTitle_recovers_year_from_both_naming_conventions(
        string path, string expectedTitle, int expectedYear)
    {
        var (title, year) = MediaTypeDetector.ExtractMovieTitle(path);
        title.Should().Be(expectedTitle);
        year.Should().Be(expectedYear);
    }

    [Fact]
    public void ExtractMovieTitle_without_year_returns_full_title_and_null_year()
    {
        var (title, year) = MediaTypeDetector.ExtractMovieTitle("/movies/The Matrix.mkv");
        title.Should().Be("The Matrix");
        year.Should().BeNull();
    }

    [Fact]
    public void ExtractMovieTitle_never_produces_empty_title_for_leading_year()
    {
        // A year-like token at position 0 is part of the title, not a release year.
        var (title, year) = MediaTypeDetector.ExtractMovieTitle("/movies/1917.mkv");
        title.Should().Be("1917");
        year.Should().BeNull();
    }
}
