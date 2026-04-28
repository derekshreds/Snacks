/**
 * Library browser modal.
 *
 * Owns directory navigation, file selection, and queue submission. Lets the
 * user drill into the library tree, pick individual video files or whole
 * folders, and enqueue them for transcoding using whatever encoder options
 * are currently in the settings form.
 *
 * Integrations:
 *   - {@link encoderForm.getEncoderOptions} for the active settings snapshot.
 *   - {@link autoScanApi.addDir} via the per-row / per-folder "Watch" buttons;
 *     the composition root subscribes to `onWatchAdded` so the Auto-Scan
 *     panel can refresh its list of watched directories afterwards.
 */

import { libraryApi, autoScanApi }    from '../api.js';
import { escapeHtml }                 from '../utils/dom.js';
import { openModal, closeModal }      from '../utils/modal-controller.js';
import { getEncoderOptions }          from '../settings/encoder-form.js';


// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

const MODAL_ID = 'libraryModal';


// ---------------------------------------------------------------------------
// LibraryBrowser
// ---------------------------------------------------------------------------

export class LibraryBrowser {

    /**
     * @param {object} deps
     * @param {() => void} deps.onWatchAdded
     *        Called after a directory is successfully added to auto-scan.
     * @param {(path: string, recursive: boolean) => void} [deps.onAnalyzeRequested]
     *        Called when the user clicks the "Analyze (Dry Run)" button. The
     *        composition root forwards this to the AnalyzeModal.
     */
    constructor({ onWatchAdded, onAnalyzeRequested } = {}) {
        this._currentDir         = null;
        this._rootDir            = null;
        this._selectedFiles      = new Set();
        this._onWatchAdded       = onWatchAdded       ?? (() => {});
        this._onAnalyzeRequested = onAnalyzeRequested ?? (() => {});
    }


    // ---- Init ----

    /**
     * Wires the button handlers that open the modal and trigger queue
     * submissions. Safe to call once at startup.
     */
    init() {
        document.getElementById('openLibraryBtn')?.addEventListener('click', () => {
            openModal(MODAL_ID);
            this.loadDirectories();
        });

        document.getElementById('selectAllFiles')      ?.addEventListener('click', () => this._selectAllFiles());
        document.getElementById('processSelectedFiles')?.addEventListener('click', () => this._processSelectedFiles());
        document.getElementById('processDirectory')    ?.addEventListener('click', () => this.processCurrentDirectory());
    }


    // ---- Directory navigation ----

