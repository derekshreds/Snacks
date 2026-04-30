/**
 * Encoder-settings form (read/write).
 *
 * Provides:
 *   - {@link getEncoderOptions}     — snapshot the form into an options object
 *                                     AND auto-persist via the settings API.
 *   - {@link restoreEncoderOptions} — populate the form from the server's
 *                                     saved settings.
 *
 * Both functions accept a `prefix` argument ("settings" in normal use) so
 * the same code can drive alternative settings dialogs whose inputs share
 * the same suffixes but a different id prefix.
 */

import { settingsApi }                    from '../api.js';
import { setChipValues, getChipValues }   from './chip-input.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/**
 * Map from UI codec names to the actual ffmpeg encoder identifiers. Keeps
 * the form simple while letting us swap encoders per codec independently.
 */
const ENCODER_MAP = {
    h265: 'libx265',
    h264: 'libx264',
    av1:  'libsvtav1',
};


// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/**
 * Looks up an element by id using `${prefix}${suffix}`.
 *
 * @param {string} prefix
 * @param {string} suffix
 * @returns {HTMLElement|null}
 */
function el(prefix, suffix) {
    return document.getElementById(`${prefix}${suffix}`);
}


// ---------------------------------------------------------------------------
// Audio output rows (row editor for AudioOutputs)
// ---------------------------------------------------------------------------

/**
 * Reads the current audio-output rows out of the row container into an array of
 * {Codec, Layout, BitrateKbps} entries. Empty/missing container returns [].
 *
 * @param {string} prefix
 * @returns {Array<{Codec:string, Layout:string, BitrateKbps:number}>}
 */
function readAudioOutputs(prefix) {
    const root = el(prefix, 'AudioOutputs');
    if (!root) return [];

    return Array.from(root.querySelectorAll('[data-audio-output-row]')).map(row => ({
        Codec:       row.querySelector('[data-field="Codec"]')?.value       ?? 'aac',
        Layout:      row.querySelector('[data-field="Layout"]')?.value      ?? 'Source',
        BitrateKbps: parseInt(row.querySelector('[data-field="BitrateKbps"]')?.value, 10) || 0,
    }));
}

/**
 * Renders one new audio-output row from the template inside the row container.
 * Fills the row with the provided profile values, or codec/layout defaults.
 *
 * @param {string} prefix
 * @param {{Codec?:string, Layout?:string, BitrateKbps?:number}} [profile]
 */
function appendAudioOutputRow(prefix, profile = {}) {
    const root = el(prefix, 'AudioOutputs');
    const tpl  = document.getElementById(`${prefix}AudioOutputRowTemplate`);
    if (!root || !tpl) return;

    const row = tpl.content.firstElementChild.cloneNode(true);
    if (profile.Codec)            row.querySelector('[data-field="Codec"]').value       = profile.Codec;
    if (profile.Layout)           row.querySelector('[data-field="Layout"]').value      = profile.Layout;
    if (profile.BitrateKbps != null) row.querySelector('[data-field="BitrateKbps"]').value = profile.BitrateKbps;

    row.querySelector('[data-audio-output-remove]').addEventListener('click', () => row.remove());
    root.appendChild(row);
}

/**
 * Replaces all rows in the audio-outputs editor with one row per saved profile.
 * No rows are rendered when `profiles` is empty — that's the "preserve only" config.
 *
 * @param {string} prefix
 * @param {Array<object>} profiles
 */
function setAudioOutputs(prefix, profiles) {
    const root = el(prefix, 'AudioOutputs');
    if (!root) return;
    root.replaceChildren();
    for (const p of (profiles || [])) {
        appendAudioOutputRow(prefix, {
            Codec:       p.Codec       ?? p.codec,
            Layout:      p.Layout      ?? p.layout,
            BitrateKbps: p.BitrateKbps ?? p.bitrateKbps ?? 0,
        });
    }
}

/**
 * Wires up the "+ Add output" button so each click appends a fresh row.
 * Idempotent — repeated calls don't double-bind.
 *
 * @param {string} prefix
 */
function ensureAudioOutputAddBound(prefix) {
    const btn = el(prefix, 'AudioOutputsAdd');
    if (!btn || btn.dataset.bound === '1') return;
    btn.dataset.bound = '1';
    btn.addEventListener('click', () => appendAudioOutputRow(prefix));
}


