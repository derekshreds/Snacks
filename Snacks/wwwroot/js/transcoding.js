// Transcoding functionality with SignalR
class TranscodingManager {
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
        this.initializeSignalR();
        this.initializeEventHandlers();
        this.restoreSettings();
        this.loadAutoScanConfig();
        this.loadWorkItems();
        this.loadPauseState();

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
                console.log('WorkItemUpdated', workItem.id, 'status:', workItem.status, 'progress:', workItem.progress);
                this.updateWorkItem(workItem);
            });

            this.connection.on("TranscodingLog", (workItemId, message) => {
                this.addLogMessage(workItemId, message);
            });

            this.connection.on("AutoScanCompleted", (newFiles, total) => {
                showToast(`Auto-scan complete: ${newFiles} new file(s) found`, newFiles > 0 ? 'success' : 'info');
                this.loadAutoScanConfig(false);
            });

            // Register lifecycle handlers BEFORE start so they catch all events
            this.connection.onreconnected(async () => {
                console.log("SignalR Reconnected — resyncing state");
                this.updateConnectionStatus(true);
                this.loadWorkItems();
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

    initializeEventHandlers() {
        // Library modal event handlers
        document.getElementById('libraryModal').addEventListener('show.bs.modal', () => {
            this.loadDirectories();
        });

        document.getElementById('selectAllFiles').addEventListener('click', () => {
            this.selectAllFiles();
        });

        document.getElementById('processSelectedFiles').addEventListener('click', () => {
            this.processSelectedFiles();
        });

        // Reload auto-scan config when settings modal opens
        document.getElementById('settingsModal')?.addEventListener('show.bs.modal', () => {
            this.loadAutoScanConfig();
        });

        document.getElementById('processDirectory').addEventListener('click', () => {
            this.processCurrentDirectory();
        });

        // Save encoder settings on change (exclude auto-scan inputs)
        document.getElementById('settingsModal').addEventListener('change', (e) => {
            if (!e.target.id.startsWith('autoScan')) {
                this.getEncoderOptions('settings');
            }
        });
        document.getElementById('settingsModal').addEventListener('input', (e) => {
            if (!e.target.id.startsWith('autoScan')) {
                this.getEncoderOptions('settings');
            }
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
    }

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
                    <div class="flex-grow-1" data-path="${dir.path}" data-count="${dir.videoCount}" style="cursor: pointer;">
                        <i class="fas ${dir.videoCount === 0 ? 'fa-hdd' : 'fa-folder'} text-warning me-2"></i>
                        <span>${dir.name}</span>
                        ${dir.videoCount > 0 ? `<small class="text-muted ms-2">${dir.videoCount} videos</small>` : ''}
                    </div>
                    <button class="btn btn-sm btn-link p-0 ms-2 watch-dir-btn" data-path="${dir.path}" title="Watch (Auto-Scan)">
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
            container.innerHTML = `<div class="alert alert-danger">Error loading directories: ${error.message}</div>`;
        }
    }

    async loadSubdirectories(directoryPath) {
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
                // Try both Windows and Unix path separators
                const parent = directoryPath.replace(/[/\\][^/\\]+[/\\]?$/, '');
                if (!parent || parent === directoryPath || parent.length < 2) {
                    this.loadDirectories(); // back to top-level list
                } else {
                    this.loadSubdirectories(parent);
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
            container.innerHTML = `<div class="alert alert-danger">Error: ${error.message}</div>`;
        }
    }

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
                <div class="file-item p-2 border-bottom" data-path="${file.path}">
                    <div class="form-check d-flex align-items-center">
                        <input class="form-check-input me-3" type="checkbox" value="${file.path}" id="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                        <label class="form-check-label w-100" for="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                            <div class="d-flex justify-content-between align-items-center">
                                <div>
                                    <i class="fas fa-file-video text-primary me-2"></i>
                                    <strong>${file.name}</strong>
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
            container.innerHTML = `<div class="alert alert-danger">Error: ${error.message}</div>`;
        }
    }

    showDirectorySummary(directoryPath, videoCount) {
        this.currentDirectory = directoryPath;
        this.selectedFiles.clear();
        const container = document.getElementById('fileList');
        const dirName = directoryPath.split('/').pop() || 'Root';

        container.innerHTML = `
            <div class="text-center py-5">
                <i class="fas fa-folder-open fa-3x text-warning mb-3"></i>
                <h5>${dirName}</h5>
                <p class="text-muted mb-4">${videoCount} video files</p>
                <button class="btn btn-primary btn-lg mb-3" onclick="transcodingManager.processCurrentDirectory()">
                    <i class="fas fa-play me-2"></i>Process All ${videoCount} Files
                </button>
                <br>
                <button class="btn btn-sm btn-outline-secondary" onclick="transcodingManager.loadDirectoryFiles('${directoryPath.replace(/'/g, "\\'")}')">
                    <i class="fas fa-list me-1"></i>Browse Individual Files
                </button>
            </div>
        `;

        this.updateProcessButton();
    }

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
                <div class="file-item p-2 border-bottom" data-path="${file.path}">
                    <div class="form-check d-flex align-items-center">
                        <input class="form-check-input me-3" type="checkbox" value="${file.path}" id="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                        <div class="flex-grow-1">
                            <label class="form-check-label w-100" for="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                                <div class="d-flex justify-content-between align-items-center">
                                    <div>
                                        <i class="fas fa-file-video text-primary me-2"></i>
                                        <strong>${file.name}</strong><br>
                                        <small class="text-muted">${file.relativePath}</small>
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
            container.innerHTML = `<div class="alert alert-danger">Error loading files: ${error.message}</div>`;
        }
    }

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

    updateProcessButton() {
        const button = document.getElementById('processSelectedFiles');
        button.disabled = this.selectedFiles.size === 0;
        button.innerHTML = `<i class="fas fa-play me-1"></i> Process Selected (${this.selectedFiles.size})`;
    }

    closeLibraryModal() {
        const modalEl = document.getElementById('libraryModal');
        const modal = bootstrap.Modal.getInstance(modalEl) || bootstrap.Modal.getOrCreateInstance(modalEl);
        modal.hide();
    }

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

    async processCurrentDirectory(recursive = true) {
        // If no directory selected, process the entire root (all listed directories)
        const dirPath = this.currentDirectory || this.rootDirectory;
        if (!dirPath) {
            showToast('No directory available', 'warning');
            return;
        }

        const options = this.getEncoderOptions('settings');
        const dirName = this.currentDirectory
            ? (this.currentDirectory.split('/').pop() || this.currentDirectory)
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
            OutputDirectory: document.getElementById(`${prefix}OutputDirectory`)?.value || ''
        };
        this.saveSettingsToServer(options);
        return options;
    }

    async saveSettingsToServer(options) {
        try {
            await fetch('/Home/SaveSettings', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(options)
            });
        } catch { }
    }

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
            set('OutputDirectory', saved.OutputDirectory || saved.outputDirectory || '');
        } catch { }
    }

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

            // Clear queue container only — processing is handled separately
            this.workItems.clear();
            document.getElementById('workItemsContainer').innerHTML = '';

            // Render processing items (always shown regardless of page/filter)
            const processingContainer = document.getElementById('processingContainer');
            const processingSection = document.getElementById('processingSection');
            processingContainer.innerHTML = '';
            if (processingItems.length > 0) {
                processingSection.style.display = '';
                for (const item of processingItems) {
                    this.workItems.set(item.id, item);
                    this.renderWorkItem(item);
                }
            } else {
                processingSection.style.display = 'none';
            }

            // Render queue items
            for (const workItem of queueItems) {
                this.workItems.set(workItem.id, workItem);
                this.renderWorkItem(workItem);
            }

            // Update stats (desktop + mobile)
            this.updateStatCounters(stats);

            if (queueItems.length === 0) {
                const msg = this.queueFilter
                    ? `No ${this.queueFilter.toLowerCase()} items`
                    : 'No files in queue';
                document.getElementById('workItemsContainer').innerHTML = `<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>${msg}</div>`;
            }

            this.renderPagination();
            this.renderFilterTabs(stats);
            this.loadPauseState();
        } catch (error) {
            console.error('Error loading work items:', error);
            showToast('Error loading work items: ' + error.message, 'danger');
        }
    }

    setFilter(filter) {
        this.queueFilter = filter;
        this.queuePage = 0;
        this.loadWorkItems();
    }

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

        container.querySelectorAll('.queue-filter-btn').forEach(btn => {
            btn.addEventListener('click', () => {
                const val = btn.dataset.filter;
                this.setFilter(val === '' ? null : val);
            });
        });
    }

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
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} id="pageFirst" title="First page">
                        <i class="fas fa-angle-double-left"></i>
                    </button>
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} id="pagePrev">
                        <i class="fas fa-chevron-left"></i>
                    </button>
                    <button class="btn btn-outline-secondary disabled">${page + 1} / ${totalPages}</button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} id="pageNext">
                        <i class="fas fa-chevron-right"></i>
                    </button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} id="pageLast" title="Last page">
                        <i class="fas fa-angle-double-right"></i>
                    </button>
                </div>
            </nav>
        `;

        document.getElementById('pageFirst')?.addEventListener('click', () => {
            if (this.queuePage > 0) { this.queuePage = 0; this.loadWorkItems(); }
        });
        document.getElementById('pagePrev')?.addEventListener('click', () => {
            if (this.queuePage > 0) { this.queuePage--; this.loadWorkItems(); }
        });
        document.getElementById('pageNext')?.addEventListener('click', () => {
            if (this.queuePage < totalPages - 1) { this.queuePage++; this.loadWorkItems(); }
        });
        document.getElementById('pageLast')?.addEventListener('click', () => {
            if (this.queuePage < totalPages - 1) { this.queuePage = totalPages - 1; this.loadWorkItems(); }
        });
    }

    addWorkItem(workItem) {
        this.workItems.set(workItem.id, workItem);
        this.scheduleQueueRefresh();
    }

    updateWorkItem(workItem) {
        this.workItems.set(workItem.id, workItem);
        const statusString = this.getStatusString(workItem.status);

        // Processing items get rendered immediately to the dedicated section
        if (statusString === 'Processing') {
            this.renderWorkItem(workItem);
        }

        this.scheduleQueueRefresh();
    }

    // Throttle full queue + stats refresh to avoid flooding the server during rapid SignalR events
    scheduleQueueRefresh() {
        if (this._refreshTimer) return;
        this._refreshTimer = setTimeout(() => {
            this._refreshTimer = null;
            this.loadWorkItems();
        }, 2000);
    }

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

    // Helper method to convert numeric status to string
    getStatusString(status) {
        const statusMap = {
            0: 'Pending',
            1: 'Processing',
            2: 'Completed',
            3: 'Failed',
            4: 'Cancelled',
            5: 'Stopped'
        };
        
        return typeof status === 'string' ? status : statusMap[status] || 'Unknown';
    }

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
        if (statusString === 'Processing') {
            if (element.parentNode !== processingContainer) {
                while (processingContainer.firstChild) {
                    processingContainer.removeChild(processingContainer.firstChild);
                }
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
        element.innerHTML = this.getWorkItemHtml({...workItem, status: statusString});

        // Add event listeners
        const removeBtn = element.querySelector('.remove-btn');
        if (removeBtn) {
            removeBtn.addEventListener('click', () => this.showStopCancelDialog(workItem.id));
        }

        const logBtn = element.querySelector('.log-btn');
        if (logBtn) {
            logBtn.addEventListener('click', () => this.showLog(workItem.id));
        }
    }

    getWorkItemHtml(workItem) {
        const statusClass = `status-${workItem.status.toLowerCase()}`;
        const progressPercent = workItem.progress || 0;
        
        return `
            <div class="d-flex justify-content-between align-items-start mb-2">
                <div class="flex-grow-1 min-width-0">
                    <div class="d-flex align-items-center flex-wrap mb-1">
                        <i class="fas fa-file-video me-2 text-primary"></i>
                        <strong class="me-2 text-truncate">${workItem.fileName}</strong>
                        <span class="status-badge ${statusClass}">${workItem.status}</span>
                    </div>
                    <small class="text-muted">
                        ${formatFileSize(workItem.size)} &bull; ${formatBitrate(workItem.bitrate)} &bull; ${formatDuration(workItem.length)}
                    </small>
                </div>
                <div class="ms-2 flex-shrink-0">
                    ${this.getActionButtons(workItem)}
                </div>
            </div>
            
            ${workItem.status === 'Processing' ? `
                <div class="progress mb-2" style="position: relative;">
                    <div class="progress-bar progress-bar-striped progress-bar-animated"
                         role="progressbar"
                         style="width: ${progressPercent}%"
                         aria-valuenow="${progressPercent}"
                         aria-valuemin="0"
                         aria-valuemax="100">
                    </div>
                    <span class="progress-label">${progressPercent}%</span>
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

    getActionButtons(workItem) {
        switch (workItem.status) {
            case 'Pending':
                return '<button class="btn btn-sm btn-outline-danger remove-btn" title="Remove from queue"><i class="fas fa-times"></i></button>';
            case 'Processing':
                return `
                    <div class="btn-group" role="group">
                        <button class="btn btn-sm btn-outline-danger remove-btn" title="Stop/Cancel"><i class="fas fa-times"></i></button>
                        <button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>
                    </div>
                `;
            case 'Completed':
                return '<button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>';
            case 'Failed':
            case 'Cancelled':
            case 'Stopped':
                return '<button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>';
            default:
                return '';
        }
    }

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

    async stopWorkItem(id) {
        try {
            const response = await fetch(`/Home/StopWorkItem?id=${id}`, { method: 'POST' });
            if (!response.ok) throw new Error('Failed to stop work item');
            showToast('Work item stopped \u2014 will be re-queued on next scan', 'info');
        } catch (error) {
            showToast('Error stopping work item: ' + error.message, 'danger');
        }
    }

    async cancelWorkItem(id) {
        try {
            const response = await fetch(`/Home/CancelWorkItem?id=${id}`, { method: 'POST' });
            if (!response.ok) throw new Error('Failed to cancel work item');
            showToast('Work item cancelled \u2014 will not be reprocessed', 'info');
        } catch (error) {
            showToast('Error cancelling work item: ' + error.message, 'danger');
        }
    }

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
            entry.innerHTML = `<span class="text-muted">[${new Date().toLocaleTimeString('en-GB')}]</span> ${escapeHtml(message)}`;
            logContent.appendChild(entry);

            if (wasAtBottom) {
                logContent.scrollTop = logContent.scrollHeight;
            }
        }
    }

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

    updateLogModal(workItemId) {
        const logContent = document.getElementById('logContent');
        const logs = this.logs.get(workItemId) || [];

        logContent.innerHTML = logs.map(log =>
            log.fromServer
                ? `<div class="log-entry">${escapeHtml(log.message)}</div>`
                : `<div class="log-entry"><span class="text-muted">[${log.timestamp.toLocaleTimeString('en-GB')}]</span> ${escapeHtml(log.message)}</div>`
        ).join('');

        // Scroll to bottom on initial load
        logContent.scrollTop = logContent.scrollHeight;
    }

    updateStatistics() {
        // Delegate to refreshStats which fetches real counts from the server
        this.refreshStats();
    }

    // --- Auto-Scan methods ---

    async loadAutoScanConfig(fullLoad = true) {
        try {
            const response = await fetch('/Home/GetAutoScanConfig');
            if (!response.ok) return;
            const config = await response.json();

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

    renderAutoScanDirectories(directories) {
        const container = document.getElementById('autoScanDirectories');
        if (!container) return;

        if (!directories || directories.length === 0) {
            container.innerHTML = '<div class="text-muted text-center py-2"><small>No directories added</small></div>';
            return;
        }

        container.innerHTML = directories.map(dir => `
            <div class="d-flex justify-content-between align-items-center py-1 px-2 border-bottom">
                <small class="text-truncate me-2" title="${escapeHtml(dir)}">
                    <i class="fas fa-folder text-warning me-1"></i>${escapeHtml(dir)}
                </small>
                <button class="btn btn-sm btn-outline-danger border-0 p-0 px-1" data-path="${escapeHtml(dir)}" title="Remove">
                    <i class="fas fa-times"></i>
                </button>
            </div>
        `).join('');

        container.querySelectorAll('button[data-path]').forEach(btn => {
            btn.addEventListener('click', () => this.removeAutoScanDirectory(btn.getAttribute('data-path')));
        });
    }

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

    async clearAutoScanHistory() {
        if (!confirm('Clear all auto-scan history? This cannot be undone.')) return;
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

    // Helper method to format file size
    formatFileSize(bytes) {
        if (bytes === 0) return '0 Bytes';
        const k = 1024;
        const sizes = ['Bytes', 'KB', 'MB', 'GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
    }
}

// Initialize transcoding manager when DOM is loaded
document.addEventListener('DOMContentLoaded', function() {
    window.transcodingManager = new TranscodingManager();
});

// Escape HTML to prevent XSS from FFmpeg output or error messages
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Global utility functions
function formatFileSize(bytes) {
    if (bytes === 0) return '0 Bytes';
    const k = 1024;
    const sizes = ['Bytes', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}

function formatBitrate(bitrate) {
    if (bitrate === 0) return '0 kbps';
    if (bitrate < 1000) return bitrate + ' kbps';
    return (bitrate / 1000).toFixed(1) + ' Mbps';
}

function formatDuration(seconds) {
    if (seconds === 0) return '0:00';
    const hours = Math.floor(seconds / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);
    const secs = Math.floor(seconds % 60);
    
    if (hours > 0) {
        return `${hours}:${minutes.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    } else {
        return `${minutes}:${secs.toString().padStart(2, '0')}`;
    }
}

