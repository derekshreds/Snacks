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

/**
 * Returns the music codec string for a given music container format. The
 * server's MusicEncoderArgs.ResolveEncoder maps these onward to ffmpeg
 * encoder names (libmp3lame, aac, libopus, libvorbis, flac).
 *
 * @param {string} format
 * @returns {string}
 */
function musicCodecForFormat(format) {
    switch ((format ?? '').toLowerCase()) {
        case 'mp3':  return 'libmp3lame';
        case 'm4a':  return 'aac';
        case 'opus': return 'libopus';
        case 'ogg':  return 'libvorbis';
        case 'flac': return 'flac';
        default:     return 'aac';
    }
}


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

/**
 * Tracks whether {@link restoreEncoderOptions} has completed successfully for
 * a prefix. Until it has, {@link getEncoderOptions} must NOT auto-persist:
 * the form would still hold HTML defaults, and saving them would overwrite
 * the user's real settings on disk (the classic "opened the app during a
 * server blip and all my settings reset" bug).
 *
 * @type {Set<string>}
 */
const restoredPrefixes = new Set();

/**
 * Legacy/alias values that older settings.json files (or hand edits) may
 * contain for select-backed fields. Mapped to the current UI values on
 * restore so the select doesn't silently blank out and re-persist "".
 */
const LEGACY_SELECT_VALUES = {
    DownscalePolicy: { IfLarger: 'CapAtTarget' },
    HardwareAcceleration: {
        nvenc: 'nvidia',
        cuda:  'nvidia',
        vaapi: 'intel',
        qsv:   'intel',
        amf:   'amd',
    },
};


// ---------------------------------------------------------------------------
// Audio output rows (row editor for AudioOutputs)
// ---------------------------------------------------------------------------

/**
 * Per-codec bitrate defaults — must agree with FfprobeService._codecSpecs on
 * the server. When a row is added or its codec is changed, the bitrate input
 * auto-fills to the new codec's default so users see "192" / "448" rather than
 * a confusing "0" sentinel that means "use codec default".
 *
 * Keep these in sync with the C# defaults in
 * `Snacks/Services/FfprobeService.cs:_codecSpecs`.
 */
export const AUDIO_CODEC_BITRATE_DEFAULTS = Object.freeze({
    aac:  192,
    ac3:  448,
    eac3: 384,
    opus: 192,
});

/**
 * Returns the codec's default bitrate, falling back to AAC's 192 for any
 * unknown codec string. Lower-cases the input so callers don't have to.
 *
 * @param {string} codec
 * @returns {number}
 */
export function defaultBitrateForCodec(codec) {
    const key = (codec ?? '').trim().toLowerCase();
    return AUDIO_CODEC_BITRATE_DEFAULTS[key] ?? 192;
}

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
        SampleRateHz: parseInt(row.querySelector('[data-field="SampleRateHz"]')?.value, 10) || 0,
    }));
}

/**
 * Renders one new audio-output row from the template inside the row container.
 * Fills the row with the provided profile values, or codec/layout defaults.
 *
 * Bitrate defaulting:
 *  - If `profile.BitrateKbps` is a positive number, use it verbatim (this is
 *    a saved row being restored — respect what the user picked).
 *  - Otherwise pre-fill with the codec's default bitrate from
 *    {@link AUDIO_CODEC_BITRATE_DEFAULTS} so a fresh row shows "192" / "448"
 *    rather than the bare "0" sentinel.
 *
 * @param {string} prefix
 * @param {{Codec?:string, Layout?:string, BitrateKbps?:number}} [profile]
 */
