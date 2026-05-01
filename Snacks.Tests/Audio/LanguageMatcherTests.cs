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


    [Fact]
    public void Matches_does_NOT_normalize_keep_list_entries()
    {
        // Pinning surprising behavior: the matcher canonicalizes the *track* tag to
        // its 2-letter form but compares string-equal against the keep-list. So a
        // user who put "eng" or "English" in their keep-list will NOT match a track
        // tagged "eng" — they must use the 2-letter "en". The chip-input UI normalizes
        // to 2-letter on save, so production users don't see this; an API caller might.
        LanguageMatcher.Matches("eng", null, new[] { "en" }).Should().BeTrue();
        LanguageMatcher.Matches("eng", null, new[] { "eng" }).Should().BeFalse();
        LanguageMatcher.Matches("eng", null, new[] { "English" }).Should().BeFalse();
    }
}
