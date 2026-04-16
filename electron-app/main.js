const { app, BrowserWindow, Menu, dialog, shell, session } = require("electron");
const { spawn } = require("child_process");
const path = require("path");
const net = require("net");
const http = require("http");
const fs = require("fs");
const treeKill = require("tree-kill");

let mainWindow = null;
let backendProcess = null;
let backendPort = null;
let isQuitting = false;

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function findFreePort(preferred = 6767) {
  return new Promise((resolve, reject) => {
    // Try the preferred port first so firewall rules stay predictable
    const srv = net.createServer();
    srv.listen(preferred, "0.0.0.0", () => {
      srv.close(() => resolve(preferred));
    });
    srv.on("error", () => {
      // Preferred port is taken — fall back to a random one
      const srv2 = net.createServer();
      srv2.listen(0, "0.0.0.0", () => {
        const port = srv2.address().port;
        srv2.close(() => resolve(port));
      });
      srv2.on("error", reject);
    });
  });
}

function getResourceBase() {
  // In a packaged app process.resourcesPath points to <install>/resources.
  // During development fall back to the electron-app directory itself.
  if (app.isPackaged) {
    return process.resourcesPath;
  }
  return __dirname;
}

function resolveBackendExe() {
  return path.join(getResourceBase(), "backend", "Snacks.exe");
}

function resolveFfmpegPath() {
  return path.join(getResourceBase(), "ffmpeg", "ffmpeg.exe");
}

function resolveFfprobePath() {
  return path.join(getResourceBase(), "ffmpeg", "ffprobe.exe");
}

function getWorkDir() {
  if (process.platform === "win32") {
    const localAppData = process.env.LOCALAPPDATA || path.join(require("os").homedir(), "AppData", "Local");
    return path.join(localAppData, "Snacks", "work");
  }
  return path.join(require("os").homedir(), ".local", "share", "Snacks", "work");
}

// ---------------------------------------------------------------------------
// Backend lifecycle
// ---------------------------------------------------------------------------

function startBackend(port) {
  const exe = resolveBackendExe();
  console.log(`Starting backend: ${exe} on port ${port}`);

  const workDir = getWorkDir();
  fs.mkdirSync(workDir, { recursive: true });

  const env = Object.assign({}, process.env, {
    ASPNETCORE_URLS: `http://0.0.0.0:${port}`,
    ASPNETCORE_ENVIRONMENT: "Production",
    FFMPEG_PATH: resolveFfmpegPath(),
    FFPROBE_PATH: resolveFfprobePath(),
    SNACKS_WORK_DIR: workDir,
    SNACKS_ALLOW_ALL_PATHS: "true",
  });

  backendProcess = spawn(exe, ["--urls", `http://0.0.0.0:${port}`], {
    env,
    cwd: path.dirname(exe),
    stdio: ["ignore", "pipe", "pipe"],
    windowsHide: true,
  });

  backendProcess.stdout.on("data", (d) => process.stdout.write(`[backend] ${d}`));
  backendProcess.stderr.on("data", (d) => process.stderr.write(`[backend:err] ${d}`));
  backendProcess.on("exit", (code) => {
    console.log(`Backend exited with code ${code}`);
    backendProcess = null;

    if (isQuitting) return;

    if (mainWindow === null) {
      dialog.showErrorBox("Snacks - Backend crashed",
        `Backend exited with code ${code} before the window could open.\n\nCheck that the backend published correctly.`);
      app.quit();
    } else if (code === 0) {
      // Clean exit (triggered by restart request) — relaunch
      console.log("Backend exited cleanly — restarting...");
      startBackend(backendPort);
      pollHealth(backendPort).then(() => {
        if (mainWindow) mainWindow.reload();
      }).catch(() => {
        dialog.showErrorBox("Snacks", "Backend failed to restart.");
        app.quit();
      });
    } else {
      // Non-zero exit while window is open — show error and attempt restart
      console.error(`Backend crashed with code ${code} — attempting restart...`);
      startBackend(backendPort);
      pollHealth(backendPort).then(() => {
        if (mainWindow) mainWindow.reload();
      }).catch(() => {
        dialog.showErrorBox("Snacks - Backend crashed",
          `Backend exited with code ${code}.\n\nThe application will now close.`);
        app.quit();
      });
    }
  });
}

function pollHealth(port, timeoutMs = 30000, intervalMs = 500) {
  const url = `http://localhost:${port}/Home/Health`;
  const start = Date.now();

  return new Promise((resolve, reject) => {
    const attempt = () => {
      if (Date.now() - start > timeoutMs) {
        return reject(new Error("Backend did not become healthy within timeout"));
      }

      http
        .get(url, (res) => {
          if (res.statusCode === 200) {
            res.resume();
            return resolve();
          }
          res.resume();
          setTimeout(attempt, intervalMs);
        })
        .on("error", () => {
          setTimeout(attempt, intervalMs);
        });
    };

    attempt();
  });
}

function killBackend() {
  return new Promise((resolve) => {
    if (!backendProcess || backendProcess.exitCode !== null) {
      return resolve();
    }
    const pid = backendProcess.pid;
    console.log(`Killing backend process tree (pid ${pid})`);
    treeKill(pid, "SIGTERM", (err) => {
      if (err) {
        console.error("tree-kill error:", err);
      }
      resolve();
    });
  });
}

// ---------------------------------------------------------------------------
// Window / Menu
// ---------------------------------------------------------------------------

function buildMenu() {
  Menu.setApplicationMenu(null);
}

function createWindow(port) {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 900,
    icon: path.join(__dirname, "icons", "snacks.ico"),
    autoHideMenuBar: true,
    webPreferences: {
      nodeIntegration: false,
      contextIsolation: true,
    },
  });

  mainWindow.loadURL(`http://localhost:${port}`);


  // Open dev tools with F12
  mainWindow.webContents.on("before-input-event", (event, input) => {
    if (input.key === "F12") {
      mainWindow.webContents.toggleDevTools();
    }
  });

  // Open external links in the system browser
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    shell.openExternal(url);
    return { action: "deny" };
  });

  mainWindow.webContents.on("will-navigate", (event, url) => {
    if (!url.startsWith(`http://localhost:${port}`)) {
      event.preventDefault();
      shell.openExternal(url);
    }
  });

  mainWindow.on("closed", () => {
    mainWindow = null;
  });
}

// ---------------------------------------------------------------------------
// App lifecycle
// ---------------------------------------------------------------------------

app.on("ready", async () => {
  try {
    // Clear cached JS/CSS so the renderer always loads fresh files from the backend
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
  // Final safety net — fire-and-forget kill in case before-quit was skipped.
  if (backendProcess && backendProcess.exitCode === null) {
    treeKill(backendProcess.pid);
  }
});