function appendAudioOutputRow(prefix, profile = {}) {
    const root = el(prefix, 'AudioOutputs');
    const tpl  = document.getElementById(`${prefix}AudioOutputRowTemplate`);
    if (!root || !tpl) return;

    const row       = tpl.content.firstElementChild.cloneNode(true);
    const codecSel  = row.querySelector('[data-field="Codec"]');
    const layoutSel = row.querySelector('[data-field="Layout"]');
    const bitrateIn = row.querySelector('[data-field="BitrateKbps"]');
    const sampleRateSel = row.querySelector('[data-field="SampleRateHz"]');

    if (profile.Codec)  codecSel.value  = profile.Codec;
    if (profile.Layout) layoutSel.value = profile.Layout;
    if (profile.SampleRateHz != null) sampleRateSel.value = String(profile.SampleRateHz);

    bitrateIn.value = (profile.BitrateKbps && profile.BitrateKbps > 0)
        ? profile.BitrateKbps
        : defaultBitrateForCodec(codecSel.value);

    // When the user swaps codec, refresh the bitrate to the new codec's default.
    // The old codec's default goes in `dataset.lastDefault` so we can detect "user
    // hasn't manually edited" — if so, clobbering is safe; if they've typed a
    // custom value, leave it alone so we don't lose it on every codec change.
    bitrateIn.dataset.lastDefault = bitrateIn.value;
    codecSel.addEventListener('change', () => {
        const next = defaultBitrateForCodec(codecSel.value);
        // Only auto-update if the user hasn't deviated from the previous default.
        // String compare to handle the empty-input edge case cleanly.
        if (bitrateIn.value === bitrateIn.dataset.lastDefault || bitrateIn.value === '' || bitrateIn.value === '0') {
            bitrateIn.value = next;
        }
        bitrateIn.dataset.lastDefault = next;
    });

    // Removing the row is a structural change to the form — clicks don't bubble
    // 'change' / 'input' events, so the settings modal's auto-save listener
    // wouldn't fire. Without this dispatch, deleting a row would only update the
    // DOM and the next page load would re-render the row from disk.
    row.querySelector('[data-audio-output-remove]').addEventListener('click', () => {
        row.remove();
        root.dispatchEvent(new Event('change', { bubbles: true }));
    });
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
            SampleRateHz: p.SampleRateHz ?? p.sampleRateHz ?? 0,
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
    btn.addEventListener('click', () => {
        appendAudioOutputRow(prefix);
        // Same reason as row-removal: the structural change doesn't fire 'change'
        // on its own, so the settings modal's auto-save wouldn't pick it up. Defaults
        // (AAC / Source / 192) are a valid state, persist them immediately.
        const root = el(prefix, 'AudioOutputs');
        root?.dispatchEvent(new Event('change', { bubbles: true }));
    });
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

    /** Reads a string value with a default for a missing input. */
    const str = (id, d = '') => el(prefix, id)?.value ?? d;

    /**
     * Reads a select-backed value, treating "" as missing. A select whose
     * value was blanked (e.g. by assigning an unknown saved value) must fall
     * back to the default instead of persisting "" — `??` alone doesn't
     * catch the empty string.
     */
    const sel = (id, d) => {
        const v = el(prefix, id)?.value;
        return v ? v : d;
    };

    /** Reads a checkbox state with a default when the element is absent. */
    const bool = (id, d = false) => el(prefix, id)?.checked ?? d;

    /**
     * Reads a numeric input, falling back to `d` for NaN and clamping to the
     * input's own min/max attributes so out-of-range typed values (or a
     * mid-edit save catching a partial number) can't persist.
     */
    const num = (id, d = 0) => {
        const node = el(prefix, id);
        let n = parseInt(node?.value, 10);
        if (Number.isNaN(n)) return d;
        const min = node?.min !== '' ? parseInt(node?.min ?? '', 10) : NaN;
        const max = node?.max !== '' ? parseInt(node?.max ?? '', 10) : NaN;
        if (!Number.isNaN(min)) n = Math.max(min, n);
        if (!Number.isNaN(max)) n = Math.min(max, n);
        return n;
    };

    /**
     * Reads a chip-input's values, returning null if the element is absent
     * so callers can distinguish "absent" from "empty".
     */
    const chips = (suffix) => {
        const root = document.getElementById(`${prefix}${suffix}Chips`);
        return root ? getChipValues(`${prefix}${suffix}Chips`) : null;
    };

    const codec = sel('Codec', 'h265');

    const options = {
        // Container + codec + hardware.
        Format:  sel('Format', 'mkv'),
        Codec:   codec,
        Encoder: ENCODER_MAP[codec] || 'libx265',
        HardwareAcceleration: sel('HardwareAcceleration', 'auto'),

        // Bitrate + skip policy.
        TargetBitrate:          num('TargetBitrate', 3500),
        RemoveBlackBorders:     bool('RemoveBlackBorders'),
        DeleteOriginalFile:     bool('DeleteOriginalFile'),
        RetryOnFail:            bool('RetryOnFail', true),
        StrictBitrate:          bool('StrictBitrate'),
        FourKBitrateMultiplier: num('FourKBitrateMultiplier', 4),
        Skip4K:                 bool('Skip4K'),
        SkipPercentAboveTarget: Math.max(0, num('SkipPercentAboveTarget', 20)),
        EncodingLogRetentionDays: Math.max(0, num('EncodingLogRetentionDays', 7)),
        QueueNewestFirst:       bool('QueueNewestFirst'),
        VerifyFilesPerDay:      Math.max(0, num('VerifyFilesPerDay', 0)),

        // Paths.
        OutputDirectory: str('OutputDirectory'),
        EncodeDirectory: str('EncodeDirectory'),

        // Audio.
        AudioLanguagesToKeep:     chips('AudioLanguagesToKeep') ?? ['en'],
        KeepOriginalLanguage:     bool('KeepOriginalLanguage'),
        OriginalLanguageProvider: sel('OriginalLanguageProvider', 'None'),
        PreserveOriginalAudio:    bool('PreserveOriginalAudio', true),
        AudioOutputs:             readAudioOutputs(prefix),
        AutoSetDefaultTrack:      bool('AutoSetDefaultTrack'),

        // Subtitles.
        SubtitleLanguagesToKeep:      chips('SubtitleLanguagesToKeep') ?? ['en'],
        ExtractSubtitlesToSidecar:    bool('ExtractSubtitlesToSidecar'),
        SidecarSubtitleFormat:        sel('SidecarSubtitleFormat', 'srt'),
        ConvertImageSubtitlesToSrt:   bool('ConvertImageSubtitlesToSrt'),
        PassThroughImageSubtitlesMkv: bool('PassThroughImageSubtitlesMkv'),
        ExcludeSdhSubtitles:          bool('ExcludeSdhSubtitles'),

        // Encoding mode.
        EncodingMode: sel('EncodingMode', 'Transcode'),
        MuxStreams:   sel('MuxStreams',   'Both'),

        // Video.
        DownscalePolicy:     sel('DownscalePolicy', 'Never'),
        DownscaleTarget:     sel('DownscaleTarget', '1080p'),
        FixedFrameSize:      str('FixedFrameSize', '') || null,
        MaxFrameRate:        Math.max(0, num('MaxFrameRate', 0)),
        TonemapHdrToSdr:     bool('TonemapHdrToSdr'),
        FfmpegQualityPreset: sel('FfmpegQualityPreset', 'medium'),
        VideoProfile:        sel('VideoProfile', '') || null,
        VideoLevel:          sel('VideoLevel', '') || null,

        // Music — nested object on EncoderOptions, codec is derived from the format selector.
        Music: {
            Format:                   sel('MusicFormat', 'm4a'),
            Codec:                    musicCodecForFormat(sel('MusicFormat', 'm4a')),
            BitrateKbps:              num('MusicBitrate', 192),
            SampleRatePolicy:         sel('MusicSampleRate', 'Source'),
            ChannelPolicy:            sel('MusicChannels', 'Source'),
            SkipIfAlreadyTargetCodec: bool('MusicSkipIfTarget', true),
            BitrateMatchTolerancePct: Math.max(0, num('MusicTolerance', 15)),
            CopyMetadataAndArt:       bool('MusicCopyMetadata', true),
            DeleteOriginalFile:       bool('MusicDeleteOriginal'),
            MasterMusicConcurrency:   Math.max(1, num('MusicConcurrency', 2)),
            DispatchToCluster:        bool('MusicCluster', true),
        },
    };

    // Best-effort auto-save — but ONLY once a successful restore has run for
    // this prefix. Before that, the form holds HTML defaults and persisting
    // them would overwrite the user's real settings.json. Don't fail silently
    // in the disarmed state either: the user is editing a form whose changes
    // are NOT being persisted, and they need to know.
    if (restoredPrefixes.has(prefix)) {
        settingsApi.save(options)
            .then(() => reportAutoSaveStatus(true))
            .catch(() => reportAutoSaveStatus(false));
    } else {
        reportAutoSaveStatus(false,
            "⚠ Settings couldn't be loaded from the server — changes are not being saved. Close and reopen Settings to retry.");
    }

    return options;
}

