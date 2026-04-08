<p align="center">
  <img src="snacks.ico" alt="Snacks" width="80">
</p>

<h1 align="center">Snacks</h1>
<p align="center"><strong>Automated Video Library Encoder</strong></p>
<p align="center">
  Batch transcode your entire video library with hardware acceleration.<br>
  Runs on your NAS via Docker or locally on Windows as a desktop app.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/version-2.2.1-8b5cf6?style=flat-square" alt="Version">
  <img src="https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square" alt=".NET 10">
  <img src="https://img.shields.io/badge/Electron-41-47848f?style=flat-square" alt="Electron">
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License">
</p>

---

## Features

- **Hardware accelerated encoding** -- NVIDIA NVENC, Intel QSV/VAAPI, AMD AMF/VAAPI
- **H.265, H.264, and AV1** -- encode to any modern codec with hardware or software
- **Smart filtering** -- skips files that already meet your quality targets
- **Persistent database** -- SQLite tracks all files across restarts, no re-scanning needed
- **Retry with fallback** -- strips subtitles, tries software decode + HW encode, then full software
- **Real-time progress** -- live encoding progress via SignalR WebSockets
- **Batch processing** -- process individual files, folders, or entire libraries
- **NAS-friendly** -- designed for QNAP, Synology, and other Docker-capable NAS devices
- **Desktop app** -- native Windows installer with local GPU support
- **Automatic scanning** -- watch directories for automatic re-scanning on a configurable interval
- **4K controls** -- configurable bitrate multiplier or skip 4K content entirely
- **Per-file logging** -- every encode writes a log file to disk, viewable in the app or on the NAS
- **Stop vs Cancel** -- stop an encode for later, or cancel it permanently
- **Change detection** -- replaced files are automatically detected and re-queued
- **Transfer-safe scanning** -- files modified within the last 30 minutes are skipped to avoid mid-transfer processing
- **Settings backup** -- atomic writes with `.bak` fallback for crash resilience
- **Distributed encoding** -- cluster multiple Snacks instances to distribute encoding across your network
- **Automatic node discovery** -- nodes find each other automatically, no manual IP configuration needed
- **Dark mode UI** -- clean, responsive interface that works on desktop, tablet, and mobile

---

## Installation

### Option 1: QNAP / Synology NAS (Docker)

Snacks runs as a Docker container with your video library mounted as a volume.

**1. Pull the image:**

```bash
docker pull derekshreds/snacks-docker:latest
```

**2. Create the application** in Container Station (QNAP) or Docker (Synology) using this compose config:

```yaml
services:
  snacks:
    image: derekshreds/snacks-docker:latest
    container_name: snacks
    ## Host networking required for cluster UDP broadcast discovery to reach the LAN.
    ## Bridge mode only forwards unicast traffic, so broadcasts never arrive.
    network_mode: host
    volumes:
      ## CHANGE THIS to your actual media folder on the NAS
      - /share/Public/Media:/app/work/uploads
      ## Transcoding logs
      - /share/CACHEDEV1_DATA/snacks/logs:/app/work/logs
      ## Persist settings and database across container updates
      - /share/CACHEDEV1_DATA/snacks/config:/app/work/config
    environment:
      - ASPNETCORE_ENVIRONMENT=Production
      - SNACKS_WORK_DIR=/app/work
      - FFMPEG_PATH=/usr/lib/jellyfin-ffmpeg/ffmpeg
      - FFPROBE_PATH=/usr/lib/jellyfin-ffmpeg/ffprobe
      ## VAAPI driver — auto-detection tries iHD then i965. Override here if needed:
      #- LIBVA_DRIVER_NAME=iHD
    devices:
      ## Intel QSV hardware acceleration (TS-453E J6412)
      - /dev/dri:/dev/dri
    ## QNAP lacks standard video/render groups — privileged grants /dev/dri access
    privileged: true
    restart: unless-stopped
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:6767/Home/Health"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 40s
```

**3. Update the volume path** to point to your actual media library:

| NAS | Typical Path |
|-----|-------------|
| QNAP | `/share/CACHEDEV1_DATA/Multimedia` or `/share/Public/Media` |
| Synology | `/volume1/video` or `/volume1/Media` |

**4. Access Snacks** at `http://YOUR-NAS-IP:6767`

---

### Option 2: Windows Desktop App

Snacks can run as a standalone desktop app with native GPU acceleration.