// ---------------------------------------------------------------------------
// Read: getEncoderOptions
// ---------------------------------------------------------------------------

/**
 * Reads the current encoder-form state into a plain options object, persists
 * it via {@link settingsApi.save}, and returns it for immediate use.
 *
 * The save is fire-and-forget (failures are ignored) — callers that need
 * the persisted state should observe the next `settingsApi.get()` instead.
 *
 * @param {string} [prefix='settings'] Id prefix shared by all form inputs.
 * @returns {object} The options snapshot.
 */
export function getEncoderOptions(prefix = 'settings') {

    /** Reads a string value with a default for a missing or empty input. */
    const str = (id, d = '') => el(prefix, id)?.value ?? d;

    /** Reads a checkbox state with a default when the element is absent. */
    const bool = (id, d = false) => el(prefix, id)?.checked ?? d;

    /** Reads a numeric input, falling back to `d` for NaN. */
    const num = (id, d = 0) => {
        const n = parseInt(el(prefix, id)?.value);
        return Number.isNaN(n) ? d : n;
    };

    /**
     * Reads a chip-input's values, returning null if the element is absent
     * so callers can distinguish "absent" from "empty".
     */
    const chips = (suffix) => {
        const root = document.getElementById(`${prefix}${suffix}Chips`);
        return root ? getChipValues(`${prefix}${suffix}Chips`) : null;
    };

    const codec = str('Codec', 'h265');

    const options = {
        // Container + codec + hardware.
        Format:  str('Format', 'mkv'),
        Codec:   codec,
        Encoder: ENCODER_MAP[codec] || 'libx265',
        HardwareAcceleration: str('HardwareAcceleration', 'auto'),

        // Bitrate + skip policy.
        TargetBitrate:          num('TargetBitrate', 3500),
        RemoveBlackBorders:     bool('RemoveBlackBorders'),
        DeleteOriginalFile:     bool('DeleteOriginalFile'),
        RetryOnFail:            bool('RetryOnFail', true),
        StrictBitrate:          bool('StrictBitrate'),
        FourKBitrateMultiplier: num('FourKBitrateMultiplier', 4),
        Skip4K:                 bool('Skip4K'),
        SkipPercentAboveTarget: Math.max(0, num('SkipPercentAboveTarget', 20)),

        // Paths.
        OutputDirectory: str('OutputDirectory'),
        EncodeDirectory: str('EncodeDirectory'),

        // Audio.
        AudioLanguagesToKeep:     chips('AudioLanguagesToKeep') ?? ['en'],
        KeepOriginalLanguage:     bool('KeepOriginalLanguage'),
        OriginalLanguageProvider: str('OriginalLanguageProvider', 'None'),
        PreserveOriginalAudio:    bool('PreserveOriginalAudio', true),
        AudioOutputs:             readAudioOutputs(prefix),

        // Subtitles.
        SubtitleLanguagesToKeep:      chips('SubtitleLanguagesToKeep') ?? ['en'],
        ExtractSubtitlesToSidecar:    bool('ExtractSubtitlesToSidecar'),
        SidecarSubtitleFormat:        str('SidecarSubtitleFormat', 'srt'),
        ConvertImageSubtitlesToSrt:   bool('ConvertImageSubtitlesToSrt'),
        PassThroughImageSubtitlesMkv: bool('PassThroughImageSubtitlesMkv'),

        // Encoding mode.
        EncodingMode: str('EncodingMode', 'Transcode'),
        MuxStreams:   str('MuxStreams',   'Both'),

        // Video.
        DownscalePolicy:     str('DownscalePolicy', 'Never'),
        DownscaleTarget:     str('DownscaleTarget', '1080p'),
        TonemapHdrToSdr:     bool('TonemapHdrToSdr'),
        FfmpegQualityPreset: str('FfmpegQualityPreset', 'medium'),
    };

    // Best-effort auto-save. A failure here doesn't block the return value;
    // callers already have what they need, and the next save attempt will
    // usually succeed.
    settingsApi.save(options).catch(() => { /* non-fatal */ });

    return options;
}


// ---------------------------------------------------------------------------
// Write: restoreEncoderOptions
// ---------------------------------------------------------------------------

