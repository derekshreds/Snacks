/**
 * Electron main process.
 *
 * Spawns the ASP.NET Core backend (`Snacks.exe`), waits for its health
 * endpoint to come up, and then creates the BrowserWindow that loads it.
 * Handles the full lifecycle:
 *
 *   - Clean restart:       backend exits with code 0 → relaunch and reload.
 *   - Crash during boot:   backend dies before the window exists → show a
 *                          fatal dialog and quit.
 *   - Crash while running: backend dies after the window is up → attempt
 *                          one auto-restart; escalate to a fatal dialog on
 *                          failure.
 *   - App quit:            before-quit hook tree-kills the backend process
 *                          so we don't orphan it on shutdown.
 */

const { app, BrowserWindow, Menu, dialog, shell, session, nativeImage } = require("electron");
const { spawn } = require("child_process");
const path     = require("path");
const net      = require("net");
const http     = require("http");
const fs       = require("fs");
const treeKill = require("tree-kill");


// ---------------------------------------------------------------------------
// Module state
// ---------------------------------------------------------------------------

let mainWindow     = null;
let backendProcess = null;
let backendPort    = null;
let isQuitting     = false;


// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/**
 * Resolves to an available TCP port to bind the backend to.
 *
 * Prefers `preferred` when it's free (firewall rules and per-user config
 * stay predictable between launches); falls back to OS-assigned random
 * port when the preferred one is taken.
 *
 * @param {number} [preferred=6767]
 * @returns {Promise<number>}
 */
function findFreePort(preferred = 6767) {
    return new Promise((resolve, reject) => {

        // Try the preferred port first.
        const srv = net.createServer();
        srv.listen(preferred, "0.0.0.0", () => {
            srv.close(() => resolve(preferred));
        });

        srv.on("error", () => {
            // Preferred port is taken — fall back to a random one.
            const srv2 = net.createServer();
            srv2.listen(0, "0.0.0.0", () => {
                const port = srv2.address().port;
                srv2.close(() => resolve(port));
            });
            srv2.on("error", reject);
        });
    });
}

/**
 * Returns the base directory where packaged resources live.
 *
 * In a packaged build this is `<install>/resources`; during development
 * we fall back to the electron-app directory itself so `npm start` works
 * without a prior `electron-builder` pack.
 *
 * @returns {string}
 */
function getResourceBase() {
    if (app.isPackaged) return process.resourcesPath;
    return __dirname;
}

/** Adds the platform-appropriate executable suffix (.exe on Windows, none elsewhere). */
const exeName = (name) => process.platform === "win32" ? `${name}.exe` : name;

/** Path to the bundled backend executable. */
function resolveBackendExe() {
    return path.join(getResourceBase(), "backend", exeName("Snacks"));
}

/** Path to the bundled ffmpeg executable. */
function resolveFfmpegPath() {
    return path.join(getResourceBase(), "ffmpeg", exeName("ffmpeg"));
}

/** Path to the bundled ffprobe executable. */
function resolveFfprobePath() {
    return path.join(getResourceBase(), "ffmpeg", exeName("ffprobe"));
}

/**
 * Returns the per-user working directory the backend should use for its
 * queue DB, temp files, and scan history.
 *
 * - Windows: `%LOCALAPPDATA%\Snacks\work`
 * - macOS:   `~/Library/Application Support/Snacks/work`
 * - Linux:   `~/.local/share/Snacks/work`
 *
 * @returns {string}
 */
function getWorkDir() {
    const home = require("os").homedir();
    if (process.platform === "win32") {
        const localAppData = process.env.LOCALAPPDATA || path.join(home, "AppData", "Local");
        return path.join(localAppData, "Snacks", "work");
    }
    if (process.platform === "darwin") {
        return path.join(home, "Library", "Application Support", "Snacks", "work");
    }
    return path.join(home, ".local", "share", "Snacks", "work");
}


// ---------------------------------------------------------------------------
// Backend lifecycle
// ---------------------------------------------------------------------------

/**
 * Spawns the backend exe bound to `port` and wires up its stdout/stderr
 * passthrough plus the exit-handler that distinguishes clean restarts,
 * boot-time crashes, and runtime crashes.
 *
 * @param {number} port
 */
