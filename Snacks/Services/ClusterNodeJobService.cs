namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

/// <summary>
///     Encapsulates all node-side job execution logic for the distributed encoding cluster.
///     Handles receiving, encoding, completion tracking, retry, and cleanup of remote jobs
///     dispatched by the master node.
///
///     <para>This service is <b>not</b> an <see cref="IHostedService"/>; it is owned and
///     driven by <see cref="ClusterService"/>.</para>
/// </summary>
public sealed class ClusterNodeJobService
{
    /******************************************************************
     *  Dependencies
     ******************************************************************/

    private readonly TranscodingService                        _transcodingService;
    private readonly IHubContext<TranscodingHub>                _hubContext;
    private readonly IHttpClientFactory                        _httpClientFactory;
    private readonly ClusterDiscoveryService                   _discoveryService;
    private readonly ConcurrentDictionary<string, ClusterNode> _nodes;
    private readonly IntegrationService                        _integrationService;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /******************************************************************
     *  Cross-Thread State
     ******************************************************************/

    /// <summary>
    ///     One entry per in-flight encode. Each job is bound to a
    ///     <see cref="HardwareDevice.DeviceId"/> slot the node consumed when
    ///     accepting the work, and carries its own cancellation source so a
    ///     master-issued cancel only kills that specific job — its peers on
    ///     other devices keep encoding.
    /// </summary>
    private sealed class ActiveRemoteJob
    {
        public WorkItem                 Item     = null!;
        public string                   DeviceId = "";
        public CancellationTokenSource  Cts      = null!;
    }

    /// <summary> Active encodes keyed by job ID. Replaces the prior single <c>_currentRemoteJob</c>. </summary>
    private readonly ConcurrentDictionary<string, ActiveRemoteJob> _activeJobs = new();

    /// <summary>
    ///     Job IDs whose encode has finished but whose output the master has not
    ///     yet downloaded. Multi-entry so several completed jobs can sit in the
    ///     "ready for download" state simultaneously.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _completedJobIds = new();

    /// <summary>
    ///     Job IDs currently receiving chunks from the master. Value is the UTC
    ///     timestamp of the last chunk activity so <see cref="ExpireStaleReceiving"/>
    ///     can prune dead uploads independently per-job.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _receivingJobIds = new();

    /// <summary>
    ///     Per-device slot pools. Tracks active slot count against a max
    ///     capacity that grows on demand to honor the master's effective
    ///     concurrency setting (<see cref="JobMetadata.DeviceMaxConcurrency"/>).
    ///     The master is the primary scheduler; this is the node's safety net,
    ///     ensuring even a misbehaving master can never push more concurrent
    ///     encodes onto a device than the worker is willing to run — but
    ///     unlike a fixed-size <see cref="SemaphoreSlim"/>, it can't silently
    ///     reject a job the master legitimately scheduled under a higher cap.
    /// </summary>
    private sealed class DeviceSlotPool
    {
        public int Used;
        public int Max;
        public readonly object Lock = new();
    }
    private readonly ConcurrentDictionary<string, DeviceSlotPool> _deviceSlotPools = new();

    private volatile bool _nodePaused;

    private readonly ConcurrentDictionary<string, SemaphoreSlim>           _receiveLocks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _receiveCts   = new();

    /******************************************************************
     *  Pending Completions Persistence
     ******************************************************************/

    private readonly string        _pendingCompletionsPath;
    private readonly SemaphoreSlim _pendingCompletionsLock = new(1, 1);

    /******************************************************************
     *  Configuration
     ******************************************************************/

    /// <summary>
    ///     The current cluster configuration. Set by <see cref="ClusterService"/> whenever
    ///     the configuration is loaded or changed.
    /// </summary>
    public ClusterConfig Config { get; set; } = new();

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Creates a new node-side job service with all required dependencies.
    /// </summary>
    /// <param name="transcodingService">Performs the actual video encoding.</param>
    /// <param name="hubContext">SignalR hub used to broadcast UI updates.</param>
    /// <param name="httpClientFactory">Factory for HTTP clients used to communicate with the master.</param>
    /// <param name="discoveryService">Provides local IP address and port resolution.</param>
    /// <param name="nodes">Shared node registry for locating the master when no explicit URL is configured.</param>
    public ClusterNodeJobService(
        TranscodingService                        transcodingService,
        IHubContext<TranscodingHub>                hubContext,
        IHttpClientFactory                        httpClientFactory,
        ClusterDiscoveryService                   discoveryService,
        ConcurrentDictionary<string, ClusterNode> nodes,
        IntegrationService                        integrationService)
    {
        _transcodingService = transcodingService;
        _hubContext         = hubContext;
        _httpClientFactory  = httpClientFactory;
        _discoveryService   = discoveryService;
        _nodes              = nodes;
        _integrationService = integrationService;

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        _pendingCompletionsPath = Path.Combine(workDir, "config", "pending-completions.json");
    }