/** Handle for clearing the transient "Saved" footer indicator. */
let autoSaveStatusTimer = null;

/**
 * Surfaces auto-save outcomes in the settings-modal footer. Failures used to
 * be swallowed entirely — the user kept editing a form that wasn't persisting.
 *
 * @param {boolean} ok
 * @param {string} [failureMessage] Custom message for the failure case.
 */
function reportAutoSaveStatus(ok, failureMessage) {
    const elStatus = document.getElementById('settingsAutoSaveStatus');
    if (!elStatus) return;

    clearTimeout(autoSaveStatusTimer);
    if (ok) {
        elStatus.textContent = '✓ Saved';
        elStatus.classList.remove('text-danger');
        elStatus.classList.add('text-success');
        autoSaveStatusTimer = setTimeout(() => { elStatus.textContent = ''; }, 2500);
    } else {
        elStatus.textContent = failureMessage
            ?? "⚠ Couldn't save — changes may not persist. Retry by editing again.";
        elStatus.classList.remove('text-success');
        elStatus.classList.add('text-danger');
    }
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
        if (!saved || Object.keys(saved).length === 0) {
            // Nothing saved yet (fresh install) — the HTML defaults ARE the
            // settings, so it's safe to arm auto-save.
            restoredPrefixes.add(prefix);
            announceRestored(prefix);
            return;
        }

        applyEncoderOptionsToForm(prefix, saved);

        // Restore completed — the form now reflects the server's settings,
        // so auto-saves from this point write real data, not defaults.
        restoredPrefixes.add(prefix);
        announceRestored(prefix);

    } catch { /* silent — restore is best-effort; auto-save stays disarmed */ }
}

