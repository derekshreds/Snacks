using System.Text;

namespace Snacks.Services.Ocr;

/// <summary>
///     Cleans up OCR output using a combination of deterministic character-level
///     substitutions (for classic OCR confusions that don't need a dictionary)
///     and frequency-ranked edit-distance-1 correction (for 1-letter errors).
/// </summary>
/// <remarks>
///     <para>
///         Loaded lazily per-language from <c>tools/wordlists/{lang}.txt</c>
///         (one word per line, UTF-8, ordered by frequency most-common-first —
///         the format produced by the bundled <c>download-wordlists.bat</c>).
///         If the file is missing only the deterministic substitutions run.
///     </para>
///     <para>
///         Deterministic substitutions handle:
///         <list type="bullet">
///             <item>Standalone <c>|</c> → <c>I</c> (by far the most common OCR mistake).</item>
///             <item>Bare <c>=</c> at start of a dialog line → <c>-</c>
///                   (dialog-hyphen misread).</item>
///         </list>
///     </para>
///     <para>
///         Dictionary correction heuristics:
///         <list type="bullet">
///             <item>Words under 3 characters are only corrected if they contain
///                   ambiguous characters (<c>|</c>, digits).</item>
///             <item>Mid-sentence title-case words (proper nouns: <c>Alan</c>,
///                   <c>ENCOM</c>, <c>Nikkei</c>) are never corrected.</item>
///             <item>Among edit-1 candidates in the dictionary, the <b>first</b>
///                   match wins — and since the wordlist is frequency-sorted,
///                   that's the most common word, which is nearly always the
///                   right answer for subtitle vocabulary.</item>
///         </list>
///     </para>
/// </remarks>
public sealed class SubtitleSpellChecker
{
    // Rank = index in the loaded list. Lower rank = more frequent word.
    private readonly Dictionary<string, int> _rank;

    private SubtitleSpellChecker(Dictionary<string, int> rank) { _rank = rank; }

    /// <summary>
    ///     Loads the word list for <paramref name="tessLang"/> (e.g. <c>"eng"</c>).
    ///     Returns a checker even if the file is missing — the deterministic
    ///     substitution pass still runs, so <c>|</c> → <c>I</c> etc. still fire.
    /// </summary>
    public static SubtitleSpellChecker LoadFor(string tessLang)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var path = Path.Combine(AppContext.BaseDirectory, "wordlists", $"{tessLang}.txt");

