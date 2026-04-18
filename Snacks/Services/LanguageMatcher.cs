namespace Snacks.Services;

/// <summary>
///     Maps between the three language representations that can appear in user
///     settings and media-container language tags: ISO 639-1 (2-letter, the
///     canonical stored form), ISO 639-2/T and 639-2/B (3-letter, often used
///     by MKV/MP4 tags), and the English name.
/// </summary>
/// <remarks>
///     A stored 2-letter code matches a track whose tag is any of the three
///     forms for that language. Unknown codes are not silently dropped —
///     <see cref="Matches"/> falls back to case-insensitive exact-string
///     comparison so exotic tags (e.g. private-use <c>qaa</c>) still work when
///     the user types the same string back.
/// </remarks>
public static class LanguageMatcher
{
    /// <summary> A single row in the language table. </summary>
    /// <param name="TwoLetter">    ISO 639-1 code, the canonical stored form.       </param>
    /// <param name="ThreeLetterT"> ISO 639-2/T (terminological) code.                </param>
    /// <param name="ThreeLetterB"> ISO 639-2/B (bibliographic) code, or <c>null</c>. </param>
    /// <param name="EnglishName">  Human-readable English name of the language.     </param>
    public sealed record Entry(string TwoLetter, string ThreeLetterT, string? ThreeLetterB, string EnglishName);

    /// <summary>
    ///     Ordered seed of common languages. Hand-maintained; additions are
    ///     cheap. Keep this in sync with <c>wwwroot/js/settings/iso-languages.js</c>.
    /// </summary>
    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry("en", "eng", null,  "English"),
        new Entry("es", "spa", null,  "Spanish"),
        new Entry("ja", "jpn", null,  "Japanese"),
        new Entry("fr", "fra", "fre", "French"),
        new Entry("de", "deu", "ger", "German"),
        new Entry("it", "ita", null,  "Italian"),
        new Entry("pt", "por", null,  "Portuguese"),
        new Entry("ru", "rus", null,  "Russian"),
        new Entry("zh", "zho", "chi", "Chinese"),
        new Entry("ko", "kor", null,  "Korean"),
        new Entry("ar", "ara", null,  "Arabic"),
        new Entry("hi", "hin", null,  "Hindi"),
        new Entry("nl", "nld", "dut", "Dutch"),
        new Entry("sv", "swe", null,  "Swedish"),
        new Entry("no", "nor", null,  "Norwegian"),
        new Entry("da", "dan", null,  "Danish"),
        new Entry("fi", "fin", null,  "Finnish"),
        new Entry("pl", "pol", null,  "Polish"),
        new Entry("tr", "tur", null,  "Turkish"),
        new Entry("cs", "ces", "cze", "Czech"),
        new Entry("hu", "hun", null,  "Hungarian"),
        new Entry("el", "ell", "gre", "Greek"),
        new Entry("he", "heb", null,  "Hebrew"),
        new Entry("th", "tha", null,  "Thai"),
        new Entry("vi", "vie", null,  "Vietnamese"),
        new Entry("id", "ind", null,  "Indonesian"),
        new Entry("uk", "ukr", null,  "Ukrainian"),
    };

    // Normalized (lowercase) alias -> 2-letter code. Built once from Entries.
    private static readonly Dictionary<string, string> _aliasToTwoLetter = BuildAliasMap();

    private static Dictionary<string, string> BuildAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            map[e.TwoLetter]                   = e.TwoLetter;
            map[e.ThreeLetterT]                = e.TwoLetter;
            if (e.ThreeLetterB != null) map[e.ThreeLetterB] = e.TwoLetter;
            map[e.EnglishName.ToLowerInvariant()] = e.TwoLetter;
        }
        return map;
    }

    /// <summary>
    ///     Returns the canonical 2-letter code for <paramref name="raw"/>, or
    ///     <see langword="null"/> if <paramref name="raw"/> is blank or not a
    ///     known language alias. Accepts 2-letter, 3-letter (T or B), and
    ///     English-name forms, case-insensitive.
    /// </summary>
    public static string? ToTwoLetter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = raw.Trim().ToLowerInvariant();
        return _aliasToTwoLetter.TryGetValue(key, out var two) ? two : null;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when <paramref name="trackLang"/>
    ///     should be kept given the user-selected languages
    ///     <paramref name="wantedTwoLetter"/>.
    /// </summary>
    /// <remarks>
    ///     A null or empty <paramref name="wantedTwoLetter"/> keeps every
    ///     track. Otherwise the track's tag is canonicalized and compared
    ///     against the list. If the track tag can't be canonicalized (exotic
    ///     code not in the table), falls back to a case-insensitive exact
    ///     match against the raw entries in <paramref name="wantedTwoLetter"/>.
    /// </remarks>
    public static bool Matches(string? trackLang, IReadOnlyList<string>? wantedTwoLetter)
    {
        if (wantedTwoLetter == null || wantedTwoLetter.Count == 0) return true;

        var trackTwo = ToTwoLetter(trackLang);
        if (trackTwo != null)
        {
            foreach (var w in wantedTwoLetter)
                if (string.Equals(w, trackTwo, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        if (string.IsNullOrWhiteSpace(trackLang)) return false;
        var trackRaw = trackLang.Trim();
        foreach (var w in wantedTwoLetter)
            if (string.Equals(w, trackRaw, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