/**
 * Signals that the form now reflects the source of truth (saved settings, or
 * the HTML defaults on a fresh install). Listeners that derive UI from the form
 * — notably the preset system's active-card highlight — re-evaluate here, since
 * programmatically setting input values doesn't fire `change` events.
 */
function announceRestored(prefix) {
    document.dispatchEvent(new CustomEvent('snacks:settings-restored', { detail: { prefix } }));
}

/**
 * Writes an options object (server settings or a preset) into the encoder
 * form. STRICTLY SPARSE: a key that is absent from `saved` leaves its input
 * completely untouched — the defaults below only translate present-but-null
 * values (e.g. a null OutputDirectory) into a usable form value. This is what
 * lets a built-in preset carry just five fields without wiping the user's
 * audio outputs, paths, languages, or music config on apply. Unknown SELECT
 * values are ignored rather than blanking the control. Shared by the restore
 * path above and the preset system (settings/presets.js).
 *
 * @param {string} prefix Id prefix shared by all form inputs.
 * @param {object} saved  Options object in either PascalCase or camelCase.
 */
export function applyEncoderOptionsToForm(prefix, saved) {
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

        /**
         * Maps a present-but-falsy value (null, "") to a default while keeping
         * ABSENT values absent — `pick(k) || d` alone would invent a value for
         * a key the caller never supplied and clobber the user's setting.
         */
        const or = (val, d) => (val === undefined ? undefined : (val || d));

        /** Writes a saved value into a form input, leaving the input alone if the value is absent. */
        const set = (id, val) => {
            const node = el(prefix, id);
            if (!node || val === undefined) return;

            if (node.type === 'checkbox') { node.checked = !!val; return; }

            if (node.tagName === 'SELECT') {
                // Map legacy aliases (e.g. DownscalePolicy "IfLarger", HW accel
                // "nvenc"/"vaapi") to the current option values. If the value
                // still matches no option, keep the select's current value —
                // assigning an unknown value blanks the select to "", and the
                // next auto-save would persist "" over the user's real setting.
                const mapped = LEGACY_SELECT_VALUES[id]?.[val] ?? val;
                const known = Array.from(node.options).some((o) => o.value === String(mapped));
                if (known) node.value = mapped;
                return;
            }

            node.value = val;
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
        const skipPct = pick('SkipPercentAboveTarget');
        set('SkipPercentAboveTarget', skipPct === undefined ? undefined : Math.max(0, skipPct));
        const logDays = pick('EncodingLogRetentionDays');
        set('EncodingLogRetentionDays', logDays === undefined ? undefined : Math.max(0, logDays));
        set('QueueNewestFirst', pick('QueueNewestFirst'));
        const verifyPerDay = pick('VerifyFilesPerDay');
        set('VerifyFilesPerDay', verifyPerDay === undefined ? undefined : Math.max(0, verifyPerDay));

        // Paths — null on the server means "unset"; render as empty string.
        const outDir = pick('OutputDirectory');
        if (outDir !== undefined) set('OutputDirectory', outDir || '');
        const encDir = pick('EncodeDirectory');
        if (encDir !== undefined) set('EncodeDirectory', encDir || '');

        // Audio.
        set('KeepOriginalLanguage',     pick('KeepOriginalLanguage'));
        set('OriginalLanguageProvider', or(pick('OriginalLanguageProvider'), 'None'));
        set('PreserveOriginalAudio',    pick('PreserveOriginalAudio'));

        const audioOutputs = pick('AudioOutputs');
        if (audioOutputs !== undefined) {
            ensureAudioOutputAddBound(prefix);
            setAudioOutputs(prefix, audioOutputs ?? []);
        }

        // Subtitles.
        set('ExtractSubtitlesToSidecar',    pick('ExtractSubtitlesToSidecar'));
        set('SidecarSubtitleFormat',        or(pick('SidecarSubtitleFormat'), 'srt'));
        set('ConvertImageSubtitlesToSrt',   pick('ConvertImageSubtitlesToSrt'));
        set('PassThroughImageSubtitlesMkv', pick('PassThroughImageSubtitlesMkv'));
        set('ExcludeSdhSubtitles',          pick('ExcludeSdhSubtitles'));
        set('AutoSetDefaultTrack',          pick('AutoSetDefaultTrack'));

        // Encoding mode.
        set('EncodingMode', or(pick('EncodingMode'), 'Transcode'));
        set('MuxStreams',   or(pick('MuxStreams'),   'Both'));

        // Video.
        set('DownscalePolicy',     or(pick('DownscalePolicy'),     'Never'));
        set('DownscaleTarget',     or(pick('DownscaleTarget'),     '1080p'));
        set('FixedFrameSize',      or(pick('FixedFrameSize'),      ''));
        set('MaxFrameRate',        or(pick('MaxFrameRate'),        0));
        set('TonemapHdrToSdr',     pick('TonemapHdrToSdr'));
        set('FfmpegQualityPreset', or(pick('FfmpegQualityPreset'), 'medium'));
        set('VideoProfile',        or(pick('VideoProfile'),        ''));
        set('VideoLevel',          or(pick('VideoLevel'),          ''));

        // Chip inputs — only when the key is present; null falls back to a sane default.
        const audioLangs = pick('AudioLanguagesToKeep');
        if (audioLangs !== undefined)
            setChipValues(`${prefix}AudioLanguagesToKeepChips`, audioLangs ?? ['en']);
        const subLangs = pick('SubtitleLanguagesToKeep');
        if (subLangs !== undefined)
            setChipValues(`${prefix}SubtitleLanguagesToKeepChips`, subLangs ?? ['en']);

        // Music settings (nested object) — skipped entirely when absent so a
        // video-only preset can't reset the music panel.
        const music = pick('Music');
        if (music) {
            const mPick = (...keys) => {
                for (const k of keys) {
                    if (music[k] !== undefined) return music[k];
                    const lower = k.charAt(0).toLowerCase() + k.slice(1);
                    if (music[lower] !== undefined) return music[lower];
                }
                return undefined;
            };

            set('MusicFormat',         mPick('Format')                   ?? 'm4a');
            set('MusicBitrate',        mPick('BitrateKbps')              ?? 192);
            set('MusicSampleRate',     mPick('SampleRatePolicy')         ?? 'Source');
            set('MusicChannels',       mPick('ChannelPolicy')            ?? 'Source');
            set('MusicSkipIfTarget',   mPick('SkipIfAlreadyTargetCodec') ?? true);
            set('MusicTolerance',      mPick('BitrateMatchTolerancePct') ?? 15);
            set('MusicCopyMetadata',   mPick('CopyMetadataAndArt')       ?? true);
            set('MusicDeleteOriginal', mPick('DeleteOriginalFile')       ?? false);
            set('MusicConcurrency',    mPick('MasterMusicConcurrency')   ?? 2);
            set('MusicCluster',        mPick('DispatchToCluster')        ?? true);
        }

        // Hide the bitrate row when the format is FLAC (lossless). Bound once —
        // this function now runs on every preset apply, and re-binding would
        // stack a listener per click for the life of the page.
        const formatEl  = el(prefix, 'MusicFormat');
        const bitrateEl = el(prefix, 'MusicBitrate')?.closest('[data-music-bitrate-row]');
        if (formatEl && bitrateEl) {
            const refreshBitrateRow = () => {
                bitrateEl.style.display = formatEl.value === 'flac' ? 'none' : '';
            };
            refreshBitrateRow();
            if (!formatEl.dataset.bitrateRowSyncBound) {
                formatEl.dataset.bitrateRowSyncBound = '1';
                formatEl.addEventListener('change', refreshBitrateRow);
            }
        }
}

/**
 * Re-runs {@link restoreEncoderOptions} only when the startup restore never
 * completed (auth redirect, transient server error). Called when the settings
 * modal opens so a failed boot-time restore doesn't leave auto-save disarmed
 * (and the form showing defaults) for the whole session.
 *
 * @param {string} [prefix='settings']
 */
export function retryRestoreEncoderOptionsIfNeeded(prefix = 'settings') {
    if (!restoredPrefixes.has(prefix)) restoreEncoderOptions(prefix);
}
