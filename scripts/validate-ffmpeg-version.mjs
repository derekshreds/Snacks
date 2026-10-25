// PixelSmash (CVE-2026-8461): the MagicYUV decoder overflows the heap on a crafted file, and
// Snacks probes every file it sees. Fixed in 8.1.2 and 7.1.5. Mirrors build-mac.sh's check for
// the Windows build scripts: refuses an older FFmpeg, or a git snapshot whose version can't be
// compared, and one without zscale (HDR tonemapping) or libvorbis (Ogg Vorbis music).
// SNACKS_ALLOW_UNPATCHED_FFMPEG=1 overrides the version check for an emergency build.
import { existsSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const ffmpeg = process.argv[2];
if (!ffmpeg) throw new Error('Usage: node scripts/validate-ffmpeg-version.mjs <ffmpeg-path>');
if (!existsSync(ffmpeg)) throw new Error(`FFmpeg binary does not exist: ${ffmpeg}`);

const run = (...args) => {
    const result = spawnSync(ffmpeg, ['-hide_banner', ...args], {
        encoding: 'utf8',
        windowsHide: true,
        timeout: 30_000,
    });
    if (result.error) throw new Error(`Unable to inspect ${ffmpeg}: ${result.error.message}`);
    return `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
};

const version = run('-version').split(/\r?\n/)[0].split(/\s+/)[2] ?? 'unknown';
const match = version.match(/^n?(\d+)\.(\d+)(?:\.(\d+))?/);
let patched = false;
if (match) {
    const [major, minor, patch] = [Number(match[1]), Number(match[2]), Number(match[3] ?? 0)];
    patched = major > 8
        || (major === 8 && minor > 1)
        || (major === 8 && minor === 1 && patch >= 2)
        || (major === 7 && minor === 1 && patch >= 5);
    if (major !== 8) {
        console.warn(`WARNING: bundling FFmpeg ${version} — Snacks' desktop build is validated against FFmpeg 8.1.x.`);
    }
}
if (!patched && process.env.SNACKS_ALLOW_UNPATCHED_FFMPEG !== '1') {
    throw new Error(
        `FFmpeg '${version}' predates the PixelSmash (CVE-2026-8461) fix, or is a git snapshot whose ` +
        'version cannot be checked. Stage a release build, 8.1.2 or newer on the 8.1 line ' +
        '(https://github.com/GyanD/codexffmpeg/releases, ffmpeg-8.1.x-full_build).',
    );
}

const missing = [];
if (!/^\s*\S+\s+zscale\s/m.test(run('-filters'))) missing.push('zscale (libzimg)');
if (!/^\s*[A-Z.]{6}\s+libvorbis\s/m.test(run('-encoders'))) missing.push('libvorbis');
if (missing.length > 0) {
    throw new Error(`FFmpeg ${version} lacks ${missing.join(', ')}. Stage a full_build, not essentials.`);
}

console.log(`FFmpeg ${version} found${patched ? '' : ' (unpatched, allowed by SNACKS_ALLOW_UNPATCHED_FFMPEG)'}.`);
