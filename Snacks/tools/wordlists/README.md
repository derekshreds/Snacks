# OCR post-pass word lists

When a `*.txt` word list is present in this folder (and copied to the build
output's `wordlists/` folder by the csproj), the OCR pipeline runs a
post-processing spellcheck pass against each subtitle cue. Words that aren't in
the dictionary and are within edit-distance 1 of a single dictionary word get
rewritten. See [SubtitleSpellChecker.cs](../../Services/Ocr/SubtitleSpellChecker.cs).

## Files

- Named by Tesseract language code: `eng.txt`, `fra.txt`, `spa.txt`, etc.
- One word per line, UTF-8, lowercase is fine (matching is case-insensitive).
- Lines shorter than 2 chars are dropped automatically.

## Scope

The corrector is deliberately conservative:

- Words under 3 characters are never corrected.
- Mid-sentence title-case words (proper nouns — `Alan`, `Dillinger`, `ENCOM`,
  `Nikkei`) are never corrected.
- A word is only replaced if it has **exactly one** edit-1 neighbour in the
  dictionary. Ambiguous words are left alone.

## Sourcing a word list

Run [download-wordlists.bat](download-wordlists.bat)
— it pulls 50k-word subtitle-derived frequency lists from
[hermitdave/FrequencyWords](https://github.com/hermitdave/FrequencyWords)
(MIT licensed, sourced from OpenSubtitles) for every OCR language Snacks
supports (eng, spa, fra, deu, ita, por, rus, jpn, kor, chi_sim, and 17 others).
The batch file skips files that already exist — delete a `{lang}.txt` first
to refresh it.

Alternative sources:

- [SCOWL](http://wordlist.aspell.net/) (permissive license) — English variants.
- `/usr/share/dict/words` on any Linux box.
- [dwyl/english-words](https://github.com/dwyl/english-words) (unlicense).
