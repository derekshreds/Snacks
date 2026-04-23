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
        TwoChannelAudio:        bool('TwoChannelAudio'),
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
        AudioCodec:               str('AudioCodec', 'copy'),
        AudioBitrateKbps:         num('AudioBitrateKbps', 192),

        // Subtitles.
        SubtitleLanguagesToKeep:    chips('SubtitleLanguagesToKeep') ?? ['en'],
        ExtractSubtitlesToSidecar:  bool('ExtractSubtitlesToSidecar'),
        SidecarSubtitleFormat:      str('SidecarSubtitleFormat', 'srt'),
        ConvertImageSubtitlesToSrt: bool('ConvertImageSubtitlesToSrt'),

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
        set('TwoChannelAudio',        pick('TwoChannelAudio'));
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
        set('AudioCodec',               pick('AudioCodec')               || 'copy');
        set('AudioBitrateKbps',         pick('AudioBitrateKbps')          ?? 192);

        // Subtitles.
        set('ExtractSubtitlesToSidecar',  pick('ExtractSubtitlesToSidecar'));
        set('SidecarSubtitleFormat',      pick('SidecarSubtitleFormat') || 'srt');
        set('ConvertImageSubtitlesToSrt', pick('ConvertImageSubtitlesToSrt'));

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
