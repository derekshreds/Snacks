import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const ffmpeg = process.argv[2];
const required = process.argv.slice(3);
if (!ffmpeg) {
    throw new Error('Usage: node scripts/validate-ffmpeg-inventory.mjs <ffmpeg-path> [required-encoder ...]');
}
if (!existsSync(ffmpeg)) throw new Error(`FFmpeg binary does not exist: ${ffmpeg}`);

const baseline = required.length > 0 ? required : ['libx264', 'libx265', 'libsvtav1'];
const result = spawnSync(ffmpeg, ['-hide_banner', '-encoders'], {
    encoding: 'utf8',
    windowsHide: true,
    timeout: 30_000,
});

if (result.error) throw new Error(`Unable to inspect ${ffmpeg}: ${result.error.message}`);
if (result.status !== 0) {
    const detail = (result.stderr || result.stdout || '').trim();
    throw new Error(`FFmpeg encoder inventory failed with exit code ${result.status}: ${detail}`);
}

const inventory = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
const encoders = new Set();
for (const line of inventory.split(/\r?\n/)) {
    const match = line.match(/^\s*[A-Z.]{6}\s+(\S+)/);
    if (match) encoders.add(match[1].toLowerCase());
}

const missing = baseline.filter(name => !encoders.has(name.toLowerCase()));
if (missing.length > 0) {
    throw new Error(
        `FFmpeg is missing required baseline encoder${missing.length === 1 ? '' : 's'}: ${missing.join(', ')}`,
    );
}

const optional = ['libaom-av1', 'librav1e'].filter(name => encoders.has(name));
console.log(
    `FFmpeg inventory valid: ${baseline.join(', ')}; ` +
    `optional Advanced encoders: ${optional.length > 0 ? optional.join(', ') : 'runtime discovery only'}.`,
);