**Prerequisites:**
- Windows 10/11
- A supported GPU (NVIDIA, Intel, or AMD) with up-to-date drivers
- [FFmpeg](https://www.gyan.dev/ffmpeg/builds/) (`ffmpeg-release-full` build)

**Building from source:**

1. Clone the repository
2. Download FFmpeg from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) (release full build)
3. Place `ffmpeg.exe` and `ffprobe.exe` in `electron-app/ffmpeg/`
4. Run `build-installer.bat`
5. Install from `electron-app/dist/Snacks Setup 2.2.1.exe`

**For development:**

```cmd
run-electron-dev.bat
```

This publishes the backend and launches Snacks in dev mode without creating an installer.

---

## Usage

### Browse & Select

Click **Browse Library** to open the file browser.

- **NAS mode** -- browse within your mounted media directory
- **Desktop mode** -- browse any drive or folder on your PC

Navigate into folders, view video files, and choose what to process:

- **Process This Folder** -- encode only files in the current folder
- **Process Folder + Subfolders** -- encode everything recursively
- **Select individual files** -- check specific files to process

### Configure

Click the **gear icon** to open encoding settings:

| Setting | Description |
|---------|-------------|
| Output Format | MKV (default) or MP4 |
| Video Codec | H.265/HEVC, H.264/AVC, or AV1 |
| Hardware Acceleration | Auto Detect, Intel QSV, AMD VAAPI, NVIDIA NVENC, or None |
| Target Bitrate | Default 3500 kbps -- files above this get compressed |
| 4K Bitrate Multiplier | 2x--8x multiplier for 4K content (default 4x) |
| Skip 4K Videos | Leave 4K content untouched |
| English Only Audio | Remove non-English audio tracks |
| English Only Subtitles | Keep only English subtitle tracks |
| Remove Black Borders | Auto-detect and crop letterboxing |
| Output Directory | Where encoded files are saved (blank = same as original) |
| Replace Original Files | Delete original and move encoded file to its location |
| Retry on Failure | Fall back to software encoding if hardware fails |

### Automatic Scanning

Snacks can watch directories and automatically re-scan them on a configurable interval.

- Open the **Settings** panel and enable **Auto Scan**
- Set the scan interval (how often Snacks checks for new or changed files)
- Add directories to the watch list using the **Watch This Folder** button in the directory browser
- Snacks tracks all file states in a SQLite database so previously processed files are never re-scanned
- Failed files are tracked with failure counts and won't be endlessly retried
- Files modified within the last 30 minutes are skipped to avoid processing mid-transfer
- If a file is replaced with a significantly different version (>10% size change), it's automatically re-queued
- Partial `[snacks]` files from interrupted encodes are detected, deleted, and the original is re-queued

Settings are saved server-side in `settings.json` (with automatic `.bak` backup) and persist across container restarts and devices.

### Distributed Encoding (Cluster)

Snacks can distribute encoding work across multiple machines on your network.

- Open the **Settings** panel and enable **Cluster Mode**
- Set a **shared secret** that all nodes will use to authenticate with each other
- Nodes on the local network discover each other automatically
- One instance acts as the **coordinator**, assigning jobs to available **worker nodes**
- Source files are transferred to workers before encoding and results are transferred back on completion
- If a worker goes offline mid-encode, the job is automatically reassigned to another node
- Job state transitions (queued, assigned, transferring, encoding, completed, failed) are tracked in the database
- The cluster status panel shows discovered nodes with health indicators and active jobs

### Monitor

The main dashboard shows:
- **Now Processing** -- current file with live progress bar (always visible regardless of page)
- **Queue** -- upcoming files sorted by bitrate (highest first), with filter tabs for All, Pending, Completed, and Failed
- **Stats** -- pending, processing, completed, and failed counts
- **Pagination** -- first/prev/next/last page navigation

Click the terminal icon on any item to view detailed FFmpeg logs -- logs are persisted to disk and viewable even after a restart.

### Queue Management

