# Snacks E2E Harness

Synthetic end-to-end tests that exercise the real pipeline — real ffprobe, real
ffmpeg encodes, real cluster transfers — without needing a real media library
or days of manual testing. Two tricks make it fast:

1. **Generated media.** ffmpeg's `lavfi` test sources produce genuine,
   probe-able, encode-able clips (8s of `testsrc2` + sine audio, ~1 MB each)
   in milliseconds. Encodes finish in seconds.
2. **Hardlink fan-out.** Six unique seed files are hardlinked to as many
   distinct paths as you want — a 100,000-file library costs a few hundred MB
   of disk and a minute to create, and every path is a real file the scanner,
   prober, and encoder handle identically to production media.

## Requirements

- macOS/Linux, bash, `jq`, `curl` (`brew install jq`)
- `ffmpeg` with `libx264` + `libx265` on PATH (or set `FFMPEG_PATH`)
- .NET SDK (the harness publishes the app once into `e2e/.build`)

## Scenarios

| Script | What it proves | Default scale |
|---|---|---|
| `scenarios/01-sweep-memory.sh` | First sweep at scale: peak RSS under a ceiling, queue counts match the manifest, skip-eligible files excluded, encodes start after unpause. Writes a `memory.csv` timeline. | `COUNT=5000`, `MAX_RSS_MB=700` |
| `scenarios/02-cluster-dispatch.sh` | Master + 2 worker nodes on one machine (manual node list, no UDP). Full remote path: dispatch → upload → encode → download → placement. Asserts every file completes and **both** nodes did work. | `COUNT=20` |
| `scenarios/03-restart-resume.sh` | The DB-first queue's restart guarantee: pending survives a clean restart exactly, restart-to-healthy is seconds, and a `kill -9` mid-encode requeues the interrupted row. | `COUNT=300` |
| `scenarios/04-priority.sh` | Move-to-front changes *real* dispatch order, not just the listing — a deep-queue item is bumped and must be the first to start encoding. | `COUNT=120` |

## Usage

```bash
cd e2e
./scenarios/01-sweep-memory.sh                    # quick (≈2–4 min)
COUNT=100000 MAX_RSS_MB=700 ./scenarios/01-sweep-memory.sh   # the full soak
./scenarios/02-cluster-dispatch.sh
./scenarios/03-restart-resume.sh
./scenarios/04-priority.sh
```

Each run gets its own directory under `e2e/.runs/<scenario>-<timestamp>/`
containing the synthetic library, every instance's isolated work dir
(`config/`, `snacks.db`, logs), `app.log` per instance, and `memory.csv`
where applicable. Failed runs leave everything in place for inspection;
instances are killed on exit either way.

Set `SNACKS_E2E_REBUILD=1` to force a fresh publish after changing app code.

## How isolation works

Each instance is a real published copy of the app with its own environment:

- `SNACKS_WORK_DIR` — private config, database, uploads, and logs
- `ASPNETCORE_URLS=http://127.0.0.1:<port>` — private port
- `SNACKS_ALLOW_ALL_PATHS=true` — lets tests point at the synthetic library

Cluster scenarios use the manual-node list with `autoDiscovery=false`, so
multiple instances on one host never fight over the UDP discovery port.

## Reading memory.csv

`epoch,rss_kb,pending,processing,completed` sampled every 2s. Quick peak:

```bash
awk -F, 'NR>1 && $2>m {m=$2} END {print m/1024 " MB peak"}' .runs/01-sweep-*/memory.csv
```

Plot it against `pending` to see whether memory tracks queue depth (it must
not — that's the regression these tests exist to catch).

## Notes and limitations

- The seed mix is ~70% encodable h264, ~20% skip-eligible low-bitrate hevc,
  ~10% music — recorded per-run in the library's `.manifest`, which scenario
  assertions read. Seed mtimes are backdated because the scanner ignores
  files modified in the last 30 minutes.
- Scenario 02 measures distribution, not throughput — 8-second clips spend
  more time in transfer overhead than encoding, which is exactly what makes
  the cluster bookkeeping race-prone and worth testing.
- These complement, not replace, a final check against a handful of real
  files (HDR, PGS subtitles, odd containers) — synthetic clips are uniform
  by design.
