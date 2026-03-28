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
        this.initializeSignalR();
        this.initializeEventHandlers();
        this.restoreSettings();
        this.loadWorkItems();
    }

    async initializeSignalR() {
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

        try {
            await this.connection.start();
            console.log("SignalR Connected");
            this.updateConnectionStatus(true);

            // Register lifecycle handlers only after a successful connection
            // to avoid duplicate handlers from failed connection retries
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
                console.log("SignalR Disconnected. Attempting to reconnect...");
                this.updateConnectionStatus(false);
                setTimeout(() => this.initializeSignalR(), 5000);
            });
        } catch (err) {
            console.error("SignalR Connection Error: ", err);
            this.updateConnectionStatus(false);
            setTimeout(() => this.initializeSignalR(), 5000);
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

        document.getElementById('processDirectory').addEventListener('click', () => {
            this.processCurrentDirectory();
        });

        // Save settings on every change inside the settings modal
        document.getElementById('settingsModal').addEventListener('change', () => {
            this.getEncoderOptions('settings');
        });
        document.getElementById('settingsModal').addEventListener('input', () => {
            this.getEncoderOptions('settings');
        });
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
                <div class="directory-item p-2 border-bottom" data-path="${dir.path}" data-count="${dir.videoCount}" style="cursor: pointer;">
                    <div class="d-flex justify-content-between align-items-center">
                        <div>
                            <i class="fas ${dir.videoCount === 0 ? 'fa-hdd' : 'fa-folder'} text-warning me-2"></i>
                            <span>${dir.name}</span>
                        </div>
                        ${dir.videoCount > 0 ? `<small class="text-muted">${dir.videoCount} videos</small>` : ''}
                    </div>
                </div>
            `).join('');

            container.innerHTML = directoriesHtml;

            // Add click handlers — always navigate into folder
            container.querySelectorAll('.directory-item').forEach(item => {
                item.addEventListener('click', () => {
                    this.loadSubdirectories(item.getAttribute('data-path'));
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
        const options = {
            Format: document.getElementById(`${prefix}Format`).value,
            Codec: codec,
            Encoder: codec === 'h265' ? 'libx265' : 'libx264',
            HardwareAcceleration: hwAccel,
            TargetBitrate: parseInt(document.getElementById(`${prefix}TargetBitrate`).value),
            TwoChannelAudio: document.getElementById(`${prefix}TwoChannelAudio`).checked,
            EnglishOnlyAudio: document.getElementById(`${prefix}EnglishOnlyAudio`).checked,
            EnglishOnlySubtitles: document.getElementById(`${prefix}EnglishOnlySubtitles`).checked,
            RemoveBlackBorders: document.getElementById(`${prefix}RemoveBlackBorders`).checked,
            DeleteOriginalFile: document.getElementById(`${prefix}DeleteOriginalFile`).checked,
            RetryOnFail: document.getElementById(`${prefix}RetryOnFail`).checked,
            StrictBitrate: document.getElementById(`${prefix}StrictBitrate`).checked
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

            set('Format', saved.Format);
            set('Codec', saved.Codec);
            set('HardwareAcceleration', saved.HardwareAcceleration);
            set('TargetBitrate', saved.TargetBitrate);
            set('TwoChannelAudio', saved.TwoChannelAudio);
            set('EnglishOnlyAudio', saved.EnglishOnlyAudio);
            set('EnglishOnlySubtitles', saved.EnglishOnlySubtitles);
            set('RemoveBlackBorders', saved.RemoveBlackBorders);
            set('DeleteOriginalFile', saved.DeleteOriginalFile);
            set('RetryOnFail', saved.RetryOnFail);
            set('StrictBitrate', saved.StrictBitrate);
        } catch { }
    }

    async loadWorkItems() {
        try {
            const skip = this.queuePage * this.queuePageSize;
            const [statsResponse, itemsResponse] = await Promise.all([
                fetch('/Home/GetWorkStats'),
                fetch(`/Home/GetWorkItems?limit=${this.queuePageSize}&skip=${skip}`)
            ]);

            const stats = await statsResponse.json();
            const data = await itemsResponse.json();
            const workItems = data.items;
            this.queueTotal = data.total;

            // Clear existing items
            this.workItems.clear();
            document.getElementById('workItemsContainer').innerHTML = '';
            document.getElementById('processingContainer').innerHTML = '';
            document.getElementById('processingSection').style.display = 'none';

            // Add each work item
            for (const workItem of workItems) {
                this.workItems.set(workItem.id, workItem);
                this.renderWorkItem(workItem);
            }

            // Update stats (desktop + mobile)
            this.updateStatCounters(stats);

            if (workItems.length === 0 && this.queuePage === 0) {
                document.getElementById('workItemsContainer').innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-inbox fa-2x mb-2"></i><br>No files in queue</div>';
            }

            this.renderPagination();
        } catch (error) {
            console.error('Error loading work items:', error);
            showToast('Error loading work items: ' + error.message, 'danger');
        }
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
                    <button class="btn btn-outline-secondary" ${page === 0 ? 'disabled' : ''} id="pagePrev">
                        <i class="fas fa-chevron-left"></i>
                    </button>
                    <button class="btn btn-outline-secondary disabled">${page + 1} / ${totalPages}</button>
                    <button class="btn btn-outline-secondary" ${page >= totalPages - 1 ? 'disabled' : ''} id="pageNext">
                        <i class="fas fa-chevron-right"></i>
                    </button>
                </div>
            </nav>
        `;

        document.getElementById('pagePrev')?.addEventListener('click', () => {
            if (this.queuePage > 0) { this.queuePage--; this.loadWorkItems(); }
        });
        document.getElementById('pageNext')?.addEventListener('click', () => {
            if (this.queuePage < totalPages - 1) { this.queuePage++; this.loadWorkItems(); }
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

    // Helper method to convert numeric status to string
    getStatusString(status) {
        const statusMap = {
            0: 'Pending',
            1: 'Processing', 
            2: 'Completed',
            3: 'Failed',
            4: 'Cancelled'
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

        // Remove empty state message if present
        const emptyMsg = queueContainer.querySelector('.text-muted.text-center');
        if (emptyMsg) emptyMsg.remove();

        let element = document.getElementById(`work-item-${workItem.id}`);

        if (!element) {
            element = document.createElement('div');
            element.id = `work-item-${workItem.id}`;
            element.className = 'work-item new';
        }

        // Move element to the correct container based on status
        if (statusString === 'Processing') {
            if (element.parentNode !== processingContainer) {
                // Remove existing children individually instead of innerHTML = ''
                // to avoid destroying elements that may be referenced elsewhere
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
                // Hide processing section if empty
                if (processingContainer.children.length === 0) {
                    processingSection.style.display = 'none';
                }
            }
            // Add to queue container if not already there
            if (!element.parentNode || element.parentNode !== queueContainer) {
                queueContainer.appendChild(element);
            }
        }

        element.className = `work-item ${statusString.toLowerCase()}`;
        element.innerHTML = this.getWorkItemHtml({...workItem, status: statusString});

        // Add event listeners
        const cancelBtn = element.querySelector('.cancel-btn');
        if (cancelBtn) {
            cancelBtn.addEventListener('click', () => this.cancelWorkItem(workItem.id));
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
                return '<button class="btn btn-sm btn-outline-danger cancel-btn" title="Cancel"><i class="fas fa-times"></i></button>';
            case 'Processing':
                return `
                    <div class="btn-group" role="group">
                        <button class="btn btn-sm btn-outline-danger cancel-btn" title="Cancel"><i class="fas fa-times"></i></button>
                        <button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>
                    </div>
                `;
            case 'Completed':
                return '<button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>';
            case 'Failed':
            case 'Cancelled':
                return '<button class="btn btn-sm btn-outline-info log-btn" title="View Log"><i class="fas fa-terminal"></i></button>';
            default:
                return '';
        }
    }

    async cancelWorkItem(id) {
        try {
            const response = await fetch(`/Home/CancelWorkItem?id=${id}`, {
                method: 'POST'
            });

            if (!response.ok) {
                throw new Error('Failed to cancel work item');
            }

            showToast('Work item cancelled', 'info');
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

        // Update log modal if it's open for this work item
        const logModal = document.getElementById('logModal');
        if (logModal.getAttribute('data-work-item-id') === workItemId) {
            this.updateLogModal(workItemId);
        }
    }

    showLog(workItemId) {
        const workItem = this.workItems.get(workItemId);
        const logModal = document.getElementById('logModal');
        const logModalTitle = logModal.querySelector('.modal-title');
        
        logModalTitle.innerHTML = `
            <i class="fas fa-terminal me-2"></i>
            Transcoding Log - ${workItem.fileName}
        `;
        
        logModal.setAttribute('data-work-item-id', workItemId);
        this.updateLogModal(workItemId);
        
        new bootstrap.Modal(logModal).show();
    }

    updateLogModal(workItemId) {
        const logContent = document.getElementById('logContent');
        const logs = this.logs.get(workItemId) || [];
        
        logContent.innerHTML = logs.map(log =>
            `<div class="log-entry"><span class="text-muted">[${log.timestamp.toLocaleTimeString()}]</span> ${escapeHtml(log.message)}</div>`
        ).join('');
        
        // Auto-scroll to bottom
        logContent.scrollTop = logContent.scrollHeight;
    }

    updateStatistics() {
        // Delegate to refreshStats which fetches real counts from the server
        this.refreshStats();
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