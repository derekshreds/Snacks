/**
 * Typed wrappers for the Snacks HTTP API.
 *
 * Every module in the frontend talks to the server through one of the
 * exported `*Api` objects — no other file should call `fetch` directly. This
 * keeps URL strings, HTTP method choices, and JSON serialization in one
 * place, and gives us a single throat to choke when an endpoint shape
 * changes.
 *
 * Errors bubble as plain `Error` objects whose message includes the
 * method, URL, and HTTP status (e.g. `GET /api/settings → 500`). Callers
 * are expected to catch and surface them via `showToast` or similar.
 */


// ---------------------------------------------------------------------------
// Internal fetch helpers
// ---------------------------------------------------------------------------

/**
 * Issues a GET and returns the parsed JSON body.
 *
 * @param {string} url
 * @returns {Promise<any>}
 * @throws {Error} When the response status is not OK.
 */
async function getJson(url) {
    const resp = await fetch(url);
    if (!resp.ok) throw new Error(`GET ${url} → ${resp.status}`);
    return resp.json();
}

/**
 * Issues a POST with a JSON body and returns the parsed JSON response.
 *
 * @param {string}      url
 * @param {unknown}     [body]    Sent as `{}` when omitted/null.
 * @param {AbortSignal} [signal]  Optional — abort the request when triggered.
 * @returns {Promise<any>}
 * @throws {Error} When the response status is not OK.
 */
async function postJson(url, body, signal) {
    const resp = await fetch(url, {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify(body ?? {}),
        signal,
    });
    if (!resp.ok) throw new Error(`POST ${url} → ${resp.status}`);
    return resp.json();
}

/**
 * Issues a DELETE (optionally with a JSON body) and returns the parsed JSON
 * response.
 *
 * @param {string} url
 * @param {unknown} [body] Omitted from the request entirely when null/undefined.
 * @returns {Promise<any>}
 * @throws {Error} When the response status is not OK.
 */
async function deleteJson(url, body) {
    const resp = await fetch(url, {
        method:  'DELETE',
        headers: { 'Content-Type': 'application/json' },
        body:    body != null ? JSON.stringify(body) : undefined,
    });
    if (!resp.ok) throw new Error(`DELETE ${url} → ${resp.status}`);
    return resp.json();
}


// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

/**
 * Encoder settings (persisted server-side, mirrored into the settings form).
 */
export const settingsApi = {
    /** Fetches the currently persisted encoder settings. */
    get:  ()        => getJson('/api/settings'),

    /** Persists the supplied encoder settings object. */
    save: (options) => postJson('/api/settings', options),
};


// ---------------------------------------------------------------------------
// Queue
// ---------------------------------------------------------------------------

/**
 * Transcoding work-queue CRUD plus pause/resume.
 */
export const queueApi = {
    /**
     * Lists queue items, optionally filtered by status and paginated.
     * Any of the three parameters may be null/undefined to omit them.
     *
     * @param {number|null} limit
     * @param {number|null} skip
     * @param {string|null} status  Status name (e.g. "Pending", "Completed").
     */
    getItems: (limit, skip, status) => {
        const params = new URLSearchParams();
        if (limit  != null) params.set('limit',  limit);
        if (skip   != null) params.set('skip',   skip);
        if (status)         params.set('status', status);

        const qs = params.toString();
        return getJson('/api/queue/items' + (qs ? '?' + qs : ''));
    },

    /** Fetches aggregate counters (pending / processing / completed / failed). */
    getStats:  ()         => getJson('/api/queue/stats'),

    /** Fetches a single work item by id. */
    getItem:   (id)       => getJson(`/api/queue/item/${encodeURIComponent(id)}`),

    /** Fetches the persisted log lines for a single work item. */
    getLogs:   (id)       => getJson(`/api/queue/logs/${encodeURIComponent(id)}`),

    /** Cancels a queued or in-progress item (it won't be reprocessed). */
    cancel:    (id)       => postJson(`/api/queue/cancel/${encodeURIComponent(id)}`),

    /** Stops an in-progress item (it will be re-queued on the next scan). */
    stop:      (id)       => postJson(`/api/queue/stop/${encodeURIComponent(id)}`),

    /** Re-adds a failed item to the queue by its original file path. */
    retry:     (filePath) => postJson('/api/queue/retry', { filePath }),

    /** Fetches the list of items that are currently in a failed state. */
    getFailed: ()         => getJson('/api/queue/failed'),

    /** Sets the queue pause state. */
    setPaused: (paused)   => postJson('/api/queue/paused', { paused }),

    /** Fetches the current queue pause state. */
    getPaused: ()         => getJson('/api/queue/paused'),
};