function startBackend(port) {
    const exe = resolveBackendExe();
    console.log(`Starting backend: ${exe} on port ${port}`);

    const workDir = getWorkDir();
    fs.mkdirSync(workDir, { recursive: true });

    const env = Object.assign({}, process.env, {
        ASPNETCORE_URLS:        `http://0.0.0.0:${port}`,
        ASPNETCORE_ENVIRONMENT: "Production",
        FFMPEG_PATH:            resolveFfmpegPath(),
        FFPROBE_PATH:           resolveFfprobePath(),
        SNACKS_WORK_DIR:        workDir,
        SNACKS_ALLOW_ALL_PATHS: "true",
    });

    backendProcess = spawn(exe, ["--urls", `http://0.0.0.0:${port}`], {
        env,
        cwd:         path.dirname(exe),
        stdio:       ["ignore", "pipe", "pipe"],
        windowsHide: true,
    });

    backendProcess.stdout.on("data", (d) => process.stdout.write(`[backend] ${d}`));
    backendProcess.stderr.on("data", (d) => process.stderr.write(`[backend:err] ${d}`));

    backendProcess.on("exit", (code) => {
        console.log(`Backend exited with code ${code}`);
        backendProcess = null;

        if (isQuitting) return;

        // Backend died before the window was ever created — fatal.
        if (mainWindow === null) {
            dialog.showErrorBox(
                "Snacks - Backend crashed",
                `Backend exited with code ${code} before the window could open.\n\nCheck that the backend published correctly.`,
            );
            app.quit();
            return;
        }

        // Clean exit (triggered by an in-app restart request) — relaunch.
        if (code === 0) {
            console.log("Backend exited cleanly — restarting...");
            startBackend(backendPort);
            pollHealth(backendPort)
                .then(() => { if (mainWindow) mainWindow.reload(); })
                .catch(() => {
                    dialog.showErrorBox("Snacks", "Backend failed to restart.");
                    app.quit();
                });
            return;
        }

        // Non-zero exit while the window is open — attempt auto-restart, escalate on failure.
        console.error(`Backend crashed with code ${code} — attempting restart...`);
        startBackend(backendPort);
        pollHealth(backendPort)
            .then(() => { if (mainWindow) mainWindow.reload(); })
            .catch(() => {
                dialog.showErrorBox(
                    "Snacks - Backend crashed",
                    `Backend exited with code ${code}.\n\nThe application will now close.`,
                );
                app.quit();
            });
    });
}

/**
 * Polls `/api/health` until it returns 200 or `timeoutMs` elapses.
 *
 * @param {number} port
 * @param {number} [timeoutMs=30000]
 * @param {number} [intervalMs=500]
 * @returns {Promise<void>} Resolves when the backend is healthy;
 *                          rejects when the timeout is exceeded.
 */
function pollHealth(port, timeoutMs = 30000, intervalMs = 500) {
    const url   = `http://localhost:${port}/api/health`;
    const start = Date.now();

    return new Promise((resolve, reject) => {

        const attempt = () => {
            if (Date.now() - start > timeoutMs) {
                return reject(new Error("Backend did not become healthy within timeout"));
            }

            http.get(url, (res) => {
                if (res.statusCode === 200) {
                    res.resume();
                    return resolve();
                }
                res.resume();
                setTimeout(attempt, intervalMs);
            }).on("error", () => {
                setTimeout(attempt, intervalMs);
            });
        };

        attempt();
    });
}

/**
 * Tree-kills the backend process (SIGTERM) so nothing is orphaned at quit.
 * Resolves once the kill signal has been dispatched; the actual process
 * exit is observed via the `exit` handler on `backendProcess`.
 *
 * @returns {Promise<void>}
 */
function killBackend() {
    return new Promise((resolve) => {
        if (!backendProcess || backendProcess.exitCode !== null) {
            return resolve();
        }

        const pid = backendProcess.pid;
        console.log(`Killing backend process tree (pid ${pid})`);

        treeKill(pid, "SIGTERM", (err) => {
            if (err) console.error("tree-kill error:", err);
            resolve();
        });
    });
}


