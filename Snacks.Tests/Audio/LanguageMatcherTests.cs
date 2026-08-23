using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Audio;

/// <summary>
///     <see cref="LanguageMatcher"/> is the workhorse behind every audio + subtitle
///     filter and the canonical sidecar-filename language tag. The class has four
///     conversion functions and a two-arg <c>Matches</c> with an title-fallback
///     inference path, so the surface is small but easy to break silently.
/// </summary>
public sealed class LanguageMatcherTests
{
    // =====================================================================
    //  ToTwoLetter — accepts 2-letter / 3-letter T / 3-letter B / English name.
    // =====================================================================

    /// <summary>
    ///     Rows: (raw input, expected canonical 2-letter — null when unknown).
    ///     Drives the alias map across all four input forms plus case/whitespace
    ///     normalization.
    /// </summary>
    public static IEnumerable<object?[]> ToTwoLetterRows() => new[]
    {
        // 2-letter passthrough
        new object?[] { "en",       "en" },
        new object?[] { "EN",       "en" },
        new object?[] { " en ",     "en" },

        // 3-letter terminological (T)
        new object?[] { "eng",      "en" },
        new object?[] { "fra",      "fr" },
        new object?[] { "deu",      "de" },
        new object?[] { "zho",      "zh" },

        // 3-letter bibliographic (B) — French/German/Chinese have distinct B codes
        new object?[] { "fre",      "fr" },
        new object?[] { "ger",      "de" },
        new object?[] { "chi",      "zh" },
        new object?[] { "dut",      "nl" },
        new object?[] { "cze",      "cs" },
        new object?[] { "gre",      "el" },

        // Languages without distinct B codes still work via T
        new object?[] { "spa",      "es" },
        new object?[] { "jpn",      "ja" },

        // English names — case-insensitive
        new object?[] { "English",  "en" },
        new object?[] { "english",  "en" },
        new object?[] { "FRENCH",   "fr" },
        new object?[] { "Japanese", "ja" },

        // Unknown / blank → null
        new object?[] { "",         null },
        new object?[] { "   ",      null },
        new object?[] { null,       null },
        new object?[] { "qaa",      null },   // ISO private-use
        new object?[] { "klingon",  null },
        new object?[] { "xx",       null },
    };

    [Theory]
    [MemberData(nameof(ToTwoLetterRows))]
    public void ToTwoLetter(string? raw, string? expected)
    {
        LanguageMatcher.ToTwoLetter(raw).Should().Be(expected);
    }


    // =====================================================================
    //  ToThreeLetterB — bibliographic form preferred, falls back to T.
    // =====================================================================

    /// <summary>Rows: (raw, expected B-form).</summary>
    public static IEnumerable<object?[]> ToThreeLetterBRows() => new[]
    {
        // Languages with distinct B codes return B
        new object?[] { "fr",       "fre" },
        new object?[] { "fra",      "fre" },
        new object?[] { "French",   "fre" },
        new object?[] { "de",       "ger" },
        new object?[] { "zh",       "chi" },
        new object?[] { "nl",       "dut" },

        // Languages without distinct B codes return T
        new object?[] { "en",       "eng" },
        new object?[] { "es",       "spa" },
        new object?[] { "ja",       "jpn" },

        // Unknown → null
        new object?[] { "klingon",  null  },
        new object?[] { null,       null  },
    };

    [Theory]
    [MemberData(nameof(ToThreeLetterBRows))]
    public void ToThreeLetterB(string? raw, string? expected)
    {
        LanguageMatcher.ToThreeLetterB(raw).Should().Be(expected);
    }


    // =====================================================================
    //  ToThreeLetterT — terminological form (what most tools use).
    // =====================================================================

    [Theory]
    [InlineData("fr",       "fra")]
    [InlineData("fre",      "fra")]
    [InlineData("French",   "fra")]
    [InlineData("en",       "eng")]
    [InlineData("zh",       "zho")]
    [InlineData("klingon",  null)]
    public void ToThreeLetterT(string raw, string? expected)
    {
        LanguageMatcher.ToThreeLetterT(raw).Should().Be(expected);
    }


    // =====================================================================
    //  ToEnglishName.
    // =====================================================================

    [Theory]
    [InlineData("en",  "English")]
    [InlineData("eng", "English")]
    [InlineData("fra", "French")]
    [InlineData("fre", "French")]
    [InlineData("ja",  "Japanese")]
    [InlineData("xx",  null)]
    public void ToEnglishName(string raw, string? expected)
    {
        LanguageMatcher.ToEnglishName(raw).Should().Be(expected);
    }