- **Stop (Encode Later)** -- removes an item from the queue with a yellow "Stopped" badge. It will be re-queued on the next auto-scan.
- **Cancel (Don't Reprocess)** -- permanently cancels an item. It will not be re-queued unless you manually select it again.
- **Process Selected override** -- explicitly selecting a file in the browser always queues it, regardless of its database status (failed, cancelled, completed).
- **Pause/Resume** -- pause state is saved across restarts. If the queue was paused when the container stopped, it stays paused.

### Logs

- Every encode writes a log file to the `logs` directory (e.g., `The Matrix (1999)_a3f2b1c4.log`)
- Logs are viewable in the app by clicking the terminal icon on any queue item
- On NAS deployments, logs are accessible via the mounted logs volume

---

## How It Works

1. **Scan** -- Snacks probes each file with FFprobe to determine codec, bitrate, and resolution
2. **Filter** -- Files already meeting your quality targets are skipped automatically
3. **Encode** -- FFmpeg encodes with hardware acceleration when available, falling back to software
4. **Validate** -- Output is verified by comparing duration to the original
5. **Retry** -- On failure, retries without subtitles, then software decode + HW encode, then full software
6. **Clean up** -- Original files are never modified; encoded files get a `[snacks]` tag

### File Naming

- Encoded files are saved as `Movie Name [snacks].mkv` alongside the original (or in the output directory if configured)
- If **Replace Original Files** is enabled, the original is deleted and the encoded file is moved to the original's location without the `[snacks]` tag
- Original files are never modified during encoding

### Smart Filtering

Files are automatically skipped if they already meet requirements:

- Already target codec and below target bitrate (1080p: 1.2x target, 4K: configurable multiplier)
- Already encoded (filename contains `[snacks]`)

---

## Hardware Acceleration

### Supported Platforms

| Platform | NAS (Docker) | Desktop (Windows) |
|----------|:---:|:---:|
| NVIDIA NVENC | CUDA | CUDA |
| Intel | VAAPI (CQP) | QSV (VBR) |
| AMD | VAAPI (CQP) | AMF (VBR) |
| Software (x265/SVT-AV1) | Always available | Always available |

### NAS Notes

- **QNAP TS-453E (J6412)**: Uses VAAPI with CQP rate control. VBR is not supported on Elkhart Lake.
- **Software decode fallback**: Codecs that VAAPI can't decode (e.g., AV1 on older Intel hardware) are automatically decoded in software while still using VAAPI for hardware encoding.
- The container uses [jellyfin-ffmpeg](https://github.com/jellyfin/jellyfin-ffmpeg) which includes full VAAPI/QSV support.
- `privileged: true` is required on QNAP for `/dev/dri` access.
- Hardware detection runs automatically on first encode and caches the result.

### Desktop Notes

- NVIDIA GPUs use NVENC with VBR for precise bitrate control.
- Intel/AMD use QSV/AMF respectively with proper bitrate targeting.
- Auto-detect tests each encoder at startup and picks the best available.

---

## Troubleshooting

**No directories in browser (NAS)**
```bash
docker exec snacks ls -la /app/work/uploads/
```
Verify your volume mount points to a directory with video files.

**Hardware acceleration not detected**
```bash
# Check GPU access inside the container
docker exec snacks vainfo
docker exec snacks ls -la /dev/dri/
```

**Encoding produces larger files (NAS/VAAPI)**

VAAPI CQP mode doesn't have precise bitrate control. The quality parameter is automatically scaled based on the target, but results vary by content. Files that end up larger than the original are automatically discarded.

**Progress bar not updating**

Check that the backend is running and SignalR is connected (green dot in the navbar). Progress updates are throttled to every 2 seconds.

---

## Project Structure

```
Snacks/
  Snacks/                 ASP.NET Core 10.0 backend + web UI
    Controllers/           API endpoints
    Services/              Transcoding, file handling, FFprobe, AutoScan, cluster services
    Data/                  SQLite database context, migrations, repository
    Models/                WorkItem, MediaFile, EncoderOptions, ClusterConfig, JobAssignment
    Hubs/                  SignalR real-time communication
    Views/                 Razor pages
    wwwroot/               Static assets (JS, CSS, fonts)
  release-notes/          Per-version release notes
  electron-app/           Electron desktop wrapper
    main.js               Electron main process
    backend/              Published .NET backend (gitignored)
    ffmpeg/               Bundled FFmpeg binaries (gitignored)
  docker-compose.gpu.yml  GPU overlay for Linux NAS (VAAPI/QSV)
  build-and-export.bat    Build & push Docker image
  build-installer.bat     Build Windows installer
  build-electron.bat      Build Electron app
  run-electron-dev.bat    Run desktop app in dev mode
  deploy-compose.yml      Docker Compose for NAS deployment
  start-snacks.bat     Start backend (Windows)
  start-snacks.sh      Start backend (Linux/Docker)
```

---

## Building

### Docker Image (for NAS)

```cmd
build-and-export.bat
```

Builds the Docker image and pushes to Docker Hub as both `derekshreds/snacks-docker:latest` and `derekshreds/snacksweb:latest`.

### Windows Installer

```cmd
build-installer.bat
```

Creates a self-contained Windows installer at `electron-app/dist/` with the .NET runtime, FFmpeg, and desktop shortcuts bundled.

---

<p align="center">
  <strong>Snacks</strong> v2.2.1 &copy; 2026 Derek Morris
</p>