// ---------------------------------------------------------------------------
// Library browser
// ---------------------------------------------------------------------------

/**
 * Filesystem browser for the library modal (list directories, enqueue jobs).
 */
export const libraryApi = {
    /** Lists the configured root directories plus their video counts. */
    getDirectories:    ()     => getJson('/api/library/directories'),

    /** Lists the immediate subdirectories of `path`. */
    getSubdirectories: (path) => getJson(`/api/library/subdirectories?directoryPath=${encodeURIComponent(path)}`),

    /**
     * Lists the video files under `path`.
     *
     * @param {string}  path
     * @param {boolean} [recursive=true] When false, only the immediate children of `path` are returned.
     */
    getFiles: (path, recursive = true) =>
        getJson(`/api/library/files?directoryPath=${encodeURIComponent(path)}&recursive=${recursive}`),

    /** Enqueues every video file under `directoryPath` (optionally recursive) with `options`. */
    processDirectory: (directoryPath, recursive, options) =>
        postJson('/api/library/process-directory', { directoryPath, recursive, options }),

    /** Enqueues a single video file at `filePath` with the given encoder `options`. */
    processFile: (filePath, options) =>
        postJson('/api/library/process-file', { filePath, options }),

    /**
     * Dry-run preview: returns per-file decisions for `directoryPath` under `options`
     * without writing to the DB or queueing any work. Resolves `{ success, results }`
     * where each result has `decision`, `reason`, codec/bitrate metadata, and an
     * `encodeTargetKbps` for Queue/Shrink/Copy rows. Pass an `AbortSignal` to cancel
     * a long-running analysis (the server honors it via `HttpContext.RequestAborted`).
     */
    analyzeDirectory: (directoryPath, recursive, options, signal) =>
        postJson('/api/library/analyze-directory', { directoryPath, recursive, options }, signal),
};


// ---------------------------------------------------------------------------
// Auto-scan
// ---------------------------------------------------------------------------

/**
 * Watched-directory auto-scan configuration and triggers.
 */
export const autoScanApi = {
    /** Fetches the auto-scan config (directories, interval, last-scan stats). */
    getConfig:    ()                => getJson('/api/auto-scan/config'),

    /** Enables/disables the scheduled auto-scan. */
    setEnabled:   (enabled)         => postJson('/api/auto-scan/enabled',  { enabled }),

    /** Updates the scan interval in minutes. */
    setInterval:  (intervalMinutes) => postJson('/api/auto-scan/interval', { intervalMinutes }),

    /** Adds `path` to the list of watched directories. */
    addDir:       (path)            => postJson('/api/auto-scan/directories',   { path }),

    /** Removes `path` from the list of watched directories. */
    removeDir:    (path)            => deleteJson('/api/auto-scan/directories', { path }),

    /** Fires an immediate scan (independent of the schedule). */
    trigger:      ()                => postJson('/api/auto-scan/trigger'),

    /** Clears persisted "already-processed" history so every file re-qualifies. */
    clearHistory: ()                => postJson('/api/auto-scan/clear-history'),

    /** Fetches the active exclusion-rules object (filename patterns, resolutions, min size). */
    getExclusions:  ()      => getJson('/api/auto-scan/exclusions'),

    /** Persists a new exclusion-rules object. */
    saveExclusions: (rules) => postJson('/api/auto-scan/exclusions', rules),
};


// ---------------------------------------------------------------------------
// Notifications
// ---------------------------------------------------------------------------

/**
 * User-configured webhook/email destinations plus test dispatch.
 */
export const notificationsApi = {
    /** Fetches the full notification config (destinations + event toggles). */
    getConfig:  ()       => getJson('/api/notifications/config'),

    /** Persists the full notification config. */
    saveConfig: (config) => postJson('/api/notifications/config', config),

    /** Sends a test payload to a single destination object. */
    test:       (dest)   => postJson('/api/notifications/test', dest),
};


// ---------------------------------------------------------------------------
// Media-server / downloader integrations
// ---------------------------------------------------------------------------

/**
 * Plex / Jellyfin / Sonarr / Radarr integration config and test-connection.
 */