/**
 * Populates the encoder form from the server's saved settings.
 *
 * The server may emit PascalCase or camelCase property names depending on
 * serializer configuration, so `pick` accepts both variants.
 *
 * Missing values leave the input at its current (HTML-default) value; a
 * throw at any point is swallowed because "restore" is strictly best-effort.
 *
 * @param {string} [prefix='settings'] Id prefix shared by all form inputs.
 */
export async function restoreEncoderOptions(prefix = 'settings') {
    try {
        const saved = await settingsApi.get();
        if (!saved || Object.keys(saved).length === 0) return;

        /**
         * Returns the first value found among the given keys, checking both
         * the original casing and the same key with a lower-cased first letter.
         */
        const pick = (...keys) => {
            for (const k of keys) {
                if (saved[k] !== undefined) return saved[k];

                const lower = k.charAt(0).toLowerCase() + k.slice(1);
                if (saved[lower] !== undefined) return saved[lower];
            }
            return undefined;
        };

        /** Writes a saved value into a form input, leaving the input alone if the value is absent. */
        const set = (id, val) => {
            const node = el(prefix, id);
            if (!node || val === undefined) return;

            if (node.type === 'checkbox') node.checked = !!val;
            else                          node.value   = val;
        };

        // Container + codec + hardware.
        set('Format',               pick('Format'));
        set('Codec',                pick('Codec'));
        set('HardwareAcceleration', pick('HardwareAcceleration'));

        // Bitrate + skip policy.
        set('TargetBitrate',          pick('TargetBitrate'));
        set('RemoveBlackBorders',     pick('RemoveBlackBorders'));
        set('DeleteOriginalFile',     pick('DeleteOriginalFile'));
        set('RetryOnFail',            pick('RetryOnFail'));
        set('StrictBitrate',          pick('StrictBitrate'));
        set('FourKBitrateMultiplier', pick('FourKBitrateMultiplier'));
        set('Skip4K',                 pick('Skip4K'));
        set('SkipPercentAboveTarget', Math.max(0, pick('SkipPercentAboveTarget') ?? 20));

        // Paths.
        set('OutputDirectory', pick('OutputDirectory') || '');
        set('EncodeDirectory', pick('EncodeDirectory') || '');

        // Audio.
        set('KeepOriginalLanguage',     pick('KeepOriginalLanguage'));
        set('OriginalLanguageProvider', pick('OriginalLanguageProvider') || 'None');

        // PreserveOriginalAudio defaults to true (the old "AudioCodec=copy" default behavior).
        // Settings that come back from the server have already been migrated server-side, so
        // we just trust the value here and only fall back to true when it's missing entirely.
        const preserve = pick('PreserveOriginalAudio');
        set('PreserveOriginalAudio', preserve === undefined ? true : !!preserve);

        ensureAudioOutputAddBound(prefix);
        setAudioOutputs(prefix, pick('AudioOutputs') ?? []);

        // Subtitles.
        set('ExtractSubtitlesToSidecar',    pick('ExtractSubtitlesToSidecar'));
        set('SidecarSubtitleFormat',        pick('SidecarSubtitleFormat') || 'srt');
        set('ConvertImageSubtitlesToSrt',   pick('ConvertImageSubtitlesToSrt'));
        set('PassThroughImageSubtitlesMkv', pick('PassThroughImageSubtitlesMkv'));

        // Encoding mode.
        set('EncodingMode', pick('EncodingMode') || 'Transcode');
        set('MuxStreams',   pick('MuxStreams')   || 'Both');

        // Video.
        set('DownscalePolicy',     pick('DownscalePolicy')     || 'Never');
        set('DownscaleTarget',     pick('DownscaleTarget')     || '1080p');
        set('TonemapHdrToSdr',     pick('TonemapHdrToSdr'));
        set('FfmpegQualityPreset', pick('FfmpegQualityPreset') || 'medium');

        // Chip inputs — use explicit defaults so a fresh install gets sane values.
        setChipValues(`${prefix}AudioLanguagesToKeepChips`,    pick('AudioLanguagesToKeep')    ?? ['en']);
        setChipValues(`${prefix}SubtitleLanguagesToKeepChips`, pick('SubtitleLanguagesToKeep') ?? ['en']);

    } catch { /* silent — restore is best-effort */ }
}
