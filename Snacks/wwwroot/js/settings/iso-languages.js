/**
 * ISO language table + helpers.
 *
 * Canonical stored form is the 2-letter ISO 639-1 code. Users may type any of
 * the three forms (2-letter, 3-letter, English name); {@link toTwoLetter}
 * normalizes the input, {@link suggest} drives autocomplete.
 *
 * Keep in sync with Services/LanguageMatcher.cs on the backend.
 */


// ---------------------------------------------------------------------------
// Seed table
// ---------------------------------------------------------------------------

/**
 * @typedef {object} LanguageEntry
 * @property {string}      twoLetter    ISO 639-1 code.
 * @property {string}      threeLetterT ISO 639-2/T code.
 * @property {string|null} threeLetterB ISO 639-2/B code, or null when equal to T.
 * @property {string}      name         English name of the language.
 */

/** @type {LanguageEntry[]} */
export const LANGUAGES = [
    { twoLetter: 'en', threeLetterT: 'eng', threeLetterB: null,  name: 'English'    },
    { twoLetter: 'es', threeLetterT: 'spa', threeLetterB: null,  name: 'Spanish'    },
    { twoLetter: 'ja', threeLetterT: 'jpn', threeLetterB: null,  name: 'Japanese'   },
    { twoLetter: 'fr', threeLetterT: 'fra', threeLetterB: 'fre', name: 'French'     },
    { twoLetter: 'de', threeLetterT: 'deu', threeLetterB: 'ger', name: 'German'     },
    { twoLetter: 'it', threeLetterT: 'ita', threeLetterB: null,  name: 'Italian'    },
    { twoLetter: 'pt', threeLetterT: 'por', threeLetterB: null,  name: 'Portuguese' },
    { twoLetter: 'ru', threeLetterT: 'rus', threeLetterB: null,  name: 'Russian'    },
    { twoLetter: 'zh', threeLetterT: 'zho', threeLetterB: 'chi', name: 'Chinese'    },
    { twoLetter: 'ko', threeLetterT: 'kor', threeLetterB: null,  name: 'Korean'     },
    { twoLetter: 'ar', threeLetterT: 'ara', threeLetterB: null,  name: 'Arabic'     },
    { twoLetter: 'hi', threeLetterT: 'hin', threeLetterB: null,  name: 'Hindi'      },
    { twoLetter: 'nl', threeLetterT: 'nld', threeLetterB: 'dut', name: 'Dutch'      },
    { twoLetter: 'sv', threeLetterT: 'swe', threeLetterB: null,  name: 'Swedish'    },
    { twoLetter: 'no', threeLetterT: 'nor', threeLetterB: null,  name: 'Norwegian'  },
    { twoLetter: 'da', threeLetterT: 'dan', threeLetterB: null,  name: 'Danish'     },
    { twoLetter: 'fi', threeLetterT: 'fin', threeLetterB: null,  name: 'Finnish'    },
    { twoLetter: 'pl', threeLetterT: 'pol', threeLetterB: null,  name: 'Polish'     },
    { twoLetter: 'tr', threeLetterT: 'tur', threeLetterB: null,  name: 'Turkish'    },
    { twoLetter: 'cs', threeLetterT: 'ces', threeLetterB: 'cze', name: 'Czech'      },
    { twoLetter: 'hu', threeLetterT: 'hun', threeLetterB: null,  name: 'Hungarian'  },
    { twoLetter: 'el', threeLetterT: 'ell', threeLetterB: 'gre', name: 'Greek'      },
    { twoLetter: 'he', threeLetterT: 'heb', threeLetterB: null,  name: 'Hebrew'     },
    { twoLetter: 'th', threeLetterT: 'tha', threeLetterB: null,  name: 'Thai'       },
    { twoLetter: 'vi', threeLetterT: 'vie', threeLetterB: null,  name: 'Vietnamese' },
    { twoLetter: 'id', threeLetterT: 'ind', threeLetterB: null,  name: 'Indonesian' },
    { twoLetter: 'uk', threeLetterT: 'ukr', threeLetterB: null,  name: 'Ukrainian'  },
];


// ---------------------------------------------------------------------------
// Lookup map built once at module load
// ---------------------------------------------------------------------------

/** Normalized alias (lowercase) -> 2-letter code. */
const ALIAS_TO_TWO = (() => {
    const m = new Map();
    for (const e of LANGUAGES) {
        m.set(e.twoLetter,                 e.twoLetter);
        m.set(e.threeLetterT,              e.twoLetter);
        if (e.threeLetterB) m.set(e.threeLetterB, e.twoLetter);
        m.set(e.name.toLowerCase(),        e.twoLetter);
    }
    return m;
})();

/** 2-letter code -> English name (for chip tooltips). */
const TWO_TO_NAME = (() => {
    const m = new Map();
    for (const e of LANGUAGES) m.set(e.twoLetter, e.name);
    return m;
})();


// ---------------------------------------------------------------------------
// Public helpers
// ---------------------------------------------------------------------------

/**
 * Converts `raw` (2-letter, 3-letter, or English name, any case) to the
 * canonical 2-letter ISO 639-1 code, or `null` if unknown.
 *
 * @param {string|null|undefined} raw
 * @returns {string|null}
 */
export function toTwoLetter(raw) {
    if (raw == null) return null;
    const key = String(raw).trim().toLowerCase();
    if (!key) return null;
    return ALIAS_TO_TWO.get(key) ?? null;
}

/**
 * Returns the English name for a known 2-letter code, or `null`.
 *
 * @param {string|null|undefined} twoLetter
 * @returns {string|null}
 */
export function nameFor(twoLetter) {
    if (twoLetter == null) return null;
    return TWO_TO_NAME.get(String(twoLetter).toLowerCase()) ?? null;
}

/**
 * Returns up to `limit` entries whose 2-letter, 3-letter, or English name
 * starts with `query` (case-insensitive). An empty `query` returns the
 * first `limit` entries in table order.
 *
 * @param {string} query
 * @param {number} [limit=8]
 * @returns {LanguageEntry[]}
 */
export function suggest(query, limit = 8) {
    const q = String(query ?? '').trim().toLowerCase();
    const out = [];

    for (const e of LANGUAGES) {
        const hit =
            q === '' ||
            e.twoLetter.startsWith(q) ||
            e.threeLetterT.startsWith(q) ||
            (e.threeLetterB?.startsWith(q) ?? false) ||
            e.name.toLowerCase().startsWith(q);

        if (hit) {
            out.push(e);
            if (out.length >= limit) break;
        }
    }

    return out;
}