    // =====================================================================
    //  InferFromTitle — used for bitmap subtitle tracks with missing tags.
    // =====================================================================

    /// <summary>
    ///     Rows: (raw title, expected 2-letter). Whole-title resolves first,
    ///     then split-on-separators picks the first matching token.
    /// </summary>
    public static IEnumerable<object?[]> InferFromTitleRows() => new[]
    {
        new object?[] { "English",          "en" },
        new object?[] { "English SDH",      "en" },
        new object?[] { "English (Forced)", "en" },
        new object?[] { "[English]",        "en" },
        new object?[] { "Eng",              "en" },     // 3-letter token in a title
        new object?[] { "French/Forced",    "fr" },
        new object?[] { "Director's Cut, English", "en" },

        // No language token present
        new object?[] { "Commentary",       null },
        new object?[] { "Director's Cut",   null },
        new object?[] { "",                 null },
        new object?[] { null,               null },
    };

    [Theory]
    [MemberData(nameof(InferFromTitleRows))]
    public void InferFromTitle(string? title, string? expected)
    {
        LanguageMatcher.InferFromTitle(title).Should().Be(expected);
    }


    // =====================================================================
    //  Matches — the predicate every audio/subtitle filter calls.
    // =====================================================================

    [Fact]
    public void Matches_with_null_keep_list_keeps_everything()
    {
        LanguageMatcher.Matches("eng", null, null).Should().BeTrue();
        LanguageMatcher.Matches(null,  null, null).Should().BeTrue();
        LanguageMatcher.Matches("zzz", null, null).Should().BeTrue();
    }


    [Fact]
    public void Matches_with_empty_keep_list_keeps_everything()
    {
        LanguageMatcher.Matches("eng", null, Array.Empty<string>()).Should().BeTrue();
    }


    /// <summary>
    ///     Rows: (track language tag, wanted 2-letter, expected match).
    ///     Drives the matrix of "tag form × keep-list form" — both are
    ///     normalized to canonical 2-letter for comparison.
    /// </summary>
    public static IEnumerable<object?[]> MatchesByLanguageRows() => new[]
    {
        new object?[] { "eng", "en", true  },     // T form vs 2-letter keep
        new object?[] { "fre", "fr", true  },     // B form vs 2-letter keep
        new object?[] { "FRA", "fr", true  },     // case-insensitive
        new object?[] { "en",  "en", true  },
        new object?[] { "fra", "en", false },
        new object?[] { "jpn", "en", false },
        new object?[] { "",    "en", false },
        new object?[] { null,  "en", false },
    };

    [Theory]
    [MemberData(nameof(MatchesByLanguageRows))]
    public void Matches_by_language_tag(string? trackLang, string wanted, bool expected)
    {
        LanguageMatcher.Matches(trackLang, null, new[] { wanted }).Should().Be(expected);
    }


    [Fact]
    public void Matches_falls_back_to_title_inference_when_tag_is_und()
    {
        // PGS / VobSub tracks routinely have language="und" and the language only
        // in the title — Matches must consult the title in that case.
        LanguageMatcher.Matches("und", "English SDH", new[] { "en" }).Should().BeTrue();
        LanguageMatcher.Matches("und", "Forced French", new[] { "fr" }).Should().BeTrue();
        LanguageMatcher.Matches("und", "Commentary",  new[] { "en" }).Should().BeFalse();
    }


    [Fact]
    public void Matches_with_unknown_tag_falls_back_to_raw_string_comparison()
    {
        // Exotic tags (qaa is ISO private-use) aren't in the alias map; the matcher
        // falls back to a case-insensitive exact-string comparison so a user who
        // typed "qaa" in their keep list still gets those tracks.
        LanguageMatcher.Matches("qaa", null, new[] { "qaa" }).Should().BeTrue();
        LanguageMatcher.Matches("qaa", null, new[] { "QAA" }).Should().BeTrue();
        LanguageMatcher.Matches("qaa", null, new[] { "en" }).Should().BeFalse();
    }


