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

    /// <summary>
    ///     Per-job state for an in-flight local encode. Each entry pins a
    ///     work item to the device slot it consumed when scheduling started,
    ///     plus the running ffmpeg <see cref="System.Diagnostics.Process"/>
    ///     and a cancellation source that aborts not just ffmpeg but every
    ///     auxiliary child process the encode has spawned (OCR, sidecar
    ///     extraction, tessdata download).
    /// </summary>
    private sealed class ActiveLocalJob
    {
        public WorkItem                Item     = null!;
        public CancellationTokenSource Cts      = null!;
        public string                  DeviceId = "";
        public Process?                Process;
    }

    /// <summary>
    ///     All in-flight local encodes keyed by work-item ID. Replaces the
    ///     prior single <c>_activeProcess</c>/<c>_activeWorkItem</c> pair so
    ///     the master can run several encodes simultaneously, one per device
    ///     slot.
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveLocalJob> _activeLocalJobs = new();

    /// <summary>Lock protecting per-job <see cref="ActiveLocalJob.Process"/> publication.</summary>
    private readonly object _activeLock = new();

    /// <summary>
    ///     Wake signal raced against in-flight task completions inside the
    ///     scheduler's <c>WhenAny</c> waits. Settings changes (per-device cap
    ///     adjustments, enable toggles) trigger a wake so the scheduler
    ///     re-evaluates its dispatch decision immediately instead of
    ///     blocking on the currently running encode to finish first.
    /// </summary>
    private TaskCompletionSource _schedulerWake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    ///     Signals the scheduler to re-evaluate slot availability now.
    ///     Idempotent — repeated calls before the scheduler observes the
    ///     wake collapse into a single re-check.
    /// </summary>
    private void WakeScheduler()
    {
        var prev = Interlocked.Exchange(
            ref _schedulerWake,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        prev.TrySetResult();
    }

    /// <summary>
    ///     Awaits the next interesting event for the scheduler — either an
    ///     in-flight encode finishing or an explicit wake (settings change).
    ///     Falls back to a short poll when there are no in-flight tasks.
    /// </summary>
    private async Task WaitForSchedulerProgressAsync(List<Task> inflight)
    {
        var wake = _schedulerWake.Task;
        if (inflight.Count == 0)
        {
            await Task.WhenAny(wake, Task.Delay(200));
            return;
        }
        await Task.WhenAny(Task.WhenAny(inflight), wake);
    }

    /// <summary>Whether the local processing loop is paused by user request.</summary>
    private volatile bool _isPaused = false;

    /// <summary>Whether local encoding is suspended so the cluster dispatch loop can handle items instead.</summary>
    private volatile bool _localEncodingPaused = false;

    /// <summary>The encoder options from the most recently started queue run. Used when resuming after unpause.</summary>
    private EncoderOptions? _lastOptions;

    /// <summary>
    ///     Per-job callbacks to forward encoding progress to the master node.
    ///     Keyed by work-item ID so concurrent encodes on a multi-slot worker
    ///     route their progress back to their own job's reporter without
    ///     racing on a shared field.
    /// </summary>
    private readonly ConcurrentDictionary<string, Func<string, int, Task>> _progressCallbacks = new();

    /// <summary>
    ///     Per-job callbacks to forward FFmpeg log lines to the master node.
    ///     Same per-jobId isolation rationale as <see cref="_progressCallbacks"/>.
    /// </summary>
    private readonly ConcurrentDictionary<string, Func<string, string, Task>> _logCallbacks = new();

    /// <summary>Optional callback to cancel a remote job on a cluster node.</summary>
    private Func<string, string, Task>? _remoteJobCanceller;

    /// <summary>Optional async callback for the cluster to check whether a file is already assigned remotely.</summary>
    private Func<string, Task<bool>>? _isRemoteJobChecker;

    /// <summary>Optional predicate to skip items for local processing (e.g. master excludes 4K).</summary>
    private Func<WorkItem, bool>? _shouldSkipLocal;

    /// <summary>
    ///     Resolves per-device settings for <em>this</em> machine when the
    ///     master is encoding locally. Set by <see cref="ClusterService"/>
    ///     based on <see cref="NodeSettings.DeviceSettings"/> stored under
    ///     the master's own NodeId. <see langword="null"/> ⇒ no overrides;
    ///     every device runs at its detected default concurrency.
    /// </summary>
    private Func<string, (bool Enabled, int? MaxOverride)>? _localDeviceSettingsProvider;

    /// <summary>
    ///     Registers (or clears) the per-device settings provider used by
    ///     the multi-slot scheduler. The scheduler reads the provider on
    ///     <em>every</em> dispatch decision — there's no cached slot pool —
    ///     so a cap raised from 1 to 2 takes effect on the very next
    ///     iteration without leaking phantom tokens. After updating the
    ///     provider, we kick the queue runner so a previously-blocked
    ///     scheduler picks up the new headroom immediately rather than
    ///     waiting for the next event.
    /// </summary>
    public void SetLocalDeviceSettingsProvider(Func<string, (bool Enabled, int? MaxOverride)>? provider)
    {
        _localDeviceSettingsProvider = provider;

        // Wake the running scheduler so it re-evaluates dispatch immediately
        // — without this, raising a cap from 1 → 2 mid-run only takes effect
        // after the currently running encode completes, because the
        // scheduler is parked in WhenAny(inflight).
        WakeScheduler();

        // Also kick off ProcessQueueAsync in case no scheduler is running
        // (queue was idle waiting for an enable). Idempotent — the lock
        // bails early if a scheduler is already alive.
        if (_lastOptions != null && !_isPaused && !_localEncodingPaused)
        {
            _ = Task.Run(async () =>
            {
                try { await ProcessQueueAsync(_lastOptions); }
                catch (Exception ex) { Console.WriteLine($"ProcessQueueAsync after settings change failed: {ex.Message}"); }
            });
        }
    }

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
    private readonly EncodeHistoryRepository? _encodeHistoryRepo;

    /// <summary>
    ///     The master's own (NodeId, Hostname) for stamping local encode-history
    ///     rows. Set by <see cref="ClusterService"/> at startup so the dashboard
    ///     can attribute "encoded on this machine" without depending on
    ///     cluster-mode specifics.
    /// </summary>
    private (string NodeId, string Hostname) _localNodeIdentity = ("local", Environment.MachineName);

    /// <summary> Updates the local-node identity used when stamping encode-history rows. </summary>
    public void SetLocalNodeIdentity(string nodeId, string hostname)
    {
        _localNodeIdentity = (
            string.IsNullOrEmpty(nodeId) ? "local" : nodeId,
            string.IsNullOrEmpty(hostname) ? Environment.MachineName : hostname);
    }

    public TranscodingService(
        FileService fileService,
        FfprobeService ffprobeService,
        IHubContext<TranscodingHub> hubContext,
        MediaFileRepository mediaFileRepo,
        NotificationService? notificationService = null,
        IntegrationService? integrationService = null,
        SubtitleExtractionService? subtitleExtractionService = null,
        EncodeHistoryRepository? encodeHistoryRepo = null)
    {
        _fileService               = fileService;
        _ffprobeService            = ffprobeService;
        _hubContext                = hubContext;
        _mediaFileRepo             = mediaFileRepo;
        _notificationService       = notificationService;
        _integrationService        = integrationService;
        _subtitleExtractionService = subtitleExtractionService;
        _encodeHistoryRepo         = encodeHistoryRepo;
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
        // Any log line is a sign of life — refresh LastUpdatedAt so the watchdog
        // doesn't kill jobs that are emitting output but no formal progress ticks
        // (e.g. crop-detect, OCR pre-pass, hardware-accel probing).
        if (_workItems.TryGetValue(workItemId, out var logItem))
            logItem.Touch();

        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItemId, message);

        if (_logCallbacks.TryGetValue(workItemId, out var logCb))
            _ = logCb(workItemId, message);

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
            bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
            bool targetIsAv1 = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
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

            // No-op gate: an HEVC file just above the bitrate ceiling would otherwise fall through
            // to a videoCopy=true encode, and if the audio/sub pipeline has nothing to change either,
            // running ffmpeg just produces a near-identical output. Skip it instead.
            if (!bypassSkip && WouldEncodeBeNoOp(
                    options, bitrate, isHevc, videoStream?.Height ?? 0, FfprobeService.IsHdr(probe),
                    ProjectAudioSummaries(probe), ProjectSubtitleSummaries(probe)))
            {
                Console.WriteLine($"Skipping {workItem.FileName}: no work to do (video would copy, no audio/sub changes, no filters)");
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

    /// <summary>
    ///     Dry-run analysis of a directory: probes each video file and predicts whether it would
    ///     be queued, mux-passed, copied, or skipped under the given options, without touching
    ///     the database or queue. Decision logic mirrors <see cref="AddFileAsync"/>; keep in sync
    ///     when skip rules change. Skips the 15s video-only bitrate remeasurement, so files just
    ///     above the skip ceiling are flagged <see cref="FileAnalysisResult.Borderline"/> — the
    ///     real run may decide differently once the accurate video-only bitrate is known.
    /// </summary>
    /// <param name="directoryPath">Directory to scan.</param>
    /// <param name="options">Encoder options used for the prediction.</param>
    /// <param name="recursive">When <see langword="true"/>, subdirectories are included.</param>
    /// <param name="cancellationToken">Token to abort a long-running analysis.</param>
    public async Task<List<FileAnalysisResult>> AnalyzeDirectoryAsync(
        string directoryPath, EncoderOptions options, bool recursive = true, CancellationToken cancellationToken = default)
    {
        var directories = recursive
            ? _fileService.RecursivelyFindDirectories(directoryPath)
            : new List<string> { directoryPath };
        var videoFiles = _fileService.GetAllVideoFiles(directories);

        var results = new List<FileAnalysisResult>(videoFiles.Count);
        foreach (var file in videoFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await AnalyzeFileAsync(file, options, cancellationToken));
        }
        return results;
    }

    /// <summary>
    ///     Predicts what would happen to a single file under <paramref name="options"/>. Never
    ///     writes to the DB, never queues work. Returns a result with <c>Decision = "Error"</c>
    ///     when probing fails rather than throwing.
    /// </summary>
    private async Task<FileAnalysisResult> AnalyzeFileAsync(string filePath, EncoderOptions options, CancellationToken cancellationToken)
    {
        var result = new FileAnalysisResult
        {
            FilePath = filePath,
            FileName = _fileService.GetFileName(filePath),
        };

        try
        {
            var fileInfo = new FileInfo(filePath);
            result.SizeBytes = fileInfo.Length;

            var normalizedPath = Path.GetFullPath(filePath);
            var dbFile = await _mediaFileRepo.GetByPathAsync(normalizedPath);

            // Reuse cached probe data when the file on disk hasn't moved much since the DB
            // record was written. Same change-detection thresholds AddFileAsync uses (>10%
            // size delta or >30s duration delta = treat as a different file). Avoids an
            // expensive ffprobe on every file in large libraries.
            bool dbFresh = false;
            if (dbFile != null && dbFile.FileSize > 0 && !string.IsNullOrEmpty(dbFile.Codec))
            {
                double sizeDelta = Math.Abs(1.0 - (double)fileInfo.Length / dbFile.FileSize);
                bool mtimeMatches = dbFile.FileMtime == fileInfo.LastWriteTimeUtc.Ticks;
                dbFresh = mtimeMatches && sizeDelta <= 0.10;
            }

            string sourceCodec;
            long bitrate;
            int width, height;
            double length;
            bool isHevc, isAv1;
            ProbeResult? probe = null;
            string? pixelFormat = null;

            if (dbFresh)
            {
                sourceCodec = dbFile!.Codec;
                bitrate = dbFile.Bitrate;
                width = dbFile.Width;
                height = dbFile.Height;
                length = dbFile.Duration;
                isHevc = dbFile.IsHevc;
                isAv1 = string.Equals(sourceCodec, "av1", StringComparison.OrdinalIgnoreCase);
                pixelFormat = dbFile.PixelFormat;
            }
            else
            {
                probe = await _ffprobeService.ProbeAsync(filePath, cancellationToken);
                length = _ffprobeService.GetVideoDuration(probe);
                var videoStream = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
                sourceCodec = videoStream?.CodecName ?? "unknown";
                isHevc = sourceCodec == "hevc";
                isAv1 = sourceCodec == "av1";
                width = videoStream?.Width ?? 0;
                height = videoStream?.Height ?? 0;
                pixelFormat = videoStream?.PixFmt;
                bitrate = length > 0 ? (long)(fileInfo.Length * 8 / length / 1000) : 0;
            }

            result.Codec = sourceCodec;
            result.BitrateKbps = bitrate;
            result.Width = width;
            result.Height = height;
            result.Duration = length;

            bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
            bool targetIsAv1 = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
            bool alreadyTargetCodec = targetIsAv1 ? isAv1 : (targetIsHevc ? isHevc : !isHevc);
            bool isHighDef = width > 1920;
            result.Is4K = isHighDef;

            string targetCodecLabel = targetIsAv1 ? "AV1" : (targetIsHevc ? "HEVC" : "H.264");

            // Borderline = real run's remeasured video-only bitrate could flip Queue/Skip.
            // Window: 30% above the applicable ceiling — wider flagged nearly every file.
            double skipMultiplier = 1.0 + (Math.Clamp(options.SkipPercentAboveTarget, 0, 100) / 100.0);
            int fourKMultiplier = Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
            int fourKTarget = options.TargetBitrate * fourKMultiplier;
            int borderlineCeiling = isHighDef ? fourKTarget : options.TargetBitrate;
            borderlineCeiling = (int)(borderlineCeiling * skipMultiplier);
            result.Borderline = alreadyTargetCodec && bitrate > borderlineCeiling
                && bitrate <= borderlineCeiling * 1.3 && length > 5;

            // Library exclusion rules — same provider used by AddFileAsync. Mirrors the
            // non-forced path; analyze never has `force` because it's a preview.
            var exclusions = GetExclusionRules();
            if (exclusions != null)
            {
                string? resolutionLabel = ExclusionRules.ClassifyResolution(width, height);
                if (exclusions.IsExcluded(Path.GetFileName(normalizedPath), fileInfo.Length, resolutionLabel))
                {
                    result.Decision = "Excluded";
                    result.Reason  = "Excluded by library rules.";
                    return result;
                }
            }

            // DB status: split the AddFileAsync "previously failed/cancelled/completed" lump
            // into distinct decisions so the UI can group them on separate filter tabs.
            // Skipped status is not terminal — it gets re-evaluated under current options below.
            if (dbFile != null && dbFresh)
            {
                if (dbFile.Status == MediaFileStatus.Completed)
                {
                    result.Decision = "AlreadyCompleted";
                    result.Reason   = "Already encoded in a previous run.";
                    return result;
                }
                if (dbFile.Status == MediaFileStatus.Failed)
                {
                    result.Decision = "AlreadyFailed";
                    result.Reason   = $"Previous run failed ({dbFile.FailureCount} attempt{(dbFile.FailureCount == 1 ? "" : "s")}).";
                    return result;
                }
                if (dbFile.Status == MediaFileStatus.Cancelled)
                {
                    result.Decision = "AlreadyCancelled";
                    result.Reason   = "Cancelled in a previous run.";
                    return result;
                }
            }

            // EncodingMode/MuxStreams gates — must precede the bitrate/codec ladder so a
            // Hybrid+muxable file shows as Mux instead of Skip. We need stream summaries to
            // answer HasMuxableWork; build them from probe (or from the cached DB blob when
            // we skipped the probe).
            IReadOnlyList<AudioStreamSummary> audioStreams;
            IReadOnlyList<SubtitleStreamSummary> subtitleStreams;
            if (probe != null)
            {
                audioStreams    = ProjectAudioSummaries(probe);
                subtitleStreams = ProjectSubtitleSummaries(probe);
            }
            else
            {
                audioStreams    = MuxStreamSummary.DeserializeAudio(dbFile?.AudioStreams);
                subtitleStreams = MuxStreamSummary.DeserializeSubtitle(dbFile?.SubtitleStreams);
            }
            bool hasMuxableWork = HasMuxableWork(options, audioStreams, subtitleStreams);
            bool bypassSkip     = options.EncodingMode != EncodingMode.Transcode && hasMuxableWork;

            if (options.EncodingMode == EncodingMode.MuxOnly && !hasMuxableWork)
            {
                result.Decision = "Skip";
                result.Reason   = "MuxOnly mode and no audio/subtitle work to do.";
                return result;
            }

            if (options.Skip4K && isHighDef)
            {
                result.Decision = "Skip";
                result.Reason   = $"4K video ({width}×{height}) — Skip 4K enabled.";
                return result;
            }

            string tolLabel = options.SkipPercentAboveTarget > 0
                ? $" (target {options.TargetBitrate} + {options.SkipPercentAboveTarget}% tolerance)"
                : $" (target {options.TargetBitrate}kbps)";
            int skipCeilingHd = (int)(options.TargetBitrate * skipMultiplier);
            int skipCeiling4K = (int)(fourKTarget * skipMultiplier);

            if (alreadyTargetCodec && bitrate > 0 && bitrate <= skipCeilingHd && !isHighDef && !bypassSkip)
            {
                result.Decision = "Skip";
                result.Reason   = $"Already {targetCodecLabel} · {bitrate}kbps ≤ {skipCeilingHd}kbps{tolLabel}.";
                return result;
            }

            if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= skipCeiling4K && !bypassSkip)
            {
                result.Decision = "Skip";
                result.Reason   = $"Already {targetCodecLabel} 4K · {bitrate}kbps ≤ {skipCeiling4K}kbps (4K target {fourKTarget}kbps).";
                return result;
            }

            bool isVaapiMode = IsVaapiAcceleration(options.HardwareAcceleration) ||
                (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                 _detectedHardware != null && IsVaapiAcceleration(_detectedHardware));
            if (isVaapiMode && !isHevc && targetIsHevc && bitrate > 0 && bitrate <= options.TargetBitrate && !isHighDef && !bypassSkip)
            {
                result.Decision = "Skip";
                result.Reason   = $"VAAPI can't compress {bitrate}kbps H.264 below {options.TargetBitrate}kbps target.";
                return result;
            }

            // No-op gate (mirrors AddFileAsync). Detecting HDR requires the probe; when we used
            // cached DB data the safe fall-back is "isHdr unknown → don't claim no-op," which we
            // express by treating the cached path as never-HDR (matches AddFileAsync conservatively
            // since the DB doesn't carry HDR metadata).
            int sourceHeight = height;
            bool isHdr = probe != null && FfprobeService.IsHdr(probe);
            if (!bypassSkip && WouldEncodeBeNoOp(options, bitrate, isHevc, sourceHeight, isHdr, audioStreams, subtitleStreams))
            {
                result.Decision = "Skip";
                result.Reason   = $"Already {targetCodecLabel} at {bitrate}kbps · no audio/subtitle changes · no filters — nothing to do.";
                return result;
            }

            // The skip ladder cleared — predict what the encode pass actually does. When the
            // mode is Hybrid/MuxOnly and the file qualifies for a mux pass (already-target or
            // MuxOnly), the encoder runs with -c:v copy. Otherwise replicate CalculateBitrates.
            bool meetsBitrateTarget = alreadyTargetCodec && bitrate > 0
                && bitrate <= (isHighDef ? skipCeiling4K : skipCeilingHd);
            bool isMuxPass = options.EncodingMode != EncodingMode.Transcode && hasMuxableWork
                && (options.EncodingMode == EncodingMode.MuxOnly || meetsBitrateTarget);
            if (isMuxPass)
            {
                result.EncodeTargetKbps = 0;
                result.Decision = "Mux";
                result.Reason   = options.EncodingMode == EncodingMode.MuxOnly
                    ? $"MuxOnly · {targetCodecLabel} {bitrate}kbps — copy video, mux audio/subs."
                    : $"Hybrid mux pass · already {targetCodecLabel} at {bitrate}kbps — copy video, mux audio/subs.";
                return result;
            }

            // CalculateBitrates path — keep aligned with TranscodingService.CalculateBitrates.
            bool willDownscaleBelow4K = WillDownscaleBelow4K(options);
            bool useFourKBudget = isHighDef && !willDownscaleBelow4K;
            int hdReference = useFourKBudget ? fourKTarget : options.TargetBitrate;
            int encodeTarget;

            if (options.StrictBitrate)
            {
                encodeTarget    = options.TargetBitrate;
                result.Decision = "Queue";
                result.Reason   = $"{sourceCodec} {bitrate}kbps {(isHighDef ? "4K" : "HD")} → {targetCodecLabel} @ {encodeTarget}kbps (strict).";
            }
            else if (useFourKBudget && bitrate > 0 && bitrate < hdReference + 700 && !isHevc)
            {
                encodeTarget    = (int)(bitrate * 0.7);
                result.Decision = "Shrink";
                result.Reason   = $"{sourceCodec} 4K {bitrate}kbps below {hdReference + 700}kbps threshold → {targetCodecLabel} @ {encodeTarget}kbps (source × 0.7).";
            }
            else if (!useFourKBudget && bitrate > 0 && bitrate < options.TargetBitrate + 700 && !isHevc)
            {
                encodeTarget    = (int)(bitrate * 0.7);
                result.Decision = "Shrink";
                result.Reason   = $"{sourceCodec} {bitrate}kbps below {options.TargetBitrate + 700}kbps threshold → {targetCodecLabel} @ {encodeTarget}kbps (source × 0.7).";
            }
            else if (isHevc && bitrate > 0 && bitrate < options.TargetBitrate + 700
                     && !HasActiveFilter(options, sourceHeight, isHdr))
            {
                // CalculateBitrates' final video-copy override: HEVC source below target+700
                // with no active filter ⇒ -c:v copy (independent of the 4K branch). Filters
                // would flip videoCopy back to false at encode time, so we exclude that case
                // here and let it fall through to the Queue branch below.
                encodeTarget    = 0;
                result.Decision = "Copy";
                result.Reason   = $"Already HEVC at {bitrate}kbps · audio/sub work to apply → -c:v copy with stream re-mapping.";
            }
            else
            {
                encodeTarget    = bitrate > 0 ? (int)Math.Min(hdReference, bitrate) : hdReference;
                result.Decision = "Queue";
                result.Reason   = $"{sourceCodec} {bitrate}kbps {(isHighDef ? "4K" : "HD")} → {targetCodecLabel} @ {encodeTarget}kbps.";
            }
            result.EncodeTargetKbps = encodeTarget;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Decision = "Error";
            result.Reason   = $"Probe failed: {ex.Message}";
            return result;
        }
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
        Console.WriteLine($"Cancel: id={id} status={workItem.Status} assignedNode={workItem.AssignedNodeId ?? "<none>"} isActiveLocal={_activeLocalJobs.ContainsKey(id)}");

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
        else if (workItem.Status == WorkItemStatus.Processing && _activeLocalJobs.ContainsKey(id))
        {
            // Cancel the per-job token first so phases that aren't backed by
            // ffmpeg (OCR pre-pass, sidecar extraction, tessdata downloads)
            // stop too — peer encodes on other slots aren't affected.
            CancelLocalJobCts(id);
            await KillJobProcess(id, "Encoding cancelled by user.");
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

    /// <summary> Cancels a single local job's CTS without touching its peers. </summary>
    private void CancelLocalJobCts(string jobId)
    {
        if (_activeLocalJobs.TryGetValue(jobId, out var active))
        {
            try { active.Cts.Cancel(); } catch { /* already disposed */ }
        }
    }

    /// <summary>
    ///     Builds and persists an <see cref="EncodeHistory"/> row for a
    ///     completed local encode. Caller has already verified the encode
    ///     succeeded (didn't fail / cancel). Outcome is auto-classified by
    ///     comparing the encoded output size against the source.
    /// </summary>
    private async Task RecordLocalEncodeHistoryAsync(WorkItem workItem, EncoderOptions options, string deviceId)
    {
        if (_encodeHistoryRepo == null) return;
        try
        {
            var encodedSize = workItem.OutputSize ?? 0;
            var noSavings   = encodedSize == 0 || encodedSize >= workItem.Size;
            var encodeStart = workItem.StartedAt ?? workItem.CreatedAt;
            var srcCodec    = workItem.Probe?.Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName ?? "";

            var record = new EncodeHistory
            {
                JobId               = workItem.Id,
                FilePath            = workItem.Path,
                FileName            = workItem.FileName,
                OriginalSizeBytes   = workItem.Size,
                EncodedSizeBytes    = noSavings ? 0 : encodedSize,
                BytesSaved          = noSavings ? 0 : Math.Max(0, workItem.Size - encodedSize),
                OriginalCodec       = srcCodec,
                EncodedCodec        = options.Codec,
                OriginalBitrateKbps = workItem.Bitrate,
                EncodedBitrateKbps  = workItem.Length > 0 && encodedSize > 0
                    ? (long)(encodedSize * 8.0 / 1024.0 / workItem.Length)
                    : 0,
                DurationSeconds     = workItem.Length,
                EncodeSeconds       = (DateTime.UtcNow - encodeStart).TotalSeconds,
                DeviceId            = string.IsNullOrEmpty(deviceId) ? "cpu" : deviceId,
                NodeId              = _localNodeIdentity.NodeId,
                NodeHostname        = _localNodeIdentity.Hostname,
                WasRemote           = false,
                Is4K                = workItem.Is4K,
                StartedAt           = encodeStart,
                CompletedAt         = DateTime.UtcNow,
                Outcome             = noSavings ? "NoSavings" : "Completed",
            };
            await _encodeHistoryRepo.RecordAsync(record);
            await _hubContext.Clients.All.SendAsync("EncodeHistoryAdded", record);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EncodeHistory: local record failed for {workItem.Id}: {ex.Message}");
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
        else if (workItem.Status == WorkItemStatus.Processing && _activeLocalJobs.ContainsKey(id))
        {
            CancelLocalJobCts(id);
            await KillJobProcess(id, "Encoding stopped by user — will retry later.");
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

    /// <summary>
    ///     Kills the FFmpeg process for a specific local job and logs the
    ///     reason. Safe to call when no process is running for that job (e.g.
    ///     ffmpeg has already exited or hasn't spawned yet for this slot).
    ///     Other in-flight jobs on different slots are untouched.
    /// </summary>
    private async Task KillJobProcess(string jobId, string logMessage)
    {
        try
        {
            Process? proc;
            lock (_activeLock)
            {
                proc = _activeLocalJobs.TryGetValue(jobId, out var active) ? active.Process : null;
            }
            if (proc != null && !proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await LogAsync(jobId, logMessage);
            }
        }
        catch (Exception ex)
        {
            await LogAsync(jobId, $"Error killing process: {ex.Message}");
        }
    }

    /// <summary>
    ///     Multi-slot queue scheduler. Loops over the pending queue acquiring
    ///     per-device slots, spawns each eligible item as its own encode task,
    ///     and exits only when the queue is empty <em>and</em> all in-flight
    ///     encodes have completed. <see cref="_processingLock"/> still gates
    ///     re-entry so multiple callers (settings save, retry, unpause) don't
    ///     race a second scheduler.
    ///
    ///     <para>Each item is encoded with a <em>cloned</em>
    ///     <see cref="EncoderOptions"/> whose <see cref="EncoderOptions.HardwareAcceleration"/>
    ///     is pinned to the chosen slot's device family — the user's "auto"
    ///     stays a hint for slot selection, but the actual ffmpeg invocation
    ///     gets a concrete device so two simultaneous encodes don't both
    ///     auto-resolve to the same NVENC card and starve.</para>
    /// </summary>
    private async Task ProcessQueueAsync(EncoderOptions options)
    {
        if (!await _processingLock.WaitAsync(100))
        {
            // A scheduler is already running. Nudge it so it re-checks the
            // queue + per-device caps right now instead of staying parked
            // in WhenAny(inflight) until the currently running encode
            // finishes. Without this, adding new items (or raising a cap)
            // mid-run only takes effect after the running encode completes
            // — which surfaces as "I have 2 slots, only 1 fills".
            WakeScheduler();
            return;
        }

        _lastOptions = options;

        var inflight = new List<Task>();

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
                // leave items in the queue for the cluster dispatch loop. Wait
                // for any in-flight encodes that are still finishing before we
                // exit so the scheduler cleans up cleanly.
                if (_localEncodingPaused)
                    break;

                // Reap completed inflight tasks before checking for queue-empty exit.
                inflight.RemoveAll(t => t.IsCompleted);

                bool queueEmpty;
                lock (_queueLock)
                {
                    _workQueue.RemoveAll(w => w.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped);
                    queueEmpty = !_workQueue.Any(w =>
                        w.Status == WorkItemStatus.Pending &&
                        (_shouldSkipLocal == null || !_shouldSkipLocal(w)));
                }

                if (queueEmpty && inflight.Count == 0) break;

                // Queue is empty but encodes are still finishing — wait for
                // one to drain or for an explicit wake (settings change)
                // before re-checking.
                if (queueEmpty)
                {
                    await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // Pick a device that has free capacity right now. The check
                // reads the current count of in-flight encodes per device
                // and the user's current cap from the settings provider, so
                // a cap change applied while we're looping takes effect on
                // the very next iteration with no semaphore rebuilding.
                var current = _lastOptions ?? options;
                var deviceId = TryAcquireLocalDeviceSlot(current);
                if (deviceId == null)
                {
                    // No slot free yet — wait for an inflight to finish OR a
                    // settings-change wake. Without the wake, raising the cap
                    // mid-run would only take effect after the running encode
                    // finished, defeating the user's "let two run at once"
                    // intent.
                    await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // Pick the next pending item this device can encode.
                WorkItem? workItem;
                lock (_queueLock)
                {
                    workItem = _workQueue.FirstOrDefault(w =>
                        w.Status == WorkItemStatus.Pending &&
                        (_shouldSkipLocal == null || !_shouldSkipLocal(w)) &&
                        DeviceCanEncode(deviceId, w, current));
                    if (workItem != null) _workQueue.Remove(workItem);
                }

                if (workItem == null)
                {
                    // No item fits this device right now. Wait for inflight
                    // progress or settings-wake before reconsidering.
                    await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // Clone options so the per-job HW pin doesn't leak into the
                // shared instance, and so a settings change after dispatch
                // can't mutate the encode mid-run.
                var perJobOptions = current.Clone();
                perJobOptions.HardwareAcceleration = deviceId == "cpu" ? "none" : deviceId;

                // Pre-register the active job synchronously before spawning so
                // the scheduler's next iteration counts it against the device's
                // cap without a race window.
                var active = new ActiveLocalJob
                {
                    Item     = workItem,
                    Cts      = new CancellationTokenSource(),
                    DeviceId = deviceId,
                };
                _activeLocalJobs[workItem.Id] = active;
                workItem.DispatchedDeviceId = deviceId;

                var jobTask = ProcessWorkItemAsync(workItem, perJobOptions, active);
                inflight.Add(jobTask);
            }

            // Drain remaining inflight before releasing the scheduler lock so
            // a follow-up ProcessQueueAsync call can't reschedule items that
            // are still finishing on disk.
            if (inflight.Count > 0)
                await Task.WhenAll(inflight.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }
        finally
        {
            _processingLock.Release();
        }
    }

    /// <summary>
    ///     Returns the first device with free capacity for an in-flight encode,
    ///     or <see langword="null"/> if every device is disabled, codec-mismatched,
    ///     or already at its user-configured concurrency cap. Capacity is computed
    ///     from <see cref="_activeLocalJobs"/> live — no cached semaphores — so a
    ///     cap raised mid-run unlocks the next slot on the very next call.
    ///
    ///     <para>CPU rules:
    ///     <list type="bullet">
    ///         <item><c>none</c> (Software): CPU is the only acceptable slot.</item>
    ///         <item><c>auto</c> on a machine with no hardware encoders: CPU is the auto-fallback.</item>
    ///         <item>Anything else: CPU is excluded so jobs queue rather than spilling onto a slow software encode.</item>
    ///     </list>
    ///     The CPU slot is hidden from the override dialog and pinned to a
    ///     single concurrent encode regardless of any stale per-node setting.</para>
    /// </summary>
    private string? TryAcquireLocalDeviceSlot(EncoderOptions options)
    {
        var devices = GetDetectedDevices();
        if (devices.Count == 0) return null;

        var hwPref = (options.HardwareAcceleration ?? "auto").ToLowerInvariant();
        bool hasHardware = devices.Any(d => d.DeviceId != "cpu");

        foreach (var device in devices)
        {
            bool isCpu = device.DeviceId == "cpu";

            // Hardware-vs-CPU gating.
            if (hwPref == "none" && !isCpu) continue;                          // Software ⇒ CPU only
            if (hwPref != "none" && hwPref != "auto" && isCpu) continue;       // Specific vendor ⇒ never CPU
            if (hwPref == "auto" && isCpu && hasHardware) continue;            // Auto with HW ⇒ never CPU

            // Specific-vendor preference must match the device family exactly.
            if (hwPref != "auto" && hwPref != "none"
                && !string.Equals(hwPref, device.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;

            int cap;
            if (isCpu)
            {
                // CPU slot is hidden from the user and capped at 1.
                cap = 1;
            }
            else
            {
                var (enabled, maxOverride) = _localDeviceSettingsProvider?.Invoke(device.DeviceId)
                    ?? (true, (int?)null);
                if (!enabled) continue;
                cap = Math.Max(0, maxOverride ?? device.DefaultConcurrency);
                if (cap == 0) continue;
            }

            // Live count from the active-jobs map. This is the source of
            // truth for slot occupancy, so changing the user's cap takes
            // effect on the next iteration with zero state churn.
            var inUse = 0;
            foreach (var kv in _activeLocalJobs)
                if (kv.Value.DeviceId == device.DeviceId) inUse++;

            if (inUse < cap) return device.DeviceId;
        }
        return null;
    }

    /// <summary>
    ///     <see langword="true"/> if a local device can encode the work item under
    ///     the current options. Validates codec support: hardware devices may
    ///     not advertise every codec, but CPU always does. Wraps the codec
    ///     name aliasing the rest of the codebase uses (h265/hevc, h264/avc).
    /// </summary>
    private bool DeviceCanEncode(string deviceId, WorkItem workItem, EncoderOptions options)
    {
        var device = GetDetectedDevices().FirstOrDefault(d => d.DeviceId == deviceId);
        if (device == null) return deviceId == "cpu"; // CPU always works as a fallback

        var codec = (options.Codec ?? "").ToLowerInvariant();
        var key   = codec switch
        {
            "hevc" or "h265" => "h265",
            "avc"  or "h264" => "h264",
            "av1"            => "av1",
            _                => codec,
        };
        if (string.IsNullOrEmpty(key)) return true; // unknown codec — let ffmpeg decide
        return device.SupportedCodecs.Any(c => c.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Transitions a work item to Processing, runs the conversion bound to
    ///     the supplied <see cref="ActiveLocalJob"/>, and handles success /
    ///     failure outcomes. The active-jobs registry entry is removed in the
    ///     finally block so the next iteration of <see cref="ProcessQueueAsync"/>
    ///     sees the slot as free.
    ///     On cancellation, cleans up the partial output file. On failure,
    ///     increments the DB failure count.
    /// </summary>
    private async Task ProcessWorkItemAsync(WorkItem workItem, EncoderOptions options, ActiveLocalJob active)
    {

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

            // Per-job watchdog: aborts the job if no log line, status change, or progress
            // tick has refreshed LastUpdatedAt for 15 minutes. Defends against hangs in
            // pre-encode stages (hardware-accel detection, FFprobe, crop-detect) that
            // wouldn't be caught by FFmpeg's own no-output stall detection.
            using var watchdogCts = new CancellationTokenSource();
            var watchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (!watchdogCts.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), watchdogCts.Token);
                        if ((DateTime.UtcNow - workItem.LastUpdatedAt) > TimeSpan.FromMinutes(15))
                        {
                            await LogAsync(workItem.Id, "Watchdog: no progress for 15 min — aborting job");
                            active.Cts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                await ConvertVideoAsync(workItem, options, cancellationToken: active.Cts.Token);
            }
            finally
            {
                watchdogCts.Cancel();
                try { await watchdogTask; } catch { }
            }

            // ConvertVideoAsync's internal fail paths (e.g. truncation detection) call
            // MarkWorkItemFailed and return without throwing. Don't overwrite Failed status
            // with Completed in that case.
            if (workItem.Status != WorkItemStatus.Failed)
            {
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

                // Append to the analytics ledger. Failure is non-fatal — the
                // dashboard is best-effort. We classify by encoded vs original
                // size: if encoded >= original, ConvertVideoAsync discarded
                // the output and the row records "NoSavings" so the dashboard
                // still attributes the encode time/device-utilization but
                // doesn't credit savings.
                _ = RecordLocalEncodeHistoryAsync(workItem, options, active.DeviceId);
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
            _activeLocalJobs.TryRemove(workItem.Id, out _);
            try { active.Cts.Dispose(); } catch { }
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
    /// <param name="dropImageSubtitlesOnly">When <c>true</c>, suppresses MKV bitmap-subtitle pass-through for this attempt (OCR'd and text subs still pass through). Set by the retry chain when the previous attempt may have failed because of the image-based streams.</param>
    /// <param name="cancellationToken">Token to abort the encode mid-stream.</param>
    private async Task ConvertVideoAsync(
        WorkItem workItem,
        EncoderOptions options,
        bool stripSubtitles = false,
        bool forceSwDecode = false,
        bool useConservativeHwFlags = false,
        IReadOnlyList<SubtitleExtractionService.OcrMuxResult>? cachedOcrSrts = null,
        string? cachedOcrMuxTmpDir = null,
        bool dropImageSubtitlesOnly = false,
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
        // empty language list = keep all, preserve-only profile = no re-encode.
        string audioFlags = _ffprobeService.MapAudio(
            workItem.Probe!,
            doAudioWork ? options.AudioLanguagesToKeep : new List<string>(),
            doAudioWork ? options.PreserveOriginalAudio : true,
            doAudioWork ? options.AudioOutputs : null,
            options.Format == "mkv",
            out var audioWarnings) + " ";

        foreach (var w in audioWarnings)
            await LogAsync(workItem.Id, w);

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
            // (encoded to the container's native text codec). When MKV pass-through is on,
            // bitmap streams are also kept here so the output has both PGS + OCR'd SRT.
            string ocrCodec = options.Format == "mkv" ? "srt" : "mov_text";

            bool passBitmaps = options.Format == "mkv"
                            && options.PassThroughImageSubtitlesMkv
                            && !dropImageSubtitlesOnly;

            var sourceSubs = _ffprobeService
                .SelectSidecarStreams(workItem.Probe!, options.SubtitleLanguagesToKeep, includeBitmaps: passBitmaps)
                .ToList();

            string maps   = "";
            string codecs = "";
            string meta   = "";
            int    outSubIndex = 0;

            foreach (var s in sourceSubs)
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
            bool passBitmaps = options.Format == "mkv"
                            && options.PassThroughImageSubtitlesMkv
                            && !dropImageSubtitlesOnly;
            subtitleFlags = _ffprobeService.MapSub(workItem.Probe!, subLangs, options.Format == "mkv", passBitmaps) + " ";
        }

        // -analyzeduration and -probesize handle files with many streams (e.g. 30+ PGS subtitle tracks)
        string analyzeFlags = "-analyzeduration 10M -probesize 50M ";
        string command = BuildFfmpegCommand(
            options.Format, initFlags, analyzeFlags, inputPath, extraInputs,
            videoFlags, compressionFlags, audioFlags, subtitleFlags, outputPath);

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
            await HandleConversionFailure(workItem, options, outputPath, ex.Message, stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, dropImageSubtitlesOnly, cancellationToken: cancellationToken);
            return;
        }

        await Task.Delay(5000); // Wait for the filesystem to finish flushing the output before probing it.

        if (!File.Exists(outputPath))
        {
            await HandleConversionFailure(workItem, options, outputPath, "Output file not found", stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, dropImageSubtitlesOnly, cancellationToken: cancellationToken);
            return;
        }

        var outputProbe = await _ffprobeService.ProbeAsync(outputPath);

        // Container header metadata (Format.Duration / stream.Duration) can lie on broken
        // sources and on freshly-muxed outputs. Read the real end-of-content from the last
        // packets of each file so the comparison reflects what the streams actually contain.
        double srcDur = await _ffprobeService.GetAccurateVideoDurationAsync(workItem.Path, workItem.Probe, cancellationToken);
        double outDur = await _ffprobeService.GetAccurateVideoDurationAsync(outputPath,    outputProbe,     cancellationToken);

        if (!_ffprobeService.ConvertedSuccessfully(srcDur, outDur))
        {
            // Duration mismatch: if the source's tail past the encoded end is just blank/black
            // padding, the output captured all real content and we accept it. If the tail has
            // real content, the encoder genuinely truncated something — retries would produce
            // the same truncation, so fail permanently.
            if (await _ffprobeService.IsTailMostlyBlackAsync(workItem.Path, outDur, srcDur, cancellationToken))
            {
                await LogAsync(workItem.Id,
                    $"Duration mismatch detected (source={srcDur:0.##}s output={outDur:0.##}s), but the source tail is blank/black — accepting output.");
            }
            else
            {
                await LogAsync(workItem.Id,
                    $"Duration mismatch detected (source={srcDur:0.##}s output={outDur:0.##}s) and source tail contains real content — output is truncated. Failing without retries.");
                try { await _fileService.FileDeleteAsync(outputPath); }
                catch (Exception ex) { await LogAsync(workItem.Id, $"Warning: Could not clean up output file: {ex.Message}"); }
                TryCleanupOcrMuxDir(ocrMuxTmpDir);
                await MarkWorkItemFailed(workItem.Id, "Output truncated — source tail has real content past the encoded end");
                return;
            }
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
    ///     (language drop, commentary drop, dropped source on Preserve=off, or any output
    ///     profile that would emit a re-encode) versus a straight stream copy. Used to
    ///     decide mux-pass eligibility — must agree with <see cref="FfprobeService.MapAudio"/>
    ///     about which source tracks survive, otherwise files with droppable tracks slip
    ///     through the no-op skip gate without being processed.
    /// </summary>
    internal static bool HasAudioWork(EncoderOptions options, IReadOnlyList<AudioStreamSummary> audioStreams)
    {
        if (audioStreams.Count == 0) return false;

        // Language filter would drop at least one track?
        IReadOnlyList<AudioStreamSummary> kept = audioStreams;
        if (options.AudioLanguagesToKeep is { Count: > 0 } audLangs)
        {
            var filtered = audioStreams
                .Where(s => LanguageMatcher.Matches(s.Language, s.Title, audLangs))
                .ToList();
            if (filtered.Count < audioStreams.Count) return true;
            kept = filtered;
        }

        // Commentary tracks are unconditionally dropped by the planner (see
        // FfprobeService.IsCommentaryTitle). If any survived the language filter,
        // running the planner would change the output — that's audio work.
        foreach (var s in kept)
            if (FfprobeService.IsCommentaryTitle(s.Title)) return true;

        // Preserve=off means at least one source track will be dropped (the planner only
        // emits encoded outputs unless its safeguard kicks in) — that's audio work.
        if (!options.PreserveOriginalAudio) return true;

        // With Preserve=on, any output profile that wouldn't dedupe-to-copy against an
        // existing source is a re-encode and therefore audio work. Codec name + channel
        // count are enough since dedup uses exactly those two fields.
        if (options.AudioOutputs is { Count: > 0 } profiles)
        {
            foreach (var p in profiles)
            {
                int? targetCh = p.Layout?.Trim().ToLowerInvariant() switch
                {
                    "mono"   => 1,
                    "stereo" => 2,
                    "5.1"    => 6,
                    "7.1"    => 8,
                    _        => null,
                };

                bool deduped = kept.Any(s =>
                    string.Equals(s.CodecName, p.Codec, StringComparison.OrdinalIgnoreCase)
                    && (targetCh == null || s.Channels == targetCh.Value));

                if (!deduped) return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when subtitle settings would change the output
    ///     (language drop, sidecar extraction, or OCR of bitmap subs) versus a straight copy.
    /// </summary>
    internal static bool HasSubtitleWork(EncoderOptions options, IReadOnlyList<SubtitleStreamSummary> subStreams)
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
    ///     Returns <see langword="true"/> when an encode would do nothing meaningful: the video
    ///     stream would be copied (HEVC under <c>TargetBitrate + 700</c> with no filter-forced
    ///     re-encode) and neither the audio nor subtitle pipeline would change anything. Used as
    ///     a skip-ladder rung in <see cref="AddFileAsync"/> so we don't run ffmpeg just to produce
    ///     a near-identical output.
    /// </summary>
    internal static bool WouldEncodeBeNoOp(
        EncoderOptions options,
        long bitrate,
        bool isHevc,
        int sourceHeight,
        bool isHdr,
        IReadOnlyList<AudioStreamSummary> audioStreams,
        IReadOnlyList<SubtitleStreamSummary> subtitleStreams)
    {
        // Video would only stream-copy when CalculateBitrates' override fires AND no filter
        // forces a re-encode (matches the videoCopy=false flip-back in ConvertVideoAsync).
        bool videoCopyEligible = isHevc && bitrate > 0 && bitrate < options.TargetBitrate + 700;
        if (!videoCopyEligible)                              return false;
        if (HasActiveFilter(options, sourceHeight, isHdr))   return false;

        // Audio/sub work checks are intentionally EncodingMode-agnostic — the encoder pipeline
        // applies these in any mode, so they're the right test for "would the audio/sub mapping
        // actually change anything?"
        if (HasAudioWork(options, audioStreams))             return false;
        if (HasSubtitleWork(options, subtitleStreams))       return false;

        return true;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when any video-stream filter (crop, downscale, HDR
    ///     tonemap) would be active for this source. Used by both the no-op skip gate and the
    ///     analyze preview's Copy-vs-Queue prediction, since an active filter forces a re-encode
    ///     even when bitrate logic would otherwise have chosen <c>videoCopy</c>.
    /// </summary>
    internal static bool HasActiveFilter(EncoderOptions options, int sourceHeight, bool isHdr)
    {
        if (options.RemoveBlackBorders)                       return true;
        if (WouldDownscale(options, sourceHeight))            return true;
        if (options.TonemapHdrToSdr && isHdr)                 return true;
        return false;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when the active downscale policy would actually fire
    ///     for a source of the given height. Mirrors <see cref="ComputeScaleExpr"/>'s decision
    ///     without producing an FFmpeg expression.
    /// </summary>
    internal static bool WouldDownscale(EncoderOptions options, int sourceHeight)
    {
        if (!IsDownscalePolicyActive(options.DownscalePolicy)) return false;
        if (sourceHeight <= 0)                                 return false;

        int targetH = ResolveDownscaleHeight(options.DownscaleTarget);
        bool always = string.Equals(options.DownscalePolicy, "Always", StringComparison.OrdinalIgnoreCase);
        return always || sourceHeight > targetH;
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
        bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
        bool targetIsAv1  = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
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

        if (mf.Bitrate <= ceilingKbps * skipMultiplier) return true;

        // No-op gate — mirrors AddFileAsync's WouldEncodeBeNoOp rung. An HEVC file just above
        // the ceiling but under target+700 would get a videoCopy=true encode at run-time, and
        // if there's no audio/sub work or filters either, the encode is a near-no-op. MediaFile
        // doesn't carry HDR metadata so we treat tonemap as inactive here — worst case the file
        // gets re-evaluated by AddFileAsync, which has the probe and will catch the HDR case.
        if (mf.IsHevc && mf.Bitrate < options.TargetBitrate + 700)
        {
            var audioStreams = MuxStreamSummary.DeserializeAudio(mf.AudioStreams);
            var subStreams   = MuxStreamSummary.DeserializeSubtitle(mf.SubtitleStreams);
            if (!HasActiveFilter(options, mf.Height, isHdr: false)
                && !HasAudioWork(options, audioStreams)
                && !HasSubtitleWork(options, subStreams))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Mirrors the scan-phase skip-gate check: <see langword="true"/> when the source is
    ///     already at the target codec and below the configured bitrate tolerance (with the
    ///     4K multiplier when applicable). Used at encode time to decide whether a configured
    ///     <see cref="EncodingMode.Hybrid"/> should trigger a video-copy mux pass.
    /// </summary>
    internal static bool MeetsBitrateTarget(WorkItem workItem, EncoderOptions options)
    {
        if (workItem.Bitrate <= 0 || workItem.Probe == null) return false;

        var videoStream = workItem.Probe.Streams.FirstOrDefault(s => s.CodecType == "video");
        bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
        bool targetIsAv1  = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
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
    /// <summary>
    ///     Assembles the final ffmpeg command string from the per-section flag fragments
    ///     produced upstream. The format toggles two things: the container muxer
    ///     (<c>matroska</c> vs <c>mp4</c>) and the variable flags (MP4 needs
    ///     <c>-movflags +faststart</c>; MKV doesn't). Extracted from
    ///     <c>ConvertVideoAsync</c> so the wire format can be unit-tested without
    ///     spinning up the encode pipeline.
    /// </summary>
    /// <param name="format">"mkv" or "mp4". Anything else is treated as MP4.</param>
    /// <param name="initFlags">Hardware init flags (from <see cref="GetInitFlags"/>).</param>
    /// <param name="analyzeFlags">Probe-size / analyze-duration flags.</param>
    /// <param name="inputPath">Source file path. Quoted into the command.</param>
    /// <param name="extraInputs">Additional <c>-i</c> arguments for OCR-mux SRT inputs, or <c>""</c>.</param>
    /// <param name="videoFlags">Video map + codec args.</param>
    /// <param name="compressionFlags">Rate-control / preset / quality flags.</param>
    /// <param name="audioFlags">Audio map + per-output-stream codec args.</param>
    /// <param name="subtitleFlags">Subtitle map + codec args (or <c>-sn</c>).</param>
    /// <param name="outputPath">Destination file path. Quoted into the command.</param>
    /// <returns>The full ffmpeg argument string (without the leading <c>ffmpeg</c> binary name).</returns>
    internal static string BuildFfmpegCommand(
        string format,
        string initFlags,
        string analyzeFlags,
        string inputPath,
        string extraInputs,
        string videoFlags,
        string compressionFlags,
        string audioFlags,
        string subtitleFlags,
        string outputPath)
    {
        bool isMkv      = string.Equals(format, "mkv", StringComparison.OrdinalIgnoreCase);
        string varFlags = isMkv
            ? "-max_muxing_queue_size 9999 "
            : "-movflags +faststart -max_muxing_queue_size 9999 ";
        string muxer = isMkv ? "matroska" : "mp4";

        return $"{initFlags} {analyzeFlags}-i \"{inputPath}\" {extraInputs}{videoFlags}{compressionFlags}{audioFlags}{subtitleFlags}{varFlags}-f {muxer} \"{outputPath}\"";
    }

    internal static (string target, string min, string max, bool copy) CalculateBitrates(WorkItem workItem, EncoderOptions options)
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

            // Bitrate>0 gates the compression branch: a probe that returned no source
            // bitrate would otherwise produce 0k targets, which ffmpeg refuses.
            if (workItem.Bitrate > 0 && workItem.Bitrate < hdBitrate + 700 && !workItem.IsHevc)
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
        else if (workItem.Bitrate > 0 && workItem.Bitrate < options.TargetBitrate + 700 && !workItem.IsHevc)
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
    internal static bool WillDownscaleBelow4K(EncoderOptions options)
    {
        if (!IsDownscalePolicyActive(options.DownscalePolicy)) return false;
        return ResolveDownscaleHeight(options.DownscaleTarget) <= 1440;
    }

    internal static bool IsDownscalePolicyActive(string policy) =>
        string.Equals(policy, "Always",      StringComparison.OrdinalIgnoreCase)
        || string.Equals(policy, "CapAtTarget", StringComparison.OrdinalIgnoreCase)
        || string.Equals(policy, "IfLarger",    StringComparison.OrdinalIgnoreCase);

    internal static int ResolveDownscaleHeight(string target) => target switch
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
    internal static string? ComputeScaleExpr(WorkItem workItem, EncoderOptions options)
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
    internal static string GetForcedReencodeCompressionFlags(string encoder, bool useVaapi, bool isSvtAv1,
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
    private List<HardwareDevice>? _detectedDevices = null;

    /// <summary>
    ///     All devices default to a single concurrent encode. Users opt in to
    ///     parallelism per-device via the dashboard's hardware-concurrency
    ///     editor — the safe default avoids surprising a fresh install with a
    ///     burst of simultaneous encodes that thrashes the GPU.
    /// </summary>
    private static int DefaultConcurrencyFor(string deviceId) => 1;

    /// <summary>
    ///     Builds the CPU encode device entry. CPU is exposed alongside the
    ///     hardware encoders as a peer option (not labelled "software") — it
    ///     has its own concurrency knob, its own slot pool, and its own row
    ///     on the dashboard. Auto-fallback when an HW encoder fails is a
    ///     separate path inside ffmpeg invocation; no UI surface needed.
    /// </summary>
    private static HardwareDevice MakeCpuDevice() => new()
    {
        DeviceId           = "cpu",
        DisplayName        = "CPU",
        SupportedCodecs    = new() { "h264", "h265", "av1" },
        Encoders           = new() { "libx264", "libx265", "libsvtav1" },
        DefaultConcurrency = DefaultConcurrencyFor("cpu"),
        IsHardware         = true,
    };

    /// <summary>
    ///     Detects available hardware acceleration by testing encoders.
    ///     Populates both the legacy primary <see cref="_detectedHardware"/> string
    ///     and the full <see cref="_detectedDevices"/> list. Result is cached after
    ///     first detection — subsequent calls short-circuit.
    /// </summary>
    private async Task<string> DetectHardwareAccelerationAsync()
    {
        if (_detectedHardware != null)
            return _detectedHardware;

        var devices = new List<HardwareDevice>();

        // Windows GPU detection — probe all vendors so a laptop with an Intel iGPU
        // *and* an NVIDIA dGPU reports both, instead of stopping at whichever
        // matches first. The cluster scheduler needs the full picture to dispatch
        // simultaneous jobs across families.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.WriteLine("Auto-detect: Running on Windows, testing GPU encoders...");

            if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
            {
                Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                bool h264 = await TestEncoderAsync("-hwaccel cuda", "h264_nvenc");
                bool av1  = await TestEncoderAsync("-hwaccel cuda", "av1_nvenc");
                devices.Add(new HardwareDevice
                {
                    DeviceId           = "nvidia",
                    DisplayName        = "NVIDIA NVENC",
                    SupportedCodecs    = BuildSupportedCodecs(true, h264, av1),
                    Encoders           = BuildNvidiaEncoders(true, h264, av1),
                    DefaultConcurrency = DefaultConcurrencyFor("nvidia"),
                    IsHardware         = true,
                });
            }

            if (await TestEncoderAsync("-hwaccel qsv -qsv_device auto", "hevc_qsv"))
            {
                Console.WriteLine("Auto-detect: Intel QSV available");
                bool h264 = await TestEncoderAsync("-hwaccel qsv -qsv_device auto", "h264_qsv");
                bool av1  = await TestEncoderAsync("-hwaccel qsv -qsv_device auto", "av1_qsv");
                devices.Add(new HardwareDevice
                {
                    DeviceId           = "intel",
                    DisplayName        = "Intel QSV",
                    SupportedCodecs    = BuildSupportedCodecs(true, h264, av1),
                    Encoders           = BuildIntelEncoders(true, h264, av1, qsv: true),
                    DefaultConcurrency = DefaultConcurrencyFor("intel"),
                    IsHardware         = true,
                });
            }

            if (await TestEncoderAsync("-hwaccel auto", "hevc_amf"))
            {
                Console.WriteLine("Auto-detect: AMD AMF available");
                bool h264 = await TestEncoderAsync("-hwaccel auto", "h264_amf");
                bool av1  = await TestEncoderAsync("-hwaccel auto", "av1_amf");
                devices.Add(new HardwareDevice
                {
                    DeviceId           = "amd",
                    DisplayName        = "AMD AMF",
                    SupportedCodecs    = BuildSupportedCodecs(true, h264, av1),
                    Encoders           = BuildAmdEncoders(true, h264, av1, amf: true),
                    DefaultConcurrency = DefaultConcurrencyFor("amd"),
                    IsHardware         = true,
                });
            }
        }
        // macOS GPU detection (VideoToolbox — works on both Apple Silicon and Intel Macs).
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Console.WriteLine("Auto-detect: Running on macOS, testing VideoToolbox...");
            if (await TestEncoderAsync("-hwaccel videotoolbox", "hevc_videotoolbox"))
            {
                Console.WriteLine("Auto-detect: VideoToolbox available");
                bool h264 = await TestEncoderAsync("-hwaccel videotoolbox", "h264_videotoolbox");
                devices.Add(new HardwareDevice
                {
                    DeviceId           = "apple",
                    DisplayName        = "Apple VideoToolbox",
                    SupportedCodecs    = BuildSupportedCodecs(true, h264, av1: false),
                    Encoders           = BuildAppleEncoders(true, h264),
                    DefaultConcurrency = DefaultConcurrencyFor("apple"),
                    IsHardware         = true,
                });
            }
        }
        else
        {
            // Linux
            await LogVaapiInfoAsync();

            // VAAPI (Intel iGPU and AMD GPUs on Linux) — probe both drivers.
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
                    bool av1Ok  = await TestEncoderAsync(hwInit, "av1_vaapi");

                    if (hevcOk || h264Ok || av1Ok)
                    {
                        Console.WriteLine($"Auto-detect: VAAPI available with {driver} driver (hevc={hevcOk}, h264={h264Ok}, av1={av1Ok})");
                        devices.Add(new HardwareDevice
                        {
                            DeviceId           = "intel",
                            DisplayName        = "Intel VAAPI",
                            SupportedCodecs    = BuildSupportedCodecs(hevcOk, h264Ok, av1Ok),
                            Encoders           = BuildIntelEncoders(hevcOk, h264Ok, av1Ok, qsv: false),
                            DefaultConcurrency = DefaultConcurrencyFor("intel"),
                            IsHardware         = true,
                        });
                        break;
                    }
                }
            }

            if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
            {
                Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                bool h264 = await TestEncoderAsync("-hwaccel cuda", "h264_nvenc");
                bool av1  = await TestEncoderAsync("-hwaccel cuda", "av1_nvenc");
                devices.Add(new HardwareDevice
                {
                    DeviceId           = "nvidia",
                    DisplayName        = "NVIDIA NVENC",
                    SupportedCodecs    = BuildSupportedCodecs(true, h264, av1),
                    Encoders           = BuildNvidiaEncoders(true, h264, av1),
                    DefaultConcurrency = DefaultConcurrencyFor("nvidia"),
                    IsHardware         = true,
                });
            }
        }

        // Always add a CPU device so the master has a fallback slot pool — every
        // worker can software-encode regardless of GPU presence.
        devices.Add(MakeCpuDevice());

        _detectedDevices = devices;

        // Legacy primary: first hardware device, else "none". Preserves the
        // original auto-resolution semantics for callers that haven't yet
        // moved to the per-device API.
        var primary = devices.FirstOrDefault(d => d.IsHardware);
        _detectedHardware = primary?.DeviceId ?? "none";

        if (primary == null)
            Console.WriteLine("Auto-detect: No hardware acceleration available, using software");

        return _detectedHardware;
    }

    private static List<string> BuildSupportedCodecs(bool h265, bool h264, bool av1)
    {
        var list = new List<string>();
        if (h264) list.Add("h264");
        if (h265) list.Add("h265");
        if (av1)  list.Add("av1");
        return list;
    }

    private static List<string> BuildNvidiaEncoders(bool h265, bool h264, bool av1)
    {
        var list = new List<string>();
        if (h264) list.Add("h264_nvenc");
        if (h265) list.Add("hevc_nvenc");
        if (av1)  list.Add("av1_nvenc");
        return list;
    }

    private static List<string> BuildIntelEncoders(bool h265, bool h264, bool av1, bool qsv)
    {
        var suffix = qsv ? "_qsv" : "_vaapi";
        var list = new List<string>();
        if (h264) list.Add("h264" + suffix);
        if (h265) list.Add("hevc" + suffix);
        if (av1)  list.Add("av1"  + suffix);
        return list;
    }

    private static List<string> BuildAmdEncoders(bool h265, bool h264, bool av1, bool amf)
    {
        var suffix = amf ? "_amf" : "_vaapi";
        var list = new List<string>();
        if (h264) list.Add("h264" + suffix);
        if (h265) list.Add("hevc" + suffix);
        if (av1)  list.Add("av1"  + suffix);
        return list;
    }

    private static List<string> BuildAppleEncoders(bool h265, bool h264)
    {
        var list = new List<string>();
        if (h264) list.Add("h264_videotoolbox");
        if (h265) list.Add("hevc_videotoolbox");
        return list;
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
    internal static bool CanVaapiDecode(ProbeResult? probe)
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
    internal static string GetInitFlags(string hardwareAcceleration, bool hwDecode = true)
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
    internal static string GetEncoder(EncoderOptions options)
    {
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // Case-insensitive: Encoder can come from settings.json or per-folder overrides
        // where casing isn't enforced; the UI's ENCODER_MAP is lowercase but external
        // entry points aren't, and a non-matching codec string silently falls through
        // to passing the raw value to ffmpeg.
        var encoder = options.Encoder ?? "";
        bool isAv1  = encoder.Contains("av1", StringComparison.OrdinalIgnoreCase)
                   || encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
        bool isH265 = !isAv1 && encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
        bool isH264 = !isAv1 && encoder.Contains("264", StringComparison.OrdinalIgnoreCase);

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
            _ => encoder
        };
    }

    /// <summary>
    ///     Returns the software fallback encoder for the user's codec preference.
    ///     Used when the requested hardware encoder isn't available on the system.
    /// </summary>
    internal static string GetSoftwareFallbackEncoder(EncoderOptions options)
    {
        // Case-insensitive — same reason as GetEncoder above.
        var encoder = options.Encoder ?? "";
        bool isAv1  = encoder.Contains("av1", StringComparison.OrdinalIgnoreCase)
                   || encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
        bool isH264 = !isAv1 && encoder.Contains("264", StringComparison.OrdinalIgnoreCase);
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
    internal static int MapSvtAv1Preset(string preset) => (preset ?? "").ToLowerInvariant() switch
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
        // Publish the process under the work item's ID so cancel/stop can
        // find this specific encode without affecting peers running on other
        // device slots. The earlier _activeProcess singleton is gone.
        lock (_activeLock)
        {
            if (_activeLocalJobs.TryGetValue(workItem.Id, out var slot))
                slot.Process = process;
        }
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

                                                if (_progressCallbacks.TryGetValue(workItem.Id, out var progressCb))
                                                    _ = progressCb(workItem.Id, progress);
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

        lock (_activeLock)
        {
            if (_activeLocalJobs.TryGetValue(workItem.Id, out var slot)) slot.Process = null;
        }

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
        bool imageSubtitlesAlreadyDropped = false,
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
                dropImageSubtitlesOnly: imageSubtitlesAlreadyDropped,
                cancellationToken: cancellationToken);
            return;
        }

        // Retry 2a: Drop image-based subs only — keeps OCR'd SRTs and text-based (srt/ass)
        // streams. Bitmap streams (PGS/VOBSUB/DVB) are the most common cause of subtitle
        // failures; many failures clear once they're removed without sacrificing the rest
        // of the user's subtitles. Only relevant when the user opted into MKV pass-through.
        bool wasPassingBitmaps = options.Format == "mkv" && options.PassThroughImageSubtitlesMkv;
        if (!subtitlesWereStripped && !imageSubtitlesAlreadyDropped && wasPassingBitmaps)
        {
            await LogAsync(workItem.Id, "Retrying without image-based subtitles...");
            workItem.Progress = 0;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            await ConvertVideoAsync(workItem, options,
                stripSubtitles: false,
                useConservativeHwFlags: conservativeHwFlagsTried,
                cachedOcrSrts: cachedOcrSrts,
                cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                dropImageSubtitlesOnly: true,
                cancellationToken: cancellationToken);
            return;
        }

        // Retry 2b: Strip all subtitles (covers broken streams that survived 2a).
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
                dropImageSubtitlesOnly: imageSubtitlesAlreadyDropped,
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
            bool isAv1Target = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase)
                            || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(options.Codec, "av1", StringComparison.OrdinalIgnoreCase);
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
    /// <summary>
    ///     Legacy first-active accessor. Returns any one in-flight local
    ///     encode for callers that pre-date multi-slot scheduling. Multi-slot
    ///     callers should use <see cref="GetActiveLocalJobs"/>.
    /// </summary>
    public WorkItem? GetActiveWorkItem()
    {
        foreach (var kv in _activeLocalJobs) return kv.Value.Item;
        return null;
    }

    /// <summary>
    ///     Returns one <see cref="ActiveJobInfo"/> per in-flight local encode,
    ///     mirroring the shape of <see cref="ClusterNodeJobService.GetActiveJobs"/>
    ///     so the dashboard can render the master's self-card with the same
    ///     per-device chips and per-job progress bars used for remote nodes.
    /// </summary>
    public List<ActiveJobInfo> GetActiveLocalJobs()
    {
        var list = new List<ActiveJobInfo>();
        foreach (var kv in _activeLocalJobs)
        {
            list.Add(new ActiveJobInfo
            {
                JobId    = kv.Key,
                DeviceId = kv.Value.DeviceId,
                FileName = kv.Value.Item.FileName,
                Progress = kv.Value.Item.Progress,
                Phase    = "Encoding",
            });
        }
        return list;
    }

    /// <summary>Returns the encoder options from the most recently started queue run.</summary>
    public EncoderOptions? GetLastOptions() => _lastOptions;

    /// <summary>
    ///     Updates the cached encoder options so that queued items pick up
    ///     settings changes without needing a new scan.
    /// </summary>
    public void UpdateOptions(EncoderOptions options) => _lastOptions = options;

    /// <summary>
    ///     Removes <see cref="WorkItemStatus.Pending"/> items from the local queue that no
    ///     longer need encoding under <paramref name="newOptions"/> — the inverse of the
    ///     scan-time skip ladder. Used by the settings-save flow when the user changes a
    ///     setting in the "no longer needs encoding" direction (e.g., adds an audio output
    ///     that re-queues a batch, then removes it). Without this, those items keep running
    ///     even though the setting that made them eligible has been reverted.
    ///
    ///     Active jobs (<see cref="WorkItemStatus.Processing"/>, etc.) are left alone — they
    ///     either finish or get cancelled explicitly. Remote-assigned items are also left
    ///     alone since they're not in <c>_workQueue</c> anymore (they're in worker hands).
    /// </summary>
    /// <param name="newOptions">The just-saved encoder options to evaluate against.</param>
    /// <returns>The number of pending items removed from the queue.</returns>
    public async Task<int> RemoveSettingsObsoletedQueueItemsAsync(EncoderOptions newOptions)
    {
        ArgumentNullException.ThrowIfNull(newOptions);

        // Step 1: snapshot the candidate (id, path) pairs under the queue lock. We don't
        // remove yet — the DB lookup that decides "is this still needed?" is async and
        // can't run under a lock. Holding the snapshot's ids lets us re-find the items
        // in step 3 and verify they're still Pending before removal.
        List<(string Id, string Path)> candidates;
        lock (_queueLock)
        {
            candidates = _workQueue
                .Where(w => w.Status == WorkItemStatus.Pending)
                .Select(w => (w.Id, w.Path))
                .ToList();
        }
        if (candidates.Count == 0) return 0;

        // Step 2: re-evaluate each candidate against the new options using its cached
        // MediaFile row. Items missing the row or the per-track summaries are left alone
        // (conservative: the next scan re-probes them).
        var idsToRemove = new HashSet<string>();
        foreach (var (id, path) in candidates)
        {
            var normalizedPath = Path.GetFullPath(path);
            var mf = await _mediaFileRepo.GetByPathAsync(normalizedPath);
            if (mf == null) continue;
            if (mf.AudioStreams == null && mf.SubtitleStreams == null) continue;

            if (WouldSkipUnderOptions(mf, newOptions)) idsToRemove.Add(id);
        }
        if (idsToRemove.Count == 0) return 0;

        // Step 3: remove the matching items from the queue under the lock. Re-check status
        // because a concurrent picker may have dequeued an item between steps 1 and 3.
        var removed = new List<WorkItem>();
        lock (_queueLock)
        {
            for (int i = _workQueue.Count - 1; i >= 0; i--)
            {
                var item = _workQueue[i];
                if (!idsToRemove.Contains(item.Id))           continue;
                if (item.Status != WorkItemStatus.Pending)    continue;

                _workQueue.RemoveAt(i);
                removed.Add(item);
            }
        }
        if (removed.Count == 0) return 0;

        // Step 4: drop from the work-item registry, notify the UI, and persist the
        // MediaFile transition to Skipped so the next scan respects the reverted setting.
        foreach (var item in removed)
        {
            _workItems.TryRemove(item.Id, out _);
            item.Status = WorkItemStatus.Cancelled;
            try
            {
                await _hubContext.Clients.All.SendAsync("WorkItemRemoved", item.Id);
            }
            catch { /* SignalR failures are non-fatal — the next page refresh corrects state */ }
            try
            {
                await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(item.Path), MediaFileStatus.Skipped);
            }
            catch { /* DB failures don't block queue cleanup; next scan corrects via WouldSkipUnderOptions */ }
        }

        Console.WriteLine($"Settings change: dropped {removed.Count} now-obsolete pending queue item(s).");
        return removed.Count;
    }

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

    /// <summary>
    ///     Returns every hardware encode device this worker can drive,
    ///     including a CPU fallback. Empty until <see cref="DetectHardwareAccelerationAsync"/>
    ///     has finished its initial probe.
    /// </summary>
    public List<HardwareDevice> GetDetectedDevices() =>
        _detectedDevices?.Select(d => CloneHardwareDevice(d)).ToList() ?? new();

    private static HardwareDevice CloneHardwareDevice(HardwareDevice src) => new()
    {
        DeviceId           = src.DeviceId,
        DisplayName        = src.DisplayName,
        SupportedCodecs    = new List<string>(src.SupportedCodecs),
        Encoders           = new List<string>(src.Encoders),
        DefaultConcurrency = src.DefaultConcurrency,
        IsHardware         = src.IsHardware,
    };

    /// <summary>
    ///     Registers a per-job progress callback for forwarding progress to the master.
    ///     Pass <see langword="null"/> to clear the callback for that specific job.
    ///     Per-jobId so concurrent encodes don't clobber each other's reporters.
    /// </summary>
    /// <param name="jobId">The work item ID this callback applies to.</param>
    /// <param name="callback">Delegate receiving (workItemId, progressPercent), or null to remove.</param>
    public void SetProgressCallback(string jobId, Func<string, int, Task>? callback)
    {
        if (callback == null) _progressCallbacks.TryRemove(jobId, out _);
        else                  _progressCallbacks[jobId] = callback;
    }

    /// <summary>
    ///     Registers a per-job log callback for forwarding FFmpeg lines to the master.
    ///     Pass <see langword="null"/> to clear the callback for that specific job.
    ///     Per-jobId so concurrent encodes don't clobber each other's log streams.
    /// </summary>
    /// <param name="jobId">The work item ID this callback applies to.</param>
    /// <param name="callback">Delegate receiving (workItemId, logLine), or null to remove.</param>
    public void SetLogCallback(string jobId, Func<string, string, Task>? callback)
    {
        if (callback == null) _logCallbacks.TryRemove(jobId, out _);
        else                  _logCallbacks[jobId] = callback;
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

        // Snapshot every active local job, kill its ffmpeg, mark Stopped,
        // and unregister so a follow-up ProcessQueueAsync starts clean.
        var snapshot = _activeLocalJobs.Values.ToList();
        foreach (var active in snapshot)
        {
            try { active.Cts.Cancel(); } catch { }

            Process? proc;
            lock (_activeLock) { proc = active.Process; }
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }

            if (_lastOptions != null)
            {
                var outputPath = GetOutputPath(active.Item, _lastOptions);
                try { if (File.Exists(outputPath)) await _fileService.FileDeleteAsync(outputPath); } catch { }
            }

            active.Item.Status      = WorkItemStatus.Stopped;
            active.Item.CompletedAt = DateTime.UtcNow;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", active.Item);
            try { await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(active.Item.Path), MediaFileStatus.Unseen); } catch { }

            _activeLocalJobs.TryRemove(active.Item.Id, out _);
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

        // Snapshot then clear so reentrancy from ProcessWorkItemAsync's
        // finally block is harmless (TryRemove on an empty dict is a no-op).
        var snapshot = _activeLocalJobs.Values.ToList();
        _activeLocalJobs.Clear();

        foreach (var active in snapshot)
        {
            try { active.Cts.Cancel(); } catch { }
            Process? proc;
            lock (_activeLock) { proc = active.Process; }
            if (proc != null)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }

            active.Item.Status      = WorkItemStatus.Stopped;
            active.Item.CompletedAt = DateTime.UtcNow;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", active.Item);
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