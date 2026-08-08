const assert = require('node:assert/strict');
const { readFile } = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const repoRoot = path.resolve(__dirname, '..', '..');

async function importBrowserModule(relativePath) {
    const source = await readFile(path.join(repoRoot, relativePath), 'utf8');
    const dataUrl = `data:text/javascript;base64,${Buffer.from(source).toString('base64')}`;
    return import(`${dataUrl}#${Date.now()}-${Math.random()}`);
}

test('escapeHtml protects both text and attribute contexts', async () => {
    const { escapeHtml } = await importBrowserModule('Snacks/wwwroot/js/utils/dom.js');

    assert.equal(escapeHtml(null), '');
    assert.equal(
        escapeHtml(`<img src=x onerror="alert('x')">&`),
        '&lt;img src=x onerror=&quot;alert(&#39;x&#39;)&quot;&gt;&amp;');
});

test('queue API serializes pagination, status, and retry payloads', async () => {
    const requests = [];
    global.fetch = async (url, options = {}) => {
        requests.push({ url, options });
        return { ok: true, json: async () => ({ success: true }) };
    };

    const { queueApi } = await importBrowserModule('Snacks/wwwroot/js/api.js');
    await queueApi.getItems(50, 100, 'Pending');
    await queueApi.retry('/media/A & B.mkv');

    assert.equal(requests[0].url, '/api/queue/items?limit=50&skip=100&status=Pending');
    assert.equal(requests[0].options.method, undefined);
    assert.equal(requests[1].url, '/api/queue/retry');
    assert.equal(requests[1].options.method, 'POST');
    assert.deepEqual(JSON.parse(requests[1].options.body), { filePath: '/media/A & B.mkv' });
});

test('API helpers reject non-success responses with actionable context', async () => {
    global.fetch = async () => ({ ok: false, status: 503 });
    const { settingsApi } = await importBrowserModule('Snacks/wwwroot/js/api.js');

    await assert.rejects(settingsApi.get(), /GET \/api\/settings → 503/);
});

test('library paths are URL encoded before browsing', async () => {
    let requestedUrl = null;
    global.fetch = async url => {
        requestedUrl = url;
        return { ok: true, json: async () => ({}) };
    };
    const { libraryApi } = await importBrowserModule('Snacks/wwwroot/js/api.js');

    await libraryApi.getSubdirectories('/media/TV & Movies');
    assert.equal(
        requestedUrl,
        '/api/library/subdirectories?directoryPath=%2Fmedia%2FTV%20%26%20Movies');
});