    /******************************************************************
     *  Node Pause
     ******************************************************************/

    /// <summary> Whether this node is paused and not accepting new jobs. </summary>
    public bool IsNodePaused => _nodePaused;

    /// <summary>
    ///     Pauses or resumes this node's ability to accept new jobs and broadcasts the
    ///     state change to all connected SignalR clients.
    /// </summary>
    /// <param name="paused">When <see langword="true"/>, the node will not accept new job offers.</param>
    public void SetNodePaused(bool paused)
    {
        _nodePaused = paused;
        Console.WriteLine($"Cluster: Node {(paused ? "paused" : "resumed")}");
        _ = _hubContext.Clients.All.SendAsync("ClusterNodePaused", paused);
    }

    /******************************************************************
     *  Job Status Queries
     ******************************************************************/

    /// <summary>
    ///     Returns <see langword="true"/> when this node has any active, receiving, or
    ///     recently-completed remote job tracked in memory.
    /// </summary>
    public bool IsProcessingRemoteJob() =>
        !_activeJobs.IsEmpty || !_receivingJobIds.IsEmpty || !_completedJobIds.IsEmpty;

    /// <summary>
    ///     Returns one <see cref="ActiveJobInfo"/> per occupied slot on this
    ///     node. Multi-slot masters consume this in heartbeats to reconcile
    ///     their optimistic per-device slot accounting against ground truth.
    /// </summary>
    public List<ActiveJobInfo> GetActiveJobs()
    {
        var list = new List<ActiveJobInfo>();
        foreach (var kv in _activeJobs)
        {
            list.Add(new ActiveJobInfo
            {
                JobId    = kv.Key,
                DeviceId = kv.Value.DeviceId,
                FileName = kv.Value.Item.FileName,
                Progress = kv.Value.Item.Progress,
                Phase    = kv.Value.Item.Status == WorkItemStatus.Downloading ? "Downloading" : "Encoding",
            });
        }
        foreach (var kv in _receivingJobIds)
        {
            // Receiving slots haven't acquired a device yet — surface them
            // separately so the master can render an "Uploading" tile without
            // double-counting against any device's slot pool.
            if (_activeJobs.ContainsKey(kv.Key)) continue;
            list.Add(new ActiveJobInfo
            {
                JobId    = kv.Key,
                DeviceId = "",
                FileName = null,
                Progress = 0,
                Phase    = "Receiving",
            });
        }
        return list;
    }

    /// <summary> Snapshot of all completed-but-not-downloaded job IDs. </summary>
    public List<string> GetCompletedJobIds() => _completedJobIds.Keys.ToList();

    /// <summary> Snapshot of all receiving job IDs. </summary>
    public List<string> GetReceivingJobIds() => _receivingJobIds.Keys.ToList();

    /// <summary>
    ///     Returns a per-job semaphore that serializes concurrent ReceiveFile requests.
    ///     Prevents file-lock collisions when a retry arrives before the previous request's
    ///     FileStream has been disposed.
    /// </summary>
    public SemaphoreSlim GetReceiveLock(string jobId) =>
        _receiveLocks.GetOrAdd(jobId, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    ///     Cancels any in-flight ReceiveFile request for this job and returns a fresh
    ///     <see cref="CancellationToken"/> for the new request. The old handler's
    ///     body-read will throw <see cref="OperationCanceledException"/>, releasing the
    ///     file handle and the per-job semaphore so the retry can proceed.
    /// </summary>
    public CancellationToken SwapReceiveCts(string jobId)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = _receiveCts.AddOrUpdate(jobId, newCts, (_, old) => { old.Cancel(); return newCts; });
        return newCts.Token;
    }

    /// <summary> Removes the receive lock and cancellation source for a completed/cleaned-up job. </summary>
    public void RemoveReceiveLock(string jobId)
    {
        _receiveLocks.TryRemove(jobId, out _);
        if (_receiveCts.TryRemove(jobId, out var cts)) cts.Dispose();
        _receivingJobIds.TryRemove(jobId, out _);
    }

    /// <summary>
    ///     Drops any receiving state whose last chunk activity is older than
    ///     <paramref name="timeout"/>. Per-jobId so a stalled upload on one slot
    ///     can be expired without disturbing healthy receives on other slots.
    /// </summary>
    public void ExpireStaleReceiving(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        foreach (var kv in _receivingJobIds)
        {
            if (_activeJobs.ContainsKey(kv.Key)) continue;            // already encoding — not stale
            if (kv.Value > cutoff)              continue;             // recent activity
            if (_receivingJobIds.TryRemove(kv.Key, out _))
                Console.WriteLine($"Cluster: Cleared stale receiving state for job {kv.Key} (no activity for {timeout.TotalSeconds:0}s)");
            // Master owns lifecycle messaging — no synthetic completion broadcast.
        }
    }

