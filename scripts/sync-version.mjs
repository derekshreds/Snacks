import { readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const checkOnly = process.argv.includes('--check');
const projectPath = path.join(repoRoot, 'Snacks', 'Snacks.csproj');
const project = await readFile(projectPath, 'utf8');
const match = project.match(/<Version>([^<]+)<\/Version>/);

if (!match) throw new Error(`No <Version> found in ${projectPath}`);
const version = match[1].trim();
if (!/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(version))
    throw new Error(`Unsupported semantic version: ${version}`);

const edits = [
    {
        file: 'electron-app/package.json',
        update: text => text.replace(
            /("name":\s*"snacks",\s*\n\s*"version":\s*")[^"]+/,
            `$1${version}`)
    },
    {
        file: 'electron-app/package-lock.json',
        update: text => text
            .replace(/^  "version": "[^"]+"/m, `  "version": "${version}"`)
            .replace(
                /("": \{\s*\n\s*"name": "snacks",\s*\n\s*"version": ")[^"]+/,
                `$1${version}`)
    },
    {
        file: 'build-and-export.bat',
        update: text => text.replace(/^set VERSION=.*$/m, `set VERSION=${version}`)
    },
    {
        file: 'README.md',
        update: text => text
            .replace(/version-\d+\.\d+\.\d+-/g, `version-${version}-`)
            .replace(/<strong>Snacks<\/strong> v\d+\.\d+\.\d+/g, `<strong>Snacks</strong> v${version}`)
    },
    {
        file: 'Snacks/wwwroot/docs/index.html',
        update: text => text
            .replace(/Documents Snacks v\d+\.\d+\.\d+/g, `Documents Snacks v${version}`)
            .replace(/the v\d+\.\d+\.\d+ contract/g, `the v${version} contract`)
            .replace(/Snacks documentation for v\d+\.\d+\.\d+/g, `Snacks documentation for v${version}`)
    }
];

const stale = [];
for (const edit of edits) {
    const absolute = path.join(repoRoot, edit.file);
    const before = await readFile(absolute, 'utf8');
    const after = edit.update(before);
    if (after === before) continue;
    stale.push(edit.file);
    if (!checkOnly) await writeFile(absolute, after);
}

if (checkOnly && stale.length) {
    console.error(`Version ${version} is not synchronized in:\n${stale.map(x => `- ${x}`).join('\n')}`);
    process.exitCode = 1;
} else if (checkOnly) {
    console.log(`Version ${version} is synchronized.`);
} else {
    console.log(stale.length
        ? `Synchronized version ${version}: ${stale.join(', ')}`
        : `Version ${version} was already synchronized.`);
}
