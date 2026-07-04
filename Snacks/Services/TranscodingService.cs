using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using Snacks.Services.Slots;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
    /// <summary>
    ///     In-memory work items keyed by ID: active jobs, the hydrated pending
    ///     window, and recently finished items. NOT the full queue — pending work
    ///     beyond the window lives only as <see cref="MediaFileStatus.Queued"/> rows
    ///     in the database (see <see cref="SyncQueueWindowAsync"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, WorkItem> _workItems = new();

    /// <summary>
    ///     Normalized-path → work-item-id index over <see cref="_workItems"/>.
    ///     Duplicate detection used to scan every in-memory item per add — O(n²)
    ///     across a sweep. Maintained exclusively by <see cref="RegisterWorkItem"/> /
    ///     <see cref="UnregisterWorkItem"/>; never mutate <see cref="_workItems"/> directly.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _pathIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     The scheduler's working window: the hydrated top of the DB pending queue
    ///     (at most <see cref="QueueWindowSize"/> items), sorted by <see cref="CompareQueueOrder"/>.
    /// </summary>
    private readonly List<WorkItem> _workQueue = new();

    /// <summary>
    ///     How many pending rows the scheduler keeps hydrated. Large enough that the
    ///     local scheduler and the cluster dispatcher never starve between syncs,
    ///     small enough that a 500k-row backlog costs no meaningful memory.
    /// </summary>
    internal const int QueueWindowSize = 50;

    /// <summary>1 when the window may be stale relative to the DB (item added/cancelled/prioritized/dispatched).</summary>
    private int _queueWindowDirty = 1;

    /// <summary>Serializes window syncs; concurrent callers coalesce on the dirty flag.</summary>
    private readonly SemaphoreSlim _windowSyncLock = new(1, 1);

    /// <summary>
    ///     Row offset for window rotation. Normally 0 (window = top of the queue).
    ///     When every hydrated item is locally unservable (e.g. 50 consecutive 4K
    ///     items with master-excludes-4K) but the DB holds more pending rows, the
    ///     scheduler advances this by <see cref="QueueWindowSize"/> per pass so deeper
    ///     servable items rotate in instead of starving behind the unservable head.
    /// </summary>
    private int _windowRotationOffset;

    /// <summary>
    ///     When true, the pending order tiebreaker is recency (newest first) instead
    ///     of bitrate. Mirrors <c>EncoderOptions.QueueOrderNewestFirst</c>.
    /// </summary>
    private volatile bool _queueNewestFirst;

    /// <summary>Lock protecting access to <see cref="_workQueue"/>.</summary>
    private readonly object _queueLock = new();

    /// <summary>Fires <see cref="SweepTerminalWorkItems"/> every 5 minutes.</summary>
    private readonly Timer _workItemSweepTimer;

    /// <summary>
    ///     Maximum number of terminal (Completed/Failed/Cancelled/Stopped/NoSavings)
    ///     work items kept in <see cref="_workItems"/>. The queue UI only pages
    ///     through recent history — the permanent record lives in the database
    ///     (<see cref="MediaFile.Status"/> and the encode-history ledger) — so
    ///     anything beyond this is pure memory cost. A 30k-file sweep used to pin
    ///     every finished item (plus its probe) forever, which is how the app
    ///     reached 6+ GB and got OOM-killed on NAS hardware.
    /// </summary>
    private const int TerminalWorkItemCap = 1000;

    private readonly FileService _fileService;
    private readonly FfprobeService _ffprobeService;
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly MediaFileRepository _mediaFileRepo;
    private readonly ILogger<TranscodingService>? _log;

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

    /// <summary>
    ///     Sentinel <see cref="ActiveLocalJob.DeviceId"/> for music encodes. They
    ///     occupy <see cref="_activeLocalJobs"/> so cancel/kill plumbing works
    ///     uniformly, but slot accounting goes through the shared
    ///     <see cref="_slotLedger"/> on the synthetic <c>"music"</c> device id —
    ///     identical to the path workers use, so a queue full of music can't
    ///     starve GPU video and vice versa. Capacity for the master's music
    ///     slot comes from <c>EncoderOptions.Music.MasterMusicConcurrency</c>
    ///     via <c>ClusterService.EffectiveDeviceCapacity</c> (where 0 means
    ///     "never encode music on master"; the ledger refuses every reserve).
    /// </summary>
    private const string MusicDeviceId = "music";

    /// <summary>
    ///     Authoritative slot ledger, shared with <see cref="ClusterService"/>.
    ///     Master-local encodes reserve <c>(_localNodeId, deviceId)</c> entries
    ///     here exactly the same way worker dispatches do — one ledger, one
    ///     source of truth, no parallel semaphore that can drift out of sync.
    ///     Set by <see cref="SetSlotLedger"/> from cluster wiring; null only
    ///     in unit tests that don't exercise scheduling.
    /// </summary>
    private SlotLedger? _slotLedger;

    /// <summary>
    ///     The master's NodeId, stamped onto every local ledger reservation
    ///     so heartbeat reconcile and the dispatch loop see master-local jobs
    ///     under the master's row in <c>_nodes</c>. Defaults to a stable
    ///     fallback so non-cluster runs (tests, standalone with cluster off)
    ///     still produce identifiable reservations.
    /// </summary>
    private string _localNodeId = "master-local";

    /// <summary>
    ///     Codecs we've already logged an "auto → CPU software fallback" message
    ///     for in this process. Deduped per codec so a queue full of AV1 files on
    ///     a machine without AV1 hardware doesn't spam one line per job — the user
    ///     gets a single explanation when the first job dispatches to CPU. Resets
    ///     on process restart. Accessed under the scheduler semaphore, so no lock.
    /// </summary>
    private readonly HashSet<string> _loggedAutoCpuFallback = new();

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
    ///     Optional gate that returns <see langword="true"/> when local encoding is
    ///     allowed by the master's configured schedule window. Wired by
    ///     <see cref="ClusterService"/>; null in standalone/tests means "always allowed".
    /// </summary>
    private Func<bool>? _localScheduleGate;

    /// <summary>
    ///     Optional callback fired immediately after a work item is added to
    ///     the queue. <see cref="ClusterService"/> wires this to its dispatch
    ///     entry point so the cluster dispatcher races the master-local
    ///     scheduler per-item via the queue lock — without it the cluster
    ///     only fires every 2 seconds via its timer, and master-local (which
    ///     wakes synchronously on enqueue) consumes fast jobs (music) before
    ///     the cluster ever sees them.
    /// </summary>
    private Action? _onWorkItemQueued;

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
    ///     Resolves the per-folder <see cref="EncoderOptionsOverride"/> for a given file
    ///     path so the local dispatcher applies the same overrides the auto-scanner used
    ///     when queueing. Wired by <see cref="AutoScanService"/> (avoids the
    ///     TranscodingService ↔ AutoScanService DI cycle). When unset (tests / node mode)
    ///     the global options are used as-is.
    /// </summary>
    private Func<string, EncoderOptionsOverride?>? _folderOverrideResolver;

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
    ///     Installs the folder-override resolver. The local dispatcher uses this in
    ///     <see cref="ProcessQueueAsync"/> to apply per-folder overrides to the per-job
    ///     options clone — without it, a folder configured to encode at a different
    ///     codec / target / language would be queued correctly but encoded under the
    ///     global settings.
    /// </summary>
    public void SetFolderOverrideResolver(Func<string, EncoderOptionsOverride?> resolver) =>
        _folderOverrideResolver = resolver;

    private EncoderOptionsOverride? ResolveFolderOverride(string filePath) =>
        _folderOverrideResolver?.Invoke(filePath);

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
        _localNodeId = _localNodeIdentity.NodeId;
    }

    /// <summary>
    ///     Wires the shared <see cref="SlotLedger"/> so master-local encodes
    ///     reserve through the same store the cluster dispatcher uses. Without
    ///     a ledger (unit tests, early startup), local scheduling falls back
    ///     to the legacy in-process counting via <see cref="_activeLocalJobs"/>.
    /// </summary>
    public void SetSlotLedger(SlotLedger ledger) => _slotLedger = ledger;

    /// <summary> Live-read accessor for callers that need to reach the ledger via TranscodingService. </summary>
    public SlotLedger? GetSlotLedger() => _slotLedger;

    /// <summary> True if local encoding is suspended (master delegating to workers). </summary>
    public bool IsLocalEncodingPaused => _localEncodingPaused;

    public TranscodingService(
        FileService fileService,
        FfprobeService ffprobeService,
        IHubContext<TranscodingHub> hubContext,
        MediaFileRepository mediaFileRepo,
        NotificationService? notificationService = null,
        IntegrationService? integrationService = null,
        SubtitleExtractionService? subtitleExtractionService = null,
        EncodeHistoryRepository? encodeHistoryRepo = null,
        ILogger<TranscodingService>? logger = null)
    {
        _fileService               = fileService;
        _ffprobeService            = ffprobeService;
        _hubContext                = hubContext;
        _mediaFileRepo             = mediaFileRepo;
        _notificationService       = notificationService;
        _integrationService        = integrationService;
        _subtitleExtractionService = subtitleExtractionService;
        _encodeHistoryRepo         = encodeHistoryRepo;
        _log                       = logger;
        _ffmpegPath          = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";

        // Periodic memory sweep — releases probe data on finished items and caps
        // how many terminal items stay resident. The service is an app-lifetime
        // singleton, so the timer is never disposed.
        _workItemSweepTimer = new Timer(_ => SweepTerminalWorkItems(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _ = Task.Run(async () =>
        {
            try
            {
                await DetectHardwareAccelerationAsync();
                // Notify the cluster service so the local node's cached
                // Capabilities.Devices in _nodes catches up with the just-
                // populated _detectedDevices. The capacity resolver reads
                // from _nodes, and without this refresh standalone mode's
                // first encode permanently fails to reserve a slot.
                try { _hardwareDetectedCallback?.Invoke(); }
                catch (Exception ex) { Console.WriteLine($"HardwareDetected callback error: {ex.Message}"); }
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
    /// <param name="forceMux">
    ///     When <c>true</c>, the file was queued by an explicit user action and is treated as
    ///     <see cref="EncodingMode.Hybrid"/> regardless of the global mode: an at-target file
    ///     gets a video-copy mux pass (audio/subs re-applied, container normalized to
    ///     <see cref="EncoderOptions.Format"/>) instead of being skipped; above-target/wrong-codec
    ///     files still re-encode. Only a file with genuinely nothing to do (at target, target codec,
    ///     matching streams, matching container, no filters) is still skipped.
    /// </param>
    /// <returns>The work item ID (may be a previously existing ID if the file was already tracked).</returns>
    public async Task<string> AddFileAsync(string filePath, EncoderOptions options, bool force = false, bool forceMux = false, CancellationToken cancellationToken = default)
    {
        // Don't queue something the encoder will only fail on. Scan-time callers
        // pass files they just enumerated so they exist by definition, but the
        // retry button and other ad-hoc entry points can be called minutes or
        // hours after a file was last seen on disk.
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"AddFile: Skipping {Path.GetFileName(filePath)} — source file no longer exists");
            try { await _mediaFileRepo.RemoveByPathAsync(Path.GetFullPath(filePath)); } catch { }
            return string.Empty;
        }

        // Music files take a much leaner code path — no HDR/4K/HW-accel logic, no
        // per-track audio profiles, no subtitle handling. Dispatch on extension here
        // so the video path below stays unchanged.
        if (_fileService.GetMediaKind(filePath) == MediaKind.Music)
            return await AddMusicFileAsync(filePath, options, force, cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            var earlyNormalizedPath = Path.GetFullPath(filePath);

            // Cheap early-outs BEFORE the expensive ffprobe + 15s bitrate sampling.
            // Re-adding an already-processed library used to re-probe every file just
            // to print "already completed" — thousands of sequential ffprobe/ffmpeg
            // spawns against NAS storage for nothing.
            if (FindWorkItemByPath(earlyNormalizedPath) is { Status: WorkItemStatus.Pending
                    or WorkItemStatus.Processing or WorkItemStatus.Uploading
                    or WorkItemStatus.Downloading })
            {
                return string.Empty;
            }

            if (_isRemoteJobChecker != null && await _isRemoteJobChecker(earlyNormalizedPath))
            {
                Console.WriteLine($"Skipping {Path.GetFileName(filePath)}: already active as a remote job");
                return string.Empty;
            }

            // Settled-status early-out, but only when the file is provably unchanged
            // (mtime + size match) — otherwise fall through to the full change-detection
            // logic below, which needs probe data (duration delta). Queued counts as
            // settled here: the queue is the DB now, and an unchanged already-queued
            // row needs no re-probe and no re-add (the in-memory check above only
            // covers rows hydrated into the working window).
            var earlyDbFile = await _mediaFileRepo.GetByPathAsync(earlyNormalizedPath);
            if (!force && earlyDbFile != null
                && earlyDbFile.FileMtime == fileInfo.LastWriteTimeUtc.Ticks
                && earlyDbFile.FileSize == fileInfo.Length
                && earlyDbFile.Status is MediaFileStatus.Failed or MediaFileStatus.Cancelled
                    or MediaFileStatus.Completed or MediaFileStatus.Queued)
            {
                Console.WriteLine($"Skipping {Path.GetFileName(filePath)}: previously {earlyDbFile.Status} (unchanged on disk)");
                return string.Empty;
            }

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
                Probe = probe,
                Kind = MediaKind.Video,
                ForceMux = forceMux,
            };

            // Don't add items that already meet the requirements.
            bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
            bool targetIsAv1 = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
            bool isAv1 = sourceCodec == "av1";
            // H.264 targets must match the actual source codec — "not HEVC" would
            // misclassify MPEG-2/VC-1/XviD/VP9 sources as already-H.264 and skip them.
            bool isH264 = sourceCodec is "h264" or "avc";
            // A source already in a codec at least as efficient as the target counts as
            // "already at target" — so an AV1 source isn't shrunk into an HEVC target.
            bool alreadyTargetCodec = SourceCodecMeetsTarget(isAv1, isHevc, isH264, targetIsHevc, targetIsAv1);
            bool isHighDef = probe.Streams.Any(s => s.CodecType == "video" && s.Width > 1920);
            workItem.Is4K = isHighDef;

            var videoStream = probe.Streams.FirstOrDefault(s => s.CodecType == "video");
            var normalizedPath = earlyNormalizedPath;

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

            // Resolve original language eagerly so every skip-ladder predicate sees the same
            // effective keep lists that ConvertVideoAsync would build at encode time. Cached
            // on MediaFile.OriginalLanguage so re-scans avoid re-querying the integration
            // provider, and so WouldSkipUnderOptions / AnalyzeFileAsync can stay in sync.
            // Reuses the row fetched before the probe — nothing else mutates it mid-add.
            var dbFile = earlyDbFile;
            string? originalLanguage = await ResolveOriginalLanguageAsync(filePath, options, dbFile, cancellationToken);
            var effectiveOptions = WithOriginalLanguageMerged(options, originalLanguage);
            bool isHdr = FfprobeService.IsHdr(probe);

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
                    IsHdr = isHdr,
                    Is4K = isHighDef,
                    Status = MediaFileStatus.Skipped,
                    LastScannedAt = DateTime.UtcNow,
                    FileMtime = fileInfo.LastWriteTimeUtc.Ticks,
                    // Per-track summaries so the Mux re-evaluation on settings save can decide
                    // whether to flip this file back to Unseen without a re-probe.
                    AudioStreams    = MuxStreamSummary.Serialize(ProjectAudioSummaries(probe)),
                    SubtitleStreams = MuxStreamSummary.Serialize(ProjectSubtitleSummaries(probe)),
                    OriginalLanguage = originalLanguage,
                });
            }

            // "Process Item" / "Process Directory" force-mux: evaluate this file as Hybrid
            // even when the global mode is Transcode, so an at-target file gets a video-copy
            // mux pass (audio/subs re-applied, container normalized) instead of being skipped.
            // Above-target / wrong-codec files still re-encode through the normal ladder below.
            // Cloned so the caller's options object isn't mutated.
            if (forceMux && effectiveOptions.EncodingMode == EncodingMode.Transcode)
            {
                effectiveOptions = effectiveOptions.Clone();
                effectiveOptions.EncodingMode = EncodingMode.Hybrid;
            }

            // Bypass the skip gate only when a non-Transcode mode has actual work to do on
            // this file. A pointless remux of a file that already matches every audio/subtitle
            // setting is never what the user wants. For a force-mux item, a container change
            // (source extension != the configured output Format) also counts as work.
            bool hasMuxableWork       = HasMuxableWork(effectiveOptions, probe);
            bool needsContainerChange = forceMux && NeedsContainerChange(effectiveOptions, filePath);
            bool bypassSkip = (effectiveOptions.EncodingMode != EncodingMode.Transcode && hasMuxableWork)
                              || needsContainerChange;

            // MuxOnly: video is never re-encoded, so a file with no muxable audio/sub work
            // (and, for a force-mux item, no container change) has nothing to do — skip it
            // unconditionally, even if it's above the bitrate target.
            if (effectiveOptions.EncodingMode == EncodingMode.MuxOnly && !hasMuxableWork && !needsContainerChange)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: MuxOnly mode, no audio/subtitle work");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            if (effectiveOptions.Skip4K && isHighDef)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: 4K video (Skip 4K enabled)");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            // Label the SOURCE codec (not the target) — a skip can fire because the source is
            // already as efficient as the target (e.g. an AV1 source under an HEVC target).
            string sourceCodecLabel = isAv1 ? "AV1" : isHevc ? "HEVC" : isH264 ? "H.264" : sourceCodec.ToUpperInvariant();
            double skipMultiplier = 1.0 + (Math.Clamp(effectiveOptions.SkipPercentAboveTarget, 0, 100) / 100.0);
            if (alreadyTargetCodec && bitrate > 0 && bitrate <= effectiveOptions.TargetBitrate * skipMultiplier && !isHighDef && !bypassSkip)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: already {sourceCodecLabel} at {bitrate}kbps (target {effectiveOptions.TargetBitrate}kbps, skip threshold {skipMultiplier:P0})");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            int fourKMultiplier = Math.Clamp(effectiveOptions.FourKBitrateMultiplier, 2, 8);
            int fourKTarget = effectiveOptions.TargetBitrate * fourKMultiplier;
            if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= fourKTarget * skipMultiplier && !bypassSkip)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: already {sourceCodecLabel} 4K at {bitrate}kbps (4K target {fourKTarget}kbps)");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            // Skip low-bitrate non-HEVC files when using VAAPI CQP — it can't target specific bitrates.
            // Check both explicit VAAPI selection and "auto" (which resolves to VAAPI on Linux NAS).
            bool isVaapiMode = IsVaapiAcceleration(effectiveOptions.HardwareAcceleration) ||
                (effectiveOptions.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                    _detectedHardware != null && IsVaapiAcceleration(_detectedHardware));
            if (isVaapiMode && !isHevc && targetIsHevc && bitrate > 0 && bitrate <= effectiveOptions.TargetBitrate && !isHighDef && !bypassSkip)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: VAAPI can't compress {bitrate}kbps H.264 below target");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            // No-op gate: an HEVC file just above the bitrate ceiling would otherwise fall through
            // to a videoCopy=true encode, and if the audio/sub pipeline has nothing to change either,
            // running ffmpeg just produces a near-identical output. Skip it instead.
            if (!bypassSkip && WouldEncodeBeNoOp(
                    effectiveOptions, bitrate, isHevc, videoStream?.Height ?? 0, FfprobeService.IsHdr(probe),
                    ProjectAudioSummaries(probe), ProjectSubtitleSummaries(probe)))
            {
                Console.WriteLine($"Skipping {workItem.FileName}: no work to do (video would copy, no audio/sub changes, no filters)");
                await MarkSkippedInDb();
                return workItem.Id;
            }

            Console.WriteLine($"Queuing {workItem.FileName}: {sourceCodec} {bitrate}kbps {(isHighDef ? "4K" : "HD")}");

            if (FindWorkItemByPath(normalizedPath) is { Status: WorkItemStatus.Pending
                    or WorkItemStatus.Processing or WorkItemStatus.Uploading
                    or WorkItemStatus.Downloading })
            {
                return workItem.Id;
            }

            if (_isRemoteJobChecker != null && await _isRemoteJobChecker(normalizedPath))
            {
                Console.WriteLine($"Skipping {workItem.FileName}: already active as a remote job");
                return workItem.Id;
            }

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
                var existing = FindWorkItemByPath(normalizedPath);
                if (existing != null)
                    UnregisterWorkItem(existing.Id);
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
                IsHdr = isHdr,
                Is4K = isHighDef,
                Status = MediaFileStatus.Queued,
                Priority = ResolveFolderOverride(filePath)?.QueuePriority ?? 0,
                ForceMux = forceMux,
                LastScannedAt = DateTime.UtcNow,
                FileMtime = fileInfo.LastWriteTimeUtc.Ticks,
                AudioStreams    = MuxStreamSummary.Serialize(ProjectAudioSummaries(probe)),
                SubtitleStreams = MuxStreamSummary.Serialize(ProjectSubtitleSummaries(probe)),
                OriginalLanguage = originalLanguage,
            });

            // The Queued row IS the queue entry — no WorkItem is parked in memory.
            // The scheduler hydrates the row when it reaches the top of the queue
            // order, so pending items beyond the working window cost nothing.
            MarkQueueWindowDirty();
            await NotifyQueueChangedAsync();

            // Wake the cluster dispatcher in parallel with the local scheduler
            // so workers compete for fast jobs (music) instead of losing every
            // item to the event-driven local loop.
            try { _onWorkItemQueued?.Invoke(); } catch { }

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
    ///     Music-aware counterpart to <see cref="AddFileAsync"/>. Probes the file,
    ///     applies the music-specific skip ladder (codec+bitrate match, lossy-to-lossless
    ///     guard), then creates a <see cref="MediaFile"/> row and a <see cref="WorkItem"/>
    ///     tagged with <see cref="MediaKind.Music"/> and queues it. Music jobs reserve the
    ///     synthetic <c>"music"</c> device through the shared <see cref="SlotLedger"/>,
    ///     keeping them off the per-device video pool so a queue of music files can't
    ///     starve a 4K HEVC encode (and vice versa).
    /// </summary>
    private async Task<string> AddMusicFileAsync(
        string filePath, EncoderOptions options, bool force, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileInfo = new FileInfo(filePath);
            var probe    = await _ffprobeService.ProbeAsync(filePath, cancellationToken);

            double length = 0;
            if (double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) length = d;

            var sourceCodec  = MusicEncoderArgs.GetSourceCodec(probe);
            var sourceKbps   = MusicEncoderArgs.GetSourceBitrateKbps(probe);
            var sourceChans  = MusicEncoderArgs.GetSourceChannels(probe);

            var workItem = new WorkItem
            {
                FileName = _fileService.GetFileName(filePath),
                Path     = filePath,
                Size     = fileInfo.Length,
                Bitrate  = sourceKbps,
                Length   = length,
                Probe    = probe,
                Kind     = MediaKind.Music,
            };

            var music = options.Music;
            var targetEncoder = MusicEncoderArgs.ResolveEncoder(music.Codec);
            bool targetLossless = MusicEncoderArgs.IsLossless(targetEncoder);
            bool sourceLossy    = MusicEncoderArgs.IsLossy(sourceCodec);
            var normalizedPath  = Path.GetFullPath(filePath);

            async Task MarkSkippedInDb(string reason)
            {
                Console.WriteLine($"Skipping {workItem.FileName}: {reason}");
                await _mediaFileRepo.UpsertAsync(new MediaFile
                {
                    FilePath      = normalizedPath,
                    Directory     = Path.GetDirectoryName(normalizedPath) ?? "",
                    FileName      = Path.GetFileName(normalizedPath),
                    BaseName      = Path.GetFileNameWithoutExtension(normalizedPath),
                    FileSize      = fileInfo.Length,
                    Bitrate       = sourceKbps,
                    Codec         = sourceCodec,
                    Duration      = length,
                    Kind          = MediaKind.Music,
                    Status        = MediaFileStatus.Skipped,
                    LastScannedAt = DateTime.UtcNow,
                    FileMtime     = fileInfo.LastWriteTimeUtc.Ticks,
                });
            }

            // Lossy → lossless: re-encoding can't recover quality, so it just bloats files.
            // Always skip with a clear reason; user can force via per-folder override.
            if (sourceLossy && targetLossless && !force)
            {
                await MarkSkippedInDb("lossy-to-lossless avoided (no quality recovery possible)");
                return workItem.Id;
            }

            // Codec + bitrate match — re-encoding lossy → lossy at near-target loses quality
            // without saving meaningful space. Lossless source + lossless target is also a noop.
            if (music.SkipIfAlreadyTargetCodec && !force)
            {
                bool codecMatch = string.Equals(sourceCodec, targetEncoder, StringComparison.OrdinalIgnoreCase)
                    || (sourceCodec.Equals("aac", StringComparison.OrdinalIgnoreCase) && targetEncoder.Equals("aac", StringComparison.OrdinalIgnoreCase))
                    || (sourceCodec.Equals("flac", StringComparison.OrdinalIgnoreCase) && targetEncoder.Equals("flac", StringComparison.OrdinalIgnoreCase));

                if (codecMatch && sourceKbps > 0 && !targetLossless)
                {
                    int tolerance = Math.Max(0, music.BitrateMatchTolerancePct);
                    long lower = music.BitrateKbps * (100L - tolerance) / 100;
                    long upper = music.BitrateKbps * (100L + tolerance) / 100;
                    if (sourceKbps >= lower && sourceKbps <= upper)
                    {
                        await MarkSkippedInDb($"already {sourceCodec} at {sourceKbps}kbps (target {music.BitrateKbps}kbps ±{tolerance}%)");
                        return workItem.Id;
                    }
                }
                else if (codecMatch && targetLossless && string.Equals(sourceCodec, "flac", StringComparison.OrdinalIgnoreCase))
                {
                    await MarkSkippedInDb("already flac (lossless target matches source)");
                    return workItem.Id;
                }
            }

            var dbFile = await _mediaFileRepo.GetByPathAsync(normalizedPath);

            if (FindWorkItemByPath(normalizedPath) is { Status: WorkItemStatus.Pending
                    or WorkItemStatus.Processing or WorkItemStatus.Uploading
                    or WorkItemStatus.Downloading })
            {
                return workItem.Id;
            }

            if (dbFile != null)
            {
                // Change detection mirrors the video path — a >10% size change or
                // >30s duration change means the file was replaced, so reset and
                // start fresh. Without this a corrected re-rip would be skipped
                // because the prior row still says Completed/Failed.
                double sizeDelta     = dbFile.FileSize > 0 ? Math.Abs(1.0 - (double)fileInfo.Length / dbFile.FileSize) : 0;
                double durationDelta = dbFile.Duration > 0 && length > 0 ? Math.Abs(dbFile.Duration - length) : 0;
                bool fileChanged     = sizeDelta > 0.10 || durationDelta > 30;

                if (fileChanged)
                {
                    Console.WriteLine($"Music file changed on disk: {workItem.FileName} (size: {dbFile.FileSize}→{fileInfo.Length}) — resetting");
                    await _mediaFileRepo.ResetFileAsync(normalizedPath);
                }
                else if (!force && dbFile.Status is MediaFileStatus.Cancelled)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: previously cancelled by user");
                    return workItem.Id;
                }
                else if (!force && dbFile.Status is MediaFileStatus.Failed && dbFile.FailureCount >= 3)
                {
                    // Music encodes are fast and the pipeline is new — give a few retry
                    // attempts before giving up so a transient bug or one-off ffmpeg
                    // hiccup doesn't permanently bench a track. After 3 strikes the user
                    // has to fix something or explicitly force-retry.
                    Console.WriteLine($"Skipping {workItem.FileName}: failed {dbFile.FailureCount} times — permanent fail");
                    return workItem.Id;
                }
                else if (!force && dbFile.Status is MediaFileStatus.Completed)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already completed");
                    return workItem.Id;
                }
                else if (!force && dbFile.Status is MediaFileStatus.Queued)
                {
                    // Already in the DB queue and unchanged on disk — nothing to do.
                    return workItem.Id;
                }
            }

            if (force)
            {
                var existing = FindWorkItemByPath(normalizedPath);
                if (existing != null)
                    UnregisterWorkItem(existing.Id);
            }

            await _mediaFileRepo.UpsertAsync(new MediaFile
            {
                FilePath      = normalizedPath,
                Directory     = Path.GetDirectoryName(normalizedPath) ?? "",
                FileName      = Path.GetFileName(normalizedPath),
                BaseName      = Path.GetFileNameWithoutExtension(normalizedPath),
                FileSize      = fileInfo.Length,
                Bitrate       = sourceKbps,
                Codec         = sourceCodec,
                Duration      = length,
                Kind          = MediaKind.Music,
                Status        = MediaFileStatus.Queued,
                Priority      = ResolveFolderOverride(filePath)?.QueuePriority ?? 0,
                LastScannedAt = DateTime.UtcNow,
                FileMtime     = fileInfo.LastWriteTimeUtc.Ticks,
            });

            Console.WriteLine($"Queuing music {workItem.FileName}: {sourceCodec} {sourceKbps}kbps {sourceChans}ch → {music.Codec} {music.BitrateKbps}kbps");

            // The Queued row IS the queue entry — no WorkItem is parked in memory.
            // The scheduler hydrates the row when it reaches the top of the queue order.
            MarkQueueWindowDirty();
            await NotifyQueueChangedAsync();

            // Wake the cluster dispatcher in parallel with the local scheduler
            // so workers compete for music items instead of losing every one to
            // the event-driven local loop. Music encodes finish in seconds, so
            // master local would otherwise burn through every queued item
            // between the cluster's 2-second timer ticks.
            try { _onWorkItemQueued?.Invoke(); } catch { }

            _ = Task.Run(async () =>
            {
                try { await ProcessQueueAsync(options); }
                catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync: {ex.Message}"); }
            });

            return workItem.Id;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to add music file: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Adds all media files (video + music) in a directory to the encoding queue.
    ///     Files are probed sequentially to avoid overwhelming NAS storage.
    ///     Encoding starts as soon as the first file is scanned — no waiting for the full batch.
    /// </summary>
    /// <param name="directoryPath">The directory to scan for media files.</param>
    /// <param name="options">Encoder options to apply to all files.</param>
    /// <param name="recursive">When <c>true</c>, subdirectories are also scanned.</param>
    /// <param name="force">When <c>true</c>, bypasses DB status checks — used for explicit user selection.</param>
    /// <param name="forceMux">When <c>true</c>, files are queued as Hybrid mux passes (see <see cref="AddFileAsync"/>).</param>
    /// <returns>A summary message with the count of files added.</returns>
    public async Task<string> AddDirectoryAsync(string directoryPath, EncoderOptions options, bool recursive = true, bool force = false, bool forceMux = false)
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
        var mediaFiles = _fileService.GetAllMediaFiles(directories);

        // Probe files sequentially to avoid overwhelming NAS storage.
        // Each file triggers queue processing via AddFileAsync, so encoding
        // starts as soon as the first file is scanned — no waiting for the full scan.
        int addedCount = 0;
        foreach (var (file, _) in mediaFiles)
        {
            try
            {
                await AddFileAsync(file, options, force, forceMux);
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
    /// <param name="progress">
    ///     Optional per-file progress callback <c>(processed, total)</c>. Invoked from
    ///     worker threads; total is reported once up front as <c>(0, total)</c>.
    /// </param>
    public async Task<List<FileAnalysisResult>> AnalyzeDirectoryAsync(
        string directoryPath, EncoderOptions options, bool recursive = true,
        CancellationToken cancellationToken = default, Action<int, int>? progress = null)
    {
        var directories = recursive
            ? _fileService.RecursivelyFindDirectories(directoryPath)
            : new List<string> { directoryPath };
        var mediaFiles = _fileService.GetAllMediaFiles(directories);
        progress?.Invoke(0, mediaFiles.Count);

        // Bounded parallelism: DB-cached files resolve in microseconds, but stale
        // ones cost an ffprobe each. A whole-library dry run used to walk them one
        // at a time inside a single HTTP request, which is why it timed out on
        // 30k-file libraries. Kept modest so a NAS isn't saturated with probes.
        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        var results = new FileAnalysisResult[mediaFiles.Count];
        int processed = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, mediaFiles.Count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (i, token) =>
            {
                var (file, kind) = mediaFiles[i];
                results[i] = kind == MediaKind.Music
                    ? await AnalyzeMusicFileAsync(file, options, token)
                    : await AnalyzeFileAsync(file, options, token);
                progress?.Invoke(Interlocked.Increment(ref processed), mediaFiles.Count);
            });
        return results.ToList();
    }

    /// <summary>
    ///     Music dry-run prediction. Mirrors the skip ladder in
    ///     <c>AddMusicFileAsync</c> so the analyze modal sees the same outcome the
    ///     real run would produce. Lossy → lossless and codec+bitrate matches are
    ///     surfaced as <c>Skip</c>; everything else is <c>Queue</c>.
    /// </summary>
    private async Task<FileAnalysisResult> AnalyzeMusicFileAsync(string filePath, EncoderOptions options, CancellationToken cancellationToken)
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

            var probe = await _ffprobeService.ProbeAsync(filePath, cancellationToken);
            double length = 0;
            if (double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) length = d;

            result.Duration    = length;
            result.Codec       = MusicEncoderArgs.GetSourceCodec(probe);
            result.BitrateKbps = MusicEncoderArgs.GetSourceBitrateKbps(probe);

            var music         = options.Music;
            var targetEncoder = MusicEncoderArgs.ResolveEncoder(music.Codec);
            bool targetLossless = MusicEncoderArgs.IsLossless(targetEncoder);
            bool sourceLossy    = MusicEncoderArgs.IsLossy(result.Codec);

            if (sourceLossy && targetLossless)
            {
                result.Decision = "Skip";
                result.Reason   = "lossy → lossless avoided (no quality recovery)";
                return result;
            }

            bool codecMatch = string.Equals(result.Codec, targetEncoder, StringComparison.OrdinalIgnoreCase);
            if (music.SkipIfAlreadyTargetCodec && codecMatch)
            {
                if (targetLossless)
                {
                    result.Decision = "Skip";
                    result.Reason   = "already in target lossless codec";
                    return result;
                }
                if (result.BitrateKbps > 0)
                {
                    int tolerance = Math.Max(0, music.BitrateMatchTolerancePct);
                    long lower = music.BitrateKbps * (100L - tolerance) / 100;
                    long upper = music.BitrateKbps * (100L + tolerance) / 100;
                    if (result.BitrateKbps >= lower && result.BitrateKbps <= upper)
                    {
                        result.Decision = "Skip";
                        result.Reason   = $"already {result.Codec} at {result.BitrateKbps}kbps (target {music.BitrateKbps}±{tolerance}%)";
                        return result;
                    }
                }
            }

            result.Decision         = "Queue";
            result.Reason           = $"transcode → {music.Codec} {music.BitrateKbps}kbps";
            result.EncodeTargetKbps = music.BitrateKbps;
            return result;
        }
        catch (Exception ex)
        {
            result.Decision = "Error";
            result.Reason   = ex.Message;
            return result;
        }
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

                // Match AddFileAsync: start with file-size÷duration (total bitrate including
                // audio/subs), then re-measure video-only via the same 15s copy pass when the
                // total is within 2× the effective target. Without this, a file with chunky
                // audio inflates the analyze bitrate, predicting Queue when AddFileAsync would
                // re-measure to a lower video-only number and Skip.
                long totalBitrate = length > 0 ? (long)(fileInfo.Length * 8 / length / 1000) : 0;
                bitrate = totalBitrate;
                int analyzeFourK = options.TargetBitrate * Math.Clamp(options.FourKBitrateMultiplier, 2, 8);
                int analyzeEffectiveTarget = (videoStream?.Width ?? 0) > 1920
                    ? analyzeFourK
                    : options.TargetBitrate;
                if (bitrate > 0 && bitrate <= analyzeEffectiveTarget * 2 && length > 5)
                {
                    long videoBitrate = await MeasureVideoBitrateAsync(
                        new WorkItem { Path = filePath, Length = length });
                    if (videoBitrate > 0 && videoBitrate <= totalBitrate)
                        bitrate = videoBitrate;
                }
            }

            result.Codec = sourceCodec;
            result.BitrateKbps = bitrate;
            result.Width = width;
            result.Height = height;
            result.Duration = length;

            // Resolve original language up front so the skip ladder predicts using the same
            // effective keep lists ConvertVideoAsync would use. Reads the cached value when
            // the row already has one (eg. AddFileAsync ran earlier), otherwise hits the
            // configured integration provider live. Falls back to no-merge on failure.
            string? originalLanguage = await ResolveOriginalLanguageAsync(filePath, options, dbFile, cancellationToken);
            // Persist any newly resolved value so subsequent re-evals / scans / dispatches are
            // cache hits — every place that resolves the language should update the row, so the
            // value ends up consistent across analyze, scan, and dispatch.
            if (!string.IsNullOrEmpty(originalLanguage)
                && dbFile != null
                && !string.Equals(dbFile.OriginalLanguage, originalLanguage, StringComparison.OrdinalIgnoreCase))
            {
                dbFile.OriginalLanguage = originalLanguage;
                try { await _mediaFileRepo.UpsertAsync(dbFile); }
                catch { /* analyze is a dry-run; a DB blip here doesn't change the verdict */ }
            }
            options = WithOriginalLanguageMerged(options, originalLanguage);

            bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
            bool targetIsAv1 = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
            // Match the actual source codec for H.264 targets — see AddFileAsync.
            bool isH264 = sourceCodec.Equals("h264", StringComparison.OrdinalIgnoreCase)
                       || sourceCodec.Equals("avc", StringComparison.OrdinalIgnoreCase);
            // A source already in a codec at least as efficient as the target counts as
            // "already at target" — so an AV1 source isn't shrunk into an HEVC target.
            bool alreadyTargetCodec = SourceCodecMeetsTarget(isAv1, isHevc, isH264, targetIsHevc, targetIsAv1);
            bool isHighDef = width > 1920;
            result.Is4K = isHighDef;

            string targetCodecLabel = targetIsAv1 ? "AV1" : (targetIsHevc ? "HEVC" : "H.264");
            // Label the SOURCE codec in skip reasons — a skip can fire because the source is
            // already as efficient as the target (e.g. "Already AV1" under an HEVC target).
            string sourceCodecLabel = isAv1 ? "AV1" : isHevc ? "HEVC" : isH264 ? "H.264" : sourceCodec.ToUpperInvariant();

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
                result.Reason   = $"Already {sourceCodecLabel} · {bitrate}kbps ≤ {skipCeilingHd}kbps{tolLabel}.";
                return result;
            }

            if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= skipCeiling4K && !bypassSkip)
            {
                result.Decision = "Skip";
                result.Reason   = $"Already {sourceCodecLabel} 4K · {bitrate}kbps ≤ {skipCeiling4K}kbps (4K target {fourKTarget}kbps).";
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

            // No-op gate (mirrors AddFileAsync). Prefer the live probe's HDR signal when we
            // have one; fall back to the cached MediaFile.IsHdr that AddFileAsync persisted on
            // its last full probe. Without the cached read, the dbFresh branch would silently
            // treat HDR sources as SDR — a tonemap-on user would see analyze report Skip while
            // AddFileAsync would queue for transcode (active filter forces re-encode).
            int sourceHeight = height;
            bool isHdr = probe != null
                ? FfprobeService.IsHdr(probe)
                : dbFile?.IsHdr ?? false;
            if (!bypassSkip && WouldEncodeBeNoOp(options, bitrate, isHevc, sourceHeight, isHdr, audioStreams, subtitleStreams))
            {
                result.Decision = "Skip";
                result.Reason   = $"Already {sourceCodecLabel} at {bitrate}kbps · no audio/subtitle changes · no filters — nothing to do.";
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
        // Register-then-unregister: the reverse order leaves a window where the
        // item is in neither the dictionary nor the path index, and a concurrent
        // window sync would hydrate a duplicate Pending item for a job that is
        // mid-dispatch. UnregisterWorkItem only clears the path-index entry when
        // it still points at the removed id, so the new registration survives.
        workItem.Id = newId;
        RegisterWorkItem(workItem);
        UnregisterWorkItem(oldId);
    }

    /// <summary>Returns <c>true</c> if the specified file path is currently Pending or Processing in the queue.</summary>
    /// <param name="filePath">The file path to check.</param>
    public bool IsFileQueued(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        return _workItems.Values.Any(w =>
            w.NormalizedPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
            w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing
                or WorkItemStatus.Uploading or WorkItemStatus.Downloading);
    }

    /// <summary>Returns all known work items ordered by creation time descending.</summary>
    public List<WorkItem> GetAllWorkItems()
    {
        return _workItems.Values.OrderByDescending(x => x.CreatedAt).ToList();
    }

    /// <summary>
    ///     Status counts for the stats endpoint. Pending comes from the DB — the
    ///     authoritative queue — via one indexed COUNT (hydrated window items are
    ///     Queued rows too, so memory must not add to it). Processing and the
    ///     recent completed/failed counts come from the in-memory registry, which
    ///     matches what the queue page can actually list.
    /// </summary>
    public async Task<(int Pending, int Processing, int Completed, int Failed, int Total)> GetWorkItemCountsAsync()
    {
        int processing = 0, completed = 0, failed = 0;
        foreach (var item in _workItems.Values)
        {
            switch (item.Status)
            {
                case WorkItemStatus.Completed:  completed++;  break;
                case WorkItemStatus.Failed:     failed++;     break;
                case WorkItemStatus.Processing:
                case WorkItemStatus.Uploading:
                case WorkItemStatus.Downloading: processing++; break;
            }
        }
        int pending = await _mediaFileRepo.CountQueuedLocalAsync();
        return (pending, processing, completed, failed, pending + processing + completed + failed);
    }

    /// <summary>
    ///     Total media-file rows this install has ever recorded. The queue UI uses this to
    ///     tell a genuine first run (nothing ever scanned) apart from an established library
    ///     that just has an empty queue right now, so the onboarding hero only shows once.
    /// </summary>
    public Task<int> GetKnownFileCountAsync() => _mediaFileRepo.CountAllAsync();

    /// <summary>Removes a work item from the in-memory tracking dictionary (e.g. after remote job cleanup).</summary>
    public void RemoveWorkItem(string id) => UnregisterWorkItem(id);

    /******************************************************************
     *  Work-item registration (keeps the path index coherent)
     ******************************************************************/

    /// <summary>
    ///     Inserts (or re-inserts) a work item into <see cref="_workItems"/> and the
    ///     path index. The sole write path into the dictionary — direct mutation
    ///     would silently desynchronize duplicate detection.
    /// </summary>
    private void RegisterWorkItem(WorkItem item)
    {
        _workItems[item.Id] = item;
        if (item.NormalizedPath.Length > 0)
            _pathIndex[item.NormalizedPath] = item.Id;
    }

    /// <summary> Removes a work item and its path-index entry (only when the entry still points at this id). </summary>
    private void UnregisterWorkItem(string id)
    {
        if (!_workItems.TryRemove(id, out var item)) return;
        if (item.NormalizedPath.Length > 0)
            _pathIndex.TryRemove(new KeyValuePair<string, string>(item.NormalizedPath, id));
    }

    /// <summary> Clears the dictionary and index together. </summary>
    private void ClearWorkItems()
    {
        _workItems.Clear();
        _pathIndex.Clear();
    }

    /// <summary>
    ///     O(1) lookup of the in-memory work item (any status) registered for a
    ///     normalized path, or null. Replaces the full-dictionary scans the add
    ///     paths used for duplicate detection.
    /// </summary>
    private WorkItem? FindWorkItemByPath(string normalizedPath)
        => _pathIndex.TryGetValue(normalizedPath, out var id) && _workItems.TryGetValue(id, out var item)
            ? item
            : null;

    /// <summary>
    ///     Canonical pending-queue ordering: user priority first ("move to front"),
    ///     then bitrate descending (big files first maximizes perceived throughput).
    ///     Every <c>_workQueue.Sort</c> call uses this comparison; the API's queue
    ///     listing mirrors it so the on-screen order matches dispatch order.
    /// </summary>
    internal static int CompareQueueOrder(WorkItem a, WorkItem b)
        => CompareQueueOrder(a, b, newestFirst: false);

    /// <summary>
    ///     Policy-aware variant: with newest-first enabled the tiebreaker is queue
    ///     recency instead of bitrate, matching the DB ordering so the window's
    ///     internal dispatch order agrees with what the queue page shows.
    /// </summary>
    internal static int CompareQueueOrder(WorkItem a, WorkItem b, bool newestFirst)
    {
        int byPriority = b.Priority.CompareTo(a.Priority);
        if (byPriority != 0) return byPriority;
        return newestFirst
            ? b.QueuedAt.CompareTo(a.QueuedAt)
            : b.Bitrate.CompareTo(a.Bitrate);
    }

    /******************************************************************
     *  DB-first queue window
     ******************************************************************/

    /// <summary>
    ///     Flags the working window as stale relative to the DB queue and nudges
    ///     the scheduler. Cheap and idempotent — call after anything that changes
    ///     which rows belong at the head of the queue (add, cancel, prioritize,
    ///     policy change, remote requeue).
    /// </summary>
    public void MarkQueueWindowDirty()
    {
        // External queue changes (add/cancel/prioritize/policy) may have changed
        // the head of the order — abandon any in-progress rotation so the window
        // snaps back to the true top.
        _windowRotationOffset = 0;
        Interlocked.Exchange(ref _queueWindowDirty, 1);
        WakeScheduler();
    }

    /// <summary> Applies the queue-order policy from saved options (item-8 wiring). </summary>
    public void SetQueueOrderNewestFirst(bool newestFirst)
    {
        if (_queueNewestFirst == newestFirst) return;
        _queueNewestFirst = newestFirst;
        MarkQueueWindowDirty();
    }

    /// <summary>
    ///     Reconciles the in-memory working window with the top of the DB pending
    ///     queue: hydrates rows that should be in the window, evicts pending items
    ///     that fell out of the top-N (their rows remain Queued — nothing is lost),
    ///     and lazily quarantines rows whose source file vanished. No-op unless the
    ///     window has been marked dirty, so calling per scheduler tick is free.
    /// </summary>
    /// <param name="rotationOffset">
    ///     Row offset into the queue order; non-zero only when the scheduler is
    ///     rotating past a locally-unservable head (see <see cref="_windowRotationOffset"/>).
    /// </param>
    internal async Task SyncQueueWindowAsync(int rotationOffset = 0)
    {
        if (Volatile.Read(ref _queueWindowDirty) == 0) return;

        await _windowSyncLock.WaitAsync();
        try
        {
            if (Interlocked.Exchange(ref _queueWindowDirty, 0) == 0) return;

            var topRows = await _mediaFileRepo.GetQueueWindowAsync(
                QueueWindowSize, _queueNewestFirst, rotationOffset);

            // Kind representation: the global order is bitrate-weighted, so 50+
            // pending videos would keep every music row out of the window forever —
            // and the dedicated music slots (master + workers) dispatch only from
            // the window. Reserve a few slots for whichever kind got shut out.
            const int KindFloor = 8;
            if (topRows.Count > 0)
            {
                if (!topRows.Any(r => r.Kind == MediaKind.Music))
                {
                    var music = await _mediaFileRepo.GetQueueWindowAsync(KindFloor, _queueNewestFirst, kind: MediaKind.Music);
                    if (music.Count > 0) topRows = topRows.Take(QueueWindowSize - music.Count).Concat(music).ToList();
                }
                else if (!topRows.Any(r => r.Kind == MediaKind.Video))
                {
                    var video = await _mediaFileRepo.GetQueueWindowAsync(KindFloor, _queueNewestFirst, kind: MediaKind.Video);
                    if (video.Count > 0) topRows = topRows.Take(QueueWindowSize - video.Count).Concat(video).ToList();
                }
            }

            // Storage-outage guard: if EVERY window row's source is unreachable,
            // the library mount is down — bail without evicting or quarantining.
            // Quarantining here would convert the whole backlog to Unseen at full
            // sync speed during a transient NAS outage, destroying queue state
            // (and every move-to-front priority) that would otherwise survive it.
            if (topRows.Count >= 5 && topRows.All(r => !File.Exists(r.FilePath)))
            {
                Console.WriteLine("QueueWindow: library storage appears offline — leaving the queue untouched until it returns");
                return;
            }

            var topPaths = new HashSet<string>(topRows.Select(r => r.FilePath), StringComparer.OrdinalIgnoreCase);

            // Evict pending window items no longer in the target set. Safe: a
            // pending hydrated item carries no state beyond its DB row.
            lock (_queueLock)
            {
                foreach (var stale in _workQueue.Where(w =>
                             w.Status == WorkItemStatus.Pending &&
                             !topPaths.Contains(w.NormalizedPath)).ToList())
                {
                    _workQueue.Remove(stale);
                    UnregisterWorkItem(stale.Id);
                }
            }

            foreach (var row in topRows)
            {
                // Skip rows already represented in memory in any live state —
                // hydrated pending, actively encoding, or mid-transfer.
                var existing = FindWorkItemByPath(row.FilePath);
                if (existing != null && existing.Status is WorkItemStatus.Pending
                        or WorkItemStatus.Processing or WorkItemStatus.Uploading
                        or WorkItemStatus.Downloading)
                    continue;

                if (!File.Exists(row.FilePath))
                {
                    // Source vanished while queued — back to Unseen so the next
                    // scan re-evaluates (and the row stops occupying the window).
                    try { await _mediaFileRepo.SetStatusAsync(row.FilePath, MediaFileStatus.Unseen); }
                    catch (Exception ex) { Console.WriteLine($"QueueWindow: failed to quarantine missing {row.FileName}: {ex.Message}"); }
                    continue;
                }

                var item = new WorkItem
                {
                    FileName = row.FileName,
                    Path     = row.FilePath,
                    Size     = row.FileSize,
                    Bitrate  = row.Bitrate,
                    Length   = row.Duration,
                    IsHevc   = row.IsHevc,
                    Is4K     = row.Is4K,
                    Kind     = row.Kind,
                    Priority = row.Priority,
                    ForceMux = row.ForceMux,
                    QueuedAt = row.CreatedAt,
                    Probe    = null, // lazily probed when processing starts
                };

                RegisterWorkItem(item);
                lock (_queueLock)
                {
                    _workQueue.Add(item);
                    _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst));
                }
            }
        }
        finally
        {
            _windowSyncLock.Release();
        }
    }

    /// <summary> Test accessor: a copy of the hydrated working window. </summary>
    internal List<WorkItem> GetQueueWindowSnapshot()
    {
        lock (_queueLock) return _workQueue.ToList();
    }

    /// <summary>
    ///     Cluster-dispatcher entry point: tops up the window when it has run dry
    ///     but the DB still holds pending rows, so worker nodes keep pulling work
    ///     even when the master's local scheduler loop has exited (paused local
    ///     encoding, off-schedule master, every device disabled).
    /// </summary>
    public async Task EnsureQueueWindowAsync()
    {
        bool windowHasPending;
        lock (_queueLock)
        {
            windowHasPending = _workQueue.Any(w => w.Status == WorkItemStatus.Pending);
        }
        if (!windowHasPending)
            MarkQueueWindowDirty();
        await SyncQueueWindowAsync();
    }

    /// <summary>
    ///     Moves a pending work item to the front of the queue. Accepts either a
    ///     hydrated work-item GUID or a DB-row id of the form <c>mf-{rowId}</c>
    ///     (how the queue UI addresses pending rows beyond the window). The
    ///     authoritative write is the row's Priority column; the hydrated copy
    ///     (when one exists) is updated in place so dispatch order changes now.
    /// </summary>
    public async Task<bool> PrioritizeWorkItemAsync(string id)
    {
        // DB-row addressing (queue UI tiles).
        if (TryParseRowId(id, out var rowId))
        {
            var newPriority = await _mediaFileRepo.BumpPriorityToFrontAsync(rowId);
            if (newPriority == null) return false;

            var row = await _mediaFileRepo.GetByIdAsync(rowId);
            if (row != null && FindWorkItemByPath(row.FilePath) is { Status: WorkItemStatus.Pending } hydrated)
            {
                hydrated.Priority = newPriority.Value;
                lock (_queueLock) { _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst)); }
            }

            MarkQueueWindowDirty();
            await SyncQueueWindowAsync();
            await NotifyQueueChangedAsync();
            Console.WriteLine($"Queue: moved row {rowId} to the front (priority {newPriority})");
            return true;
        }

        // Hydrated-item addressing (legacy path; still used by anything holding a GUID).
        if (!_workItems.TryGetValue(id, out var item) || item.Status != WorkItemStatus.Pending)
            return false;

        var bumped = await _mediaFileRepo.BumpPriorityToFrontAsync(
            await ResolveRowIdByPathAsync(item.NormalizedPath) ?? -1);
        item.Priority = bumped ?? item.Priority + 1;
        lock (_queueLock) { _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst)); }
        MarkQueueWindowDirty();
        await NotifyQueueChangedAsync();
        Console.WriteLine($"Queue: moved {item.FileName} to the front");
        return true;
    }

    /// <summary> Parses the queue UI's <c>mf-{rowId}</c> addressing form. </summary>
    internal static bool TryParseRowId(string id, out int rowId)
    {
        rowId = 0;
        return id.StartsWith("mf-", StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(3), out rowId);
    }

    /// <summary> Looks up the DB row id for a normalized path, or null. </summary>
    private async Task<int?> ResolveRowIdByPathAsync(string normalizedPath)
        => (await _mediaFileRepo.GetByPathAsync(normalizedPath))?.Id;

    /// <summary>UTC ticks of the last QueueChanged broadcast (Interlocked access).</summary>
    private long _lastQueueChangedTicks;

    /// <summary>1 while a trailing-edge QueueChanged send is scheduled.</summary>
    private int _queueChangedPending;

    /// <summary>
    ///     Broadcasts a lightweight "the pending queue changed" signal. The queue UI
    ///     fetches a fresh page in response — pending tiles are DB-sourced, so there
    ///     is no per-item payload to push. Throttled to one broadcast per second with
    ///     a guaranteed trailing send, so a parallel sweep adding hundreds of files a
    ///     minute produces a steady tick instead of an event flood — and the burst's
    ///     final add is never silently dropped.
    /// </summary>
    private async Task NotifyQueueChangedAsync()
    {
        var now  = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastQueueChangedTicks);
        if (now - last >= TimeSpan.TicksPerSecond
            && Interlocked.CompareExchange(ref _lastQueueChangedTicks, now, last) == last)
        {
            try { await _hubContext.Clients.All.SendAsync("QueueChanged"); }
            catch { /* SignalR failures are non-fatal — UI reconciles on next poll */ }
            return;
        }

        // Inside the throttle window — schedule exactly one trailing send.
        if (Interlocked.CompareExchange(ref _queueChangedPending, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1100);
                Interlocked.Exchange(ref _queueChangedPending, 0);
                Interlocked.Exchange(ref _lastQueueChangedTicks, DateTime.UtcNow.Ticks);
                try { await _hubContext.Clients.All.SendAsync("QueueChanged"); }
                catch { /* non-fatal */ }
            });
        }
    }

    /// <summary>
    ///     Drops a work item whose source file is no longer on disk. Removes it from
    ///     <c>_workItems</c> and <c>_workQueue</c>, deletes the matching DB row immediately
    ///     so the next scan doesn't have to do it, and pushes a <c>WorkItemRemoved</c>
    ///     event so the UI clears the stale row without waiting for a page refresh.
    ///     Callers must already have verified the item is not actively encoding.
    /// </summary>
    public async Task DropMissingWorkItemAsync(WorkItem item)
    {
        UnregisterWorkItem(item.Id);
        lock (_queueLock)
        {
            _workQueue.Remove(item);
        }

        try { await _mediaFileRepo.RemoveByPathAsync(Path.GetFullPath(item.Path)); }
        catch (Exception ex) { Console.WriteLine($"DropMissingWorkItem: DB cleanup failed for {item.FileName}: {ex.Message}"); }

        try { await _hubContext.Clients.All.SendAsync("WorkItemRemoved", item.Id); }
        catch { /* SignalR failures are non-fatal — UI will reconcile on next refresh */ }
    }

    /// <summary>
    ///     Memory sweep over finished work items. Two passes:
    ///     <list type="number">
    ///       <item>Releases <see cref="WorkItem.Probe"/> on any terminal item that has
    ///         been quiet for a couple of minutes — completion bookkeeping (encode
    ///         history, cluster keep/delete recompute) reads the probe immediately
    ///         after the status flip, so a settle window avoids racing it.</item>
    ///       <item>Evicts the oldest terminal items beyond <see cref="TerminalWorkItemCap"/>.
    ///         The DB keeps the authoritative outcome; the queue UI reconciles
    ///         dropped IDs on its next full refresh.</item>
    ///     </list>
    /// </summary>
    internal void SweepTerminalWorkItems()
    {
        static bool IsTerminal(WorkItemStatus s) => s is WorkItemStatus.Completed
            or WorkItemStatus.Failed or WorkItemStatus.Cancelled
            or WorkItemStatus.Stopped or WorkItemStatus.NoSavings;

        var now = DateTime.UtcNow;
        var terminal = new List<WorkItem>();
        foreach (var item in _workItems.Values)
        {
            if (!IsTerminal(item.Status)) continue;
            terminal.Add(item);
            if (item.Probe != null && now - item.LastUpdatedAt > TimeSpan.FromMinutes(2))
                item.Probe = null;
        }

        if (terminal.Count <= TerminalWorkItemCap) return;

        int evicted = 0;
        foreach (var victim in terminal.OrderBy(w => w.CompletedAt ?? w.CreatedAt)
                                       .Take(terminal.Count - TerminalWorkItemCap))
        {
            // Re-check right before removal — a Stopped item can be requeued to
            // Pending at any time, and restore it if the flip lands mid-removal.
            if (!IsTerminal(victim.Status)) continue;
            UnregisterWorkItem(victim.Id);
            if (!IsTerminal(victim.Status)) RegisterWorkItem(victim);
            else evicted++;
        }
        if (evicted > 0)
            Console.WriteLine($"Queue sweep: evicted {evicted} finished items from memory (cap {TerminalWorkItemCap}; full history remains in the database)");
    }

    /// <summary>
    ///     Sweeps every non-active work item and drops any whose source file has
    ///     disappeared since it was queued. Active encodes (Processing / Uploading /
    ///     Downloading) are left alone — yanking them mid-stream would race with
    ///     ffmpeg / the cluster transfer, and their own completion paths already
    ///     surface a "source missing" failure if it really has gone.
    /// </summary>
    /// <returns> The number of items dropped. </returns>
    public async Task<int> PruneMissingWorkItemsAsync()
    {
        var snapshot = _workItems.Values
            .Where(w => w.Status is not (WorkItemStatus.Processing
                                          or WorkItemStatus.Uploading
                                          or WorkItemStatus.Downloading))
            .ToList();

        int dropped = 0;
        foreach (var item in snapshot)
        {
            try
            {
                if (File.Exists(item.Path)) continue;
            }
            catch { continue; }

            Console.WriteLine($"PruneMissingWorkItems: dropping {item.FileName} — source no longer exists");
            await DropMissingWorkItemAsync(item);
            dropped++;
        }
        return dropped;
    }


    /// <summary>
    ///     Permanently cancels a work item. The file will not be reprocessed unless explicitly reset.
    ///     For remote items, the cancellation is forwarded to the assigned cluster node.
    /// </summary>
    /// <param name="id"> The ID of the work item to cancel. </param>
    public async Task CancelWorkItemAsync(string id)
    {
        // DB-row addressing ("mf-{rowId}") — how the queue UI refers to pending
        // rows. When the row is hydrated into the working window, fall through to
        // the normal in-memory path so the window copy is cancelled too.
        if (TryParseRowId(id, out var cancelRowId))
        {
            var row = await _mediaFileRepo.GetByIdAsync(cancelRowId);
            if (row == null) return;

            // Redirect only to a LIVE in-memory copy. A stale terminal item (recent
            // history for a file that was since re-queued) would swallow the cancel —
            // none of the status branches below match it — leaving the row Queued.
            if (FindWorkItemByPath(row.FilePath) is { Status: WorkItemStatus.Pending
                    or WorkItemStatus.Processing or WorkItemStatus.Uploading
                    or WorkItemStatus.Downloading } hydrated)
            {
                await CancelWorkItemAsync(hydrated.Id);
                return;
            }

            if (await _mediaFileRepo.SetQueuedRowStatusAsync(cancelRowId, MediaFileStatus.Cancelled))
            {
                MarkQueueWindowDirty();
                await NotifyQueueChangedAsync();
                Console.WriteLine($"Cancel: queued row {cancelRowId} ({row.FileName}) cancelled");
            }
            return;
        }

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

    /// <summary>
    ///     Records a Failed outcome for a job aborted by the per-job watchdog.
    ///     A watchdog cancel raises the same OperationCanceledException as a
    ///     user cancel, but nobody has set a terminal status — without this the
    ///     item stays in Processing forever in standalone mode. No-op when a
    ///     terminal status was already set (user cancel won the race).
    /// </summary>
    private async Task RecordWatchdogFailureAsync(WorkItem workItem)
    {
        if (workItem.Status != WorkItemStatus.Processing) return;

        Interlocked.Increment(ref _localFailedJobs);
        workItem.Status = WorkItemStatus.Failed;
        workItem.ErrorMessage = "Watchdog: no progress for 15 minutes — job aborted.";
        workItem.CompletedAt = DateTime.UtcNow;
        try { await _mediaFileRepo.IncrementFailureCountAsync(Path.GetFullPath(workItem.Path), workItem.ErrorMessage); } catch { }

        if (_notificationService != null && ShouldDispatchExternal)
            _ = _notificationService.NotifyEncodeFailedAsync(Path.GetFileName(workItem.Path), workItem.ErrorMessage);
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
            bool isMusic    = workItem.Kind == MediaKind.Music;
            var srcStream   = workItem.Probe?.Streams?.FirstOrDefault(s =>
                s.CodecType == (isMusic ? "audio" : "video"));
            var srcCodec    = srcStream?.CodecName ?? "";
            var encodedCodec = isMusic ? options.Music.Codec : options.Codec;

            var record = new EncodeHistory
            {
                JobId               = workItem.Id,
                FilePath            = workItem.Path,
                FileName            = workItem.FileName,
                OriginalSizeBytes   = workItem.Size,
                EncodedSizeBytes    = noSavings ? 0 : encodedSize,
                BytesSaved          = noSavings ? 0 : Math.Max(0, workItem.Size - encodedSize),
                OriginalCodec       = srcCodec,
                EncodedCodec        = encodedCodec,
                OriginalBitrateKbps = workItem.Bitrate,
                // ÷1000, not ÷1024 — every other bitrate in the app (scan-time
                // OriginalBitrateKbps included) uses decimal kilobits.
                EncodedBitrateKbps  = workItem.Length > 0 && encodedSize > 0
                    ? (long)(encodedSize * 8.0 / 1000.0 / workItem.Length)
                    : 0,
                DurationSeconds     = workItem.Length,
                EncodeSeconds       = (DateTime.UtcNow - encodeStart).TotalSeconds,
                DeviceId            = string.IsNullOrEmpty(deviceId) ? "cpu" : deviceId,
                NodeId              = _localNodeIdentity.NodeId,
                NodeHostname        = _localNodeIdentity.Hostname,
                WasRemote           = false,
                Is4K                = workItem.Is4K,
                Kind                = workItem.Kind,
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
        // DB-row addressing — mirrors CancelWorkItemAsync. Stop on a pending row
        // maps to Unseen (re-discoverable on the next scan), same as the in-memory
        // pending path below.
        if (TryParseRowId(id, out var stopRowId))
        {
            var row = await _mediaFileRepo.GetByIdAsync(stopRowId);
            if (row == null) return;

            // Live-status filter mirrors CancelWorkItemAsync — a stale terminal
            // history item must not swallow the stop.
            if (FindWorkItemByPath(row.FilePath) is { Status: WorkItemStatus.Pending
                    or WorkItemStatus.Processing or WorkItemStatus.Uploading
                    or WorkItemStatus.Downloading } hydrated)
            {
                await StopWorkItemAsync(hydrated.Id);
                return;
            }

            if (await _mediaFileRepo.SetQueuedRowStatusAsync(stopRowId, MediaFileStatus.Unseen))
            {
                MarkQueueWindowDirty();
                await NotifyQueueChangedAsync();
                Console.WriteLine($"Stop: queued row {stopRowId} ({row.FileName}) returned to Unseen");
            }
            return;
        }

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
    ///     Emits <c>WorkItemRemoved</c> so the UI drops the failed card immediately — the file
    ///     will reappear in the queue on the next AutoScan pass (or via an immediate "Scan Now").
    /// </summary>
    /// <param name="filePath"> Absolute path to the file to reset. </param>
    public async Task RetryFileAsync(string filePath)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        await _mediaFileRepo.ResetFileAsync(normalizedPath);

        var existing = _workItems.Values.FirstOrDefault(w =>
            w.NormalizedPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            UnregisterWorkItem(existing.Id);
            try { await _hubContext.Clients.All.SendAsync("WorkItemRemoved", existing.Id); }
            catch { /* SignalR errors are non-fatal */ }
        }
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

        // True when a servable item has been seen since the last rotation wrap —
        // a full backlog walk without one means nothing here is locally servable.
        bool servedSinceRotationWrap = false;

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

                // Off-schedule on this machine: leave items in the queue for
                // the cluster dispatch loop (workers in their own windows can
                // still pick them up) and exit the local loop. The dispatch
                // timer's RefreshOffScheduleFlags re-triggers ProcessQueueAsync
                // when the master's schedule reopens.
                if (_localScheduleGate != null && !_localScheduleGate())
                    break;

                // Reap completed inflight tasks before checking for queue-empty exit.
                inflight.RemoveAll(t => t.IsCompleted);

                var current = _lastOptions ?? options;

                // Top up the working window from the DB queue (no-op unless dirty).
                await SyncQueueWindowAsync(_windowRotationOffset);

                bool anyVideoPending, anyMusicPending;
                int windowPending;
                lock (_queueLock)
                {
                    _workQueue.RemoveAll(w => w.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped);
                    anyVideoPending = _workQueue.Any(w =>
                        w.Kind == MediaKind.Video &&
                        w.Status == WorkItemStatus.Pending &&
                        (_shouldSkipLocal == null || !_shouldSkipLocal(w)));
                    anyMusicPending = _workQueue.Any(w =>
                        w.Kind == MediaKind.Music &&
                        w.Status == WorkItemStatus.Pending &&
                        (_shouldSkipLocal == null || !_shouldSkipLocal(w)));
                    windowPending = _workQueue.Count(w => w.Status == WorkItemStatus.Pending);
                }

                bool queueEmpty = !anyVideoPending && !anyMusicPending;

                if (queueEmpty)
                {
                    // The window says empty, but the real queue is the DB — there
                    // may be more rows than the window holds, or the whole window
                    // may be locally-unservable (skip-local exclusions) with
                    // servable rows sitting deeper in the order.
                    int dbPending = await _mediaFileRepo.CountQueuedLocalAsync();
                    if (dbPending > windowPending)
                    {
                        if (windowPending == 0)
                        {
                            // Window drained — refill from the head of the queue.
                            _windowRotationOffset = 0;
                            Interlocked.Exchange(ref _queueWindowDirty, 1);
                            await SyncQueueWindowAsync();

                            // If nothing hydrated (e.g. rows mid-transition to
                            // Processing, or missing files being quarantined),
                            // pace the retry instead of hot-looping on the DB.
                            bool hydratedAny;
                            lock (_queueLock) hydratedAny = _workQueue.Any(w => w.Status == WorkItemStatus.Pending);
                            if (!hydratedAny) await WaitForSchedulerProgressAsync(inflight);
                            continue;
                        }

                        // Window full of items this machine can't serve — rotate
                        // deeper so servable rows get a turn. Cluster nodes keep
                        // consuming the head regardless. The dirty flag is set
                        // directly (NOT via MarkQueueWindowDirty, which resets the
                        // offset) so the rotation position survives the sync.
                        _windowRotationOffset += QueueWindowSize;
                        if (_windowRotationOffset >= dbPending)
                        {
                            _windowRotationOffset = 0;

                            // A full walk of the backlog produced nothing servable —
                            // stop churning (each pass costs a window rebuild plus
                            // File.Exists per row against the NAS). The items belong
                            // to the cluster loop; any add/cancel/settings change
                            // re-kicks this scheduler via MarkQueueWindowDirty.
                            if (!servedSinceRotationWrap)
                            {
                                if (inflight.Count == 0) break;
                                await WaitForSchedulerProgressAsync(inflight);
                                continue;
                            }
                            servedSinceRotationWrap = false;
                        }
                        Interlocked.Exchange(ref _queueWindowDirty, 1);
                        await SyncQueueWindowAsync(_windowRotationOffset);
                        await WaitForSchedulerProgressAsync(inflight);
                        continue;
                    }

                    if (inflight.Count == 0) break;

                    // Queue is empty but encodes are still finishing — wait for
                    // one to drain or for an explicit wake (settings change)
                    // before re-checking.
                    await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // A servable item is visible. Deliberately do NOT reset the rotation
                // offset here — resetting on every dispatch snapped the window back
                // to the unservable head and re-walked the whole backlog per item.
                // External queue changes reset it via MarkQueueWindowDirty.
                servedSinceRotationWrap = true;

                bool dispatched = false;

                // ───── MUSIC DISPATCH ─────
                // Music targets the synthetic "music" device, reserved through
                // the same SlotLedger workers use. EffectiveDeviceCapacity returns
                // 0 when MasterMusicConcurrency is 0 (or unset and the default-
                // concurrency fallback is 0) — TryReserve refuses, the master
                // skips, and the cluster dispatcher routes the item to a worker.
                if (anyMusicPending && _slotLedger != null)
                {
                    WorkItem? musicItem;
                    lock (_queueLock)
                    {
                        musicItem = _workQueue.FirstOrDefault(w =>
                            w.Kind == MediaKind.Music &&
                            w.Status == WorkItemStatus.Pending &&
                            (_shouldSkipLocal == null || !_shouldSkipLocal(w)));
                        if (musicItem != null) _workQueue.Remove(musicItem);
                    }

                    if (musicItem == null)
                    {
                        // Race: another path consumed it. Nothing to do.
                    }
                    else if (musicItem.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped)
                    {
                        UnregisterWorkItem(musicItem.Id);
                    }
                    else if (!File.Exists(musicItem.Path))
                    {
                        Console.WriteLine($"Dropping {musicItem.FileName}: source file no longer exists at dispatch time");
                        await DropMissingWorkItemAsync(musicItem);
                    }
                    else if (!_slotLedger.TryReserve(_localNodeId, MusicDeviceId, musicItem.Id, musicItem.FileName))
                    {
                        // Master at music capacity (or master music encoding is
                        // disabled by MasterMusicConcurrency=0). Put the item
                        // back so the cluster dispatcher can route it to a
                        // worker, or so a future tick retries once a master
                        // music slot frees up.
                        lock (_queueLock)
                        {
                            _workQueue.Add(musicItem);
                            _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst));
                        }
                    }
                    else
                    {
                        _slotLedger.TransitionPhase(musicItem.Id, SlotPhase.Encoding);
                        // Item left the window — flag it so refill tops up from the DB.
                        Interlocked.Exchange(ref _queueWindowDirty, 1);

                        var musicFolderOverride = ResolveFolderOverride(musicItem.Path);
                        var musicPerJobOptions = EncoderOptionsOverride.ApplyOverrides(current, musicFolderOverride, null);

                        var musicActive = new ActiveLocalJob
                        {
                            Item     = musicItem,
                            Cts      = new CancellationTokenSource(),
                            DeviceId = MusicDeviceId,
                        };
                        _activeLocalJobs[musicItem.Id] = musicActive;
                        musicItem.DispatchedDeviceId = MusicDeviceId;

                        var capturedItem = musicItem;
                        var capturedLedger = _slotLedger;
                        var musicTask = Task.Run(async () =>
                        {
                            try { await ProcessMusicWorkItemAsync(capturedItem, musicPerJobOptions, musicActive); }
                            finally
                            {
                                capturedLedger.Release(capturedItem.Id, ReleaseReason.Completed);
                                WakeScheduler();
                            }
                        });
                        inflight.Add(musicTask);
                        dispatched = true;
                    }
                }

                // ───── VIDEO DISPATCH ─────
                if (!anyVideoPending)
                {
                    if (!dispatched)
                        await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // Pick the next pending video item, then try to reserve a
                // device slot for it via the ledger. Picking-then-reserving
                // (vs. the prior reserve-then-pick) matches the cluster
                // dispatcher's order so the two schedulers see the same
                // head-of-queue when they race, and avoids the "reserved a
                // slot then no item fit it — release" round-trip.
                WorkItem? workItem;
                lock (_queueLock)
                {
                    workItem = _workQueue.FirstOrDefault(w =>
                        w.Kind == MediaKind.Video &&
                        w.Status == WorkItemStatus.Pending &&
                        (_shouldSkipLocal == null || !_shouldSkipLocal(w)));
                    if (workItem != null) _workQueue.Remove(workItem);
                }

                if (workItem == null)
                {
                    if (!dispatched)
                        await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }

                // Source-vanished guard: the source can disappear between scan-time
                // enqueue and dispatch (user deletes the folder, file is renamed,
                // network share drops the path). Drop the item now rather than burn a
                // device slot and have ConvertVideoAsync throw "Source file not found"
                // mid-encode.
                if (!File.Exists(workItem.Path))
                {
                    Console.WriteLine($"Dropping {workItem.FileName}: source file no longer exists at dispatch time");
                    await DropMissingWorkItemAsync(workItem);
                    continue;
                }

                var deviceId = TryReserveLocalDeviceSlot(workItem, current);
                if (deviceId == null)
                {
                    // No master slot fits this item (capacity full, codec
                    // mismatch, or every device disabled). Re-queue so the
                    // cluster dispatcher can route to a worker, or a future
                    // master tick retries once a slot frees up.
                    lock (_queueLock)
                    {
                        _workQueue.Add(workItem);
                        _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst));
                    }
                    if (!dispatched)
                        await WaitForSchedulerProgressAsync(inflight);
                    continue;
                }
                _slotLedger?.TransitionPhase(workItem.Id, SlotPhase.Encoding);
                // Item left the window — flag it so refill tops up from the DB.
                Interlocked.Exchange(ref _queueWindowDirty, 1);

                // Resolve any per-folder override and merge it into the per-job options.
                // Cluster dispatch already does this via ClusterService.ResolveOptionsForJob;
                // local dispatch was the one path that wasn't applying overrides — without
                // this, a folder configured to encode at h264 (say) was queued under those
                // settings (the scan-phase skip ladder ran correctly) but the local encoder
                // ran at the global codec. Three-tier merge: global → folder → (no node
                // override on the local path).
                var folderOverride = ResolveFolderOverride(workItem.Path);
                var perJobOptions = EncoderOptionsOverride.ApplyOverrides(current, folderOverride, null);
                perJobOptions.HardwareAcceleration = deviceId == "cpu" ? "none" : deviceId;
                perJobOptions.HardwareDevicePath   = deviceId == "cpu" ? null : GetDevicePathForDeviceId(deviceId);

                // Force-mux items ("Process Item" / "Process Directory") dispatch as Hybrid even
                // when the global mode is Transcode, so an at-target file is mux-passed instead of
                // being dropped by the pre-dispatch skip gate below. The upgraded mode flows into
                // the encode (ConvertVideoAsync), giving a video-copy remux to the target container.
                if (workItem.ForceMux && perJobOptions.EncodingMode == EncodingMode.Transcode)
                    perJobOptions.EncodingMode = EncodingMode.Hybrid;

                // Pre-dispatch finalisation: resolve any missing OriginalLanguage live,
                // merge it into the per-job keep lists, and re-run the skip ladder under
                // the merged options. Keeps every "should this still encode?" decision in
                // the dispatcher — workers are only ever handed items that genuinely need
                // to encode. Catches three cases the queue couldn't pre-vet:
                //   • Legacy DB rows queued before the OriginalLanguage cache existed.
                //   • Settings toggled between AddFileAsync and dispatch (eg. user flipped
                //     KeepOriginalLanguage on after the file was already queued).
                //   • Force-adds that bypass parts of AddFileAsync's skip ladder.
                if (!await FinaliseForDispatchAsync(workItem, perJobOptions, CancellationToken.None))
                {
                    await MarkDispatchSkippedAsync(workItem, "pre-dispatch check — file already meets target under current options");
                    // Release the ledger reservation we made above for this item —
                    // the encode is being skipped, so the slot must go back to the pool.
                    _slotLedger?.Release(workItem.Id, ReleaseReason.NoSavings);
                    continue;
                }

                // Cancel/Stop race: a Pending → Cancelled transition could have landed during
                // FinaliseForDispatchAsync's awaits. Re-check the workItem.Status before
                // committing to dispatch — without this, ProcessWorkItemAsync's unconditional
                // Status = Processing assignment would clobber the user's cancel.
                if (workItem.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped)
                {
                    Console.WriteLine($"Dropping {workItem.FileName}: cancelled/stopped during dispatch finalisation");
                    UnregisterWorkItem(workItem.Id);
                    _slotLedger?.Release(workItem.Id, ReleaseReason.Cancelled);
                    try { await _hubContext.Clients.All.SendAsync("WorkItemRemoved", workItem.Id); } catch { /* SignalR errors are non-fatal */ }
                    continue;
                }

                // Pre-register the active job synchronously before spawning so
                // cancellation can find the running ffmpeg process (slot accounting
                // itself is now in the ledger; _activeLocalJobs is purely for
                // process-kill plumbing).
                var active = new ActiveLocalJob
                {
                    Item     = workItem,
                    Cts      = new CancellationTokenSource(),
                    DeviceId = deviceId,
                };
                _activeLocalJobs[workItem.Id] = active;
                workItem.DispatchedDeviceId = deviceId;

                // Wrap the local encode so the ledger is released exactly once
                // when it finishes, regardless of success / failure / cancel.
                // ProcessWorkItemAsync owns _activeLocalJobs cleanup in its
                // own finally — the ledger release is layered above.
                var capturedVideoItem   = workItem;
                var capturedVideoLedger = _slotLedger;
                var jobTask = Task.Run(async () =>
                {
                    try { await ProcessWorkItemAsync(capturedVideoItem, perJobOptions, active); }
                    finally
                    {
                        capturedVideoLedger?.Release(capturedVideoItem.Id, ReleaseReason.Completed);
                        WakeScheduler();
                    }
                });
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
    ///     Picks the first hwaccel-eligible device for <paramref name="workItem"/>
    ///     and atomically reserves a slot on it via the shared
    ///     <see cref="SlotLedger"/>. Returns the chosen device id on success,
    ///     <see langword="null"/> when every eligible device is disabled, full,
    ///     or codec-mismatched — in which case the caller is expected to
    ///     re-queue and let either the cluster dispatcher (workers) or a
    ///     future tick (master slot freed) try again.
    ///
    ///     <para>Capacity is owned by the ledger's resolver, which honours
    ///     the user's per-device <c>MaxConcurrency</c> overrides and falls
    ///     back to <see cref="HardwareDevice.DefaultConcurrency"/>; CPU is
    ///     hard-capped at 1 inside <c>EffectiveDeviceCapacity</c>.</para>
    ///
    ///     <para>CPU rules:
    ///     <list type="bullet">
    ///         <item><c>none</c> (Software): CPU is the only acceptable slot.</item>
    ///         <item><c>auto</c> on a machine where no hardware encoder supports the requested codec:
    ///               CPU is the auto-fallback. Mirrors <c>VideoJobRouter</c>'s
    ///               <c>nodeHasUsableHardware</c> predicate for the cluster path, so a node with
    ///               hardware that can't do the chosen codec (e.g. UHD 630 + AV1) falls back to
    ///               software instead of deadlocking the queue.</item>
    ///         <item>Anything else: CPU is excluded so jobs queue rather than spilling onto a slow software encode.</item>
    ///     </list></para>
    /// </summary>
    private string? TryReserveLocalDeviceSlot(WorkItem workItem, EncoderOptions options)
    {
        if (_slotLedger == null) return null;

        var devices = GetDetectedDevices();
        if (devices.Count == 0) return null;

        var hwPref = (options.HardwareAcceleration ?? "auto").ToLowerInvariant();
        bool hasHardwareThatCanEncode = devices.Any(d =>
            d.DeviceId != "cpu" && DeviceCanEncode(d.DeviceId, workItem, options));
        // For an explicit vendor pick, can *that* vendor's device actually encode this
        // codec? False when the vendor isn't detected (e.g. AMD on Linux that failed to
        // register) or is present but lacks the codec (e.g. AV1 on a pre-RDNA3 Radeon) —
        // either way CPU becomes an eligible software fallback below.
        bool selectedVendorCanEncode = devices.Any(d =>
            d.DeviceId != "cpu"
            && string.Equals(d.DeviceId, hwPref, StringComparison.OrdinalIgnoreCase)
            && DeviceCanEncode(d.DeviceId, workItem, options));

        foreach (var device in devices)
        {
            if (!IsDeviceEligibleUnderHwPref(device.DeviceId, hwPref, hasHardwareThatCanEncode, selectedVendorCanEncode)) continue;

            // Skip devices that can't encode this item's target codec —
            // the ledger only cares about capacity, not codec compatibility.
            if (!DeviceCanEncode(device.DeviceId, workItem, options)) continue;

            // Atomic capacity check + reserve. Returns false when the device
            // is at capacity (MaxConcurrency / DefaultConcurrency / cpu=1)
            // or disabled (resolver returns 0).
            if (_slotLedger.TryReserve(_localNodeId, device.DeviceId, workItem.Id, workItem.FileName))
            {
                // Surface a warning when a job lands on CPU because the selected (or
                // auto) hardware can't encode the codec — under "auto" no detected device
                // does the codec, under an explicit vendor that vendor can't (absent, or
                // present-but-incapable, e.g. AV1 on a pre-RDNA3 Radeon). Without this the
                // user just sees jobs run slowly (or, before the broader fix, not at all
                // under an explicit vendor) with no signal — the GitHub issue that
                // motivated the original auto-fallback called out "no errors reported to
                // help track down the cause", and that report came in via stdout only,
                // which Serilog never captured. Route it through the structured logger so
                // it lands in the log file the user actually reads, and emit a per-item
                // TranscodingLog line so the reason shows on that item in the UI.
                if (device.DeviceId == "cpu" && hwPref != "none")
                {
                    var codec = (options.Codec ?? "").ToLowerInvariant();
                    var fallbackEncoder = GetSoftwareFallbackEncoder(options);
                    var message = hwPref == "auto"
                        ? $"No hardware encoder for '{codec}' on any detected device — " +
                          $"using software fallback ({fallbackEncoder}). Encodes will be significantly slower."
                        : $"Hardware acceleration '{hwPref}' can't encode '{codec}' on this machine " +
                          $"(device not detected, or no hardware {codec} encoder) — using software fallback " +
                          $"({fallbackEncoder}). Encodes will be significantly slower.";

                    // Dedupe the global log line per (vendor, codec) so a long queue of
                    // identical fallbacks doesn't spam the log file every dispatch.
                    if (_loggedAutoCpuFallback.Add($"{hwPref}:{codec}"))
                    {
                        Console.WriteLine(message);
                        _log?.LogWarning("{Message}", message);
                    }
                    // Per-item line (not deduped) so each affected item explains itself.
                    _ = LogAsync(workItem.Id, message);
                }
                return device.DeviceId;
            }
        }
        return null;
    }

    /// <summary>
    ///     Pure hw-vs-CPU gating predicate, extracted from
    ///     <see cref="TryReserveLocalDeviceSlot"/> so it can be exercised by unit
    ///     tests without standing up a slot ledger or detected-device list. Encodes
    ///     the eligibility ladder used by both the local-master dispatcher and
    ///     (in spirit) <c>VideoJobRouter.ScoreSlot</c> for cluster routing.
    ///
    ///     <para><paramref name="selectedVendorCanEncode"/> only matters for an explicit
    ///     vendor preference: it is <see langword="true"/> when the chosen vendor's device
    ///     is present, enabled, and able to encode the requested codec. When it is
    ///     <see langword="false"/> — the vendor isn't detected at all (e.g. an AMD GPU on
    ///     Linux that didn't register), or is present but can't do the codec (e.g. AV1 on
    ///     a pre-RDNA3 Radeon) — CPU becomes an eligible software fallback so the queue
    ///     keeps moving instead of stalling forever. The caller surfaces a warning when
    ///     this happens. A vendor device that <em>can</em> do the codec but is merely busy
    ///     keeps <paramref name="selectedVendorCanEncode"/> <see langword="true"/>, so the
    ///     job still queues for the GPU rather than silently spilling onto a slow software
    ///     encode.</para>
    /// </summary>
    internal static bool IsDeviceEligibleUnderHwPref(
        string deviceId,
        string hwPref,
        bool hasHardwareThatCanEncode,
        bool selectedVendorCanEncode)
    {
        bool isCpu = deviceId == "cpu";
        bool specificVendor = hwPref != "none" && hwPref != "auto";

        if (hwPref == "none" && !isCpu) return false;                              // Software ⇒ CPU only
        if (specificVendor && isCpu) return !selectedVendorCanEncode;              // Specific vendor ⇒ CPU only as a fallback when that vendor can't serve the codec
        if (hwPref == "auto" && isCpu && hasHardwareThatCanEncode) return false;   // Auto with usable HW ⇒ never CPU
        if (specificVendor && !isCpu
            && !string.Equals(hwPref, deviceId, StringComparison.OrdinalIgnoreCase)) return false; // Specific vendor must match
        return true;
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
        return CanDeviceEncodeCodec(device, deviceId, options.Codec);
    }

    /// <summary>
    ///     Pure codec-vs-device match, extracted from <see cref="DeviceCanEncode"/>
    ///     so it can be unit-tested without standing up the detected-device list.
    ///     CPU (no detected device row) is treated as always-capable. Unknown codecs
    ///     return <see langword="true"/> so the value is handed to ffmpeg untouched
    ///     rather than synthesising a refusal.
    /// </summary>
    internal static bool CanDeviceEncodeCodec(HardwareDevice? device, string deviceId, string? codec)
    {
        if (device == null) return deviceId == "cpu";

        var codecLc = (codec ?? "").ToLowerInvariant();
        var key = codecLc switch
        {
            "hevc" or "h265" => "h265",
            "avc"  or "h264" => "h264",
            "av1"            => "av1",
            _                => codecLc,
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
        // Set when the per-job watchdog (not the user) cancels the CTS, so the
        // cancellation catch below can record a Failed outcome. Without this the
        // item stays in Processing forever in standalone mode — only the cluster
        // master's stuck-item watchdog would ever rescue it.
        bool watchdogFired = false;

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
                            watchdogFired = true;
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
                // Distinguish a kept encode from a discarded "no savings" outcome. The flag
                // is set by ConvertVideoAsync's no-keep branch; without this, a discarded
                // local encode would land in Completed and feed the same Re-evaluate
                // flip-back loop the cluster path used to.
                bool noSavings = workItem.LastEncodeProducedNoSavings;
                workItem.Status = noSavings ? WorkItemStatus.NoSavings : WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                Interlocked.Increment(ref _localCompletedJobs);
                await _mediaFileRepo.SetStatusAndLastEncodedAtAsync(
                    Path.GetFullPath(workItem.Path),
                    noSavings ? MediaFileStatus.NoSavings : MediaFileStatus.Completed,
                    DateTime.UtcNow);

                if (!noSavings && ShouldDispatchExternal)
                {
                    if (_notificationService != null)
                        _ = _notificationService.NotifyEncodeCompletedAsync(Path.GetFileName(workItem.Path), workItem.OutputSize);
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
            // Cancelled — clean up partial output. For a user cancel/stop the status was
            // already set by CancelWorkItemAsync/StopWorkItemAsync; for a watchdog abort
            // nobody set a terminal status, so record the failure here or the item is a
            // Processing zombie until restart.
            var outputPath = GetOutputPath(workItem, options);
            try { await _fileService.FileDeleteAsync(outputPath); } catch { }

            if (watchdogFired)
                await RecordWatchdogFailureAsync(workItem);
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
    ///     Music-only counterpart to <see cref="ProcessWorkItemAsync"/>. Drives the
    ///     status/hub/watchdog plumbing around <see cref="ConvertMusicAsync"/> and
    ///     records analytics on completion. The dedicated music slot semaphore is
    ///     released by the dispatcher's wrapping continuation, not this method.
    /// </summary>
    private async Task ProcessMusicWorkItemAsync(WorkItem workItem, EncoderOptions options, ActiveLocalJob active)
    {
        // See ProcessWorkItemAsync — distinguishes a watchdog abort (must be
        // recorded as Failed) from a user cancel (status already set).
        bool watchdogFired = false;

        try
        {
            workItem.Status    = WorkItemStatus.Processing;
            workItem.StartedAt = DateTime.UtcNow;
            workItem.Progress  = 0;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Processing);

            if (_notificationService != null && ShouldDispatchExternal)
                _ = _notificationService.NotifyEncodeStartedAsync(Path.GetFileName(workItem.Path));

            if (workItem.Probe == null)
            {
                workItem.Probe = await _ffprobeService.ProbeAsync(workItem.Path);
                if (workItem.Length <= 0 && workItem.Probe?.Format?.Duration != null)
                    workItem.Length = _ffprobeService.DurationStringToSeconds(workItem.Probe.Format.Duration);
            }

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
                            await LogAsync(workItem.Id, "Watchdog: no music progress for 15 min — aborting job");
                            watchdogFired = true;
                            active.Cts.Cancel();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });

            try
            {
                await ConvertMusicAsync(workItem, options, active.Cts.Token);
            }
            finally
            {
                watchdogCts.Cancel();
                try { await watchdogTask; } catch { }
            }

            if (workItem.Status != WorkItemStatus.Failed)
            {
                bool noSavings = workItem.LastEncodeProducedNoSavings;
                workItem.Status = noSavings ? WorkItemStatus.NoSavings : WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
                Interlocked.Increment(ref _localCompletedJobs);
                await _mediaFileRepo.SetStatusAndLastEncodedAtAsync(
                    Path.GetFullPath(workItem.Path),
                    noSavings ? MediaFileStatus.NoSavings : MediaFileStatus.Completed,
                    DateTime.UtcNow);

                if (!noSavings && _notificationService != null && ShouldDispatchExternal)
                    _ = _notificationService.NotifyEncodeCompletedAsync(Path.GetFileName(workItem.Path), workItem.OutputSize);

                _ = RecordLocalEncodeHistoryAsync(workItem, options, active.DeviceId);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled — clean up partial output if it exists.
            try
            {
                var outputPath = GetMusicOutputPath(workItem, options);
                await _fileService.FileDeleteAsync(outputPath);
            }
            catch { }

            if (watchdogFired)
                await RecordWatchdogFailureAsync(workItem);
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
    ///     Resolves the destination path for a music encode. Mirrors
    ///     <see cref="GetOutputPath"/> for video but uses the music-specific
    ///     extension and <see cref="MusicEncoderOptions.OutputDirectory"/>.
    /// </summary>
    private string GetMusicOutputPath(WorkItem workItem, EncoderOptions options)
    {
        var music = options.Music;
        var fileName = _fileService.RemoveExtension(workItem.FileName);
        var extension = "." + MusicEncoderArgs.ExtensionForFormat(music.Format);
        var snacksName = $"{fileName} [snacks]{extension}";

        if (!string.IsNullOrEmpty(music.OutputDirectory))
            return Path.Combine(music.OutputDirectory, snacksName);

        var originalDir = _fileService.GetDirectory(workItem.Path);
        return Path.Combine(originalDir, snacksName);
    }

    /// <summary>
    ///     Encodes a music file via ffmpeg. Significantly leaner than
    ///     <c>ConvertVideoAsync</c>: no HDR detection, no hardware acceleration,
    ///     no per-track audio profiles, no subtitle handling, no retry chain.
    ///     On output ≥ source the encode is classified as no-savings and the
    ///     output is discarded — same semantics as the video path.
    /// </summary>
    private async Task ConvertMusicAsync(WorkItem workItem, EncoderOptions options, CancellationToken cancellationToken)
    {
        if (workItem.Probe == null)
            workItem.Probe = await _ffprobeService.ProbeAsync(workItem.Path, cancellationToken);

        var outputPath = GetMusicOutputPath(workItem, options);
        var outputDir  = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Pre-clean any stale [snacks] output from a prior run; ffmpeg's -y also
        // overwrites, but an explicit delete avoids ambiguity if the prior file
        // was corrupt and ffmpeg's overwrite-in-place behavior on the user's NAS
        // is finicky.
        try { await _fileService.FileDeleteAsync(outputPath); } catch { }

        var command = MusicEncoderArgs.Build(workItem.Path, outputPath, options.Music, workItem.Probe);
        await LogAsync(workItem.Id, $"Music encode: ffmpeg {command}");
        await RunFfmpegAsync(command, workItem, cancellationToken);

        if (!File.Exists(outputPath))
            throw new Exception("Music encode failed: output file not produced");

        var outFileInfo = new FileInfo(outputPath);
        if (outFileInfo.Length == 0)
        {
            try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            throw new Exception("Music encode failed: output file is empty");
        }

        // Validate output duration vs source (within 1s tolerance) — catches
        // partial encodes where ffmpeg returned 0 but the file is truncated.
        try
        {
            var outProbe = await _ffprobeService.ProbeAsync(outputPath, cancellationToken);
            if (double.TryParse(outProbe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var outDur)
                && workItem.Length > 0
                && Math.Abs(outDur - workItem.Length) > 1.0)
            {
                try { await _fileService.FileDeleteAsync(outputPath); } catch { }
                throw new Exception($"Music encode failed: output duration {outDur:F1}s differs from source {workItem.Length:F1}s");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Probe failure on output — treat as encode failure.
            try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            throw;
        }

        workItem.OutputSize = outFileInfo.Length;

        // No-savings classification: discard output if encoded >= source unless
        // the user is doing a lossy → lossless intentional re-encode. A lossless
        // FLAC output of an MP3 will always be larger; that's by design and
        // shouldn't count as no-savings — but the AddMusicFileAsync skip ladder
        // already prevents that case from running, so here we treat "encoded
        // >= source" as discardable across the board.
        if (outFileInfo.Length >= workItem.Size)
        {
            workItem.LastEncodeProducedNoSavings = true;
            try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            await LogAsync(workItem.Id, $"Music encode produced no savings: {outFileInfo.Length} ≥ {workItem.Size} bytes — discarded");
            return;
        }

        // Optional: delete original after successful encode.
        if (options.Music.DeleteOriginalFile)
        {
            try
            {
                await _fileService.FileDeleteAsync(workItem.Path);
                await LogAsync(workItem.Id, $"Deleted original: {workItem.FileName}");
            }
            catch (Exception ex)
            {
                await LogAsync(workItem.Id, $"Failed to delete original: {ex.Message}");
            }
        }
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
    /// <param name="forceSwDecode">When <c>true</c>, disables hardware decoding while keeping HW encoding. VAAPI: drops <c>-hwaccel vaapi</c> and routes SW frames in via hwupload. NVIDIA: drops the explicit <c>-c:v src_cuvid</c> decoder so the file falls back to <c>-hwaccel cuda</c>'s auto-attach (which can in turn fall back to a SW decoder).</param>
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
        bool skipPlacement = false,
        CancellationToken cancellationToken = default)
    {
        if (workItem.Probe == null)
            throw new Exception("No probe data available");

        // WebM only allows AV1/VP8/VP9 video and Opus/Vorbis audio. If the user picked
        // a non-AV1 codec alongside Format=webm, coerce the per-job clone to AV1 here so
        // the rest of the pipeline (encoder resolution, MapAudio, BuildFfmpegCommand) sees
        // a self-consistent options block. Audio is coerced inside MapAudio via
        // ResolveAudioCodec. The user's settings.json is not modified — only this job's
        // options object, which is already a per-job clone.
        await CoerceForWebmAsync(workItem, options);

        // Resolve "auto" to a concrete hardware type before building the command
        await ResolveHardwareAccelerationAsync(options);
        await LogAsync(workItem.Id, $"Hardware acceleration: {options.HardwareAcceleration}");
        await LogAsync(workItem.Id, $"Video Bitrate: {workItem.Bitrate}kbps");

        var (targetBitrate, minBitrate, maxBitrate, videoCopy) = CalculateBitrates(workItem, options);

        // Mux pass: copy the video stream and only touch the stream types selected by
        // MuxStreams. Requires actual muxable work — otherwise re-encode normally (or, for
        // at-target files, the skip gate would have already skipped). EncodingMode decides
        // when a mux pass is chosen over a full re-encode:
        //   Hybrid  — mux pass only for files at the bitrate target; above-target re-encodes.
        //   MuxOnly — mux pass for every file with muxable work (files without work were
        //             already force-skipped upstream; video is never re-encoded).
        bool isMuxPass = options.EncodingMode != EncodingMode.Transcode &&
            (HasMuxableWork(options, workItem.Probe!)
                || (workItem.ForceMux && NeedsContainerChange(options, workItem.Path))) &&
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
            string testHwFlags = GetInitFlags(hwAccel, options.HardwareDevicePath);
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
                compressionFlags = $"-g 25 -rc_mode CQP -global_quality:v {quality} ";
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
        string? scaleExpr = isMuxPass ? null
            : ComputeFixedFrameFilter(options) ?? ComputeScaleExpr(workItem, options);
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
                string testHwFlags = GetInitFlags(hwAccel, options.HardwareDevicePath);
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

        string initFlags = useVaapi
            ? GetInitFlags(hwAccel, options.HardwareDevicePath, canHwDecode)
            : GetInitFlags(hwAccel, options.HardwareDevicePath);

        // Explicitly select the NVDEC (cuvid) decoder on the nvidia path. `-hwaccel cuda` alone
        // is just a hint — on some driver/setup combinations (datacenter Windows drivers, vGPU
        // profiles, etc.) ffmpeg silently falls back to the software decoder while NVENC keeps
        // working, which pegs the CPU. Forcing the cuvid decoder makes NVDEC engagement
        // deterministic. Skip on mux pass (no decode), software fallback, or when forceSwDecode
        // disabled HW decode for retry. If the source codec has no cuvid mapping or NVDEC
        // refuses the profile, the retry path drops the explicit decoder.
        bool isNvidia = hwAccel.Equals("nvidia", StringComparison.OrdinalIgnoreCase);
        if (isNvidia && !videoCopy && !forceSwDecode && !encoder.StartsWith("lib"))
        {
            var srcCodec = workItem.Probe?.Streams?.FirstOrDefault(s => s.CodecType == "video")?.CodecName;
            string cuvid = GetNvidiaInputDecoder(srcCodec);
            if (!string.IsNullOrEmpty(cuvid))
                initFlags = $"{initFlags} {cuvid}";
        }
        // After tonemap the frame is 8-bit SDR; p010 would waste bandwidth on the hwupload.
        string vaapiFormat = (is10Bit && !tonemap) ? "p010" : "nv12";
        string vfFlag = VideoFilterBuilder.Emit(
            cropExpr: cropExpr, tonemap: tonemap, scaleExpr: scaleExpr,
            useVaapi: useVaapi, canHwDecode: canHwDecode, vaapiFormat: vaapiFormat);
        bool isSvtAv1 = encoder == "libsvtav1";
        bool isAmf    = encoder.Contains("amf");
        string presetFlag = useVaapi
            ? (useLowPower ? "-low_power 1 " : "")
            : isSvtAv1 ? $"-preset {MapSvtAv1Preset(options.FfmpegQualityPreset)} "
            : isAmf    ? $"-quality {MapAmfPreset(options.FfmpegQualityPreset)} "
                        : $"-preset {options.FfmpegQualityPreset} ";
        // H.264/H.265 profile + level — only for software encoders (libx264/libx265).
        // Hardware encoders (NVENC/VAAPI/QSV/AMF) accept different profile value sets,
        // so we gate to the lib* path to avoid passing incompatible values.
        string profileLevel = "";
        if (!videoCopy && encoder.StartsWith("lib"))
        {
            if (!string.IsNullOrWhiteSpace(options.VideoProfile))
                profileLevel += $"-profile:v {options.VideoProfile} ";
            if (!string.IsNullOrWhiteSpace(options.VideoLevel))
                profileLevel += $"-level {options.VideoLevel} ";
        }
        string videoFlags = videoCopy ?
            $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v copy " :
            $"{_ffprobeService.MapVideo(workItem.Probe!)} -c:v {encoder} {presetFlag}{profileLevel}{vfFlag}";

        // On a mux pass that excludes audio (MuxStreams.Subtitles), keep every audio track as-is:
        // empty language list = keep all, preserve-only profile = no re-encode.
        string audioFlags = _ffprobeService.MapAudio(
            workItem.Probe!,
            doAudioWork ? options.AudioLanguagesToKeep : new List<string>(),
            doAudioWork ? options.PreserveOriginalAudio : true,
            doAudioWork ? options.AudioOutputs : null,
            options.Format,
            out var audioWarnings,
            autoSetDefault: doAudioWork && options.AutoSetDefaultTrack) + " ";

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
                options.ExcludeSdhSubtitles,
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
                    options.ExcludeSdhSubtitles,
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
            string ocrCodec = FfprobeService.IsMatroska(options.Format) ? "srt" : "mov_text";

            bool passBitmaps = FfprobeService.IsMatroska(options.Format)
                            && options.PassThroughImageSubtitlesMkv
                            && !dropImageSubtitlesOnly;

            var sourceSubs = _ffprobeService
                .SelectSidecarStreams(
                    workItem.Probe!,
                    options.SubtitleLanguagesToKeep,
                    includeBitmaps: passBitmaps,
                    excludeSdh:     options.ExcludeSdhSubtitles)
                .ToList();

            // Reorder source subs by user preference so the default flag below lands on
            // the top-priority language. OCR'd SRTs follow in their original order — they
            // already came back from OcrBitmapsForMuxAsync in source-stream order, and the
            // user-facing priority comes from the source subs first.
            if (options.SubtitleLanguagesToKeep is { Count: > 0 } subPref)
            {
                sourceSubs = sourceSubs
                    .Select((s, srcIdx) => new { s, srcIdx, prefIdx = SidecarPreferenceIndex(s.Lang, subPref) })
                    .OrderBy(x => x.prefIdx)
                    .ThenBy(x => x.srcIdx)
                    .Select(x => x.s)
                    .ToList();
            }

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

            // Default-flag the first kept sub when the user opts in. -disposition resets
            // all flags on the targeted stream, so this also clears any stale default=1
            // carried through from the source via -c:s copy.
            string disposition = "";
            if (doSubtitleWork && options.AutoSetDefaultTrack && outSubIndex > 0)
            {
                disposition = "-disposition:s:0 default ";
                for (int i = 1; i < outSubIndex; i++) disposition += $"-disposition:s:{i} 0 ";
            }

            subtitleFlags = maps + codecs + meta + disposition;
        }
        else
        {
            // On a mux pass that excludes subs (MuxStreams.Audio), keep every subtitle track by
            // passing an empty language list — MapSub treats that as "keep all".
            var subLangs = doSubtitleWork ? options.SubtitleLanguagesToKeep : new List<string>();
            bool passBitmaps = FfprobeService.IsMatroska(options.Format)
                            && options.PassThroughImageSubtitlesMkv
                            && !dropImageSubtitlesOnly;
            subtitleFlags = _ffprobeService.MapSub(
                workItem.Probe!,
                subLangs,
                options.Format,
                passBitmaps,
                excludeSdh:     doSubtitleWork && options.ExcludeSdhSubtitles,
                autoSetDefault: doSubtitleWork && options.AutoSetDefaultTrack) + " ";
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
            await HandleConversionFailure(workItem, options, outputPath, ex.Message, stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, dropImageSubtitlesOnly, skipPlacement, cancellationToken: cancellationToken);
            return;
        }

        await Task.Delay(5000); // Wait for the filesystem to finish flushing the output before probing it.

        if (!File.Exists(outputPath))
        {
            await HandleConversionFailure(workItem, options, outputPath, "Output file not found", stripSubtitles, forceSwDecode, useConservativeHwFlags, ocrMuxSrts, ocrMuxTmpDir, dropImageSubtitlesOnly, skipPlacement, cancellationToken: cancellationToken);
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

        var (keep, reason) = ShouldKeepEncodedOutput(options, workItem, workItem.Size, outputSize, videoCopy);

        // Stash the actual videoCopy outcome so the cluster completion path can forward it
        // to the master verbatim — without this, the master's recompute can only see the
        // mux-pass branch and would miss the HEVC-at-target-bitrate copy that
        // CalculateBitrates enables independently.
        workItem.OutputUsedVideoCopy = keep ? videoCopy : null;

        if (keep)
        {
            // Show both sizes explicitly so users don't misread the savings number
            // as the output size.
            if (reason == "savings")
            {
                await LogAsync(workItem.Id,
                    $"Size: {FormatSize(workItem.Size)} → {FormatSize(outputSize)}  (saved {FormatSize((long)(savings * 1048576))}, {percent:P0})");
            }
            else
            {
                long delta = outputSize - workItem.Size;
                await LogAsync(workItem.Id,
                    $"Size: {FormatSize(workItem.Size)} → {FormatSize(outputSize)}  (+{FormatSize(delta)}, kept due to {reason})");
            }

            // Workers must not place — the master is the side that has the user's library
            // mounted and runs HandleOutputPlacement after download. If a worker ran the
            // in-place DeleteOriginalFile branch it would strip the [snacks] tag in its temp
            // dir; the post-encode `*[snacks]*` glob in ClusterNodeJobService would then
            // return null and falsely report noSavings, vanishing the job from the queue.
            if (!skipPlacement)
                await HandleOutputPlacement(outputPath, workItem, options);
        }
        else
        {
            await LogAsync(workItem.Id,
                "No savings realized. Deleting conversion.");

            // Signals the local-completion path (ProcessQueueAsync) to write NoSavings rather
            // than Completed. The cluster path doesn't read this — workers report noSavings
            // directly via the JobCompletion payload — but local encodes flow back through
            // the same caller that needs to distinguish a kept encode from a discarded one.
            workItem.LastEncodeProducedNoSavings = true;

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

            // Preference order changed? Re-mux so the top-priority language ends up
            // on track 0. Without this, a MuxOnly user toggling the order would see
            // nothing happen on already-fine files.
            if (WouldReorder(audLangs, kept.Select(s => s.Language))) return true;
        }

        // Commentary tracks are unconditionally dropped by the planner (see
        // FfprobeService.IsCommentaryTitle). If any survived the language filter,
        // running the planner would change the output — that's audio work.
        foreach (var s in kept)
            if (FfprobeService.IsCommentaryTitle(s.Title)) return true;

        // Preserve=off + AudioOutputs profiles: the planner emits encoded (or codec-deduped
        // copy) outputs for the matched sources and DROPS every other source — that's work
        // either way.
        if (!options.PreserveOriginalAudio
            && options.AudioOutputs is { Count: > 0 })
        {
            return true;
        }

        // Preserve=off + empty AudioOutputs: the empty-language safeguard in
        // FfprobeService.MapAudio copies the highest-channel kept track per language.
        // That's a no-op when each language bucket has a single track, but it drops
        // sibling tracks when a language has multiple (eg. 5.1 + stereo English) — that
        // is real work. Without this distinction, the common single-track-per-language
        // case would burn an encode that ConvertVideoAsync's savings check would just
        // discard, while the multi-track case would slip through the no-op gate.
        if (!options.PreserveOriginalAudio)
        {
            foreach (var bucket in kept.GroupBy(s => (s.Language ?? "und").ToLowerInvariant()))
            {
                if (bucket.Count() > 1) return true;
            }
            return false;
        }

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
    ///     (language drop, sidecar extraction, OCR of bitmap subs, or the default bitmap
    ///     drop that fires when <c>PassThroughImageSubtitlesMkv</c> is off and the format
    ///     is Matroska) versus a straight copy.
    /// </summary>
    internal static bool HasSubtitleWork(EncoderOptions options, IReadOnlyList<SubtitleStreamSummary> subStreams)
    {
        if (subStreams.Count == 0) return false;

        // SDH drop would remove at least one track?
        if (options.ExcludeSdhSubtitles && subStreams.Any(s => s.Sdh)) return true;

        // Language filter would drop at least one track?
        if (options.SubtitleLanguagesToKeep is { Count: > 0 } subLangs)
        {
            var kept = subStreams
                .Where(s => LanguageMatcher.Matches(s.Language, s.Title, subLangs))
                .ToList();
            if (kept.Count < subStreams.Count) return true;
            subStreams = kept;

            // Reordering would change the output stream order — that's work even when
            // no streams are added or removed.
            if (WouldReorder(subLangs, subStreams.Select(s => s.Language))) return true;
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

        // Bitmap-drop. MapSub silently strips PGS / VobSub / DVB streams whenever
        // PassThroughImageSubtitlesMkv is off — including from the no-op gate path. Without
        // counting that as work, a kept-language bitmap-only file would be reported as
        // "Mux pass would copy audio/subs" or "no work to do" when in reality those subs
        // would disappear from the output. MP4 always strips bitmap subs (MapSub returns
        // -sn for non-Matroska anyway), so the format check is "are we keeping the MKV
        // copy path AND has the user opted out of pass-through?"
        bool isMatroskaOutput = FfprobeService.IsMatroska(options.Format);
        if (isMatroskaOutput && !options.PassThroughImageSubtitlesMkv)
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
                Sdh       = FfprobeService.IsHearingImpaired(s),
            })
            .ToList();

    /// <summary>
    ///     Returns <see langword="true"/> when the kept-language order in
    ///     <paramref name="streamLangs"/> would change after sorting by
    ///     <paramref name="keepList"/>. Used by <see cref="HasAudioWork"/> /
    ///     <see cref="HasSubtitleWork"/> so changing the priority order in settings
    ///     triggers a re-mux on files that already contain all the kept languages.
    ///     A null/empty keep-list means "no preference" — never re-orders.
    /// </summary>
    internal static bool WouldReorder(IReadOnlyList<string>? keepList, IEnumerable<string?> streamLangs)
    {
        if (keepList == null || keepList.Count == 0) return false;

        // Canonicalize the user's keep-list once.
        var preference = keepList
            .Select(l => LanguageMatcher.ToTwoLetter(l) ?? l?.Trim().ToLowerInvariant() ?? "")
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        // Build the source-order sequence of kept languages, deduped (only the first
        // occurrence per language matters for ordering — equal-language tracks stay
        // grouped because their preference index is identical).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceOrder = new List<string>();
        foreach (var raw in streamLangs)
        {
            var two = LanguageMatcher.ToTwoLetter(raw) ?? raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(two)) continue;
            if (!preference.Contains(two, StringComparer.OrdinalIgnoreCase)) continue;
            if (seen.Add(two)) sourceOrder.Add(two);
        }

        if (sourceOrder.Count <= 1) return false;

        // Target order: preference list filtered down to languages actually present.
        var targetOrder = preference
            .Where(p => sourceOrder.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (sourceOrder.Count != targetOrder.Count) return false;
        for (int i = 0; i < sourceOrder.Count; i++)
            if (!string.Equals(sourceOrder[i], targetOrder[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    ///     Index of <paramref name="lang"/> within <paramref name="preferences"/>, or
    ///     <see cref="int.MaxValue"/> when it doesn't match any preference. Used to sort
    ///     <see cref="FfprobeService.SidecarSpec"/> lists in the OCR-mux path.
    /// </summary>
    private static int SidecarPreferenceIndex(string lang, IReadOnlyList<string> preferences)
    {
        var two = LanguageMatcher.ToTwoLetter(lang) ?? lang;
        for (int i = 0; i < preferences.Count; i++)
        {
            var wantedTwo = LanguageMatcher.ToTwoLetter(preferences[i]) ?? preferences[i];
            if (string.Equals(wantedTwo, two, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return int.MaxValue;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when <see cref="EncoderOptions.MuxStreams"/> selects a
    ///     non-video branch that has work to do on this file. Drives the skip-gate bypass so
    ///     at-target files still get a video-copy mux pass when their audio or subs need attention.
    ///     Always <see langword="false"/> in <see cref="EncodingMode.Transcode"/>.
    /// </summary>
    private static bool HasMuxableWork(EncoderOptions options, ProbeResult probe) =>
        HasMuxableWork(options, ProjectAudioSummaries(probe), ProjectSubtitleSummaries(probe));

    /// <summary>
    ///     Returns <see langword="true"/> when the source file's container differs from the
    ///     configured output <see cref="EncoderOptions.Format"/> — i.e. a remux would change
    ///     the container even if no stream re-encode is needed. Only consulted on the force-mux
    ///     ("Process Item" / "Process Directory") path, where the user explicitly asked the file
    ///     to be normalized to current settings. <c>mp4</c> and <c>m4v</c> are treated as the
    ///     same container so an existing <c>.m4v</c> isn't needlessly rewritten under Format=mp4.
    /// </summary>
    internal static bool NeedsContainerChange(EncoderOptions options, string sourcePath)
    {
        var ext    = System.IO.Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        var target = (options.Format ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || string.IsNullOrEmpty(target)) return false;
        if (target == "mp4" && ext is "mp4" or "m4v") return false;
        return ext != target;
    }

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
    ///     Returns a clone of <paramref name="options"/> with <paramref name="originalLanguage"/>
    ///     merged into the audio + subtitle keep lists when <c>KeepOriginalLanguage</c> is on.
    ///     Mirrors the merge that <see cref="ConvertVideoAsync"/> performs at encode time so
    ///     the scan-phase skip predicates and the analyze dry-run see the same effective lists.
    ///     Returns <paramref name="options"/> unchanged when the merge is a no-op.
    /// </summary>
    /// <param name="options"> Base options to merge into. Not mutated. </param>
    /// <param name="originalLanguage"> ISO 639-1 code, or <see langword="null"/> when unknown. </param>
    internal static EncoderOptions WithOriginalLanguageMerged(EncoderOptions options, string? originalLanguage)
    {
        if (!options.KeepOriginalLanguage) return options;
        if (string.IsNullOrWhiteSpace(originalLanguage)) return options;

        bool inAudio = options.AudioLanguagesToKeep.Contains(originalLanguage);
        bool inSubs  = options.SubtitleLanguagesToKeep.Contains(originalLanguage);
        if (inAudio && inSubs) return options;

        var merged = options.Clone();
        if (!inAudio) merged.AudioLanguagesToKeep.Add(originalLanguage);
        if (!inSubs)  merged.SubtitleLanguagesToKeep.Add(originalLanguage);
        return merged;
    }

    /// <summary>
    ///     Resolves the original language for a file, preferring the cached value on
    ///     <paramref name="dbFile"/> over a live integration-provider lookup. Returns
    ///     <see langword="null"/> when <c>KeepOriginalLanguage</c> is off, no provider is
    ///     wired, or the lookup fails — callers fall back to the user-configured keep
    ///     lists in that case (matches <see cref="ConvertVideoAsync"/>'s historical behavior).
    /// </summary>
    private async Task<string?> ResolveOriginalLanguageAsync(
        string filePath,
        EncoderOptions options,
        MediaFile? dbFile,
        CancellationToken cancellationToken)
    {
        if (!options.KeepOriginalLanguage) return null;
        if (!string.IsNullOrWhiteSpace(dbFile?.OriginalLanguage)) return dbFile!.OriginalLanguage;
        if (_integrationService == null) return null;

        try
        {
            return await _integrationService.LookupOriginalLanguageAsync(
                filePath, options.OriginalLanguageProvider, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    ///     Builds a transient <see cref="MediaFile"/> view over an in-flight <see cref="WorkItem"/>
    ///     so the late skip-bail in <see cref="ProcessWorkItemAsync"/> can run the full
    ///     <see cref="WouldSkipUnderOptions"/> ladder without touching the DB. Stream summaries
    ///     are projected from the work item's probe; <paramref name="originalLanguage"/> is
    ///     plugged in so the inner merge inside <see cref="WouldSkipUnderOptions"/> sees the
    ///     same value <see cref="ApplyOriginalLanguageMergeAsync"/> already merged into options.
    /// </summary>
    private static MediaFile SyntheticMediaFile(WorkItem workItem, string? originalLanguage)
    {
        var videoStream = workItem.Probe?.Streams.FirstOrDefault(s => s.CodecType == "video");
        return new MediaFile
        {
            FilePath        = workItem.Path,
            // Kind must propagate from the work item — without it the synthetic
            // row defaults to MediaKind.Video and the music early-return at the
            // top of WouldSkipUnderOptions never fires, causing a music dispatch
            // through the synthetic-fallback path (e.g. force-add or path-mismatch
            // DB lookup) to be evaluated against the video skip ladder and
            // silently marked Skipped.
            Kind            = workItem.Kind,
            Bitrate         = workItem.Bitrate,
            Codec           = videoStream?.CodecName ?? (workItem.IsHevc ? "hevc" : "unknown"),
            Width           = videoStream?.Width  ?? 0,
            Height          = videoStream?.Height ?? 0,
            IsHevc          = workItem.IsHevc,
            IsHdr           = workItem.Probe != null && FfprobeService.IsHdr(workItem.Probe),
            Is4K            = workItem.Is4K,
            AudioStreams    = workItem.Probe != null
                                ? MuxStreamSummary.Serialize(ProjectAudioSummaries(workItem.Probe))
                                : null,
            SubtitleStreams = workItem.Probe != null
                                ? MuxStreamSummary.Serialize(ProjectSubtitleSummaries(workItem.Probe))
                                : null,
            OriginalLanguage = originalLanguage,
        };
    }

    /// <summary>
    ///     Bookkeeping companion to <see cref="FinaliseForDispatchAsync"/>. When the
    ///     dispatch gate decides a queued item shouldn't encode, the caller pairs the
    ///     `false` return with this method: persist Skipped to the DB, drop the item
    ///     from the in-memory work-item registry, mark the in-flight WorkItem terminal,
    ///     and notify the UI. Used by both local (<see cref="ProcessQueueAsync"/>) and
    ///     cluster dispatch paths so the cleanup is identical.
    /// </summary>
    public async Task MarkDispatchSkippedAsync(WorkItem workItem, string reason)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        // Logged structurally so the ops log captures the silent-skip channel — items
        // dropped here vanish from the active queue without a Failed/Completed UI signal,
        // so we want every occurrence in the persistent log.
        Console.WriteLine($"Skipping {workItem.FileName}: {reason}");
        _log?.LogInformation(
            "DispatchSkipped jobId={JobId} fileName={FileName} reason={Reason}",
            workItem.Id, workItem.FileName, reason);
        try
        {
            await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Skipped);
        }
        catch { /* DB blip — next scan re-evaluates */ }
        UnregisterWorkItem(workItem.Id);
        workItem.Status = WorkItemStatus.Cancelled;
        try { await _hubContext.Clients.All.SendAsync("WorkItemRemoved", workItem.Id); }
        catch { /* SignalR errors are non-fatal */ }
    }

    /// <summary>
    ///     The dispatcher's last gate before a <see cref="WorkItem"/> goes to a worker:
    ///     resolves and persists any missing <see cref="MediaFile.OriginalLanguage"/>,
    ///     merges it into <paramref name="perJobOptions"/>'s keep lists, and re-runs the
    ///     full <see cref="WouldSkipUnderOptions"/> ladder under those merged options.
    ///
    ///     Returns <see langword="true"/> when the file should still encode; the caller
    ///     dispatches it. Returns <see langword="false"/> when the merged state reveals
    ///     there's no real work — the caller pairs that with
    ///     <see cref="MarkDispatchSkippedAsync"/> to mark the row Skipped and drop it
    ///     from the queue without ever invoking the worker. This keeps every
    ///     "should this encode?" decision in the dispatcher and lets the worker path
    ///     trust the items it receives.
    ///
    ///     <paramref name="perJobOptions"/> must already be a per-job clone — its keep
    ///     lists are mutated in place. Both the local <see cref="ProcessQueueAsync"/>
    ///     and the cluster <c>DispatchPendingItemsAsync</c> path call this so the two
    ///     dispatch paths agree on the skip decision.
    /// </summary>
    public async Task<bool> FinaliseForDispatchAsync(
        WorkItem workItem, EncoderOptions perJobOptions, CancellationToken cancellationToken)
    {
        var normalizedPath = Path.GetFullPath(workItem.Path);
        var dbFile = await _mediaFileRepo.GetByPathAsync(normalizedPath);
        string? originalLanguage = await ResolveOriginalLanguageAsync(
            workItem.Path, perJobOptions, dbFile, cancellationToken);

        // Cache newly resolved values so re-evaluations and re-scans are cache hits.
        if (!string.IsNullOrEmpty(originalLanguage)
            && dbFile != null
            && !string.Equals(dbFile.OriginalLanguage, originalLanguage, StringComparison.OrdinalIgnoreCase))
        {
            dbFile.OriginalLanguage = originalLanguage;
            try { await _mediaFileRepo.UpsertAsync(dbFile); }
            catch { /* a DB blip here doesn't change the encode decision; next scan persists it */ }
        }

        // Merge into perJobOptions so HasMuxableWork at encode time + the audio/sub
        // planner inside ConvertVideoAsync both see the same effective keep lists.
        if (!string.IsNullOrEmpty(originalLanguage) && perJobOptions.KeepOriginalLanguage)
        {
            if (!perJobOptions.AudioLanguagesToKeep.Contains(originalLanguage))
                perJobOptions.AudioLanguagesToKeep.Add(originalLanguage);
            if (!perJobOptions.SubtitleLanguagesToKeep.Contains(originalLanguage))
                perJobOptions.SubtitleLanguagesToKeep.Add(originalLanguage);
        }

        // Final skip check under the merged options. Prefer the persisted DB
        // row when available — items restored from DB on startup are queued
        // lazily with no probe, so a synthetic built from workItem alone
        // would have null AudioStreams / SubtitleStreams. HasMuxableWork
        // would then see no work, the ladder would fall through to the
        // bitrate gate, and an at-target HEVC file with a non-English audio
        // track that needs muxing would be silently marked Skipped here —
        // diverging from Re-evaluate, which examines the same DB row and
        // correctly says "still needs to encode". Use the DB row directly
        // so both paths agree. Fall back to synthetic only when there's
        // no DB row at all (force-add path).
        MediaFile skipMf;
        if (dbFile != null)
        {
            skipMf = dbFile;
            if (!string.IsNullOrEmpty(originalLanguage))
                skipMf.OriginalLanguage = originalLanguage;
        }
        else
        {
            skipMf = SyntheticMediaFile(workItem, originalLanguage);
        }
        return !WouldSkipUnderOptions(skipMf, perJobOptions, workItem.ForceMux);
    }

    /// <summary>
    ///     Pure function that mirrors the scan-phase skip gate using only <see cref="MediaFile"/>
    ///     fields (no probe required). Returns <see langword="true"/> when the file would still be
    ///     skipped under the given options — used on settings-save to decide whether to reset
    ///     <see cref="MediaFileStatus.Skipped"/> rows back to <see cref="MediaFileStatus.Unseen"/>.
    ///     Reads <see cref="MediaFile.OriginalLanguage"/> so files that would have their original
    ///     language preserved at encode time are evaluated against the same effective keep lists
    ///     instead of predicting drops that won't actually happen.
    /// </summary>
    public static bool WouldSkipUnderOptions(MediaFile mf, EncoderOptions options, bool forceMux = false)
    {
        // Music has its own skip ladder at enqueue time (AddMusicFileAsync) keyed
        // off the audio codec / bitrate against MusicEncoderOptions. Running it
        // through the video skip gate is meaningless — and dangerous: a non-HEVC
        // video encoder (libx264, h264_*) makes the codec-match branch evaluate
        // !mf.IsHevc as "already at target", and a typical music bitrate
        // (192 kbps) sits well under any video TargetBitrate, which then trips
        // the bitrate ceiling and silently marks the dispatch as "already meets
        // target." Music that's reached the dispatch path is by definition past
        // its own skip checks and should always proceed.
        if (mf.Kind == MediaKind.Music) return false;

        options = WithOriginalLanguageMerged(options, mf.OriginalLanguage);

        // Skip4K overrides everything else.
        if (options.Skip4K && mf.Is4K) return true;

        // If the user's current Mux settings would turn this file into a mux pass, it's
        // no longer a skip candidate. We need the per-track summaries to answer that.
        bool muxable = HasMuxableWork(
            options,
            MuxStreamSummary.DeserializeAudio(mf.AudioStreams),
            MuxStreamSummary.DeserializeSubtitle(mf.SubtitleStreams));
        if (muxable) return false;

        // Force-mux items ("Process Item" / "Process Directory") additionally treat a container
        // change (source extension != configured output Format) as work, so an at-target file
        // with matching streams but the wrong container is still remuxed rather than skipped.
        if (forceMux && NeedsContainerChange(options, mf.FilePath)) return false;

        // MuxOnly guarantees video is never re-encoded, so a file with no muxable work
        // is skipped regardless of its bitrate / codec.
        if (options.EncodingMode == EncodingMode.MuxOnly) return true;

        // Codec match — mirrors the scan-phase `alreadyTargetCodec` computation.
        bool targetIsHevc = options.Encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
        bool targetIsAv1  = options.Encoder.Contains("av1", StringComparison.OrdinalIgnoreCase) || options.Encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
        bool sourceIsAv1  = string.Equals(mf.Codec, "av1", StringComparison.OrdinalIgnoreCase);
        bool sourceIsH264 = string.Equals(mf.Codec, "h264", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(mf.Codec, "avc", StringComparison.OrdinalIgnoreCase);
        // Source already at least as efficient as the target → no downgrade re-encode.
        bool alreadyTargetCodec = SourceCodecMeetsTarget(sourceIsAv1, mf.IsHevc, sourceIsH264, targetIsHevc, targetIsAv1);
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
        // now carries cached IsHdr (set by AddFileAsync during the live probe), so the tonemap
        // arm of HasActiveFilter is evaluated correctly even on the predicate path that has no
        // probe in hand.
        if (mf.IsHevc && mf.Bitrate < options.TargetBitrate + 700)
        {
            var audioStreams = MuxStreamSummary.DeserializeAudio(mf.AudioStreams);
            var subStreams   = MuxStreamSummary.DeserializeSubtitle(mf.SubtitleStreams);
            if (!HasActiveFilter(options, mf.Height, mf.IsHdr)
                && !HasAudioWork(options, audioStreams)
                && !HasSubtitleWork(options, subStreams))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Decides whether an encoded output should be kept (placed at the destination) or
    ///     discarded as a no-savings encode. Shared by the local <see cref="ConvertVideoAsync"/>
    ///     and the cluster <see cref="HandleRemoteCompletion"/> paths so they don't diverge.
    ///
    ///     <para>An output is kept when any of: (a) it's smaller than the source, (b) it was
    ///     a mux pass (video stream copied — output is the user's intended remux even if size
    ///     is similar), or (c) the user configured audio fan-out (opt-in growth).</para>
    ///
    ///     <paramref name="videoCopyHint"/> is set by the local path which knows directly
    ///     whether ffmpeg was invoked with <c>-c:v copy</c>; the cluster path passes
    ///     <see langword="null"/> and the helper recomputes mux-pass eligibility from options
    ///     plus the work item's probe.
    /// </summary>
    internal static (bool Keep, string Reason) ShouldKeepEncodedOutput(
        EncoderOptions options, WorkItem workItem, long sourceSize, long outputSize, bool? videoCopyHint = null)
    {
        if (outputSize < sourceSize) return (true, "savings");

        bool userConfiguredGrowth = options.AudioOutputs is { Count: > 0 };
        if (userConfiguredGrowth) return (true, "configured audio outputs");

        bool videoCopy = videoCopyHint ?? IsMuxPass(options, workItem);
        if (videoCopy) return (true, "remux");

        return (false, "no savings");
    }

    /// <summary>
    ///     Companion to <see cref="ShouldKeepEncodedOutput"/>. Mirrors the local
    ///     <c>isMuxPass</c> computation in <see cref="ConvertVideoAsync"/> so the cluster
    ///     completion path can decide keep-vs-delete without the worker's <c>videoCopy</c>
    ///     flag in hand.
    /// </summary>
    internal static bool IsMuxPass(EncoderOptions options, WorkItem workItem) =>
        options.EncodingMode != EncodingMode.Transcode
        && workItem.Probe != null
        && (HasMuxableWork(options, workItem.Probe)
            || (workItem.ForceMux && NeedsContainerChange(options, workItem.Path)))
        && (options.EncodingMode == EncodingMode.MuxOnly || MeetsBitrateTarget(workItem, options));

    /// <summary>
    ///     Relative space-efficiency of a video codec at equal quality: AV1 (3) beats HEVC
    ///     (2) beats H.264 (1); everything older — MPEG-2, VC-1, VP9, XviD, … — is 0.
    /// </summary>
    private static int CodecRank(bool isAv1, bool isHevc, bool isH264) =>
        isAv1 ? 3 : isHevc ? 2 : isH264 ? 1 : 0;

    /// <summary>
    ///     <see langword="true"/> when a source already in a codec at least as space-efficient
    ///     as the configured target encoder. Re-encoding a more-efficient source down to a
    ///     less-efficient target loses quality without saving meaningful space, so such a file
    ///     is treated as "already at the target codec" — an AV1 source is never re-encoded to
    ///     an HEVC target, and an HEVC source is never re-encoded to an H.264 target. This
    ///     replaces the old exact-codec-match test (which treated AV1 as "not HEVC" and ran it
    ///     through the H.264 shrink path). Legacy codecs (rank 0) sit below any modern target,
    ///     so they still convert; an H.264 target still rejects MPEG-2/VC-1/VP9 sources because
    ///     those are rank 0, not rank 1.
    /// </summary>
    internal static bool SourceCodecMeetsTarget(
        bool sourceIsAv1, bool sourceIsHevc, bool sourceIsH264, bool targetIsHevc, bool targetIsAv1)
    {
        int targetRank = targetIsAv1 ? 3 : targetIsHevc ? 2 : 1;
        return CodecRank(sourceIsAv1, sourceIsHevc, sourceIsH264) >= targetRank;
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
        bool sourceIsH264 = string.Equals(videoStream?.CodecName, "h264", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(videoStream?.CodecName, "avc", StringComparison.OrdinalIgnoreCase);
        // Source already at least as efficient as the target → eligible for a video-copy pass.
        bool alreadyTargetCodec = SourceCodecMeetsTarget(sourceIsAv1, workItem.IsHevc, sourceIsH264, targetIsHevc, targetIsAv1);
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
    ///     (<c>matroska</c> / <c>mp4</c> / <c>webm</c>) and the variable flags
    ///     (MP4 needs <c>-movflags +faststart</c>; MKV/WebM don't). Extracted from
    ///     <c>ConvertVideoAsync</c> so the wire format can be unit-tested without
    ///     spinning up the encode pipeline.
    /// </summary>
    /// <param name="format">"mkv", "mp4", or "webm". Anything else is treated as MP4.</param>
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
        bool isMp4 = FfprobeService.IsMp4(format);
        // MP4 alone needs +faststart for progressive playback; MKV and WebM don't.
        string varFlags = isMp4
            ? "-movflags +faststart -max_muxing_queue_size 9999 "
            : "-max_muxing_queue_size 9999 ";
        string muxer = FormatMuxer(format);

        return $"{initFlags} {analyzeFlags}-i \"{inputPath}\" {extraInputs}{videoFlags}{compressionFlags}{audioFlags}{subtitleFlags}{varFlags}-f {muxer} \"{outputPath}\"";
    }

    /// <summary> Maps an output container token to its ffmpeg <c>-f</c> muxer name. </summary>
    internal static string FormatMuxer(string format) =>
        FfprobeService.IsMatroska(format) ? "matroska"
      : FfprobeService.IsWebm(format)     ? "webm"
      : "mp4";

    /// <summary> Maps an output container token to its on-disk file extension. </summary>
    internal static string FormatExtension(string format) =>
        FfprobeService.IsMatroska(format) ? ".mkv"
      : FfprobeService.IsWebm(format)     ? ".webm"
      : ".mp4";

    /// <summary>
    ///     Coerces the per-job options clone so the encode is internally consistent when
    ///     <see cref="EncoderOptions.Format"/> is <c>"webm"</c>. WebM's official codec list is
    ///     AV1/VP9/VP8 + Opus/Vorbis; ffmpeg's <c>webm</c> muxer rejects everything else. This
    ///     forces <c>Codec=av1</c> + <c>Encoder=libsvtav1</c> when the user picked H.264/H.265,
    ///     so the rest of the pipeline doesn't have to think about it. Audio coercion happens
    ///     inside <see cref="FfprobeService.MapAudio"/>.
    /// </summary>
    private async Task CoerceForWebmAsync(WorkItem workItem, EncoderOptions options)
    {
        if (!FfprobeService.IsWebm(options.Format)) return;

        if (!string.Equals(options.Codec, "av1", StringComparison.OrdinalIgnoreCase))
        {
            await LogAsync(workItem.Id,
                $"WebM output requires AV1 video — coercing codec from '{options.Codec}' to 'av1'.");
            options.Codec   = "av1";
            options.Encoder = "libsvtav1";
        }
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
        "240p"  => 240,
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
    ///     When <see cref="EncoderOptions.FixedFrameSize"/> is set (e.g. "640x480"),
    ///     builds a scale+pad+format filter chain that fits the video inside the
    ///     target frame with letterboxing and forces <c>yuv420p</c>. This is required
    ///     by device-specific presets like iPod Classic, which demand an exact frame
    ///     size. Returns <c>null</c> when <see cref="EncoderOptions.FixedFrameSize"/>
    ///     is unset or unparseable, so the caller falls back to <see cref="ComputeScaleExpr"/>.
    /// </summary>
    internal static string? ComputeFixedFrameFilter(EncoderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FixedFrameSize)) return null;

        // Parse "WxH" (case-insensitive).
        var parts = options.FixedFrameSize.ToLowerInvariant().Split('x');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int w)
            || !int.TryParse(parts[1], out int h)
            || w <= 0 || h <= 0) return null;

        // Scale to fit inside w×h preserving aspect ratio, then pad to exact w×h
        // with letterboxing, then force yuv420p (required by Baseline profile and
        // most hardware players). The commas inside min() are escaped as \, so
        // ffmpeg treats them as part of the expression, not filter-chain separators.
        return $"scale=min(iw\\,{w}):min(ih\\,{h}):force_original_aspect_ratio=decrease," +
               $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,format=yuv420p";
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
            return $"-g 25 -rc_mode CQP -global_quality:v 25 ";
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
    ///     Serializes hardware detection. The constructor kicks off a background
    ///     detection, and any encode that starts with "auto" before it finishes
    ///     would otherwise launch a SECOND concurrent detection — both mutate the
    ///     process-wide LIBVA_DRIVER_NAME env var and corrupt each other's probes.
    /// </summary>
    private readonly SemaphoreSlim _detectionGate = new(1, 1);

    /// <summary>
    ///     Set to <c>true</c> during Linux detection when the QSV probe on an Intel
    ///     render node succeeds. The static helpers <see cref="GetEncoder"/> and
    ///     <see cref="GetInitFlags"/> read this to pick QSV variants over the VAAPI
    ///     defaults on Linux without plumbing a new field through <c>EncoderOptions</c>.
    ///     Static because detection is a one-shot at startup and the static helpers
    ///     don't have an instance handle.
    /// </summary>
    internal static bool LinuxIntelUsesQsv { get; set; }

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

        await _detectionGate.WaitAsync();
        try
        {
            // Double-check after acquiring the gate — a concurrent caller may have
            // just finished the full detection while we waited.
            if (_detectedHardware != null)
                return _detectedHardware;
            return await DetectHardwareAccelerationCoreAsync();
        }
        finally
        {
            _detectionGate.Release();
        }
    }

    private async Task<string> DetectHardwareAccelerationCoreAsync()
    {
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

            // Intel iGPU and AMD GPUs on Linux — walk every render node, not just
            // renderD128. On hybrid laptops (e.g. Pop!_OS with system76-power in
            // hybrid/nvidia/compute mode) the NVIDIA card can claim renderD128 while
            // the iGPU lands on renderD129. The legacy single-node probe failed against
            // the NVIDIA node (no iHD/i965 VAAPI support there) and silently skipped
            // VAAPI entirely, even though a perfectly good iGPU was sitting on the
            // next render node over.
            //
            // Per-node order is QSV first, then VAAPI. On modern Intel parts with
            // Jellyfin-FFmpeg's oneVPL runtime (shipped in the Docker image), QSV is
            // the Intel-native encode path and outperforms VAAPI on HEVC; VAAPI stays
            // as the fallback for builds without QSV support or older drivers.
            var renderNodes = EnumerateRenderNodes();
            var originalLibvaDriver = Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME");
            try
            {
                foreach (var nodePath in renderNodes)
                {
                    var nodeVendor = ReadRenderNodeVendor(nodePath);

                    // NVIDIA nodes don't expose VAAPI/QSV encode — the dedicated NVENC
                    // probe below handles them. Skip so we don't waste probes or mislabel
                    // a CUDA card as a VAAPI device.
                    if (nodeVendor == "nvidia")
                    {
                        Console.WriteLine($"Auto-detect: {nodePath} is NVIDIA — handled by the NVENC probe, skipping VAAPI/QSV");
                        continue;
                    }

                    // AMD Radeon: VAAPI via Mesa's radeonsi driver (QSV is Intel-only, so
                    // don't probe it). Crucially this registers DeviceId="amd" so an explicit
                    // "AMD" hardware-acceleration preference can match — the historical code
                    // labelled every Linux VAAPI GPU "intel", which stranded AMD users with
                    // jobs stuck Pending. Encoder names are the vendor-agnostic *_vaapi set,
                    // identical to the Intel-VAAPI path; only the family id and driver differ.
                    if (nodeVendor == "amd")
                    {
                        bool amdMatched = false;
                        // radeonsi is the Mesa VAAPI driver for Radeon; fall back to libva's
                        // own auto-detection ("" → unset) if the explicit driver doesn't take.
                        foreach (var driver in new[] { "radeonsi", "" })
                        {
                            Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", string.IsNullOrEmpty(driver) ? null : driver);
                            var label = string.IsNullOrEmpty(driver) ? "auto" : driver;
                            Console.WriteLine($"Auto-detect: Trying AMD VAAPI on {nodePath} with {label} driver...");

                            var amdInit = $"-init_hw_device vaapi=hw:{nodePath} -filter_hw_device hw";
                            bool hevcOk = await TestEncoderAsync(amdInit, "hevc_vaapi");
                            bool h264Ok = await TestEncoderAsync(amdInit, "h264_vaapi");
                            bool av1Ok  = await TestEncoderAsync(amdInit, "av1_vaapi");

                            if (hevcOk || h264Ok || av1Ok)
                            {
                                Console.WriteLine($"Auto-detect: AMD VAAPI available on {nodePath} with {label} driver (hevc={hevcOk}, h264={h264Ok}, av1={av1Ok})");
                                devices.Add(new HardwareDevice
                                {
                                    DeviceId           = "amd",
                                    DisplayName        = "AMD VAAPI",
                                    SupportedCodecs    = BuildSupportedCodecs(hevcOk, h264Ok, av1Ok),
                                    Encoders           = BuildAmdEncoders(hevcOk, h264Ok, av1Ok, amf: false),
                                    DefaultConcurrency = DefaultConcurrencyFor("amd"),
                                    IsHardware         = true,
                                    DevicePath         = nodePath,
                                });
                                amdMatched = true;
                                break;
                            }
                        }
                        if (amdMatched) break;
                        continue; // AMD node that didn't probe — move on to the next render node
                    }

                    // Intel iGPU/dGPU (or an unknown vendor — fall back to the historical
                    // Intel probe path). QSV first. oneVPL on Linux needs the iHD driver —
                    // i965 doesn't expose QSV — so pin it before probing instead of leaving
                    // it to the host's default.
                    Console.WriteLine($"Auto-detect: Trying Intel QSV (Linux) on {nodePath}...");
                    Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", "iHD");

                    var qsvInit = $"-hwaccel qsv -qsv_device {nodePath}";
                    bool qsvHevc = await TestEncoderAsync(qsvInit, "hevc_qsv");
                    bool qsvH264 = await TestEncoderAsync(qsvInit, "h264_qsv");
                    bool qsvAv1  = await TestEncoderAsync(qsvInit, "av1_qsv");

                    if (qsvHevc || qsvH264 || qsvAv1)
                    {
                        Console.WriteLine($"Auto-detect: Intel QSV (Linux) available on {nodePath} (hevc={qsvHevc}, h264={qsvH264}, av1={qsvAv1})");
                        devices.Add(new HardwareDevice
                        {
                            DeviceId           = "intel",
                            DisplayName        = "Intel QSV",
                            SupportedCodecs    = BuildSupportedCodecs(qsvHevc, qsvH264, qsvAv1),
                            Encoders           = BuildIntelEncoders(qsvHevc, qsvH264, qsvAv1, qsv: true),
                            DefaultConcurrency = DefaultConcurrencyFor("intel"),
                            IsHardware         = true,
                            DevicePath         = nodePath,
                        });
                        LinuxIntelUsesQsv = true;
                        break;
                    }

                    // QSV unavailable on this node — fall back to VAAPI with the
                    // existing two-driver probe (iHD, then legacy i965).
                    var driversToTry = new[] { "iHD", "i965" };
                    bool nodeMatched = false;
                    foreach (var driver in driversToTry)
                    {
                        Console.WriteLine($"Auto-detect: Trying VAAPI on {nodePath} with {driver} driver...");
                        Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", driver);

                        var hwInit = $"-init_hw_device vaapi=hw:{nodePath} -filter_hw_device hw";
                        bool hevcOk = await TestEncoderAsync(hwInit, "hevc_vaapi");
                        bool h264Ok = await TestEncoderAsync(hwInit, "h264_vaapi");
                        bool av1Ok  = await TestEncoderAsync(hwInit, "av1_vaapi");

                        if (hevcOk || h264Ok || av1Ok)
                        {
                            Console.WriteLine($"Auto-detect: VAAPI available on {nodePath} with {driver} driver (hevc={hevcOk}, h264={h264Ok}, av1={av1Ok})");
                            devices.Add(new HardwareDevice
                            {
                                DeviceId           = "intel",
                                DisplayName        = "Intel VAAPI",
                                SupportedCodecs    = BuildSupportedCodecs(hevcOk, h264Ok, av1Ok),
                                Encoders           = BuildIntelEncoders(hevcOk, h264Ok, av1Ok, qsv: false),
                                DefaultConcurrency = DefaultConcurrencyFor("intel"),
                                IsHardware         = true,
                                DevicePath         = nodePath,
                            });
                            nodeMatched = true;
                            break;
                        }
                    }
                    // First working render node wins — stop probing the rest. We only
                    // expose one VAAPI device family right now (DeviceId="intel"), so
                    // there's no way to surface a second VAAPI GPU through the existing
                    // slot-pool model. If/when the model grows per-card devices, drop
                    // this break and let every node register its own HardwareDevice.
                    if (nodeMatched) break;
                }
            }
            finally
            {
                // Restore whatever LIBVA_DRIVER_NAME the host set (or unset, if it
                // wasn't set at all) so we don't leak driver state into later ffmpeg
                // invocations that don't go through GetInitFlags.
                Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", originalLibvaDriver);
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
    ///     Returns every <c>/dev/dri/renderD*</c> node sorted by the trailing index so
    ///     iteration is deterministic — important for both reproducible probe order on
    ///     hybrid systems and stable test expectations. Returns an empty array when the
    ///     <c>/dev/dri</c> directory is missing (Windows, macOS, container without GPU
    ///     passthrough).
    /// </summary>
    internal static string[] EnumerateRenderNodes()
    {
        if (!Directory.Exists("/dev/dri")) return Array.Empty<string>();
        return Directory.GetFiles("/dev/dri", "renderD*")
            .OrderBy(p =>
            {
                var name = Path.GetFileName(p);
                return int.TryParse(name.AsSpan("renderD".Length), out var n) ? n : int.MaxValue;
            })
            .ToArray();
    }

    /// <summary>
    ///     Maps a <c>/dev/dri/renderD*</c> node to a Snacks device family by reading its
    ///     PCI vendor id from sysfs (<c>/sys/class/drm/{node}/device/vendor</c>):
    ///     <c>0x1002</c>→<c>"amd"</c>, <c>0x8086</c>→<c>"intel"</c>, <c>0x10de</c>→<c>"nvidia"</c>.
    ///     Returns <see langword="null"/> when the file is missing or the id is unrecognised —
    ///     callers treat that as "probe the historical Intel/VAAPI path". This is what lets
    ///     Linux detection register an AMD Radeon as <c>DeviceId="amd"</c> instead of
    ///     mislabelling every VAAPI GPU as <c>"intel"</c> (which stranded AMD users with an
    ///     explicit "AMD" preference — jobs stuck Pending forever).
    /// </summary>
    internal static string? ReadRenderNodeVendor(string nodePath)
    {
        try
        {
            var name = Path.GetFileName(nodePath);
            var vendorFile = $"/sys/class/drm/{name}/device/vendor";
            if (!File.Exists(vendorFile)) return null;
            return File.ReadAllText(vendorFile).Trim().ToLowerInvariant() switch
            {
                "0x1002" => "amd",
                "0x8086" => "intel",
                "0x10de" => "nvidia",
                _        => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Logs VAAPI diagnostic info (vainfo output) for troubleshooting. On hybrid
    ///     systems with multiple render nodes, vainfo runs against the first node — the
    ///     detection probe loop covers the rest.
    /// </summary>
    private async Task LogVaapiInfoAsync()
    {
        // VAAPI is Linux-only — skip entirely on Windows and macOS
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return;

        try
        {
            var renderNodes = EnumerateRenderNodes();
            if (renderNodes.Length == 0)
            {
                Console.WriteLine("Auto-detect: no /dev/dri/renderD* nodes found");
                if (Directory.Exists("/dev/dri"))
                {
                    var entries = Directory.GetFileSystemEntries("/dev/dri");
                    Console.WriteLine($"Auto-detect: /dev/dri contents: {string.Join(", ", entries)}");
                }
                else
                    Console.WriteLine("Auto-detect: /dev/dri directory does not exist");
                return;
            }

            Console.WriteLine($"Auto-detect: render nodes found: {string.Join(", ", renderNodes)}");
            var firstNode = renderNodes[0];

            var psi = new ProcessStartInfo("vainfo")
            {
                Arguments = $"--display drm --device {firstNode}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // Pick the VAAPI driver by the node's actual vendor so vainfo reports something
            // useful on AMD too — forcing iHD (Intel) on a Radeon prints a driver-load error
            // instead of the codec table. Honour an explicit host override first.
            var defaultDriver = ReadRenderNodeVendor(firstNode) == "amd" ? "radeonsi" : "iHD";
            psi.Environment["LIBVA_DRIVER_NAME"] = Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME") ?? defaultDriver;

            using var process = new Process { StartInfo = psi };
            process.Start();

            // Start both reads BEFORE waiting — awaiting ReadToEndAsync first would
            // only complete when the process exits, making the timeout unreachable
            // (and risking a pipe-buffer deadlock between stdout and stderr).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(5000));
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var output = !string.IsNullOrEmpty(stdout) ? stdout : stderr;
            Console.WriteLine($"Auto-detect vainfo ({firstNode}) output:\n{output.Substring(0, Math.Min(output.Length, 1000))}");
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
                extra = "-rc_mode CQP -global_quality:v 25";
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

            // Start the read but DON'T await it before the timeout wait —
            // ReadToEndAsync only completes when ffmpeg exits, so awaiting it
            // first made the 10s hang-protection unreachable. A hung driver
            // probe would wedge hardware detection forever.
            var stderrTask = process.StandardError.ReadToEndAsync();

            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(10000));
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Console.WriteLine($"Auto-detect: {encoder} test timed out");
                return false;
            }
            var stderr = await stderrTask;

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
    ///
    ///     <para>Once a concrete device family is in hand, also fills in
    ///     <see cref="EncoderOptions.HardwareDevicePath"/> from the locally detected
    ///     <see cref="HardwareDevice.DevicePath"/> so VAAPI jobs target the render
    ///     node detection picked instead of falling back to the legacy renderD128
    ///     default. Skips the path lookup when the caller pre-set a path (e.g. a
    ///     test fixture exercising a specific node) or when the family doesn't
    ///     map to a node path (NVIDIA, Windows QSV/AMF, Apple, CPU).</para>
    /// </summary>
    private async Task ResolveHardwareAccelerationAsync(EncoderOptions options)
    {
        if (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            options.HardwareAcceleration = await DetectHardwareAccelerationAsync();
        }

        if (options.HardwareDevicePath == null
            && !string.Equals(options.HardwareAcceleration, "none", StringComparison.OrdinalIgnoreCase))
        {
            options.HardwareDevicePath = GetDevicePathForDeviceId(options.HardwareAcceleration);
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
        => GetInitFlags(hardwareAcceleration, devicePath: null, RuntimeInformation.IsOSPlatform(OSPlatform.Windows), LinuxIntelUsesQsv, hwDecode);

    /// <summary>
    ///     Path-aware overload. <paramref name="devicePath"/> selects the VAAPI render
    ///     node (e.g. <c>/dev/dri/renderD129</c>); <see langword="null"/> falls back to
    ///     <c>/dev/dri/renderD128</c> for compatibility with the legacy single-node
    ///     assumption. Use this overload from the dispatch path so jobs land on the
    ///     same node detection picked.
    /// </summary>
    internal static string GetInitFlags(string hardwareAcceleration, string? devicePath, bool hwDecode = true)
        => GetInitFlags(hardwareAcceleration, devicePath, RuntimeInformation.IsOSPlatform(OSPlatform.Windows), LinuxIntelUsesQsv, hwDecode);

    /// <summary>
    ///     Pure overload that takes <paramref name="isWindows"/> explicitly so unit tests can
    ///     exercise the VAAPI / QSV / AMF branches independently of the test host OS.
    /// </summary>
    internal static string GetInitFlags(string hardwareAcceleration, bool isWindows, bool hwDecode)
        => GetInitFlags(hardwareAcceleration, devicePath: null, isWindows, linuxIntelQsv: false, hwDecode);

    /// <summary>
    ///     Pure overload with explicit OS gate AND device-path injection. The four VAAPI
    ///     branches use <paramref name="devicePath"/> when set, falling back to renderD128
    ///     so single-GPU machines get the same flag string they always have.
    /// </summary>
    internal static string GetInitFlags(string hardwareAcceleration, string? devicePath, bool isWindows, bool hwDecode)
        => GetInitFlags(hardwareAcceleration, devicePath, isWindows, linuxIntelQsv: false, hwDecode);

    /// <summary>
    ///     Pure overload with explicit OS gate, device path, and Linux-QSV preference. When
    ///     <paramref name="linuxIntelQsv"/> is <c>true</c> and we're on Linux, Intel jobs
    ///     use the QSV pipeline (<c>-hwaccel qsv -qsv_device {path}</c>) instead of VAAPI.
    ///     <paramref name="linuxIntelQsv"/> is ignored on Windows, where QSV is always the
    ///     Intel path, and for non-Intel hwaccel values.
    /// </summary>
    internal static string GetInitFlags(string hardwareAcceleration, string? devicePath, bool isWindows, bool linuxIntelQsv, bool hwDecode)
    {
        var node = string.IsNullOrEmpty(devicePath) ? "/dev/dri/renderD128" : devicePath;
        return hardwareAcceleration.ToLower() switch
        {
            "intel" when isWindows => "-y -hwaccel qsv -hwaccel_output_format qsv -qsv_device auto",
            "amd" when isWindows => "-y -hwaccel auto",
            // Linux Intel QSV: derive a QSV device from VAAPI on the same render node
            // when input is software-decoded, so the encoder can still find a QSV
            // context. Full pipeline when input is hw-decodable.
            "intel" when linuxIntelQsv && !hwDecode => $"-y -init_hw_device vaapi=va:{node} -init_hw_device qsv=hw@va -filter_hw_device hw",
            "intel" when linuxIntelQsv => $"-y -hwaccel qsv -hwaccel_output_format qsv -qsv_device {node}",
            // Software decode + VAAPI encode: init the device but don't force hwaccel decode
            "intel" when !hwDecode => $"-y -init_hw_device vaapi=hw:{node} -filter_hw_device hw",
            "amd" when !hwDecode => $"-y -init_hw_device vaapi=hw:{node} -filter_hw_device hw",
            "intel" => $"-y -init_hw_device vaapi=hw:{node} -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
            "amd" => $"-y -init_hw_device vaapi=hw:{node} -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
            "nvidia" => "-y -hwaccel cuda",
            "apple" => "-y -hwaccel videotoolbox",
            _ => "-y"
        };
    }

    /// <summary>
    ///     Returns the explicit NVDEC (cuvid) input decoder flag for the given source codec, or
    ///     <see cref="string.Empty"/> when none applies. Forces NVDEC instead of relying on
    ///     <c>-hwaccel cuda</c>'s auto-attach, which silently falls back to a software decoder
    ///     on some driver/setup combinations (datacenter Windows drivers, vGPU profiles, etc.).
    /// </summary>
    internal static string GetNvidiaInputDecoder(string? sourceCodec) =>
        (sourceCodec?.ToLowerInvariant()) switch
        {
            "h264"       => "-c:v h264_cuvid",
            "hevc"       => "-c:v hevc_cuvid",
            "av1"        => "-c:v av1_cuvid",
            "vp9"        => "-c:v vp9_cuvid",
            "vp8"        => "-c:v vp8_cuvid",
            "vc1"        => "-c:v vc1_cuvid",
            "mpeg2video" => "-c:v mpeg2_cuvid",
            "mpeg4"      => "-c:v mpeg4_cuvid",
            "mjpeg"      => "-c:v mjpeg_cuvid",
            _ => ""
        };

    /// <summary>
    ///     Maps the user's encoder preference and hardware acceleration setting to the
    ///     concrete FFmpeg encoder name (e.g., <c>"hevc_vaapi"</c>, <c>"hevc_nvenc"</c>, <c>"libx265"</c>).
    /// </summary>
    internal static string GetEncoder(EncoderOptions options)
        => GetEncoder(options, RuntimeInformation.IsOSPlatform(OSPlatform.Windows), LinuxIntelUsesQsv);

    /// <summary>
    ///     Pure overload that takes the OS gate and Linux-QSV preference explicitly so unit
    ///     tests can exercise every branch (QSV / VAAPI / AMF / NVENC / VideoToolbox)
    ///     independently of the test host. <paramref name="linuxIntelQsv"/> only matters
    ///     when <paramref name="isWindows"/> is <c>false</c> and the hwaccel is "intel".
    /// </summary>
    internal static string GetEncoder(EncoderOptions options, bool isWindows, bool linuxIntelQsv)
    {
        // Case-insensitive: Encoder can come from settings.json or per-folder overrides
        // where casing isn't enforced; the UI's ENCODER_MAP is lowercase but external
        // entry points aren't, and a non-matching codec string silently falls through
        // to passing the raw value to ffmpeg.
        var encoder = options.Encoder ?? "";
        bool isAv1  = encoder.Contains("av1", StringComparison.OrdinalIgnoreCase)
                   || encoder.Contains("svt", StringComparison.OrdinalIgnoreCase);
        bool isH265 = !isAv1 && encoder.Contains("265", StringComparison.OrdinalIgnoreCase);
        bool isH264 = !isAv1 && encoder.Contains("264", StringComparison.OrdinalIgnoreCase);
        bool intelUsesQsv = isWindows || linuxIntelQsv;

        return options.HardwareAcceleration.ToLower() switch
        {
            // AV1 encoders
            "intel" when intelUsesQsv && isAv1 => "av1_qsv",
            "amd" when isWindows && isAv1 => "av1_amf",
            "intel" when isAv1 => "av1_vaapi",
            "amd" when isAv1 => "av1_vaapi",
            "nvidia" when isAv1 => "av1_nvenc",
            // H.264 encoders
            "intel" when intelUsesQsv && isH264 => "h264_qsv",
            "amd" when isWindows && isH264 => "h264_amf",
            "intel" when isH264 => "h264_vaapi",
            "amd" when isH264 => "h264_vaapi",
            "nvidia" when isH264 => "h264_nvenc",
            // H.265 encoders
            "intel" when intelUsesQsv && isH265 => "hevc_qsv",
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
    ///     AMD AMF encoders (h264_amf, hevc_amf, av1_amf) only accept three quality
    ///     presets: "speed", "balanced", "quality". Passing the libx265-style names
    ///     ("veryslow" etc.) makes FFmpeg fail with "Unable to parse preset option
    ///     value". Maps the shared UI preset onto AMF's three-step ladder.
    /// </summary>
    internal static string MapAmfPreset(string preset) => (preset ?? "").ToLowerInvariant() switch
    {
        "veryslow" => "quality",
        "slow"     => "quality",
        "medium"   => "balanced",
        "fast"     => "speed",
        "veryfast" => "speed",
        _          => "balanced",
    };

    /// <summary>
    ///     Computes the output file path for a work item.
    ///     Prefers <c>EncodeDirectory</c>, then <c>OutputDirectory</c>, then the source file's directory.
    ///     Output file is named <c>"{basename} [snacks].{ext}"</c>.
    /// </summary>
    private string GetOutputPath(WorkItem workItem, EncoderOptions options)
    {
        string fileName = _fileService.RemoveExtension(workItem.FileName);
        string extension = FormatExtension(options.Format);
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
    /// <remarks>
    ///     <c>internal static</c> so the regression test in
    ///     <c>RemoteConversionPlacementTests</c> can document the rename behavior — the
    ///     bug behind cluster mux-pass disappearance was the worker running the rename
    ///     in its own temp dir, after which <see cref="ClusterNodeJobService.GetOutputFileForJob"/>'s
    ///     <c>*[snacks]*</c> glob found nothing and falsely reported noSavings.
    /// </remarks>
    internal static string GetCleanOutputName(string snacksPath)
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
        string initFlags = GetInitFlags(options.HardwareAcceleration, options.HardwareDevicePath, canHwDecode);
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

            // Every pass in this mode failed before producing a measurable bitrate
            // (e.g. iGPUs with no HEVC LP entrypoint — common on pre-Tiger Lake Intel
            // and on Proxmox LXC where GuC firmware isn't loaded). Fall through to
            // normal mode; in normal mode, signal incompatibility so the retry chain
            // skips conservative-HW (a no-op for VAAPI) and goes straight to software.
            if (lowPower)
            {
                await LogAsync(workItem.Id,
                    $"{modeLabel}: encoder rejected every sample — falling back to normal mode.");
                continue;
            }
            await LogAsync(workItem.Id,
                $"{modeLabel}: encoder rejected every sample — VAAPI cannot encode this file.");
            return (-1, false);
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

            // Async wait — the old synchronous WaitForExit pinned a threadpool
            // thread for up to 30s per measured file during library scans.
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(30000));
            if (!process.HasExited)
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
                                        + double.Parse("0." + timeMatch.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
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

        string initFlags = GetInitFlags(options.HardwareAcceleration, options.HardwareDevicePath);
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

            // Async wait — calibration runs can pin threadpool threads for minutes.
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(180000));
            if (!process.HasExited)
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
            $"-c:v {encoder} {lpFlag}{hwFilter} -g 25 -rc_mode CQP -global_quality:v {qp} " +
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

            // Async wait — VAAPI calibration can run up to 6 iterations × 2 samples
            // and used to pin a threadpool thread for the whole time.
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(120000));
            if (!process.HasExited)
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
        // Seconds-granular so files under a minute don't end up with -t 00:00:00
        // (which decodes zero frames and silently disables cropping).
        int sampleSeconds = lengthInMinutes > 20
            ? 600
            : (int)Math.Clamp(workItem.Length, 1, 600);
        string duration = TimeSpan.FromSeconds(sampleSeconds).ToString(@"hh\:mm\:ss");

        // cropdetect is a software-only filter, so the sample must decode into
        // system memory. VAAPI/QSV init flags include -hwaccel_output_format
        // (frames stay on the GPU → the filter graph fails and crop detection
        // silently no-ops), so those decode in software. NVIDIA/Apple emit a
        // plain -hwaccel whose frames land in system memory — keep the
        // hardware-assisted decode there; a 10-minute 4K sample is expensive
        // on CPU.
        bool hwDecodeSafeForCropdetect =
            options.HardwareAcceleration.Equals("nvidia", StringComparison.OrdinalIgnoreCase) ||
            options.HardwareAcceleration.Equals("apple", StringComparison.OrdinalIgnoreCase);
        string initFlags = hwDecodeSafeForCropdetect
            ? GetInitFlags(options.HardwareAcceleration, options.HardwareDevicePath) + " "
            : "";
        string command = $"{initFlags}-ss {startTime} -i \"{inputPath}\" " +
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

        // Timeout cropdetect at 3 minutes to prevent hangs (async wait — the
        // synchronous WaitForExit pinned a threadpool thread for the duration).
        await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(180000));
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            await LogAsync(workItem.Id, "Crop detection timed out, skipping.");
            return "";
        }
        // Let the async event readers flush their final lines.
        process.WaitForExit();

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

        // Wait for exit but kill the process if it stalls (no output for stallTimeoutSeconds).
        // The finally block guarantees the reader tasks are drained and the Process handle
        // is disposed on EVERY exit path — the cancel/stop throws used to leak both.
        int exitCode;
        try
        {
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

            if (workItem.Status is WorkItemStatus.Cancelled or WorkItemStatus.Stopped)
                throw new OperationCanceledException("Encoding was cancelled.");

            exitCode = process.ExitCode;
        }
        finally
        {
            // Ensure ffmpeg is dead before awaiting the readers so they can't hang
            // on an open pipe, then drain them and release the process handle.
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            try { await stderrTask; } catch { }
            try { await stdoutTask; } catch { }

            lock (_activeLock)
            {
                if (_activeLocalJobs.TryGetValue(workItem.Id, out var slot)) slot.Process = null;
            }
            process.Dispose();
        }

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
        bool skipPlacement = false,
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
                skipPlacement: skipPlacement,
                cancellationToken: cancellationToken);
            return;
        }

        // Retry 2a: Drop image-based subs only — keeps OCR'd SRTs and text-based (srt/ass)
        // streams. Bitmap streams (PGS/VOBSUB/DVB) are the most common cause of subtitle
        // failures; many failures clear once they're removed without sacrificing the rest
        // of the user's subtitles. Only relevant when the user opted into MKV pass-through.
        bool wasPassingBitmaps = FfprobeService.IsMatroska(options.Format) && options.PassThroughImageSubtitlesMkv;
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
                skipPlacement: skipPlacement,
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
                skipPlacement: skipPlacement,
                cancellationToken: cancellationToken);
            return;
        }

        // Retry 3: Software decode + HW encode for hwaccel filter graph / decoder errors.
        // VAAPI: drops -hwaccel vaapi and routes SW frames into the encoder via hwupload.
        // NVIDIA: drops the explicit -c:v <src>_cuvid (cuvid decoder rejected the profile,
        // or nvcuvid isn't loaded on this driver/setup); -hwaccel cuda's auto-attach + native
        // decoder still runs, so NVENC keeps the GPU encode path.
        bool isHwaccelError = reason.Contains("hwaccel", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("filter graph", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Impossible to convert", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("hwupload", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Reconfiguring filter", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("cuvid", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("nvcuvid", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("ffnvcodec", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Failed to get HW config", StringComparison.OrdinalIgnoreCase);

        bool isVaapi = IsVaapiAcceleration(options.HardwareAcceleration);
        bool isNvidia = options.HardwareAcceleration.Equals("nvidia", StringComparison.OrdinalIgnoreCase);

        if (isHwaccelError && (isVaapi || isNvidia) && !swDecodeWasForced)
        {
            await LogAsync(workItem.Id,
                isNvidia
                    ? "Retrying without explicit NVDEC decoder (falling back to -hwaccel cuda auto-attach)..."
                    : "Retrying with software decode + VAAPI encode...");
            workItem.Progress = 0;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            await ConvertVideoAsync(workItem, options,
                stripSubtitles: subtitlesWereStripped,
                forceSwDecode: true,
                useConservativeHwFlags: conservativeHwFlagsTried,
                cachedOcrSrts: cachedOcrSrts,
                cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                dropImageSubtitlesOnly: imageSubtitlesAlreadyDropped,
                skipPlacement: skipPlacement,
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
            // Use the correct software encoder for the target codec — an H.264 target
            // must fall back to libx264, not libx265, or the retry silently changes
            // the output codec the user asked for.
            bool isAv1Target = string.Equals(options.Codec, "av1", StringComparison.OrdinalIgnoreCase);
            options.Encoder = isAv1Target ? "libsvtav1" : GetSoftwareFallbackEncoder(options);
            options.HardwareAcceleration = "none";
            await ConvertVideoAsync(workItem, options,
                stripSubtitles: false,
                cachedOcrSrts: cachedOcrSrts,
                cachedOcrMuxTmpDir: cachedOcrMuxTmpDir,
                skipPlacement: skipPlacement,
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
    ///     settings changes without needing a new scan, and wakes the
    ///     scheduler so a raised <c>MasterMusicConcurrency</c> takes effect on
    ///     the very next iteration. Capacity for music (and every other
    ///     master-local device) lives in the SlotLedger's resolver, which
    ///     reads <c>MasterMusicConcurrency</c> live from the options on every
    ///     reservation — no semaphore to resize.
    /// </summary>
    public void UpdateOptions(EncoderOptions options)
    {
        _lastOptions = options;
        SetQueueOrderNewestFirst(options.QueueNewestFirst);
        // A settings change (e.g. flipping hardware acceleration) can make rows the
        // scheduler had rotated past as "locally unservable" servable again. Mark the
        // window dirty rather than just waking the loop so the rotation offset resets and
        // the window snaps back to the head — otherwise newly-servable items can keep
        // getting skipped until some other queue mutation clears the offset.
        MarkQueueWindowDirty();
    }

    /// <summary> Current queue-order policy, exposed so the queue API pages the DB in the same order the scheduler dispatches. </summary>
    public bool QueueNewestFirst => _queueNewestFirst;

    /// <summary>
    ///     Resolves and persists <see cref="MediaFile.OriginalLanguage"/> for every row in
    ///     <paramref name="files"/> whose value is currently null. No-op when
    ///     <c>KeepOriginalLanguage</c> is off or no integration provider is wired.
    ///     The integration service caches per-show / per-movie, so a library with N files
    ///     across M titles makes ~M API calls, not N.
    /// </summary>
    /// <returns>The number of rows whose <c>OriginalLanguage</c> was newly populated.</returns>
    public async Task<int> BackfillOriginalLanguageAsync(
        IReadOnlyList<MediaFile> files,
        EncoderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.KeepOriginalLanguage || _integrationService == null) return 0;

        int filled = 0;
        foreach (var mf in files)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!string.IsNullOrWhiteSpace(mf.OriginalLanguage)) continue;

            try
            {
                var resolved = await _integrationService.LookupOriginalLanguageAsync(
                    mf.FilePath, options.OriginalLanguageProvider, cancellationToken);
                if (string.IsNullOrEmpty(resolved)) continue;
                mf.OriginalLanguage = resolved;
                await _mediaFileRepo.UpsertAsync(mf);
                filled++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* lookup failures fall back to the configured keep lists, matching ConvertVideoAsync */ }
        }
        return filled;
    }

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
        //
        // When KeepOriginalLanguage is on we resolve and cache the original language for
        // any row that doesn't have one yet — without it, WouldSkipUnderOptions can't see
        // the merged keep lists and would mis-predict drops on rows queued before the
        // OriginalLanguage cache existed. Live lookups go through the integration provider
        // and may be slow; the resolved value is persisted so subsequent re-evaluations are
        // cache hits.
        var idsToRemove = new HashSet<string>();
        foreach (var (id, path) in candidates)
        {
            var normalizedPath = Path.GetFullPath(path);
            var mf = await _mediaFileRepo.GetByPathAsync(normalizedPath);
            if (mf == null) continue;
            if (mf.AudioStreams == null && mf.SubtitleStreams == null) continue;

            if (newOptions.KeepOriginalLanguage
                && string.IsNullOrWhiteSpace(mf.OriginalLanguage)
                && _integrationService != null)
            {
                try
                {
                    var resolved = await _integrationService.LookupOriginalLanguageAsync(
                        mf.FilePath, newOptions.OriginalLanguageProvider, CancellationToken.None);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        mf.OriginalLanguage = resolved;
                        await _mediaFileRepo.UpsertAsync(mf);
                    }
                }
                catch { /* lookup failures fall back to the configured keep lists, matching ConvertVideoAsync */ }
            }

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
        // Step 4: drop from the work-item registry, notify the UI, and persist the
        // MediaFile transition to Skipped so the next scan respects the reverted setting.
        foreach (var item in removed)
        {
            UnregisterWorkItem(item.Id);
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

        // Step 5: the window only holds the top ~50 — the rest of the pending queue
        // lives in the DB. Walk it in pages and flip the now-obsolete rows too.
        // (The Reevaluate endpoint backfilled OriginalLanguage on queued rows before
        // calling us, so the predicate sees merged keep lists without live lookups.)
        int backlogFlipped = 0;
        try
        {
            backlogFlipped = await _mediaFileRepo.ReevaluateQueuedAsync(mf =>
                WouldSkipUnderOptions(mf, newOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Settings change: backlog re-evaluation failed: {ex.Message}");
        }

        if (removed.Count + backlogFlipped > 0)
        {
            MarkQueueWindowDirty();
            await NotifyQueueChangedAsync();
            Console.WriteLine($"Settings change: dropped {removed.Count} hydrated + {backlogFlipped} backlog now-obsolete queue item(s).");
        }
        return removed.Count + backlogFlipped;
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
        DevicePath         = src.DevicePath,
    };

    /// <summary>
    ///     Returns the locally detected <see cref="HardwareDevice.DevicePath"/> for the
    ///     given device id, or <see langword="null"/> if the device has no node path
    ///     (NVIDIA, Windows QSV/AMF, Apple, CPU) or hasn't been detected yet. Dispatch
    ///     sites stamp the result onto <see cref="EncoderOptions.HardwareDevicePath"/>
    ///     so per-job ffmpeg invocations target the same render node detection picked.
    /// </summary>
    public string? GetDevicePathForDeviceId(string deviceId)
    {
        if (_detectedDevices == null) return null;
        var match = _detectedDevices.FirstOrDefault(d =>
            string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        return match?.DevicePath;
    }

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
    ///     Installs the gate that decides whether local encoding is currently
    ///     allowed by the master's schedule window. <see cref="ClusterService"/>
    ///     wires this; without it (standalone / tests) local encoding is
    ///     always allowed.
    /// </summary>
    public void SetLocalScheduleGate(Func<bool>? gate) => _localScheduleGate = gate;

    /// <summary>
    ///     Registers a callback to fire whenever a work item is added to the
    ///     queue. Used by <see cref="ClusterService"/> to wake its dispatcher
    ///     immediately on enqueue so it competes with the master-local
    ///     scheduler for new items instead of waiting up to 2 seconds for the
    ///     next timer tick.
    /// </summary>
    public void SetWorkItemQueuedCallback(Action? callback) => _onWorkItemQueued = callback;

    /// <summary>
    ///     Fires once after <see cref="DetectHardwareAccelerationAsync"/>
    ///     populates <see cref="_detectedDevices"/>. <see cref="ClusterService"/>
    ///     wires this to refresh the local node's <c>Capabilities.Devices</c>
    ///     snapshot in its <c>_nodes</c> registry — without that refresh, the
    ///     SlotLedger's capacity resolver only sees the pre-detection snapshot
    ///     (music device only) and refuses every TryReserve for the real GPU/CPU
    ///     devices, which silently prevents standalone-mode encoding from
    ///     starting on macOS and Windows. In master mode the heartbeat timer
    ///     hides the race by re-snapshotting every few seconds; standalone has
    ///     no such timer.
    /// </summary>
    private Action? _hardwareDetectedCallback;

    /// <summary>
    ///     Registers the one-shot post-detection callback. If detection has
    ///     already completed by the time the callback is registered (the cluster
    ///     service may bind after the async probe finishes on fast machines),
    ///     fire it immediately so we don't leak the missed-edge race.
    /// </summary>
    public void SetHardwareDetectedCallback(Action callback)
    {
        _hardwareDetectedCallback = callback;
        if (_detectedDevices != null)
        {
            try { callback(); }
            catch (Exception ex) { Console.WriteLine($"HardwareDetected callback error: {ex.Message}"); }
        }
    }

    /// <summary>
    ///     Restarts <see cref="ProcessQueueAsync"/> if it had previously
    ///     exited because the schedule gate was closed. Idempotent — bails
    ///     early when the queue is paused or already running.
    /// </summary>
    public void WakeFromSchedule()
    {
        if (_lastOptions == null || _isPaused || _localEncodingPaused) return;
        _ = Task.Run(async () =>
        {
            try { await ProcessQueueAsync(_lastOptions); }
            catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync after schedule resume: {ex.Message}"); }
        });
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

        // Reset hydrated pending items to Unseen so the node doesn't inherit master-mode queue state.
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

        ClearWorkItems();

        // The window above is only the hydrated top of the queue — bulk-reset the
        // rest of the DB backlog or it re-hydrates straight back into the window.
        int resetRows = 0;
        try { resetRows = await _mediaFileRepo.ResetAllQueuedAsync(); }
        catch (Exception ex) { Console.WriteLine($"StopAndClearQueue: bulk queue reset failed: {ex.Message}"); }
        MarkQueueWindowDirty();
        await NotifyQueueChangedAsync();

        Console.WriteLine($"Cluster: Queue cleared ({pendingItems.Count} hydrated + {resetRows} backlog rows stopped)");
    }

    /// <summary>
    ///     Starts (or wakes) the queue scheduler under the given options. The DB-first
    ///     replacement for the old per-row restore: startup just marks the window
    ///     dirty and calls this once — Queued rows hydrate on demand. Also wakes the
    ///     cluster dispatcher so workers can pull straight from the refilled window.
    /// </summary>
    public Task KickQueueAsync(EncoderOptions options)
    {
        _lastOptions ??= options;
        SetQueueOrderNewestFirst(options.QueueNewestFirst);
        try { _onWorkItemQueued?.Invoke(); } catch { }
        _ = Task.Run(async () =>
        {
            try { await ProcessQueueAsync(options); }
            catch (Exception ex) { Console.WriteLine($"Error in ProcessQueueAsync after kick: {ex.Message}"); }
        });
        return Task.CompletedTask;
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
        ClearWorkItems();

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
                // Item left the window — flag it so the next sync refills from the DB.
                Interlocked.Exchange(ref _queueWindowDirty, 1);
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

        // Re-register unconditionally: the terminal-item memory sweep may have
        // evicted this item from _workItems while a cluster failure path was
        // deciding to revive it. A queue entry without a dictionary entry is
        // invisible to cancel/logs/duplicate-detection — heal it here rather
        // than trying to make sweep-vs-requeue atomic.
        RegisterWorkItem(item);

        lock (_queueLock)
        {
            _workQueue.Add(item);
            _workQueue.Sort((a, b) => CompareQueueOrder(a, b, _queueNewestFirst));
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
    public async Task HandleRemoteCompletion(WorkItem workItem, string outputPath, EncoderOptions options, bool videoCopy = false)
    {
        var outputSize = new FileInfo(outputPath).Length;
        float savings = (workItem.Size - outputSize) / 1048576f;
        float percent = 1 - ((float)outputSize / workItem.Size);
        workItem.OutputSize = outputSize;

        Console.WriteLine($"Cluster: Remote encode of {workItem.FileName}: {FormatSize(workItem.Size)} → {FormatSize(outputSize)} (saved {FormatSize((long)(savings * 1048576))}, {percent:P0})");

        // Shared keep/delete decision with the local path. videoCopy is the worker's actual
        // ffmpeg-copy outcome propagated through JobCompletion; passing it (rather than letting
        // the helper recompute IsMuxPass) catches the HEVC-at-target-bitrate copy case that
        // CalculateBitrates enables independently of EncodingMode.
        var (keep, reason) = ShouldKeepEncodedOutput(options, workItem, workItem.Size, outputSize, videoCopy);

        if (keep)
        {
            await HandleOutputPlacement(outputPath, workItem, options);
            workItem.Status = WorkItemStatus.Completed;
            workItem.CompletedAt = DateTime.UtcNow;
            workItem.Progress = 100;
            workItem.ErrorMessage = null;
            workItem.RemoteJobPhase = null;
            await _mediaFileRepo.SetStatusAndLastEncodedAtAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed, DateTime.UtcNow);
            _log?.LogInformation(
                "RemoteEncodeKept jobId={JobId} fileName={FileName} sourceSize={SourceSize} outputSize={OutputSize} reason={Reason} videoCopy={VideoCopy}",
                workItem.Id, workItem.FileName, workItem.Size, outputSize, reason, videoCopy);
            if (reason != "savings")
                Console.WriteLine($"Cluster: Kept {workItem.FileName} despite no savings — {reason}");
        }
        else
        {
            Console.WriteLine($"Cluster: No savings for {workItem.FileName}, deleting output");
            // Master-side no-savings decision (worker reported the output, master recomputed
            // and chose to drop). Logged structurally so the ops log captures the empirical
            // outcome that drives the NoSavings status — the same row Re-evaluate now respects
            // unless the user opts into "Retry no-savings encodes."
            _log?.LogInformation(
                "RemoteEncodeNoSavings source={Source} jobId={JobId} fileName={FileName} sourceSize={SourceSize} outputSize={OutputSize} videoCopy={VideoCopy}",
                "master", workItem.Id, workItem.FileName, workItem.Size, outputSize, videoCopy);
            try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            // WorkItem.Status = NoSavings keeps the queue tile honest (was Completed before,
            // which lied to the user when the DB row said Skipped). MediaFile.NoSavings is the
            // sticky empirical outcome that re-evaluate skips by default.
            workItem.Status = WorkItemStatus.NoSavings;
            workItem.CompletedAt = DateTime.UtcNow;
            workItem.OutputSize = outputSize;
            workItem.Progress = 100;
            workItem.ErrorMessage = null;
            workItem.RemoteJobPhase = null;
            await _mediaFileRepo.SetStatusAndLastEncodedAtAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.NoSavings, DateTime.UtcNow);
        }

        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

        if (ShouldDispatchExternal)
        {
            if (_notificationService != null)
                // workItem.Path may have been deleted by HandleOutputPlacement when
                // DeleteOriginalFile=true and the source/output extensions differ
                // (e.g. .mp4 → .mkv) — reading FileInfo.Length here would throw
                // FileNotFoundException and surface as "Finalize failed: Could not find file …".
                _ = _notificationService.NotifyEncodeCompletedAsync(Path.GetFileName(workItem.Path), workItem.OutputSize);
            if (_integrationService != null && keep)
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
                Kind = _fileService.GetMediaKind(filePath) ?? MediaKind.Video,
                Probe = probe,
                Status = WorkItemStatus.Processing,
                StartedAt = DateTime.UtcNow
            };

            RegisterWorkItem(workItem);
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
    ///     Registers the work item so logging works, then delegates to <see cref="ConvertVideoAsync"/>
    ///     with <c>skipPlacement: true</c>.
    ///
    ///     <para>The worker does not run <see cref="HandleOutputPlacement"/> — it has no view of the
    ///     master's library and the in-place <c>DeleteOriginalFile=true</c> branch would strip the
    ///     <c>[snacks]</c> tag from the file in the worker's temp dir. The master then runs
    ///     <see cref="GetOutputFileForJob"/> with a <c>*[snacks]*</c> glob and gets <see langword="null"/>,
    ///     reports false noSavings, and the job vanishes into <c>Skipped</c>. Placement is the master's
    ///     job after download.</para>
    ///     The work item is kept in the dictionary after completion so the output remains accessible for download.
    /// </summary>
    /// <param name="workItem">The work item to encode.</param>
    /// <param name="options">Encoding options from the master's job assignment.</param>
    /// <param name="cancellationToken">Token to abort encoding if the job is cancelled.</param>
    public async Task ConvertVideoForRemoteAsync(WorkItem workItem, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        RegisterWorkItem(workItem);

        try
        {
            await ConvertVideoAsync(workItem, options, skipPlacement: true, cancellationToken: cancellationToken);
        }
        finally
        {
            // Don't remove from _workItems — the output file needs to remain accessible
        }
    }

    /// <summary>
    ///     Music counterpart to <see cref="ConvertVideoForRemoteAsync"/>. Steers
    ///     the encoder's output into the worker's scratch dir
    ///     (<see cref="EncoderOptions.EncodeDirectory"/>) so the master's
    ///     <c>GetOutputFileForJob</c> glob (<c>*[snacks]*</c>) finds it, and
    ///     forces <c>DeleteOriginalFile=false</c> so the worker never touches
    ///     the master-uploaded source — placement and source lifecycle are the
    ///     master's job.
    /// </summary>
    /// <param name="workItem">The music work item to encode.</param>
    /// <param name="options">Encoding options from the master's job assignment. Mutated: Music.OutputDirectory and Music.DeleteOriginalFile are overridden.</param>
    /// <param name="cancellationToken">Token to abort encoding if the job is cancelled.</param>
    public async Task ConvertMusicForRemoteAsync(WorkItem workItem, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        RegisterWorkItem(workItem);

        if (!string.IsNullOrEmpty(options.EncodeDirectory))
            options.Music.OutputDirectory = options.EncodeDirectory;
        options.Music.DeleteOriginalFile = false;

        try
        {
            await ConvertMusicAsync(workItem, options, cancellationToken);
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
        // Route FileService retry messages into the work-item log so a multi-minute
        // backoff against an external lock (AV, indexer) is visible to the user instead
        // of looking like a silent hang followed by a generic failure.
        Func<string, Task> log = msg => LogAsync(workItem.Id, msg);

        try
        {
            if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                // Output already in the right directory (GetOutputPath used OutputDirectory)
                // If EncodeDirectory was used, move from there to OutputDirectory
                if (!string.IsNullOrEmpty(options.EncodeDirectory))
                {
                    string finalSnacksPath = Path.Combine(options.OutputDirectory, Path.GetFileName(outputPath));
                    await LogAsync(workItem.Id, $"Moving to output directory: {outputPath} -> {finalSnacksPath}");
                    await _fileService.FileMoveAsync(outputPath, finalSnacksPath, log);
                    await MoveSidecarsAlongsideAsync(outputPath, finalSnacksPath, workItem);
                    outputPath = finalSnacksPath;
                }

                if (options.DeleteOriginalFile)
                {
                    // Replace original: delete it, then move encoded file back to original location
                    await LogAsync(workItem.Id, "Replacing original file");
                    await LogAsync(workItem.Id, $"Deleting original: {workItem.Path}");
                    await _fileService.FileDeleteAsync(workItem.Path, log);

                    // Move back to the original's directory with a clean name (no [snacks] tag)
                    string originalDir = _fileService.GetDirectory(workItem.Path);
                    string cleanName = Path.GetFileNameWithoutExtension(outputPath).Replace(" [snacks]", "") + Path.GetExtension(outputPath);
                    string finalPath = Path.Combine(originalDir, cleanName);
                    await LogAsync(workItem.Id, $"Moving encoded output: {outputPath} -> {finalPath}");
                    await _fileService.FileMoveAsync(outputPath, finalPath, log);
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
                // No OutputDirectory configured — final destination is the original's directory.
                // The staged file may be alongside the original, or in EncodeDirectory if scratch
                // was used; either way, route the move target through workItem.Path's directory so
                // we don't strand the encode on the scratch volume.
                string originalDir = _fileService.GetDirectory(workItem.Path);

                if (options.DeleteOriginalFile)
                {
                    // Replace original: delete it and rename transcoded file to take its place
                    await LogAsync(workItem.Id, "Replacing original with transcoded version");
                    await LogAsync(workItem.Id, $"Deleting original: {workItem.Path}");
                    await _fileService.FileDeleteAsync(workItem.Path, log);

                    string cleanName = Path.GetFileNameWithoutExtension(outputPath).Replace(" [snacks]", "") + Path.GetExtension(outputPath);
                    string finalPath = Path.Combine(originalDir, cleanName);
                    await LogAsync(workItem.Id, $"Moving encoded output: {outputPath} -> {finalPath}");
                    await _fileService.FileMoveAsync(outputPath, finalPath, log);
                    await MoveSidecarsAlongsideAsync(outputPath, finalPath, workItem);
                    await LogAsync(workItem.Id, $"Final output: {finalPath}");
                }
                else
                {
                    // Keep both — original untouched, transcoded file keeps [snacks] tag.
                    // If staged in EncodeDirectory, move it next to the original so the user
                    // isn't surprised to find their encode on the scratch drive.
                    if (!string.IsNullOrEmpty(options.EncodeDirectory))
                    {
                        string finalPath = Path.Combine(originalDir, Path.GetFileName(outputPath));
                        await LogAsync(workItem.Id, $"Moving encoded output: {outputPath} -> {finalPath}");
                        await _fileService.FileMoveAsync(outputPath, finalPath, log);
                        await MoveSidecarsAlongsideAsync(outputPath, finalPath, workItem);
                        outputPath = finalPath;
                    }

                    await LogAsync(workItem.Id,
                        $"Original kept at: {workItem.Path}");
                    await LogAsync(workItem.Id,
                        $"Transcoded file at: {outputPath}");
                }
            }
        }
        catch (Exception ex)
        {
            // Include the exception type and inner exception (if any) so future failure reports
            // distinguish e.g. IOException sharing-violation from access-denied or disk-full,
            // and the preceding "Deleting original: ..." / "Moving encoded output: ..." log line
            // identifies which file operation was in flight.
            string suffix = ex.InnerException != null ? $" -> {ex.InnerException.Message}" : "";
            await LogAsync(workItem.Id, $"Error handling output placement [{ex.GetType().Name}]: {ex.Message}{suffix}");
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