    /// <summary>
    ///     Records that <paramref name="jobId"/> is actively receiving chunks
    ///     and stamps its last-activity time. Multi-slot safe: multiple jobs
    ///     can be in the receiving state simultaneously.
    /// </summary>
    /// <param name="jobId">The job ID being received.</param>
    public void SetReceivingJob(string? jobId)
    {
        if (jobId == null) return;
        _receivingJobIds[jobId] = DateTime.UtcNow;
    }

    /******************************************************************
     *  Job Acceptance
     ******************************************************************/

    /// <summary>
    ///     Starts autonomous encoding after a file upload completes.
    ///     Validates the file, claims a device slot, and begins encoding in a background task.
    ///     The slot to use is taken from <see cref="JobMetadata.DeviceId"/>; older masters
    ///     that don't yet send a device hint fall back to the worker's primary GPU
    ///     (or CPU if no GPU is detected).
    /// </summary>
    /// <param name="jobId">The job ID that was uploaded.</param>
    /// <param name="metadata">The job metadata sent with the upload.</param>
    /// <param name="filePath">The local path where the file was saved.</param>
    /// <returns><see langword="true"/> if encoding was started; <see langword="false"/> otherwise.</returns>
    public async Task<(bool Started, string? RejectReason)> StartAutonomousEncodingAsync(string jobId, JobMetadata metadata, string filePath)
    {
        if (_nodePaused)
            return (false, "Node is paused");

        // Resolve which device slot the master assigned. A null DeviceId means
        // the master is older than this build — fall back to whichever device
        // we'd pick by default (primary detected hardware, else CPU).
        var deviceId = ResolveDeviceId(metadata.DeviceId);
        if (deviceId == null)
            return (false, $"Device {metadata.DeviceId} is not available on this worker");

        // Re-running the same job (e.g. master crash + recovery) shouldn't
        // queue behind itself — we already hold the slot.
        if (_activeJobs.ContainsKey(jobId))
            return (false, $"Job {jobId} is already encoding on this node");

        // Try to acquire one of the device's slots. If every slot is busy,
        // reject — the master is the primary scheduler and shouldn't have
        // over-dispatched, but this guard catches drift between its optimistic
        // accounting and the node's actual state. The pool's cap grows to honor
        // the master's effective concurrency, so a legitimate higher dispatch
        // doesn't get rejected by a stale fixed-size semaphore.
        var slotPool = GetOrCreateDeviceSlotPool(deviceId, metadata.DeviceMaxConcurrency);
        if (!TryAcquireSlot(slotPool))
            return (false, $"All {deviceId} slots are busy on this node");

        bool slotHandedOff = false;

        try
        {
            if (!File.Exists(filePath))
                return (false, $"File not found at {filePath}");

            // Output already on disk (master crash + recovery) — short-circuit
            // to the completed-jobs set so the master's download path picks it up.
            var existingOutput = GetOutputFileForJob(jobId);
            if (existingOutput != null)
            {
                Console.WriteLine($"Cluster: Output already exists for {metadata.FileName} — skipping encode, ready for download");
                _completedJobIds[jobId] = 0;
                _receivingJobIds.TryRemove(jobId, out _);
                return (true, null);
            }

            var actualSize = new FileInfo(filePath).Length;
            if (actualSize != metadata.FileSize)
                return (false, $"File size mismatch — expected {metadata.FileSize}, got {actualSize}");

            // Most common failure mode after an interrupted chunk write is a
            // run of null bytes at the start of the file — catch it before we
            // burn cycles on a doomed encode.
            try
            {
                var header = new byte[4];
                using var checkStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var headerRead = await checkStream.ReadAsync(header);
                if (headerRead >= 4 && header[0] == 0x00 && header[1] == 0x00 && header[2] == 0x00 && header[3] == 0x00)
                    return (false, $"File corrupt — first 4 bytes are 0x00000000 (expected container header). " +
                        $"Size on disk: {actualSize}, expected: {metadata.FileSize}");
            }
            catch { }

            var workItem = new WorkItem
            {
                Id        = jobId,
                FileName  = metadata.FileName,
                Path      = filePath,
                Size      = metadata.FileSize,
                Bitrate   = metadata.Bitrate,
                Length    = metadata.Duration,
                IsHevc    = metadata.IsHevc,
                Is4K      = metadata.Is4K,
                Probe     = metadata.Probe,
                Status    = WorkItemStatus.Processing,
                StartedAt = DateTime.UtcNow,
            };

            var active = new ActiveRemoteJob
            {
                Item     = workItem,
                DeviceId = deviceId,
                Cts      = new CancellationTokenSource(),
            };

            // Register before launching so the very next heartbeat sees it.
            _activeJobs[jobId] = active;
            _receivingJobIds.TryRemove(jobId, out _);

            // The slot stays held for the lifetime of the encode — the
            // background task releases it in a finally block.
            slotHandedOff = true;
            _ = Task.Run(() => ExecuteRemoteJobAsync(active, metadata.Options, slotPool));

            return (true, null);
        }
        finally
        {
            if (!slotHandedOff)
                ReleaseSlot(slotPool);
        }
    }

