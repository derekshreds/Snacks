# Building and releasing Snacks

This guide is for contributors and release maintainers building Snacks from source.
End users installing a published desktop package or Docker image do not need these tools.

## Supported build targets

| Target | Host used to build it | Output |
|---|---|---|
| ASP.NET Core web app | Windows, macOS, or Linux | `dotnet run` or a published backend directory |
| Windows desktop | Windows x64 | NSIS installer under `electron-app/dist/` |
| macOS desktop | Apple silicon macOS 11+ | DMG under `electron-app/dist/` |
| Docker / NAS | Docker Buildx host | Linux container image |

The backend targets .NET 10 and the desktop wrapper uses Electron 43. Use Node.js 22 LTS
or newer for repository tooling.

## Common setup

Install:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 22 LTS or newer](https://nodejs.org/)
- Git
- FFmpeg and FFprobe when running the media pipeline or packaging a desktop build

From the repository root:

```bash
dotnet restore Snacks.sln
npm --prefix electron-app ci
dotnet build Snacks.sln --configuration Release
dotnet test Snacks.sln --configuration Release --no-build --verbosity minimal
npm --prefix electron-app run check
```

`npm run check` validates the Electron entry points, browser modules, HTML/API documentation,
and release-version synchronization.

## Run the web application

```bash
dotnet run --project Snacks/Snacks.csproj
```

The default standalone URL is `http://localhost:6767`. Runtime state is stored under the
platform-specific Snacks application-data directory unless `SNACKS_WORK_DIR` is set.

For an isolated development instance:

```bash
SNACKS_WORK_DIR=/tmp/snacks-dev \
ASPNETCORE_URLS=http://127.0.0.1:16767 \
dotnet run --project Snacks/Snacks.csproj
```

Useful live checks:

```bash
curl -fsS http://127.0.0.1:16767/api/health
curl -fsS http://127.0.0.1:16767/openapi/v1.json
node scripts/validate-openapi.mjs http://127.0.0.1:16767/openapi/v1.json
```

## Windows desktop build

### Requirements

- Windows 10 or 11, x64
- .NET 10 SDK
- Node.js 22 or newer
- Current NVIDIA, Intel, or AMD drivers when testing hardware encoding
- A Windows `ffmpeg-release-full` build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)

Place both executables here:

```text
electron-app/ffmpeg/ffmpeg.exe
electron-app/ffmpeg/ffprobe.exe
```

Native package builds run `scripts/validate-ffmpeg-inventory.mjs` before packaging and fail if
the staged FFmpeg does not advertise the baseline `libx264`, `libx265`, and `libsvtav1`
encoders. Optional Advanced Video implementations such as libaom and rav1e are discovered at
runtime and are not packaging requirements.

They also run `scripts/validate-advanced-templates.mjs`, which builds every Advanced Video
quick-start template from the settings UI module and fails if a template has dangling profile
references, if the expert template drifts from `examples/advanced-video-policy.json`, or if
the generated C# fixture (`Snacks.Tests/Video/Fixtures/quick-start-templates.json`) is stale —
the JS templates are the single source of truth; regenerate the fixture with
`node scripts/validate-advanced-templates.mjs --write` after changing them.

`scripts/test-advanced-video-ui.mjs` unit-tests the pure display logic in the Advanced Video
settings module (plain-language rule sentences, byte humanization, policy import id-remapping)
and runs in both native package builds as well.

### Development launch

```cmd
run-electron-dev.bat
```

The script publishes a self-contained `win-x64` backend into `electron-app/backend/`, verifies
the FFmpeg binaries, and starts Electron without creating an installer.

### Installer

```cmd
build-installer.bat
```

The resulting installer is written to `electron-app/dist/`. It contains the Electron shell,
self-contained .NET runtime and backend, FFmpeg/FFprobe, application icons, and shortcuts.

The installer build supports optional Authenticode signing:

```text
signing/snacks-signing.pfx
signing/password.txt
```

`password.txt` contains only the PFX password and both files must stay out of source control.
When the certificate is absent, the script creates an unsigned installer. `create-cert.bat`
can generate a self-signed development certificate; public releases should use a trusted
code-signing certificate.

## macOS desktop build

### Requirements

- Apple silicon Mac running macOS 11 or later
- .NET 10 SDK
- Node.js 22 or newer
- Xcode command-line tools (`xcode-select --install`)
- Homebrew packages used only on the build machine:

```bash
brew install ffmpeg tesseract leptonica
```

Copy FFmpeg and FFprobe into the package staging directory:

```bash
mkdir -p electron-app/ffmpeg
cp "$(brew --prefix ffmpeg)/bin/ffmpeg" electron-app/ffmpeg/
cp "$(brew --prefix ffmpeg)/bin/ffprobe" electron-app/ffmpeg/
chmod +x electron-app/ffmpeg/ffmpeg electron-app/ffmpeg/ffprobe
xattr -d com.apple.quarantine electron-app/ffmpeg/* 2>/dev/null || true
```

### DMG build

```bash
./build-mac.sh
```

The script:

1. Clears prior `electron-app/backend/` and `electron-app/dist/` output.
2. Bundles FFmpeg's non-system dylib dependencies and rewrites them to relative paths.
3. Publishes the backend as self-contained `osx-arm64`.
4. Bundles Tesseract, Leptonica, and their non-system dylib dependencies.
5. Runs Electron Builder and writes the DMG to `electron-app/dist/`.

