using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Snacks.Services;

/// <summary>
///     Core transcoding service that manages the in-memory work queue, drives FFmpeg encoding,
///     handles hardware acceleration detection and calibration, and supports cluster integration
///     for distributed encoding across master and worker nodes.
/// </summary>
public class TranscodingService
    {
        /// <summary>All known work items keyed by ID, including completed and failed items.</summary>
        private readonly ConcurrentDictionary<string, WorkItem> _workItems = new();

        /// <summary>Ordered list of pending work items; sorted by bitrate descending.</summary>
        private readonly List<WorkItem> _workQueue = new();

        /// <summary>Lock protecting access to <see cref="_workQueue"/>.</summary>
        private readonly object _queueLock = new();

        private readonly FileService _fileService;
        private readonly FfprobeService _ffprobeService;
        private readonly IHubContext<TranscodingHub> _hubContext;
        private readonly MediaFileRepository _mediaFileRepo;

        /// <summary>Path to the FFmpeg binary, resolved from the <c>FFMPEG_PATH</c> environment variable.</summary>
        private readonly string _ffmpegPath;

        /// <summary>Ensures only one local encoding job runs at a time.</summary>
        private readonly SemaphoreSlim _processingLock = new(1, 1);

        /// <summary>Lock protecting access to <see cref="_activeProcess"/> and <see cref="_activeWorkItem"/>.</summary>
        private readonly object _activeLock = new();

        /// <summary>The currently running FFmpeg process, or <c>null</c> when idle.</summary>
        private Process? _activeProcess;

        /// <summary>The work item currently being encoded locally, or <c>null</c> when idle.</summary>
        private WorkItem? _activeWorkItem;

        /// <summary>Whether the local processing loop is paused by user request.</summary>
        private volatile bool _isPaused = false;

        /// <summary>Whether local encoding is suspended so the cluster dispatch loop can handle items instead.</summary>
        private volatile bool _localEncodingPaused = false;

        /// <summary>The encoder options from the most recently started queue run. Used when resuming after unpause.</summary>
        private EncoderOptions? _lastOptions;

        /// <summary>Optional callback to forward encoding progress to the master node.</summary>
        private Func<string, int, Task>? _progressCallback;

        /// <summary>Optional callback to forward FFmpeg log lines to the master node.</summary>
        private Func<string, string, Task>? _logCallback;

        /// <summary>Optional callback to cancel a remote job on a cluster node.</summary>
        private Func<string, string, Task>? _remoteJobCanceller;

        /// <summary>Optional async callback for the cluster to check whether a file is already assigned remotely.</summary>
        private Func<string, Task<bool>>? _isRemoteJobChecker;

        /// <summary>Whether the encoding queue is paused by user request.</summary>
        public bool IsPaused => _isPaused;

        /// <summary>
        /// Pauses or resumes the encoding queue. When resuming, restarts the processing loop
        /// if <see cref="_lastOptions"/> is available.
        /// </summary>
        /// <param name="paused">Whether to pause processing.</param>
        public void SetPaused(bool paused)
        {
            _isPaused = paused;
            Console.WriteLine($"Queue {(paused ? "paused" : "resumed")}");

            if (!paused && _lastOptions != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await ProcessQueueAsync(_lastOptions); }
                    catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync: {ex.Message}"); }
                });
            }
        }

        /// <summary>
        /// Initializes the service and eagerly starts hardware acceleration detection in the background
        /// so that the first queue item doesn't pay the detection cost.
        /// </summary>
        public TranscodingService(FileService fileService, FfprobeService ffprobeService, IHubContext<TranscodingHub> hubContext, MediaFileRepository mediaFileRepo)
        {
            _fileService = fileService;
            _ffprobeService = ffprobeService;
            _hubContext = hubContext;
            _mediaFileRepo = mediaFileRepo;
            _ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";

            _ = Task.Run(async () =>
            {
                try { await DetectHardwareAccelerationAsync(); }
                catch { }
            });
        }

        /// <summary>
        /// Sends a log line to all SignalR clients, forwards it to the master node if this is
        /// a remote job, and appends it to the per-item log file on disk.
        /// </summary>
        private async Task LogAsync(string workItemId, string message)
        {
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItemId, message);

            if (_logCallback != null)
                _ = _logCallback(workItemId, message);

            // Log files are named after the source video so they can be matched by eye when browsing the logs directory.
            try
            {
                var logPath = GetLogFilePath(workItemId);
                if (logPath != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                    await File.AppendAllTextAsync(logPath, $"[{DateTime.Now:HH:mm:ss}] {message}\n");
                }
            }
            catch { }
        }

        /// <summary>Returns all persisted log lines for a work item, or an empty list if none exist.</summary>
        /// <param name="workItemId">The work item ID to retrieve logs for.</param>
        public List<string> GetWorkItemLogs(string workItemId)
        {
            var logPath = GetLogFilePath(workItemId);
            if (logPath != null && File.Exists(logPath))
            {
                try { return File.ReadAllLines(logPath).ToList(); }
                catch { }
            }
            return new List<string>();
        }

        /// <summary>
        /// Returns the log file path for a work item, named after the video file for easy browsing.
        /// Returns <c>null</c> if the work item ID is not found in the dictionary.
        /// </summary>
        private string? GetLogFilePath(string workItemId)
        {
            if (!_workItems.TryGetValue(workItemId, out var workItem))
                return null;

            var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");
            var safeName = string.Join("_", _fileService.RemoveExtension(workItem.FileName).Split(Path.GetInvalidFileNameChars()));
            var shortId = workItemId.Length > 8 ? workItemId[..8] : workItemId;
            return Path.Combine(logsDir, $"{safeName}_{shortId}.log");
        }

        /// <summary>
        /// Probes a file, checks skip conditions (already target codec, 4K skip, VAAPI limits),
        /// performs DB change detection, and enqueues the file for encoding if eligible.
        /// Starts the processing loop in the background.
        /// </summary>
        /// <param name="filePath">Absolute path to the video file.</param>
        /// <param name="options">Encoder options for this job.</param>
        /// <param name="force">When <c>true</c>, bypasses DB status checks — used for explicit user selection.</param>
        /// <returns>The work item ID (may be a previously existing ID if the file was already tracked).</returns>
        public async Task<string> AddFileAsync(string filePath, EncoderOptions options, bool force = false)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var probe = await _ffprobeService.ProbeAsync(filePath);

                var length = _ffprobeService.GetVideoDuration(probe);
                var isHevc = false;
                string sourceCodec = "unknown";

                foreach (var stream in probe.Streams)
                {
                    if (stream.CodecType == "video")
                    {
                        isHevc = stream.CodecName == "hevc";
                        sourceCodec = stream.CodecName ?? "unknown";
                        break;
                    }
                }

                long bitrate = length > 0 ? (long)(fileInfo.Length * 8 / length / 1000) : 0;

                var workItem = new WorkItem
                {
                    FileName = _fileService.GetFileName(filePath),
                    Path = filePath,
                    Size = fileInfo.Length,
                    Bitrate = bitrate,
                    Length = length,
                    IsHevc = isHevc,
                    Probe = probe
                };

                // Don't add items that already meet the requirements.
                bool targetIsHevc = options.Encoder.Contains("265");
                bool targetIsAv1 = options.Encoder.Contains("av1") || options.Encoder.Contains("svt");
                bool isAv1 = sourceCodec == "av1";
                bool alreadyTargetCodec = targetIsAv1 ? isAv1 : (targetIsHevc ? isHevc : !isHevc);
                bool isHighDef = probe.Streams.Any(s => s.CodecType == "video" && s.Width > 1920);

                var videoStream = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
                var normalizedPath = Path.GetFullPath(filePath);

                async Task MarkSkippedInDb()
                {
                    await _mediaFileRepo.UpsertAsync(new MediaFile
                    {
                        FilePath = normalizedPath,
                        Directory = Path.GetDirectoryName(normalizedPath) ?? "",
                        FileName = Path.GetFileName(normalizedPath),
                        BaseName = Path.GetFileNameWithoutExtension(normalizedPath),
                        FileSize = fileInfo.Length,
                        Bitrate = bitrate,
                        Codec = sourceCodec,
                        Width = videoStream?.Width ?? 0,
                        Height = videoStream?.Height ?? 0,
                        PixelFormat = videoStream?.PixFmt,
                        Duration = length,
                        IsHevc = isHevc,
                        Is4K = isHighDef,
                        Status = MediaFileStatus.Skipped,
                        LastScannedAt = DateTime.UtcNow,
                        FileMtime = fileInfo.LastWriteTimeUtc.Ticks
                    });
                }

                if (options.Skip4K && isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: 4K video (Skip 4K enabled)");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                string targetCodecLabel = targetIsAv1 ? "AV1" : (isHevc ? "HEVC" : "H.264");
                if (alreadyTargetCodec && bitrate > 0 && bitrate <= options.TargetBitrate * 1.2 && !isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {targetCodecLabel} at {bitrate}kbps (target {options.TargetBitrate}kbps)");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                int fourKMultiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
                if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= options.TargetBitrate * fourKMultiplier)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {targetCodecLabel} 4K at {bitrate}kbps (target {options.TargetBitrate}kbps)");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                // Skip low-bitrate non-HEVC files when using VAAPI CQP — it can't target specific bitrates.
                // Check both explicit VAAPI selection and "auto" (which resolves to VAAPI on Linux NAS).
                bool isVaapiMode = IsVaapiAcceleration(options.HardwareAcceleration) ||
                    (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                     _detectedHardware != null && IsVaapiAcceleration(_detectedHardware));
                if (isVaapiMode && !isHevc && targetIsHevc && bitrate > 0 && bitrate <= options.TargetBitrate && !isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: VAAPI can't compress {bitrate}kbps H.264 below target");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                Console.WriteLine($"Queuing {workItem.FileName}: {sourceCodec} {bitrate}kbps {(isHighDef ? "4K" : "HD")}");

                if (_workItems.Values.Any(w =>
                    Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing))
                {
                    return workItem.Id;
                }

                if (_isRemoteJobChecker != null && await _isRemoteJobChecker(normalizedPath))
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already active as a remote job");
                    return workItem.Id;
                }

                var dbFile = await _mediaFileRepo.GetByPathAsync(normalizedPath);
                if (dbFile != null)
                {
                    // Change detection: if the file's size changed significantly or duration differs,
                    // it's been replaced with a different file — treat as new.
                    // Small changes (metadata edits, remux) are ignored to avoid false positives.
                    double sizeDelta = dbFile.FileSize > 0 ? Math.Abs(1.0 - (double)fileInfo.Length / dbFile.FileSize) : 0;
                    double durationDelta = dbFile.Duration > 0 && length > 0 ? Math.Abs(dbFile.Duration - length) : 0;
                    bool fileChanged = sizeDelta > 0.10 || durationDelta > 30; // >10% size change or >30s duration change

                    if (fileChanged)
                    {
                        Console.WriteLine($"File changed on disk: {workItem.FileName} (size: {dbFile.FileSize}→{fileInfo.Length}) — resetting");
                        await _mediaFileRepo.ResetFileAsync(normalizedPath);
                    }
                    else if (!force && dbFile.Status is MediaFileStatus.Failed or MediaFileStatus.Cancelled)
                    {
                        Console.WriteLine($"Skipping {workItem.FileName}: previously {dbFile.Status} ({dbFile.FailureCount} failures)");
                        return workItem.Id;
                    }
                    else if (!force && dbFile.Status is MediaFileStatus.Completed)
                    {
                        Console.WriteLine($"Skipping {workItem.FileName}: already completed");
                        return workItem.Id;
                    }
                }

                // Force-add must clear any stale in-memory entry so AddFileAsync won't skip it on the duplicate path check.
                if (force)
                {
                    var existing = _workItems.Values.FirstOrDefault(w =>
                        Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                        _workItems.TryRemove(existing.Id, out _);
                }

                await _mediaFileRepo.UpsertAsync(new MediaFile
                {
                    FilePath = normalizedPath,
                    Directory = Path.GetDirectoryName(normalizedPath) ?? "",
                    FileName = Path.GetFileName(normalizedPath),
                    BaseName = Path.GetFileNameWithoutExtension(normalizedPath),
                    FileSize = fileInfo.Length,
                    Bitrate = bitrate,
                    Codec = sourceCodec,
                    Width = videoStream?.Width ?? 0,
                    Height = videoStream?.Height ?? 0,
                    PixelFormat = videoStream?.PixFmt,
                    Duration = length,
                    IsHevc = isHevc,
                    Is4K = isHighDef,
                    Status = MediaFileStatus.Queued,
                    LastScannedAt = DateTime.UtcNow,
                    FileMtime = fileInfo.LastWriteTimeUtc.Ticks
                });

                _workItems[workItem.Id] = workItem;
                lock (_queueLock)
                {
                    _workQueue.Add(workItem);
                    _workQueue.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));
                }

                await _hubContext.Clients.All.SendAsync("WorkItemAdded", workItem);

                // Always try to start queue processing — the semaphore ensures only one runs at a time
                _ = Task.Run(async () =>
                {
                    try { await ProcessQueueAsync(options); }
                    catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync: {ex.Message}"); }
                });

                return workItem.Id;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add file: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Adds all video files in a directory to the encoding queue.
        /// Files are probed sequentially to avoid overwhelming NAS storage.
        /// Encoding starts as soon as the first file is scanned — no waiting for the full batch.
        /// </summary>
        /// <param name="directoryPath">The directory to scan for video files.</param>
        /// <param name="options">Encoder options to apply to all files.</param>
        /// <param name="recursive">When <c>true</c>, subdirectories are also scanned.</param>
        /// <returns>A summary message with the count of files added.</returns>
        public async Task<string> AddDirectoryAsync(string directoryPath, EncoderOptions options, bool recursive = true)
        {
            List<string> directories;
            if (recursive)
            {
                directories = _fileService.RecursivelyFindDirectories(directoryPath);
            }
            else
            {
                directories = new List<string> { directoryPath };
            }
            var videoFiles = _fileService.GetAllVideoFiles(directories);

            // Probe files sequentially to avoid overwhelming NAS storage.
            // Each file triggers queue processing via AddFileAsync, so encoding
            // starts as soon as the first file is scanned — no waiting for the full scan.
            int addedCount = 0;
            foreach (var file in videoFiles)
            {
                try
                {
                    await AddFileAsync(file, options);
                    addedCount++;
                }
                catch (Exception ex) { Console.WriteLine($"Failed to add {file}: {ex.Message}"); }
            }

            return $"Added {addedCount} files from directory";
        }

        /// <summary>Returns the work item with the specified ID, or <c>null</c> if not found.</summary>
        /// <param name="id">The work item ID.</param>
        public WorkItem? GetWorkItem(string id)
        {
            _workItems.TryGetValue(id, out var workItem);
            return workItem;
        }

        /// <summary>
        ///     Replaces a work item's key in the tracking dictionary, used during crash recovery
        ///     to reassign a persisted job ID from a previous run.
        /// </summary>
        /// <param name="oldId"> The existing key to remove. </param>
        /// <param name="newId"> The replacement key to insert under. </param>
        /// <param name="workItem"> The work item whose <c>Id</c> property will also be updated. </param>
        public void ReplaceWorkItemId(string oldId, string newId, WorkItem workItem)
        {
            _workItems.TryRemove(oldId, out _);
            workItem.Id = newId;
            _workItems[newId] = workItem;
        }

        /// <summary>Returns <c>true</c> if the specified file path is currently Pending or Processing in the queue.</summary>
        /// <param name="filePath">The file path to check.</param>
        public bool IsFileQueued(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            return _workItems.Values.Any(w =>
                Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing);
        }

        /// <summary>Returns all known work items ordered by creation time descending.</summary>
        public List<WorkItem> GetAllWorkItems()
        {
            return _workItems.Values.OrderByDescending(x => x.CreatedAt).ToList();
        }

        /// <summary>Removes a work item from the in-memory tracking dictionary (e.g. after remote job cleanup).</summary>
        public void RemoveWorkItem(string id) => _workItems.TryRemove(id, out _);


        /// <summary>
        ///     Permanently cancels a work item. The file will not be reprocessed unless explicitly reset.
        ///     For remote items, the cancellation is forwarded to the assigned cluster node.
        /// </summary>
        /// <param name="id"> The ID of the work item to cancel. </param>
        public async Task CancelWorkItemAsync(string id)
        {
            if (!_workItems.TryGetValue(id, out var workItem))
                return;

            if (workItem.Status == WorkItemStatus.Pending)
            {
                workItem.Status = WorkItemStatus.Cancelled;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
            else if (workItem.Status == WorkItemStatus.Processing && workItem.IsRemote)
            {
                if (_remoteJobCanceller != null && workItem.AssignedNodeId != null)
                    await _remoteJobCanceller.Invoke(id, workItem.AssignedNodeId);
                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
            else if (workItem.Status == WorkItemStatus.Processing && _activeWorkItem?.Id == id)
            {
                await KillActiveProcess(workItem, "Encoding cancelled by user.");
                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
            else if (workItem.Status == WorkItemStatus.Processing)
            {
                // Orphaned: processing but not assigned to a remote node or local encoder
                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
        }

        /// <summary>
        ///     Stops a work item and returns it to an unprocessed state so it can be requeued on the
        ///     next scan or manual add. Unlike cancellation, stopped items are not permanently excluded.
        /// </summary>
        /// <param name="id"> The ID of the work item to stop. </param>
        public async Task StopWorkItemAsync(string id)
        {
            if (!_workItems.TryGetValue(id, out var workItem))
                return;

            if (workItem.Status == WorkItemStatus.Pending)
            {
                workItem.Status = WorkItemStatus.Stopped;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Unseen);
            }
            else if (workItem.Status == WorkItemStatus.Processing && workItem.IsRemote)
            {
                if (_remoteJobCanceller != null && workItem.AssignedNodeId != null)
                    await _remoteJobCanceller.Invoke(id, workItem.AssignedNodeId);
                workItem.Status = WorkItemStatus.Stopped;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Unseen);
            }
            else if (workItem.Status == WorkItemStatus.Processing && _activeWorkItem?.Id == id)
            {
                await KillActiveProcess(workItem, "Encoding stopped by user — will retry later.");
                workItem.Status = WorkItemStatus.Stopped;
                workItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Unseen);
            }
            else if (workItem.Status == WorkItemStatus.Processing)
            {
                // Orphaned: processing but not assigned to a remote node or local encoder
                workItem.Status = WorkItemStatus.Stopped;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Unseen);
            }
        }

        /// <summary>
        ///     Resets a previously failed or cancelled file in both the database and the in-memory
        ///     tracking dictionary so it will be accepted by <see cref="AddFileAsync"/> on the next attempt.
        /// </summary>
        /// <param name="filePath"> Absolute path to the file to reset. </param>
        public async Task RetryFileAsync(string filePath)
        {
            var normalizedPath = Path.GetFullPath(filePath);
            await _mediaFileRepo.ResetFileAsync(normalizedPath);

            var existing = _workItems.Values.FirstOrDefault(w =>
                Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                _workItems.TryRemove(existing.Id, out _);
        }

        /// <summary>Kills the active FFmpeg process and logs the reason. Safe to call when no process is running.</summary>
        private async Task KillActiveProcess(WorkItem workItem, string logMessage)
        {
            try
            {
                Process? proc;
                lock (_activeLock) { proc = _activeProcess; }
                if (proc != null && !proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    await LogAsync(workItem.Id, logMessage);
                }
            }
            catch (Exception ex)
            {
                await LogAsync(workItem.Id, $"Error killing process: {ex.Message}");
            }
        }

        /// <summary>
        /// Main queue processing loop. Dequeues items one at a time and encodes them locally.
        /// Stops if the queue is paused or local encoding is suspended for cluster dispatch.
        /// The <see cref="_processingLock"/> semaphore guarantees only one instance runs at a time.
        /// </summary>
        private async Task ProcessQueueAsync(EncoderOptions options)
        {
            if (!await _processingLock.WaitAsync(100))
                return; // Already processing

            _lastOptions = options;

            try
            {
                while (true)
                {
                    if (_isPaused)
                    {
                        Console.WriteLine("Queue is paused — stopping processing loop");
                        break;
                    }

                    // When local encoding is paused (master delegating to nodes),
                    // leave items in the queue for the cluster dispatch loop
                    if (_localEncodingPaused)
                        break;

                    WorkItem? workItem = null;
                    lock (_queueLock)
                    {
                        if (_workQueue.Count == 0) break;
                        workItem = _workQueue[0];
                        _workQueue.RemoveAt(0);
                    }

                    if (workItem.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped)
                        continue;

                    // Clone options so retries don't mutate settings for subsequent queue items
                    var itemOptions = new EncoderOptions
                    {
                        Format = options.Format,
                        Codec = options.Codec,
                        Encoder = options.Encoder,
                        TargetBitrate = options.TargetBitrate,
                        TwoChannelAudio = options.TwoChannelAudio,
                        DeleteOriginalFile = options.DeleteOriginalFile,
                        EnglishOnlyAudio = options.EnglishOnlyAudio,
                        EnglishOnlySubtitles = options.EnglishOnlySubtitles,
                        RemoveBlackBorders = options.RemoveBlackBorders,
                        RetryOnFail = options.RetryOnFail,
                        OutputDirectory = options.OutputDirectory,
                        EncodeDirectory = options.EncodeDirectory,
                        StrictBitrate = options.StrictBitrate,
                        HardwareAcceleration = options.HardwareAcceleration,
                        FourKBitrateMultiplier = options.FourKBitrateMultiplier,
                        Skip4K = options.Skip4K
                    };
                    await ProcessWorkItemAsync(workItem, itemOptions);
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Transitions a work item to Processing, runs the conversion, and handles success/failure outcomes.
        /// On cancellation, cleans up the partial output file. On failure, increments the DB failure count.
        /// </summary>
        private async Task ProcessWorkItemAsync(WorkItem workItem, EncoderOptions options)
        {
            lock (_activeLock) { _activeWorkItem = workItem; }
            try
            {
                workItem.Status = WorkItemStatus.Processing;
                workItem.StartedAt = DateTime.UtcNow;
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Processing);

                await ConvertVideoAsync(workItem, options);

                workItem.Status = WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                // Cancelled — status already set by CancelWorkItemAsync, just clean up output
                var outputPath = GetOutputPath(workItem, options);
                try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            }
            catch (Exception ex)
            {
                workItem.Status = WorkItemStatus.Failed;
                workItem.ErrorMessage = ex.Message;
                workItem.CompletedAt = DateTime.UtcNow;
                await _mediaFileRepo.IncrementFailureCountAsync(Path.GetFullPath(workItem.Path), ex.Message);
            }
            finally
            {
                lock (_activeLock)
                {
                    _activeWorkItem = null;
                    _activeProcess = null;
                }
            }

            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
        }

        /// <summary>
        /// Builds and executes an FFmpeg command for the given work item.
        /// Handles hardware acceleration setup, VAAPI quality calibration, bitrate calculation,
        /// stream mapping, and post-conversion validation and file placement.
        /// On failure, invokes the retry chain (no subs → sw decode → sw encode).
        /// </summary>
        /// <param name="workItem">The item to encode.</param>
        /// <param name="options">Encoding options (may be mutated by retry logic).</param>
        /// <param name="stripSubtitles">When <c>true</c>, forces <c>-sn</c> to drop all subtitle streams.</param>
        /// <param name="forceSwDecode">When <c>true</c>, disables VAAPI hardware decoding while keeping VAAPI encoding.</param>
        /// <param name="cancellationToken">Token to abort the encode mid-stream.</param>
        private async Task ConvertVideoAsync(WorkItem workItem, EncoderOptions options, bool stripSubtitles = false, bool forceSwDecode = false, CancellationToken cancellationToken = default)
        {
            if (workItem.Probe == null)
                throw new Exception("No probe data available");

            // Resolve "auto" to a concrete hardware type before building the command
            await ResolveHardwareAccelerationAsync(options);
            await LogAsync(workItem.Id, $"Hardware acceleration: {options.HardwareAcceleration}");
            await LogAsync(workItem.Id, $"Current Bitrate: {workItem.Bitrate}kbps");

            var (targetBitrate, minBitrate, maxBitrate, videoCopy) = CalculateBitrates(workItem, options);

            // Resolve the actual encoder — may fall back to software if VAAPI doesn't support this codec
            string encoder = videoCopy ? "copy" : GetEncoder(options);
            bool useVaapi = !videoCopy && encoder.Contains("vaapi");

            // VAAPI on Elkhart Lake (J6412) only supports CQP reliably.
            // CQP is content-dependent — same QP gives wildly different bitrates per content.
            // Do a 30-second test encode to measure actual output, then adjust QP to hit target.
            string compressionFlags;
            bool useLowPower = true;
            if (videoCopy)
                compressionFlags = "";
            else if (useVaapi)
            {
                long targetKbps = long.Parse(targetBitrate.TrimEnd('k'));
                (var quality, useLowPower) = await CalibrateVaapiQualityAsync(workItem, options, workItem.Path, targetKbps);

                if (quality < 0)
                {
                    // VAAPI truly can't encode this file even with correct pixel format — skip it
                    await LogAsync(workItem.Id,
                        "VAAPI cannot encode this file — skipping. Use the desktop app for this file.");
                    throw new Exception("VAAPI incompatible with this file");
                }
                else
                {
                    compressionFlags = $"-g 25 -rc_mode CQP -global_quality {quality} ";
                }
            }
            else if (encoder == "libsvtav1")
            {
                // SVT-AV1 doesn't support forced keyframes in VBR mode.
                // Use CRF with a bitrate cap — CRF 30 is a good middle ground,
                // and maxrate constrains output to our target. This gives quality-adaptive
                // encoding that won't exceed the target bitrate.
                int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
                compressionFlags = $"-crf 30 -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            else
                compressionFlags = $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrate} ";

            // Detect 10-bit content — VAAPI needs p010 format instead of nv12 for 10-bit
            bool is10Bit = workItem.Probe?.Streams?.Any(s =>
                s.CodecType == "video" && (s.PixFmt?.Contains("10") == true || s.Profile?.Contains("10") == true)) == true;

            // forceSwDecode overrides hardware decode — used as a retry when VAAPI hwaccel
            // fails mid-stream due to format changes (keeps VAAPI encoder, uses software decoder)
            bool canHwDecode = forceSwDecode ? false : CanVaapiDecode(workItem.Probe);
            if (forceSwDecode && useVaapi)
            {
                await LogAsync(workItem.Id,
                    "Using software decode + VAAPI encode (hwaccel decode disabled)");
            }

            string initFlags = useVaapi ? GetInitFlags(options.HardwareAcceleration, canHwDecode) : GetInitFlags("none");
            string vaapiFormat = is10Bit ? "p010" : "nv12";
            string hwFilter = useVaapi ? $"-vf format={vaapiFormat}|vaapi,hwupload " : "";
            bool isSvtAv1 = encoder == "libsvtav1";
            string presetFlag = useVaapi
                ? (useLowPower ? "-low_power 1 " : "")
                : isSvtAv1 ? "-preset 6 " : "-preset medium ";
            string videoFlags = videoCopy ?
                $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v copy " :
                $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v {encoder} {presetFlag}{hwFilter}";

            string audioFlags = _ffprobeService.MapAudio(workItem.Probe!, options.EnglishOnlyAudio,
                options.TwoChannelAudio, options.Format == "mkv") + " ";

            string subtitleFlags = stripSubtitles
                ? "-sn "
                : _ffprobeService.MapSub(workItem.Probe!, options.EnglishOnlySubtitles, options.Format == "mkv") + " ";

            string varFlags = options.Format == "mkv" ? "-max_muxing_queue_size 9999 " : "-movflags +faststart -max_muxing_queue_size 9999 ";

            string outputPath = GetOutputPath(workItem, options);
            string inputPath = workItem.Path;

            if (!File.Exists(inputPath))
            {
                throw new Exception($"Source file not found: {inputPath}");
            }

            // Clean up any existing partial output from a previous interrupted encode
            if (File.Exists(outputPath))
            {
                await LogAsync(workItem.Id,
                    "Deleting existing partial output from prior run...");
                try { await _fileService.FileDeleteAsync(outputPath); }
                catch (Exception ex)
                {
                    await LogAsync(workItem.Id,
                        $"Warning: Could not delete existing output: {ex.Message}");
                }
            }

            await LogAsync(workItem.Id,
                $"Encoding from: {inputPath}");
            await LogAsync(workItem.Id,
                $"Output to: {outputPath}");

            if (options.RemoveBlackBorders)
            {
                var cropFilter = await GetCropParametersAsync(workItem, options, inputPath);
                if (!string.IsNullOrEmpty(cropFilter))
                {
                    if (videoCopy)
                    {
                        // Can't crop with copy, need to re-encode
                        videoCopy = false;
                        string cropHwFilter = useVaapi
                            ? $"-vf {cropFilter.Replace("-vf ", "")},format=nv12|vaapi,hwupload "
                            : $"{cropFilter} ";
                        videoFlags = $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v {encoder} {presetFlag}{cropHwFilter}";
                        compressionFlags = useVaapi
                            ? $"-g 25 -rc_mode CQP -global_quality 25 "
                            : isSvtAv1 ? $"-crf 30 -maxrate {maxBitrate} -bufsize {int.Parse(maxBitrate.TrimEnd('k')) * 2}k "
                            : $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrate} ";
                    }
                    else
                    {
                        string cropHwFilter = useVaapi
                            ? $"-vf {cropFilter.Replace("-vf ", "")},format=nv12|vaapi,hwupload "
                            : $"{cropFilter} ";
                        videoFlags = $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v {encoder} {presetFlag}{cropHwFilter}";
                    }
                }
            }

            // -analyzeduration and -probesize handle files with many streams (e.g. 30+ PGS subtitle tracks)
            string analyzeFlags = "-analyzeduration 10M -probesize 50M ";
            string command = $"{initFlags} {analyzeFlags}-i \"{inputPath}\" {videoFlags}{compressionFlags}{audioFlags}{subtitleFlags}" +
                           $"{varFlags}-f {(options.Format == "mkv" ? "matroska" : "mp4")} \"{outputPath}\"";

            await LogAsync(workItem.Id, $"Converting {workItem.FileName}");
            await LogAsync(workItem.Id, $"Command: ffmpeg {command}");

            var startTime = DateTime.Now;

            // Run FFmpeg — catch failures so we can retry and clean up
            try
            {
                await RunFfmpegAsync(command, workItem, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (Exception ex)
            {
                await HandleConversionFailure(workItem, options, outputPath, ex.Message, stripSubtitles, forceSwDecode, cancellationToken: cancellationToken);
                return;
            }

            await Task.Delay(5000); // Wait for the filesystem to finish flushing the output before probing it.

            if (!File.Exists(outputPath))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Output file not found", stripSubtitles, forceSwDecode, cancellationToken: cancellationToken);
                return;
            }

            var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
            if (!_ffprobeService.ConvertedSuccessfully(workItem.Probe!, outputProbe))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Duration mismatch detected", stripSubtitles, forceSwDecode, cancellationToken: cancellationToken);
                return;
            }

            var outputSize = new FileInfo(outputPath).Length;
            float savings = (workItem.Size - outputSize) / 1048576f;
            float percent = 1 - ((float)outputSize / workItem.Size);

            await LogAsync(workItem.Id,
                $"Converted successfully in {DateTime.Now.Subtract(startTime).TotalMinutes:0.00} minutes.");

            if (savings > 0 || videoCopy)
            {
                await LogAsync(workItem.Id,
                    $"{savings:0,0}mb / {percent:P} saved.");

                await HandleOutputPlacement(outputPath, workItem, options);
            }
            else
            {
                await LogAsync(workItem.Id,
                    "No savings realized. Deleting conversion.");

                try { await _fileService.FileDeleteAsync(outputPath); }
                catch (Exception ex)
                {
                    await LogAsync(workItem.Id, $"Error cleaning up output: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Calculates target, min, and max bitrate strings for FFmpeg's VBR mode, and determines
        /// whether to copy the video stream instead of re-encoding (when source is already HEVC at low bitrate).
        /// 4K content uses the configured multiplier; source bitrate is always respected as an upper cap.
        /// </summary>
        /// <returns>A tuple of (targetBitrate, minBitrate, maxBitrate, videoCopy).</returns>
        private (string target, string min, string max, bool copy) CalculateBitrates(WorkItem workItem, EncoderOptions options)
        {
            bool videoCopy = false;
            string targetBitrate, minBitrate, maxBitrate;

            if (options.StrictBitrate)
            {
                targetBitrate = $"{options.TargetBitrate}k";
                minBitrate = targetBitrate;
                maxBitrate = targetBitrate;
            }
            else if (workItem.Probe!.Streams.Any(x => x.Width > 1920)) // 4k
            {
                int multiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
                int hdBitrate = options.TargetBitrate * multiplier;
                targetBitrate = $"{hdBitrate}k";
                minBitrate = $"{hdBitrate - 500}k";
                maxBitrate = $"{hdBitrate + 1000}k";
            }
            else if (workItem.Bitrate < options.TargetBitrate + 700 && !workItem.IsHevc)
            {
                targetBitrate = $"{(int)(workItem.Bitrate * 0.7)}k";
                minBitrate = $"{(int)(workItem.Bitrate * 0.6)}k";
                maxBitrate = $"{(int)(workItem.Bitrate * 0.8)}k";
            }
            else
            {
                // Never encode higher than the source bitrate — cap to whichever is lower
                int effectiveTarget = workItem.Bitrate > 0
                    ? (int)Math.Min(options.TargetBitrate, workItem.Bitrate)
                    : options.TargetBitrate;
                targetBitrate = $"{effectiveTarget}k";
                minBitrate = $"{Math.Max(effectiveTarget - 200, 0)}k";
                maxBitrate = $"{effectiveTarget + 500}k";
            }

            // If bitrate is already below target and using HEVC, copy instead
            if (workItem.Bitrate < options.TargetBitrate + 700 && workItem.IsHevc && !options.RemoveBlackBorders)
            {
                videoCopy = true;
            }

            return (targetBitrate, minBitrate, maxBitrate, videoCopy);
        }

        private string? _detectedHardware = null;

        /// <summary>
        /// Detects available hardware acceleration by testing encoders.
        /// Result is cached after first detection.
        /// </summary>
        private async Task<string> DetectHardwareAccelerationAsync()
        {
            if (_detectedHardware != null)
                return _detectedHardware;

            // Windows GPU detection
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Auto-detect: Running on Windows, testing GPU encoders...");

                if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
                {
                    Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                    _detectedHardware = "nvidia";
                    return _detectedHardware;
                }

                if (await TestEncoderAsync("-hwaccel qsv -qsv_device auto", "hevc_qsv"))
                {
                    Console.WriteLine("Auto-detect: Intel QSV available");
                    _detectedHardware = "intel";
                    return _detectedHardware;
                }

                if (await TestEncoderAsync("-hwaccel auto", "hevc_amf"))
                {
                    Console.WriteLine("Auto-detect: AMD AMF available");
                    _detectedHardware = "amd";
                    return _detectedHardware;
                }

                Console.WriteLine("Auto-detect: No hardware acceleration available on Windows, using software");
                _detectedHardware = "none";
                return _detectedHardware;
            }

            // Linux: Log VAAPI diagnostics
            await LogVaapiInfoAsync();

            // Test VAAPI (Intel and AMD GPUs on Linux)
            // Try both iHD and i965 drivers — QNAP systems may need either one
            if (File.Exists("/dev/dri/renderD128"))
            {
                var driversToTry = new[] { "iHD", "i965" };
                foreach (var driver in driversToTry)
                {
                    Console.WriteLine($"Auto-detect: Trying VAAPI with {driver} driver...");
                    Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", driver);

                    var hwInit = "-init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw";
                    bool hevcOk = await TestEncoderAsync(hwInit, "hevc_vaapi");
                    bool h264Ok = await TestEncoderAsync(hwInit, "h264_vaapi");

                    if (hevcOk || h264Ok)
                    {
                        Console.WriteLine($"Auto-detect: VAAPI available with {driver} driver (hevc={hevcOk}, h264={h264Ok})");
                        _detectedHardware = "intel";
                        return _detectedHardware;
                    }
                }
            }

            if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
            {
                Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                _detectedHardware = "nvidia";
                return _detectedHardware;
            }

            Console.WriteLine("Auto-detect: No hardware acceleration available, using software");
            _detectedHardware = "none";
            return _detectedHardware;
        }

        /// <summary>
        /// Logs VAAPI diagnostic info (vainfo output) for troubleshooting.
        /// </summary>
        private async Task LogVaapiInfoAsync()
        {
            // VAAPI is Linux-only — skip entirely on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                if (File.Exists("/dev/dri/renderD128"))
                    Console.WriteLine("Auto-detect: /dev/dri/renderD128 exists");
                else
                {
                    Console.WriteLine("Auto-detect: /dev/dri/renderD128 NOT FOUND");
                    if (Directory.Exists("/dev/dri"))
                    {
                        var entries = Directory.GetFileSystemEntries("/dev/dri");
                        Console.WriteLine($"Auto-detect: /dev/dri contents: {string.Join(", ", entries)}");
                    }
                    else
                        Console.WriteLine("Auto-detect: /dev/dri directory does not exist");
                    return;
                }

                var psi = new ProcessStartInfo("vainfo")
                {
                    Arguments = "--display drm --device /dev/dri/renderD128",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.Environment["LIBVA_DRIVER_NAME"] = Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME") ?? "iHD";

                using var process = new Process { StartInfo = psi };
                process.Start();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                process.WaitForExit(5000);

                var output = !string.IsNullOrEmpty(stdout) ? stdout : stderr;
                Console.WriteLine($"Auto-detect vainfo output:\n{output.Substring(0, Math.Min(output.Length, 1000))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auto-detect: vainfo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tests whether a hardware encoder is functional by running a minimal encode.
        /// </summary>
        private async Task<bool> TestEncoderAsync(string hwFlags, string encoder)
        {
            try
            {
                // Test with the same flags used in actual encoding: CQP mode with low-power for VAAPI
                string vf, extra;
                if (encoder.Contains("vaapi"))
                {
                    vf = "-vf format=nv12|vaapi,hwupload";
                    extra = "-low_power 1 -rc_mode CQP -global_quality 25";
                }
                else
                {
                    vf = "";
                    extra = "";
                }
                var args = $"-y {hwFlags} -f lavfi -i color=c=black:s=256x256:d=0.1 {vf} -c:v {encoder} {extra} -frames:v 1 -f null -";
                var psi = new ProcessStartInfo(_ffmpegPath)
                {
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var stderr = await process.StandardError.ReadToEndAsync();

                var completed = process.WaitForExit(10000);
                if (!completed)
                {
                    process.Kill();
                    Console.WriteLine($"Auto-detect: {encoder} test timed out");
                    return false;
                }

                Console.WriteLine($"Auto-detect: {encoder} test exit={process.ExitCode}");
                if (process.ExitCode != 0)
                {
                    // Get last 500 chars of stderr (actual error is at the end, not the build config at the start)
                    var errTail = stderr.Length > 500 ? stderr.Substring(stderr.Length - 500) : stderr;
                    Console.WriteLine($"Auto-detect: {encoder} stderr (tail): {errTail}");
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves "auto" to a concrete hardware acceleration type.
        /// For explicit selections, returns as-is.
        /// </summary>
        private async Task ResolveHardwareAccelerationAsync(EncoderOptions options)
        {
            if (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                options.HardwareAcceleration = await DetectHardwareAccelerationAsync();
            }
        }

        /// <summary>
        /// Returns <c>true</c> if the specified hardware acceleration mode maps to VAAPI (Intel/AMD on Linux).
        /// Always returns <c>false</c> on Windows since VAAPI is Linux-only.
        /// </summary>
        private bool IsVaapiAcceleration(string hardwareAcceleration)
        {
            // VAAPI only exists on Linux — never use VAAPI paths on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            return hardwareAcceleration.ToLower() is "intel" or "amd";
        }

        /// <summary>
        /// Returns <c>true</c> if the source video codec is supported by VAAPI hardware decoding
        /// on Elkhart Lake (J6412) hardware (h264, hevc, mpeg2, vp8, vp9, mjpeg).
        /// </summary>
        private static bool CanVaapiDecode(ProbeResult? probe)
        {
            var codec = probe?.Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName;
            // J6412 (Elkhart Lake) VAAPI decode: h264, hevc, mpeg2, vp8, vp9, jpeg
            return codec is "h264" or "hevc" or "mpeg2video" or "vp8" or "vp9" or "mjpeg";
        }

        /// <summary>
        /// Returns the FFmpeg initialization flags for the specified hardware acceleration mode.
        /// On Windows, maps Intel → QSV and AMD → AMF. On Linux, maps Intel/AMD → VAAPI.
        /// When <paramref name="hwDecode"/> is <c>false</c>, initializes the VAAPI device but skips
        /// forcing hardware decode (software decode + VAAPI encode mode).
        /// </summary>
        private string GetInitFlags(string hardwareAcceleration, bool hwDecode = true)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            return hardwareAcceleration.ToLower() switch
            {
                "intel" when isWindows => "-y -hwaccel qsv -qsv_device auto",
                "amd" when isWindows => "-y -hwaccel auto",
                // Software decode + VAAPI encode: init the device but don't force hwaccel decode
                "intel" when !hwDecode => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw",
                "amd" when !hwDecode => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw",
                "intel" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "amd" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "nvidia" => "-y -hwaccel cuda",
                _ => "-y"
            };
        }

        /// <summary>
        /// Maps the user's encoder preference and hardware acceleration setting to the
        /// concrete FFmpeg encoder name (e.g., <c>"hevc_vaapi"</c>, <c>"hevc_nvenc"</c>, <c>"libx265"</c>).
        /// </summary>
        private string GetEncoder(EncoderOptions options)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            bool isAv1 = options.Encoder.Contains("av1") || options.Encoder.Contains("svt");
            bool isH265 = !isAv1 && options.Encoder.Contains("265");
            bool isH264 = !isAv1 && options.Encoder.Contains("264");

            return options.HardwareAcceleration.ToLower() switch
            {
                // AV1 encoders
                "intel" when isWindows && isAv1 => "av1_qsv",
                "amd" when isWindows && isAv1 => "av1_amf",
                "intel" when isAv1 => "av1_vaapi",
                "amd" when isAv1 => "av1_vaapi",
                "nvidia" when isAv1 => "av1_nvenc",
                // H.265 encoders
                "intel" when isWindows && isH265 => "hevc_qsv",
                "intel" when isWindows && isH264 => "h264_qsv",
                "amd" when isWindows && isH265 => "hevc_amf",
                "amd" when isWindows && isH264 => "h264_amf",
                "intel" when isH265 => "hevc_vaapi",
                "intel" when isH264 => "h264_vaapi",
                "amd" when isH265 => "hevc_vaapi",
                "amd" when isH264 => "h264_vaapi",
                "nvidia" when isH265 => "hevc_nvenc",
                "nvidia" when isH264 => "h264_nvenc",
                _ => options.Encoder
            };
        }

        /// <summary>
        /// Computes the output file path for a work item.
        /// Prefers <c>EncodeDirectory</c>, then <c>OutputDirectory</c>, then the source file's directory.
        /// Output file is named <c>"{basename} [snacks].{ext}"</c>.
        /// </summary>
        private string GetOutputPath(WorkItem workItem, EncoderOptions options)
        {
            string fileName = _fileService.RemoveExtension(workItem.FileName);
            string extension = options.Format == "mkv" ? ".mkv" : ".mp4";
            string snacksName = $"{fileName} [snacks]{extension}";

            if (!string.IsNullOrEmpty(options.EncodeDirectory))
            {
                return Path.Combine(options.EncodeDirectory, snacksName);
            }
            else if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                return Path.Combine(options.OutputDirectory, snacksName);
            }
            else
            {
                string originalDir = _fileService.GetDirectory(workItem.Path);
                return Path.Combine(originalDir, snacksName);
            }
        }

        /// <summary>
        /// Returns the final "clean" path (without [snacks] tag) for output placement.
        /// </summary>
        private string GetCleanOutputName(string snacksPath)
        {
            string dir = Path.GetDirectoryName(snacksPath) ?? "";
            string fileName = Path.GetFileNameWithoutExtension(snacksPath).Replace(" [snacks]", "");
            string extension = Path.GetExtension(snacksPath);
            return Path.Combine(dir, fileName + extension);
        }

        /// <summary>
        /// Does iterative 30-second test encodes to find the right QP for the target bitrate.
        /// Starts at QP 24, measures output, adjusts, and retests until within 15% of target
        /// or max 3 iterations.
        /// </summary>
        private async Task<(int qp, bool useLowPower)> CalibrateVaapiQualityAsync(WorkItem workItem, EncoderOptions options, string inputPath, long targetKbps)
        {
            int testDuration = 60;
            int maxIterations = 6;
            double tolerance = 0.20; // within 20% of target

            // Seek to ~40% into the file for a representative sample (avoids intros/credits)
            int seekSeconds = Math.Max(0, (int)(workItem.Length * 0.40));
            string seekTime = $"{seekSeconds / 3600:D2}:{(seekSeconds % 3600) / 60:D2}:{seekSeconds % 60:D2}";

            bool canHwDecode = CanVaapiDecode(workItem.Probe);
            string initFlags = GetInitFlags(options.HardwareAcceleration, canHwDecode);
            string encoder = GetEncoder(options);
            // Use p010 for 10-bit content, nv12 for 8-bit
            bool is10Bit = workItem.Probe?.Streams?.Any(s =>
                s.CodecType == "video" && (s.PixFmt?.Contains("10") == true || s.Profile?.Contains("10") == true)) == true;
            string vaapiFormat = is10Bit ? "p010" : "nv12";
            string hwFilter = $"-vf format={vaapiFormat}|vaapi,hwupload";

            // Try LP mode first, then fall back to normal mode if it fails
            bool[] lowPowerModes = [true, false];
            foreach (bool lowPower in lowPowerModes)
            {
                string lpFlag = lowPower ? "-low_power 1 " : "";
                string modeLabel = lowPower ? "LP mode" : "normal mode";
                int currentQp = 24;

                for (int iteration = 1; iteration <= maxIterations; iteration++)
                {
                    await LogAsync(workItem.Id,
                        $"Calibration pass {iteration}/{maxIterations} ({modeLabel}) — testing QP {currentQp}...");

                    long measuredKbps = await RunTestEncodeAsync(inputPath, initFlags, encoder, hwFilter, lpFlag, currentQp, seekTime, testDuration);

                    if (measuredKbps <= 0)
                    {
                        if (lowPower)
                        {
                            await LogAsync(workItem.Id,
                                "LP mode produced no output — retrying without low_power...");
                            break; // try next mode
                        }
                        await LogAsync(workItem.Id,
                            "VAAPI produced no measurable output — encoder incompatible with this file");
                        return (-1, false);
                    }

                    // Detect absurd output (more than 5x source bitrate = encoder broken for this file)
                    if (workItem.Bitrate > 0 && measuredKbps > workItem.Bitrate * 5)
                    {
                        if (lowPower)
                        {
                            await LogAsync(workItem.Id,
                                $"LP mode output ({measuredKbps}kbps) is absurdly high — retrying without low_power...");
                            break;
                        }
                        await LogAsync(workItem.Id,
                            $"VAAPI output ({measuredKbps}kbps) is absurdly high — encoder broken for this file");
                        return (-1, false);
                    }

                    double ratio = (double)measuredKbps / targetKbps;
                    await LogAsync(workItem.Id,
                        $"Pass {iteration}: QP {currentQp} → {measuredKbps}kbps (target {targetKbps}kbps, ratio {ratio:F2}x)");

                    if (ratio >= (1 - tolerance) && ratio <= (1 + tolerance))
                    {
                        await LogAsync(workItem.Id,
                            $"QP {currentQp} is within {tolerance:P0} of target. Using QP {currentQp} ({modeLabel}).");
                        return (currentQp, lowPower);
                    }

                    // Already below target and at minimum QP — can't increase quality further
                    if (measuredKbps <= targetKbps && currentQp <= 18)
                    {
                        await LogAsync(workItem.Id,
                            $"QP {currentQp} already at minimum and below target. Using QP {currentQp} ({modeLabel}).");
                        return (currentQp, lowPower);
                    }

                    // Calculate adjustment: each +2 QP ≈ 0.72x bitrate
                    double qpDelta = 2.0 * Math.Log((double)targetKbps / measuredKbps) / Math.Log(0.72);
                    int adjustment = (int)Math.Round(qpDelta);
                    if (adjustment == 0) adjustment = measuredKbps > targetKbps ? 1 : -1;

                    currentQp = Math.Clamp(currentQp + adjustment, 18, 51);
                }

                // Completed all iterations without early return — use final QP
                await LogAsync(workItem.Id,
                    $"Calibration complete after {maxIterations} passes. Using QP {currentQp} ({modeLabel}).");
                return (currentQp, lowPower);
            }

            // Both LP and normal mode failed
            return (-1, false);
        }

        /// <summary>
        /// Runs a short test encode to measure the actual output bitrate for a given VAAPI QP value.
        /// Returns the measured bitrate in kbps, or 0 if the encode produced no output.
        /// </summary>
        private async Task<long> RunTestEncodeAsync(string inputPath, string initFlags, string encoder, string hwFilter, string lpFlag, int qp, string seekTime, int duration)
        {
            string command = $"{initFlags} -ss {seekTime} -i \"{inputPath}\" -t {duration} " +
                $"-c:v {encoder} {lpFlag}{hwFilter} -g 25 -rc_mode CQP -global_quality {qp} " +
                $"-an -sn -f null -";

            Console.WriteLine($"Calibration command: ffmpeg {command}");

            try
            {
                var psi = new ProcessStartInfo(_ffmpegPath)
                {
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                // Read all stderr (contains FFmpeg output including summary line)
                var stderrTask = process.StandardError.ReadToEndAsync();
                // Drain stdout to prevent deadlock
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                var completed = process.WaitForExit(120000);
                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return -1;
                }

                var outputText = await stderrTask;
                var sizeMatch = Regex.Match(outputText, @"video:\s*(\d+)\s*(?:kB|KiB)");
                if (sizeMatch.Success)
                {
                    long outputKb = long.Parse(sizeMatch.Groups[1].Value);
                    return outputKb * 8 / duration; // kbps
                }

                // Log stderr to help diagnose failures — grab error lines and tail
                var lines = outputText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var errorLines = lines.Where(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("Invalid", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("Impossible", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("not support", StringComparison.OrdinalIgnoreCase));
                var tail = string.Join("\n", lines.TakeLast(15));
                var errors = string.Join("\n", errorLines);
                Console.WriteLine($"Calibration test produced no measurable output.\nErrors: {errors}\nLast lines:\n{tail}");
            }
            catch (Exception ex) { Console.WriteLine($"Calibration test exception: {ex.Message}"); }

            return -1;
        }

        private async Task<string> GetCropParametersAsync(WorkItem workItem, EncoderOptions options, string inputPath)
        {
            await LogAsync(workItem.Id, "Getting crop values.");

            int lengthInMinutes = (int)workItem.Length / 60;
            string startTime = lengthInMinutes > 20 ? "00:10:00" : "00:00:00";
            string duration = lengthInMinutes > 20 ? "00:10:00" : $"00:{Math.Min(lengthInMinutes, 10):D2}:00";
            
            string command = $"{GetInitFlags(options.HardwareAcceleration)} -ss {startTime} -i \"{inputPath}\" " +
                           $"-t {duration} -vf cropdetect=24:2:8 -f null -";

            var cropValues = new ConcurrentDictionary<string, int>();

            var processStartInfo = new ProcessStartInfo(_ffmpegPath)
            {
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null && e.Data.Contains("crop="))
                {
                    var match = Regex.Match(e.Data, @"crop=([^\s]+)");
                    if (match.Success)
                    {
                        string crop = match.Groups[1].Value;
                        cropValues.AddOrUpdate(crop, 1, (_, count) => count + 1);
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Timeout cropdetect at 3 minutes to prevent hangs
            var completed = process.WaitForExit(180000);
            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await LogAsync(workItem.Id, "Crop detection timed out, skipping.");
                return "";
            }

            if (cropValues.Count == 0)
                return "";

            string mostCommonCrop = cropValues.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;
            await LogAsync(workItem.Id, $"Detected crop: {mostCommonCrop}");
            return $"-vf crop={mostCommonCrop}";
        }

        private async Task RunFfmpegAsync(string command, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            var processStartInfo = new ProcessStartInfo(_ffmpegPath)
            {
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process { StartInfo = processStartInfo };
            lock (_activeLock) { _activeProcess = process; }
            var errorOutput = new ConcurrentQueue<string>();
            var lastProgressUpdate = DateTime.MinValue;
            var lastActivity = DateTime.UtcNow;
            // Final muxing phase produces no output — slow NAS drives need extra time
            const int stallTimeoutSeconds = 300;

            process.Start();

            // Read stderr manually — FFmpeg uses \r for progress lines which
            // BeginErrorReadLine() doesn't split on Linux .NET
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var lineBuilder = new System.Text.StringBuilder();
                    var stream = process.StandardError;

                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {

                        for (int i = 0; i < read; i++)
                        {
                            if (buffer[i] == '\r' || buffer[i] == '\n')
                            {
                                if (lineBuilder.Length > 0)
                                {
                                    var line = lineBuilder.ToString();
                                    lineBuilder.Clear();

                                    errorOutput.Enqueue(line);
                                    lastActivity = DateTime.UtcNow;

                                    try
                                    {
                                        if (line.Contains("time=") && workItem.Length > 0)
                                        {
                                            var match = Regex.Match(line, @"time=(\d{2}:\d{2}:\d{2}\.\d{2,})");
                                            if (match.Success)
                                            {
                                                var timeStr = match.Groups[1].Value;
                                                var seconds = _ffprobeService.DurationStringToSeconds(timeStr);
                                                var progress = (int)Math.Clamp(Math.Round(seconds / workItem.Length * 100), 0, 99);

                                                workItem.Progress = progress;

                                                var now = DateTime.UtcNow;
                                                if ((now - lastProgressUpdate).TotalSeconds >= 2)
                                                {
                                                    lastProgressUpdate = now;
                                                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                                    await LogAsync(workItem.Id, $"FFmpeg: {line}");

                                                    if (_progressCallback != null)
                                                        _ = _progressCallback(workItem.Id, progress);
                                                }
                                            }
                                        }
                                        else if (!string.IsNullOrWhiteSpace(line))
                                        {
                                            // Forward non-progress lines (errors, warnings, info)
                                            await LogAsync(workItem.Id, $"FFmpeg: {line}");
                                        }
                                    }
                                    catch { }
                                }
                            }
                            else
                            {
                                lineBuilder.Append(buffer[i]);
                            }
                        }
                    }
                }
                catch { }
            });

            // Drain stdout to prevent the process from blocking on a full pipe buffer.
            _ = Task.Run(async () =>
            {
                try { await process.StandardOutput.ReadToEndAsync(); } catch { }
            });

            // Wait for exit but kill the process if it stalls (no output for stallTimeoutSeconds)
            var exitTask = process.WaitForExitAsync();
            while (!exitTask.IsCompleted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await LogAsync(workItem.Id, "Cancellation requested — killing FFmpeg process.");
                    try { process.Kill(entireProcessTree: true); } catch { }
                    await exitTask; // Wait for kill to complete
                    throw new OperationCanceledException("Encoding was cancelled.");
                }

                Task delayTask;
                try { delayTask = Task.Delay(30000, cancellationToken); }
                catch (OperationCanceledException)
                {
                    await LogAsync(workItem.Id, "Cancellation requested — killing FFmpeg process.");
                    try { process.Kill(entireProcessTree: true); } catch { }
                    await exitTask;
                    throw new OperationCanceledException("Encoding was cancelled.");
                }
                var winner = await Task.WhenAny(exitTask, delayTask);
                if (winner != exitTask && !process.HasExited)
                {
                    if ((DateTime.UtcNow - lastActivity).TotalSeconds >= stallTimeoutSeconds)
                    {
                        await LogAsync(workItem.Id,
                            $"FFmpeg stalled (no output for {stallTimeoutSeconds} seconds). Killing process.");
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await exitTask; // Wait for kill to complete
                        break;
                    }
                }
            }

            lock (_activeLock) { _activeProcess = null; }

            if (workItem.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped)
            {
                process.Dispose();
                throw new OperationCanceledException("Encoding was cancelled.");
            }

            var exitCode = process.ExitCode;
            process.Dispose();

            if (exitCode != 0)
            {
                var errorText = string.Join("\n", errorOutput.ToArray().TakeLast(10));
                await LogAsync(workItem.Id, $"FFmpeg failed with exit code {exitCode}");
                await LogAsync(workItem.Id, $"Last error lines:\n{errorText}");
                throw new Exception($"FFmpeg exited with code {exitCode}. Error: {errorText}");
            }
        }

        private async Task HandleConversionFailure(WorkItem workItem, EncoderOptions options, string outputPath, string reason, bool subtitlesWereStripped, bool swDecodeWasForced = false, CancellationToken cancellationToken = default)
        {
            await LogAsync(workItem.Id, $"Conversion failed: {reason}");

            try
            {
                await _fileService.FileDeleteAsync(outputPath);
                await LogAsync(workItem.Id, "Cleaned up failed output file.");
            }
            catch (Exception ex)
            {
                await LogAsync(workItem.Id, $"Warning: Could not clean up output file: {ex.Message}");
            }

            // Retry 1: Strip all subtitles (covers bitmap subs, broken streams, etc.)
            if (!subtitlesWereStripped)
            {
                await LogAsync(workItem.Id, "Retrying without subtitles...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await ConvertVideoAsync(workItem, options, stripSubtitles: true, cancellationToken: cancellationToken);
                return;
            }

            // Retry 2: Software decode + VAAPI encode for hwaccel filter graph errors
            // This keeps GPU encoding but avoids the problematic hardware decoder that crashes
            // on mid-stream format/resolution changes
            bool isHwaccelError = reason.Contains("hwaccel", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("filter graph", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Impossible to convert", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("hwupload", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("Reconfiguring filter", StringComparison.OrdinalIgnoreCase);

            bool isVaapi = IsVaapiAcceleration(options.HardwareAcceleration);

            if (isHwaccelError && isVaapi && !swDecodeWasForced)
            {
                await LogAsync(workItem.Id,
                    "Retrying with software decode + VAAPI encode...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await ConvertVideoAsync(workItem, options, stripSubtitles: subtitlesWereStripped, forceSwDecode: true, cancellationToken: cancellationToken);
                return;
            }

            // Retry 3: Fall back to software encoding (resets subtitle stripping to try subs first on software)
            // Check the actual resolved encoder, not options.Encoder (which is the user's base preference
            // like "libsvtav1" that GetEncoder() maps to hardware variants like "av1_nvenc")
            bool isAlreadySoftware = options.HardwareAcceleration.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (options.RetryOnFail && !isAlreadySoftware)
            {
                await LogAsync(workItem.Id, "Retrying with software encoding...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                // Use the correct software encoder for the target codec
                bool isAv1Target = options.Encoder.Contains("av1") || options.Encoder.Contains("svt") || options.Codec == "av1";
                options.Encoder = isAv1Target ? "libsvtav1" : "libx265";
                options.HardwareAcceleration = "none";
                await ConvertVideoAsync(workItem, options, stripSubtitles: false, cancellationToken: cancellationToken);
                return;
            }

            // All retries exhausted — original file is untouched
            await LogAsync(workItem.Id, "All retries exhausted. Original file is unchanged.");
            throw new Exception($"Conversion failed after retries: {reason}");
        }

        /// <summary>Returns the work item currently being encoded locally, or <c>null</c> when idle.</summary>
        public WorkItem? GetActiveWorkItem() => _activeWorkItem;

        /// <summary>Returns the encoder options from the most recently started queue run.</summary>
        public EncoderOptions? GetLastOptions() => _lastOptions;

        /// <summary>
        ///     Suspends or resumes the local processing loop. When suspended, pending items remain
        ///     in the queue for the cluster dispatch loop to assign to remote nodes instead.
        /// </summary>
        /// <param name="paused"> <see langword="true"/> to suspend local encoding; <see langword="false"/> to resume it. </param>
        public void SetLocalEncodingPaused(bool paused)
        {
            _localEncodingPaused = paused;
            Console.WriteLine($"Cluster: Local encoding {(paused ? "paused" : "resumed")}");
        }

        /// <summary>Returns the detected hardware acceleration type (e.g., <c>"nvidia"</c>, <c>"intel"</c>, <c>"none"</c>), or <c>null</c> if detection hasn't completed.</summary>
        public string? GetDetectedHardware() => _detectedHardware;

        /// <summary>Sets the callback invoked on each progress update when running as a cluster node.</summary>
        /// <param name="callback">Delegate receiving (workItemId, progressPercent).</param>
        public void SetProgressCallback(Func<string, int, Task>? callback)
        {
            _progressCallback = callback;
        }

        /// <summary>Sets the callback invoked for each FFmpeg log line when running as a cluster node.</summary>
        /// <param name="callback">Delegate receiving (workItemId, logLine).</param>
        public void SetLogCallback(Func<string, string, Task>? callback)
        {
            _logCallback = callback;
        }

        /// <summary>Sets the callback used by the cluster service to cancel a remote job on a specific node.</summary>
        /// <param name="canceller">Delegate receiving (jobId, nodeId).</param>
        public void SetRemoteJobCanceller(Func<string, string, Task>? canceller)
        {
            _remoteJobCanceller = canceller;
        }

        /// <summary>Sets the async callback the cluster service uses to check whether a file is already assigned to a remote node.</summary>
        /// <param name="checker">Async delegate receiving (filePath), returning <c>true</c> if currently remote.</param>
        public void SetRemoteJobChecker(Func<string, Task<bool>>? checker)
        {
            _isRemoteJobChecker = checker;
        }

        /// <summary>
        ///     Kills any active FFmpeg process, clears the pending queue, and resets all in-memory
        ///     state. Called when the master switches to node mode and must hand off processing.
        /// </summary>
        public async Task StopAndClearQueue()
        {
            Console.WriteLine("Cluster: Stopping all processing and clearing queue...");

            WorkItem? activeItem;
            Process? activeProc;
            lock (_activeLock)
            {
                activeItem = _activeWorkItem;
                activeProc = _activeProcess;
            }

            if (activeItem != null && activeProc != null)
            {
                try
                {
                    if (!activeProc.HasExited)
                        activeProc.Kill(entireProcessTree: true);
                }
                catch { }

                if (_lastOptions != null)
                {
                    var outputPath = GetOutputPath(activeItem, _lastOptions);
                    try
                    {
                        if (File.Exists(outputPath))
                            await _fileService.FileDeleteAsync(outputPath);
                    }
                    catch { }
                }

                activeItem.Status = WorkItemStatus.Stopped;
                activeItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", activeItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(activeItem.Path), MediaFileStatus.Unseen);
                lock (_activeLock)
                {
                    _activeWorkItem = null;
                    _activeProcess = null;
                }
            }

            List<WorkItem> pendingItems;
            lock (_queueLock)
            {
                pendingItems = _workQueue.ToList();
                _workQueue.Clear();
            }

            // Reset all pending items to Unseen so the node doesn't inherit master-mode queue state.
            foreach (var item in pendingItems)
            {
                item.Status = WorkItemStatus.Stopped;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", item);
                try
                {
                    await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(item.Path), MediaFileStatus.Unseen);
                }
                catch { }
            }

            _workItems.Clear();

            Console.WriteLine($"Cluster: Queue cleared ({pendingItems.Count} items stopped)");
        }

        /// <summary>
        ///     Atomically dequeues the next pending work item and marks it as Processing,
        ///     making it available for dispatch to a cluster node.
        /// </summary>
        /// <returns> The next pending <see cref="WorkItem"/>, or <see langword="null"/> if the queue is empty. </returns>
        public WorkItem? DequeueForRemoteProcessing()
        {
            lock (_queueLock)
            {
                var item = _workQueue.FirstOrDefault(w => w.Status == WorkItemStatus.Pending);
                if (item != null)
                {
                    item.Status = WorkItemStatus.Processing;
                    _workQueue.Remove(item);
                }
                return item;
            }
        }

        /// <summary>
        ///     Returns a work item to the pending queue after a remote node failure,
        ///     clearing node assignment metadata so it can be dispatched again.
        /// </summary>
        /// <param name="item"> The work item to requeue. </param>
        public void RequeueWorkItem(WorkItem item, bool silent = false)
        {
            item.Status = WorkItemStatus.Pending;
            item.AssignedNodeId = null;
            item.AssignedNodeName = null;
            item.RemoteJobPhase = null;
            item.TransferProgress = 0;

            lock (_queueLock)
            {
                _workQueue.Add(item);
                _workQueue.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));
            }

            if (!silent)
            {
                _ = _hubContext.Clients.All.SendAsync("WorkItemUpdated", item);
                Console.WriteLine($"Cluster: Re-queued {item.FileName} for processing");
            }
        }

        /// <summary>
        ///     Finalizes a job completed on a remote cluster node by calculating space savings,
        ///     performing file placement, and updating the work item status to Completed.
        /// </summary>
        /// <param name="workItem"> The work item that was encoded remotely. </param>
        /// <param name="outputPath"> Local path to the encoded output file transferred from the node. </param>
        /// <param name="options"> The encoder options that were used for the job. </param>
        public async Task HandleRemoteCompletion(WorkItem workItem, string outputPath, EncoderOptions options)
        {
            var outputSize = new FileInfo(outputPath).Length;
            float savings = (workItem.Size - outputSize) / 1048576f;
            float percent = 1 - ((float)outputSize / workItem.Size);

            Console.WriteLine($"Cluster: Remote encode of {workItem.FileName}: {savings:0,0}MB / {percent:P} saved");

            if (savings > 0)
            {
                await HandleOutputPlacement(outputPath, workItem, options);
                workItem.Status = WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                workItem.ErrorMessage = null;
                workItem.RemoteJobPhase = null;
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);
            }
            else
            {
                Console.WriteLine($"Cluster: No savings for {workItem.FileName}, deleting output");
                try { await _fileService.FileDeleteAsync(outputPath); } catch { }
                workItem.Status = WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                workItem.ErrorMessage = null;
                workItem.RemoteJobPhase = null;
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Skipped);
            }

            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
        }

        /// <summary>
        ///     Marks a work item as failed and records the error in the database.
        ///     Used by the cluster service to propagate remote node failures back to the master.
        /// </summary>
        /// <param name="workItemId"> The ID of the work item that failed. </param>
        /// <param name="errorMessage"> The error message to record. </param>
        public async Task MarkWorkItemFailed(string workItemId, string errorMessage)
        {
            if (_workItems.TryGetValue(workItemId, out var workItem))
            {
                workItem.Status = WorkItemStatus.Failed;
                workItem.ErrorMessage = errorMessage;
                workItem.CompletedAt = DateTime.UtcNow;
                await _mediaFileRepo.IncrementFailureCountAsync(Path.GetFullPath(workItem.Path), errorMessage);
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            }
        }

        /// <summary>
        ///     Probes a file on disk and constructs a <see cref="WorkItem"/> with a predetermined ID.
        ///     Used during crash recovery so that a restarted master can hand the original job ID
        ///     back to the cluster node that was already encoding it.
        /// </summary>
        /// <param name="id"> The job ID to assign to the reconstructed work item. </param>
        /// <param name="filePath"> Absolute path to the source video file. </param>
        /// <returns> The reconstructed <see cref="WorkItem"/>, or <see langword="null"/> if the file does not exist or probing fails. </returns>
        public async Task<WorkItem?> CreateWorkItemWithIdAsync(string id, string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return null;

                var fileInfo = new FileInfo(filePath);
                var probe = await _ffprobeService.ProbeAsync(filePath);
                var length = _ffprobeService.GetVideoDuration(probe);

                var isHevc = false;
                long bitrate = 0;
                foreach (var stream in probe.Streams)
                {
                    if (stream.CodecType == "video")
                    {
                        isHevc = stream.CodecName == "hevc";
                        break;
                    }
                }

                if (length > 0)
                    bitrate = (long)(fileInfo.Length * 8 / length / 1000);

                var workItem = new WorkItem
                {
                    Id = id,
                    FileName = Path.GetFileName(filePath),
                    Path = filePath,
                    Size = fileInfo.Length,
                    Bitrate = bitrate,
                    Length = length,
                    IsHevc = isHevc,
                    Probe = probe,
                    Status = WorkItemStatus.Processing,
                    StartedAt = DateTime.UtcNow
                };

                _workItems[id] = workItem;
                return workItem;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Failed to reconstruct work item {id}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Runs the encoding pipeline for a job received from a master node.
        /// Registers the work item so logging works, then delegates to <see cref="ConvertVideoAsync"/>.
        /// The work item is kept in the dictionary after completion so the output remains accessible for download.
        /// </summary>
        /// <param name="workItem">The work item to encode.</param>
        /// <param name="options">Encoding options from the master's job assignment.</param>
        /// <param name="cancellationToken">Token to abort encoding if the job is cancelled.</param>
        public async Task ConvertVideoForRemoteAsync(WorkItem workItem, EncoderOptions options, CancellationToken cancellationToken = default)
        {
            _workItems[workItem.Id] = workItem;

            try
            {
                await ConvertVideoAsync(workItem, options, cancellationToken: cancellationToken);
            }
            finally
            {
                // Don't remove from _workItems — the output file needs to remain accessible
            }
        }

        /// <summary>
        /// Moves the encoded output to its final destination, optionally deletes the original,
        /// and renames the file from the <c>[snacks]</c> staging name to the clean final name.
        /// No-op if the output file produced no size savings (already handled by the caller).
        /// </summary>
        private async Task HandleOutputPlacement(string outputPath, WorkItem workItem, EncoderOptions options)
        {
            try
            {
                if (!string.IsNullOrEmpty(options.OutputDirectory))
                {
                    // Output already in the right directory (GetOutputPath used OutputDirectory)
                    // If EncodeDirectory was used, move from there to OutputDirectory
                    if (!string.IsNullOrEmpty(options.EncodeDirectory))
                    {
                        string finalSnacksPath = Path.Combine(options.OutputDirectory, Path.GetFileName(outputPath));
                        await LogAsync(workItem.Id, $"Moving to output directory: {finalSnacksPath}");
                        await _fileService.FileMoveAsync(outputPath, finalSnacksPath);
                        outputPath = finalSnacksPath;
                    }

                    if (options.DeleteOriginalFile)
                    {
                        // Replace original: delete it, then move encoded file back to original location
                        await LogAsync(workItem.Id, "Replacing original file");
                        await _fileService.FileDeleteAsync(workItem.Path);

                        // Move back to the original's directory with a clean name (no [snacks] tag)
                        string originalDir = _fileService.GetDirectory(workItem.Path);
                        string cleanName = Path.GetFileNameWithoutExtension(outputPath).Replace(" [snacks]", "") + Path.GetExtension(outputPath);
                        string finalPath = Path.Combine(originalDir, cleanName);
                        await _fileService.FileMoveAsync(outputPath, finalPath);
                        await LogAsync(workItem.Id, $"Final output: {finalPath}");
                    }
                    else
                    {
                        await LogAsync(workItem.Id,
                            $"Original kept at: {workItem.Path}");
                        await LogAsync(workItem.Id,
                            $"Transcoded file at: {outputPath}");
                    }
                }
                else
                {
                    // In-place processing — output is in the same directory as the original with [snacks] tag
                    if (options.DeleteOriginalFile)
                    {
                        // Replace original: delete it and rename transcoded file to take its place
                        await LogAsync(workItem.Id, "Replacing original with transcoded version");
                        await _fileService.FileDeleteAsync(workItem.Path);

                        string cleanPath = GetCleanOutputName(outputPath);
                        await _fileService.FileMoveAsync(outputPath, cleanPath);
                        await LogAsync(workItem.Id, $"Final output: {cleanPath}");
                    }
                    else
                    {
                        // Keep both — original untouched, transcoded file has [snacks] tag
                        await LogAsync(workItem.Id,
                            $"Original kept at: {workItem.Path}");
                        await LogAsync(workItem.Id,
                            $"Transcoded file at: {outputPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                await LogAsync(workItem.Id, $"Error handling output placement: {ex.Message}");
                throw;
            }
        }
    }