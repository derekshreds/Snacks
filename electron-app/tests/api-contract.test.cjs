const assert = require('node:assert/strict');
const { readFile, readdir } = require('node:fs/promises');
const path = require('node:path');
const test = require('node:test');

const repoRoot = path.resolve(__dirname, '..', '..');
const controllersDir = path.join(repoRoot, 'Snacks', 'Controllers');
const docsPath = path.join(repoRoot, 'Snacks', 'wwwroot', 'docs', 'index.html');

function normalizePath(value) {
    const pathOnly = value.split('?')[0];
    return ('/' + pathOnly).replace(/\/+/g, '/').replace(/\/$/, '') || '/';
}

function publicRoutes(source) {
    const controllerRoute = source.match(/\[Route\("([^"]+)"\)\][\s\S]*?class\s+\w+Controller/)?.[1] ?? '';
    const routes = [];
    const action = /\[Http(Get|Post|Put|Delete|Patch|Head)(?:\("([^"]*)"\))?\][\s\S]*?public\s+(?:async\s+)?(?:Task<)?IActionResult>?[\s\S]*?\s+\w+\s*\(/g;

    for (const match of source.matchAll(action)) {
        const method = match[1].toUpperCase();
        const template = match[2] ?? '';
        const combined = template.startsWith('/') ? template : [controllerRoute, template].filter(Boolean).join('/');
        const route = normalizePath(combined);
        if ((route.startsWith('/api/') || route === '/metrics') && !route.startsWith('/api/cluster/'))
            routes.push(`${method} ${route}`);
    }
    return routes;
}

test('every public controller route appears in the HTML API reference', async () => {
    const files = (await readdir(controllersDir)).filter(file => file.endsWith('Controller.cs'));
    const routes = [];
    for (const file of files)
        routes.push(...publicRoutes(await readFile(path.join(controllersDir, file), 'utf8')));

    const docs = await readFile(docsPath, 'utf8');
    const missing = routes
        .filter(route => {
            const [method, routePath] = route.split(' ');
            return docs.includes(`>${method}<`) && !docs.includes(`<code>${routePath}</code>`);
        });

    assert.deepEqual(missing, [], `Routes missing from docs:\n${missing.join('\n')}`);
    assert.ok(routes.length >= 70, `Expected a broad public API, found only ${routes.length} routes`);
});