    /**
     * Loads and renders the list of library root directories. Called on
     * modal open and on reset.
     */
    async loadDirectories() {
        const container = document.getElementById('directoryList');
        const fileList  = document.getElementById('fileList');

        if (fileList) {
            fileList.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-folder-open fa-2x mb-2"></i><br>Select a directory to view files</div>';
        }

        this._currentDir = null;
        this._selectedFiles.clear();
        this._updateProcessButton();

        try {
            container.innerHTML = '<div class="text-center"><div class="spinner-border" role="status"><span class="visually-hidden">Loading...</span></div></div>';

            const data = await libraryApi.getDirectories();
            this._rootDir = data.rootPath;

            if (data.directories.length === 0) {
                container.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-folder-open fa-2x mb-2"></i><br>No video directories found<br><small>Mount your video library to /app/work/uploads</small></div>';
                return;
            }

            container.innerHTML = data.directories.map(dir => `
                <div class="directory-item p-2 border-bottom d-flex justify-content-between align-items-center">
                    <div class="flex-grow-1" data-path="${escapeHtml(dir.path)}" data-count="${dir.videoCount}" style="cursor: pointer;">
                        <i class="fas ${dir.videoCount === 0 ? 'fa-hdd' : 'fa-folder'} text-warning me-2"></i>
                        <span>${escapeHtml(dir.name)}</span>
                        ${dir.videoCount > 0 ? `<small class="text-muted ms-2">${dir.videoCount} videos</small>` : ''}
                    </div>
                    <button class="btn btn-sm btn-link p-0 ms-2 watch-dir-btn" data-path="${escapeHtml(dir.path)}" title="Watch (Auto-Scan)">
                        <i class="fas fa-eye" style="color: var(--primary); opacity: 0.6;"></i>
                    </button>
                </div>`).join('');

            container.querySelectorAll('.directory-item .flex-grow-1[data-path]').forEach(item => {
                item.addEventListener('click', () =>
                    this.loadSubdirectories(item.getAttribute('data-path')));
            });

            container.querySelectorAll('.watch-dir-btn').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    e.stopPropagation();
                    this._watchDirectory(btn.getAttribute('data-path'));
                });
            });
        } catch (err) {
            container.innerHTML = `<div class="alert alert-danger">Error loading directories: ${escapeHtml(err.message)}</div>`;
        }
    }

    /**
     * Loads and renders the immediate subdirectories of `directoryPath`,
     * plus the list of files in that directory (shallow).
     *
     * @param {string} directoryPath
     */
    async loadSubdirectories(directoryPath) {

        // Normalize bare Windows drive letters (e.g. "D:") to include a trailing backslash.
        if (/^[A-Za-z]:$/.test(directoryPath)) directoryPath += '\\';

        this._currentDir = directoryPath;
        const container = document.getElementById('directoryList');

        try {
            container.innerHTML = '<div class="text-center"><div class="spinner-border" role="status"></div></div>';
            const data = await libraryApi.getSubdirectories(directoryPath);

            let html = '';

            // Header rows: back, process this folder (shallow), process with subfolders, watch.
            html += `<div class="directory-item p-2 border-bottom" id="dirBack" style="cursor:pointer;">
                <i class="fas fa-arrow-left text-muted me-2"></i><span class="text-muted">Back</span>
            </div>`;
            html += `<div class="directory-item p-2 border-bottom bg-success bg-opacity-10" id="dirProcess" style="cursor:pointer;">
                <i class="fas fa-play text-success me-2"></i><span>Process This Folder</span>
            </div>`;
            html += `<div class="directory-item p-2 border-bottom bg-success bg-opacity-10" id="dirProcessRecursive" style="cursor:pointer;">
                <i class="fas fa-layer-group text-success me-2"></i><span>Process Folder + Subfolders</span>
            </div>`;
            html += `<div class="directory-item p-2 border-bottom" id="dirAnalyze" style="cursor:pointer; opacity: 0.8;">
                <i class="fas fa-search me-2" style="color: var(--bs-info);"></i><span>Analyze (Dry Run)</span>
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
                    </div>`).join('');
            }

            container.innerHTML = html;

            document.getElementById('dirBack').addEventListener('click', () => {
                if (data.parentPath) this.loadSubdirectories(data.parentPath);
                else                 this.loadDirectories();
            });
            document.getElementById('dirProcess')         .addEventListener('click', () => this.processCurrentDirectory(false));
            document.getElementById('dirProcessRecursive').addEventListener('click', () => this.processCurrentDirectory(true));
            document.getElementById('dirAnalyze')         .addEventListener('click', () => this.analyzeCurrentDirectory(true));
            document.getElementById('dirWatch')           .addEventListener('click', () => this._watchDirectory(directoryPath));

            container.querySelectorAll('.directory-item[data-path]').forEach(item => {
                item.addEventListener('click', () =>
                    this.loadSubdirectories(item.getAttribute('data-path')));
            });

            this._loadDirectoryFilesShallow(directoryPath);
        } catch (err) {
            container.innerHTML = `<div class="alert alert-danger">Error: ${escapeHtml(err.message)}</div>`;
        }
    }


    // ---- File list ----

    /**
     * Populates the file panel with the immediate video files of
     * `directoryPath` (shallow only — subfolder files don't appear here).
     *
     * @param {string} directoryPath
     */
    async _loadDirectoryFilesShallow(directoryPath) {
        this._currentDir = directoryPath;
        this._selectedFiles.clear();

        const container = document.getElementById('fileList');

        try {
            container.innerHTML = '<div class="text-center py-4"><div class="spinner-border" role="status"></div></div>';
            const data = await libraryApi.getFiles(directoryPath, false);

            if (data.files.length === 0) {
                container.innerHTML = '<div class="text-muted text-center py-4"><i class="fas fa-file-video fa-2x mb-2"></i><br>No video files in this folder</div>';
                return;
            }

            container.innerHTML = data.files.map(file => `
                <div class="file-item p-2 border-bottom" data-path="${escapeHtml(file.path)}">
                    <div class="form-check d-flex align-items-center">
                        <input class="form-check-input me-3" type="checkbox" value="${escapeHtml(file.path)}" id="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                        <label class="form-check-label w-100" for="file-${file.path.replace(/[^a-zA-Z0-9]/g, '_')}">
                            <div class="d-flex justify-content-between align-items-center">
                                <div>
                                    <i class="fas fa-file-video text-primary me-2"></i>
                                    <strong>${escapeHtml(file.name)}</strong>
                                </div>
                                <small class="text-muted">${formatFileSize(file.size)}</small>
                            </div>
                        </label>
                    </div>
                </div>`).join('');

            container.querySelectorAll('input[type="checkbox"]').forEach(checkbox => {
                checkbox.addEventListener('change', () => {
                    if (checkbox.checked) this._selectedFiles.add(checkbox.value);
                    else                  this._selectedFiles.delete(checkbox.value);
                    this._updateProcessButton();
                });
            });
        } catch (err) {
            container.innerHTML = `<div class="alert alert-danger">Error: ${escapeHtml(err.message)}</div>`;
        }
    }

    /**
     * Toggles "select all" / "deselect all" for every visible checkbox.
     */
    _selectAllFiles() {
        const checkboxes  = document.querySelectorAll('#fileList input[type="checkbox"]');
        const allSelected = Array.from(checkboxes).every(cb => cb.checked);

        checkboxes.forEach(cb => {
            cb.checked = !allSelected;
            if (cb.checked) this._selectedFiles.add(cb.value);
            else            this._selectedFiles.delete(cb.value);
        });

        this._updateProcessButton();

        const btn = document.getElementById('selectAllFiles');
        btn.innerHTML = allSelected
            ? '<i class="fas fa-check-square me-1"></i> Select All'
            : '<i class="fas fa-square me-1"></i> Deselect All';
    }

    /**
     * Refreshes the enabled-state + label of the "Process Selected" button
     * from the current selection.
     */
    _updateProcessButton() {
        const btn = document.getElementById('processSelectedFiles');
        btn.disabled  = this._selectedFiles.size === 0;
        btn.innerHTML = `<i class="fas fa-play me-1"></i> Process Selected (${this._selectedFiles.size})`;
    }


    // ---- Queue submission ----

    /**
     * Closes the modal and enqueues every currently-selected file using the
     * encoder options from the settings form. Successful adds are counted
     * and reported via a trailing toast.
     */
    async _processSelectedFiles() {
        if (this._selectedFiles.size === 0) {
            showToast('No files selected', 'warning');
            return;
        }

        const options   = getEncoderOptions('settings');
        const fileCount = this._selectedFiles.size;
        const filePaths = [...this._selectedFiles];

        closeModal(MODAL_ID);
        this._selectedFiles.clear();
        showToast(`Scanning ${fileCount} file(s)...`, 'info');

        let successCount = 0;
        for (const filePath of filePaths) {
            try {
                await libraryApi.processFile(filePath, options);
                successCount++;
            } catch (err) {
                console.error(`Failed to process ${filePath}:`, err);
            }
        }

        showToast(`Added ${successCount} file(s) to transcoding queue`, 'success');
    }

    /**
     * Closes the modal and enqueues every video file under the current (or
     * root) directory.
     *
     * @param {boolean} [recursive=true] Descend into subfolders when true.
     */
    async processCurrentDirectory(recursive = true) {
        const dirPath = this._currentDir || this._rootDir;
        if (!dirPath) {
            showToast('No directory available', 'warning');
            return;
        }

        const options = getEncoderOptions('settings');
        const dirName = this._currentDir
            ? (this._currentDir.split(/[/\\]/).pop() || this._currentDir)
            : 'all directories';

        closeModal(MODAL_ID);

        // Let the modal's close animation paint before kicking off the scan,
        // otherwise the toast and the close animation fight for the frame.
        await new Promise(resolve => setTimeout(resolve, 100));

        showToast(`Scanning "${dirName}"${recursive ? ' (including subfolders)' : ''}...`, 'info');

        try {
            const result = await libraryApi.processDirectory(dirPath, recursive, options);
            showToast(result.message, 'success');
        } catch (err) {
            showToast('Error processing directory: ' + err.message, 'danger');
        }
    }

    /**
     * Hands the current directory off to the analyze (dry-run) modal so the
     * user can preview what would be queued before committing. The library
     * modal stays open behind the analyze modal so a Close on the analyze
     * modal returns the user to where they were.
     *
     * @param {boolean} [recursive=true] Descend into subfolders when true.
     */
    analyzeCurrentDirectory(recursive = true) {
        const dirPath = this._currentDir || this._rootDir;
        if (!dirPath) {
            showToast('No directory available', 'warning');
            return;
        }
        this._onAnalyzeRequested(dirPath, recursive);
    }


    // ---- Auto-scan hook ----

    /**
     * Adds `path` to the auto-scan watched list and invokes the
     * `onWatchAdded` callback so the panel that owns that list can refresh.
     *
     * @param {string} path
     */
    async _watchDirectory(path) {
        try {
            await autoScanApi.addDir(path);

            const label = path.split(/[/\\]/).pop() || path;
            showToast(`Added "${label}" to auto-scan`, 'success');

            this._onWatchAdded();
        } catch (err) {
            showToast('Error adding directory: ' + err.message, 'danger');
        }
    }
}