export const integrationsApi = {
    /** Fetches the full integrations config (all four providers). */
    getConfig:    ()                 => getJson('/api/integrations/config'),

    /** Persists the full integrations config. */
    saveConfig:   (config)           => postJson('/api/integrations/config', config),

    /** Verifies a Plex base URL + auth token reach the server. */
    testPlex:     (baseUrl, token)   => postJson('/api/integrations/test/plex',     { baseUrl, token }),

    /** Verifies a Jellyfin base URL + auth token reach the server. */
    testJellyfin: (baseUrl, token)   => postJson('/api/integrations/test/jellyfin', { baseUrl, token }),

    /** Verifies a Sonarr base URL + API key reach the server. */
    testSonarr:   (baseUrl, apiKey)  => postJson('/api/integrations/test/sonarr',   { baseUrl, apiKey }),

    /** Verifies a Radarr base URL + API key reach the server. */
    testRadarr:   (baseUrl, apiKey)  => postJson('/api/integrations/test/radarr',   { baseUrl, apiKey }),

    /** Verifies a TVDB API key (and optional PIN) can authenticate. */
    testTvdb:     (apiKey, pin)      => postJson('/api/integrations/test/tvdb',     { apiKey, pin }),

    /** Verifies a TMDb API key reaches the service. */
    testTmdb:     (apiKey)           => postJson('/api/integrations/test/tmdb',     { apiKey }),
};


// ---------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------

/**
 * Local user-authentication config plus log-out.
 */
export const authApi = {
    /** Fetches the current auth state (enabled, username, whether a password is set). */
    getConfig: () => getJson('/api/auth/config'),

    /**
     * Saves auth settings.
     *
     * @param {boolean}     enabled
     * @param {string}      username
     * @param {string|null} password Passing null keeps the existing password unchanged.
     */
    save: (enabled, username, password) => postJson('/api/auth/config', { enabled, username, password }),

    /** Signs the current user out; navigation is typically handled by the caller. */
    logout: () => fetch('/Auth/Logout', { method: 'POST' }),
};


// ---------------------------------------------------------------------------
// Cluster administration
// ---------------------------------------------------------------------------

/**
 * Cluster mode config, live status, per-node settings, and per-folder overrides.
 */
export const clusterApi = {
    /** Fetches the cluster config (role, secret, manual nodes, timings). */
    getConfig:     ()       => getJson('/api/cluster-admin/config'),

    /** Persists the cluster config. */
    saveConfig:    (config) => postJson('/api/cluster-admin/config', config),

    /** Fetches live cluster status (this node + connected workers). */
    getStatus:     ()       => getJson('/api/cluster-admin/status'),

    /** Lists connected worker nodes (subset of `getStatus`). */
    getWorkers:    ()       => getJson('/api/cluster-admin/workers'),

    /** Pauses or resumes a specific worker node by id. */
    setNodePaused: (nodeId, paused) =>
        postJson('/api/cluster-admin/node-paused', { nodeId, paused }),

    /** Pauses or resumes local encoding on the master node itself. */
    setLocalEncodingPaused: (paused) =>
        postJson('/api/cluster-admin/local-encoding-paused', { paused }),

    /** Fetches per-node encoding-override settings. */
    getNodeSettings:    ()         => getJson('/api/cluster-admin/node-settings'),

    /** Persists per-node encoding-override settings for one node. */
    saveNodeSettings:   (settings) => postJson('/api/cluster-admin/node-settings', settings),

    /** Removes all override settings for a node, returning it to defaults. */
    deleteNodeSettings: (nodeId)   => deleteJson('/api/cluster-admin/node-settings', { nodeId }),

    /** Persists per-folder encoding overrides (or clears them when `encodingOverrides` is null). */
    saveFolderSettings: (path, encodingOverrides) =>
        postJson('/api/cluster-admin/folder-settings', { path, encodingOverrides }),
};


// ---------------------------------------------------------------------------
// Dashboard
// ---------------------------------------------------------------------------

/**
 * Encode-history dashboard maintenance. The read paths each module hits
 * directly (the dashboard page issues its own per-chart fetches); the only
 * mutating action exposed here is the "wipe the ledger" escape hatch from
 * the Advanced settings panel.
 */
export const dashboardApi = {
    /**
     * Wipes every row in the encode-history ledger. On a worker, the request
     * is proxied to the master (workers don't own the ledger). The server
     * broadcasts a SignalR event so connected dashboards refresh.
     */
    clearHistory: () => deleteJson('/api/dashboard/history'),
};


// ---------------------------------------------------------------------------
// App lifecycle
// ---------------------------------------------------------------------------

/**
 * Process-level operations (health probe, restart).
 */
export const appApi = {
    /** Liveness probe — resolves once the backend is up. */
    health:  () => getJson('/api/health'),

    /** Triggers a backend restart; see `electron-app/main.js` for the relaunch flow. */
    restart: () => postJson('/api/restart'),
};