The resulting application is self-contained; end users do not need Homebrew, .NET, FFmpeg,
Tesseract, or Leptonica installed.

### Signing and notarization

Unsigned local builds work without Apple credentials. For distribution, join the Apple Developer
Program, install a **Developer ID Application** certificate through Xcode, create an app-specific
password, and find the ten-character Team ID in the Apple developer account.

Create the gitignored `electron-app/.env.mac.local` file:

```bash
export CSC_NAME="Developer ID Application: Your Name (TEAMID)"
export APPLE_ID="you@example.com"
export APPLE_APP_SPECIFIC_PASSWORD="xxxx-xxxx-xxxx-xxxx"
export APPLE_TEAM_ID="ABCD123456"
```

`build-mac.sh` sources that file automatically. Electron Builder signs the application and the
`electron-app/scripts/notarize.js` hook submits it through Apple's `notarytool`. If signing or
notarization variables are missing, the corresponding step is skipped; users must normally use
**Control-click → Open** the first time they launch an unsigned build.

Verify a distribution build:

```bash
codesign --verify --deep --strict --verbose=2 electron-app/dist/mac-arm64/Snacks.app
spctl --assess --type execute --verbose=4 electron-app/dist/mac-arm64/Snacks.app
xcrun stapler validate electron-app/dist/Snacks-*-arm64.dmg
```

Never commit `.env.mac.local`, certificates, passwords, or notarization credentials.

## Docker image and NAS deployment

Build a local image without publishing it:

```bash
docker buildx build \
  --tag snacks:local \
  --load \
  --provenance=false \
  --sbom=false \
  -f Snacks/Dockerfile .
```

Use `deploy-compose.yml` as the maintained QNAP-oriented deployment example. Change the media,
config, and log host paths before starting it. `unraid/snacks.xml` and `unraid/README.md` contain
the Unraid-specific template and GPU instructions.

### Maintainer publishing script

```cmd
build-and-export.bat
```

This is a publishing operation, not a local build shortcut. After an explicit `YES` confirmation,
it builds and pushes both `latest` and the project version to:

- `derekshreds/snacks-docker`
- `derekshreds/snacksweb`

Authenticate with `docker login` first and confirm the version synchronization check passes.

## Versioning

`<Version>` in `Snacks/Snacks.csproj` is the release-version source of truth. After changing it:

```bash
node scripts/sync-version.mjs
node scripts/sync-version.mjs --check
```

The script synchronizes Electron metadata, lockfile metadata, Docker publishing metadata,
README display text, and the HTML documentation. Do not hand-edit those generated version values.

## End-to-end scenarios

The real-process harness in `e2e/` generates synthetic media and exercises FFprobe, FFmpeg,
SQLite persistence, cluster transfer, restart recovery, queue priority, and high-volume scans.
See `e2e/README.md` for requirements and outputs.

```bash
cd e2e
./scenarios/01-sweep-memory.sh
./scenarios/02-cluster-dispatch.sh
./scenarios/03-restart-resume.sh
./scenarios/04-priority.sh
```

Run representative real files separately—especially HDR, image subtitles, unusual containers,
and each hardware encoder family available for a release.

## Repository map

```text
Snacks/
├── Snacks/                     ASP.NET Core backend and web UI
│   ├── Controllers/            MVC pages and HTTP endpoints
│   ├── Data/                   EF Core context, repositories, and migrations
│   ├── Hubs/                   SignalR real-time events
│   ├── Models/                 Configuration, queue, media, and cluster contracts
│   ├── Services/               Transcoding, scanning, routing, cluster, and integrations
│   ├── Views/                  Razor UI
│   ├── wwwroot/                JavaScript, CSS, assets, and the HTML handbook
│   └── Dockerfile              Runtime container image
├── Snacks.Tests/               Unit and integration tests
├── electron-app/               Electron wrapper, packaging config, and frontend tests
├── e2e/                        Synthetic real-process scenarios
├── docs/                       Repository documentation and screenshots
├── release-notes/              Versioned implementation notes
├── scripts/                    Version and OpenAPI validation tools
├── unraid/                     Unraid template and instructions
├── deploy-compose.yml          QNAP/NAS deployment example
├── build-installer.bat         Windows installer build
├── build-mac.sh                Apple silicon DMG build
└── build-and-export.bat         Maintainer Docker publish workflow
```

## Release checklist

1. Update `<Version>` in `Snacks/Snacks.csproj` and run the synchronization script.
2. Update release notes and confirm documentation describes changed behavior.
3. Run release build, .NET tests, Electron checks, FFmpeg inventory validation, and both package vulnerability audits.
4. Run the OpenAPI validator against a production-mode local instance.
5. Run relevant E2E scenarios and representative real-media tests.
6. Build the target installer or image on its native build host.
7. Verify signatures/notarization where applicable and smoke-test the packaged artifact.
8. Publish only after inspecting the final version, generated filenames, and repository status.

The GitHub Actions workflow in `.github/workflows/ci.yml` repeats the portable build, test,
security, documentation, and OpenAPI checks for pushes and pull requests.