    /// <summary>
    ///     Picks the device slot to use for an incoming job. Honors the master's
    ///     <see cref="JobMetadata.DeviceId"/> when set and present on this worker;
    ///     otherwise falls back to the first detected hardware device (or CPU).
    ///     Returns <see langword="null"/> if the master named a device the worker
    ///     does not have.
    /// </summary>
    private string? ResolveDeviceId(string? requested)
    {
        var devices = _transcodingService.GetDetectedDevices();
        if (devices.Count == 0)
            return "cpu"; // detection hasn't completed; CPU is always safe

        if (!string.IsNullOrEmpty(requested))
            return devices.Any(d => d.DeviceId == requested) ? requested : null;

        // Older master — pick first hardware device, else CPU.
        return devices.FirstOrDefault(d => d.IsHardware)?.DeviceId
            ?? devices.First().DeviceId;
    }

    /// <summary>
    ///     Lazily creates a slot pool for the given device. Initial capacity
    ///     comes from the master's <paramref name="masterMax"/> hint when
    ///     supplied, falling back to the device's reported
    ///     <see cref="HardwareDevice.DefaultConcurrency"/> for legacy masters.
    ///     If the same device is later asked to honor a larger cap, the pool
    ///     grows to match — never shrinks below the current high-water Max so
    ///     in-flight jobs never lose a reservation.
    /// </summary>
    private DeviceSlotPool GetOrCreateDeviceSlotPool(string deviceId, int? masterMax)
    {
        var pool = _deviceSlotPools.GetOrAdd(deviceId, id =>
        {
            var device = _transcodingService.GetDetectedDevices().FirstOrDefault(d => d.DeviceId == id);
            var capacity = Math.Max(1, masterMax ?? device?.DefaultConcurrency ?? 1);
            return new DeviceSlotPool { Max = capacity };
        });

        if (masterMax.HasValue)
        {
            lock (pool.Lock)
            {
                if (masterMax.Value > pool.Max) pool.Max = masterMax.Value;
            }
        }

        return pool;
    }

    /// <summary> Attempts to claim a slot from the pool. <see langword="false"/> when full. </summary>
    private static bool TryAcquireSlot(DeviceSlotPool pool)
    {
        lock (pool.Lock)
        {
            if (pool.Used >= pool.Max) return false;
            pool.Used++;
            return true;
        }
    }

    /// <summary> Returns a slot to the pool. Idempotent against double-release via the floor. </summary>
    private static void ReleaseSlot(DeviceSlotPool pool)
    {
        lock (pool.Lock)
        {
            if (pool.Used > 0) pool.Used--;
        }
    }

    /******************************************************************
     *  Job Execution
     ******************************************************************/