// ---------------------------------------------------------------------------
// Window / Menu
// ---------------------------------------------------------------------------

/**
 * Removes Electron's default application menu. We don't need it — all
 * commands live inside the web UI.
 */
function buildMenu() {
    Menu.setApplicationMenu(null);
}

/**
 * Creates the main BrowserWindow pointed at the running backend.
 *
 * @param {number} port
 */
function createWindow(port) {
    const iconFile = process.platform === "darwin" ? "snacks.icns" : "snacks.ico";
    mainWindow = new BrowserWindow({
        width:           1280,
        height:          900,
        icon:            path.join(__dirname, "icons", iconFile),
        autoHideMenuBar: true,
        webPreferences: {
            nodeIntegration:  false,
            contextIsolation: true,
        },
    });

    mainWindow.loadURL(`http://localhost:${port}`);

    // F12 toggles DevTools (we explicitly don't install a full menu).
    mainWindow.webContents.on("before-input-event", (event, input) => {
        if (input.key === "F12") mainWindow.webContents.toggleDevTools();
    });

    // External URLs (target="_blank", window.open, in-page nav, redirects) all
    // open in the system browser — never inside the Electron window.
    const openExternal = (url) => {
        shell.openExternal(url).catch((err) => console.error(`openExternal failed for ${url}:`, err));
    };
    mainWindow.webContents.setWindowOpenHandler(({ url }) => {
        openExternal(url);
        return { action: "deny" };
    });
    const interceptNav = (event, url) => {
        if (url.startsWith(`http://localhost:${port}`)) return;
        event.preventDefault();
        openExternal(url);
    };
    mainWindow.webContents.on("will-navigate", interceptNav);
    mainWindow.webContents.on("will-redirect",  interceptNav);

    mainWindow.on("closed", () => {
        mainWindow = null;
    });
}


// ---------------------------------------------------------------------------
// App lifecycle
// ---------------------------------------------------------------------------

app.on("ready", async () => {
    try {
        // Override the process name + dock icon so dev runs (`npx electron .`) show
        // "Snacks" with our icon instead of the generic Electron defaults — packaged
        // builds get this from the .app's Info.plist, but unpackaged dev does not.
        if (!app.isPackaged) {
            app.setName("Snacks");
            // Use the PNG, not the .icns — Electron's nativeImage is finicky about
            // some .icns variants and silently produces an empty image. PNG always works.
            if (process.platform === "darwin" && app.dock) {
                const iconPath = path.join(__dirname, "icons", "snacks.png");
                if (fs.existsSync(iconPath)) {
                    app.dock.setIcon(nativeImage.createFromPath(iconPath));
                }
            }
        }

        // Clear cached JS/CSS so the renderer always loads fresh files from
        // the newly-spawned backend. Without this, an older build's assets
        // can linger after an update.
        await session.defaultSession.clearCache();
        await session.defaultSession.clearStorageData({
            storages: ["cachestorage", "serviceworkers"],
        });

        backendPort = await findFreePort();
        startBackend(backendPort);
        await pollHealth(backendPort);
        buildMenu();
        createWindow(backendPort);
    } catch (err) {
        console.error("Failed to start:", err);
        dialog.showErrorBox("Snacks - Failed to start", err.message || String(err));
        app.quit();
    }
});

app.on("window-all-closed", () => {
    app.quit();
});

// Intercept the first quit attempt to kill the backend cleanly before the
// app actually exits. Subsequent quit events (after we call app.quit()
// below) are allowed through because `quitHandled` is true.
let quitHandled = false;
app.on("before-quit", async (e) => {
    isQuitting = true;

    if (!quitHandled && backendProcess && backendProcess.exitCode === null) {
        quitHandled = true;
        e.preventDefault();
        await killBackend();
        app.quit();
    }
});

app.on("quit", () => {
    // Final safety net — fire-and-forget kill in case before-quit was
    // skipped (OS-initiated shutdown, etc.).
    if (backendProcess && backendProcess.exitCode === null) {
        treeKill(backendProcess.pid);
    }
});
