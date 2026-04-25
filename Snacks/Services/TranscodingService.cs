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

        /// <summary>
        ///     Cancellation source for the active local job. Cancelled by
        ///     <see cref="CancelWorkItemAsync"/> to stop phases that run outside the ffmpeg
        ///     process (OCR pre-pass, sidecar extraction, tessdata downloads) — killing
        ///     <see cref="_activeProcess"/> isn't enough because those spawn their own
        ///     child processes that aren't registered as the active one.
        /// </summary>
        private CancellationTokenSource? _activeJobCts;

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

        /// <summary>Optional predicate to skip items for local processing (e.g. master excludes 4K).</summary>
        private Func<WorkItem, bool>? _shouldSkipLocal;

        /// <summary>Local encoding job counters.</summary>
        private int _localCompletedJobs;
        private int _localFailedJobs;

        /// <summary>Number of jobs completed by local encoding.</summary>
        public int LocalCompletedJobs => _localCompletedJobs;

        /// <summary>Number of jobs failed during local encoding.</summary>
        public int LocalFailedJobs => _localFailedJobs;

        /// <summary>Whether the encoding queue is paused by user request.</summary>
        public bool IsPaused => _isPaused;

        /// <summary>
        ///     Pauses or resumes the encoding queue. When resuming, restarts the processing loop
        ///     if <see cref="_lastOptions"/> is available.
        /// </summary>
        /// <param name="paused"> Whether to pause processing. </param>
        public void SetPaused(bool paused)
        {
            _isPaused = paused;
            Console.WriteLine($"Queue {(paused ? "paused" : "resumed")}");

            if (!paused && _lastOptions != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessQueueAsync(_lastOptions);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in ProcessQueueAsync: {ex.Message}");
                    }
                });
            }
        }

        private readonly NotificationService?        _notificationService;
        private readonly IntegrationService?         _integrationService;
        private readonly SubtitleExtractionService?  _subtitleExtractionService;
        private Func<bool>?                          _externalDispatchGate;
        private Func<ExclusionRules?>?               _exclusionRulesProvider;

        /// <summary>
        ///     Installs a predicate that returns <c>true</c> when this instance
        ///     should originate external-facing side effects (webhooks, library rescans).
        ///     Wired by <see cref="ClusterService"/> during its own construction to
        ///     avoid the TranscodingService ↔ ClusterService DI cycle. When unset
        ///     (standalone / tests), external dispatch defaults to enabled.
        /// </summary>
        public void SetExternalDispatchGate(Func<bool> gate) => _externalDispatchGate = gate;

        private bool ShouldDispatchExternal => _externalDispatchGate?.Invoke() ?? true;

        /// <summary>
        ///     Installs a provider for the current library <see cref="ExclusionRules"/> so manual
        ///     adds honor the same filename/size/resolution filters the auto-scanner applies.
        ///     Wired from <see cref="AutoScanService"/> (avoids the TranscodingService ↔ AutoScanService
        ///     DI cycle). When unset (tests / node mode) no exclusions are applied.
        /// </summary>
        public void SetExclusionRulesProvider(Func<ExclusionRules?> provider) =>
            _exclusionRulesProvider = provider;

        private ExclusionRules? GetExclusionRules() => _exclusionRulesProvider?.Invoke();

        /// <summary>
        ///     Initializes the service and eagerly starts hardware acceleration detection in the
        ///     background so that the first queue item does not pay the detection cost.
        /// </summary>
        public TranscodingService(
            FileService fileService,
            FfprobeService ffprobeService,
            IHubContext<TranscodingHub> hubContext,
            MediaFileRepository mediaFileRepo,
            NotificationService? notificationService = null,
            IntegrationService? integrationService = null,
            SubtitleExtractionService? subtitleExtractionService = null)
        {
            _fileService               = fileService;
            _ffprobeService            = ffprobeService;
            _hubContext                = hubContext;
            _mediaFileRepo             = mediaFileRepo;
            _notificationService       = notificationService;
            _integrationService        = integrationService;
            _subtitleExtractionService = subtitleExtractionService;
            _ffmpegPath          = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";

            _ = Task.Run(async () =>
            {
                try
                {
                    await DetectHardwareAccelerationAsync();
                    await _hubContext.Clients.All.SendAsync("HardwareDetected", _detectedHardware);
                }
                catch
                {
                    // Detection is best-effort; failure here does not block encoding.
                }
            });
        }

        /// <summary>
        ///     Sends a log line to all SignalR clients, forwards it to the master node if this is
        ///     a remote job, and appends it to the per-item log file on disk.
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

            // Fallback: search logs directory by short ID — picks up remote job logs
            // written by ClusterService that aren't in the local _workItems dictionary.
            var shortId = workItemId.Length > 8 ? workItemId[..8] : workItemId;
            var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");
            if (Directory.Exists(logsDir))
            {
                try
                {
                    var match = Directory.GetFiles(logsDir, $"*_{shortId}.log").FirstOrDefault();
                    if (match != null)
                        return File.ReadAllLines(match).ToList();
                }
                catch { }
            }

            return new List<string>();
        }

        /// <summary>
        ///     Returns the log file path for a work item, named after the video file for easy browsing.
        ///     Returns <c>null</c> if the work item ID is not found in the dictionary.
        /// </summary>
        private string? GetLogFilePath(string workItemId)
        {
            if (_workItems.TryGetValue(workItemId, out var workItem))
                return BuildLogFilePath(workItemId, workItem.FileName);
            return null;
        }

        /// <summary>
        ///     Builds the log file path from a work item ID and file name. Public so that
        ///     ClusterService can write remote job logs to the same location.
        /// </summary>
        public string BuildLogFilePath(string workItemId, string fileName)
        {
            var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");
            var safeName = string.Join("_", _fileService.RemoveExtension(fileName).Split(Path.GetInvalidFileNameChars()));
            var shortId = workItemId.Length > 8 ? workItemId[..8] : workItemId;
            return Path.Combine(logsDir, $"{safeName}_{shortId}.log");
        }

        /// <summary>
        ///     Probes a file, checks skip conditions (already target codec, 4K skip, VAAPI limits),
        ///     performs DB change detection, and enqueues the file for encoding if eligible.
        ///     Starts the processing loop in the background.
        /// </summary>
        /// <param name="filePath">Absolute path to the video file.</param>
        /// <param name="options">Encoder options for this job.</param>
        /// <param name="force">When <c>true</c>, bypasses DB status checks — used for explicit user selection.</param>
        /// <returns>The work item ID (may be a previously existing ID if the file was already tracked).</returns>
        public async Task<string> AddFileAsync(string filePath, EncoderOptions options, bool force = false, CancellationToken cancellationToken = default)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(filePath);
                var probe = await _ffprobeService.ProbeAsync(filePath, cancellationToken);

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

                long totalBitrate = length > 0 ? (long)(fileInfo.Length * 8 / length / 1000) : 0;
                long bitrate = totalBitrate;

                // For files near the target bitrate, measure the actual video-only bitrate
                // with a quick 15s copy pass. Total bitrate includes audio/subs which can
                // inflate the number by 1000-4000kbps and cause bad skip/encode decisions.
                // Only bother for files where audio could tip the decision — within 2x target.
                int effectiveTarget = probe.Streams.Any(s => s.CodecType == "video" && s.Width > 1920)
                    ? options.TargetBitrate * Math.Clamp(options.FourKBitrateMultiplier, 2, 8)
                    : options.TargetBitrate;
                if (bitrate > 0 && bitrate <= effectiveTarget * 2 && length > 5)
                {
                    long videoBitrate = await MeasureVideoBitrateAsync(new WorkItem { Path = filePath, Length = length });
                    if (videoBitrate > 0 && videoBitrate <= totalBitrate)
                    {
                        Console.WriteLine($"Video bitrate for {_fileService.GetFileName(filePath)}: {videoBitrate}kbps (total was {totalBitrate}kbps)");
                        bitrate = videoBitrate;
                    }
                    else if (videoBitrate > totalBitrate)
                    {
                        Console.WriteLine($"Video bitrate measurement for {_fileService.GetFileName(filePath)} was {videoBitrate}kbps but total is only {totalBitrate}kbps — using total");
                    }
                }

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
                workItem.Is4K = isHighDef;

                var videoStream = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
                var normalizedPath = Path.GetFullPath(filePath);

                // Library exclusion rules — shared with the auto-scanner via a provider that
                // AutoScanService wires in StartAsync. Respect `force` so manually re-added
                // files aren't silently dropped when the user asks explicitly.
                if (!force)
                {
                    var exclusions = GetExclusionRules();
                    if (exclusions != null)
                    {
                        string? resolutionLabel = ExclusionRules.ClassifyResolution(
                            videoStream?.Width ?? 0, videoStream?.Height ?? 0);
                        if (exclusions.IsExcluded(
                                Path.GetFileName(normalizedPath),
                                fileInfo.Length,
                                resolutionLabel))
                        {
                            Console.WriteLine($"Excluded by library rules: {workItem.FileName}");
                            return workItem.Id;
                        }
                    }
                }

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
                        FileMtime = fileInfo.LastWriteTimeUtc.Ticks,
                        // Per-track summaries so the Mux re-evaluation on settings save can decide
                        // whether to flip this file back to Unseen without a re-probe.
                        AudioStreams    = MuxStreamSummary.Serialize(ProjectAudioSummaries(probe)),
                        SubtitleStreams = MuxStreamSummary.Serialize(ProjectSubtitleSummaries(probe)),
                    });
                }

                // Bypass the skip gate only when a non-Transcode mode has actual work to do on
                // this file. A pointless remux of a file that already matches every audio/subtitle
                // setting is never what the user wants.
                bool hasMuxableWork = HasMuxableWork(options, probe);
                bool bypassSkip     = options.EncodingMode != EncodingMode.Transcode && hasMuxableWork;

                // MuxOnly: video is never re-encoded, so a file with no muxable audio/sub work
                // has nothing to do — skip it unconditionally, even if it's above the bitrate target.
                if (options.EncodingMode == EncodingMode.MuxOnly && !hasMuxableWork)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: MuxOnly mode, no audio/subtitle work");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                if (options.Skip4K && isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: 4K video (Skip 4K enabled)");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                string targetCodecLabel = targetIsAv1 ? "AV1" : (isHevc ? "HEVC" : "H.264");
                double skipMultiplier = 1.0 + (Math.Clamp(options.SkipPercentAboveTarget, 0, 100) / 100.0);
                if (alreadyTargetCodec && bitrate > 0 && bitrate <= options.TargetBitrate * skipMultiplier && !isHighDef && !bypassSkip)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {targetCodecLabel} at {bitrate}kbps (target {options.TargetBitrate}kbps, skip threshold {skipMultiplier:P0})");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                int fourKMultiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
                int fourKTarget = options.TargetBitrate * fourKMultiplier;
                if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= fourKTarget * skipMultiplier && !bypassSkip)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {targetCodecLabel} 4K at {bitrate}kbps (4K target {fourKTarget}kbps)");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                // Skip low-bitrate non-HEVC files when using VAAPI CQP — it can't target specific bitrates.
                // Check both explicit VAAPI selection and "auto" (which resolves to VAAPI on Linux NAS).
                bool isVaapiMode = IsVaapiAcceleration(options.HardwareAcceleration) ||
                    (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                     _detectedHardware != null && IsVaapiAcceleration(_detectedHardware));
                if (isVaapiMode && !isHevc && targetIsHevc && bitrate > 0 && bitrate <= options.TargetBitrate && !isHighDef && !bypassSkip)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: VAAPI can't compress {bitrate}kbps H.264 below target");
                    await MarkSkippedInDb();
                    return workItem.Id;
                }

                Console.WriteLine($"Queuing {workItem.FileName}: {sourceCodec} {bitrate}kbps {(isHighDef ? "4K" : "HD")}");

                if (_workItems.Values.Any(w =>
                    Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing
                        or WorkItemStatus.Uploading or WorkItemStatus.Downloading))
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
                    FileMtime = fileInfo.LastWriteTimeUtc.Ticks,
                    AudioStreams    = MuxStreamSummary.Serialize(ProjectAudioSummaries(probe)),
                    SubtitleStreams = MuxStreamSummary.Serialize(ProjectSubtitleSummaries(probe)),
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
        ///     Adds all video files in a directory to the encoding queue.
        ///     Files are probed sequentially to avoid overwhelming NAS storage.
        ///     Encoding starts as soon as the first file is scanned — no waiting for the full batch.
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
                w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing
                    or WorkItemStatus.Uploading or WorkItemStatus.Downloading);
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

            // Diagnostic log — if cancel ever seems to "not work", this line tells us exactly
            // which branch fired and what the item's state was going in.
            Console.WriteLine($"Cancel: id={id} status={workItem.Status} assignedNode={workItem.AssignedNodeId ?? "<none>"} isActiveLocal={_activeWorkItem?.Id == id}");

            if (workItem.Status == WorkItemStatus.Pending)
            {
                workItem.Status = WorkItemStatus.Cancelled;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
            else if (workItem.Status is WorkItemStatus.Processing or WorkItemStatus.Uploading or WorkItemStatus.Downloading
                     && workItem.IsRemote)
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
                // Cancel the token first so phases that aren't backed by _activeProcess
                // (OCR pre-pass, sidecar extraction, tessdata downloads) stop too.
                CancelActiveJobCts();
                await KillActiveProcess(workItem, "Encoding cancelled by user.");
                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
            else if (workItem.Status is WorkItemStatus.Processing or WorkItemStatus.Uploading or WorkItemStatus.Downloading)
            {
                // Orphaned: processing/transferring but not assigned to a remote node or local encoder
                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Cancelled);
            }
        }

        private void CancelActiveJobCts()
        {
            CancellationTokenSource? cts;
            lock (_activeLock) { cts = _activeJobCts; }
            try { cts?.Cancel(); } catch { /* already disposed */ }
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
            else if (workItem.Status is WorkItemStatus.Processing or WorkItemStatus.Uploading or WorkItemStatus.Downloading
                     && workItem.IsRemote)
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
                CancelActiveJobCts();
                await KillActiveProcess(workItem, "Encoding stopped by user — will retry later.");
                workItem.Status = WorkItemStatus.Stopped;
                workItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Unseen);
            }
            else if (workItem.Status is WorkItemStatus.Processing or WorkItemStatus.Uploading or WorkItemStatus.Downloading)
            {
                // Orphaned: processing/transferring but not assigned to a remote node or local encoder
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
        ///     Main queue processing loop. Dequeues items one at a time and encodes them locally.
        ///     Stops if the queue is paused or local encoding is suspended for cluster dispatch.
        ///     The <see cref="_processingLock"/> semaphore guarantees only one instance runs at a time.
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
                        // Remove cancelled/stopped items
                        _workQueue.RemoveAll(w => w.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped);

                        // LINQ: first pending item that passes the local skip predicate
                        workItem = _workQueue.FirstOrDefault(w =>
                            w.Status == WorkItemStatus.Pending &&
                            (_shouldSkipLocal == null || !_shouldSkipLocal(w)));

                        if (workItem == null) break;
                        _workQueue.Remove(workItem);
                    }

                    // Clone from _lastOptions so settings changes take effect on the next item.
                    // _lastOptions gets updated when the user saves new settings.
                    var current = _lastOptions ?? options;
                    await ProcessWorkItemAsync(workItem, current.Clone());
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        ///     Transitions a work item to Processing, runs the conversion, and handles success/failure outcomes.
        ///     On cancellation, cleans up the partial output file. On failure, increments the DB failure count.
        /// </summary>
        private async Task ProcessWorkItemAsync(WorkItem workItem, EncoderOptions options)
        {
            var jobCts = new CancellationTokenSource();
            lock (_activeLock)
            {
                _activeWorkItem = workItem;
                _activeJobCts   = jobCts;
            }
            try
            {
                workItem.Status = WorkItemStatus.Processing;
                workItem.StartedAt = DateTime.UtcNow;
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Processing);

                if (_notificationService != null && ShouldDispatchExternal)
                    _ = _notificationService.NotifyEncodeStartedAsync(Path.GetFileName(workItem.Path));

                // Lazy probe: items restored from DB on startup don't have probe data yet
                if (workItem.Probe == null)
                {
                    workItem.Probe = await _ffprobeService.ProbeAsync(workItem.Path);
                    if (workItem.Length <= 0 && workItem.Probe?.Format?.Duration != null)
                        workItem.Length = _ffprobeService.DurationStringToSeconds(workItem.Probe.Format.Duration);
                }

                await ConvertVideoAsync(workItem, options, cancellationToken: jobCts.Token);

                workItem.Status = WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                Interlocked.Increment(ref _localCompletedJobs);
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);

                if (ShouldDispatchExternal)
                {
                    if (_notificationService != null)
                        _ = _notificationService.NotifyEncodeCompletedAsync(Path.GetFileName(workItem.Path), workItem.Size);
                    if (_integrationService != null)
                        _ = _integrationService.TriggerRescansAsync(workItem.Path);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelled — status already set by CancelWorkItemAsync, just clean up output
                var outputPath = GetOutputPath(workItem, options);
                try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _localFailedJobs);
                workItem.Status = WorkItemStatus.Failed;
                workItem.ErrorMessage = ex.Message;
                workItem.CompletedAt = DateTime.UtcNow;
                await _mediaFileRepo.IncrementFailureCountAsync(Path.GetFullPath(workItem.Path), ex.Message);

                if (_notificationService != null && ShouldDispatchExternal)
                    _ = _notificationService.NotifyEncodeFailedAsync(Path.GetFileName(workItem.Path), ex.Message);
            }
            finally
            {
                lock (_activeLock)
                {
                    _activeWorkItem = null;
                    _activeProcess = null;
                    _activeJobCts  = null;
                }
                jobCts.Dispose();
            }

            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
        }

        /// <summary>
        ///     Builds and executes an FFmpeg command for the given work item.
        ///     Handles hardware acceleration setup, VAAPI quality calibration, bitrate calculation,
        ///     stream mapping, and post-conversion validation and file placement.
        ///     On failure, invokes the retry chain (no subs → sw decode → sw encode).
        /// </summary>
        /// <param name="workItem">The item to encode.</param>
        /// <param name="options">Encoding options (may be mutated by retry logic).</param>
        /// <param name="stripSubtitles">When <c>true</c>, forces <c>-sn</c> to drop all subtitle streams.</param>
        /// <param name="forceSwDecode">When <c>true</c>, disables VAAPI hardware decoding while keeping VAAPI encoding.</param>
        /// <param name="useConservativeHwFlags">When <c>true</c>, drops optional hw-encoder flags that older GPUs reject (NVENC temporal AQ, QSV lookahead/extbrc). Set by the retry chain after an encoder-feature failure.</param>
        /// <param name="cachedOcrSrts">OCR'd SRT results from an earlier attempt. When non-null, the OCR pass is skipped and these files are muxed as-is.</param>
        /// <param name="cachedOcrMuxTmpDir">Temp dir that owns the files in <paramref name="cachedOcrSrts"/>. Paired with the cache so cleanup still fires on success / exhaustion.</param>
        /// <param name="cancellationToken">Token to abort the encode mid-stream.</param>
        private async Task ConvertVideoAsync(
            WorkItem workItem,
            EncoderOptions options,
            bool stripSubtitles = false,
            bool forceSwDecode = false,
            bool useConservativeHwFlags = false,
            IReadOnlyList<SubtitleExtractionService.OcrMuxResult>? cachedOcrSrts = null,
            string? cachedOcrMuxTmpDir = null,
            CancellationToken cancellationToken = default)
        {
            if (workItem.Probe == null)
                throw new Exception("No probe data available");

            // Resolve "auto" to a concrete hardware type before building the command
            await ResolveHardwareAccelerationAsync(options);
            await LogAsync(workItem.Id, $"Hardware acceleration: {options.HardwareAcceleration}");
            await LogAsync(workItem.Id, $"Video Bitrate: {workItem.Bitrate}kbps");

            // Original-language lookup: merge the source's original language into the keep-lists
            // for THIS job only (options is already a per-job clone from EncoderOptionsOverride).
            if (options.KeepOriginalLanguage && _integrationService != null)
            {
                var orig = await _integrationService.LookupOriginalLanguageAsync(
                    workItem.Path, options.OriginalLanguageProvider, cancellationToken);
                if (!string.IsNullOrEmpty(orig))
                {
                    await LogAsync(workItem.Id, $"Original language resolved: {orig}");
                    if (!options.AudioLanguagesToKeep.Contains(orig))
                        options.AudioLanguagesToKeep.Add(orig);
                    if (!options.SubtitleLanguagesToKeep.Contains(orig))
                        options.SubtitleLanguagesToKeep.Add(orig);
                }
                else
                {
                    await LogAsync(workItem.Id,
                        $"Original-language lookup ({options.OriginalLanguageProvider}) returned nothing; keeping configured languages.");
                }
            }

            var (targetBitrate, minBitrate, maxBitrate, videoCopy) = CalculateBitrates(workItem, options);

            // Mux pass: copy the video stream and only touch the stream types selected by
            // MuxStreams. Requires actual muxable work — otherwise re-encode normally (or, for
            // at-target files, the skip gate would have already skipped). EncodingMode decides
            // when a mux pass is chosen over a full re-encode:
            //   Hybrid  — mux pass only for files at the bitrate target; above-target re-encodes.
            //   MuxOnly — mux pass for every file with muxable work (files without work were
            //             already force-skipped upstream; video is never re-encoded).
            bool isMuxPass = options.EncodingMode != EncodingMode.Transcode &&
                HasMuxableWork(options, workItem.Probe!) &&
                (options.EncodingMode == EncodingMode.MuxOnly || MeetsBitrateTarget(workItem, options));
            bool doAudioWork    = !isMuxPass || options.MuxStreams is MuxStreams.Audio     or MuxStreams.Both;
            bool doSubtitleWork = !isMuxPass || options.MuxStreams is MuxStreams.Subtitles or MuxStreams.Both;
            if (isMuxPass)
            {
                videoCopy = true;
                await LogAsync(workItem.Id, $"Mux pass ({options.EncodingMode}, streams {options.MuxStreams}): copying video stream.");
            }

            // Resolve the actual encoder — verify it works, fall back to software if not
            string encoder = videoCopy ? "copy" : GetEncoder(options);
            string hwAccel = options.HardwareAcceleration;
            if (!videoCopy && !encoder.StartsWith("lib") && encoder != "copy")
            {
                string testHwFlags = GetInitFlags(hwAccel);
                if (!await TestEncoderAsync(testHwFlags, encoder))
                {
                    string swEncoder = GetSoftwareFallbackEncoder(options);
                    await LogAsync(workItem.Id,
                        $"{encoder} not available — falling back to {swEncoder}");
                    encoder = swEncoder;
                    hwAccel = "none"; // Don't use hardware init flags with software encoder
                }
            }
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
                // SVT-AV1 v4.1+ VBR (rc=1) undershoots -b:v by ~5% on full encodes.
                // -maxrate is only supported in CRF mode, not VBR. Inflate the target
                // by 5% to compensate for the systematic undershoot.
                long targetKbps = long.Parse(targetBitrate.TrimEnd('k'));
                long adjustedBv = (long)(targetKbps * 1.05);
                compressionFlags = $"-svtav1-params rc=1 -b:v {adjustedBv}k ";
            }
            else if (encoder.Contains("nvenc"))
            {
                // NVENC VBR doesn't strictly enforce -maxrate, but lookahead + adaptive QP
                // gets it within ~2-4%. This is the best VBR can do on NVENC — CBR would be
                // exact but wastes bits on simple scenes.
                // Conservative mode drops -temporal_aq 1, which older NVENC silicon rejects
                // ("Temporal AQ not supported" → encoder fails to open on Pascal and earlier).
                int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
                string aqFlags = useConservativeHwFlags ? "-spatial_aq 1" : "-spatial_aq 1 -temporal_aq 1";
                compressionFlags = $"-g 25 -rc vbr -rc-lookahead 32 {aqFlags} -b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            else if (encoder.Contains("amf"))
            {
                // AMF ignores -maxrate in generic VBR mode (confirmed bug in FFmpeg >= 7.1).
                // Use vbr_peak (peak-constrained VBR) with enforce_hrd to enforce the ceiling.
                int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
                compressionFlags = $"-g 25 -rc vbr_peak -enforce_hrd 1 -b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            else if (encoder.Contains("qsv"))
            {
                // QSV needs lookahead and extbrc for reliable maxrate enforcement.
                // Conservative mode drops both — iGPUs that don't implement oneVPL lookahead
                // fail surface handoff at frame 0 ("Invalid FrameType:0" on Tiger Lake).
                int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
                string laFlags = useConservativeHwFlags ? "" : "-extbrc 1 -look_ahead 1 -look_ahead_depth 40 ";
                compressionFlags = $"-g 25 {laFlags}-b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            else
            {
                // Software encoders (libx264, libx265) — standard VBR with maxrate enforcement.
                int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
                compressionFlags = $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }

            // Detect 10-bit content — VAAPI needs p010 format instead of nv12 for 10-bit
            bool is10Bit = workItem.Probe?.Streams?.Any(s =>
                s.CodecType == "video" && (s.PixFmt?.Contains("10") == true || s.Profile?.Contains("10") == true)) == true;

            // Filter decisions: crop / downscale / tonemap. All three produce SW filter expressions
            // that feed VideoFilterBuilder. A mux pass disables every video filter.
            string? cropExpr = null;
            if (options.RemoveBlackBorders && !isMuxPass)
            {
                var detected = await GetCropParametersAsync(workItem, options, workItem.Path);
                if (!string.IsNullOrEmpty(detected)) cropExpr = detected;
            }
            string? scaleExpr = isMuxPass ? null : ComputeScaleExpr(workItem, options);
            bool tonemap = options.TonemapHdrToSdr && !isMuxPass && FfprobeService.IsHdr(workItem.Probe!);
            bool hasFilter = cropExpr != null || scaleExpr != null || tonemap;

            // Any active filter forces a re-encode even if bitrate logic chose videoCopy.
            if (videoCopy && hasFilter)
            {
                await LogAsync(workItem.Id,
                    "Active filter (crop/downscale/tonemap) — re-encoding despite videoCopy eligibility.");
                videoCopy = false;
                encoder = GetEncoder(options);
                if (!encoder.StartsWith("lib") && encoder != "copy")
                {
                    string testHwFlags = GetInitFlags(hwAccel);
                    if (!await TestEncoderAsync(testHwFlags, encoder))
                    {
                        string swEncoder = GetSoftwareFallbackEncoder(options);
                        await LogAsync(workItem.Id, $"{encoder} not available — falling back to {swEncoder}");
                        encoder = swEncoder;
                        hwAccel = "none";
                    }
                }
                useVaapi = encoder.Contains("vaapi");
                compressionFlags = GetForcedReencodeCompressionFlags(
                    encoder, useVaapi, encoder == "libsvtav1",
                    targetBitrate, minBitrate, maxBitrate, useConservativeHwFlags);
            }

            // forceSwDecode: external retry path when VAAPI hwaccel fails mid-stream.
            // User's chosen strategy is "SW filters + hwupload", so any active filter on a
            // VAAPI path also forces SW decode — SW filter ops can't run on GPU frames.
            bool canHwDecode = !forceSwDecode && CanVaapiDecode(workItem.Probe);
            if (useVaapi && hasFilter) canHwDecode = false;
            if (useVaapi && !canHwDecode && (forceSwDecode || hasFilter))
            {
                await LogAsync(workItem.Id,
                    "Using software decode + VAAPI encode (hwaccel decode disabled)");
            }

            string initFlags = useVaapi ? GetInitFlags(hwAccel, canHwDecode) : GetInitFlags(hwAccel);
            // After tonemap the frame is 8-bit SDR; p010 would waste bandwidth on the hwupload.
            string vaapiFormat = (is10Bit && !tonemap) ? "p010" : "nv12";
            string vfFlag = VideoFilterBuilder.Emit(
                cropExpr: cropExpr, tonemap: tonemap, scaleExpr: scaleExpr,
                useVaapi: useVaapi, canHwDecode: canHwDecode, vaapiFormat: vaapiFormat);
            bool isSvtAv1 = encoder == "libsvtav1";
            string presetFlag = useVaapi
                ? (useLowPower ? "-low_power 1 " : "")
                : isSvtAv1 ? $"-preset {MapSvtAv1Preset(options.FfmpegQualityPreset)} "
                           : $"-preset {options.FfmpegQualityPreset} ";
            string videoFlags = videoCopy ?
                $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v copy " :
                $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v {encoder} {presetFlag}{vfFlag}";

            // On a mux pass that excludes audio (MuxStreams.Subtitles), keep every audio track as-is:
            // empty language list = keep all, "copy" codec = no re-encode, no downmix.
            string audioFlags = _ffprobeService.MapAudio(
                workItem.Probe!,
                doAudioWork ? options.AudioLanguagesToKeep : new List<string>(),
                doAudioWork ? options.AudioCodec : "copy",
                options.AudioBitrateKbps,
                doAudioWork && options.TwoChannelAudio,
                options.Format == "mkv") + " ";

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

            // Pre-pass sidecar subtitle extraction — runs BEFORE the main encode so the
            // encoded output never carries tracks that are being extracted. The main encode
            // then strips all subs via stripSubtitles=true.
            var ocrMuxSrts  = new List<SubtitleExtractionService.OcrMuxResult>();
            string? ocrMuxTmpDir = null;

            if (cachedOcrSrts != null)
            {
                // Retry path: an earlier attempt already produced the SRTs. Reuse them
                // instead of re-OCR'ing thousands of PGS cues — OCR is the single most
                // expensive step in the pipeline.
                ocrMuxSrts.AddRange(cachedOcrSrts);
                ocrMuxTmpDir = cachedOcrMuxTmpDir;
            }
            else if (doSubtitleWork && options.ExtractSubtitlesToSidecar && !stripSubtitles && _subtitleExtractionService != null)
            {
                string sidecarBase = Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    Path.GetFileNameWithoutExtension(outputPath));
                await _subtitleExtractionService.ExtractAsync(
                    workItem, inputPath, sidecarBase,
                    options.SubtitleLanguagesToKeep,
                    options.SidecarSubtitleFormat,
                    options.ConvertImageSubtitlesToSrt,
                    msg => LogAsync(workItem.Id, msg),
                    cancellationToken);
                stripSubtitles = true;
            }
            else if (doSubtitleWork && options.ConvertImageSubtitlesToSrt && !stripSubtitles && _subtitleExtractionService != null)
            {
                // OCR-without-sidecar: run OCR and mux the resulting SRTs back into the main output
                // as text subtitle tracks, so the user gets a single file with searchable subtitles.
                ocrMuxTmpDir = Path.Combine(
                    Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work"),
                    "tmp", "ocrmux-" + Guid.NewGuid().ToString("N"));

                try
                {
                    var results = await _subtitleExtractionService.OcrBitmapsForMuxAsync(
                        workItem, inputPath, ocrMuxTmpDir,
                        options.SubtitleLanguagesToKeep,
                        msg => LogAsync(workItem.Id, msg),
                        cancellationToken);
                    ocrMuxSrts.AddRange(results);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation fires during OCR, before the main encode's try/catch. Clean
                    // up the temp dir ourselves since the encode path never runs.
                    TryCleanupOcrMuxDir(ocrMuxTmpDir);
                    throw;
                }
            }

            string subtitleFlags;
            string extraInputs = "";

            if (stripSubtitles)
            {
                subtitleFlags = "-sn ";
            }
            else if (ocrMuxSrts.Count > 0)
            {
                // Mux mode — build the subtitle args ourselves so we can interleave source text
                // subs (passed through as-is via per-stream -c:s:N copy) with the OCR'd SRTs
                // (encoded to the container's native text codec). Source bitmap subs are replaced
                // by the OCR output and so are dropped from the map.
                string ocrCodec = options.Format == "mkv" ? "srt" : "mov_text";

                var textSubs = _ffprobeService
                    .SelectSidecarStreams(workItem.Probe!, options.SubtitleLanguagesToKeep, includeBitmaps: false)
                    .Where(s => !s.IsBitmap)
                    .ToList();

                string maps   = "";
                string codecs = "";
                string meta   = "";
                int    outSubIndex = 0;

                foreach (var s in textSubs)
                {
                    maps   += $"-map 0:{s.StreamIndex} ";
                    codecs += $"-c:s:{outSubIndex} copy ";
                    // Matroska's language element and Plex/Jellyfin auto-select both key on
                    // ISO 639-2/B. Map the 2-letter tag if we can, otherwise pass through.
                    var tag = LanguageMatcher.ToThreeLetterB(s.Lang) ?? s.Lang;
                    if (!string.Equals(tag, "und", StringComparison.OrdinalIgnoreCase))
                        meta += $"-metadata:s:s:{outSubIndex} language={tag} ";
                    outSubIndex++;
                }

                for (int i = 0; i < ocrMuxSrts.Count; i++)
                {
                    extraInputs += $"-i \"{ocrMuxSrts[i].SrtPath}\" ";
                    maps        += $"-map {i + 1}:0 ";
                    codecs      += $"-c:s:{outSubIndex} {ocrCodec} ";
                    var tag = LanguageMatcher.ToThreeLetterB(ocrMuxSrts[i].Lang) ?? ocrMuxSrts[i].Lang;
                    if (!string.Equals(tag, "und", StringComparison.OrdinalIgnoreCase))
                        meta += $"-metadata:s:s:{outSubIndex} language={tag} ";

                    // Preserve the original track's title when present so "English" and
                    // "English [SDH]" remain distinguishable after OCR (was collapsing
                    // both to "OCR (eng)"). Fall back to "{Language} (OCR)" for tracks
                    // that were untitled in the source.
                    string title;
                    if (!string.IsNullOrWhiteSpace(ocrMuxSrts[i].Title))
                    {
                        title = $"{ocrMuxSrts[i].Title} (OCR)";
                    }
                    else
                    {
                        var name = LanguageMatcher.ToEnglishName(ocrMuxSrts[i].Lang) ?? ocrMuxSrts[i].Lang;
                        title = $"{name} (OCR)";
                    }
                    // Quotes need escaping for FFmpeg's metadata arg — replace " with a single quote.
                    meta += $"-metadata:s:s:{outSubIndex} title=\"{title.Replace("\"", "'")}\" ";
                    outSubIndex++;
                }

                subtitleFlags = maps + codecs + meta;
            }
            else
            {
                // On a mux pass that excludes subs (MuxStreams.Audio), keep every subtitle track by
                // passing an empty language list — MapSub treats that as "keep all".
                var subLangs = doSubtitleWork ? options.SubtitleLanguagesToKeep : new List<string>();
                subtitleFlags = _ffprobeService.MapSub(workItem.Probe!, subLangs, options.Format == "mkv") + " ";
            }

            // -analyzeduration and -probesize handle files with many streams (e.g. 30+ PGS subtitle tracks)
            string analyzeFlags = "-analyzeduration 10M -probesize 50M ";
            string command = $"{initFlags} {analyzeFlags}-i \"{inputPath}\" {extraInputs}{videoFlags}{compressionFlags}{audioFlags}{subtitleFlags}" +
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
                TryCleanupOcrMuxDir(ocrMuxTmpDir);
                throw; // Don't retry on cancellation
            }
            catch (Exception ex)
            {
                // Don't clean up the OCR tmp dir here — retries reuse the cached SRTs.
                // HandleConversionFailure is responsible for cleanup on final exhaustion.
                await HandleConversionFailure(workItem, options, outputPath, ex.Message, stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, cancellationToken: cancellationToken);
                return;
            }

            await Task.Delay(5000); // Wait for the filesystem to finish flushing the output before probing it.

            if (!File.Exists(outputPath))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Output file not found", stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, cancellationToken: cancellationToken);
                return;
            }

            var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
            if (!_ffprobeService.ConvertedSuccessfully(workItem.Probe!, outputProbe))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Duration mismatch detected", stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, cancellationToken: cancellationToken);
                return;
            }

            var outputSize = new FileInfo(outputPath).Length;
            float savings = (workItem.Size - outputSize) / 1048576f;
            float percent = 1 - ((float)outputSize / workItem.Size);
            workItem.OutputSize = outputSize;

            await LogAsync(workItem.Id,
                $"Converted successfully in {DateTime.Now.Subtract(startTime).TotalMinutes:0.00} minutes.");

            if (savings > 0 || videoCopy)
            {
                // Show both sizes explicitly so users don't misread the savings number
                // as the output size.
                await LogAsync(workItem.Id,
                    $"Size: {FormatSize(workItem.Size)} → {FormatSize(outputSize)}  (saved {FormatSize((long)(savings * 1048576))}, {percent:P0})");

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

            TryCleanupOcrMuxDir(ocrMuxTmpDir);
        }

        private static string FormatSize(long bytes)
        {
            // Matches formatFileSize() in wwwroot/js/site.js so log output is consistent
            // with the queue UI's labels (KB/MB/GB using base-1024).
            if (bytes <= 0) return "0 Bytes";
            string[] units = { "Bytes", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int i = 0;
            while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
            return $"{value:0.##} {units[i]}";
        }

        private static void TryCleanupOcrMuxDir(string? dir)
        {
            if (dir == null) return;
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best-effort — dir is under SNACKS_WORK_DIR/tmp, not user-facing */ }
        }

        /// <summary>
        ///     Returns <see langword="true"/> when audio settings would change the output
        ///     (language drop, codec re-encode, or downmix) versus a straight stream copy.
        /// </summary>
        private static bool HasAudioWork(EncoderOptions options, IReadOnlyList<AudioStreamSummary> audioStreams)
        {
            if (audioStreams.Count == 0) return false;

            // Language filter would drop at least one track?
            if (options.AudioLanguagesToKeep is { Count: > 0 } audLangs)
            {
                var kept = audioStreams
                    .Where(s => LanguageMatcher.Matches(s.Language, s.Title, audLangs))
                    .ToList();
                if (kept.Count < audioStreams.Count) return true;
                audioStreams = kept;
            }

            // Codec re-encode on any surviving track?
            if (!string.IsNullOrEmpty(options.AudioCodec)
                && !options.AudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in audioStreams)
                    if (!string.Equals(s.CodecName, options.AudioCodec, StringComparison.OrdinalIgnoreCase))
                        return true;
            }

            // Downmix would reduce channels on any surviving track?
            if (options.TwoChannelAudio)
            {
                foreach (var s in audioStreams)
                    if (s.Channels > 2) return true;
            }

            return false;
        }

        /// <summary>
        ///     Returns <see langword="true"/> when subtitle settings would change the output
        ///     (language drop, sidecar extraction, or OCR of bitmap subs) versus a straight copy.
        /// </summary>
        private static bool HasSubtitleWork(EncoderOptions options, IReadOnlyList<SubtitleStreamSummary> subStreams)
        {
            if (subStreams.Count == 0) return false;

            // Language filter would drop at least one track?
            if (options.SubtitleLanguagesToKeep is { Count: > 0 } subLangs)
            {
                var kept = subStreams
                    .Where(s => LanguageMatcher.Matches(s.Language, s.Title, subLangs))
                    .ToList();
                if (kept.Count < subStreams.Count) return true;
                subStreams = kept;
            }

            // Sidecar extraction with any text (or OCR-able bitmap) track present?
            if (options.ExtractSubtitlesToSidecar && subStreams.Count > 0) return true;

            // OCR requested and a bitmap track is actually present to OCR?
            if (options.ConvertImageSubtitlesToSrt)
            {
                foreach (var s in subStreams)
                    if (FfprobeService._bitmapSubCodecs.Contains(s.CodecName ?? ""))
                        return true;
            }

            return false;
        }

        /// <summary>
        ///     Projects a probe into the minimal audio summary shape used by <see cref="HasAudioWork" />.
        /// </summary>
        internal static List<AudioStreamSummary> ProjectAudioSummaries(ProbeResult probe) =>
            probe.Streams.Where(s => s.CodecType == "audio")
                .Select(s => new AudioStreamSummary
                {
                    Language  = s.Tags?.Language,
                    CodecName = s.CodecName,
                    Channels  = s.Channels,
                    Title     = s.Tags?.Title,
                })
                .ToList();

        /// <summary>
        ///     Projects a probe into the minimal subtitle summary shape used by <see cref="HasSubtitleWork" />.
        /// </summary>
        internal static List<SubtitleStreamSummary> ProjectSubtitleSummaries(ProbeResult probe) =>
            probe.Streams.Where(s => s.CodecType == "subtitle")
                .Select(s => new SubtitleStreamSummary
                {
                    Language  = s.Tags?.Language,
                    CodecName = s.CodecName,
                    Title     = s.Tags?.Title,
                })
                .ToList();

        /// <summary>
        ///     Returns <see langword="true"/> when <see cref="EncoderOptions.MuxStreams"/> selects a
        ///     non-video branch that has work to do on this file. Drives the skip-gate bypass so
        ///     at-target files still get a video-copy mux pass when their audio or subs need attention.
        ///     Always <see langword="false"/> in <see cref="EncodingMode.Transcode"/>.
        /// </summary>
        private static bool HasMuxableWork(EncoderOptions options, ProbeResult probe) =>
            HasMuxableWork(options, ProjectAudioSummaries(probe), ProjectSubtitleSummaries(probe));

        /// <summary>
        ///     Overload that takes the compact summary shape directly — used by the settings-save
        ///     re-evaluation path to decide whether a previously-skipped file should be re-queued.
        /// </summary>
        public static bool HasMuxableWork(
            EncoderOptions options,
            IReadOnlyList<AudioStreamSummary> audioStreams,
            IReadOnlyList<SubtitleStreamSummary> subtitleStreams)
        {
            if (options.EncodingMode == EncodingMode.Transcode) return false;
            return (options.MuxStreams is MuxStreams.Audio     or MuxStreams.Both && HasAudioWork(options, audioStreams))
                || (options.MuxStreams is MuxStreams.Subtitles or MuxStreams.Both && HasSubtitleWork(options, subtitleStreams));
        }

        /// <summary>
        ///     Pure function that mirrors the scan-phase skip gate using only <see cref="MediaFile"/>
        ///     fields (no probe required). Returns <see langword="true"/> when the file would still be
        ///     skipped under the given options — used on settings-save to decide whether to reset
        ///     <see cref="MediaFileStatus.Skipped"/> rows back to <see cref="MediaFileStatus.Unseen"/>.
        /// </summary>
        public static bool WouldSkipUnderOptions(MediaFile mf, EncoderOptions options)
        {
            // Skip4K overrides everything else.
            if (options.Skip4K && mf.Is4K) return true;

            // If the user's current Mux settings would turn this file into a mux pass, it's
            // no longer a skip candidate. We need the per-track summaries to answer that.
            bool muxable = HasMuxableWork(
                options,
                MuxStreamSummary.DeserializeAudio(mf.AudioStreams),
                MuxStreamSummary.DeserializeSubtitle(mf.SubtitleStreams));
            if (muxable) return false;

            // MuxOnly guarantees video is never re-encoded, so a file with no muxable work
            // is skipped regardless of its bitrate / codec.
            if (options.EncodingMode == EncodingMode.MuxOnly) return true;

            // Codec match — mirrors the scan-phase `alreadyTargetCodec` computation.
            bool targetIsHevc = options.Encoder.Contains("265");
            bool targetIsAv1  = options.Encoder.Contains("av1") || options.Encoder.Contains("svt");
            bool sourceIsAv1  = string.Equals(mf.Codec, "av1", StringComparison.OrdinalIgnoreCase);
            bool alreadyTargetCodec = targetIsAv1 ? sourceIsAv1
                                    : targetIsHevc ? mf.IsHevc
                                    : !mf.IsHevc;
            if (!alreadyTargetCodec || mf.Bitrate <= 0) return false;

            double skipMultiplier = 1.0 + (Math.Clamp(options.SkipPercentAboveTarget, 0, 100) / 100.0);
            int fourKMultiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
            long ceilingKbps = mf.Is4K
                ? options.TargetBitrate * fourKMultiplier
                : options.TargetBitrate;

            return mf.Bitrate <= ceilingKbps * skipMultiplier;
        }

        /// <summary>
        ///     Mirrors the scan-phase skip-gate check: <see langword="true"/> when the source is
        ///     already at the target codec and below the configured bitrate tolerance (with the
        ///     4K multiplier when applicable). Used at encode time to decide whether a configured
        ///     <see cref="EncodingMode.Hybrid"/> should trigger a video-copy mux pass.
        /// </summary>
        private static bool MeetsBitrateTarget(WorkItem workItem, EncoderOptions options)
        {
            if (workItem.Bitrate <= 0 || workItem.Probe == null) return false;

            var videoStream = workItem.Probe.Streams.FirstOrDefault(s => s.CodecType == "video");
            bool targetIsHevc = options.Encoder.Contains("265");
            bool targetIsAv1  = options.Encoder.Contains("av1") || options.Encoder.Contains("svt");
            bool sourceIsAv1  = string.Equals(videoStream?.CodecName, "av1", StringComparison.OrdinalIgnoreCase);
            bool alreadyTargetCodec = targetIsAv1 ? sourceIsAv1
                                    : targetIsHevc ? workItem.IsHevc
                                    : !workItem.IsHevc;
            if (!alreadyTargetCodec) return false;

            double skipMultiplier = 1.0 + (Math.Clamp(options.SkipPercentAboveTarget, 0, 100) / 100.0);
            int fourKMultiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
            int ceilingKbps = workItem.Is4K
                ? options.TargetBitrate * fourKMultiplier
                : options.TargetBitrate;

            return workItem.Bitrate <= ceilingKbps * skipMultiplier;
        }

        /// <summary>
        ///     Calculates target, min, and max bitrate strings for FFmpeg's VBR mode, and determines
        ///     whether to copy the video stream instead of re-encoding (when source is already HEVC at low bitrate).
        ///     4K content uses the configured multiplier; source bitrate is always respected as an upper cap.
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
            else if (workItem.Probe!.Streams.Any(x => x.Width > 1920) && !WillDownscaleBelow4K(options)) // 4k
            {
                int multiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
                int hdBitrate = options.TargetBitrate * multiplier;

                if (workItem.Bitrate < hdBitrate + 700 && !workItem.IsHevc)
                {
                    // Low-bitrate 4K H.264 — compress to 70% like non-4K
                    targetBitrate = $"{(int)(workItem.Bitrate * 0.7)}k";
                    minBitrate = $"{(int)(workItem.Bitrate * 0.6)}k";
                    maxBitrate = $"{(int)(workItem.Bitrate * 0.8)}k";
                }
                else
                {
                    targetBitrate = $"{hdBitrate}k";
                    minBitrate = $"{hdBitrate - 200}k";
                    maxBitrate = $"{hdBitrate + 500}k";
                }
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

        /// <summary>
        ///     Returns <c>true</c> when downscale is configured to a target height that
        ///     no longer qualifies as 4K (≤ 1440p). Used to skip the 4K bitrate multiplier
        ///     so a 4K→1080p downscale doesn't get allocated a 4K-sized bitrate budget.
        /// </summary>
        private static bool WillDownscaleBelow4K(EncoderOptions options)
        {
            if (!IsDownscalePolicyActive(options.DownscalePolicy)) return false;
            return ResolveDownscaleHeight(options.DownscaleTarget) <= 1440;
        }

        private static bool IsDownscalePolicyActive(string policy) =>
            string.Equals(policy, "Always",      StringComparison.OrdinalIgnoreCase)
         || string.Equals(policy, "CapAtTarget", StringComparison.OrdinalIgnoreCase)
         || string.Equals(policy, "IfLarger",    StringComparison.OrdinalIgnoreCase);

        private static int ResolveDownscaleHeight(string target) => target switch
        {
            "4K"    => 2160,
            "2160p" => 2160,
            "1440p" => 1440,
            "1080p" => 1080,
            "720p"  => 720,
            "480p"  => 480,
            _       => 1080,
        };

        /// <summary>
        ///     Resolves the downscale target height for an active policy, or <c>null</c>
        ///     when no scaling should occur. Returns the SW <c>scale=</c> expression;
        ///     <see cref="VideoFilterBuilder"/> keeps this form verbatim for the SW
        ///     hwupload path the user has chosen for all HW encoders.
        /// </summary>
        private static string? ComputeScaleExpr(WorkItem workItem, EncoderOptions options)
        {
            var policy = options.DownscalePolicy;
            if (!IsDownscalePolicyActive(policy)) return null;

            int targetH = ResolveDownscaleHeight(options.DownscaleTarget);

            var v = workItem.Probe?.Streams?.FirstOrDefault(s => s.CodecType == "video");
            int sourceH = v?.Height ?? 0;
            if (sourceH <= 0) return null;

            // "Always" downscales unconditionally; "CapAtTarget"/"IfLarger" only downscale
            // when the source is actually larger than the target.
            bool always = string.Equals(policy, "Always", StringComparison.OrdinalIgnoreCase);
            if (!always && sourceH <= targetH) return null;

            // -2 preserves aspect ratio and rounds to an even width (required by most encoders).
            return $"scale=w=-2:h={targetH}:flags=lanczos";
        }

        /// <summary>
        ///     Returns compression flags for filter-triggered re-encodes (crop, downscale,
        ///     tonemap). Matches the per-encoder rate-control logic of the main path but
        ///     skips VAAPI calibration — uses a fixed CQP since filter activation typically
        ///     already implies bitrate guesswork is acceptable for an exceptional case.
        /// </summary>
        private static string GetForcedReencodeCompressionFlags(string encoder, bool useVaapi, bool isSvtAv1,
            string targetBitrate, string minBitrate, string maxBitrate, bool useConservativeHwFlags)
        {
            int maxBitrateVal = int.Parse(maxBitrate.TrimEnd('k'));
            if (useVaapi)
                return $"-g 25 -rc_mode CQP -global_quality 25 ";
            if (isSvtAv1)
            {
                long tbr = long.Parse(targetBitrate.TrimEnd('k'));
                return $"-svtav1-params rc=1 -b:v {(long)(tbr * 1.05)}k ";
            }
            if (encoder.Contains("nvenc"))
            {
                string aqFlags = useConservativeHwFlags ? "-spatial_aq 1" : "-spatial_aq 1 -temporal_aq 1";
                return $"-g 25 -rc vbr -rc-lookahead 32 {aqFlags} -b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            if (encoder.Contains("amf"))
                return $"-g 25 -rc vbr_peak -enforce_hrd 1 -b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            if (encoder.Contains("qsv"))
            {
                string laFlags = useConservativeHwFlags ? "" : "-extbrc 1 -look_ahead 1 -look_ahead_depth 40 ";
                return $"-g 25 {laFlags}-b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            }
            if (encoder.Contains("videotoolbox"))
                return $"-b:v {targetBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
            return $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrateVal * 2}k ";
        }

        private string? _detectedHardware = null;

        /// <summary>
        ///     Detects available hardware acceleration by testing encoders.
        ///     Result is cached after first detection.
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

            // macOS GPU detection (VideoToolbox — works on both Apple Silicon and Intel Macs).
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Console.WriteLine("Auto-detect: Running on macOS, testing VideoToolbox...");
                if (await TestEncoderAsync("-hwaccel videotoolbox", "hevc_videotoolbox"))
                {
                    Console.WriteLine("Auto-detect: VideoToolbox available");
                    _detectedHardware = "apple";
                    return _detectedHardware;
                }
                Console.WriteLine("Auto-detect: VideoToolbox unavailable, using software");
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
        ///     Logs VAAPI diagnostic info (vainfo output) for troubleshooting.
        /// </summary>
        private async Task LogVaapiInfoAsync()
        {
            // VAAPI is Linux-only — skip entirely on Windows and macOS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
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
        ///     Tests whether a hardware encoder is functional by running a minimal encode.
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
                    extra = "-rc_mode CQP -global_quality 25";
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
        ///     Resolves "auto" to a concrete hardware acceleration type.
        ///     For explicit selections, returns as-is.
        /// </summary>
        private async Task ResolveHardwareAccelerationAsync(EncoderOptions options)
        {
            if (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                options.HardwareAcceleration = await DetectHardwareAccelerationAsync();
            }
        }

        /// <summary>
        ///     Returns <c>true</c> if the specified hardware acceleration mode maps to VAAPI (Intel/AMD on Linux).
        ///     Always returns <c>false</c> on Windows since VAAPI is Linux-only.
        /// </summary>
        private bool IsVaapiAcceleration(string hardwareAcceleration)
        {
            // VAAPI only exists on Linux — never use VAAPI paths on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            return hardwareAcceleration.ToLower() is "intel" or "amd";
        }

        /// <summary>
        ///     Returns <c>true</c> if the source video codec is supported by VAAPI hardware decoding
        ///     on Elkhart Lake (J6412) hardware (h264, hevc, mpeg2, vp8, vp9, mjpeg).
        /// </summary>
        private static bool CanVaapiDecode(ProbeResult? probe)
        {
            var codec = probe?.Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName;
            // J6412 (Elkhart Lake) VAAPI decode: h264, hevc, mpeg2, vp8, vp9, jpeg
            return codec is "h264" or "hevc" or "mpeg2video" or "vp8" or "vp9" or "mjpeg";
        }

        /// <summary>
        ///     Returns the FFmpeg initialization flags for the specified hardware acceleration mode.
        ///     On Windows, maps Intel → QSV and AMD → AMF. On Linux, maps Intel/AMD → VAAPI.
        ///     When <paramref name="hwDecode"/> is <c>false</c>, initializes the VAAPI device but skips
        ///     forcing hardware decode (software decode + VAAPI encode mode).
        /// </summary>
        private string GetInitFlags(string hardwareAcceleration, bool hwDecode = true)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            return hardwareAcceleration.ToLower() switch
            {
                "intel" when isWindows => "-y -hwaccel qsv -hwaccel_output_format qsv -qsv_device auto",
                "amd" when isWindows => "-y -hwaccel auto",
                // Software decode + VAAPI encode: init the device but don't force hwaccel decode
                "intel" when !hwDecode => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw",
                "amd" when !hwDecode => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw",
                "intel" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "amd" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "nvidia" => "-y -hwaccel cuda",
                "apple" => "-y -hwaccel videotoolbox",
                _ => "-y"
            };
        }

        /// <summary>
        ///     Maps the user's encoder preference and hardware acceleration setting to the
        ///     concrete FFmpeg encoder name (e.g., <c>"hevc_vaapi"</c>, <c>"hevc_nvenc"</c>, <c>"libx265"</c>).
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
                // H.264 encoders
                "intel" when isWindows && isH264 => "h264_qsv",
                "amd" when isWindows && isH264 => "h264_amf",
                "intel" when isH264 => "h264_vaapi",
                "amd" when isH264 => "h264_vaapi",
                "nvidia" when isH264 => "h264_nvenc",
                // H.265 encoders
                "intel" when isWindows && isH265 => "hevc_qsv",
                "amd" when isWindows && isH265 => "hevc_amf",
                "intel" when isH265 => "hevc_vaapi",
                "amd" when isH265 => "hevc_vaapi",
                "nvidia" when isH265 => "hevc_nvenc",
                // VideoToolbox (macOS). H.264 + HEVC encode in hardware. AV1 has no
                // av1_videotoolbox encoder in ffmpeg yet, so we use libsvtav1 software encode —
                // input is still hw-decoded via "-hwaccel videotoolbox" (see GetInitFlags),
                // ffmpeg auto-downloads frames to system memory before the software encoder.
                "apple" when isAv1 => "libsvtav1",
                "apple" when isH265 => "hevc_videotoolbox",
                "apple" when isH264 => "h264_videotoolbox",
                _ => options.Encoder
            };
        }

        /// <summary>
        ///     Returns the software fallback encoder for the user's codec preference.
        ///     Used when the requested hardware encoder isn't available on the system.
        /// </summary>
        private static string GetSoftwareFallbackEncoder(EncoderOptions options)
        {
            bool isAv1 = options.Encoder.Contains("av1") || options.Encoder.Contains("svt");
            bool isH264 = !isAv1 && options.Encoder.Contains("264");
            if (isAv1) return "libsvtav1";
            if (isH264) return "libx264";
            return "libx265";
        }

        /// <summary>
        ///     SVT-AV1 takes a numeric preset (0 = slowest/best, 13 = fastest/worst) instead
        ///     of the libx264/libx265 preset names. Maps the shared UI preset string into
        ///     SVT-AV1's range so a user who picks "slow" in the UI actually gets slower
        ///     encodes on AV1 too. Unknown values fall back to 6 (matches the prior hardcode).
        /// </summary>
        private static int MapSvtAv1Preset(string preset) => (preset ?? "").ToLowerInvariant() switch
        {
            "veryslow" => 2,
            "slow"     => 4,
            "medium"   => 6,
            "fast"     => 8,
            "veryfast" => 10,
            _          => 6,
        };

        /// <summary>
        ///     Computes the output file path for a work item.
        ///     Prefers <c>EncodeDirectory</c>, then <c>OutputDirectory</c>, then the source file's directory.
        ///     Output file is named <c>"{basename} [snacks].{ext}"</c>.
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
        ///     Returns the final "clean" path (without [snacks] tag) for output placement.
        /// </summary>
        private string GetCleanOutputName(string snacksPath)
        {
            string dir = Path.GetDirectoryName(snacksPath) ?? "";
            string fileName = Path.GetFileNameWithoutExtension(snacksPath).Replace(" [snacks]", "");
            string extension = Path.GetExtension(snacksPath);
            return Path.Combine(dir, fileName + extension);
        }

        /// <summary>
        ///     Does iterative test encodes at two file locations to find the right QP for the
        ///     target bitrate. Samples at ~25% and ~60% of the file (30s each) and averages the
        ///     results. For short files (<90s) falls back to a single sample. Starts at QP 24,
        ///     adjusts per iteration, and stops when within 15% of target or after 6 iterations.
        /// </summary>
        private async Task<(int qp, bool useLowPower)> CalibrateVaapiQualityAsync(WorkItem workItem, EncoderOptions options, string inputPath, long targetKbps)
        {
            int maxIterations = 6;
            double tolerance = 0.15; // within 15% of target

            // Build sample points: two 30s samples at 25% and 60%, or one shorter sample for short files
            var samples = new List<(string seekTime, int duration)>();
            if (workItem.Length >= 90)
            {
                samples.Add((FormatSeekTime((int)(workItem.Length * 0.25)), 30));
                samples.Add((FormatSeekTime((int)(workItem.Length * 0.60)), 30));
            }
            else
            {
                // Short file — single sample, capped to avoid overrunning the file
                int sampleDuration = Math.Max(5, (int)(workItem.Length * 0.4));
                int seekSeconds = Math.Max(0, (int)(workItem.Length * 0.30));
                // Don't seek + duration past the end of the file
                if (seekSeconds + sampleDuration > (int)workItem.Length)
                    seekSeconds = Math.Max(0, (int)workItem.Length - sampleDuration);
                samples.Add((FormatSeekTime(seekSeconds), sampleDuration));
            }

            bool canHwDecode = CanVaapiDecode(workItem.Probe);
            string initFlags = GetInitFlags(options.HardwareAcceleration, canHwDecode);
            string encoder = GetEncoder(options);
            // Use p010 for 10-bit content, nv12 for 8-bit
            bool is10Bit = workItem.Probe?.Streams?.Any(s =>
                s.CodecType == "video" && (s.PixFmt?.Contains("10") == true || s.Profile?.Contains("10") == true)) == true;
            string vaapiFormat = is10Bit ? "p010" : "nv12";
            // hwupload only needed for sw decode → hw encode; hw-decoded frames are already on the GPU
            string hwFilter = canHwDecode ? "" : $"-vf format={vaapiFormat}|vaapi,hwupload";

            // LP mode is Intel-specific (VAEntrypointEncSliceLP); AMD only supports VAEntrypointEncSlice
            bool isIntel = options.HardwareAcceleration.Equals("intel", StringComparison.OrdinalIgnoreCase);
            bool[] lowPowerModes = isIntel ? [true, false] : [false];
            foreach (bool lowPower in lowPowerModes)
            {
                string lpFlag = lowPower ? "-low_power 1 " : "";
                string modeLabel = lowPower ? "LP mode" : "normal mode";
                int currentQp = 24;
                // QP -> peak kbps from prior passes in this mode; used to detect oscillation
                // and bisect between a bracketing pair rather than re-testing the same QP.
                var tested = new Dictionary<int, long>();

                for (int iteration = 1; iteration <= maxIterations; iteration++)
                {
                    await LogAsync(workItem.Id,
                        $"Calibration pass {iteration}/{maxIterations} ({modeLabel}) — testing QP {currentQp} at {samples.Count} location(s)...");

                    // Run test encodes at each sample point, track per-sample bitrates
                    var sampleResults = new List<long>();
                    bool sampleFailed = false;
                    foreach (var (seekTime, duration) in samples)
                    {
                        long kbps = await RunTestEncodeAsync(inputPath, initFlags, encoder, hwFilter, lpFlag, currentQp, seekTime, duration);
                        if (kbps <= 0)
                        {
                            sampleFailed = true;
                            break;
                        }
                        if (workItem.Bitrate > 0 && kbps > workItem.Bitrate * 5)
                        {
                            sampleFailed = true;
                            if (lowPower)
                                await LogAsync(workItem.Id,
                                    $"LP mode output ({kbps}kbps at {seekTime}) is absurdly high — retrying without low_power...");
                            else
                                await LogAsync(workItem.Id,
                                    $"VAAPI output ({kbps}kbps at {seekTime}) is absurdly high — encoder broken for this file");
                            break;
                        }
                        sampleResults.Add(kbps);
                    }

                    if (sampleFailed)
                    {
                        if (lowPower) break; // try normal mode
                        return (-1, false);
                    }

                    // Use the peak (highest bitrate) sample for calibration — complex scenes
                    // are what overshoot the target, so we tune QP to the worst case
                    long peakKbps = sampleResults.Max();
                    long avgKbps = sampleResults.Sum() / sampleResults.Count;
                    tested[currentQp] = peakKbps;

                    double ratio = (double)peakKbps / targetKbps;
                    await LogAsync(workItem.Id,
                        $"Pass {iteration}: QP {currentQp} → peak {peakKbps}kbps, avg {avgKbps}kbps (target {targetKbps}kbps, ratio {ratio:F2}x)");

                    if (ratio >= (1 - tolerance) && ratio <= (1 + tolerance))
                    {
                        await LogAsync(workItem.Id,
                            $"QP {currentQp} is within {tolerance:P0} of target. Using QP {currentQp} ({modeLabel}).");
                        return (currentQp, lowPower);
                    }

                    // Already below target and at minimum QP — can't increase quality further
                    if (peakKbps <= targetKbps && currentQp <= 18)
                    {
                        await LogAsync(workItem.Id,
                            $"QP {currentQp} already at minimum and below target. Using QP {currentQp} ({modeLabel}).");
                        return (currentQp, lowPower);
                    }

                    // Pick next QP. If we already bracket the target, bisect within the
                    // bracket — converges in ~log2(range) passes and is immune to the
                    // encoder's nonlinear QP→bitrate curve. Only extrapolate when no
                    // bracket exists yet.
                    int? lowQp  = tested.Where(kv => kv.Value >  targetKbps).Select(kv => (int?)kv.Key).DefaultIfEmpty(null).Max(); // higher bitrate = lower QP
                    int? highQp = tested.Where(kv => kv.Value <= targetKbps).Select(kv => (int?)kv.Key).DefaultIfEmpty(null).Min(); // lower bitrate = higher QP

                    int nextQp;
                    if (lowQp.HasValue && highQp.HasValue)
                    {
                        if (highQp.Value - lowQp.Value <= 1)
                        {
                            await LogAsync(workItem.Id,
                                $"Calibration converged — adjacent QPs {lowQp}/{highQp} bracket target. Selecting best observed.");
                            break;
                        }

                        nextQp = (lowQp.Value + highQp.Value) / 2;
                        if (tested.ContainsKey(nextQp))
                            nextQp = (lowQp.Value + highQp.Value + 1) / 2;
                        if (tested.ContainsKey(nextQp))
                        {
                            await LogAsync(workItem.Id,
                                $"Calibration converged — bracket {lowQp}–{highQp} exhausted. Selecting best observed.");
                            break;
                        }
                    }
                    else
                    {
                        // No bracket — extrapolate. Fit the slope from prior samples when we
                        // have ≥2; the default 0.72x-per-+2QP heuristic is wrong for many
                        // VAAPI encoders (real slope is often ~0.5x per +2QP), so a fixed
                        // model overshoots and skips past the bracket on each pass.
                        double logSlopePerQp;
                        if (tested.Count >= 2)
                        {
                            var sorted = tested.OrderBy(kv => kv.Key).ToList();
                            var firstSample = sorted.First();
                            var lastSample  = sorted.Last();
                            double s = Math.Log((double)lastSample.Value / firstSample.Value) / (lastSample.Key - firstSample.Key);
                            // Reject positive/near-zero slopes (measurement noise) — fall back to default
                            logSlopePerQp = s < -0.05 ? s : Math.Log(0.72) / 2.0;
                        }
                        else
                        {
                            logSlopePerQp = Math.Log(0.72) / 2.0;
                        }

                        double qpDelta = Math.Log((double)targetKbps / peakKbps) / logSlopePerQp;
                        int adjustment = (int)Math.Round(qpDelta);
                        if (adjustment == 0) adjustment = peakKbps > targetKbps ? 1 : -1;
                        // Cap step to keep extrapolation sane — the QP→bitrate curve gets
                        // steeper at higher QP, so a long extrapolation often overshoots.
                        adjustment = Math.Clamp(adjustment, -4, 4);

                        nextQp = Math.Clamp(currentQp + adjustment, 18, 51);
                        if (tested.ContainsKey(nextQp))
                        {
                            // Predictor wants a duplicate — step further in the same direction
                            // to find a novel QP and establish the bracket.
                            int direction = peakKbps > targetKbps ? 1 : -1;
                            int candidate = nextQp + direction;
                            while (candidate >= 18 && candidate <= 51 && tested.ContainsKey(candidate))
                                candidate += direction;
                            if (candidate < 18 || candidate > 51)
                            {
                                await LogAsync(workItem.Id,
                                    $"Calibration exhausted on {modeLabel} — no novel QP available. Selecting best observed.");
                                break;
                            }
                            nextQp = candidate;
                        }
                    }

                    currentQp = nextQp;
                }

                // Pick the best tested QP: prefer highest-quality (lowest QP) whose peak
                // is at or under the upper tolerance bound; otherwise fall back to the QP
                // whose peak is closest to target so we don't return an untested last guess.
                if (tested.Count > 0)
                {
                    long upperBound = (long)(targetKbps * (1 + tolerance));
                    var underBound  = tested.Where(kv => kv.Value <= upperBound).ToList();
                    int chosenQp = underBound.Count > 0
                        ? underBound.OrderBy(kv => kv.Key).First().Key
                        : tested.OrderBy(kv => Math.Abs(kv.Value - targetKbps)).First().Key;

                    await LogAsync(workItem.Id,
                        $"Calibration complete after {tested.Count} pass(es). Using QP {chosenQp} " +
                        $"(peak {tested[chosenQp]}kbps vs target {targetKbps}kbps) ({modeLabel}).");
                    return (chosenQp, lowPower);
                }

                await LogAsync(workItem.Id,
                    $"Calibration complete after {maxIterations} passes. Using QP {currentQp} ({modeLabel}).");
                return (currentQp, lowPower);
            }

            // Both LP and normal mode failed
            return (-1, false);
        }

        /// <summary>
        ///     Measures the true video-only bitrate by remuxing 30 seconds of video
        ///     (no audio, no subs) with -c:v copy to null. This is near-instant since
        ///     it's just a copy, and gives us the actual video bitrate without audio
        ///     inflating the number. Returns 0 if measurement fails.
        /// </summary>
        private async Task<long> MeasureVideoBitrateAsync(WorkItem workItem)
        {
            const int sampleDuration = 15;

            // Seek to ~30% into the file for a representative sample
            int seekSeconds = Math.Max(0, (int)(workItem.Length * 0.30));
            // Don't seek past the end
            if (seekSeconds + sampleDuration > (int)workItem.Length)
                seekSeconds = Math.Max(0, (int)workItem.Length - sampleDuration);

            int actualDuration = Math.Min(sampleDuration, Math.Max(5, (int)workItem.Length));
            string seekTime = FormatSeekTime(seekSeconds);

            string command = $"-y -ss {seekTime} -i \"{workItem.Path}\" -t {actualDuration} " +
                $"-c:v copy -an -sn -f null -";

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

                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                var completed = process.WaitForExit(30000);
                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return 0;
                }

                var outputText = await stderrTask;
                var sizeMatch = Regex.Match(outputText, @"video:\s*(\d+)\s*(?:kB|KiB)");
                var timeMatch = Regex.Match(outputText, @"time=\s*(\d+):(\d+):(\d+)\.(\d+)");
                if (sizeMatch.Success && timeMatch.Success)
                {
                    long videoKb = long.Parse(sizeMatch.Groups[1].Value);
                    double measuredSeconds = int.Parse(timeMatch.Groups[1].Value) * 3600
                                           + int.Parse(timeMatch.Groups[2].Value) * 60
                                           + int.Parse(timeMatch.Groups[3].Value)
                                           + double.Parse("0." + timeMatch.Groups[4].Value);
                    if (measuredSeconds < 1) return 0;
                    return (long)(videoKb * 8 / measuredSeconds);
                }
            }
            catch { }

            return 0;
        }

        /// <summary> Formats a duration in seconds as an HH:MM:SS seek time string for FFmpeg. </summary>
        private static string FormatSeekTime(int seconds)
        {
            return $"{seconds / 3600:D2}:{(seconds % 3600) / 60:D2}:{seconds % 60:D2}";
        }

        /// <summary>
        ///     Calibrates the -b:v value for SVT-AV1 VBR mode by running iterative 30s
        ///     test encodes at two positions and adjusting until the output straddles
        ///     the target. SVT-AV1's VBR consistently undershoots by ~20-25%, so we
        ///     inflate -b:v until the measured output matches the desired bitrate.
        /// </summary>
        private async Task<long> CalibrateSvtAv1BitrateAsync(WorkItem workItem, EncoderOptions options, string inputPath, long targetKbps)
        {
            const int sampleDuration = 30;
            const int maxIterations = 6;

            // Two sample points at ~25% and ~60% of the file, with short-file fallback
            var samples = new List<(string seekTime, int duration)>();
            if (workItem.Length >= 90)
            {
                samples.Add((FormatSeekTime((int)(workItem.Length * 0.25)), sampleDuration));
                samples.Add((FormatSeekTime((int)(workItem.Length * 0.60)), sampleDuration));
            }
            else
            {
                int dur = Math.Max(5, (int)(workItem.Length * 0.4));
                int seekSec = Math.Max(0, (int)(workItem.Length * 0.30));
                if (seekSec + dur > (int)workItem.Length)
                    seekSec = Math.Max(0, (int)workItem.Length - dur);
                samples.Add((FormatSeekTime(seekSec), dur));
            }

            string initFlags = GetInitFlags(options.HardwareAcceleration);
            long currentBv = targetKbps;

            for (int iter = 1; iter <= maxIterations; iter++)
            {
                await LogAsync(workItem.Id,
                    $"SVT-AV1 calibration pass {iter}/{maxIterations} — testing b:v {currentBv}k...");

                var sampleBitrates = new List<long>();
                foreach (var (seekTime, dur) in samples)
                {
                    long measured = await RunSvtAv1TestEncodeAsync(inputPath, initFlags, currentBv, seekTime, dur);
                    if (measured > 0)
                        sampleBitrates.Add(measured);
                }

                if (sampleBitrates.Count == 0)
                {
                    await LogAsync(workItem.Id, "SVT-AV1 calibration failed — using target bitrate as-is");
                    return targetKbps;
                }

                long lo = sampleBitrates.Min();
                long hi = sampleBitrates.Max();
                long avg = sampleBitrates.Sum() / sampleBitrates.Count;

                await LogAsync(workItem.Id,
                    $"Pass {iter}: b:v {currentBv}k → lo={lo}k hi={hi}k avg={avg}k (target {targetKbps}k)");

                // Done when the average is within 5% of target — individual samples
                // can vary more due to content complexity, the average is what matters
                double avgError = Math.Abs((double)(avg - targetKbps) / targetKbps);
                if (avgError <= 0.05)
                    break;

                // Scale currentBv by the ratio of target to measured average
                double correctionRatio = (double)targetKbps / avg;
                currentBv = (long)(currentBv * correctionRatio);
                currentBv = Math.Clamp(currentBv, targetKbps, targetKbps * 4);
            }

            await LogAsync(workItem.Id, $"SVT-AV1 calibration complete — using b:v {currentBv}k for target {targetKbps}k");
            return currentBv;
        }

        /// <summary>
        ///     Runs a short SVT-AV1 VBR test encode and returns the measured bitrate in kbps.
        ///     Returns 0 if the encode produced no measurable output.
        /// </summary>
        private async Task<long> RunSvtAv1TestEncodeAsync(string inputPath, string initFlags, long bitrateKbps, string seekTime, int duration)
        {
            string command = $"{initFlags} -ss {seekTime} -i \"{inputPath}\" -t {duration} " +
                $"-c:v libsvtav1 -preset 6 -svtav1-params rc=1 -b:v {bitrateKbps}k " +
                $"-an -sn -f null -";

            Console.WriteLine($"SVT-AV1 calibration command: ffmpeg {command}");

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

                var stderrTask = process.StandardError.ReadToEndAsync();
                var stdoutTask = process.StandardOutput.ReadToEndAsync();

                var completed = process.WaitForExit(180000);
                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return 0;
                }

                var outputText = await stderrTask;
                var sizeMatch = Regex.Match(outputText, @"video:\s*(\d+)\s*(?:kB|KiB)");
                if (sizeMatch.Success)
                {
                    long outputKb = long.Parse(sizeMatch.Groups[1].Value);
                    return outputKb * 8 / duration;
                }
            }
            catch (Exception ex) { Console.WriteLine($"SVT-AV1 calibration test exception: {ex.Message}"); }

            return 0;
        }

        /// <summary>
        ///     Runs a short test encode to measure the actual output bitrate for a given VAAPI QP value.
        ///     Returns the measured bitrate in kbps, or 0 if the encode produced no output.
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
            return $"crop={mostCommonCrop}";
        }

        private async Task RunFfmpegAsync(string command, WorkItem workItem, CancellationToken cancellationToken = default)
        {
            // On Linux, FFmpeg's stderr switches to block-buffered when piped, which delays
            // progress reporting for minutes. Wrap with stdbuf to force line buffering.
            var usesStdbuf = !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                && File.Exists("/usr/bin/stdbuf");
            var processStartInfo = new ProcessStartInfo(usesStdbuf ? "/usr/bin/stdbuf" : _ffmpegPath)
            {
                Arguments = usesStdbuf ? $"-eL {_ffmpegPath} {command}" : command,
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
            var stderrTask = Task.Run(async () =>
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
            var stdoutTask = Task.Run(async () =>
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

            // Wait for stream readers to finish before disposing the process
            try { await stderrTask; } catch { }
            try { await stdoutTask; } catch { }

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

        private async Task HandleConversionFailure(
            WorkItem workItem,
            EncoderOptions options,
            string outputPath,
            string reason,
            bool subtitlesWereStripped,
            bool swDecodeWasForced = false,
            bool conservativeHwFlagsTried = false,
            IReadOnlyList<SubtitleExtractionService.OcrMuxResult>? cachedOcrSrts = null,
            string? cachedOcrMuxTmpDir = null,
            CancellationToken cancellationToken = default)
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

            // Classify the failure once — order of the retry tiers below is driven by this.
            bool isEncoderFeatureError =
                reason.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("Provided device doesn't support", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("Error while opening encoder", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("Invalid FrameType", StringComparison.OrdinalIgnoreCase) ||
                reason.Contains("Function not implemented", StringComparison.OrdinalIgnoreCase);

            bool isHwEncoder = !options.HardwareAcceleration.Equals("none", StringComparison.OrdinalIgnoreCase);

            // Retry 1: Hardware encoder rejected a feature flag (older NVENC silicon missing
            // Temporal AQ, QSV iGPUs missing oneVPL lookahead). Try this BEFORE stripping
            // subtitles — the failure has nothing to do with the sub streams, and stripping
            // them would drop the cached OCR tracks from the final output for no reason.
            if (isEncoderFeatureError && isHwEncoder && !conservativeHwFlagsTried)
            {
                await LogAsync(workItem.Id, "Retrying with conservative hardware encoder flags...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await ConvertVideoAsync(workItem, options,
                    stripSubtitles: subtitlesWereStripped,
                    forceSwDecode: swDecodeWasForced,
                    useConservativeHwFlags: true,
                    cachedOcrSrts: cachedOcrSrts,
                    cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                    cancellationToken: cancellationToken);
                return;
            }

            // Retry 2: Strip all subtitles (covers bitmap subs, broken streams, etc.)
            if (!subtitlesWereStripped)
            {
                await LogAsync(workItem.Id, "Retrying without subtitles...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await ConvertVideoAsync(workItem, options,
                    stripSubtitles: true,
                    useConservativeHwFlags: conservativeHwFlagsTried,
                    cachedOcrSrts: cachedOcrSrts,
                    cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                    cancellationToken: cancellationToken);
                return;
            }

            // Retry 3: Software decode + VAAPI encode for hwaccel filter graph errors
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
                await ConvertVideoAsync(workItem, options,
                    stripSubtitles: subtitlesWereStripped,
                    forceSwDecode: true,
                    useConservativeHwFlags: conservativeHwFlagsTried,
                    cachedOcrSrts: cachedOcrSrts,
                    cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                    cancellationToken: cancellationToken);
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
                await ConvertVideoAsync(workItem, options,
                    stripSubtitles: false,
                    cachedOcrSrts: cachedOcrSrts,
                    cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                    cancellationToken: cancellationToken);
                return;
            }

            // All retries exhausted — original file is untouched. Clean up the OCR tmp dir
            // now that no retry will reuse it.
            TryCleanupOcrMuxDir(cachedOcrMuxTmpDir);
            await LogAsync(workItem.Id, "All retries exhausted. Original file is unchanged.");
            throw new Exception($"Conversion failed after retries: {reason}");
        }

        /// <summary>Returns the work item currently being encoded locally, or <c>null</c> when idle.</summary>
        public WorkItem? GetActiveWorkItem() => _activeWorkItem;

        /// <summary>Returns the encoder options from the most recently started queue run.</summary>
        public EncoderOptions? GetLastOptions() => _lastOptions;

        /// <summary>
        ///     Updates the cached encoder options so that queued items pick up
        ///     settings changes without needing a new scan.
        /// </summary>
        public void UpdateOptions(EncoderOptions options) => _lastOptions = options;

        /// <summary>
        ///     Suspends or resumes the local processing loop. When suspended, pending items remain
        ///     in the queue for the cluster dispatch loop to assign to remote nodes instead.
        /// </summary>
        /// <param name="paused"> <see langword="true"/> to suspend local encoding; <see langword="false"/> to resume it. </param>
        public void SetLocalEncodingPaused(bool paused)
        {
            _localEncodingPaused = paused;
            Console.WriteLine($"Cluster: Local encoding {(paused ? "paused" : "resumed")}");

            // When unpausing, kick off queue processing for any items already waiting
            if (!paused && _lastOptions != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await ProcessQueueAsync(_lastOptions); }
                    catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync after unpause: {ex.Message}"); }
                });
            }
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

        /// <summary>Sets a predicate that decides whether a work item should be skipped for local processing (left for remote dispatch).</summary>
        public void SetLocalSkipPredicate(Func<WorkItem, bool>? predicate)
        {
            _shouldSkipLocal = predicate;

            // Re-trigger the processing loop so previously-skipped items get re-evaluated
            if (_lastOptions != null && !_isPaused && !_localEncodingPaused)
            {
                _ = Task.Run(async () =>
                {
                    try { await ProcessQueueAsync(_lastOptions); }
                    catch { }
                });
            }
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
        ///     Fast-path queue restoration from a database record. Skips ffprobe entirely by
        ///     building the WorkItem from persisted fields. The probe will be performed lazily
        ///     when the item is actually picked up for processing or cluster dispatch.
        ///     Used during startup to avoid re-probing every file in the queue.
        /// </summary>
        public async Task<string?> RestoreToQueueAsync(MediaFile dbFile, EncoderOptions options)
        {
            _lastOptions ??= options;

            if (!File.Exists(dbFile.FilePath))
                return null;

            var normalizedPath = Path.GetFullPath(dbFile.FilePath);

            // Skip if already in memory
            if (_workItems.Values.Any(w =>
                Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing
                    or WorkItemStatus.Uploading or WorkItemStatus.Downloading))
            {
                return null;
            }

            if (_isRemoteJobChecker != null && await _isRemoteJobChecker(normalizedPath))
                return null;

            var workItem = new WorkItem
            {
                FileName = dbFile.FileName,
                Path     = dbFile.FilePath,
                Size     = dbFile.FileSize,
                Bitrate  = dbFile.Bitrate,
                Length   = dbFile.Duration,
                IsHevc   = dbFile.IsHevc,
                Is4K     = dbFile.Is4K,
                Probe    = null // Lazily probed when processing starts
            };

            _workItems[workItem.Id] = workItem;
            lock (_queueLock)
            {
                _workQueue.Add(workItem);
                _workQueue.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));
            }

            await _hubContext.Clients.All.SendAsync("WorkItemAdded", workItem);
            return workItem.Id;
        }

        /// <summary>
        ///     Clears all in-memory state: kills any active FFmpeg process, clears the queue
        ///     and work items dictionary. Used by ClearHistoryAsync for a full reset. Does not
        ///     touch the database — the caller is responsible for DB reset.
        /// </summary>
        public async Task ClearAllInMemoryState()
        {
            Console.WriteLine("TranscodingService: Clearing all in-memory state...");

            // Kill active FFmpeg process
            WorkItem? activeItem;
            Process? activeProc;
            lock (_activeLock)
            {
                activeItem = _activeWorkItem;
                activeProc = _activeProcess;
                _activeWorkItem = null;
                _activeProcess = null;
            }

            if (activeProc != null)
            {
                try { if (!activeProc.HasExited) activeProc.Kill(entireProcessTree: true); } catch { }
            }

            if (activeItem != null)
            {
                activeItem.Status = WorkItemStatus.Stopped;
                activeItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", activeItem);
            }

            // Clear queue and work items
            lock (_queueLock)
            {
                _workQueue.Clear();
            }
            _workItems.Clear();

            Console.WriteLine("TranscodingService: In-memory state cleared");
        }

        /// <summary>
        ///     Atomically dequeues the next pending work item and marks it as Processing,
        ///     making it available for dispatch to a cluster node.
        /// </summary>
        /// <returns> The next pending <see cref="WorkItem"/>, or <see langword="null"/> if the queue is empty. </returns>
        public WorkItem? DequeueForRemoteProcessing(Func<WorkItem, bool>? filter = null)
        {
            lock (_queueLock)
            {
                var item = _workQueue.FirstOrDefault(w =>
                    w.Status == WorkItemStatus.Pending && (filter == null || filter(w)));
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
            workItem.OutputSize = outputSize;

            Console.WriteLine($"Cluster: Remote encode of {workItem.FileName}: {FormatSize(workItem.Size)} → {FormatSize(outputSize)} (saved {FormatSize((long)(savings * 1048576))}, {percent:P0})");

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

            if (ShouldDispatchExternal)
            {
                if (_notificationService != null)
                    _ = _notificationService.NotifyEncodeCompletedAsync(Path.GetFileName(workItem.Path), new FileInfo(workItem.Path).Length);
                if (_integrationService != null && savings > 0)
                    _ = _integrationService.TriggerRescansAsync(workItem.Path);
            }
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

                if (_notificationService != null && ShouldDispatchExternal)
                    _ = _notificationService.NotifyEncodeFailedAsync(Path.GetFileName(workItem.Path), errorMessage);
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
                    Is4K = probe.Streams.Any(s => s.CodecType == "video" && s.Width > 1920),
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
        ///     Runs the encoding pipeline for a job received from a master node.
        ///     Registers the work item so logging works, then delegates to <see cref="ConvertVideoAsync"/>.
        ///     The work item is kept in the dictionary after completion so the output remains accessible for download.
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
        ///     Moves the encoded output to its final destination, optionally deletes the original,
        ///     and renames the file from the <c>[snacks]</c> staging name to the clean final name.
        ///     No-op if the output file produced no size savings (already handled by the caller).
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
                        await MoveSidecarsAlongsideAsync(outputPath, finalSnacksPath, workItem);
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
                        await MoveSidecarsAlongsideAsync(outputPath, finalPath, workItem);
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
                        await MoveSidecarsAlongsideAsync(outputPath, cleanPath, workItem);
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

        /// <summary>
        ///     Moves any sidecar subtitle files that were written next to <paramref name="oldMainPath"/>
        ///     so they stay colocated with the main output when it's renamed/relocated. Matches
        ///     <c>{oldBasename}.*.{srt|ass|vtt}</c>; each matched file is relocated to the same
        ///     directory as <paramref name="newMainPath"/> with its language suffix preserved and
        ///     the base name updated to the new basename.
        /// </summary>
        private async Task MoveSidecarsAlongsideAsync(string oldMainPath, string newMainPath, WorkItem workItem)
        {
            try
            {
                string? oldDir = Path.GetDirectoryName(oldMainPath);
                if (string.IsNullOrEmpty(oldDir)) return;
                string oldBase = Path.GetFileNameWithoutExtension(oldMainPath);
                string newDir  = Path.GetDirectoryName(newMainPath) ?? oldDir;
                string newBase = Path.GetFileNameWithoutExtension(newMainPath);

                string[] exts  = { ".srt", ".ass", ".vtt" };
                var matches = Directory
                    .EnumerateFiles(oldDir, oldBase + ".*")
                    .Where(p => exts.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                foreach (var src in matches)
                {
                    // Preserve everything after the base name (e.g. ".en.srt", ".en.2.srt").
                    string tail = Path.GetFileName(src).Substring(oldBase.Length);
                    string dst  = Path.Combine(newDir, newBase + tail);
                    await _fileService.FileMoveAsync(src, dst);
                }
                if (matches.Count > 0)
                    await LogAsync(workItem.Id, $"Moved {matches.Count} sidecar file(s) alongside main output.");
            }
            catch (Exception ex)
            {
                // Sidecar move is best-effort — never fail the job over it.
                await LogAsync(workItem.Id, $"Sidecar relocation error (non-fatal): {ex.Message}");
            }
        }
    }