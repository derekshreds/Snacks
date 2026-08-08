// Unit tests for the pure display logic in wwwroot/js/settings/advanced-video.js:
// the plain-language sentence formatter the decision flow renders, byte
// humanization in the impact legend, and the id remapping applied to imported
// or user-saved policies. Same zero-dependency style as the sibling validators.
//
// Usage: node scripts/test-advanced-video-ui.mjs
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

globalThis.document = {
    getElementById: () => null,
    addEventListener: () => {},
    querySelectorAll: () => [],
    dispatchEvent: () => {},
};
globalThis.window = globalThis;

const {
    describeCondition, describeRule, actionPhrase, fmtBytes, materializePolicy, formatConditionValue,
} = await import(new URL(`file://${join(repoRoot, 'Snacks/wwwroot/js/settings/advanced-video.js')}`));

let failures = 0;
const check = (label, actual, expected) => {
    const a = JSON.stringify(actual);
    const b = JSON.stringify(expected);
    if (a === b) { console.log(`ok: ${label}`); return; }
    failures++;
    console.error(`FAIL: ${label}\n  actual:   ${a}\n  expected: ${b}`);
};

// --- formatConditionValue -------------------------------------------------
check('bytes humanize to GB', formatConditionValue('FileSizeBytes', '10000000000'), '10 GB');
check('bytes humanize to MB', formatConditionValue('FileSizeBytes', '500000000'), '500 MB');
check('duration humanizes to minutes', formatConditionValue('DurationSeconds', '600'), '10 min');
check('duration humanizes to hours', formatConditionValue('DurationSeconds', '7200'), '2 h');
check('bitrate keeps kb/s unit', formatConditionValue('BitrateKbps', '5000'), '5000 kb/s');
check('non-numeric passes through', formatConditionValue('Codec', 'h264'), 'h264');

// --- describeCondition ----------------------------------------------------
const cond = (field, operator, ...values) => ({ field, operator, values });
check('codec is-not sentence',
    describeCondition(cond('Codec', 'IsNot', 'av1')), 'the codec is not av1');
check('in-list joins with or',
    describeCondition(cond('Codec', 'In', 'h264', 'hevc', 'vc1')), 'the codec is one of h264, hevc or vc1');
check('between renders both bounds humanized',
    describeCondition(cond('DurationSeconds', 'Between', '60', '120')), 'the duration is between 1 min and 2 min');
check('boolean true reads naturally',
    describeCondition(cond('IsHdr', 'Is', 'true')), 'it is HDR');
check('boolean negation reads naturally',
    describeCondition(cond('IsHdr', 'Is', 'false')), 'it is not HDR');
check('is-not false double-negates to positive',
    describeCondition(cond('Is4K', 'IsNot', 'false')), 'it is 4K');
check('is-unknown has no value clause',
    describeCondition(cond('BitrateKbps', 'IsUnknown')), 'the video bitrate is unknown');

// --- describeRule -----------------------------------------------------------
const profiles = [{ id: 'p1', name: 'AV1 4K' }];
check('all-rule joins with and, names the recipe',
    describeRule({
        match: 'All', action: 'TranscodeWithProfile', profileId: 'p1',
        conditions: [cond('Codec', 'IsNot', 'av1'), cond('ResolutionClass', 'Is', '2160p+')],
    }, profiles),
    'If the codec is not av1 and the resolution class is 2160p+ → encode with “AV1 4K”');
check('any-rule joins with or',
    describeRule({
        match: 'Any', action: 'Skip',
        conditions: [cond('IsHdr', 'Is', 'true'), cond('Is4K', 'Is', 'true')],
    }, profiles),
    'If it is HDR, or it is 4K → skip the file entirely');
check('missing recipe is called out',
    describeRule({ match: 'All', action: 'TranscodeWithProfile', profileId: 'nope', conditions: [cond('Codec', 'Is', 'h264')] }, profiles),
    'If the codec is h264 → encode with a recipe (none selected)');
check('empty conditions warn instead of lying',
    describeRule({ match: 'All', action: 'MuxOnly', conditions: [] }, profiles),
    'Never matches — add a condition. Would remux only — video is copied.');

// --- actionPhrase / fmtBytes -------------------------------------------------
check('mux phrase', actionPhrase('MuxOnly', null), 'remux only — video is copied');
check('simple phrase', actionPhrase('UseSimpleSettings', null), 'use the Simple settings above');
check('fmtBytes GB', fmtBytes(1_500_000_000), '1.5 GB');
check('fmtBytes TB', fmtBytes(2_000_000_000_000), '2.0 TB');
check('fmtBytes zero is null', fmtBytes(0), null);

// --- materializePolicy -------------------------------------------------------
const stored = {
    version: 1,
    enabled: true,
    profiles: [{ id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', name: 'P' }],
    rules: [
        { id: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', name: 'R', action: 'TranscodeWithProfile', profileId: 'AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA', conditions: [cond('Codec', 'Is', 'h264')] },
        { id: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', name: 'D', action: 'Skip', profileId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', conditions: [cond('Codec', 'Is', 'av1')] },
    ],
    defaultAction: 'TranscodeWithProfile',
    defaultProfileId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
};
const materialized = materializePolicy(stored);
check('profile ids are regenerated',
    materialized.profiles[0].id !== stored.profiles[0].id, true);
check('rule references remap onto the new profile id (case-insensitive)',
    materialized.rules[0].profileId === materialized.profiles[0].id, true);
check('dangling references become null instead of pointing nowhere',
    materialized.rules[1].profileId, null);
check('default profile reference remaps too',
    materialized.defaultProfileId === materialized.profiles[0].id, true);

if (failures) {
    console.error(`${failures} failure(s).`);
    process.exit(1);
}
console.log('All advanced-video UI logic tests passed.');
