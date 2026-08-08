// Validates the quick-start templates in wwwroot/js/settings/advanced-video.js:
//  1. Every template builds without throwing, with unique profile names, at least
//     one condition per rule, and no dangling profile references.
//  2. Consecutive builds mint fresh ids (loading a template twice must never
//     cross-link the previous load's rules to the new profiles).
//  3. The "libaom-expert" template is field-for-field identical to
//     examples/advanced-video-policy.json once ids are normalized, so the shipped
//     example, the docs walkthrough, and the one-click template cannot drift apart.
//  4. The JS templates are the single source of truth for the C# fixture file
//     Snacks.Tests/Video/Fixtures/quick-start-templates.json. Default mode fails
//     when the committed fixture drifts from the JS; `--write` regenerates it.
//
// Usage: node scripts/validate-advanced-templates.mjs [--write]
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

// The module is written for the browser; it only touches the DOM inside
// functions, so a inert document shim is enough to import it in node.
globalThis.document = {
    getElementById: () => null,
    addEventListener: () => {},
    querySelectorAll: () => [],
    dispatchEvent: () => {},
};
globalThis.window = globalThis;

const { ADVANCED_VIDEO_TEMPLATES } = await import(
    new URL(`file://${join(repoRoot, 'Snacks/wwwroot/js/settings/advanced-video.js')}`));

let failures = 0;
const fail = message => { failures++; console.error(`FAIL: ${message}`); };

if (ADVANCED_VIDEO_TEMPLATES.length === 0) fail('no templates exported');

for (const template of ADVANCED_VIDEO_TEMPLATES) {
    const built = template.build();
    if (!template.key || !template.name || !template.description) fail(`${template.key}: missing key/name/description`);
    if (new Set(built.profiles.map(p => p.name)).size !== built.profiles.length)
        fail(`${template.key}: duplicate profile names`);
    for (const rule of built.rules) {
        if (!rule.conditions.length) fail(`${template.key}: rule "${rule.name}" has no conditions`);
        if (rule.action === 'TranscodeWithProfile' && !built.profiles.some(p => p.id === rule.profileId))
            fail(`${template.key}: rule "${rule.name}" references a missing profile`);
    }
    const again = template.build();
    if (built.profiles.some((profile, index) => profile.id === again.profiles[index].id))
        fail(`${template.key}: reused profile ids across builds`);
    console.log(`ok: ${template.key} — ${built.profiles.length} profile(s), ${built.rules.length} rule(s), default ${built.defaultAction}`);
}

const normalizeIds = policy => {
    const idMap = new Map(policy.profiles.map((profile, index) => [String(profile.id).toLowerCase(), `P${index}`]));
    return {
        profiles: policy.profiles.map(profile => ({ ...profile, id: idMap.get(String(profile.id).toLowerCase()) })),
        rules: policy.rules.map((rule, index) => ({
            ...rule,
            id: `R${index}`,
            profileId: rule.profileId == null ? null : idMap.get(String(rule.profileId).toLowerCase()) ?? 'DANGLING',
        })),
        defaultAction: policy.defaultAction,
        defaultProfileId: policy.defaultProfileId ?? null,
    };
};

const expert = ADVANCED_VIDEO_TEMPLATES.find(template => template.key === 'libaom-expert');
if (!expert) {
    fail('libaom-expert template is missing');
} else {
    const example = JSON.parse(readFileSync(join(repoRoot, 'examples/advanced-video-policy.json'), 'utf8')).advancedVideo;
    const builtLines = JSON.stringify(normalizeIds(expert.build()), null, 2).split('\n');
    const exampleLines = JSON.stringify(normalizeIds(example), null, 2).split('\n');
    if (builtLines.join('\n') !== exampleLines.join('\n')) {
        fail('libaom-expert differs from examples/advanced-video-policy.json');
        for (let i = 0; i < Math.max(builtLines.length, exampleLines.length); i++)
            if (builtLines[i] !== exampleLines[i])
                console.error(`  line ${i + 1}:\n    template: ${builtLines[i]}\n    example:  ${exampleLines[i]}`);
    } else {
        console.log('ok: libaom-expert matches examples/advanced-video-policy.json (ids normalized)');
    }
}

// ---------------------------------------------------------------------------
// Fixture generation: deterministic ids so the emitted JSON is byte-stable.
// ---------------------------------------------------------------------------

const deterministicGuid = (templateIndex, kind, ordinal) => {
    const tail = (templateIndex * 1000 + (kind === 'rule' ? 500 : 0) + ordinal + 1)
        .toString(16).padStart(12, '0');
    return `00000000-0000-4000-8000-${tail}`;
};

const fixtureEntries = ADVANCED_VIDEO_TEMPLATES.map((template, index) => {
    const built = template.build();
    const idMap = new Map();
    built.profiles.forEach((profile, ordinal) => {
        const fixed = deterministicGuid(index, 'profile', ordinal);
        idMap.set(String(profile.id).toLowerCase(), fixed);
        profile.id = fixed;
    });
    built.rules.forEach((rule, ordinal) => {
        rule.id = deterministicGuid(index, 'rule', ordinal);
        rule.profileId = rule.profileId == null ? null : idMap.get(String(rule.profileId).toLowerCase()) ?? null;
    });
    return {
        key: template.key,
        name: template.name,
        advancedVideo: {
            version: 1,
            enabled: true,
            profiles: built.profiles,
            rules: built.rules,
            defaultAction: built.defaultAction,
            defaultProfileId: built.defaultProfileId == null
                ? null : idMap.get(String(built.defaultProfileId).toLowerCase()) ?? null,
        },
    };
});

const fixturePath = join(repoRoot, 'Snacks.Tests/Video/Fixtures/quick-start-templates.json');
const fixtureJson = `${JSON.stringify({ templates: fixtureEntries }, null, 2)}\n`;
if (process.argv.includes('--write')) {
    mkdirSync(dirname(fixturePath), { recursive: true });
    writeFileSync(fixturePath, fixtureJson);
    console.log(`wrote ${fixturePath}`);
} else if (!existsSync(fixturePath)) {
    fail(`fixture missing: ${fixturePath} — run: node scripts/validate-advanced-templates.mjs --write`);
} else if (readFileSync(fixturePath, 'utf8') !== fixtureJson) {
    fail('fixture drift: the JS templates changed — run: node scripts/validate-advanced-templates.mjs --write');
} else {
    console.log('ok: C# fixture matches the JS templates');
}

if (failures) {
    console.error(`${failures} failure(s).`);
    process.exit(1);
}
console.log('All quick-start templates are valid.');
