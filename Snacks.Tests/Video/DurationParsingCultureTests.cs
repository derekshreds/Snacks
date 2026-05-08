using System.Globalization;
using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Regression tests for the locale bug where ffprobe/ffmpeg duration strings
///     (which always use '.' as the decimal separator) were parsed under
///     CurrentCulture and either silently inflated by 10^N or failed outright on
///     ',' decimal locales like de-DE — producing 0 kbps bitrates and progress
///     bars stuck near 0%. The fix pins these parses to InvariantCulture.
/// </summary>
public sealed class DurationParsingCultureTests
{
    /// <summary> Run <paramref name="action"/> under the de-DE culture and restore the prior culture afterward. </summary>
    private static void RunInGermanCulture(System.Action action)
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try { action(); }
        finally { CultureInfo.CurrentCulture = prior; }
    }


    [Fact]
    public void Decimal_seconds_string_parses_under_comma_decimal_locale()
    {
        RunInGermanCulture(() =>
        {
            var sut = new FfprobeService();
            sut.DurationStringToSeconds("5953.234567").Should().BeApproximately(5953.234567, 0.0005);
        });
    }


    [Fact]
    public void Hms_with_fractional_seconds_parses_under_comma_decimal_locale()
    {
        RunInGermanCulture(() =>
        {
            var sut = new FfprobeService();
            sut.DurationStringToSeconds("01:39:13.45").Should().BeApproximately(5953.45, 0.0005);
        });
    }
}
