/**
 * Main controller for the Snacks transcoding UI.
 * Manages the SignalR connection, work item queue rendering, encoder settings,
 * auto-scan configuration, and cluster panel for a single-page dashboard.
 */
class TranscodingManager {
    /**
     * Initializes state, starts SignalR, registers event handlers, and loads initial data.
     * Re-syncs queue state whenever the page becomes visible (handles iOS Safari background suspension).
     */
    constructor() {
        this.connection = null;
        this.workItems = new Map();
        this.logs = new Map();
        this.selectedFiles = new Set();
        this.currentDirectory = null;
        this.rootDirectory = null;
        this.queuePage = 0;
        this.queuePageSize = 5;
        this.queueTotal = 0;
        this.queueFilter = null; // null = all, 'Pending', 'Completed', 'Failed'
        this.isPaused = false;
        this.clusterEnabled = false;
        this.clusterRole = 'standalone';
        this.workers = new Map();
        this._lastAutoScanConfig = null;

        this.initializeSignalR();
        this.initializeEventHandlers();
        this.restoreSettings();
        this.loadAutoScanConfig();
        this.loadWorkItems();
        this.loadPauseState();
        this.loadClusterConfig();

        // iOS Safari suspends WebSockets when the tab is backgrounded.
        // Re-check connection when the page becomes visible again.
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') {
                if (this.connection?.state !== 'Connected') {
                    this.initializeSignalR();
                } else {
                    this.updateConnectionStatus(true);
                }
                this.loadWorkItems();
            }
        });
    }

    /**
     * Connects (or reconnects) to the SignalR hub at `/transcodingHub`.
     * Registers all hub event handlers and sets up a periodic connection status check.
     * Guards against concurrent initialization with a flag to prevent duplicate connections.
     */
    async initializeSignalR() {
        // Prevent concurrent initialization
        if (this._signalingInit) return;
        this._signalingInit = true;

        try {
            // Stop any existing connection before creating a new one to prevent duplicate handlers
            if (this.connection) {
                this._intentionalStop = true;
                try { await this.connection.stop(); } catch { /* already stopped */ }
                this._intentionalStop = false;
            }

            this.connection = new signalR.HubConnectionBuilder()
                .withUrl("/transcodingHub")
                .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                .build();

            // Handle work item events
            this.connection.on("WorkItemAdded", (workItem) => {
                this.addWorkItem(workItem);
            });

            this.connection.on("WorkItemUpdated", (workItem) => {
                console.log('WorkItemUpdated', workItem.id, 'status:', workItem.status, 'progress:', workItem.progress, workItem.errorMessage ? 'error: ' + workItem.errorMessage : '');
                this.updateWorkItem(workItem);
            });

            this.connection.on("TranscodingLog", (workItemId, message) => {
                this.addLogMessage(workItemId, message);
            });

            this.connection.on("AutoScanCompleted", (newFiles, total) => {
                showToast(`Auto-scan complete: ${newFiles} new file(s) found`, newFiles > 0 ? 'success' : 'info');
                this.loadAutoScanConfig(false);
            });

            this.connection.on("HistoryCleared", () => {
                this.workItems.clear();
                this.logs.clear();
                this.loadWorkItems();
            });

            // Cluster events
            this.connection.on("WorkerConnected", (node) => {
                if (!this.workers.has(node.nodeId)) {
                    showToast(`Node "${node.hostname}" connected`, 'success');
                }
                this.workers.set(node.nodeId, node);
                this.renderClusterPanel();
            });

            this.connection.on("HardwareDetected", () => {
                this.loadWorkers().then(() => this.renderClusterPanel());
            });

            this.connection.on("WorkerDisconnected", (nodeId) => {
                const node = this.workers.get(nodeId);
                this.workers.delete(nodeId);
                this.renderClusterPanel();
                if (node) showToast(`Node "${node.hostname}" disconnected`, 'warning');
            });

            this.connection.on("WorkerUpdated", (node) => {
                this.workers.set(node.nodeId, node);
                this.renderClusterPanel();
            });

            this.connection.on("ClusterConfigChanged", (config) => {
                this.clusterEnabled = config.enabled;
                this.clusterRole = config.role;
                this.renderClusterPanel();
                this.updateNodeBanner();
            });

            // Node: master paused/resumed us
            this.connection.on("ClusterNodePaused", (paused) => {
                this.isPaused = paused;
                this.updatePauseButton();
            });

            // Register lifecycle handlers BEFORE start so they catch all events
            this.connection.onreconnected(async () => {
                console.log("SignalR Reconnected — resyncing state");
                this.updateConnectionStatus(true);
                this.loadWorkItems();
                this.loadClusterConfig();
            });

            this.connection.onreconnecting(() => {
                console.log("SignalR Reconnecting...");
                this.updateConnectionStatus(false);
            });

            this.connection.onclose(async () => {
                if (this._intentionalStop) return;
                console.log("SignalR Disconnected. Attempting to reconnect...");
                this.updateConnectionStatus(false);
                setTimeout(() => this.initializeSignalR(), 5000);
            });

            await this.connection.start();
            console.log("SignalR Connected");
            this.updateConnectionStatus(true);
            this.loadWorkItems();
            this.loadClusterConfig();
        } catch (err) {
            console.error("SignalR Connection Error: ", err);
            this.updateConnectionStatus(false);
            setTimeout(() => this.initializeSignalR(), 5000);
        } finally {
            this._signalingInit = false;
        }

        // Periodically sync the connection status indicator with actual state.
        // iOS Safari can miss the initial status update due to paint timing.
        if (!this._statusInterval) {
            this._statusInterval = setInterval(() => {
                const connected = this.connection?.state === 'Connected';
                this.updateConnectionStatus(connected);
            }, 3000);
        }
    }

    /**
     * Binds all static DOM event listeners for modals, settings form, pagination,
     * filter tabs, and work item action buttons using event delegation where possible.
     */
    initializeEventHandlers() {
        // Library modal
        const libraryEl = document.getElementById('libraryModal');
        document.getElementById('openLibraryBtn')?.addEventListener('click', () => {
            libraryEl.classList.add('open');
            this.loadDirectories();
        });
        libraryEl.querySelector('.snacks-modal-wrapper').addEventListener('mousedown', function(e) {
            if (e.target === this) libraryEl.classList.remove('open');
        });

        document.getElementById('selectAllFiles').addEventListener('click', () => {
            this.selectAllFiles();
        });

        document.getElementById('processSelectedFiles').addEventListener('click', () => {
            this.processSelectedFiles();
        });

        // Settings modal — open/close via .open class
        document.getElementById('openSettingsBtn')?.addEventListener('click', () => {
            document.getElementById('settingsModal').classList.add('open');
            this.loadAutoScanConfig();
            this.loadClusterConfig();
        });

        // Close settings also closes override dialog
        const settingsEl = document.getElementById('settingsModal');
        new MutationObserver(() => {
            if (!settingsEl.classList.contains('open')) this.closeOverrideDialog();
        }).observe(settingsEl, { attributes: true, attributeFilter: ['class'] });

        // Escape key closes topmost open modal
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                const override = document.getElementById('overrideDialog');
                if (override.classList.contains('open')) { override.classList.remove('open'); return; }
                if (settingsEl.classList.contains('open')) { settingsEl.classList.remove('open'); return; }
                if (libraryEl.classList.contains('open')) { libraryEl.classList.remove('open'); return; }
            }
        });

        // Click backdrop to close (not the dialog itself)
        settingsEl.querySelector('.snacks-modal-wrapper').addEventListener('mousedown', function(e) {
            if (e.target === this) settingsEl.classList.remove('open');
        });
        document.getElementById('overrideDialog').querySelector('.snacks-modal-wrapper').addEventListener('mousedown', function(e) {
            if (e.target === this) document.getElementById('overrideDialog').classList.remove('open');
        });

        // Cluster event handlers
        this.initializeClusterEventHandlers();

        document.getElementById('processDirectory').addEventListener('click', () => {
            this.processCurrentDirectory();
        });

        // Save encoder settings on change (exclude auto-scan and override dialog inputs)
        const isMainSettingsField = (e) =>
            !e.target.id.startsWith('autoScan') && !e.target.closest('#overrideDialog');
        settingsEl.addEventListener('change', (e) => {
            if (isMainSettingsField(e)) this.getEncoderOptions('settings');
        });
        settingsEl.addEventListener('input', (e) => {
            if (isMainSettingsField(e)) this.getEncoderOptions('settings');
        });

        document.getElementById('pauseResumeBtn')?.addEventListener('click', () => this.togglePause());

        document.getElementById('addAutoScanDir')?.addEventListener('click', () => this.addAutoScanDirectory());
        document.getElementById('triggerAutoScan')?.addEventListener('click', () => this.triggerAutoScan());
        document.getElementById('clearScanHistory')?.addEventListener('click', () => this.clearAutoScanHistory());
        document.getElementById('autoScanEnabled')?.addEventListener('change', (e) => this.setAutoScanEnabled(e.target.checked));
        const intervalInput = document.getElementById('autoScanInterval');
        if (intervalInput) {
            let intervalTimer = null;
            intervalInput.addEventListener('input', (e) => {
                clearTimeout(intervalTimer);
                intervalTimer = setTimeout(() => {
                    const val = parseInt(e.target.value);
                    if (isNaN(val) || val < 1 || val > 1440) {
                        showToast('Interval must be between 1 and 1440 minutes', 'danger');
                        intervalInput.value = Math.max(1, Math.min(1440, val || 1));
                        this.setAutoScanInterval(parseInt(intervalInput.value));
                    } else {
                        this.setAutoScanInterval(val);
                    }
                }, 1000);
            });
        }

        // --- Delegated event handlers (bound once, survive DOM rebuilds) ---

        // Work item action buttons (cancel/log) in both containers
        for (const containerId of ['processingContainer', 'workItemsContainer']) {
            document.getElementById(containerId)?.addEventListener('click', (e) => {
                const btn = e.target.closest('[data-action]');
                if (!btn) return;
                const itemEl = btn.closest('.work-item');
                const itemId = itemEl?.id?.replace('work-item-', '');
                if (!itemId) return;
                if (btn.dataset.action === 'remove') this.showStopCancelDialog(itemId);
                if (btn.dataset.action === 'log') this.showLog(itemId);
            });
        }

        // Pagination buttons
        document.getElementById('queuePagination')?.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-page-action]');
            if (!btn || btn.disabled) return;
            const totalPages = Math.ceil(this.queueTotal / this.queuePageSize);
            switch (btn.dataset.pageAction) {
                case 'first': if (this.queuePage > 0) { this.queuePage = 0; this.loadWorkItems(); } break;
                case 'prev': if (this.queuePage > 0) { this.queuePage--; this.loadWorkItems(); } break;
                case 'next': if (this.queuePage < totalPages - 1) { this.queuePage++; this.loadWorkItems(); } break;
                case 'last': if (this.queuePage < totalPages - 1) { this.queuePage = totalPages - 1; this.loadWorkItems(); } break;
            }
        });

        // Filter tab buttons
        document.getElementById('queueFilterTabs')?.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-filter]');
            if (!btn) return;
            const val = btn.dataset.filter;
            this.queueFilter = val === '' ? null : val;
            this.queuePage = 0;
            this.loadWorkItems();
        });
    }

    /**
     * Updates the connection status dot and label in the navbar.
     * @param {boolean} connected - True when the SignalR hub is connected.
     */
    updateConnectionStatus(connected) {
        const dot = document.getElementById('connectionDot');
        const text = document.getElementById('connectionText');
        if (dot && text) {
            if (connected) {
                dot.classList.remove('text-danger', 'text-warning');
                dot.classList.add('text-success');
                text.textContent = 'Connected';
            } else {
                dot.classList.remove('text-success', 'text-warning');
                dot.classList.add('text-danger');
                text.textContent = 'Reconnecting...';
            }
        }
    }

    /**
     * Fetches the top-level video directories from the server and renders them in the library modal.
     * Each directory entry includes a "Watch" button to add it to auto-scan.
     */
    async loadDirectories() {
        const container = document.getElementById('directoryList');
        const fileList = document.getElementById('fileList');
        if (fileList) fileList.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-folder-open fa-2x mb-2"></i><br>Select a directory to view files</div>';
        this.currentDirectory = null;
        this.selectedFiles.clear();
        this.updateProcessButton();

        try {
            container.innerHTML = '<div class="text-center"><div class="spinner-border" role="status"><span class="visually-hidden">Loading...</span></div></div>';
            
            const response = await fetch('/Home/GetAvailableDirectories');
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }
            
            const data = await response.json();
            this.rootDirectory = data.rootPath;

            if (data.directories.length === 0) {
                container.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-folder-open fa-2x mb-2"></i><br>No video directories found<br><small>Mount your video library to /app/work/uploads</small></div>';
                return;
            }
            
            const directoriesHtml = data.directories.map(dir => `
                <div class="directory-item p-2 border-bottom d-flex justify-content-between align-items-center">
                    <div class="flex-grow-1" data-path="${escapeHtml(dir.path)}" data-count="${dir.videoCount}" style="cursor: pointer;">
                        <i class="fas ${dir.videoCount === 0 ? 'fa-hdd' : 'fa-folder'} text-warning me-2"></i>
                        <span>${escapeHtml(dir.name)}</span>
                        ${dir.videoCount > 0 ? `<small class="text-muted ms-2">${dir.videoCount} videos</small>` : ''}
                    </div>
                    <button class="btn btn-sm btn-link p-0 ms-2 watch-dir-btn" data-path="${escapeHtml(dir.path)}" title="Watch (Auto-Scan)">
                        <i class="fas fa-eye" style="color: var(--primary); opacity: 0.6;"></i>
                    </button>
                </div>
            `).join('');

            container.innerHTML = directoriesHtml;

            // Click directory name to navigate into it
            container.querySelectorAll('.directory-item .flex-grow-1[data-path]').forEach(item => {
                item.addEventListener('click', () => {
                    this.loadSubdirectories(item.getAttribute('data-path'));
                });
            });

            // Watch button for each directory
            container.querySelectorAll('.watch-dir-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this.watchDirectory(btn.getAttribute('data-path'));
                });
            });
            
        } catch (error) {
            container.innerHTML = `<div class="alert alert-danger">Error loading directories: ${escapeHtml(error.message)}</div>`;
        }
    }

    /**
     * Navigates into a directory, showing its subdirectories and process/watch action items.
     * Also triggers a shallow file list load for the selected directory.
     * @param {string} directoryPath - Absolute path of the directory to navigate into.
     */
    async loadSubdirectories(directoryPath) {
        // Normalize bare Windows drive letters (e.g. "D:") to include trailing backslash
        // Without this, Windows resolves "D:" to the process CWD on that drive
        if (/^[A-Za-z]:$/.test(directoryPath)) {
            directoryPath += '\\';
        }
        this.currentDirectory = directoryPath;
        const container = document.getElementById('directoryList');

        try {
            container.innerHTML = '<div class="text-center"><div class="spinner-border" role="status"></div></div>';

            const response = await fetch(`/Home/GetSubdirectories?directoryPath=${encodeURIComponent(directoryPath)}`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const data = await response.json();

            // Back button + subdirectory list
            let html = `<div class="directory-item p-2 border-bottom" id="dirBack" style="cursor:pointer;">
                <i class="fas fa-arrow-left text-muted me-2"></i><span class="text-muted">Back</span>
            </div>`;

            html += `<div class="directory-item p-2 border-bottom bg-success bg-opacity-10" id="dirProcess" style="cursor:pointer;">
                <i class="fas fa-play text-success me-2"></i><span>Process This Folder</span>
            </div>`;

            html += `<div class="directory-item p-2 border-bottom bg-success bg-opacity-10" id="dirProcessRecursive" style="cursor:pointer;">
                <i class="fas fa-layer-group text-success me-2"></i><span>Process Folder + Subfolders</span>
            </div>`;

            html += `<div class="directory-item p-2 border-bottom" id="dirWatch" style="cursor:pointer; opacity: 0.8;">
                <i class="fas fa-eye me-2" style="color: var(--primary);"></i><span>Watch This Folder (Auto-Scan)</span>
            </div>`;

            if (data.directories.length === 0) {
                html += '<div class="text-muted text-center py-3"><small>No subdirectories</small></div>';
            } else {
                html += data.directories.map(dir => `
                    <div class="directory-item p-2 border-bottom" data-path="${escapeHtml(dir.path)}" style="cursor:pointer;">
                        <i class="fas fa-folder text-warning me-2"></i><span>${escapeHtml(dir.name)}</span>
                    </div>
                `).join('');
            }

            container.innerHTML = html;

            // Back button — go to parent or back to top-level directory list
            document.getElementById('dirBack').addEventListener('click', () => {
                if (data.parentPath) {
                    this.loadSubdirectories(data.parentPath);
                } else {
                    this.loadDirectories();
                }
            });

            // Process this folder (non-recursive)
            document.getElementById('dirProcess').addEventListener('click', () => {
                this.processCurrentDirectory(false);
            });

            // Process folder + subfolders (recursive)
            document.getElementById('dirProcessRecursive').addEventListener('click', () => {
                this.processCurrentDirectory(true);
            });

            // Watch this folder (add to auto-scan)
            document.getElementById('dirWatch').addEventListener('click', async () => {
                try {
                    const response = await fetch('/Home/AddAutoScanDirectory', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ path: directoryPath })
                    });
                    if (!response.ok) throw new Error(await response.text());
                    showToast(`Added "${directoryPath.split(/[/\\]/).pop() || directoryPath}" to auto-scan`, 'success');
                    this.loadAutoScanConfig(false);
                } catch (error) {
                    showToast('Error adding directory: ' + error.message, 'danger');
                }
            });

            // Subdirectory click handlers
            container.querySelectorAll('.directory-item[data-path]').forEach(item => {
                item.addEventListener('click', () => {
                    this.loadSubdirectories(item.getAttribute('data-path'));
                });
            });

            // Load video files for just this directory (non-recursive) into the file list panel
            this.loadDirectoryFilesShallow(directoryPath);

        } catch (error) {
            container.innerHTML = `<div class="alert alert-danger">Error: ${escapeHtml(error.message)}</div>`;
        }
    }

    /**
     * Loads video files directly inside a directory (non-recursive) into the file list panel.
     * Clears the current selection and renders checkboxes for individual file selection.
     * @param {string} directoryPath - Absolute path of the directory to list.
     */
    async loadDirectoryFilesShallow(directoryPath) {
        this.currentDirectory = directoryPath;
        this.selectedFiles.clear();
        const container = document.getElementById('fileList');

        try {
            container.innerHTML = '<div class="text-center py-4"><div class="spinner-border" role="status"></div></div>';

            const response = await fetch(`/Home/GetDirectoryFiles?directoryPath=${encodeURIComponent(directoryPath)}&recursive=false`);
            if (!response.ok) throw new Error(`HTTP ${response.status}`);

            const data = await response.json();

            if (data.files.length === 0) {
                container.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-file-video fa-2x mb-2"></i><br>No video files in this folder</div>';
                return;
            }

            const filesHtml = data.files.map(file => `
                <div class="file-item p-2 border-bottom" data-path="${escapeHtml(file.path)}">
                    <div class="form-check d-flex align-items-center">
                        <input class="form-check-input me-3" type="checkbox" value="${escapeHtml(file.path)}" id="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                        <label class="form-check-label w-100" for="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                            <div class="d-flex justify-content-between align-items-center">
                                <div>
                                    <i class="fas fa-file-video text-primary me-2"></i>
                                    <strong>${escapeHtml(file.name)}</strong>
                                </div>
                                <small class="text-muted">${this.formatFileSize(file.size)}</small>
                            </div>
                        </label>
                    </div>
                </div>
            `).join('');

            container.innerHTML = filesHtml;

            container.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
                checkbox.addEventListener('change', () => {
                    if (checkbox.checked) this.selectedFiles.add(checkbox.value);
                    else this.selectedFiles.delete(checkbox.value);
                    this.updateProcessButton();
                });
            });

        } catch (error) {
            container.innerHTML = `<div class="alert alert-danger">Error: ${escapeHtml(error.message)}</div>`;
        }
    }

    /**
     * Renders a summary view for a directory in the file list panel with "Process All" and "Browse" buttons.
     * @param {string} directoryPath - Absolute path of the selected directory.
     * @param {number} videoCount - Number of video files found in the directory.
     */
    showDirectorySummary(directoryPath, videoCount) {
        this.currentDirectory = directoryPath;
        this.selectedFiles.clear();
        const container = document.getElementById('fileList');
        const dirName = directoryPath.split(/[/\\]/).pop() || 'Root';

        container.innerHTML = `
            <div class="text-center py-5">
                <i class="fas fa-folder-open fa-3x text-warning mb-3"></i>
                <h5>${escapeHtml(dirName)}</h5>
                <p class="text-muted mb-4">${videoCount} video files</p>
                <button class="btn btn-primary btn-lg mb-3" onclick="transcodingManager.processCurrentDirectory()">
                    <i class="fas fa-play me-2"></i>Process All ${videoCount} Files
                </button>
                <br>
                <button class="btn btn-sm btn-outline-secondary" onclick="transcodingManager.loadDirectoryFiles('${escapeHtml(directoryPath).replace(/'/g, "\\'")}')">
                    <i class="fas fa-list me-1"></i>Browse Individual Files
                </button>
            </div>
        `;

        this.updateProcessButton();
    }

    /**
     * Loads all video files in a directory (recursively by default) into the file list panel.
     * @param {string} directoryPath - Absolute path of the directory to list.
     */
    async loadDirectoryFiles(directoryPath) {
        this.currentDirectory = directoryPath;
        this.selectedFiles.clear();
        const container = document.getElementById('fileList');

        try {
            container.innerHTML = '<div class="text-center py-4"><div class="spinner-border" role="status"><span class="visually-hidden">Loading...</span></div><br><small class="text-muted mt-2">Loading files...</small></div>';

            const response = await fetch(`/Home/GetDirectoryFiles?directoryPath=${encodeURIComponent(directoryPath)}`);
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();

            if (data.files.length === 0) {
                container.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-file-video fa-2x mb-2"></i><br>No video files found</div>';
                return;
            }

            const filesHtml = data.files.map(file => `
                <div class="file-item p-2 border-bottom" data-path="${escapeHtml(file.path)}">
                    <div class="form-check d-flex align-items-center">
                        <input class="form-check-input me-3" type="checkbox" value="${escapeHtml(file.path)}" id="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                        <div class="flex-grow-1">
                            <label class="form-check-label w-100" for="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                                <div class="d-flex justify-content-between align-items-center">
                                    <div>
                                        <i class="fas fa-file-video text-primary me-2"></i>
                                        <strong>${escapeHtml(file.name)}</strong><br>
                                        <small class="text-muted">${escapeHtml(file.relativePath)}</small>
                                    </div>
                                    <div class="text-end">
                                        <small class="text-muted">
                                            ${this.formatFileSize(file.size)}<br>
                                            ${new Date(file.modified).toLocaleDateString()}
                                        </small>
                                    </div>
                                </div>
                            </label>
                        </div>
                    </div>
                </div>
            `).join('');

            container.innerHTML = filesHtml;

            // Add change handlers for checkboxes
            container.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
                checkbox.addEventListener('change', () => {
                    if (checkbox.checked) {
                        this.selectedFiles.add(checkbox.value);
                    } else {
                        this.selectedFiles.delete(checkbox.value);
                    }
                    this.updateProcessButton();
                });
            });

        } catch (error) {
            container.innerHTML = `<div class="alert alert-danger">Error loading files: ${escapeHtml(error.message)}</div>`;
        }
    }

    /**
     * Toggles all file checkboxes in the file list between selected and deselected,
     * and updates the "Select All / Deselect All" button label accordingly.
     */
    selectAllFiles() {
        const checkboxes = document.querySelectorAll('#fileList input[type="checkbox"]');
        const allSelected = Array.from(checkboxes).every(cb => cb.checked);
        
        checkboxes.forEach(checkbox => {
            checkbox.checked = !allSelected;
            if (checkbox.checked) {
                this.selectedFiles.add(checkbox.value);
            } else {
                this.selectedFiles.delete(checkbox.value);
            }
        });
        
        this.updateProcessButton();
        
        // Update button text
        const button = document.getElementById('selectAllFiles');
        button.innerHTML = allSelected ? 
            '<i class="fas fa-check-square me-1"></i> Select All' : 
            '<i class="fas fa-square me-1"></i> Deselect All';
    }

    /** Updates the "Process Selected" button's disabled state and file count label. */
    updateProcessButton() {
        const button = document.getElementById('processSelectedFiles');
        button.disabled = this.selectedFiles.size === 0;
        button.innerHTML = `<i class="fas fa-play me-1"></i> Process Selected (${this.selectedFiles.size})`;
    }

    /** Closes the library modal. */
    closeLibraryModal() {
        document.getElementById('libraryModal').classList.remove('open');
    }

    /**
     * Submits each individually selected file to the transcoding queue.
     * Closes the modal immediately for perceived performance and reports final count.
     */
    async processSelectedFiles() {
        if (this.selectedFiles.size === 0) {
            showToast('No files selected', 'warning');
            return;
        }

        const options = this.getEncoderOptions('settings');
        const fileCount = this.selectedFiles.size;
        const filePaths = [...this.selectedFiles];

        // Close modal immediately and show feedback
        this.closeLibraryModal();
        this.selectedFiles.clear();
        showToast(`Scanning ${fileCount} file(s)...`, 'info');

        // Submit files in background
        let successCount = 0;
        for (const filePath of filePaths) {
            try {
                const response = await fetch('/Home/ProcessSingleFile', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ filePath, options })
                });
                if (response.ok) successCount++;
                else console.error(`Failed to process ${filePath}:`, await response.text());
            } catch (error) {
                console.error(`Failed to process ${filePath}:`, error);
            }
        }

        showToast(`Added ${successCount} file(s) to transcoding queue`, 'success');
    }

    /**
     * Submits the current directory for batch transcoding.
     * @param {boolean} [recursive=true] - When true, processes subdirectories as well.
     */
    async processCurrentDirectory(recursive = true) {
        // If no directory selected, process the entire root (all listed directories)
        const dirPath = this.currentDirectory || this.rootDirectory;
        if (!dirPath) {
            showToast('No directory available', 'warning');
            return;
        }

        const options = this.getEncoderOptions('settings');
        const dirName = this.currentDirectory
            ? (this.currentDirectory.split(/[/\\]/).pop() || this.currentDirectory)
            : 'all directories';

        // Close modal FIRST, then yield to let the browser process the close
        this.closeLibraryModal();

        // Yield to the browser so the modal animation actually runs before the fetch
        await new Promise(resolve => setTimeout(resolve, 100));

        showToast(`Scanning "${dirName}"${recursive ? ' (including subfolders)' : ''}...`, 'info');

        // Submit directory in background
        try {
            const response = await fetch('/Home/ProcessDirectory', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    directoryPath: dirPath,
                    recursive,
                    options
                })
            });

            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }

            const result = await response.json();
            showToast(result.message, 'success');
        } catch (error) {
            showToast('Error processing directory: ' + error.message, 'danger');
        }
    }

    /**
     * Reads encoder settings from the form inputs, persists them to the server, and returns the options object.
     * @param {string} [prefix=''] - ID prefix used to distinguish settings form inputs from other modal inputs.
     * @returns {object} The current encoder options as a plain object.
     */
    getEncoderOptions(prefix = '') {
        const codec = document.getElementById(`${prefix}Codec`).value;
        const hwAccel = document.getElementById(`${prefix}HardwareAcceleration`).value;
        const encoderMap = { 'h265': 'libx265', 'h264': 'libx264', 'av1': 'libsvtav1' };
        const options = {
            Format: document.getElementById(`${prefix}Format`).value,
            Codec: codec,
            Encoder: encoderMap[codec] || 'libx265',
            HardwareAcceleration: hwAccel,
            TargetBitrate: parseInt(document.getElementById(`${prefix}TargetBitrate`).value),
            TwoChannelAudio: document.getElementById(`${prefix}TwoChannelAudio`).checked,
            EnglishOnlyAudio: document.getElementById(`${prefix}EnglishOnlyAudio`).checked,
            EnglishOnlySubtitles: document.getElementById(`${prefix}EnglishOnlySubtitles`).checked,
            RemoveBlackBorders: document.getElementById(`${prefix}RemoveBlackBorders`).checked,
            DeleteOriginalFile: document.getElementById(`${prefix}DeleteOriginalFile`).checked,
            RetryOnFail: document.getElementById(`${prefix}RetryOnFail`).checked,
            StrictBitrate: document.getElementById(`${prefix}StrictBitrate`).checked,
            FourKBitrateMultiplier: parseInt(document.getElementById(`${prefix}FourKBitrateMultiplier`)?.value || '4'),
            Skip4K: document.getElementById(`${prefix}Skip4K`)?.checked || false,
            SkipPercentAboveTarget: Math.max(0, parseInt(document.getElementById(`${prefix}SkipPercentAboveTarget`)?.value || '20')),
            OutputDirectory: document.getElementById(`${prefix}OutputDirectory`)?.value || ''
        };
        this.saveSettingsToServer(options);
        return options;
    }

    /**
     * Persists encoder options to the server so they survive page reloads.
     * @param {object} options - Encoder options object to save.
     */
    async saveSettingsToServer(options) {
        try {
            await fetch('/Home/SaveSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(options)
            });
        } catch { }
    }

    /**
     * Fetches persisted encoder settings from the server and populates the form inputs.
     * @param {string} [prefix='settings'] - ID prefix matching {@link getEncoderOptions}.
     */
    async restoreSettings(prefix = 'settings') {
        try {
            const response = await fetch('/Home/GetSettings');
            if (!response.ok) return;
            const saved = await response.json();
            if (!saved || Object.keys(saved).length === 0) return;

            const set = (id, val) => {
                const el = document.getElementById(`${prefix}${id}`);
                if (!el || val === undefined) return;
                if (el.type === 'checkbox') el.checked = val;
                else el.value = val;
            };

            set('Format', saved.Format || saved.format);
            set('Codec', saved.Codec || saved.codec);
            set('HardwareAcceleration', saved.HardwareAcceleration || saved.hardwareAcceleration);
            set('TargetBitrate', saved.TargetBitrate || saved.targetBitrate);
            set('TwoChannelAudio', saved.TwoChannelAudio ?? saved.twoChannelAudio);
            set('EnglishOnlyAudio', saved.EnglishOnlyAudio ?? saved.englishOnlyAudio);
            set('EnglishOnlySubtitles', saved.EnglishOnlySubtitles ?? saved.englishOnlySubtitles);
            set('RemoveBlackBorders', saved.RemoveBlackBorders ?? saved.removeBlackBorders);
            set('DeleteOriginalFile', saved.DeleteOriginalFile ?? saved.deleteOriginalFile);
            set('RetryOnFail', saved.RetryOnFail ?? saved.retryOnFail);
            set('StrictBitrate', saved.StrictBitrate ?? saved.strictBitrate);
            set('FourKBitrateMultiplier', saved.FourKBitrateMultiplier || saved.fourKBitrateMultiplier);
            set('Skip4K', saved.Skip4K ?? saved.skip4K);
            set('SkipPercentAboveTarget', Math.max(0, saved.SkipPercentAboveTarget ?? saved.skipPercentAboveTarget ?? 20));
            set('OutputDirectory', saved.OutputDirectory || saved.outputDirectory || '');
        } catch { }
    }

    /**
     * Fetches the current page of queue items and active processing items from the server,
     * reconciles the DOM without a full rebuild to preserve CSS transitions, and updates
     * stat counters, pagination, and filter tabs.
     */
    async loadWorkItems() {
        try {
            const skip = this.queuePage * this.queuePageSize;
            const filterParam = this.queueFilter ? `&status=${this.queueFilter}` : '';
            const [statsResponse, itemsResponse] = await Promise.all([
                fetch('/Home/GetWorkStats'),
                fetch(`/Home/GetWorkItems?limit=${this.queuePageSize}&skip=${skip}${filterParam}`)
            ]);

            const stats = await statsResponse.json();
            const data = await itemsResponse.json();
            const queueItems = data.items;
            const processingItems = data.processing || [];
            this.queueTotal = data.total;

            this.workItems.clear();

            // --- Reconcile processing container ---
            const processingContainer = document.getElementById('processingContainer');
            const processingSection = document.getElementById('processingSection');
            const expectedProcessingIds = new Set(processingItems.map(i => `work-item-${i.id}`));

            // Remove processing DOM children no longer in server response
            for (const child of [...processingContainer.children]) {
                if (child.id && !expectedProcessingIds.has(child.id)) {
                    child.remove();
                }
            }

            // Render/update processing items (includes Uploading/Downloading)
            if (processingItems.length > 0) {
                processingSection.style.display = '';
                for (const item of processingItems) {
                    this.workItems.set(item.id, item);
                    this.renderWorkItem(item);
                }
            } else {
                processingSection.style.display = 'none';
            }

            // --- Reconcile queue container (no nuclear clear) ---
            const queueContainer = document.getElementById('workItemsContainer');
            const expectedQueueIds = new Set(queueItems.map(i => `work-item-${i.id}`));

            // Remove queue DOM children no longer in server response
            for (const child of [...queueContainer.children]) {
                if (child.id && !expectedQueueIds.has(child.id)) {
                    child.remove();
                } else if (!child.id) {
                    child.remove(); // Remove empty-state messages etc.
                }
            }

            // Render/update queue items
            for (const workItem of queueItems) {
                this.workItems.set(workItem.id, workItem);
                this.renderWorkItem(workItem);
            }

            // Reorder queue children to match server order
            for (let i = 0; i < queueItems.length; i++) {
                const el = document.getElementById(`work-item-${queueItems[i].id}`);
                if (el && el !== queueContainer.children[i]) {
                    queueContainer.insertBefore(el, queueContainer.children[i]);
                }
            }

            // Update stats (desktop + mobile)
            this.updateStatCounters(stats);

            if (queueItems.length === 0) {
                const msg = this.queueFilter
                    ? `No ${this.queueFilter.toLowerCase()} items`
                    : 'No files in queue';
                queueContainer.innerHTML = `<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>${msg}</div>`;
            }

            this.renderPagination();
            this.renderFilterTabs(stats);
            this.loadPauseState();
        } catch (error) {
            console.error('Error loading work items:', error);
            showToast('Error loading work items: ' + error.message, 'danger');
        }
    }

    /**
     * Sets the queue status filter and reloads the first page.
     * @param {string|null} filter - Status string to filter by ('Pending', 'Completed', 'Failed'), or null for all.
     */
    setFilter(filter) {
        this.queueFilter = filter;
        this.queuePage = 0;
        this.loadWorkItems();
    }

    /**
     * Renders the queue filter tab buttons with live counts from the stats object.
     * @param {object} stats - Work item stats with `pending`, `completed`, and `failed` counts.
     */
    renderFilterTabs(stats) {
        const container = document.getElementById('queueFilterTabs');
        if (!container) return;

        const filters = [
            { label: 'All', value: null, count: (stats.pending || 0) + (stats.completed || 0) + (stats.failed || 0) },
            { label: 'Pending', value: 'Pending', count: stats.pending || 0 },
            { label: 'Completed', value: 'Completed', count: stats.completed || 0 },
            { label: 'Failed', value: 'Failed', count: stats.failed || 0 },
        ];

        container.innerHTML = filters.map(f => {
            const active = this.queueFilter === f.value ? 'active' : '';
            return `<button class="btn btn-sm btn-outline-secondary ${active} queue-filter-btn" data-filter="${f.value ?? ''}">${f.label} <span class="badge bg-secondary ms-1">${f.count}</span></button>`;
        }).join('');
        // Event handlers are delegated on #queueFilterTabs — no per-button binding needed
    }

    /** Renders first/prev/next/last pagination buttons, hiding the container when only one page exists. */
    renderPagination() {
        let paginationEl = document.getElementById('queuePagination');
        if (!paginationEl) return;

        const totalPages = Math.ceil(this.queueTotal / this.queuePageSize);
        if (totalPages <= 1) {
            paginationEl.innerHTML = '';
            return;
        }

        const page = this.queuePage;
        paginationEl.innerHTML = `
            <nav class="d-flex justify-content-between align-items-center mt-3">
                <small class="text-muted">${this.queueTotal} items</small>
                <div class="btn-group btn-group-sm">
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} data-page-action="first" title="First page">
                        <i class="fas fa-angle-double-left"></i>
                    </button>
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} data-page-action="prev">
                        <i class="fas fa-chevron-left"></i>
                    </button>
                    <button class="btn btn-outline-secondary disabled">${page + 1} / ${totalPages}</button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} data-page-action="next">
                        <i class="fas fa-chevron-right"></i>
                    </button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} data-page-action="last" title="Last page">
                        <i class="fas fa-angle-double-right"></i>
                    </button>
                </div>
            </nav>
        `;
        // Event handlers are delegated on #queuePagination — no per-button binding needed
    }

    /**
     * Handles a `WorkItemAdded` SignalR event by storing the item and scheduling a queue refresh.
     * @param {object} workItem - The new work item received from the hub.
     */
    addWorkItem(workItem) {
        this.workItems.set(workItem.id, workItem);
        this.scheduleQueueRefresh();
    }

    /**
     * Handles a `WorkItemUpdated` SignalR event.
     * Renders processing items immediately; for upload/download transfer phases only a
     * short-lived fallback refresh is scheduled to avoid wiping ephemeral progress from
     * a full server reload.
     * @param {object} workItem - The updated work item received from the hub.
     */
    updateWorkItem(workItem) {
        this.workItems.set(workItem.id, workItem);
        const statusString = this.getStatusString(workItem.status);

        // Processing items get rendered immediately to the dedicated section
        if (['Processing', 'Uploading', 'Downloading'].includes(statusString)) {
            // Remove orphaned items for the same file (e.g., master restarted with a new job ID)
            if (workItem.fileName && (workItem.remoteJobPhase === 'Downloading' || workItem.remoteJobPhase === 'Uploading')) {
                for (const [existingId, existing] of this.workItems) {
                    if (existingId !== workItem.id &&
                        existing.fileName === workItem.fileName &&
                        this.getStatusString(existing.status) === 'Processing') {
                        this.workItems.delete(existingId);
                        document.getElementById(`work-item-${existingId}`)?.remove();
                    }
                }
            }

            this.renderWorkItem(workItem);
            // Don't trigger a full server refresh for processing items —
            // on nodes, the server-side queue doesn't include remote jobs,
            // so a refresh would wipe the item from the DOM.
            return;
        }

        this.scheduleQueueRefresh();
    }

    /**
     * Throttles full queue and stats refreshes to at most once per 2 seconds,
     * preventing server flooding during rapid SignalR event bursts.
     */
    scheduleQueueRefresh() {
        if (this._refreshTimer) return;
        this._refreshTimer = setTimeout(() => {
            this._refreshTimer = null;
            this.loadWorkItems();
        }, 2000);
    }

    /** Fetches the current queue pause state from the server and updates the pause button. */
    async loadPauseState() {
        try {
            const response = await fetch('/Home/GetPausedState');
            if (!response.ok) return;
            const data = await response.json();
            this.isPaused = data.paused;
            this.updatePauseButton();
        } catch (error) {
            console.error('Error loading pause state:', error);
        }
    }

    /** Toggles the queue between paused and running and updates the pause button state. */
    async togglePause() {
        try {
            const newState = !this.isPaused;
            const response = await fetch('/Home/SetPaused', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ paused: newState })
            });
            if (!response.ok) throw new Error('Failed to set pause state');
            const data = await response.json();
            this.isPaused = data.paused;
            this.updatePauseButton();
            showToast(this.isPaused ? 'Queue paused — current encode will finish' : 'Queue resumed', 'info');
        } catch (error) {
            console.error('Error toggling pause:', error);
            showToast('Error toggling pause: ' + error.message, 'danger');
        }
    }

    /** Syncs the pause/resume button icon and style with the current `isPaused` state. */
    updatePauseButton() {
        const btn = document.getElementById('pauseResumeBtn');
        const icon = document.getElementById('pauseResumeIcon');
        if (!btn || !icon) return;

        if (this.isPaused) {
            icon.className = 'fas fa-play';
            btn.className = 'btn btn-outline-warning btn-sm me-2';
            btn.title = 'Resume Queue';
        } else {
            icon.className = 'fas fa-pause';
            btn.className = 'btn btn-outline-secondary btn-sm me-2';
            btn.title = 'Pause Queue';
        }
    }

    /**
     * Converts a numeric or string work item status to its canonical string name.
     * @param {number|string} status - Numeric status code (0–5) or existing string.
     * @returns {string} Human-readable status string (e.g. 'Pending', 'Processing', 'Completed').
     */
    getStatusString(status) {
        const statusMap = {
            0: 'Pending',
            1: 'Processing',
            2: 'Completed',
            3: 'Failed',
            4: 'Cancelled',
            5: 'Stopped',
            6: 'Uploading',
            7: 'Downloading'
        };
        
        return typeof status === 'string' ? status : statusMap[status] || 'Unknown';
    }

    /**
     * Updates the stat counter badges in both the desktop navbar and the mobile summary bar.
     * @param {object} stats - Object with `pending`, `processing`, `completed`, `failed` counts.
     */
    updateStatCounters(stats) {
        const set = (id, val) => {
            const el = document.getElementById(id);
            if (el) el.textContent = val || 0;
        };
        set('pendingCount', stats.pending);
        set('processingCount', stats.processing);
        set('completedCount', stats.completed);
        set('failedCount', stats.failed);
        set('pendingCountMobile', stats.pending);
        set('processingCountMobile', stats.processing);
        set('completedCountMobile', stats.completed);
        set('failedCountMobile', stats.failed);
    }

    /**
     * Renders or updates a work item element in the correct container (processing or queue).
     * Creates a new DOM element on first render; uses differential DOM updates on subsequent calls
     * to preserve CSS transitions.
     * @param {object} workItem - The work item to render.
     */
    renderWorkItem(workItem) {
        const queueContainer = document.getElementById('workItemsContainer');
        const processingContainer = document.getElementById('processingContainer');
        const processingSection = document.getElementById('processingSection');
        const statusString = this.getStatusString(workItem.status);

        let element = document.getElementById(`work-item-${workItem.id}`);

        if (!element) {
            element = document.createElement('div');
            element.id = `work-item-${workItem.id}`;
            element.className = 'work-item new';
        }

        // Move element to the correct container based on status
        if (['Processing', 'Uploading', 'Downloading'].includes(statusString)) {
            if (element.parentNode !== processingContainer) {
                processingContainer.appendChild(element);
            }
            processingSection.style.display = '';
        } else {
            // If it was in the processing container, move it out
            if (element.parentNode === processingContainer) {
                element.remove();
                if (processingContainer.children.length === 0) {
                    processingSection.style.display = 'none';
                }
            }
            // Remove empty state message only when adding a real item to the queue
            const emptyMsg = queueContainer.querySelector('.text-muted.text-center');
            if (emptyMsg) emptyMsg.remove();

            // Add to queue container if not already there
            if (!element.parentNode || element.parentNode !== queueContainer) {
                queueContainer.appendChild(element);
            }
        }

        element.className = `work-item ${statusString.toLowerCase()}`;

        if (element.dataset.status) {
            // Element already exists — do a differential update instead of full innerHTML rebuild
            this.updateWorkItemDOM(element, workItem, statusString);
        } else {
            // First render — full HTML
            element.innerHTML = this.getWorkItemHtml({...workItem, status: statusString});
        }
        element.dataset.status = statusString;
        // Event listeners are handled by delegation on the container — no per-element binding needed
    }

    /**
     * Performs a surgical DOM update on an existing work item element, targeting only the
     * changed fields (status badge, progress bar, action buttons) instead of rebuilding innerHTML.
     * @param {HTMLElement} element - The existing work item DOM node.
     * @param {object} workItem - Updated work item data.
     * @param {string} statusString - Pre-resolved status string for the work item.
     */
    updateWorkItemDOM(element, workItem, statusString) {
        const prevStatus = element.dataset.status;

        // Update status badge
        const badge = element.querySelector('.status-badge');
        if (badge) {
            const newClass = `status-badge status-${statusString.toLowerCase()} flex-shrink-0`;
            if (badge.className !== newClass) badge.className = newClass;
            if (badge.textContent !== statusString) badge.textContent = statusString;
        }

        // Update progress bar (processing items only)
        const isTransfer = workItem.remoteJobPhase === 'Uploading' || workItem.remoteJobPhase === 'Downloading';
        const pct = isTransfer ? (workItem.transferProgress || 0) : (workItem.progress || 0);
        const progressContainer = element.querySelector('.progress');

        if (['Processing', 'Uploading', 'Downloading'].includes(statusString)) {
            if (progressContainer) {
                // Update existing progress bar width directly (preserves CSS transition)
                const bar = progressContainer.querySelector('.progress-bar');
                if (bar) bar.style.width = pct + '%';
                const label = progressContainer.querySelector('.progress-label');
                if (label) {
                    const labelText = `${pct}%`;
                    if (label.textContent !== labelText) label.textContent = labelText;
                }
            } else {
                // Status just changed to Processing — need to add progress bar.
                // Fall through to full rebuild for this transition.
                element.innerHTML = this.getWorkItemHtml({...workItem, status: statusString});
                return;
            }
        } else if (progressContainer) {
            progressContainer.remove();
        }

        // Update action buttons only if status changed
        if (prevStatus !== statusString) {
            const actionsDiv = element.querySelector('.ms-2.flex-shrink-0');
            if (actionsDiv) actionsDiv.innerHTML = this.getActionButtons({...workItem, status: statusString});
        }

        // Update error message
        const existingError = element.querySelector('.alert-danger');
        if (workItem.errorMessage) {
            if (existingError) {
                const newMsg = `<i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(workItem.errorMessage)}`;
                if (existingError.innerHTML !== newMsg) existingError.innerHTML = newMsg;
            } else {
                const errorDiv = document.createElement('div');
                errorDiv.className = 'alert alert-danger alert-sm mb-0 mt-2';
                errorDiv.innerHTML = `<i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(workItem.errorMessage)}`;
                const timeEl = element.querySelector('.text-muted.small.mt-1');
                if (timeEl) timeEl.before(errorDiv);
            }
        } else if (existingError) {
            existingError.remove();
        }

        // Update node badge
        const nodeBadge = element.querySelector('.badge.bg-secondary');
        if (this.clusterEnabled && workItem.assignedNodeName) {
            if (nodeBadge) {
                const expectedText = workItem.assignedNodeName;
                if (!nodeBadge.textContent.includes(expectedText)) {
                    nodeBadge.innerHTML = `<i class="fas fa-server me-1"></i>${escapeHtml(workItem.assignedNodeName)}`;
                }
            }
        } else if (nodeBadge && !workItem.assignedNodeName) {
            nodeBadge.remove();
        }

        // Update timestamp
        const timeEl = element.querySelector('.text-muted.small.mt-1');
        if (timeEl) {
            const newTime = `${new Date(workItem.createdAt).toLocaleString()}${workItem.completedAt ? ` &rarr; ${new Date(workItem.completedAt).toLocaleString()}` : ''}`;
            if (timeEl.innerHTML !== newTime) timeEl.innerHTML = newTime;
        }
    }

    /**
     * Returns the full HTML string for a work item card (used on first render only).
     * @param {object} workItem - Work item data with status already converted to a string.
     * @returns {string} HTML markup for the work item card.
     */
    getWorkItemHtml(workItem) {
        const statusClass = `status-${workItem.status.toLowerCase()}`;
        const progressPercent = workItem.progress || 0;
        
        const badges = [
            `<span class="status-badge ${statusClass} flex-shrink-0">${workItem.status}</span>`,
            this.clusterEnabled && workItem.assignedNodeName ? `<span class="badge bg-secondary flex-shrink-0" title="Processing on remote node"><i class="fas fa-server me-1"></i>${escapeHtml(workItem.assignedNodeName)}</span>` : '',
            ''
        ].filter(Boolean).join(' ');

        return `
            <div class="d-flex justify-content-between align-items-start mb-2">
                <div class="flex-grow-1" style="min-width:0;">
                    <div class="d-flex align-items-center flex-wrap gap-1 mb-1" style="min-width:0;">
                        <div class="d-flex align-items-center" style="min-width:0; max-width:100%;">
                            <i class="fas fa-file-video me-2 text-primary flex-shrink-0"></i>
                            <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(workItem.fileName)}</strong>
                        </div>
                        ${badges}
                    </div>
                    <small class="text-muted">
                        ${formatFileSize(workItem.size)} &bull; ${formatBitrate(workItem.bitrate)} &bull; ${formatDuration(workItem.length)}
                    </small>
                </div>
                <div class="ms-2 flex-shrink-0">
                    ${this.getActionButtons(workItem)}
                </div>
            </div>
            
            ${['Processing', 'Uploading', 'Downloading'].includes(workItem.status) ? `
                <div class="progress mb-2" style="position: relative;">
                    <div class="progress-bar progress-bar-striped progress-bar-animated"
                         role="progressbar"
                         style="width: ${workItem.status === 'Uploading' || workItem.status === 'Downloading' ? (workItem.transferProgress || 0) : progressPercent}%"
                         aria-valuenow="${progressPercent}"
                         aria-valuemin="0"
                         aria-valuemax="100">
                    </div>
                    <span class="progress-label">${['Uploading', 'Downloading'].includes(workItem.status) ? `${workItem.transferProgress || 0}%` : `${progressPercent}%`}</span>
                </div>
            ` : ''}
            
            ${workItem.errorMessage ? `
                <div class="alert alert-danger alert-sm mb-0 mt-2">
                    <i class="fas fa-exclamation-triangle me-2"></i>
                    ${escapeHtml(workItem.errorMessage)}
                </div>
            ` : ''}
            
            <div class="text-muted small mt-1">
                ${new Date(workItem.createdAt).toLocaleString()}${workItem.completedAt ? ` &rarr; ${new Date(workItem.completedAt).toLocaleString()}` : ''}
            </div>
        `;
    }

    /**
     * Returns the HTML for the action button(s) appropriate to the work item's current status.
     * @param {object} workItem - Work item with a string `status` property.
     * @returns {string} HTML for one or more action buttons.
     */
    getActionButtons(workItem) {
        switch (workItem.status) {
            case 'Pending':
                return '<button class="btn btn-sm btn-outline-danger remove-btn" data-action="remove" title="Remove from queue"><i class="fas fa-times"></i></button>';
            case 'Processing':
            case 'Uploading':
            case 'Downloading':
                return `
                    <div class="btn-group" role="group">
                        <button class="btn btn-sm btn-outline-danger remove-btn" data-action="remove" title="Stop/Cancel"><i class="fas fa-times"></i></button>
                        <button class="btn btn-sm btn-outline-info log-btn" data-action="log" title="View Log"><i class="fas fa-terminal"></i></button>
                    </div>
                `;
            case 'Completed':
                return '<button class="btn btn-sm btn-outline-info log-btn" data-action="log" title="View Log"><i class="fas fa-terminal"></i></button>';
            case 'Failed':
            case 'Cancelled':
            case 'Stopped':
                return '<button class="btn btn-sm btn-outline-info log-btn" data-action="log" title="View Log"><i class="fas fa-terminal"></i></button>';
            default:
                return '';
        }
    }

    /**
     * Opens the stop/cancel confirmation modal for the specified work item.
     * Replaces button nodes to remove stale event listeners before attaching new ones.
     * @param {string} workItemId - ID of the work item to stop or cancel.
     */
    showStopCancelDialog(workItemId) {
        const modalEl = document.getElementById('stopCancelModal');
        const modal = bootstrap.Modal.getOrCreateInstance(modalEl);

        // Clone and replace buttons to remove old event listeners
        const stopBtn = document.getElementById('stopCancelStop');
        const cancelBtn = document.getElementById('stopCancelCancel');
        const newStop = stopBtn.cloneNode(true);
        const newCancel = cancelBtn.cloneNode(true);
        stopBtn.replaceWith(newStop);
        cancelBtn.replaceWith(newCancel);

        newStop.addEventListener('click', async () => {
            modal.hide();
            await this.stopWorkItem(workItemId);
        });

        newCancel.addEventListener('click', async () => {
            modal.hide();
            await this.cancelWorkItem(workItemId);
        });

        modal.show();
    }

    /**
     * Stops the specified work item and re-queues it for retry on the next scan.
     * @param {string} id - Work item ID to stop.
     */
    async stopWorkItem(id) {
        try {
            const response = await fetch(`/Home/StopWorkItem?id=${id}`, { method: 'POST' });
            if (!response.ok) throw new Error('Failed to stop work item');
            showToast('Work item stopped \u2014 will be re-queued on next scan', 'info');
        } catch (error) {
            showToast('Error stopping work item: ' + error.message, 'danger');
        }
    }

    /**
     * Permanently cancels the specified work item so it will not be reprocessed.
     * @param {string} id - Work item ID to cancel.
     */
    async cancelWorkItem(id) {
        try {
            const response = await fetch(`/Home/CancelWorkItem?id=${id}`, { method: 'POST' });
            if (!response.ok) throw new Error('Failed to cancel work item');
            showToast('Work item cancelled \u2014 will not be reprocessed', 'info');
        } catch (error) {
            showToast('Error cancelling work item: ' + error.message, 'danger');
        }
    }

    /**
     * Appends a log message received via SignalR to the in-memory log buffer.
     * If the log modal is currently open for this work item, appends the entry live
     * and auto-scrolls if the user was already at the bottom.
     * @param {string} workItemId - ID of the work item the message belongs to.
     * @param {string} message - Log line text from the transcoding service.
     */
    addLogMessage(workItemId, message) {
        if (!this.logs.has(workItemId)) {
            this.logs.set(workItemId, []);
        }

        this.logs.get(workItemId).push({
            timestamp: new Date(),
            message: message
        });

        // Append to log modal if it's open for this work item (don't replace everything)
        const logModal = document.getElementById('logModal');
        if (logModal.getAttribute('data-work-item-id') === workItemId) {
            const logContent = document.getElementById('logContent');
            const wasAtBottom = logContent.scrollHeight - logContent.scrollTop - logContent.clientHeight < 50;

            const entry = document.createElement('div');
            entry.className = 'log-entry';
            entry.innerHTML = `<div class="log-entry">${escapeHtml(message)}</div>`;
            logContent.appendChild(entry);

            if (wasAtBottom) {
                logContent.scrollTop = logContent.scrollHeight;
            }
        }
    }

    /**
     * Opens the log modal for a work item, loading persisted server logs first and overlaying
     * any additional in-memory SignalR entries.
     * @param {string} workItemId - ID of the work item whose log to display.
     */
    async showLog(workItemId) {
        const workItem = this.workItems.get(workItemId);
        const logModal = document.getElementById('logModal');
        const logModalTitle = logModal.querySelector('.modal-title');

        logModalTitle.innerHTML = `
            <i class="fas fa-terminal me-2"></i>
            Transcoding Log - ${workItem?.fileName || 'Unknown'}
        `;

        logModal.setAttribute('data-work-item-id', workItemId);

        // Load persisted logs from server, then overlay any in-memory entries
        await this.loadLogsFromServer(workItemId);
        this.updateLogModal(workItemId);

        bootstrap.Modal.getOrCreateInstance(logModal).show();
    }

    /**
     * Fetches persisted log lines from the server and replaces the in-memory log buffer for the item.
     * @param {string} workItemId - ID of the work item to load logs for.
     */
    async loadLogsFromServer(workItemId) {
        try {
            const response = await fetch(`/Home/GetWorkItemLogs?id=${workItemId}`);
            if (!response.ok) return;
            const serverLogs = await response.json();
            if (serverLogs.length > 0) {
                // Replace in-memory logs with server-persisted ones
                this.logs.set(workItemId, serverLogs.map(line => ({
                    timestamp: null,
                    message: line,
                    fromServer: true
                })));
            }
        } catch { }
    }

    /**
     * Rebuilds the log modal content from the in-memory log buffer and scrolls to the bottom.
     * @param {string} workItemId - ID of the work item whose logs to render.
     */
    updateLogModal(workItemId) {
        const logContent = document.getElementById('logContent');
        const logs = this.logs.get(workItemId) || [];

        logContent.innerHTML = logs.map(log =>
            log.fromServer
                ? `<div class="log-entry">${escapeHtml(log.message)}</div>`
                : `<div class="log-entry">${escapeHtml(log.message)}</div>`
        ).join('');

        // Scroll to bottom on initial load
        logContent.scrollTop = logContent.scrollHeight;
    }

    /** @deprecated Delegates to `refreshStats`. Use `loadWorkItems` for a full refresh. */
    updateStatistics() {
        // Delegate to refreshStats which fetches real counts from the server
        this.refreshStats();
    }

    // --- Auto-Scan methods ---

    /**
     * Loads auto-scan configuration from the server.
     * @param {boolean} [fullLoad=true] - When true, also populates the enabled/interval form inputs.
     *   Pass false for lightweight refreshes after adding/removing directories.
     */
    async loadAutoScanConfig(fullLoad = true) {
        try {
            const response = await fetch('/Home/GetAutoScanConfig');
            if (!response.ok) return;
            const config = await response.json();
            this._lastAutoScanConfig = config;

            // Only set form inputs on full load (page init), not after every action
            if (fullLoad) {
                const enabledEl = document.getElementById('autoScanEnabled');
                const intervalEl = document.getElementById('autoScanInterval');
                if (enabledEl) enabledEl.checked = !!config.enabled;
                if (intervalEl) intervalEl.value = config.intervalMinutes > 0 ? config.intervalMinutes : 60;
            }

            // Always refresh directory list and status
            this.renderAutoScanDirectories(config.directories || []);

            const statusEl = document.getElementById('autoScanStatus');
            if (statusEl) {
                if (config.lastScanTime) {
                    const scanDate = new Date(config.lastScanTime);
                    const ago = this.formatTimeAgo(scanDate);
                    const newFiles = config.lastScanNewFiles ?? 0;
                    statusEl.textContent = `Last scan: ${ago} — Found ${newFiles} new file(s)`;
                } else {
                    statusEl.textContent = 'Last scan: Never';
                }
            }
        } catch (error) {
            console.error('Error loading auto-scan config:', error);
        }
    }

    /**
     * Renders the list of auto-scan watched directories with per-item remove buttons.
     * @param {string[]} directories - Array of absolute directory paths to display.
     */
    renderAutoScanDirectories(directories) {
        const container = document.getElementById('autoScanDirectories');
        if (!container) return;

        if (!directories || directories.length === 0) {
            container.innerHTML = '<div class="text-muted text-center py-2"><small>No directories added</small></div>';
            return;
        }

        container.innerHTML = directories.map(dir => {
            // Support both legacy string format and new WatchedFolder object format
            const path = typeof dir === 'string' ? dir : (dir.path || '');
            const hasOverrides = typeof dir === 'object' && dir.encodingOverrides &&
                Object.values(dir.encodingOverrides).some(v => v !== null && v !== undefined);
            return `
            <div class="d-flex justify-content-between align-items-center py-1 px-2 border-bottom">
                <small class="text-truncate me-2" title="${escapeHtml(path)}">
                    <i class="fas fa-folder text-warning me-1"></i>${escapeHtml(path)}
                    ${hasOverrides ? '<i class="fas fa-sliders-h text-info ms-1" title="Has custom encoding settings"></i>' : ''}
                </small>
                <div class="d-flex gap-1 flex-shrink-0">
                    <button class="btn btn-sm btn-outline-secondary border-0 p-0 px-1 folder-settings-btn" data-path="${escapeHtml(path)}" title="Folder encoding settings">
                        <i class="fas fa-cog"></i>
                    </button>
                    <button class="btn btn-sm btn-outline-danger border-0 p-0 px-1 folder-remove-btn" data-path="${escapeHtml(path)}" title="Remove">
                        <i class="fas fa-times"></i>
                    </button>
                </div>
            </div>`;
        }).join('');

        container.querySelectorAll('.folder-remove-btn').forEach(btn => {
            btn.addEventListener('click', () => this.removeAutoScanDirectory(btn.getAttribute('data-path')));
        });
        container.querySelectorAll('.folder-settings-btn').forEach(btn => {
            btn.addEventListener('click', () => this.openFolderSettings(btn.getAttribute('data-path')));
        });
    }

    /**
     * Adds a directory to the auto-scan watch list via a quick-action button in the library modal.
     * @param {string} path - Absolute path of the directory to watch.
     */
    async watchDirectory(path) {
        try {
            const response = await fetch('/Home/AddAutoScanDirectory', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path })
            });
            if (!response.ok) throw new Error(await response.text());
            const dirName = path.split(/[/\\]/).filter(Boolean).pop() || path;
            showToast(`Watching "${dirName}" for auto-scan`, 'success');
            this.loadAutoScanConfig(false);
        } catch (error) {
            showToast('Error: ' + error.message, 'danger');
        }
    }

    /** Reads the directory path from the auto-scan input field and adds it to the watch list. */
    async addAutoScanDirectory() {
        const input = document.getElementById('autoScanDirInput');
        const path = input?.value?.trim();
        if (!path) {
            showToast('Please enter a directory path', 'warning');
            return;
        }

        try {
            const response = await fetch('/Home/AddAutoScanDirectory', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path })
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
            input.value = '';
            await this.loadAutoScanConfig(false);
        } catch (error) {
            showToast('Error adding directory: ' + error.message, 'danger');
        }
    }

    /**
     * Removes a directory from the auto-scan watch list and refreshes the directory display.
     * @param {string} path - Absolute path of the directory to remove.
     */
    async removeAutoScanDirectory(path) {
        try {
            const response = await fetch('/Home/RemoveAutoScanDirectory', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path })
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
            await this.loadAutoScanConfig(false);
        } catch (error) {
            showToast('Error removing directory: ' + error.message, 'danger');
        }
    }

    /**
     * Enables or disables the auto-scan background service.
     * @param {boolean} enabled - True to enable automatic scanning.
     */
    async setAutoScanEnabled(enabled) {
        try {
            const response = await fetch('/Home/SetAutoScanEnabled', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ enabled })
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
        } catch (error) {
            showToast('Error updating auto-scan: ' + error.message, 'danger');
        }
    }

    /**
     * Updates the auto-scan polling interval on the server.
     * @param {number} minutes - Scan interval in minutes (must be ≥ 1).
     */
    async setAutoScanInterval(minutes) {
        if (isNaN(minutes) || minutes < 1) return;
        try {
            const response = await fetch('/Home/SetAutoScanInterval', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ intervalMinutes: minutes })
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
        } catch (error) {
            showToast('Error updating scan interval: ' + error.message, 'danger');
        }
    }

    /** Requests an immediate auto-scan run outside of the normal schedule. */
    async triggerAutoScan() {
        try {
            showToast('Starting auto-scan...', 'info');
            const response = await fetch('/Home/TriggerAutoScan', {
                method: 'POST'
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
            showToast('Auto-scan triggered successfully', 'success');
        } catch (error) {
            showToast('Error triggering scan: ' + error.message, 'danger');
        }
    }

    /** Prompts for confirmation then clears all auto-scan history from the server. */
    async clearAutoScanHistory() {
        if (!await showConfirmModal('Clear History', '<p>Clear all auto-scan history? This cannot be undone.</p>', 'Clear History')) return;
        try {
            const response = await fetch('/Home/ClearAutoScanHistory', {
                method: 'POST'
            });
            if (!response.ok) {
                const error = await response.text();
                throw new Error(error);
            }
            await this.loadAutoScanConfig(false);
            showToast('Scan history cleared', 'success');
        } catch (error) {
            showToast('Error clearing history: ' + error.message, 'danger');
        }
    }

    /**
     * Returns a human-readable relative time string (e.g. "3 hours ago").
     * Falls back to locale date string for dates older than a week.
     * @param {Date} date - The date to compare against now.
     * @returns {string} Relative time description.
     */
    formatTimeAgo(date) {
        const now = new Date();
        const diffMs = now - date;
        const diffSec = Math.floor(diffMs / 1000);
        const diffMin = Math.floor(diffSec / 60);
        const diffHr = Math.floor(diffMin / 60);
        const diffDays = Math.floor(diffHr / 24);

        if (diffSec < 60) return 'just now';
        if (diffMin < 60) return `${diffMin} minute${diffMin !== 1 ? 's' : ''} ago`;
        if (diffHr < 24) return `${diffHr} hour${diffHr !== 1 ? 's' : ''} ago`;
        if (diffDays < 7) return `${diffDays} day${diffDays !== 1 ? 's' : ''} ago`;
        return date.toLocaleString();
    }

    /**
     * Formats a byte count into a human-readable size string (e.g. "1.23 GB").
     * @param {number} bytes - File size in bytes.
     * @returns {string} Formatted size string.
     */
    formatFileSize(bytes) {
        return formatFileSize(bytes);
    }

    // --- Cluster functionality ---

    /**
     * Loads cluster configuration from the server and optionally populates the cluster settings form.
     * @param {boolean} [updateUI=true] - When true, fills in all cluster settings form inputs.
     */
    async loadClusterConfig(updateUI = true) {
        try {
            const response = await fetch('/Home/GetClusterConfig');
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const config = await response.json();
            this.clusterEnabled = config.enabled;
            this.clusterRole = config.role;
            this.clusterNodeId = config.nodeId;
            this.clusterNodeName = config.nodeName;

            if (updateUI) {
                // Populate settings form
                const el = (id) => document.getElementById(id);
                if (el('clusterEnabled')) el('clusterEnabled').checked = config.enabled;
                if (el('clusterRole')) el('clusterRole').value = config.role;
                if (el('clusterNodeName')) el('clusterNodeName').value = config.nodeName || '';
                this._hasExistingSecret = config.hasSecret;
                if (el('clusterSecret')) {
                    el('clusterSecret').value = '';
                    el('clusterSecret').placeholder = config.hasSecret ? '(secret configured)' : 'Enter a shared secret';
                }
                if (el('clusterAutoDiscovery')) el('clusterAutoDiscovery').checked = config.autoDiscovery !== false;
                if (el('clusterLocalEncoding')) el('clusterLocalEncoding').checked = config.localEncodingEnabled !== false;
                if (el('clusterMasterUrl')) el('clusterMasterUrl').value = config.masterUrl || '';
                if (el('clusterNodeTempDir')) el('clusterNodeTempDir').value = config.nodeTempDirectory || '';

                this.updateClusterRoleUI(config.role);
                this.renderManualNodes(config.manualNodes || []);
            }

            // Load workers if cluster is active
            if (config.enabled && config.role !== 'standalone') {
                await this.loadWorkers();
            }

            this.renderClusterPanel();
            this.updateNodeBanner();
        } catch (error) {
            console.error('Failed to load cluster config:', error);
        }
    }

    /** Fetches the current list of connected worker nodes and refreshes the local `workers` map. */
    async loadWorkers() {
        try {
            const response = await fetch('/Home/GetClusterStatus');
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const status = await response.json();
            this.localEncodingEnabled = status.localEncodingEnabled !== false;
            this.selfCapabilities = status.selfCapabilities || null;
            this.localCompletedJobs = status.localCompletedJobs || 0;
            this.localFailedJobs = status.localFailedJobs || 0;
            if (status.nodeId) this.clusterNodeId = status.nodeId;
            if (status.nodeName) this.clusterNodeName = status.nodeName;
            const nodes = status.nodes || [];
            this.workers.clear();
            for (const node of nodes) {
                this.workers.set(node.nodeId, node);
            }
        } catch (error) {
            console.error('Failed to load workers:', error);
        }
    }

    /**
     * Reads cluster settings from the form, merges them with the existing server config (to preserve
     * fields not shown in the UI), and saves to the server. Prompts for restart when cluster mode
     * is toggled on or off, and confirms before switching to node mode.
     */
    async saveClusterConfig() {
        // Load existing config first to preserve nodeId, timeouts, etc.
        // Also use it to detect if cluster mode is being toggled (for restart prompt)
        let config;
        let serverWasCluster = false;
        try {
            const existing = await fetch('/Home/GetClusterConfig');
            config = await existing.json();
            serverWasCluster = config.enabled && config.role !== 'standalone';
        } catch {
            config = {};
        }

        const el = (id) => document.getElementById(id);
        config.enabled = el('clusterEnabled')?.checked || false;
        config.role = el('clusterRole')?.value || 'standalone';
        config.nodeName = el('clusterNodeName')?.value || '';
        // Only send secret if user typed a new one (field is blank on load for security)
        const newSecret = el('clusterSecret')?.value;
        if (newSecret) config.sharedSecret = newSecret;
        config.autoDiscovery = el('clusterAutoDiscovery')?.checked !== false;
        config.localEncodingEnabled = el('clusterLocalEncoding')?.checked !== false;
        config.masterUrl = el('clusterMasterUrl')?.value || '';
        config.nodeTempDirectory = el('clusterNodeTempDir')?.value || '';
        config.manualNodes = this._manualNodes || [];

        // Require secret for cluster mode
        if (config.enabled && config.role !== 'standalone' && !config.sharedSecret && !this._hasExistingSecret) {
            showToast('A shared secret is required to enable cluster mode. Enter one or click Generate.', 'danger');
            return;
        }

        // Warn when switching to node mode
        if (config.role === 'node' && this.clusterRole !== 'node') {
            const confirmed = await showConfirmModal(
                'Switch to Node Mode',
                '<p>Switching to Node mode will:</p><ul><li>Stop any active encoding</li><li>Clear the local queue</li><li>Disable auto-scanning</li></ul><p>This instance will only process jobs delegated by a master.</p>',
                'Switch to Node Mode'
            );
            if (!confirmed) return;
        }

        try {
            const response = await fetch('/Home/SaveClusterConfig', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(config)
            });
            if (!response.ok) throw new Error(`HTTP ${response.status}`);
            const result = await response.json();
            if (result.success) {
                const isCluster = config.enabled && config.role !== 'standalone';

                this.clusterEnabled = config.enabled;
                this.clusterRole = config.role;
                const status = document.getElementById('clusterSaveStatus');
                if (status) {
                    status.style.display = 'inline';
                    setTimeout(() => status.style.display = 'none', 3000);
                }
                if (serverWasCluster !== isCluster) {
                    const confirmed = await showConfirmModal(
                        'Restart Required',
                        '<p>Snacks needs to restart to apply network binding changes.</p><p>Any active encoding will be stopped and re-queued after restart.</p>',
                        'Restart Now'
                    );
                    if (confirmed) {
                        await fetch('/Home/Restart', { method: 'POST' });
                        // App will restart — page will reconnect automatically
                    }
                } else {
                    showToast('Cluster settings saved', 'success');
                }
                this.localEncodingEnabled = config.localEncodingEnabled;
                if (config.enabled) await this.loadWorkers();
                this.renderClusterPanel();
                this.updateNodeBanner();
            } else {
                showToast('Error saving cluster settings: ' + (result.error || 'Unknown error'), 'danger');
            }
        } catch (error) {
            showToast('Error saving cluster settings: ' + error.message, 'danger');
        }
    }

    /**
     * Shows or hides role-specific settings sections (master vs. node) and updates the description text.
     * @param {string} role - Cluster role: 'standalone', 'master', or 'node'.
     */
    updateClusterRoleUI(role) {
        const masterSettings = document.getElementById('masterSettings');
        const nodeSettings = document.getElementById('nodeSettings');
        const roleDesc = document.getElementById('roleDescription');

        if (masterSettings) masterSettings.style.display = role === 'master' ? '' : 'none';
        if (nodeSettings) nodeSettings.style.display = role === 'node' ? '' : 'none';

        if (roleDesc) {
            switch (role) {
                case 'master': roleDesc.textContent = 'Has the media library, delegates encoding to nodes, and encodes locally'; break;
                case 'node': roleDesc.textContent = 'Accepts encoding jobs from a master instance'; break;
                default: roleDesc.textContent = 'Standard single-instance mode'; break;
            }
        }
    }

    /**
     * Renders the list of manually configured cluster nodes with per-item remove buttons.
     * @param {Array<{name: string, url: string}>} nodes - Array of manual node entries.
     */
    renderManualNodes(nodes) {
        this._manualNodes = nodes || [];
        const container = document.getElementById('manualNodesList');
        if (!container) return;

        if (this._manualNodes.length === 0) {
            container.innerHTML = '<div class="text-muted text-center py-2"><small>No manual nodes configured</small></div>';
            return;
        }

        container.innerHTML = this._manualNodes.map((node, idx) => `
            <div class="d-flex justify-content-between align-items-center mb-1 p-1 border rounded">
                <div>
                    <strong class="me-2">${escapeHtml(node.name)}</strong>
                    <small class="text-muted">${escapeHtml(node.url)}</small>
                </div>
                <button class="btn btn-sm btn-outline-danger remove-manual-node" data-idx="${idx}">
                    <i class="fas fa-times"></i>
                </button>
            </div>
        `).join('');

        container.querySelectorAll('.remove-manual-node').forEach(btn => {
            btn.addEventListener('click', () => {
                this._manualNodes.splice(parseInt(btn.dataset.idx), 1);
                this.renderManualNodes(this._manualNodes);
            });
        });
    }

    /**
     * Renders the cluster worker node panel, showing a card per connected node with status,
     * GPU info, job counts, and pause/resume buttons (master-only).
     */
    renderClusterPanel() {
        const panel = document.getElementById('clusterPanel');
        const container = document.getElementById('clusterNodesContainer');
        const countBadge = document.getElementById('clusterNodeCount');
        if (!panel || !container) return;

        const showPanel = this.clusterEnabled && this.clusterRole !== 'standalone';
        panel.style.display = showPanel ? '' : 'none';
        if (!showPanel) return;

        // Filter out self from remote nodes (rendered separately as the self-card),
        // then sort so master nodes appear before worker nodes
        const nodes = Array.from(this.workers.values())
            .filter(n => n.nodeId !== this.clusterNodeId)
            .sort((a, b) => (a.role === 'master' ? -1 : 1) - (b.role === 'master' ? -1 : 1));
        const totalNodes = nodes.length + (this.clusterNodeId ? 1 : 0);
        if (countBadge) countBadge.textContent = `${totalNodes} node${totalNodes !== 1 ? 's' : ''}`;

        if (nodes.length === 0 && !this.clusterNodeId) {
            container.innerHTML = '<div class="text-muted"><i class="fas fa-search me-1"></i>Discovering nodes...</div>';
            return;
        }

        // NodeStatus enum: 0=Online, 1=Busy, 2=Uploading, 3=Downloading, 4=Offline, 5=Unreachable, 6=Paused
        const statusNames = { 0: 'Online', 1: 'Busy', 2: 'Uploading', 3: 'Downloading', 4: 'Offline', 5: 'Unreachable', 6: 'Paused',
            'Online': 'Online', 'Busy': 'Busy', 'Uploading': 'Uploading', 'Downloading': 'Downloading',
            'Offline': 'Offline', 'Unreachable': 'Unreachable', 'Paused': 'Paused' };
        const statusColors = {
            'Online': 'var(--success-color, #28a745)',
            'Busy': 'var(--info-color, #17a2b8)',
            'Uploading': 'var(--info-color, #17a2b8)',
            'Downloading': 'var(--info-color, #17a2b8)',
            'Offline': 'var(--danger-color, #dc3545)',
            'Unreachable': 'var(--warning-color, #ffc107)',
            'Paused': 'var(--warning-color, #ffc107)'
        };

        // Self-card: this machine shown first, works for both master and worker roles
        const selfGpu = this.selfCapabilities?.gpuVendor && this.selfCapabilities.gpuVendor !== 'none'
            ? this.selfCapabilities.gpuVendor.charAt(0).toUpperCase() + this.selfCapabilities.gpuVendor.slice(1)
            : 'CPU only';
        const selfOs = this.selfCapabilities?.osPlatform || '';
        const localPaused = this.clusterRole === 'master' && !this.localEncodingEnabled;
        const selfStatus = localPaused ? 'Paused' : 'Online';
        const selfStatusColor = statusColors[selfStatus] || 'gray';
        const selfCard = this.clusterNodeId ? `
            <div class="card hover-lift" style="min-width: 180px; max-width: 240px; flex: 1 1 200px;">
                <div class="card-body p-2" style="overflow:hidden;">
                    <div class="d-flex align-items-center mb-1" style="min-width:0;">
                        <span class="flex-shrink-0" style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${selfStatusColor};margin-right:6px;"></span>
                        <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(this.clusterNodeName || 'This Machine')}</strong>
                    </div>
                    <div class="text-muted small">
                        <div>${escapeHtml(this.clusterRole)} &bull; ${escapeHtml(selfOs)}${selfGpu ? ' / ' + escapeHtml(selfGpu) : ''}</div>
                        <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(selfStatus)}</div>
                        <div class="mt-1">Jobs: ${this.localCompletedJobs || 0} done, ${this.localFailedJobs || 0} failed</div>
                        ${this.clusterRole === 'master' ? `
                        <div class="d-flex gap-1 mt-1">
                            <button class="btn btn-sm ${localPaused ? 'btn-outline-success' : 'btn-outline-warning'} flex-grow-1" id="masterLocalPause">
                                <i class="fas fa-${localPaused ? 'play' : 'pause'} me-1"></i>${localPaused ? 'Resume' : 'Pause'}
                            </button>
                            <button class="btn btn-sm btn-outline-secondary cluster-node-settings" data-node-id="${this.clusterNodeId}" data-hostname="${escapeHtml(this.clusterNodeName || 'This Machine')}">
                                <i class="fas fa-cog"></i>
                            </button>
                        </div>` : ''}
                    </div>
                </div>
            </div>` : '';

        container.innerHTML = selfCard + nodes.map(node => {
            const statusName = statusNames[node.status] || 'Unknown';
            const statusColor = statusColors[statusName] || 'gray';

            const statusText = statusName;

            const gpuInfo = node.capabilities?.gpuVendor && node.capabilities.gpuVendor !== 'none'
                ? node.capabilities.gpuVendor.charAt(0).toUpperCase() + node.capabilities.gpuVendor.slice(1)
                : 'CPU only';

            const osInfo = node.capabilities?.osPlatform || '';

            return `
                <div class="card hover-lift" style="min-width: 180px; max-width: 240px; flex: 1 1 200px;">
                    <div class="card-body p-2" style="overflow:hidden;">
                        <div class="d-flex align-items-center mb-1" style="min-width:0;">
                            <span class="flex-shrink-0" style="display:inline-block;width:8px;height:8px;border-radius:50%;background:${statusColor};margin-right:6px;"></span>
                            <strong style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(node.hostname)}</strong>
                        </div>
                        <div class="text-muted small">
                            <div>${escapeHtml(node.role)} &bull; ${escapeHtml(osInfo)}${gpuInfo ? ' / ' + escapeHtml(gpuInfo) : ''}</div>
                            <div style="overflow:hidden; text-overflow:ellipsis; white-space:nowrap;">${escapeHtml(statusText)}</div>
                            <div class="mt-1">Jobs: ${node.completedJobs || 0} done, ${node.failedJobs || 0} failed</div>
                            ${this.clusterRole === 'master' && node.role === 'node' ? `
                                <div class="d-flex gap-1 mt-1">
                                    <button class="btn btn-sm ${node.isPaused ? 'btn-outline-success' : 'btn-outline-warning'} flex-grow-1 cluster-node-pause" data-node-id="${node.nodeId}" data-paused="${node.isPaused}">
                                        <i class="fas fa-${node.isPaused ? 'play' : 'pause'} me-1"></i>${node.isPaused ? 'Resume' : 'Pause'}
                                    </button>
                                    <button class="btn btn-sm btn-outline-secondary cluster-node-settings" data-node-id="${node.nodeId}" data-hostname="${escapeHtml(node.hostname)}" title="Node settings">
                                        <i class="fas fa-cog"></i>
                                    </button>
                                </div>
                            ` : ''}
                        </div>
                    </div>
                </div>
            `;
        }).join('');

        // Bind pause/resume buttons
        container.querySelectorAll('.cluster-node-pause').forEach(btn => {
            btn.addEventListener('click', async () => {
                const nodeId = btn.dataset.nodeId;
                const isPaused = btn.dataset.paused === 'true';
                try {
                    await fetch('/Home/SetNodePaused', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ nodeId, paused: !isPaused })
                    });
                } catch (error) {
                    showToast('Error: ' + error.message, 'danger');
                }
            });
        });

        // Bind node settings buttons
        container.querySelectorAll('.cluster-node-settings').forEach(btn => {
            btn.addEventListener('click', () => {
                this.openNodeSettings(btn.dataset.nodeId, btn.dataset.hostname);
            });
        });

        // Bind master local encoding pause button
        document.getElementById('masterLocalPause')?.addEventListener('click', async () => {
            try {
                await fetch('/Home/SetLocalEncodingPaused', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ paused: !localPaused })
                });
                this.localEncodingEnabled = localPaused; // flip state
                this.renderClusterPanel();
            } catch (error) {
                showToast('Error: ' + error.message, 'danger');
            }
        });
    }

    /** Shows or hides the "node mode" banner and updates the master hostname display. */
    updateNodeBanner() {
        const banner = document.getElementById('nodeBanner');
        if (!banner) return;

        if (this.clusterEnabled && this.clusterRole === 'node') {
            banner.style.display = '';
            // Find master in workers
            const master = Array.from(this.workers.values()).find(n => n.role === 'master');
            const masterName = document.getElementById('nodeBannerMaster');
            if (masterName) {
                masterName.textContent = master ? `${master.hostname} (${master.ipAddress})` : 'a master';
            }
        } else {
            banner.style.display = 'none';
        }
    }

    /**
     * Binds all cluster settings panel event handlers: role selector, save, secret generation,
     * secret visibility toggle, manual node addition, and standalone switch.
     */
    initializeClusterEventHandlers() {
        // Role selector changes
        const roleSelect = document.getElementById('clusterRole');
        if (roleSelect) {
            roleSelect.addEventListener('change', () => {
                this.updateClusterRoleUI(roleSelect.value);
            });
        }

        // Save cluster config
        const saveBtn = document.getElementById('saveClusterConfig');
        if (saveBtn) {
            saveBtn.addEventListener('click', () => this.saveClusterConfig());
        }

        // Generate secret
        const genBtn = document.getElementById('generateSecret');
        if (genBtn) {
            genBtn.addEventListener('click', () => {
                const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
                const array = new Uint8Array(32);
                crypto.getRandomValues(array);
                let secret = '';
                for (let i = 0; i < 32; i++) secret += chars.charAt(array[i] % chars.length);
                document.getElementById('clusterSecret').value = secret;
            });
        }

        // Toggle secret visibility
        const toggleBtn = document.getElementById('toggleSecretVisibility');
        if (toggleBtn) {
            toggleBtn.addEventListener('click', () => {
                const input = document.getElementById('clusterSecret');
                const isPassword = input.type === 'password';
                input.type = isPassword ? 'text' : 'password';
                toggleBtn.innerHTML = `<i class="fas fa-eye${isPassword ? '-slash' : ''}"></i>`;
            });
        }

        // Add manual node
        const addNodeBtn = document.getElementById('addManualNode');
        if (addNodeBtn) {
            addNodeBtn.addEventListener('click', () => {
                const name = document.getElementById('manualNodeName')?.value?.trim();
                const url = document.getElementById('manualNodeUrl')?.value?.trim();
                if (name && url) {
                    if (!this._manualNodes) this._manualNodes = [];
                    this._manualNodes.push({ name, url });
                    this.renderManualNodes(this._manualNodes);
                    document.getElementById('manualNodeName').value = '';
                    document.getElementById('manualNodeUrl').value = '';
                }
            });
        }

        // Switch to standalone button on node banner
        const standaloneBtn = document.getElementById('switchToStandalone');
        if (standaloneBtn) {
            standaloneBtn.addEventListener('click', async () => {
                const confirmed = await showConfirmModal('Switch to Standalone', '<p>Switch back to standalone mode? This will disconnect from the cluster.</p>', 'Switch to Standalone');
                if (confirmed) {
                    const response = await fetch('/Home/GetClusterConfig');
                    if (!response.ok) throw new Error(`HTTP ${response.status}`);
                    const config = await response.json();
                    config.role = 'standalone';
                    config.enabled = false;
                    const saveResp = await fetch('/Home/SaveClusterConfig', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify(config)
                    });
                    if (!saveResp.ok) throw new Error(`HTTP ${saveResp.status}`);
                    this.clusterEnabled = false;
                    this.clusterRole = 'standalone';
                    this.renderClusterPanel();
                    this.updateNodeBanner();
                    showToast('Switched to standalone mode', 'success');
                }
            });
        }
    }

    /******************************************************************
     *  Override Dialog — built entirely in JS, appended to <body>
     ******************************************************************/

    /** Shows the override dialog. */
    openOverrideDialog(title, showNodeRules, onSave, onReset) {
        document.getElementById('overrideDialogTitle').innerHTML = title;
        document.getElementById('overrideNodeRules').style.display = showNodeRules ? '' : 'none';

        // Wire save/reset buttons (clone to clear old listeners)
        for (const [id, handler] of [['overrideDialogSave', onSave], ['overrideDialogReset', onReset]]) {
            const btn = document.getElementById(id);
            const clone = btn.cloneNode(true);
            btn.replaceWith(clone);
            clone.addEventListener('click', handler);
        }

        document.getElementById('overrideDialog').classList.add('open');
    }

    /** Hides the override dialog. */
    closeOverrideDialog() {
        document.getElementById('overrideDialog').classList.remove('open');
    }

    /******************************************************************
     *  Node Settings
     ******************************************************************/

    /** Opens the node settings overlay. */
    async openNodeSettings(nodeId, hostname) {
        this.resetOverrideForm();
        document.getElementById('nodeOnly4K').checked = false;
        document.getElementById('nodeExclude4K').checked = false;

        try {
            const resp = await fetch('/Home/GetNodeSettings');
            if (resp.ok) {
                const config = await resp.json();
                const ns = config.nodes?.[nodeId];
                if (ns) {
                    document.getElementById('nodeOnly4K').checked = ns.only4K || false;
                    document.getElementById('nodeExclude4K').checked = ns.exclude4K || false;
                    if (ns.encodingOverrides) this.populateOverrideForm(ns.encodingOverrides);
                }
            }
        } catch (e) { console.error('Failed to load node settings', e); }

        this.initOverrideToggles();

        const only4K = document.getElementById('nodeOnly4K');
        const excl4K = document.getElementById('nodeExclude4K');
        only4K.onchange = () => { if (only4K.checked) excl4K.checked = false; };
        excl4K.onchange = () => { if (excl4K.checked) only4K.checked = false; };

        this.openOverrideDialog(
            `<i class="fas fa-server me-2"></i>Node Settings: ${escapeHtml(hostname)}`,
            true,
            () => this.saveNodeSettings(nodeId),
            () => this.deleteNodeSettings(nodeId)
        );
    }

    async saveNodeSettings(nodeId) {
        const overrides = this.readOverrideForm();
        const settings = {
            nodeId,
            only4K: document.getElementById('nodeOnly4K').checked || null,
            exclude4K: document.getElementById('nodeExclude4K').checked || null,
            encodingOverrides: Object.values(overrides).some(v => v !== null) ? overrides : null
        };
        try {
            const resp = await fetch('/Home/SaveNodeSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(settings)
            });
            if (!resp.ok) throw new Error(await resp.text());
            showToast('Node settings saved', 'success');
            this.closeOverrideDialog();
        } catch (e) { showToast('Error saving node settings: ' + e.message, 'danger'); }
    }

    async deleteNodeSettings(nodeId) {
        try {
            const resp = await fetch('/Home/DeleteNodeSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ nodeId })
            });
            if (!resp.ok) throw new Error(await resp.text());
            showToast('Node settings reset to defaults', 'success');
            this.closeOverrideDialog();
        } catch (e) { showToast('Error resetting node settings: ' + e.message, 'danger'); }
    }

    /******************************************************************
     *  Folder Settings
     ******************************************************************/

    /** Opens the folder settings overlay. */
    openFolderSettings(path) {
        this.resetOverrideForm();

        const config = this._lastAutoScanConfig;
        if (config?.directories) {
            const folder = config.directories.find(d =>
                (typeof d === 'string' ? d : d.path) === path
            );
            if (folder && typeof folder === 'object' && folder.encodingOverrides) {
                this.populateOverrideForm(folder.encodingOverrides);
            }
        }

        this.initOverrideToggles();

        const displayName = path.split(/[/\\]/).filter(Boolean).pop() || path;
        this.openOverrideDialog(
            `<i class="fas fa-folder-open me-2"></i>Folder Settings: ${escapeHtml(displayName)}`,
            false,
            () => this.saveFolderSettings(path),
            () => this.resetFolderSettings(path)
        );
    }

    async saveFolderSettings(path) {
        const overrides = this.readOverrideForm();
        const hasOverrides = Object.values(overrides).some(v => v !== null);
        try {
            const resp = await fetch('/Home/SaveFolderSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path, encodingOverrides: hasOverrides ? overrides : null })
            });
            if (!resp.ok) throw new Error(await resp.text());
            showToast('Folder settings saved', 'success');
            this.closeOverrideDialog();
            this.loadAutoScanConfig(false);
        } catch (e) { showToast('Error saving folder settings: ' + e.message, 'danger'); }
    }

    async resetFolderSettings(path) {
        try {
            const resp = await fetch('/Home/SaveFolderSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ path, encodingOverrides: null })
            });
            if (!resp.ok) throw new Error(await resp.text());
            showToast('Folder settings reset to defaults', 'success');
            this.closeOverrideDialog();
            this.loadAutoScanConfig(false);
        } catch (e) { showToast('Error resetting folder settings: ' + e.message, 'danger'); }
    }

    /******************************************************************
     *  Override Form Helpers (shared single form with 'ovr' prefix)
     ******************************************************************/

    /** The list of override fields and their types for form reading/writing. */
    static OVERRIDE_FIELDS = {
        Format: 'select', Codec: 'select', HardwareAcceleration: 'select',
        TargetBitrate: 'number', FourKBitrateMultiplier: 'select',
        Skip4K: 'bool', StrictBitrate: 'bool', TwoChannelAudio: 'bool',
        EnglishOnlyAudio: 'bool', EnglishOnlySubtitles: 'bool',
        DeleteOriginalFile: 'bool', SkipPercentAboveTarget: 'number'
    };

    /** Wire up override toggle checkboxes to enable/disable their associated fields. */
    initOverrideToggles() {
        const defaults = { TargetBitrate: '3500', FourKBitrateMultiplier: '4', SkipPercentAboveTarget: '20' };
        document.querySelectorAll('#overrideFields .override-toggle').forEach(toggle => {
            toggle.onchange = () => {
                const field = toggle.dataset.field;
                const type = TranscodingManager.OVERRIDE_FIELDS[field];
                const el = document.getElementById(`ovr${field}`);
                if (!el) return;
                el.disabled = !toggle.checked;
                if (!toggle.checked) {
                    if (type === 'bool') el.checked = false;
                    else if (type === 'number') el.value = defaults[field] || '0';
                    else el.selectedIndex = 0;
                }
            };
        });
    }

    /** Resets all override toggles to unchecked and resets all field values to defaults. */
    resetOverrideForm() {
        const defaults = { TargetBitrate: '3500', FourKBitrateMultiplier: '4', SkipPercentAboveTarget: '20' };
        for (const [field, type] of Object.entries(TranscodingManager.OVERRIDE_FIELDS)) {
            const toggle = document.getElementById(`ovr_${field}`);
            if (toggle) toggle.checked = false;
            const el = document.getElementById(`ovr${field}`);
            if (!el) continue;
            el.disabled = true;
            if (type === 'bool') el.checked = false;
            else if (type === 'number') el.value = defaults[field] || '0';
            else el.selectedIndex = 0;
        }
    }

    /** Populates the override form from a server-returned overrides object. */
    populateOverrideForm(overrides) {
        for (const [field, type] of Object.entries(TranscodingManager.OVERRIDE_FIELDS)) {
            const key = field.charAt(0).toLowerCase() + field.slice(1);
            const val = overrides[key];
            if (val === null || val === undefined) continue;

            const toggle = document.getElementById(`ovr_${field}`);
            if (toggle) toggle.checked = true;

            const el = document.getElementById(`ovr${field}`);
            if (!el) continue;
            el.disabled = false;

            if (type === 'bool') el.checked = val;
            else if (type === 'number') el.value = val;
            else el.value = val;
        }
    }

    /** Reads the override form and returns an overrides object (null for unset fields). */
    readOverrideForm() {
        const result = {};
        for (const [field, type] of Object.entries(TranscodingManager.OVERRIDE_FIELDS)) {
            const key = field.charAt(0).toLowerCase() + field.slice(1);
            const toggle = document.getElementById(`ovr_${field}`);
            if (!toggle?.checked) { result[key] = null; continue; }

            const el = document.getElementById(`ovr${field}`);
            if (!el) { result[key] = null; continue; }

            if (type === 'bool') result[key] = el.checked;
            else if (type === 'number') result[key] = parseInt(el.value) || 0;
            else result[key] = el.value;
        }
        return result;
    }
}

