using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers
{
    /// <summary>
    /// Primary MVC controller serving the main UI and all JSON API endpoints consumed
    /// by the front-end JavaScript. Handles job management, settings, directory browsing,
    /// auto-scan configuration, pause/resume, and cluster management.
    /// </summary>
    public class HomeController : Controller
    {
        private readonly TranscodingService _transcodingService;
        private readonly FileService _fileService;
        private readonly AutoScanService _autoScanService;
        private readonly MediaFileRepository _mediaFileRepo;
        private readonly ClusterService _clusterService;

        /// <summary> Initializes the controller with all required services. </summary>
        public HomeController(TranscodingService transcodingService, FileService fileService,
            AutoScanService autoScanService, MediaFileRepository mediaFileRepo,
            ClusterService clusterService)
        {
            _transcodingService = transcodingService;
            _fileService = fileService;
            _autoScanService = autoScanService;
            _mediaFileRepo = mediaFileRepo;
            _clusterService = clusterService;
        }

        /// <summary> Renders the main queue view, passing all current work items to the template. </summary>
        public IActionResult Index()
        {
            var workItems = _transcodingService.GetAllWorkItems();
            return View(workItems);
        }

        /// <summary> Returns a simple health check payload including the current version. </summary>
        [HttpGet]
        public IActionResult Health()
        {
            return Json(new {
                status = "healthy",
                timestamp = DateTime.UtcNow,
                version = "2.2.4"
            });
        }

        /// <summary>
        ///     Returns browsable top-level directories. In desktop mode, returns all ready drive
        ///     roots. In container mode, returns only subdirectories of the uploads folder that
        ///     contain at least one video file.
        /// </summary>
        [HttpGet]
        public IActionResult GetAvailableDirectories()
        {
            try
            {
                if (_fileService.AllowAllPaths())
                {
                    var directories = DriveInfo.GetDrives()
                        .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                        .Select(d => new
                        {
                            path = d.RootDirectory.FullName,
                            name = d.RootDirectory.FullName,
                            videoCount = 0
                        })
                        .OrderBy(d => d.name)
                        .ToList<object>();

                    return Json(new { directories, rootPath = "" });
                }

                var inputDir = _fileService.GetUploadsDirectory();
                var directories2 = new List<object>();

                if (Directory.Exists(inputDir))
                {
                    var topLevelDirs = Directory.GetDirectories(inputDir)
                        .Select(dir => new
                        {
                            path = dir,
                            name = Path.GetFileName(dir),
                            videoCount = 0
                        })
                        .OrderBy(d => d.name)
                        .ToList();

                    directories2.AddRange(topLevelDirs);
                }

                return Json(new { directories = directories2, rootPath = inputDir });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting directories: {ex}");
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        ///     Queues all video files in a directory for encoding. Validates that the directory
        ///     is within the allowed path when running in container mode.
        /// </summary>
        /// <param name="request"> The directory path, encoder options, and recursion flag. </param>
        [HttpPost]
        public async Task<IActionResult> ProcessDirectory([FromBody] ProcessDirectoryRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest("Request is null");
                }

                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return BadRequest("Directory path is required");
                }

                if (request.Options == null)
                {
                    return BadRequest("Encoder options are required");
                }

                if (!Directory.Exists(request.DirectoryPath))
                {
                    return BadRequest($"Directory does not exist: {request.DirectoryPath}");
                }

                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar);
                    var fullInputDir = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);

                    if (!fullRequestPath.StartsWith(fullInputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !fullRequestPath.Equals(fullInputDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest("Directory is not within allowed library path");
                    }
                }

                var result = await _transcodingService.AddDirectoryAsync(request.DirectoryPath, request.Options, request.Recursive);
                return Json(new { success = true, message = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process directory error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        ///     Queues a single file for encoding. The <c>force</c> flag bypasses the database
        ///     status check because the user explicitly selected the file, overriding any
        ///     prior failed, cancelled, or completed state.
        /// </summary>
        /// <param name="request"> The file path and encoder options. </param>
        [HttpPost]
        public async Task<IActionResult> ProcessSingleFile([FromBody] ProcessFileRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest("Request is null");
                }

                if (string.IsNullOrEmpty(request.FilePath))
                {
                    return BadRequest("File path is required");
                }

                if (request.Options == null)
                {
                    return BadRequest("Encoder options are required");
                }

                if (!System.IO.File.Exists(request.FilePath))
                {
                    return BadRequest($"File does not exist: {request.FilePath}");
                }

                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.FilePath);
                    var fullInputDir = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);

                    if (!fullRequestPath.StartsWith(fullInputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest("File is not within allowed library path");
                    }
                }

                // force: true overrides any prior failed/cancelled/completed status in the database.
                var workItemId = await _transcodingService.AddFileAsync(request.FilePath, request.Options, force: true);
                return Json(new { success = true, workItemId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process file error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Permanently cancels a work item. It will not be reprocessed unless manually reset. </summary>
        /// <param name="id"> The work item ID to cancel. </param>
        [HttpPost]
        public async Task<IActionResult> CancelWorkItem(string id)
        {
            try
            {
                await _transcodingService.CancelWorkItemAsync(id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Stops a work item and returns it to the Unseen state so it can be re-queued later. </summary>
        /// <param name="id"> The work item ID to stop. </param>
        [HttpPost]
        public async Task<IActionResult> StopWorkItem(string id)
        {
            try
            {
                await _transcodingService.StopWorkItemAsync(id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Resets a failed file's status so it can be re-added to the queue. </summary>
        /// <param name="request"> Contains the file path to retry. </param>
        [HttpPost]
        public async Task<IActionResult> RetryFailedFile([FromBody] RetryRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.FilePath))
                    return BadRequest("File path is required");

                await _transcodingService.RetryFileAsync(request.FilePath);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Returns all failed files from the database, ordered by failure count descending. </summary>
        [HttpGet]
        public async Task<IActionResult> GetFailedFiles()
        {
            try
            {
                var files = await _mediaFileRepo.GetFailedFilesAsync();
                return Json(files);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Returns the persisted FFmpeg log lines for a specific work item. </summary>
        /// <param name="id"> The work item ID. </param>
        [HttpGet]
        public IActionResult GetWorkItemLogs(string id)
        {
            var logs = _transcodingService.GetWorkItemLogs(id);
            return Json(logs);
        }

        /// <summary>
        ///     Returns paginated work items, always including all currently processing items
        ///     regardless of page boundaries. Non-processing items are sorted by status priority
        ///     (Pending, Completed, Failed, Cancelled) then by bitrate descending within each group.
        /// </summary>
        /// <param name="limit"> Maximum number of non-processing items to return. </param>
        /// <param name="skip"> Number of non-processing items to skip for pagination. </param>
        /// <param name="status"> Optional status filter applied before pagination. </param>
        [HttpGet]
        public IActionResult GetWorkItems(int? limit = null, int skip = 0, string? status = null)
        {
            var allItems = _transcodingService.GetAllWorkItems();

            // Processing items are always returned in full so they don't count against the page limit.
            var processingItems = allItems.Where(w => w.Status is WorkItemStatus.Processing
                or WorkItemStatus.Uploading or WorkItemStatus.Downloading).ToList();

            var queueItems = allItems.Where(w => w.Status is not WorkItemStatus.Processing
                and not WorkItemStatus.Uploading and not WorkItemStatus.Downloading).ToList();

            if (!string.IsNullOrEmpty(status))
            {
                if (Enum.TryParse<WorkItemStatus>(status, true, out var filterStatus))
                    queueItems = queueItems.Where(w => w.Status == filterStatus).ToList();
            }

            queueItems.Sort((a, b) =>
            {
                int StatusPriority(WorkItemStatus s) => s switch
                {
                    WorkItemStatus.Pending => 0,
                    WorkItemStatus.Completed => 1,
                    WorkItemStatus.Failed => 2,
                    WorkItemStatus.Cancelled => 3,
                    _ => 4
                };

                int cmp = StatusPriority(a.Status).CompareTo(StatusPriority(b.Status));
                if (cmp != 0) return cmp;

                // Higher bitrate files are more valuable to finish first within a status group.
                return b.Bitrate.CompareTo(a.Bitrate);
            });

            var total = queueItems.Count;
            queueItems = queueItems.Skip(skip).ToList();
            if (limit.HasValue)
                queueItems = queueItems.Take(limit.Value).ToList();
            return Json(new { items = queueItems, total, processing = processingItems });
        }

        /// <summary> Returns aggregate counts of work items grouped by status: pending, processing, completed, failed, and total. </summary>
        [HttpGet]
        public IActionResult GetWorkStats()
        {
            var workItems = _transcodingService.GetAllWorkItems();
            return Json(new
            {
                pending = workItems.Count(w => w.Status == WorkItemStatus.Pending),
                processing = workItems.Count(w => w.Status is WorkItemStatus.Processing
                    or WorkItemStatus.Uploading or WorkItemStatus.Downloading),
                completed = workItems.Count(w => w.Status == WorkItemStatus.Completed),
                failed = workItems.Count(w => w.Status == WorkItemStatus.Failed),
                total = workItems.Count
            });
        }

        /// <summary> Returns a single work item by ID, or 404 if not found. </summary>
        /// <param name="id"> The work item ID. </param>
        [HttpGet]
        public IActionResult GetWorkItem(string id)
        {
            var workItem = _transcodingService.GetWorkItem(id);
            if (workItem == null)
            {
                return NotFound();
            }
            return Json(workItem);
        }

        /// <summary> Returns the immediate child directories of a given path for the browser UI. </summary>
        /// <param name="directoryPath"> The parent directory to list. </param>
        [HttpGet]
        public IActionResult GetSubdirectories(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return BadRequest("Directory does not exist");

                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar);
                    var fullInputDir = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);
                    if (!fullRequestPath.StartsWith(fullInputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !fullRequestPath.Equals(fullInputDir, StringComparison.OrdinalIgnoreCase))
                        return BadRequest("Directory is not within allowed library path");
                }

                var dirs = Directory.GetDirectories(directoryPath)
                    .Select(d => new
                    {
                        path = d,
                        name = Path.GetFileName(d)
                    })
                    .OrderBy(d => d.name)
                    .ToArray();

                // Compute the parent path for back-navigation.
                // Returns null when at a filesystem root OR at the configured library root,
                // so the frontend knows to show the top-level directory listing.
                string? parentPath = null;
                var rawParent = Path.GetDirectoryName(directoryPath);
                if (rawParent != null)
                {
                    if (_fileService.AllowAllPaths())
                    {
                        parentPath = rawParent;
                    }
                    else
                    {
                        var inputDir = _fileService.GetUploadsDirectory();
                        var normalizedParent = Path.GetFullPath(rawParent).TrimEnd(Path.DirectorySeparatorChar);
                        var normalizedRoot = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);
                        // Only return a parent if it's strictly inside the library root.
                        // When parent IS the root, return null so the frontend shows the
                        // top-level filtered listing via loadDirectories() instead.
                        if (normalizedParent.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        {
                            parentPath = rawParent;
                        }
                    }
                }

                return Json(new { directories = dirs, parentPath });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { directories = Array.Empty<object>(), parentPath = (string?)null });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        ///     Returns all video files in a directory, optionally including subdirectories.
        ///     Paths are returned relative to the library root for display in the UI.
        /// </summary>
        /// <param name="directoryPath"> The directory to list video files from. </param>
        /// <param name="recursive"> When <see langword="true"/>, includes files in all subdirectories. </param>
        [HttpGet]
        public IActionResult GetDirectoryFiles(string directoryPath, bool recursive = true)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return BadRequest("Directory does not exist");
                }

                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar);
                    var fullInputDir = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);

                    if (!fullRequestPath.StartsWith(fullInputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !fullRequestPath.Equals(fullInputDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest("Directory is not within allowed library path");
                    }
                }

                // In NAS mode the scan is always recursive; desktop mode uses shallow browsing.
                var allDirs = new List<string> { directoryPath };
                if (recursive)
                    allDirs.AddRange(Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories));

                var relativeRoot = _fileService.AllowAllPaths() ? directoryPath : _fileService.GetUploadsDirectory();
                var videoFiles = _fileService.GetAllVideoFiles(allDirs)
                    .Select(f => new
                    {
                        path = f,
                        name = Path.GetFileName(f),
                        size = new FileInfo(f).Length,
                        modified = new FileInfo(f).LastWriteTime,
                        relativePath = Path.GetRelativePath(relativeRoot, f)
                    })
                    .OrderBy(f => f.relativePath)
                    .ToArray();

                return Json(new { files = videoFiles });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Get directory files error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Returns the total count of video files in a directory and all its subdirectories. </summary>
        /// <param name="directory"> The root directory to count from. </param>
        /// <returns> Video file count, or 0 if the directory is inaccessible. </returns>
        private int CountVideoFilesRecursive(string directory)
        {
            try
            {
                var allDirs = new List<string> { directory };
                allDirs.AddRange(Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));
                return _fileService.GetAllVideoFiles(allDirs).Count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary> Returns the absolute path to <c>settings.json</c>, creating the config directory if it does not yet exist. </summary>
        private string GetSettingsPath()
        {
            var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, "settings.json");
        }

        /// <summary>
        ///     Returns the current encoder settings as raw JSON. Falls back to the <c>.bak</c>
        ///     file if the primary settings file is corrupt, and returns an empty object if
        ///     neither file exists.
        /// </summary>
        [HttpGet]
        public IActionResult GetSettings()
        {
            var settingsPath = GetSettingsPath();
            var backupPath = settingsPath + ".bak";

            foreach (var path in new[] { settingsPath, backupPath })
            {
                if (!System.IO.File.Exists(path)) continue;
                try
                {
                    var json = System.IO.File.ReadAllText(path);
                    System.Text.Json.JsonDocument.Parse(json);
                    return Content(json, "application/json");
                }
                catch
                {
                    Console.WriteLine($"Settings file corrupted: {path}");
                }
            }
            return Json(new { });
        }

        /// <summary>
        ///     Atomically saves encoder settings using a write-then-rename pattern. Keeps a
        ///     <c>.bak</c> copy of the previous settings in case the new file is ever found
        ///     to be corrupt on the next read.
        /// </summary>
        /// <param name="settings"> The settings object to serialize and persist. </param>
        [HttpPost]
        public IActionResult SaveSettings([FromBody] System.Text.Json.JsonElement settings)
        {
            try
            {
                var settingsPath = GetSettingsPath();
                var backupPath = settingsPath + ".bak";
                var tempPath = settingsPath + ".tmp";
                var jsonOptions = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNameCaseInsensitive = true
                };
                var json = System.Text.Json.JsonSerializer.Serialize(settings, jsonOptions);

                // Atomic write: write to .tmp, rename current to .bak, rename .tmp to settings.json
                System.IO.File.WriteAllText(tempPath, json);
                if (System.IO.File.Exists(settingsPath))
                    System.IO.File.Copy(settingsPath, backupPath, overwrite: true);
                System.IO.File.Move(tempPath, settingsPath, overwrite: true);

                // Update in-memory options so queued items pick up changes immediately
                try
                {
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<EncoderOptions>(json, jsonOptions);
                    if (parsed != null)
                        _transcodingService.UpdateOptions(parsed);
                }
                catch { } // Non-fatal — settings are still saved to disk

                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Returns the current auto-scan configuration. </summary>
        [HttpGet]
        public IActionResult GetAutoScanConfig()
        {
            return Json(_autoScanService.GetConfig());
        }

        /// <summary> Enables or disables the auto-scan background service. </summary>
        /// <param name="request"> Contains the desired enabled state. </param>
        [HttpPost]
        public IActionResult SetAutoScanEnabled([FromBody] AutoScanEnabledRequest request)
        {
            try
            {
                _autoScanService.SetEnabled(request.Enabled);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Adds a directory to the auto-scan watch list. </summary>
        /// <param name="request"> Contains the directory path to add. </param>
        [HttpPost]
        public IActionResult AddAutoScanDirectory([FromBody] AutoScanDirectoryRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Path))
                    return BadRequest("Path is required");

                if (!Directory.Exists(request.Path))
                    return BadRequest($"Directory does not exist: {request.Path}");

                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.Path).TrimEnd(Path.DirectorySeparatorChar);
                    var fullInputDir = Path.GetFullPath(inputDir).TrimEnd(Path.DirectorySeparatorChar);

                    if (!fullRequestPath.StartsWith(fullInputDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        && !fullRequestPath.Equals(fullInputDir, StringComparison.OrdinalIgnoreCase))
                        return BadRequest("Directory is not within allowed library path");
                }

                _autoScanService.AddDirectory(request.Path);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Removes a directory from the auto-scan watch list. </summary>
        /// <param name="request"> Contains the directory path to remove. </param>
        [HttpPost]
        public IActionResult RemoveAutoScanDirectory([FromBody] AutoScanDirectoryRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.Path))
                    return BadRequest("Directory path is required");

                _autoScanService.RemoveDirectory(request.Path);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Updates the auto-scan interval. Must be between 1 and 1440 minutes. </summary>
        /// <param name="request"> Contains the new interval in minutes. </param>
        [HttpPost]
        public IActionResult SetAutoScanInterval([FromBody] AutoScanIntervalRequest request)
        {
            try
            {
                if (request.IntervalMinutes < 1 || request.IntervalMinutes > 1440)
                    return BadRequest("Interval must be between 1 and 1440 minutes");

                _autoScanService.SetInterval(request.IntervalMinutes);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Runs an immediate scan outside the scheduled interval. </summary>
        [HttpPost]
        public IActionResult TriggerAutoScan()
        {
            _ = Task.Run(() => _autoScanService.TriggerScanNowAsync());
            return Json(new { success = true });
        }

        /// <summary> Resets all file statuses to Unseen so every file will be re-evaluated on the next scan. </summary>
        [HttpPost]
        public async Task<IActionResult> ClearAutoScanHistory()
        {
            try
            {
                await _autoScanService.ClearHistoryAsync();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary>
        ///     Pauses or resumes the encoding queue. In node mode, also pauses or resumes
        ///     accepting remote jobs from the master.
        /// </summary>
        /// <param name="request"> Contains the desired paused state. </param>
        [HttpPost]
        public IActionResult SetPaused([FromBody] PauseRequest request)
        {
            try
            {
                _transcodingService.SetPaused(request.Paused);
                _autoScanService.SetQueuePaused(request.Paused);

                if (_clusterService.IsNodeMode)
                    _clusterService.SetNodePaused(request.Paused);

                return Json(new { success = true, paused = _transcodingService.IsPaused });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Returns whether the queue is currently paused, either locally or by the master in node mode. </summary>
        [HttpGet]
        public IActionResult GetPausedState()
        {
            // In node mode, reflect node pause state (may be set by master)
            var paused = _transcodingService.IsPaused || _clusterService.IsNodePaused;
            return Json(new { paused });
        }

        /// <summary>
        ///     Returns the cluster configuration. The shared secret is never exposed; only a
        ///     <c>hasSecret</c> boolean is returned to indicate whether one is configured.
        /// </summary>
        [HttpGet]
        public IActionResult GetClusterConfig()
        {
            var config = _clusterService.GetConfig();
            // Never expose the raw secret over the unauthenticated UI API
            return Json(new
            {
                config.Enabled,
                config.Role,
                config.NodeName,
                hasSecret = !string.IsNullOrEmpty(config.SharedSecret),
                config.AutoDiscovery,
                config.LocalEncodingEnabled,
                config.MasterUrl,
                config.NodeTempDirectory,
                config.ManualNodes,
                config.HeartbeatIntervalSeconds,
                config.NodeTimeoutSeconds,
                config.NodeId
            });
        }

        /// <summary>
        ///     Saves and immediately applies a new cluster configuration. Preserves the existing
        ///     shared secret when none is provided, since the UI never echoes it back. A shared
        ///     secret is required to enable cluster mode.
        /// </summary>
        /// <param name="config"> The new cluster configuration to apply. </param>
        [HttpPost]
        public async Task<IActionResult> SaveClusterConfig([FromBody] ClusterConfig config)
        {
            try
            {
                // Preserve existing secret if none was sent (UI doesn't echo it back)
                if (string.IsNullOrEmpty(config.SharedSecret))
                    config.SharedSecret = _clusterService.GetConfig().SharedSecret;

                // Require a secret to enable cluster mode
                if (config.Enabled && config.Role != "standalone" && string.IsNullOrEmpty(config.SharedSecret))
                    return Json(new { success = false, error = "A shared secret is required to enable cluster mode." });

                await _clusterService.SaveConfigAndApplyAsync(config);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        /// <summary> Returns the list of known cluster nodes and their current status. </summary>
        [HttpGet]
        public IActionResult GetWorkers()
        {
            return Json(_clusterService.GetNodes());
        }

        /// <summary> Pauses or resumes a specific cluster node from the master UI. </summary>
        /// <param name="request"> Contains the node ID and desired paused state. </param>
        [HttpPost]
        public async Task<IActionResult> SetNodePaused([FromBody] NodePauseRequest request)
        {
            try
            {
                await _clusterService.SetRemoteNodePausedAsync(request.NodeId, request.Paused);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        /// <summary> Pauses or resumes local encoding on the master (remote dispatch continues). </summary>
        [HttpPost]
        public IActionResult SetLocalEncodingPaused([FromBody] PauseRequest request)
        {
            try
            {
                _clusterService.SetLocalEncodingEnabled(!request.Paused);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, error = ex.Message });
            }
        }

        /// <summary> Returns a summary of cluster status: role, node count, and the full node list. </summary>
        [HttpGet]
        public IActionResult GetClusterStatus()
        {
            var config = _clusterService.GetConfig();
            return Json(new
            {
                enabled = config.Enabled,
                role = config.Role,
                nodeName = config.NodeName,
                nodeId = config.NodeId,
                localEncodingEnabled = config.LocalEncodingEnabled,
                selfCapabilities = _clusterService.GetCapabilities(),
                localCompletedJobs = _transcodingService.LocalCompletedJobs,
                localFailedJobs = _transcodingService.LocalFailedJobs,
                nodeCount = _clusterService.GetNodes().Count + 1, // include self
                nodes = _clusterService.GetNodes()
            });
        }

        /// <summary>
        ///     Stops the active encode, cleans up partial output, then exits the process.
        ///     In Electron mode, the host detects the clean exit and relaunches the application.
        /// </summary>
        [HttpPost]
        public IActionResult Restart()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // Allow the HTTP response to complete before the process exits.

                await _transcodingService.StopAndClearQueue();

                Environment.Exit(0); // Electron detects clean exit and relaunches the application.
            });
            return Json(new { success = true, message = "Restarting..." });
        }

        /******************************************************************
         *  Node Settings
         ******************************************************************/

        /// <summary> Returns all per-node settings. </summary>
        [HttpGet]
        public IActionResult GetNodeSettings()
        {
            return Json(_clusterService.GetNodeSettingsConfig());
        }

        /// <summary> Saves settings for a single cluster node. </summary>
        [HttpPost]
        public IActionResult SaveNodeSettings([FromBody] NodeSettings settings)
        {
            try
            {
                if (string.IsNullOrEmpty(settings.NodeId))
                    return BadRequest("NodeId is required");

                if (settings.Only4K == true && settings.Exclude4K == true)
                    return BadRequest("Only4K and Exclude4K are mutually exclusive");

                _clusterService.SaveNodeSettings(settings);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Removes settings for a cluster node. </summary>
        [HttpPost]
        public IActionResult DeleteNodeSettings([FromBody] DeleteNodeSettingsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.NodeId))
                    return BadRequest("NodeId is required");

                _clusterService.DeleteNodeSettings(request.NodeId);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /******************************************************************
         *  Folder Settings
         ******************************************************************/

        /// <summary> Saves encoding overrides for a watched folder. </summary>
        [HttpPost]
        public IActionResult SaveFolderSettings([FromBody] SaveFolderSettingsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Path))
                    return BadRequest("Path is required");

                _autoScanService.SaveFolderSettings(request.Path, request.EncodingOverrides);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        /// <summary> Renders the error page view. </summary>
        public IActionResult Error()
        {
            return View();
        }
    }

    /// <summary> Request body for <see cref="HomeController.ProcessDirectory"/>. </summary>
    public class ProcessDirectoryRequest
    {
        /// <summary> Absolute path to the directory containing video files. </summary>
        public string DirectoryPath { get; set; } = "";

        /// <summary> When <c>true</c>, subdirectories are also scanned. </summary>
        public bool Recursive { get; set; } = true;

        /// <summary> Encoding options to apply to all files in the directory. </summary>
        public EncoderOptions Options { get; set; } = new();
    }

    /// <summary> Request body for <see cref="HomeController.ProcessSingleFile"/>. </summary>
    public class ProcessFileRequest
    {
        /// <summary> Absolute path to the video file to encode. </summary>
        public string FilePath { get; set; } = "";

        /// <summary> Encoding options to apply to this file. </summary>
        public EncoderOptions Options { get; set; } = new();
    }

    /// <summary> Request body for <see cref="HomeController.SetAutoScanEnabled"/>. </summary>
    public class AutoScanEnabledRequest
    {
        /// <summary> Whether auto-scanning should be enabled. </summary>
        public bool Enabled { get; set; }
    }

    /// <summary>
    ///     Request body for <see cref="HomeController.AddAutoScanDirectory"/>
    ///     and <see cref="HomeController.RemoveAutoScanDirectory"/>.
    /// </summary>
    public class AutoScanDirectoryRequest
    {
        /// <summary> The directory path to add or remove from the auto-scan list. </summary>
        public string Path { get; set; } = "";
    }

    /// <summary> Request body for <see cref="HomeController.SetAutoScanInterval"/>. </summary>
    public class AutoScanIntervalRequest
    {
        /// <summary> The new scan interval in minutes (1–1440). </summary>
        public int IntervalMinutes { get; set; }
    }

    /// <summary> Request body for <see cref="HomeController.SetPaused"/>. </summary>
    public class PauseRequest
    {
        /// <summary> Whether the encoding queue should be paused. </summary>
        public bool Paused { get; set; }
    }

    /// <summary> Request body for <see cref="HomeController.SetNodePaused"/>. </summary>
    public class NodePauseRequest
    {
        /// <summary> The ID of the cluster node to pause or resume. </summary>
        public string NodeId { get; set; } = "";

        /// <summary> Whether the node should be paused. </summary>
        public bool Paused { get; set; }
    }

    /// <summary> Request body for <see cref="HomeController.RetryFailedFile"/>. </summary>
    public class RetryRequest
    {
        /// <summary> Absolute path to the file that should be retried. </summary>
        public string FilePath { get; set; } = "";
    }

    /// <summary> Request body for <see cref="HomeController.DeleteNodeSettings"/>. </summary>
    public class DeleteNodeSettingsRequest
    {
        /// <summary> The NodeId to remove settings for. </summary>
        public string NodeId { get; set; } = "";
    }

    /// <summary> Request body for <see cref="HomeController.SaveFolderSettings"/>. </summary>
    public class SaveFolderSettingsRequest
    {
        /// <summary> Absolute path of the watched folder. </summary>
        public string Path { get; set; } = "";

        /// <summary> Encoding overrides to apply. Pass null to clear overrides. </summary>
        public EncoderOptionsOverride? EncodingOverrides { get; set; }
    }
}