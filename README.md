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
  <img src="https://img.shields.io/badge/version-2.0.1-8b5cf6?style=flat-square" alt="Version">
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=flat-square" alt=".NET 8">
  <img src="https://img.shields.io/badge/Electron-33-47848f?style=flat-square" alt="Electron">
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License">
</p>

---

## Features

- **Hardware accelerated encoding** -- NVIDIA NVENC, Intel QSV/VAAPI, AMD AMF/VAAPI
- **Smart filtering** -- skips files that already meet your quality targets
- **Retry with fallback** -- strips subtitles, falls back to software encoding on failure
- **Real-time progress** -- live encoding progress via SignalR WebSockets
- **Batch processing** -- process individual files, folders, or entire libraries
- **NAS-friendly** -- designed for QNAP, Synology, and other Docker-capable NAS devices
- **Desktop app** -- native Windows installer with local GPU support
- **Automatic scanning** -- watch directories for automatic re-scanning on a configurable interval
- **Dark mode UI** -- clean, modern interface that works on desktop and mobile

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
    ports:
      - "6767:6767"
    volumes:
      ## CHANGE THIS to your actual media folder on the NAS
      - /share/Public/Media:/app/work/uploads
      ## Output directory for transcoded files (optional, on NAS storage)
      #- /share/CACHEDEV1_DATA/snacks/output:/app/work/output
      ## Transcoding logs
      - /share/CACHEDEV1_DATA/snacks/logs:/app/work/logs
      ## Persist encoder settings across container updates
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
5. Install from `electron-app/dist/Snacks Setup 2.0.1.exe`

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
| Video Codec | H.265/HEVC or H.264/AVC |
| Hardware Acceleration | Auto Detect, Intel QSV, AMD VAAPI, NVIDIA NVENC, or None |
| Target Bitrate | Default 3500 kbps -- files above this get compressed |
| English Only Audio | Remove non-English audio tracks |
| English Only Subtitles | Keep only English subtitle tracks |
| Remove Black Borders | Auto-detect and crop letterboxing |
| Delete Original File | Replace original with encoded version |
| Retry on Failure | Fall back to software encoding if hardware fails |

### Automatic Scanning

Snacks can watch directories and automatically re-scan them on a configurable interval.

- Open the **Settings** panel and enable **Auto Scan**
- Set the scan interval (how often Snacks checks for new or changed files)
- Add directories to the watch list using the **Watch This Folder** button in the directory browser
- Snacks keeps a persistent scan history so previously processed files are not re-queued

Settings are saved server-side in `settings.json` and persist across container restarts and devices.

### Monitor

The main dashboard shows:
- **Now Processing** -- current file with live progress bar (always visible regardless of page)
- **Queue** -- upcoming files sorted by bitrate (highest first), with filter tabs for All, Pending, Completed, and Failed
- **Stats** -- pending, processing, completed, and failed counts
- **Pagination** -- first/prev/next/last page navigation

Click the terminal icon on any item to view detailed FFmpeg logs.

---

## How It Works

1. **Scan** -- Snacks probes each file with FFprobe to determine codec, bitrate, and resolution
2. **Filter** -- Files already meeting your quality targets are skipped automatically
3. **Encode** -- FFmpeg encodes with hardware acceleration when available, falling back to software
4. **Validate** -- Output is verified by comparing duration to the original
5. **Retry** -- On failure, retries without subtitles, then with software encoding
6. **Clean up** -- Original files are never modified; encoded files get a `[snacks]` tag

### File Naming

- Encoded files are saved as `Movie Name [snacks].mkv` alongside the original
- If **Delete Original** is enabled, the original is removed and the `[snacks]` tag is stripped
- Original files are never moved or renamed during encoding

### Smart Filtering

Files are automatically skipped if they already meet requirements:

- Already HEVC and below target bitrate (1080p: 1.5x target, 4K: 3x target)
- Already encoded (filename contains `[snacks]`)

---

## Hardware Acceleration

### Supported Platforms

| Platform | NAS (Docker) | Desktop (Windows) |
|----------|:---:|:---:|
| NVIDIA NVENC | CUDA | CUDA |
| Intel | VAAPI (CQP) | QSV (VBR) |
| AMD | VAAPI (CQP) | AMF (VBR) |
| Software (x265) | Always available | Always available |

### NAS Notes

- **QNAP TS-453E (J6412)**: Uses VAAPI with CQP rate control. VBR is not supported on Elkhart Lake.
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
  Snacks/                 ASP.NET Core 8.0 backend + web UI
    Controllers/           API endpoints
    Services/              Transcoding, file handling, FFprobe, AutoScanService
    Hubs/                  SignalR real-time communication
    Views/                 Razor pages
    wwwroot/               Static assets (JS, CSS, fonts)
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
  <strong>Snacks</strong> v2.0.1 &copy; 2026 Derek Morris
</p>
