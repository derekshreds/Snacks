const source = process.argv[2];
if (!source) throw new Error('Usage: node scripts/validate-openapi.mjs <url-or-file>');

let json;
if (/^https?:\/\//.test(source)) {
    const response = await fetch(source);
    if (!response.ok) throw new Error(`OpenAPI request failed: ${response.status}`);
    json = await response.json();
} else {
    const { readFile } = await import('node:fs/promises');
    json = JSON.parse(await readFile(source, 'utf8'));
}

const paths = Object.keys(json.paths ?? {});
if (!json.openapi?.startsWith('3.')) throw new Error(`Unexpected OpenAPI version: ${json.openapi}`);
if (paths.length < 50) throw new Error(`OpenAPI document is unexpectedly small: ${paths.length} paths`);
if (paths.some(path => path.startsWith('/api/cluster/')))
    throw new Error('Public OpenAPI document contains internal cluster RPC routes');
if (!paths.includes('/api/health')) throw new Error('OpenAPI document is missing /api/health');
if (!paths.includes('/api/queue/items')) throw new Error('OpenAPI document is missing /api/queue/items');

console.log(`OpenAPI contract valid: ${json.openapi}, ${paths.length} public paths.`);