    /// <summary>
    ///     Runs the full encoding pipeline for a remote job. Per-job state — work
    ///     item, device assignment, cancellation source, slot semaphore — is
    ///     scoped to the supplied <see cref="ActiveRemoteJob"/>; multiple
    ///     instances of this method can run concurrently for different jobs on
    ///     the same node, each owning a different device slot.
    /// </summary>
    /// <param name="active">The active job descriptor including work item, device, and CTS.</param>
    /// <param name="options">Encoder options dictating codec, quality, and hardware settings.</param>
    /// <param name="slotPool">Device slot pool to release the claimed slot back to when the job finishes.</param>
    private async Task ExecuteRemoteJobAsync(ActiveRemoteJob active, EncoderOptions options, DeviceSlotPool slotPool)
    {
        var workItem  = active.Item;
        var masterUrl = ResolveMasterUrl();

        // The master assigned this job to a specific device slot — pin the
        // hardware acceleration to that device family so the encode lands
        // where the scheduler intended (and not on whichever device "auto"
        // would have picked locally). CPU jobs map to "none".
        options.HardwareAcceleration = active.DeviceId == "cpu" ? "none" : active.DeviceId;

        var encodingSucceeded = false;
        try
        {
            var tempDir = GetNodeTempDirectory(workItem.Id);
            options.OutputDirectory     = null;
            options.EncodeDirectory     = tempDir;
            options.DeleteOriginalFile  = false;

            Console.WriteLine($"Cluster: Encoding {workItem.FileName} on {active.DeviceId} — " +
                $"EncodingMode={options.EncodingMode}, MuxStreams={options.MuxStreams}, " +
                $"Is4K={workItem.Is4K}, IsHevc={workItem.IsHevc}, Bitrate={workItem.Bitrate}kbps");

            // Pull the master's integration credentials before every encode so
            // KeepOriginalLanguage lookups and OCR on this worker use the master's
            // Sonarr / Radarr / TVDB / TMDb config, not whatever is cached locally.
            await PullIntegrationsFromMasterAsync(active.Cts.Token);

            // Per-job log buffer + reporter. Each concurrent encode keeps its
            // own buffer and lastLogSend timestamp so cross-job log lines never
            // mix into a single POST.
            var logBuffer   = new ConcurrentQueue<string>();
            var lastLogSend = DateTime.MinValue;

            _transcodingService.SetLogCallback(workItem.Id, async (id, message) =>
            {
                logBuffer.Enqueue(message);

                var now = DateTime.UtcNow;
                if ((now - lastLogSend).TotalSeconds < 2) return;
                lastLogSend = now;

                var lines = new List<string>();
                while (logBuffer.TryDequeue(out var line)) lines.Add(line);
                if (lines.Count == 0 || masterUrl == null) return;

                try
                {
                    var client         = CreateAuthenticatedClient();
                    var progressReport = new JobProgress
                    {
                        JobId    = id,
                        Progress = workItem.Progress,
                        Phase    = "Encoding",
                        LogLine  = string.Join("\n", lines)
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(progressReport, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                }
                catch { }
            });

            _transcodingService.SetProgressCallback(workItem.Id, async (id, progress) =>
            {
                if (masterUrl == null) return;
                try
                {
                    var client         = CreateAuthenticatedClient();
                    var progressReport = new JobProgress
                    {
                        JobId    = id,
                        Progress = progress,
                        Phase    = "Encoding"
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(progressReport, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                }
                catch { }
            });

            await _transcodingService.ConvertVideoForRemoteAsync(workItem, options, active.Cts.Token);
            encodingSucceeded = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Remote job encoding failed for {workItem.Id}: {ex.Message}");

            // Tear down per-job state BEFORE reporting failure to master — the
            // master reacts immediately by freeing this slot and dispatching new
            // work. If we report first and clean up after, the next upload can
            // arrive while we still hold the slot.
            ReleaseJobState(workItem.Id, slotPool);

            if (masterUrl != null)
            {
                try
                {
                    var client  = CreateAuthenticatedClient();
                    var failure = new { jobId = workItem.Id, errorMessage = ex.Message };
                    var content = new StringContent(
                        JsonSerializer.Serialize(failure, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/failed", content);
                }
                catch { }
            }
        }

        // No-savings path: ConvertAsync deletes the output when it isn't smaller
        // than the source. Detect that before reporting completion so the master
        // skips the download phase.
        var noSavings = encodingSucceeded && GetOutputFileForJob(workItem.Id) == null;
        if (noSavings)
            Console.WriteLine($"Cluster: Encoding succeeded for {workItem.FileName} but no savings — will notify master to skip download");

        try
        {
            if (encodingSucceeded && !noSavings)
            {
                // Zero TransferProgress and set RemoteJobPhase so the UI renders
                // the download bar from 0% instead of flashing Progress=100
                // through the encode-progress field.
                workItem.Progress         = 100;
                workItem.Status           = WorkItemStatus.Downloading;
                workItem.RemoteJobPhase   = "Downloading";
                workItem.TransferProgress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                _completedJobIds[workItem.Id] = 0;
            }

            ReleaseJobState(workItem.Id, slotPool);
        }
        catch { }

        // Report completion OUTSIDE the try/catch — encoding succeeded,
        // so even if this POST fails, the output file still exists on disk
        // and the master can discover it via heartbeat or recovery
        if (encodingSucceeded && masterUrl != null)
        {
            if (!noSavings)
                await PersistCompletedJobAsync(workItem.Id, masterUrl, selfUrl: null, outputFileName: workItem.FileName);

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    var client  = CreateAuthenticatedClient();
                    var selfUrl = $"{(Config.UseHttps ? "https" : "http")}://{ClusterDiscoveryService.GetLocalIpAddress()}:{_discoveryService.GetListeningPort()}";
                    var completion = new JobCompletion
                    {
                        JobId          = workItem.Id,
                        Success        = true,
                        NoSavings      = noSavings,
                        OutputFileName = workItem.FileName
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { completion, nodeBaseUrl = selfUrl }, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/complete", content);
                    Console.WriteLine($"Cluster: Reported {(noSavings ? "no-savings" : "completion")} for {workItem.FileName} to master");

                    if (!noSavings)
                        await RemoveCompletedJobAsync(workItem.Id);
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cluster: Failed to report completion (attempt {attempt + 1}): {ex.Message}");
                    if (attempt < 9)
                        await Task.Delay(TimeSpan.FromSeconds(10));
                }
            }

            // Clean up temp files immediately for no-savings jobs since there's nothing to download
            if (noSavings)
                CleanupJobFiles(workItem.Id);
        }
    }

    /******************************************************************
     *  Job Cancellation
     ******************************************************************/

    /// <summary>
    ///     Cancels a specific job running on this node. Looks up the per-job
    ///     cancellation source by ID — peer jobs on other slots keep encoding.
    /// </summary>
    /// <param name="jobId">The ID of the job to cancel.</param>
    public void CancelRemoteJob(string jobId)
    {
        if (_activeJobs.TryGetValue(jobId, out var active))
        {
            Console.WriteLine($"Cluster: Cancelling remote job {jobId} on {active.DeviceId}");
            try { active.Cts.Cancel(); } catch { }
        }
    }

    /// <summary>
    ///     Releases all per-job state for a finished or failed encode: removes
    ///     the active-job entry, clears its callbacks on the transcoding
    ///     service, disposes its CTS, and returns its device slot to the pool
    ///     so the next dispatched job for that device can proceed. Idempotent.
    /// </summary>
    private void ReleaseJobState(string jobId, DeviceSlotPool slotPool)
    {
        if (_activeJobs.TryRemove(jobId, out var active))
        {
            try { active.Cts.Dispose(); } catch { }
        }
        _transcodingService.SetProgressCallback(jobId, null);
        _transcodingService.SetLogCallback(jobId, null);
        ReleaseSlot(slotPool);
    }

    /******************************************************************
     *  Pending Completions Persistence
     ******************************************************************/

    /// <summary>
    ///     Appends a completed job record to the pending-completions file so the completion
    ///     can be re-reported on every heartbeat until the master acknowledges it.
    /// </summary>
    /// <param name="jobId">The completed job ID to persist.</param>
    /// <param name="masterUrl">The master's base URL, stored for retry requests.</param>
    /// <param name="selfUrl">This node's base URL, included in completion callbacks.</param>
    public async Task PersistCompletedJobAsync(string jobId, string masterUrl, string? selfUrl, string? outputFileName = null)
    {
        await _pendingCompletionsLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingCompletionsPath)!);
            var completions = await LoadPendingCompletionsInternalAsync();
            if (!completions.ContainsKey(jobId))
            {
                completions[jobId] = new PendingCompletion
                {
                    JobId          = jobId,
                    MasterUrl      = masterUrl,
                    OutputFileName = outputFileName
                                  ?? (_activeJobs.TryGetValue(jobId, out var act) ? act.Item.FileName : null)
                                  ?? "",
                    Timestamp      = DateTime.UtcNow
                };
                var json = JsonSerializer.Serialize(completions.Values, _jsonOptions);
                await File.WriteAllTextAsync(_pendingCompletionsPath, json);
                Console.WriteLine($"Cluster: Persisted completed job {jobId} for retry");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to persist completed job {jobId}: {ex.Message}");
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }
    }

    /// <summary>
    ///     Removes a job from the pending-completions file once the master has acknowledged it.
    /// </summary>
    /// <param name="jobId">The acknowledged job ID to remove.</param>
    public async Task RemoveCompletedJobAsync(string jobId)
    {
        await _pendingCompletionsLock.WaitAsync();
        try
        {
            var completions = await LoadPendingCompletionsInternalAsync();
            if (completions.Remove(jobId))
            {
                var json = JsonSerializer.Serialize(completions.Values, _jsonOptions);
                await File.WriteAllTextAsync(_pendingCompletionsPath, json);
                Console.WriteLine($"Cluster: Removed acknowledged job {jobId} from pending completions");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to remove completed job {jobId}: {ex.Message}");
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }
    }

    /// <summary>
    ///     Loads the pending-completions JSON file, returning an empty dictionary if the
    ///     file does not exist or is corrupt. Must be called under <see cref="_pendingCompletionsLock"/>.
    /// </summary>
    /// <returns>A dictionary keyed by job ID.</returns>
    private async Task<Dictionary<string, PendingCompletion>> LoadPendingCompletionsInternalAsync()
    {
        if (File.Exists(_pendingCompletionsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_pendingCompletionsPath);
                var list = JsonSerializer.Deserialize<List<PendingCompletion>>(json, _jsonOptions);
                return list?.ToDictionary(c => c.JobId, c => c) ?? new();
            }
            catch { }
        }
        return new();
    }

    /// <summary>
    ///     Re-posts all persisted pending completions to the master. Takes a snapshot under
    ///     the lock, then iterates outside the lock to avoid holding it during network I/O.
    ///     Called on each heartbeat cycle to recover from lost completion notifications.
    /// </summary>
    public async Task RetryPendingCompletionsAsync()
    {
        await _pendingCompletionsLock.WaitAsync();
        List<PendingCompletion> snapshot;
        try
        {
            snapshot = (await LoadPendingCompletionsInternalAsync()).Values.ToList();
        }
        finally
        {
            _pendingCompletionsLock.Release();
        }

        foreach (var completion in snapshot)
        {
            try
            {
                var client  = CreateAuthenticatedClient();
                var selfUrl = $"{(Config.UseHttps ? "https" : "http")}://{ClusterDiscoveryService.GetLocalIpAddress()}:{_discoveryService.GetListeningPort()}";
                var completionPayload = new JobCompletion
                {
                    JobId          = completion.JobId,
                    Success        = true,
                    OutputFileName = completion.OutputFileName
                };
                var content = new StringContent(
                    JsonSerializer.Serialize(new { completion = completionPayload, nodeBaseUrl = selfUrl }, _jsonOptions),
                    Encoding.UTF8, "application/json");
                var response = await client.PostAsync(
                    $"{completion.MasterUrl}/api/cluster/jobs/{completion.JobId}/complete", content);
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Cluster: Retried completion for {completion.JobId} — acknowledged by master");
                    await RemoveCompletedJobAsync(completion.JobId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Pending completion retry failed for {completion.JobId}: {ex.Message}");
            }
        }
    }

    /******************************************************************
     *  Temp Directory and File Management
     ******************************************************************/

    /// <summary>
    ///     Returns the temp directory path for a remote job, creating it if it does not
    ///     exist. The job ID is sanitized to prevent path-traversal attacks.
    /// </summary>
    /// <param name="jobId">The job ID to build the temp directory for.</param>
    /// <returns>The absolute path to the temp directory for this job.</returns>
    public string GetNodeTempDirectory(string jobId)
    {
        var safeJobId = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        if (string.IsNullOrEmpty(safeJobId)) throw new ArgumentException("Invalid job ID");

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;
        var dir = Path.Combine(baseDir, safeJobId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    ///     Returns the path to the encoded output file for a job, or <see langword="null"/>
    ///     if no output exists yet. Looks for files matching the <c>[snacks]</c> naming
    ///     convention in the job's temp directory.
    /// </summary>
    /// <param name="jobId">The job ID to look up the output for.</param>
    /// <returns>The absolute path to the output file, or <see langword="null"/>.</returns>
    public string? GetOutputFileForJob(string jobId)
    {
        var tempDir = GetNodeTempDirectory(jobId);
        var files   = Directory.GetFiles(tempDir, "*[snacks]*");
        return files.FirstOrDefault();
    }

    /// <summary>
    ///     Deletes all temp files for a completed or cancelled job and clears the
    ///     receiving and completed job ID references if they match.
    /// </summary>
    /// <param name="jobId">The job ID to clean up.</param>
    public void CleanupJobFiles(string jobId)
    {
        _receivingJobIds.TryRemove(jobId, out _);
        _completedJobIds.TryRemove(jobId, out _);
        RemoveReceiveLock(jobId);

        _transcodingService.RemoveWorkItem(jobId);

        try
        {
            var tempDir = GetNodeTempDirectory(jobId);
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Cleanup failed for job {jobId}: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deletes remote job temp directories from a previous crashed session,
    ///     <b>except</b> those that have pending completions awaiting master acknowledgement.
    ///     Called on node startup to reclaim disk space without losing completed work.
    /// </summary>
    public void CleanupAllRemoteJobs()
    {
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;

        if (!Directory.Exists(baseDir)) return;

        // Load pending completions so we can preserve their output directories
        var pendingJobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(_pendingCompletionsPath))
        {
            try
            {
                var json = File.ReadAllText(_pendingCompletionsPath);
                var list = JsonSerializer.Deserialize<List<PendingCompletion>>(json, _jsonOptions);
                if (list != null)
                    foreach (var pc in list)
                        pendingJobIds.Add(pc.JobId);
            }
            catch { }
        }

        try
        {
            int cleaned = 0;
            int preserved = 0;
            foreach (var dir in Directory.GetDirectories(baseDir))
            {
                var dirName = Path.GetFileName(dir);
                if (pendingJobIds.Contains(dirName))
                {
                    preserved++;
                    continue;
                }

                try
                {
                    Directory.Delete(dir, true);
                    cleaned++;
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"Cluster: Could not delete {dir}: {ex.Message} — trying individual files");
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            if (cleaned > 0)
                Console.WriteLine($"Cluster: Cleaned up {cleaned} orphaned remote job directories");
            if (preserved > 0)
                Console.WriteLine($"Cluster: Preserved {preserved} directories with pending completions");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Cleanup error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Deletes remote job temp directories that are older than <paramref name="ttlHours"/>
    ///     hours based on last write time.
    /// </summary>
    /// <param name="ttlHours">Directories last written more than this many hours ago are deleted.</param>
    public void CleanupOldRemoteJobs(int ttlHours)
    {
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var baseDir = string.IsNullOrWhiteSpace(Config.NodeTempDirectory)
            ? Path.Combine(workDir, "remote-jobs")
            : Config.NodeTempDirectory;

        if (!Directory.Exists(baseDir)) return;

        var cutoff  = DateTime.UtcNow.AddHours(-ttlHours);
        int cleaned = 0;

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            try
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.LastWriteTimeUtc < cutoff)
                {
                    Directory.Delete(dir, true);
                    cleaned++;
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine($"Cluster: Could not delete {dir}: {ex.Message}");
            }
        }

        if (cleaned > 0)
            Console.WriteLine($"Cluster: Cleaned up {cleaned} remote job directories older than {ttlHours}h");
    }

    /******************************************************************
     *  Private Helpers
     ******************************************************************/

    /// <summary>
    ///     Resolves the master node's base URL from the explicit config value or by
    ///     scanning the shared nodes dictionary for a node with the master role.
    /// </summary>
    /// <returns>The master's base URL with no trailing slash, or <see langword="null"/>.</returns>
    private string? ResolveMasterUrl()
    {
        var masterUrl = Config.MasterUrl?.TrimEnd('/');
        if (!string.IsNullOrEmpty(masterUrl))
            return masterUrl;

        var masterNode = _nodes.Values.FirstOrDefault(n => n.Role == "master");
        return masterNode != null
            ? $"{(Config.UseHttps ? "https" : "http")}://{masterNode.IpAddress}:{masterNode.Port}"
            : null;
    }

    /// <summary>
    ///     Creates an <see cref="HttpClient"/> with the cluster shared secret attached as
    ///     the <c>X-Snacks-Secret</c> header and a generous 30-minute timeout.
    /// </summary>
    /// <returns>A configured <see cref="HttpClient"/>.</returns>
    private HttpClient CreateAuthenticatedClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.Add("X-Snacks-Secret", ClusterAuthFilter.EncodeSecretForHeader(Config.SharedSecret));
        return client;
    }

    /// <summary> UTC timestamp of the last successful integration pull, or <c>null</c> if never synced. </summary>
    public DateTime? LastIntegrationSyncAt { get; private set; }

    /// <summary> Human-readable status of the last pull attempt. </summary>
    public string LastIntegrationSyncStatus { get; private set; } = "Never";

    /// <summary>
    ///     Pulls the master's current integration config and writes it to the local
    ///     <c>integrations.json</c> so original-language lookups and OCR on this worker
    ///     use the master's credentials instead of whatever is cached locally. Called
    ///     immediately before every remote encode, and exposed publicly so the UI can
    ///     trigger an on-demand refresh.
    ///
    ///     <para>On any failure (master unreachable, 403, parse error) the existing
    ///     local config is preserved and the encode continues — a transient pull
    ///     failure must never abort a queued job.</para>
    /// </summary>
    public async Task PullIntegrationsFromMasterAsync(CancellationToken ct)
    {
        var masterUrl = ResolveMasterUrl();
        if (string.IsNullOrEmpty(masterUrl))
        {
            LastIntegrationSyncStatus = "Master URL unresolved";
            return;
        }

        try
        {
            var client     = CreateAuthenticatedClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            using var resp = await client.GetAsync($"{masterUrl}/api/cluster/integrations", ct);
            if (!resp.IsSuccessStatusCode)
            {
                LastIntegrationSyncStatus = $"Failed: HTTP {(int)resp.StatusCode}";
                Console.WriteLine($"Cluster: Integration pull failed (HTTP {(int)resp.StatusCode}); using cached config.");
                return;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var pulled = JsonSerializer.Deserialize<IntegrationConfig>(json, _jsonOptions);
            if (pulled == null)
            {
                LastIntegrationSyncStatus = "Failed: empty payload";
                Console.WriteLine("Cluster: Integration pull returned empty payload; using cached config.");
                return;
            }

            _integrationService.SaveConfig(pulled);
            LastIntegrationSyncAt     = DateTime.UtcNow;
            LastIntegrationSyncStatus = "OK";
            Console.WriteLine("Cluster: Integration config synced from master.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LastIntegrationSyncStatus = $"Failed: {ex.Message}";
            Console.WriteLine($"Cluster: Integration pull error ({ex.Message}); using cached config.");
        }
    }
}