        if (File.Exists(path))
        {
            int i = 0;
            foreach (var line in File.ReadLines(path))
            {
                var w = line.Trim().ToLowerInvariant();
                if (w.Length == 0) continue;
                // First occurrence wins — keeps the frequency rank meaningful.
                if (!rank.ContainsKey(w)) rank[w] = i++;
            }
        }
        return new SubtitleSpellChecker(rank);
    }

    /// <summary> Walks <paramref name="text"/> token-by-token and returns a cleaned-up copy. </summary>
    public string Correct(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        // Pass 1: deterministic character substitutions. These don't need any
        // dictionary and fix errors that tokenisation-then-edit-1 can't see.
        text = ApplyDeterministicSubs(text);

        // Pass 2: word-by-word edit-1 correction against the dictionary.
        if (_rank.Count == 0) return text;

        // Two-pass scan: first extract tokens with their positions so we can give
        // TryCorrect the previous and next word for context-aware disambiguation.
        // "what aid they" should prefer "did" over "aid" because "what did they"
        // is a common English trigram whereas "what aid they" isn't.
        var tokens = new List<(int start, int end, string word)>();
        {
            int i = 0;
            while (i < text.Length)
            {
                if (IsWordChar(text[i]))
                {
                    int start = i;
                    while (i < text.Length && IsWordChar(text[i])) i++;
                    tokens.Add((start, i, text[start..i]));
                }
                else i++;
            }
        }

        var sb = new StringBuilder(text.Length);
        int cursor = 0;
        bool sentenceStart = true;

        for (int ti = 0; ti < tokens.Count; ti++)
        {
            var (start, end, word) = tokens[ti];

            // Emit punctuation/whitespace between last token and this one, updating
            // sentence-start when we cross a sentence-ending char.
            while (cursor < start)
            {
                char c = text[cursor++];
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n' or '\r' or ':') sentenceStart = true;
            }

            var prev = ti > 0                ? tokens[ti - 1].word.ToLowerInvariant() : "";
            var next = ti + 1 < tokens.Count ? tokens[ti + 1].word.ToLowerInvariant() : "";
            sb.Append(TryCorrect(word, prev, next, sentenceStart));
            cursor = end;
            sentenceStart = false;
        }
        // Trailing punctuation / whitespace after the last token.
        while (cursor < text.Length) sb.Append(text[cursor++]);

        return sb.ToString();
    }

    // =================================================================================
    // Deterministic substitutions — OCR errors that dictionary lookup can't fix because
    // they produce non-word tokens (|, [, ], =, etc.) that the tokeniser skips over.
    // These rules require zero vocabulary and are safe to run by themselves.
    // =================================================================================

    /// <summary>
    ///     Applies only the character-level OCR substitutions (pipe/bracket → I,
    ///     line-start equals → dialog hyphen). Does no dictionary lookup. Safe to
    ///     call without ever loading a wordlist.
    /// </summary>
    public static string ApplyDeterministicSubs(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            // `|`, `[`, `]` are all standard OCR misreads of an isolated `I`. Require
            // boundary conditions on both sides so mid-word punctuation (e.g. a URL or
            // a reference like "[1]") isn't rewritten.
            if (c == '|' || c == '[' || c == ']')
            {
                bool prevBoundary = i == 0
                                 || char.IsWhiteSpace(text[i - 1])
                                 || text[i - 1] == '-';
                bool nextBoundary = i == text.Length - 1
                                 || char.IsWhiteSpace(text[i + 1])
                                 || text[i + 1] is ',' or '.' or '?' or '!' or '\'';
                if (prevBoundary && nextBoundary) { sb.Append('I'); continue; }
            }

            // Dialog hyphen misread as `=`. Safe only at start of a line.
            if (c == '=' && (i == 0 || text[i - 1] is '\n' or '\r'))
            {
                sb.Append('-');
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    // =================================================================================
    // Edit-1 correction
    // =================================================================================

    private string TryCorrect(string word, string prev, string next, bool sentenceStart)
    {
        if (word.Length < 3) return word;

        if (!sentenceStart && char.IsUpper(word[0])) return word;
        // All-caps acronym guard (ENCOM, CIA, FBI) — preserve as-is. Tokens that mix
        // digits with letters (100K, GPS-12) aren't acronyms; let the digit-to-letter
        // pass try to recover them.
        if (word.Length >= 2 && word.All(char.IsLetter) && word.All(char.IsUpper)) return word;

        // Digit-to-letter OCR fix: tokens mixing digits with letters (e.g. "100K" for
        // "look") won't match any dictionary word directly. Try digit→letter variants.
        if (word.Any(char.IsDigit))
        {
            foreach (var variant in DigitLetterVariants(word.ToLowerInvariant()))
                if (_rank.ContainsKey(variant))
                    return MatchCase(variant, word);
            return word;
        }

        var lower = word.ToLowerInvariant();
        bool inDict  = _rank.TryGetValue(lower, out int selfRank);

        // Score every edit-1 candidate and keep the best one. Scoring model:
        //   bigram match with prev or next word:  +1000   (a strong context signal)
        //   shape confusability (3 / 2 / 1 / 0):  +100 × shape
        //   frequency rank:                       −rank   (lower rank = more common)
        // If the original is already in the dict, we require the candidate to beat
        // its own score by a clear margin so legitimate rare words aren't clobbered
        // in the absence of supporting evidence.
        int  selfScore    = ScoreCandidate(lower, lower, prev, next, inDict ? selfRank : int.MaxValue, 0);
        string? best      = null;
        int     bestScore = inDict ? selfScore : int.MinValue;

        foreach (var cand in EditOne(lower))
        {
            if (cand == lower) continue;
            if (!_rank.TryGetValue(cand, out int r)) continue;
            int shape = ShapeScore(lower, cand);
            int s     = ScoreCandidate(cand, lower, prev, next, r, shape);
            if (s > bestScore)
            {
                bestScore = s;
                best      = cand;
            }
        }

        if (best == null) return word;

        // When the original is itself a valid word, require the challenger to beat
        // it by a meaningful margin — bigram match, or frequency gap of >20×.
        if (inDict)
        {
            int diff = bestScore - selfScore;
            if (diff < 500) return word; // only swap on strong evidence

            // Don't flip between singular/plural or tense forms (length-changing
            // edits) unless a bigram vote supports it — otherwise "Ships" → "Ship",
            // "circuits" → "circuit", "users" → "uses" happen on pure frequency.
            bool lengthChange = best.Length != lower.Length;
            bool bigramSupport =
                (!string.IsNullOrEmpty(prev) && _bigrams.Contains((prev, best))) ||
                (!string.IsNullOrEmpty(next) && _bigrams.Contains((best, next)));
            if (lengthChange && !bigramSupport) return word;
        }

        return MatchCase(best, word);
    }

    private int ScoreCandidate(string cand, string originalLower, string prev, string next, int rank, int shape)
    {
        int score = -Math.Min(rank, 100_000);                       // frequency
        score    += shape * 100;                                     // visual confusability

        // Edit-direction bias: OCR routinely drops thin characters (trailing s/d/r,
        // leading r/l/t/i) but almost never inserts spurious ones. A candidate that
        // is LONGER than the original is therefore a plausible "restore a dropped
        // character" correction; a SHORTER candidate is much less likely to be right.
        int lenDiff = cand.Length - originalLower.Length;
        if (lenDiff > 0) score += 200;
        else if (lenDiff < 0) score -= 200;

        // Bigram context. Small hand-curated table for canonical English function-word
        // pairs. Where a pair fits, it's a very strong vote for this candidate.
        if (!string.IsNullOrEmpty(prev) && _bigrams.Contains((prev, cand))) score += 1000;
        if (!string.IsNullOrEmpty(next) && _bigrams.Contains((cand, next))) score += 1000;

        // Pair-plausibility proxy (no bigram corpus required): if both prev and cand
        // are very common words, they're more likely to form a natural English pair.
        // This is a weak signal but it's general — doesn't rely on any curated list.
        if (!string.IsNullOrEmpty(prev) && _rank.TryGetValue(prev, out int pr) && pr + rank < 500) score += 300;
        if (!string.IsNullOrEmpty(next) && _rank.TryGetValue(next, out int nr) && nr + rank < 500) score += 300;

        return score;
    }

    /// <summary>
    ///     Generates all combinations of digit→letter substitutions for tokens that
    ///     mix digits and letters — covers the common OCR failures where numbers
    ///     stand in for visually similar letters (0→o, 1→l/i, 5→s, 8→b, 6→g).
    /// </summary>
    private static IEnumerable<string> DigitLetterVariants(string word)
    {
        // Build a list of possible characters at each position. Non-digits have one
        // entry (themselves); digits have multiple candidate letters.
        var choices = new List<char[]>(word.Length);
        foreach (var c in word)
        {
            choices.Add(c switch
            {
                '0' => new[] { 'o' },
                '1' => new[] { 'l', 'i' },
                '5' => new[] { 's' },
                '8' => new[] { 's', 'b' }, // 's' first: "80" in OCR is nearly always "so", not "bo"
                '6' => new[] { 'g' },
                _   => new[] { c },
            });
        }
        // Cartesian product. Cap on explosion size for pathological tokens.
        long total = 1;
        foreach (var opts in choices) { total *= opts.Length; if (total > 256) yield break; }

        var buf = new char[word.Length];
        foreach (var variant in Expand(choices, buf, 0))
            yield return variant;
    }

    private static IEnumerable<string> Expand(List<char[]> choices, char[] buf, int idx)
    {
        if (idx == choices.Count) { yield return new string(buf); yield break; }
        foreach (var c in choices[idx])
        {
            buf[idx] = c;
            foreach (var s in Expand(choices, buf, idx + 1))
                yield return s;
        }
    }

    // =================================================================================
    // Context bigrams — compact hand-curated set of English word pairs that appear
    // very commonly in dialog/subtitle text. When one of these pairs forms between
    // a candidate and its neighbour, it's a strong vote for that candidate over
    // alternatives that share the same shape/frequency profile.
    //
    // "what did they" beats "what aid they" because (what, did) and (did, they) both
    // score, while (what, aid) and (aid, they) don't. Deliberately small (~300 entries)
    // so the table stays maintainable; coverage is biased toward high-confidence
    // function-word pairs rather than content-word associations.
    // =================================================================================

    private static readonly HashSet<(string, string)> _bigrams = new()
    {
        // question word + auxiliary
        ("what","did"),("what","is"),("what","are"),("what","was"),("what","were"),
        ("what","will"),("what","would"),("what","should"),("what","could"),("what","can"),
        ("what","do"),("what","does"),("what","happened"),("what","about"),("what","if"),
        ("what","i"),("what","you"),("what","he"),("what","she"),("what","they"),("what","we"),
        ("where","is"),("where","are"),("where","was"),("where","did"),("where","do"),("where","will"),
        ("when","did"),("when","is"),("when","are"),("when","was"),("when","will"),("when","do"),
        ("who","is"),("who","are"),("who","was"),("who","were"),("who","did"),("who","will"),
        ("how","did"),("how","do"),("how","does"),("how","can"),("how","could"),("how","would"),
        ("how","many"),("how","much"),("how","long"),("how","are"),("how","is"),
        ("why","did"),("why","do"),("why","does"),("why","would"),("why","are"),("why","is"),("why","not"),
        // auxiliary + pronoun
        ("did","you"),("did","they"),("did","he"),("did","she"),("did","we"),("did","it"),("did","i"),
        ("do","you"),("do","they"),("do","we"),("do","i"),
        ("does","he"),("does","she"),("does","it"),("does","that"),("does","this"),
        ("is","this"),("is","that"),("is","it"),("is","he"),("is","she"),("is","there"),
        ("are","you"),("are","they"),("are","we"),("are","the"),("are","there"),
        ("was","it"),("was","he"),("was","she"),("was","that"),("was","this"),("was","a"),
        ("were","you"),("were","they"),("were","we"),("were","the"),("were","there"),
        ("will","you"),("will","they"),("will","be"),("will","not"),("will","he"),("will","she"),
        ("would","you"),("would","they"),("would","be"),("would","have"),("would","not"),
        ("can","you"),("can","they"),("can","we"),("can","i"),("can","he"),("can","she"),("can","be"),("can","not"),
        ("could","you"),("could","they"),("could","we"),("could","i"),("could","be"),("could","have"),("could","not"),
        ("should","you"),("should","they"),("should","we"),("should","i"),("should","be"),("should","have"),("should","not"),
        ("has","been"),("have","been"),("had","been"),
        ("have","to"),("has","to"),("had","to"),
        ("going","to"),("used","to"),("want","to"),("need","to"),("have","a"),("has","a"),("had","a"),
        // subject + verb
        ("i","am"),("i","was"),("i","have"),("i","had"),("i","will"),("i","would"),("i","can"),("i","could"),
        ("i","should"),("i","did"),("i","do"),("i","don't"),("i","didn't"),("i","know"),("i","think"),("i","want"),
        ("i","need"),("i","got"),("i","saw"),("i","see"),("i","said"),("i","told"),("i","asked"),("i","hope"),
        ("you","are"),("you","were"),("you","have"),("you","had"),("you","will"),("you","would"),("you","can"),
        ("you","could"),("you","should"),("you","did"),("you","do"),("you","don't"),("you","didn't"),
        ("you","know"),("you","think"),("you","want"),("you","need"),("you","got"),("you","said"),
        ("he","is"),("he","was"),("he","has"),("he","had"),("he","will"),("he","would"),("he","can"),("he","could"),
        ("he","should"),("he","did"),("he","does"),("he","doesn't"),("he","said"),("he","told"),("he","knows"),
        ("she","is"),("she","was"),("she","has"),("she","had"),("she","will"),("she","would"),("she","can"),
        ("she","could"),("she","should"),("she","did"),("she","does"),("she","doesn't"),("she","said"),
        ("it","is"),("it","was"),("it","has"),("it","had"),("it","will"),("it","would"),("it","can"),
        ("it","could"),("it","should"),("it","did"),("it","does"),("it","doesn't"),
        ("we","are"),("we","were"),("we","have"),("we","had"),("we","will"),("we","would"),("we","can"),
        ("we","could"),("we","should"),("we","did"),("we","do"),("we","don't"),("we","need"),("we","want"),
        ("they","are"),("they","were"),("they","have"),("they","had"),("they","will"),("they","would"),
        ("they","can"),("they","could"),("they","should"),("they","did"),("they","do"),("they","don't"),
        ("they","said"),("they","know"),("they","want"),("they","need"),
        // articles + nouns / adjectives
        ("a","little"),("a","few"),("a","lot"),("a","bit"),("a","day"),("a","moment"),("a","minute"),
        ("a","second"),("a","chance"),("a","man"),("a","woman"),("a","child"),("a","time"),("a","place"),
        ("the","same"),("the","other"),("the","next"),("the","last"),("the","first"),("the","only"),
        ("the","other"),("the","day"),("the","night"),("the","time"),("the","man"),("the","woman"),
        ("the","kids"),("the","world"),("the","room"),("the","house"),("the","car"),("the","door"),
        ("one","day"),("one","night"),("one","time"),("one","of"),("one","more"),("one","thing"),
        ("this","is"),("this","was"),("this","one"),("this","time"),("this","way"),("that","is"),
        ("that","was"),("that","one"),("that","way"),("that","time"),("that","i"),("that","you"),
        ("these","are"),("those","are"),
        // prepositions
        ("in","the"),("in","a"),("in","this"),("in","that"),("in","my"),("in","your"),("in","his"),
        ("in","her"),("in","our"),("in","their"),("in","fact"),("in","case"),("in","time"),
        ("on","the"),("on","a"),("on","this"),("on","that"),("on","my"),("on","your"),("on","his"),
        ("on","her"),("on","our"),("on","their"),("on","top"),
        ("at","the"),("at","a"),("at","this"),("at","that"),("at","my"),("at","your"),("at","his"),
        ("at","her"),("at","our"),("at","their"),("at","all"),("at","least"),("at","once"),
        ("of","the"),("of","a"),("of","this"),("of","that"),("of","my"),("of","your"),("of","his"),
        ("of","her"),("of","our"),("of","their"),("of","us"),("of","them"),("of","course"),
        ("to","the"),("to","a"),("to","be"),("to","do"),("to","go"),("to","get"),("to","have"),
        ("to","know"),("to","see"),("to","make"),("to","take"),("to","give"),("to","tell"),
        ("for","the"),("for","a"),("for","this"),("for","that"),("for","you"),("for","me"),("for","us"),
        ("for","them"),("for","once"),("for","now"),("for","sure"),
        ("with","the"),("with","a"),("with","me"),("with","you"),("with","us"),("with","them"),
        ("from","the"),("from","a"),("from","me"),("from","you"),("from","us"),("from","them"),("from","here"),
        // common phrases
        ("kind","of"),("sort","of"),("out","of"),("all","right"),("of","course"),("i","mean"),
        ("you","know"),("right","now"),("come","on"),("go","on"),("hold","on"),("get","out"),
        ("get","in"),("get","up"),("take","care"),("take","it"),("let","me"),("let","us"),("let","go"),
        ("let","him"),("let","her"),("let","them"),("thank","you"),("excuse","me"),("look","at"),
        ("more","than"),("less","than"),("rather","than"),("as","well"),("as","much"),
        ("so","much"),("so","many"),("so","that"),("too","much"),("too","many"),("too","late"),
        ("not","sure"),("not","really"),("not","to"),("would","like"),("would","rather"),
        ("in","front"),("in","back"),("up","there"),("down","there"),("over","there"),("in","there"),
        ("look","like"),("looks","like"),("looked","like"),("looking","for"),("looking","at"),
        ("dreaming","of"),("thinking","about"),("thought","of"),("thought","i"),("thought","i'd"),
        // comparisons ("more/less X than")
        ("more","than"),("less","than"),("rather","than"),("other","than"),("better","than"),
        ("than","i"),("than","you"),("than","he"),("than","she"),("than","they"),("than","we"),
        ("than","that"),("than","this"),("than","any"),("than","ever"),
        // common sentence starters / connectives
        ("try","to"),("tried","to"),("tries","to"),("trying","to"),
        ("so","many"),("so","much"),("so","that"),("so","far"),("so","fast"),
        ("ever","seen"),("ever","been"),("ever","had"),("ever","want"),("ever","needed"),
        ("no","one"),("anyone","else"),("someone","else"),("everyone","else"),
    };

    // =================================================================================
    // Visual-shape confusability — small table of letter pairs that share enough shape
    // at subtitle resolution to be commonly swapped by OCR. Score 3 = near-identical
    // shapes, 2 = strongly similar, 1 = loosely similar (used for transpositions), 0 = no
    // relationship. Insertions and deletions get 0 since there's no "what did the OCR
    // pick instead" signal for them — frequency rank picks the winner in those cases.
    // =================================================================================

    private static readonly Dictionary<(char, char), int> _confusion = BuildConfusion();

    private static Dictionary<(char, char), int> BuildConfusion()
    {
        // Pairs are symmetric: if (a,b) maps to a score, (b,a) also does.
        var pairs = new (string, int)[]
        {
            ("ao", 3), ("ad", 3), ("bd", 3), ("ce", 3), ("co", 3), ("eo", 3),
            ("il", 3), ("nm", 3), ("rn", 3), ("oq", 3), ("pq", 3),
            ("uv", 3), ("vw", 3),
            ("ae", 2), ("bh", 2), ("bp", 2), ("cg", 2), ("fr", 2), ("ft", 2),
            ("gq", 2), ("hn", 2), ("ij", 2), ("lt", 2), ("nr", 2), ("qo", 2),
            ("rt", 2), ("yj", 2), ("yg", 2), ("uw", 2),
        };
        var d = new Dictionary<(char, char), int>();
        foreach (var (s, score) in pairs)
        {
            d[(s[0], s[1])] = score;
            d[(s[1], s[0])] = score;
        }
        return d;
    }

    private static int ShapeScore(string original, string candidate)
    {
        int ol = original.Length, cl = candidate.Length;
        if (ol == cl)
        {
            // Substitution (or transposition). Find the diff positions.
            int diff = -1;
            for (int i = 0; i < ol; i++)
            {
                if (original[i] == candidate[i]) continue;
                if (diff >= 0) return 1; // two diffs = transposition
                diff = i;
            }
            if (diff < 0) return 0;
            return _confusion.TryGetValue((original[diff], candidate[diff]), out var score) ? score : 0;
        }
        return 0; // insertion or deletion — no shape signal
    }

    // Digits are considered word characters so tokens like "100K" (OCR'd from "look")
    // reach the corrector, which then tries digit→letter substitutions. The corrector
    // still skips tokens it can't confidently fix, so legitimate numerics (years,
    // "24/7", "ENCOM-12") pass through untouched.
    private static bool IsWordChar(char c) => char.IsLetter(c) || char.IsDigit(c) || c == '\'';

    private static IEnumerable<string> EditOne(string w)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz'";
        for (int i = 0; i < w.Length; i++)
        {
            yield return w.Remove(i, 1);
            if (i < w.Length - 1)
                yield return w.Substring(0, i) + w[i + 1] + w[i] + w.Substring(i + 2);
            foreach (var c in alphabet)
            {
                if (c != w[i]) yield return w.Substring(0, i) + c + w.Substring(i + 1);
                yield return w.Substring(0, i) + c + w.Substring(i);
            }
        }
        foreach (var c in alphabet)
            yield return w + c;
    }

    private static string MatchCase(string replacement, string original)
    {
        if (original.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0]))  return char.ToUpper(replacement[0]) + replacement[1..];
        return replacement;
    }
}
