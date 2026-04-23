using System.Text.RegularExpressions;

namespace Snacks.Services;

/// <summary>
///     Classifies a media file path as TV or Movie based on filename/foldername
///     patterns, and extracts the best-effort series/movie title for downstream
///     metadata lookups (TMDb, TVDB).
/// </summary>
public static class MediaTypeDetector
{
    public enum MediaKind { Movie, Tv }

    // Order matters: most specific / least ambiguous patterns first.
    private static readonly Regex[] TvPatterns =
    {
        // S01E02, s1e2, S01 E02, S01.E02
        new(@"[Ss](\d{1,2})[ ._]?[Ee](\d{1,3})",                              RegexOptions.Compiled),
        // 1x02, 01x002 (with word boundaries)
        new(@"(?:^|[ ._\-\[])(\d{1,2})x(\d{1,3})(?:[ ._\-\]]|$)",             RegexOptions.Compiled),
        // Path contains a "Season N" folder
        new(@"[\\/][Ss]eason[ _]?\d+[\\/]",          RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // "Show.EP01", "Show EP01", "Show.E01", "E01v2"
        new(@"(?:^|[ ._\-])[Ee][Pp]?(\d{1,3})(?:v\d)?(?:[ ._\-]|$)",          RegexOptions.Compiled),
        // Anime release-group style: "Show - 01 [1080p]", "Show - 01v2 (source)"
        new(@" - (\d{1,3})(?:v\d)?(?:[ .\[\(]|$)",                            RegexOptions.Compiled),
    };

    private static readonly Regex YearPattern = new(@"(?:^|[ ._\-\(\[])((?:19|20)\d{2})(?:[ ._\-\)\]]|$)", RegexOptions.Compiled);
    private static readonly Regex ReleaseGroupBracketPrefix = new(@"^\s*\[[^\]]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex TrailingBracketsOrParens  = new(@"[\[\(][^\]\)]*[\]\)]", RegexOptions.Compiled);

    /// <summary> Returns <see cref="MediaKind.Tv"/> if any TV pattern matches; otherwise Movie. </summary>
    public static MediaKind Classify(string path)
    {
        if (string.IsNullOrEmpty(path)) return MediaKind.Movie;
        return TvPatterns.Any(r => r.IsMatch(path)) ? MediaKind.Tv : MediaKind.Movie;
    }

    /// <summary>
    ///     Best-effort series title extracted from a TV file path. Strips release-group
    ///     brackets, episode/season markers, and trailing metadata. Falls back to the
    ///     file name sans extension when nothing else matches.
    /// </summary>
    public static string ExtractSeriesTitle(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path) ?? "";
        name = ReleaseGroupBracketPrefix.Replace(name, "");

        // Cut at the first TV pattern hit.
        int cut = -1;
        foreach (var r in TvPatterns)
        {
            var m = r.Match(name);
            if (m.Success && (cut < 0 || m.Index < cut)) cut = m.Index;
        }
        if (cut > 0) name = name.Substring(0, cut);

        // Anime files often use " - " before the episode marker. Trim stray trailing dashes/periods.
        name = name.Trim(' ', '.', '_', '-');
        name = name.Replace('.', ' ').Replace('_', ' ');
        return Regex.Replace(name, @"\s+", " ").Trim();
    }

    /// <summary>
    ///     Best-effort movie title and release year from a file path. Year is <c>null</c>
    ///     when no 4-digit year is present. Title is everything before the year (or the
    ///     whole basename sans extension when no year is present).
    /// </summary>
    public static (string Title, int? Year) ExtractMovieTitle(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path) ?? "";
        name = TrailingBracketsOrParens.Replace(name, " ");

        int? year = null;
        var ym = YearPattern.Match(name);
        if (ym.Success)
        {
            if (int.TryParse(ym.Groups[1].Value, out int y)) year = y;
            name = name.Substring(0, ym.Index);
        }
        name = name.Replace('.', ' ').Replace('_', ' ');
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return (name, year);
    }
}
