using System.Text.RegularExpressions;

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
    ///     cheap. Keep this in sync with <c>wwwroot/js/settings/iso-languages.js</c>
    ///     (which deliberately carries one extra row — <c>und</c> / "Undetermined" —
    ///     so the chip UI can offer it; see <see cref="Undetermined"/>).
    ///
    ///     <para>Never add <c>und</c>, <c>mul</c>, or <c>zxx</c> rows here:
    ///     <see cref="ToTwoLetter"/> must keep returning <see langword="null"/> for
    ///     them so title inference, sidecar naming, and metadata suppression keep
    ///     treating those tags as "no known language".</para>
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
    private static readonly Dictionary<string, Entry>  _twoLetterToEntry = Entries.ToDictionary(e => e.TwoLetter, StringComparer.Ordinal);

    private static Dictionary<string, string> BuildAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in Entries)
        {
            map[e.TwoLetter]    = e.TwoLetter;
            map[e.ThreeLetterT] = e.TwoLetter;
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
    ///     Returns the ISO 639-2/B (bibliographic) 3-letter code for <paramref name="raw"/>,
    ///     falling back to the T (terminological) form when the language has no distinct B
    ///     code, or <see langword="null"/> if the language is unknown. This is the form
    ///     Matroska's <c>language</c> element expects and is what Plex documents for
    ///     sidecar filenames (<c>.eng.srt</c>).
    /// </summary>
    public static string? ToThreeLetterB(string? raw)
    {
        var two = ToTwoLetter(raw);
        if (two == null) return null;
        var e = _twoLetterToEntry[two];
        return e.ThreeLetterB ?? e.ThreeLetterT;
    }

    /// <summary>
    ///     Returns the ISO 639-2/T (terminological) 3-letter code for <paramref name="raw"/>,
    ///     or <see langword="null"/> if unknown. T is what most modern tools — and Tesseract's
    ///     language packs — use.
    /// </summary>
    public static string? ToThreeLetterT(string? raw)
    {
        var two = ToTwoLetter(raw);
        return two == null ? null : _twoLetterToEntry[two].ThreeLetterT;
    }

    /// <summary>
    ///     Returns the English name ("English", "French") for <paramref name="raw"/>, or
    ///     <see langword="null"/> if unknown. Useful for human-readable track titles.
    /// </summary>
    public static string? ToEnglishName(string? raw)
    {
        var two = ToTwoLetter(raw);
        return two == null ? null : _twoLetterToEntry[two].EnglishName;
    }

    /// <summary>
    ///     Infers the canonical 2-letter language code from a free-form track
    ///     title, for bitmap subtitle tracks (PGS, VobSub) that commonly ship
    ///     with a missing or <c>und</c> language tag but a descriptive title
    ///     like <c>"English SDH"</c> or <c>"English (Forced)"</c>.
    /// </summary>
    /// <remarks>
    ///     Tries the whole title first (so <c>"English"</c> resolves directly),
    ///     then splits on common separators and returns the first token that
    ///     resolves to a known language. Returns <see langword="null"/> when no
    ///     token matches — the caller should fall back to <c>und</c> or skip.
    /// </remarks>
    public static string? InferFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var direct = ToTwoLetter(title.Trim());
        if (direct != null) return direct;

        var separators = new[] { ' ', '(', ')', '[', ']', '-', ',', '/', '.', ':' };
        foreach (var token in title.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var m = ToTwoLetter(token);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when <paramref name="trackLang"/>
    ///     should be kept given the user-selected languages
    ///     <paramref name="wantedTwoLetter"/>. See <see cref="Matches(string?, string?, IReadOnlyList{string}?)" />
    ///     for the title-aware form used when the caller has a track title.
    /// </summary>
    public static bool Matches(string? trackLang, IReadOnlyList<string>? wantedTwoLetter)
        => Matches(trackLang, trackTitle: null, wantedTwoLetter);

    // Word-boundary match for the common hearing-impaired markers. The narrow tokens
    // (cc, hi, hoh) only match when surrounded by non-letter delimiters so we don't
    // false-positive on "Chichewa" or words containing "hi". "Sdh" and the
    // multi-word forms are unambiguous enough to allow case-insensitive matches.
    private static readonly Regex _sdhTitleRegex = new(
        @"(?ix)
            \b sdh \b                              |
            \b cc \b                               |
            \b hi \b                               |
            \b hoh \b                              |
            \b hearing [\s\-_]* impaired \b        |
            \b for [\s\-_]* the [\s\-_]* hearing [\s\-_]* impaired \b
        ",
        RegexOptions.Compiled);

    /// <summary>
    ///     Returns <see langword="true"/> when <paramref name="title"/> looks like a
    ///     hearing-impaired / closed-captions / SDH subtitle track. Matches common
    ///     descriptive markers — "SDH", "CC", "HI", "HoH", "Hearing Impaired" — using
    ///     word boundaries so unrelated tokens (e.g. a language name that happens to
    ///     contain "hi") aren't flagged. Used as a fallback when the source doesn't
    ///     set <c>disposition.hearing_impaired</c>, common on Blu-ray rips.
    /// </summary>
    public static bool IsSdhTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return _sdhTitleRegex.IsMatch(title);
    }

    /// <summary>
    ///     Returns <see langword="true"/> when a track should be kept given
    ///     the user-selected languages, consulting the language tag first and
    ///     falling back to an inference from the track title. Bitmap subtitle
    ///     tracks from Blu-ray / DVD rips frequently carry a missing or
    ///     <c>und</c> language tag with a descriptive title like <c>"English SDH"</c>,
    ///     so the title is our only reliable source of language for those.
    /// </summary>
    /// <remarks>
    ///     A null or empty <paramref name="wantedTwoLetter"/> keeps every
    ///     track. Otherwise delegates to <see cref="KeepEntryIndex"/> so
    ///     filtering, bucketing, and ordering all agree on which keep entry
    ///     claims a track.
    /// </remarks>
    public static bool Matches(string? trackLang, string? trackTitle, IReadOnlyList<string>? wantedTwoLetter)
    {
        if (wantedTwoLetter == null || wantedTwoLetter.Count == 0) return true;
        return KeepEntryIndex(trackLang, trackTitle, wantedTwoLetter) != null;
    }

    /// <summary>
    ///     The sentinel keep-list value meaning "tracks whose language no specific
    ///     language entry could claim": a missing/empty tag, a literal <c>und</c> /
    ///     <c>mul</c> / <c>zxx</c>, or an unrecognized tag with no language-bearing
    ///     title. Stored like any other keep entry so overrides, env vars, and chip
    ///     ordering carry it for free — but deliberately absent from
    ///     <see cref="Entries"/> so <see cref="ToTwoLetter"/> stays null for it.
    /// </summary>
    public const string Undetermined = "und";

    /// <summary>
    ///     Whether <paramref name="entry"/> is the "undetermined" keep-list sentinel.
    ///     Accepts the aliases users may type into env overrides (which are not
    ///     validated by the chip UI): <c>und</c>, <c>undetermined</c>, <c>unknown</c>.
    /// </summary>
    public static bool IsUndeterminedKeepEntry(string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return false;
        return entry.Trim().ToLowerInvariant() is "und" or "undetermined" or "unknown";
    }

    /// <summary>
    ///     Returns the index of the keep-list entry that claims this track, or
    ///     <see langword="null"/> when no entry does. Every track is claimed by at
    ///     most ONE entry, resolved in precedence order:
    ///     <list type="number">
    ///         <item><description>Canonical: the tag (or, failing that, the title) resolves
    ///         to a known language matching a non-undetermined entry. A resolvable track
    ///         never falls through to the undetermined entry — an <c>und</c>-tagged track
    ///         titled "English SDH" belongs to <c>en</c>, and a French track with keep
    ///         list [en, und] is dropped, not swallowed by <c>und</c>.</description></item>
    ///         <item><description>Raw-exact: an unresolvable non-blank tag string-equals a
    ///         non-undetermined entry (preserves exotic-tag matching, e.g. private-use
    ///         <c>qaa</c>), regardless of where the undetermined entry sits.</description></item>
    ///         <item><description>Otherwise the undetermined entry, if present.</description></item>
    ///     </list>
    /// </summary>
    public static int? KeepEntryIndex(string? trackLang, string? trackTitle, IReadOnlyList<string>? keepList)
    {
        if (keepList == null || keepList.Count == 0) return null;

        var trackTwo = ToTwoLetter(trackLang) ?? InferFromTitle(trackTitle);
        if (trackTwo != null)
        {
            for (int i = 0; i < keepList.Count; i++)
            {
                if (IsUndeterminedKeepEntry(keepList[i])) continue;
                var entryTwo = ToTwoLetter(keepList[i]) ?? keepList[i]?.Trim();
                if (string.Equals(entryTwo, trackTwo, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return null;
        }

        if (!string.IsNullOrWhiteSpace(trackLang))
        {
            var trackRaw = trackLang.Trim();
            for (int i = 0; i < keepList.Count; i++)
            {
                if (IsUndeterminedKeepEntry(keepList[i])) continue;
                if (string.Equals(keepList[i]?.Trim(), trackRaw, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }

        for (int i = 0; i < keepList.Count; i++)
            if (IsUndeterminedKeepEntry(keepList[i])) return i;

        return null;
    }
}