function showToast(message, type = 'info') {
    // Create toast container if it doesn't exist
    let container = document.getElementById('toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toast-container';
        container.className = 'toast-container position-fixed top-0 end-0 p-3';
        container.style.zIndex = '9999';
        document.body.appendChild(container);
    }

    // Create toast element
    const toastId = 'toast-' + Date.now();
    const toastHtml = `
        <div id="${toastId}" class="toast align-items-center text-white bg-${type === 'danger' ? 'danger' : type === 'success' ? 'success' : type === 'warning' ? 'warning' : 'primary'} border-0" role="alert" aria-live="assertive" aria-atomic="true">
            <div class="d-flex">
                <div class="toast-body">
                    ${message}
                </div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>
            </div>
        </div>
    `;
    
    container.insertAdjacentHTML('beforeend', toastHtml);
    
    // Show toast
    const toastElement = document.getElementById(toastId);
    const toast = new bootstrap.Toast(toastElement, {
        autohide: true,
        delay: 5000
    });
    
    toast.show();
    
    // Remove from DOM after hiding
    toastElement.addEventListener('hidden.bs.toast', () => {
        toastElement.remove();
    });
}

function showLoading(button, text = 'Processing...') {
    const originalText = button.innerHTML;
    button.innerHTML = `<span class="spinner-border spinner-border-sm me-2" role="status" aria-hidden="true"></span>${text}`;
    button.disabled = true;
    
    return function hideLoading() {
        button.innerHTML = originalText;
        button.disabled = false;
    };
}