    // =====================================================================
    //  Undetermined sentinel ("und" chip) — KeepEntryIndex claim precedence.
    // =====================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("und")]
    [InlineData("UND")]
    [InlineData("mul")]
    [InlineData("zxx")]
    [InlineData("not-a-language")]
    public void Und_entry_matches_tracks_with_no_determinable_language(string? tag)
    {
        LanguageMatcher.Matches(tag, null, new[] { "en", "und" }).Should().BeTrue();
        // Regression: without the und entry these tracks stay dropped, exactly as
        // before — the silent-movie safeguard downstream depends on that.
        LanguageMatcher.Matches(tag, null, new[] { "en" }).Should().BeFalse();
    }


    [Fact]
    public void Und_entry_does_not_claim_determinable_tracks()
    {
        // A resolvable language that isn't in the keep list stays dropped — the
        // und chip means "no chip could claim it", not "keep everything".
        LanguageMatcher.Matches("fra", null, new[] { "en", "und" }).Should().BeFalse();

        // Title inference claims the track as English, so und can't swallow it —
        // it matches iff "en" is kept, same as before the sentinel existed.
        LanguageMatcher.Matches("und", "English SDH", new[] { "fr", "und" }).Should().BeFalse();
        LanguageMatcher.Matches("und", "English SDH", new[] { "en" }).Should().BeTrue();
    }


    [Fact]
    public void KeepEntryIndex_resolves_claims_by_precedence()
    {
        // Canonical claim (tag or title resolves to a known language) wins over und.
        LanguageMatcher.KeepEntryIndex("und", "English SDH", new[] { "und", "en" }).Should().Be(1);
        // Raw-exact claim beats und regardless of chip order.
        LanguageMatcher.KeepEntryIndex("qaa", null, new[] { "und", "qaa" }).Should().Be(1);
        // Unclaimed unresolvable tags land on the und entry.
        LanguageMatcher.KeepEntryIndex("mul", null, new[] { "en", "und" }).Should().Be(1);
        LanguageMatcher.KeepEntryIndex(null,  null, new[] { "en", "und" }).Should().Be(1);
        // A literal "und" tag is itself undetermined, not a raw-exact match.
        LanguageMatcher.KeepEntryIndex("und", null, new[] { "und", "en" }).Should().Be(0);
        // Resolvable language absent from the keep list → no claim at all.
        LanguageMatcher.KeepEntryIndex("fra", null, new[] { "en", "und" }).Should().BeNull();
    }


    [Fact]
    public void IsUndeterminedKeepEntry_accepts_env_aliases()
    {
        // Env overrides bypass the chip UI's normalization, so the free-typed
        // aliases must be recognized too.
        LanguageMatcher.IsUndeterminedKeepEntry("und").Should().BeTrue();
        LanguageMatcher.IsUndeterminedKeepEntry("Undetermined").Should().BeTrue();
        LanguageMatcher.IsUndeterminedKeepEntry("UNKNOWN").Should().BeTrue();
        LanguageMatcher.IsUndeterminedKeepEntry("en").Should().BeFalse();
        LanguageMatcher.IsUndeterminedKeepEntry(null).Should().BeFalse();
    }


    [Fact]
    public void Matches_normalizes_keep_list_entries()
    {
        // Keep-list entries are canonicalized the same way track tags are, so an
        // API- or env-provided "eng" / "English" entry matches the same tracks the
        // 2-letter "en" chip would. (The chip UI still stores 2-letter codes; this
        // matters for env overrides and API callers, and keeps Matches consistent
        // with the preference-ordering path, which always normalized entries.)
        LanguageMatcher.Matches("eng", null, new[] { "en" }).Should().BeTrue();
        LanguageMatcher.Matches("eng", null, new[] { "eng" }).Should().BeTrue();
        LanguageMatcher.Matches("eng", null, new[] { "English" }).Should().BeTrue();
    }


    // =====================================================================
    //  IsSdhTitle — title-based fallback when ffprobe's disposition is silent.
    // =====================================================================

    [Theory]
    [InlineData("English SDH",                true)]
    [InlineData("English [SDH]",              true)]
    [InlineData("English (CC)",               true)]
    [InlineData("Hearing Impaired",           true)]
    [InlineData("English - Hearing-Impaired", true)]
    [InlineData("English HoH",                true)]
    [InlineData("English HI",                 true)]
    [InlineData("English",                    false)]
    [InlineData("Director's Commentary",      false)]
    [InlineData("Chichewa",                   false)] // contains "hi" as a substring but not a word
    [InlineData("",                           false)]
    [InlineData(null,                         false)]
    public void IsSdhTitle_matches_common_markers(string? title, bool expected)
    {
        LanguageMatcher.IsSdhTitle(title).Should().Be(expected);
    }
}
