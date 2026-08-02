const assert = require('node:assert/strict');
const { readFile } = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');

const docsPath = path.resolve(__dirname, '..', '..', 'Snacks', 'wwwroot', 'docs', 'index.html');

test('documentation fragment links resolve and inline scripts parse', async () => {
    const html = await readFile(docsPath, 'utf8');
    const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(match => match[1]));
    const targets = [...html.matchAll(/href="#([^"]+)"/g)].map(match => match[1]);
    const missing = [...new Set(targets.filter(target => !ids.has(target)))];

    assert.deepEqual(missing, []);
    assert.ok(ids.has('quick-start'));
    assert.ok(ids.has('api-basics'));
    assert.ok(ids.has('api-realtime'));

    const scripts = [...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].map(match => match[1]);
    assert.equal(scripts.length, 1);
    scripts.forEach(script => new Function(script));
});

test('documentation marks destructive operations and links the generated contract', async () => {
    const html = await readFile(docsPath, 'utf8');

    assert.match(html, /Destructive:/);
    assert.match(html, /\/api\/library\/health\/delete-all/);
    assert.match(html, /\/openapi\/v1\.json/);
});