// Initialize transcoding manager when DOM is loaded
document.addEventListener('DOMContentLoaded', function() {
    window.transcodingManager = new TranscodingManager();
});

/**
 * Escapes HTML special characters to prevent XSS injection from FFmpeg output or server error messages.
 * @param {string} text - Raw text to escape.
 * @returns {string} HTML-safe string.
 */
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

/**
 * Shows a Bootstrap confirmation modal and returns a Promise that resolves to true
 * if the user confirms, or false if they dismiss it.
 * @param {string} title - Modal header title.
 * @param {string} message - HTML body content.
 * @param {string} [confirmText='Confirm'] - Label for the confirm button.
 * @returns {Promise<boolean>} Resolves to true on confirm, false on dismiss.
 */
function showConfirmModal(title, message, confirmText = 'Confirm') {
    return new Promise((resolve) => {
        const modalEl = document.getElementById('confirmModal');
        document.getElementById('confirmModalTitle').innerHTML = `<i class="fas fa-exclamation-triangle me-2"></i>${escapeHtml(title)}`;
        document.getElementById('confirmModalBody').innerHTML = message;
        const confirmBtn = document.getElementById('confirmModalConfirm');
        confirmBtn.textContent = confirmText;

        const modal = bootstrap.Modal.getOrCreateInstance(modalEl);
        const newBtn = confirmBtn.cloneNode(true);
        confirmBtn.replaceWith(newBtn);

        newBtn.addEventListener('click', () => {
            modal.hide();
            resolve(true);
        });
        modalEl.addEventListener('hidden.bs.modal', () => resolve(false), { once: true });

        modal.show();
    });
}

// Global utility functions (formatFileSize, formatDuration, formatBitrate, showToast, showLoading)
// are defined in site.js which is loaded before this file.