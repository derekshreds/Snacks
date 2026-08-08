const assert = require('node:assert/strict');
const { readFile } = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');

const docsPath = path.resolve(__dirname, '..', '..', 'Snacks', 'wwwroot', 'docs', 'index.html');
const buildingPath = path.resolve(__dirname, '..', '..', 'docs', 'BUILDING.md');

test('documentation fragment links resolve and inline scripts parse', async () => {
    const html = await readFile(docsPath, 'utf8');
    const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]));
    const targets = [...html.matchAll(/href="#([^"]+)"/g)].map(match => match[1]);
    const missing = [...new Set(targets.filter(target => !ids.has(target)))];

    assert.deepEqual(missing, []);
    assert.ok(ids.has('quick-start'));
    assert.ok(ids.has('processing-details'));
    assert.ok(ids.has('hardware-containers'));
    assert.ok(ids.has('api-basics'));
    assert.ok(ids.has('api-realtime'));

    const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]);
    assert.equal(scripts.length, 1);
    scripts.forEach(script => new Function(script));
});

test('removed README operational details remain in maintained documentation', async () => {
    const [html, building] = await Promise.all([
        readFile(docsPath, 'utf8'),
        readFile(buildingPath, 'utf8'),
    ]);

    for (const detail of [
        'Movie [snacks].mkv',
        'more than 10%',
        'more than 30 seconds',
        'Retry without subtitles',
        'NVIDIA_DRIVER_CAPABILITIES=compute,video,utility',
        'SNACKS_SET_AudioOutputs',
        'QueuePaused',
    ]) {
        assert.ok(html.includes(detail), `HTML guide is missing: ${detail}`);
    }

    for (const detail of [
        'build-installer.bat',
        'ffmpeg.exe',
        'APPLE_APP_SPECIFIC_PASSWORD',
        'xcrun stapler validate',
        'build-and-export.bat',
        'Release checklist',
    ]) {
        assert.ok(building.includes(detail), `Build guide is missing: ${detail}`);
    }
});

test('documentation marks destructive operations and links the generated contract', async () => {
    const html = await readFile(docsPath, 'utf8');

    assert.match(html, /Destructive:/);
    assert.match(html, /\/api\/library\/health\/delete-all/);
    assert.match(html, /\/openapi\/v1\.json/);
});
