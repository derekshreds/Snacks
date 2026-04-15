namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

/// <summary>
///     Orchestrates the distributed encoding cluster — heartbeat monitoring,
///     job dispatch, file transfer coordination, and crash recovery. Delegates
///     discovery to <see cref="ClusterDiscoveryService"/>, chunked transfers to
///     <see cref="ClusterFileTransferService"/>, and node-side job execution to
///     <see cref="ClusterNodeJobService"/>.
///
///     <para><b>Architecture:</b></para>
///     <list type="bullet">
///       <item>
///           <description>Master mode: Coordinates jobs, dispatches to worker nodes, monitors health</description>
///       </item>
///       <item>
///           <description>Node mode: Receives jobs, encodes, reports progress/completion</description>
///       </item>
///       <item><description>Standalone: No clustering, all encoding is local</description></item>
///     </list>
///
///     <para><b>Resilience Features:</b></para>
///     <list type="bullet">
///       <item><description>Chunked file transfer with SHA256 hash verification</description></item>
///       <item><description>Resume support for uploads and downloads</description></item>
///       <item><description>Crash recovery via SQLite database on startup</description></item>
///       <item><description>Persistent completion tracking on nodes</description></item>
///       <item><description>Graceful shutdown with transfer completion</description></item>
///       <item><description>Node temp file TTL cleanup</description></item>
///     </list>
/// </summary>
public sealed class ClusterService : IHostedService, IDisposable
{
    /******************************************************************
     *  Dependencies
     ******************************************************************/

    private readonly TranscodingService                        _transcodingService;
    private readonly FfprobeService                            _ffprobeService;
    private readonly FileService                               _fileService;
    private readonly IHubContext<TranscodingHub>                _hubContext;
    private readonly IHttpClientFactory                        _httpClientFactory;
    private readonly MediaFileRepository                       _mediaFileRepo;
    private readonly StateTransitionService                    _stateTransitions;
    private readonly ConcurrentDictionary<string, ClusterNode> _nodes = new();
    private readonly ConcurrentDictionary<string, WorkItem>    _remoteJobs         = new();
    private readonly ConcurrentDictionary<string, bool>        _activeDownloads    = new();
    private readonly ConcurrentDictionary<string, bool>        _activeUploads      = new();
    private readonly ConcurrentDictionary<string, int>         _downloadRetryCounts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts = new();
    private readonly ConcurrentDictionary<string, StateTransitionService.TransitionScope> _activeTransitions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nodeDispatchLocks = new();
    private readonly ConcurrentDictionary<string, Task>          _activeDispatchTasks = new();
    private readonly ConcurrentDictionary<string, DateTime>      _nodeDispatchCooldowns = new();
    private readonly ConcurrentDictionary<string, int>           _nodeConsecutiveFailures = new();
    private readonly string _configPath;
    private readonly string _workDir;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /******************************************************************
     *  Sub-Services
     ******************************************************************/

    private readonly ClusterDiscoveryService     _discovery;
    private readonly ClusterFileTransferService  _fileTransfer;
    private readonly ClusterNodeJobService       _nodeJobs;

    /******************************************************************
     *  Internal State
     ******************************************************************/

    private ClusterConfig            _config = new();
    private Timer?                   _heartbeatTimer;
    private Timer?                   _dispatchTimer;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource     _recoveryComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim   _dispatchLock = new(1, 1);

    /// <summary> Completes when crash recovery of remote jobs has finished on startup. </summary>
    public Task RecoveryCompleteTask => _recoveryComplete.Task;

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Initialises the orchestrator and all sub-services, wiring shared state
    ///     such as the node registry and cluster configuration.
    /// </summary>
    public ClusterService(
        TranscodingService transcodingService,
        FfprobeService ffprobeService,
        FileService fileService,
        IHubContext<TranscodingHub> hubContext,
        IHttpClientFactory httpClientFactory,
        MediaFileRepository mediaFileRepo,
        StateTransitionService stateTransitions)
    {
        _transcodingService = transcodingService;
        _ffprobeService     = ffprobeService;
        _fileService        = fileService;
        _hubContext         = hubContext;
        _httpClientFactory  = httpClientFactory;
        _mediaFileRepo      = mediaFileRepo;
        _stateTransitions   = stateTransitions;

        _workDir = _fileService.GetWorkingDirectory();
        var configDir = Path.Combine(_workDir, "config");
        Directory.CreateDirectory(configDir);
        _configPath = Path.Combine(configDir, "cluster.json");

        // Sub-services share the node registry and are configured after config load
        _discovery = new ClusterDiscoveryService(
            _config, hubContext, httpClientFactory, transcodingService, _nodes);

        _fileTransfer = new ClusterFileTransferService(hubContext, httpClientFactory);

        _nodeJobs = new ClusterNodeJobService(
            transcodingService, hubContext, httpClientFactory, _discovery, _nodes);
    }

    /******************************************************************
     *  IHostedService
     ******************************************************************/

    /// <summary>
    ///     Loads cluster configuration, starts cluster operations if enabled,
    ///     and triggers crash recovery for master nodes.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfig();
        Console.WriteLine($"Cluster: Config loaded — enabled={_config.Enabled}, role={_config.Role}, nodeId={_config.NodeId}");

        if (_config.Enabled && _config.Role != "standalone")
        {
            if (!_config.UseHttps)
                Console.WriteLine("Cluster: WARNING — TLS is disabled. The shared secret is transmitted in plaintext. Set UseHttps=true in cluster.json for secure communication.");

            // Job dispatch (master only)
            if (_config.Role == "master")
                _recoveryComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else
                _recoveryComplete.TrySetResult();

            StartClusterOperations();

            if (_config.Role == "master")
                _ = Task.Run(() => RecoverRemoteJobsAsync(_cts?.Token ?? CancellationToken.None));
        }
        else
        {
            _recoveryComplete.TrySetResult();
            _transcodingService.SetLocalEncodingPaused(false);
            Console.WriteLine("Cluster: Not starting — disabled or standalone mode");
        }

        return Task.CompletedTask;
    }

    /// <summary> Stops all cluster operations gracefully when the application shuts down. </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("Cluster: Shutting down...");
        await StopClusterOperationsAsync();
    }

    /******************************************************************
     *  Cluster Lifecycle
     ******************************************************************/

    /// <summary>
    ///     Initiates a graceful or immediate shutdown. In graceful mode, pauses all
    ///     nodes, waits for active transfers to complete, then stops.
    /// </summary>
    public async Task InitiateShutdownAsync(bool graceful, int timeoutSeconds)
    {
        Console.WriteLine($"Cluster: Initiating {(graceful ? "graceful" : "immediate")} shutdown (timeout: {timeoutSeconds}s)");

        if (graceful && IsMasterMode)
        {
            foreach (var node in _nodes.Values.Where(n => n.Role == "node"))
            {
                try { await SetRemoteNodePausedAsync(node.NodeId, true); }
                catch { }
            }

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                var activeTransfers = _activeUploads.Count + _activeDownloads.Count;
                if (activeTransfers == 0) break;
                Console.WriteLine($"Cluster: Waiting for {activeTransfers} active transfer(s) to complete...");
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        try { await _mediaFileRepo.CleanupOldTransitionsAsync(); } catch { }
        await StopClusterOperationsAsync();
    }

    /// <summary> Disposes timers and cancellation sources. </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _heartbeatTimer?.Dispose();
        _dispatchTimer?.Dispose();
        _dispatchLock.Dispose();
        _cts?.Dispose();
        _cts = null;
    }

    /******************************************************************
     *  Full State Reset
     ******************************************************************/

    /// <summary>
    ///     Clears all remote job state: cancels in-flight transfers, notifies worker nodes
    ///     to stop and clean up, clears all tracking dictionaries, resets node states,
    ///     and purges the WAL table. Called by ClearHistoryAsync for a full system reset.
    /// </summary>
    public async Task ClearAllRemoteStateAsync()
    {
        Console.WriteLine("Cluster: Clearing all remote job state...");

        // Cancel all active transfer CTS
        foreach (var kvp in _jobCts)
        {
            try { kvp.Value.Cancel(); kvp.Value.Dispose(); } catch { }
        }
        _jobCts.Clear();

        // Send cancel + cleanup to all worker nodes (best-effort)
        var client = _discovery.CreateAuthenticatedClient();
        foreach (var kvp in _remoteJobs)
        {
            var workItem = kvp.Value;
            if (!string.IsNullOrEmpty(workItem.AssignedNodeId) &&
                _nodes.TryGetValue(workItem.AssignedNodeId, out var node))
            {
                try
                {
                    var baseUrl = NodeBaseUrl(node);
                    await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{kvp.Key}");
                    await client.DeleteAsync($"{baseUrl}/api/cluster/files/{kvp.Key}");
                }
                catch { }
            }
        }

        // Complete and clear all active WAL transitions
        foreach (var kvp in _activeTransitions)
        {
            try { await kvp.Value.CompleteAsync(); } catch { }
        }
        _activeTransitions.Clear();

        // Clear all tracking dictionaries
        _remoteJobs.Clear();
        _activeUploads.Clear();
        _activeDownloads.Clear();
        _downloadRetryCounts.Clear();
        _nodeDispatchCooldowns.Clear();
        _nodeConsecutiveFailures.Clear();
        _activeDispatchTasks.Clear();
        foreach (var kvp in _nodeDispatchLocks)
        {
            if (_nodeDispatchLocks.TryRemove(kvp.Key, out var sl))
                sl.Dispose();
        }

        // Reset all worker node states
        foreach (var node in _nodes.Values.Where(n => n.Role == "node"))
        {
            node.ActiveWorkItemId = null;
            node.ActiveFileName   = null;
            node.ActiveProgress   = 0;
            if (node.Status != NodeStatus.Unreachable && node.Status != NodeStatus.Offline)
                node.Status = NodeStatus.Online;
            _ = _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
        }

        // Clear WAL table
        try { await _mediaFileRepo.ClearAllTransitionsAsync(); } catch { }

        Console.WriteLine("Cluster: Remote state cleared");
    }

    /******************************************************************
     *  Configuration
     ******************************************************************/

    /// <summary> Returns the current cluster configuration. </summary>
    public ClusterConfig GetConfig() => _config;

    /// <summary>
    ///     Persists a new configuration and restarts cluster operations immediately.
    ///     Handles role transitions such as switching from master to node mode.
    /// </summary>
    public async Task SaveConfigAndApplyAsync(ClusterConfig newConfig)
    {
        var oldRole    = _config.Role;
        _config        = newConfig;
        SaveConfig();

        _discovery.Config = newConfig;
        _nodeJobs.Config  = newConfig;

        if (newConfig.Enabled && newConfig.Role != "standalone")
        {
            if (newConfig.Role == "node" && oldRole != "node")
                await _transcodingService.StopAndClearQueue();

            await StopClusterOperationsAsync();
            StartClusterOperations();
        }
        else
        {
            await StopClusterOperationsAsync();
            _transcodingService.SetLocalEncodingPaused(false);
        }

        await _hubContext.Clients.All.SendAsync("ClusterConfigChanged", new
        {
            newConfig.Enabled,
            newConfig.Role,
            newConfig.NodeName,
            newConfig.AutoDiscovery,
            newConfig.LocalEncodingEnabled
        });
    }

    /// <summary> Returns a snapshot of all currently known cluster nodes. </summary>
    public IReadOnlyList<ClusterNode> GetNodes() => _nodes.Values.ToList();

    /// <summary> Whether this instance is running as a worker node. </summary>
    public bool IsNodeMode => _config.Enabled && _config.Role == "node";

    /// <summary> Whether this instance is running as the master coordinator. </summary>
    public bool IsMasterMode => _config.Enabled && _config.Role == "master";

    /******************************************************************
     *  Delegated Node-Side API
     ******************************************************************/

    /// <summary> Whether this node is paused and not accepting new jobs. </summary>
    public bool IsNodePaused => _nodeJobs.IsNodePaused;

    /// <summary>
    ///     Starts autonomous encoding on a worker node after a file upload completes.
    ///     Called by the ClusterController.ReceiveFileWithMetadata endpoint.
    /// </summary>
    /// <param name="jobId"> The job ID that was uploaded. </param>
    /// <param name="metadata"> The job metadata sent with the upload. </param>
    /// <param name="filePath"> The local path where the file was saved. </param>
    /// <returns> A tuple of (started, rejectReason). </returns>
    public async Task<(bool Started, string? RejectReason)> StartAutonomousEncodingAsync(string jobId, JobMetadata metadata, string filePath)
    {
        return await _nodeJobs.StartAutonomousEncodingAsync(jobId, metadata, filePath);
    }

    /// <summary> Whether this node is currently processing a remote job. </summary>
    public bool IsProcessingRemoteJob() => _nodeJobs.IsProcessingRemoteJob();

    /// <summary> Returns the current remote job ID. </summary>
    public string? GetCurrentRemoteJobId() => _nodeJobs.GetCurrentRemoteJobId();

    /// <summary> Returns the encoding progress of the current remote job. </summary>
    public int GetCurrentRemoteJobProgress() => _nodeJobs.GetCurrentRemoteJobProgress();

    /// <summary> Returns the completed job ID if encoding finished but cleanup hasn't occurred yet. </summary>
    public string? GetCompletedJobId() => _nodeJobs.GetCompletedJobId();

    /// <summary> Returns the job ID currently being received via file transfer. </summary>
    public string? GetReceivingJobId() => _nodeJobs.GetReceivingJobId();

    /// <summary> Pauses or resumes this node. </summary>
    public void SetNodePaused(bool paused) => _nodeJobs.SetNodePaused(paused);

    /// <summary> Sets the job ID being received during file transfer. </summary>
    public void SetReceivingJob(string? jobId) => _nodeJobs.SetReceivingJob(jobId);

    /// <summary> Returns the per-job semaphore that serializes ReceiveFile requests. </summary>
    public SemaphoreSlim GetReceiveLock(string jobId) => _nodeJobs.GetReceiveLock(jobId);

    /// <summary> Cancels any in-flight receive for this job and returns a fresh cancellation token. </summary>
    public CancellationToken SwapReceiveCts(string jobId) => _nodeJobs.SwapReceiveCts(jobId);

    /// <summary> Cancels a job running locally on this node. </summary>
    public void CancelRemoteJob(string jobId) => _nodeJobs.CancelRemoteJob(jobId);

    /// <summary> Returns the temp directory for a remote job. </summary>
    public string GetNodeTempDirectory(string jobId) => _nodeJobs.GetNodeTempDirectory(jobId);

    /// <summary> Returns the output file path for a job, or <see langword="null" /> if none exists. </summary>
    public string? GetOutputFileForJob(string jobId) => _nodeJobs.GetOutputFileForJob(jobId);

    /// <summary> Cleans up temp files for a completed or cancelled job. </summary>
    public void CleanupJobFiles(string jobId) => _nodeJobs.CleanupJobFiles(jobId);

    /// <summary> Deletes remote job temp directories older than the specified hours. </summary>
    public void CleanupOldRemoteJobs(int ttlHours) => _nodeJobs.CleanupOldRemoteJobs(ttlHours);

    /// <summary> Deletes all remote job temp directories unconditionally. </summary>
    public void CleanupAllRemoteJobs() => _nodeJobs.CleanupAllRemoteJobs();

    /******************************************************************
     *  Delegated Discovery API
     ******************************************************************/

    /// <summary> Builds a <see cref="ClusterNode" /> representing this instance. </summary>
    public ClusterNode BuildSelfNode() => _discovery.BuildSelfNode();

    /// <summary> Returns this node's hardware and software capabilities. </summary>
    public WorkerCapabilities GetCapabilities() => _discovery.GetCapabilities();

    /// <summary> Registers or updates a cluster node. </summary>
    public (bool Accepted, string? RejectReason) RegisterOrUpdateNode(ClusterNode node, bool fromHandshake = false) =>
        _discovery.RegisterOrUpdateNode(node, fromHandshake);

    /******************************************************************
     *  Remote Job Phase Queries
     ******************************************************************/

    /// <summary> Returns the phase of a remote job, or <see langword="null" /> if not tracked. </summary>
    public string? GetRemoteJobPhase(string jobId) =>
        _remoteJobs.TryGetValue(jobId, out var w) ? w.RemoteJobPhase : null;

    /// <summary>
    ///     Checks if a file path is currently being handled as a remote job.
    ///     Uses the in-memory cache first, then the database as the authoritative source.
    /// </summary>
    public async Task<bool> IsRemoteJobAsync(string filePath)
    {
        var normalized = Path.GetFullPath(filePath);

        if (_remoteJobs.Values.Any(w =>
            Path.GetFullPath(w.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return true;

        return await _mediaFileRepo.IsRemoteJobAsync(normalized);
    }

    /******************************************************************
     *  Node Pause (Remote)
     ******************************************************************/

    /// <summary>
    ///     Sends a pause or resume command to a remote node and updates its local
    ///     registry entry only after the node confirms the change.
    /// </summary>
    public async Task SetRemoteNodePausedAsync(string nodeId, bool paused)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return;

        try
        {
            var client  = _discovery.CreateAuthenticatedClient();
            var baseUrl = NodeBaseUrl(node);
            var content = new StringContent(
                JsonSerializer.Serialize(new { paused }, _jsonOptions),
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"{baseUrl}/api/cluster/pause", content);
            response.EnsureSuccessStatusCode();

            node.IsPaused = paused;
            node.Status   = paused ? NodeStatus.Paused : NodeStatus.Online;
            await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
            Console.WriteLine($"Cluster: Node {node.Hostname} {(paused ? "paused" : "resumed")} by master");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to pause node {node.Hostname}: {ex.Message}");
        }
    }

    /******************************************************************
     *  Cluster Operations Start / Stop
     ******************************************************************/

    /// <summary> Starts discovery, heartbeat, dispatch, and manual node connections. </summary>
    private void StartClusterOperations()
    {
        if (string.IsNullOrEmpty(_config.SharedSecret))
        {
            Console.WriteLine("Cluster: Cannot start — no shared secret configured");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        _discovery.Config = _config;
        _nodeJobs.Config  = _config;

        // UDP discovery
        bool needsDiscovery = _config.AutoDiscovery ||
            (_config.Role == "node" && string.IsNullOrEmpty(_config.MasterUrl));
        if (needsDiscovery)
            _discovery.Start(_cts.Token);

        // Heartbet with jitter
        var heartbeatInterval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)heartbeatInterval.TotalMilliseconds));
        _heartbeatTimer = new Timer(async _ =>
        {
            if (_cts?.IsCancellationRequested == true) return;
            try { await RunHeartbeatAsync(); }
            catch (Exception ex) { Console.WriteLine($"Cluster: Heartbeat error: {ex.Message}"); }

            // On nodes, clear stale receiving state and retry any pending completions
            if (_config.Role == "node")
            {
                _nodeJobs.ExpireStaleReceiving(TimeSpan.FromSeconds(_config.NodeTimeoutSeconds));
                try { await _nodeJobs.RetryPendingCompletionsAsync(); }
                catch (Exception ex) { Console.WriteLine($"Cluster: Pending completion retry error: {ex.Message}"); }
            }
        }, null, jitter, heartbeatInterval);

        if (_config.Role == "master")
        {
            _transcodingService.SetLocalEncodingPaused(!_config.LocalEncodingEnabled);
            _transcodingService.SetRemoteJobCanceller(CancelRemoteJobOnNodeAsync);
            _transcodingService.SetRemoteJobChecker(IsRemoteJobAsync);

            _dispatchTimer = new Timer(async _ =>
            {
                if (_cts?.IsCancellationRequested == true) return;
                try { await RunDispatchAsync(); }
                catch (Exception ex) { Console.WriteLine($"Cluster: Dispatch error: {ex.Message}"); }
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
        }
        else
        {
            _transcodingService.SetLocalEncodingPaused(false);
        }

        var localIp = ClusterDiscoveryService.GetLocalIpAddress();
        var port    = _discovery.GetListeningPort();
        Console.WriteLine($"Cluster started: role={_config.Role}, localIp={localIp}, port={port}, discovery={needsDiscovery}");
    }

    /// <summary> Stops all timers and the discovery sub-service. </summary>
    private async Task StopClusterOperationsAsync()
    {
        _cts?.Cancel();

        await _discovery.StopAsync();

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        _dispatchTimer?.Dispose();
        _dispatchTimer = null;

        _cts?.Dispose();
        _cts = null;

        Console.WriteLine("Cluster: Stopped");
    }

    /******************************************************************
     *  Heartbeat
     ******************************************************************/

    /// <summary>
    ///     Pings all nodes, detects timeouts, and reconciles job state between master
    ///     and worker nodes.
    /// </summary>
    private async Task RunHeartbeatAsync()
    {
        if (!_recoveryComplete.Task.IsCompleted) return;

        var now     = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(_config.NodeTimeoutSeconds);
        var removeAfter = TimeSpan.FromMinutes(5);

        foreach (var kvp in _nodes)
        {
            var node = kvp.Value;
            var timeSinceHeartbeat = now - node.LastHeartbeat;

            if (timeSinceHeartbeat > removeAfter && node.Status == NodeStatus.Unreachable)
            {
                if (_nodes.TryRemove(kvp.Key, out _))
                {
                    Console.WriteLine($"Cluster: Removed unreachable node {node.Hostname}");
                    await _hubContext.Clients.All.SendAsync("WorkerDisconnected", node.NodeId);
                    // Clean up dispatch tracking for removed node
                    _nodeDispatchCooldowns.TryRemove(kvp.Key, out _);
                    _nodeConsecutiveFailures.TryRemove(kvp.Key, out _);
                    _activeDispatchTasks.TryRemove(kvp.Key, out _);
                    if (_nodeDispatchLocks.TryRemove(kvp.Key, out var removedLock))
                        removedLock.Dispose();
                }
                continue;
            }

            if (timeSinceHeartbeat > timeout && node.Status != NodeStatus.Unreachable && node.Status != NodeStatus.Offline)
            {
                Console.WriteLine($"Cluster: Node {node.Hostname} timed out (last heartbeat {timeSinceHeartbeat.TotalSeconds:0}s ago)");
                node.Status = NodeStatus.Unreachable;
                await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);

                if (node.ActiveWorkItemId != null)
                {
                    var failJobId = node.ActiveWorkItemId;
                    bool hasActiveTransfer = _activeUploads.ContainsKey(failJobId)
                        || _activeDownloads.ContainsKey(failJobId);

                    if (hasActiveTransfer)
                    {
                        // Cancel the transfer's CTS so the upload/download fails fast
                        // instead of retrying against a dead node for hours.
                        Console.WriteLine($"Cluster: Node {node.Hostname} unreachable — cancelling active transfer for {failJobId}");
                        if (_jobCts.TryGetValue(failJobId, out var transferCts))
                            transferCts.Cancel();
                    }
                    else
                    {
                        await HandleNodeFailureAsync(failJobId);
                    }

                    node.ActiveWorkItemId = null;
                }
                continue;
            }

            // Ping the node
            try
            {
                var client  = _discovery.CreateAuthenticatedClient();
                var baseUrl = NodeBaseUrl(node);
                var response = await client.GetAsync($"{baseUrl}/api/cluster/heartbeat");

                if (response.IsSuccessStatusCode)
                {
                    var body      = await response.Content.ReadAsStringAsync();
                    var heartbeat = JsonSerializer.Deserialize<JsonElement>(body);

                    node.LastHeartbeat = DateTime.UtcNow;
                    if (node.Status == NodeStatus.Unreachable)
                    {
                        node.Status = NodeStatus.Online;
                        Console.WriteLine($"Cluster: Node {node.Hostname} reconnected");
                    }

                    if (heartbeat.TryGetProperty("isPaused", out var pausedProp))
                        node.IsPaused = pausedProp.GetBoolean();

                    if (node.Status == NodeStatus.Uploading || node.Status == NodeStatus.Downloading)
                    {
                        // Master-side transfer in progress — heartbeat cannot override this.
                        // The upload/download codepath owns the state transition.
                    }
                    else if (node.IsPaused)
                    {
                        node.Status = NodeStatus.Paused;
                    }
                    else if (heartbeat.TryGetProperty("currentJobId", out var jobId) && jobId.ValueKind != JsonValueKind.Null)
                    {
                        var nodeJobId = jobId.GetString();
                        node.Status          = NodeStatus.Busy;
                        node.ActiveWorkItemId = nodeJobId;

                        // Node is actively working — clear any idle grace counter
                        if (nodeJobId != null)
                            _downloadRetryCounts.TryRemove($"_idle_grace_{nodeJobId}", out _);

                        // Sync progress from heartbeat as fallback
                        if (nodeJobId != null && heartbeat.TryGetProperty("progress", out var progProp) &&
                            _remoteJobs.TryGetValue(nodeJobId, out var trackedJob))
                        {
                            trackedJob.Progress = progProp.GetInt32();
                            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", trackedJob);
                        }

                        // Reconciliation: unknown job on node
                        if (nodeJobId != null && !_remoteJobs.ContainsKey(nodeJobId))
                        {
                            var dbFile = await _mediaFileRepo.GetByRemoteWorkItemIdAsync(nodeJobId);
                            if (dbFile?.AssignedNodeId != null)
                            {
                                Console.WriteLine($"Cluster: Node {node.Hostname} is working on DB-assigned job {nodeJobId} — skipping cancellation");
                            }
                            else
                            {
                                Console.WriteLine($"Cluster: Node {node.Hostname} is working on unknown job {nodeJobId} — telling it to cancel");
                                try
                                {
                                    await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{nodeJobId}");
                                    await client.DeleteAsync($"{baseUrl}/api/cluster/files/{nodeJobId}");
                                }
                                catch (Exception ex) { Console.WriteLine($"Cluster: Failed to cancel unknown job: {ex.Message}"); }
                            }
                        }
                    }
                    else
                    {
                        // Node is idle — reconcile if master expected a job.
                        // But DON'T re-queue immediately: there's a window between upload
                        // completing and the node starting to encode where the heartbeat
                        // shows idle. Also check completedJobId — the node may have finished.

                        // Skip idle detection entirely if an upload or dispatch is in progress
                        if (node.ActiveWorkItemId != null &&
                            (_activeUploads.ContainsKey(node.ActiveWorkItemId) ||
                             (_activeDispatchTasks.TryGetValue(node.NodeId, out var pendingDispatch) && !pendingDispatch.IsCompleted)))
                        {
                            // Master-side upload or dispatch task in progress — don't count as idle
                        }
                        else if (node.ActiveWorkItemId != null && _remoteJobs.ContainsKey(node.ActiveWorkItemId))
                        {
                            // Check if the node has the completed output for this job
                            bool hasCompleted = heartbeat.TryGetProperty("completedJobId", out var compId)
                                && compId.ValueKind != JsonValueKind.Null
                                && compId.GetString() == node.ActiveWorkItemId;

                            bool isReceiving = heartbeat.TryGetProperty("receivingJobId", out var recvId)
                                && recvId.ValueKind != JsonValueKind.Null
                                && recvId.GetString() == node.ActiveWorkItemId;

                            if (hasCompleted)
                            {
                                Console.WriteLine($"Cluster: Node {node.Hostname} completed job {node.ActiveWorkItemId} — initiating download");
                                node.Status = NodeStatus.Downloading;
                                var completionBaseUrl = NodeBaseUrl(node);
                                _ = Task.Run(() => HandleRemoteCompletionAsync(node.ActiveWorkItemId, completionBaseUrl));
                            }
                            else if (isReceiving)
                            {
                                // Node is still receiving chunks — upload is in progress on the node side
                                Console.WriteLine($"Cluster: Node {node.Hostname} is still receiving job {node.ActiveWorkItemId}");
                            }
                            else
                            {
                                // Use a grace counter: only re-queue after the node reports idle
                                // for multiple consecutive heartbeats to avoid racing with encoding startup.
                                var graceKey = $"_idle_grace_{node.ActiveWorkItemId}";
                                var graceCount = _downloadRetryCounts.AddOrUpdate(graceKey, 1, (_, c) => c + 1);
                                if (graceCount >= 10)
                                {
                                    Console.WriteLine($"Cluster: Node {node.Hostname} idle for {graceCount} heartbeats for job {node.ActiveWorkItemId} — re-queuing");
                                    _downloadRetryCounts.TryRemove(graceKey, out _);
                                    await HandleNodeFailureAsync(node.ActiveWorkItemId);
                                    node.Status          = NodeStatus.Online;
                                    node.ActiveWorkItemId = null;
                                }
                                else
                                {
                                    Console.WriteLine($"Cluster: Node {node.Hostname} appears idle for job {node.ActiveWorkItemId} (grace {graceCount}/3)");
                                }
                            }
                        }
                        else
                        {
                            node.Status          = NodeStatus.Online;
                            node.ActiveWorkItemId = null;
                        }
                    }

                    if (heartbeat.TryGetProperty("capabilities", out var caps))
                    {
                        try
                        {
                            var updated = JsonSerializer.Deserialize<WorkerCapabilities>(caps.GetRawText(), _jsonOptions);
                            if (updated != null) node.Capabilities = updated;
                        }
                        catch { }
                    }

                    await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
                }
            }
            catch
            {
                // HTTP failed — don't update LastHeartbeat so timeout logic kicks in
            }
        }

        // Periodic master conflict detection
        if (IsMasterMode)
        {
            var otherMasters = _nodes.Values.Where(n => n.Role == "master").ToList();
            if (otherMasters.Count > 0)
            {
                // Deterministic tiebreaker: lower NodeId wins
                var selfNode = _discovery.BuildSelfNode();
                var allMasters = otherMasters.Concat(new[] { selfNode }).OrderBy(n => n.NodeId, StringComparer.Ordinal).ToList();
                var winner = allMasters.First();

                if (winner.NodeId != _config.NodeId)
                {
                    Console.WriteLine($"Cluster: WARNING — another master detected: {otherMasters[0].Hostname}. " +
                        $"This node lost tiebreak ({_config.NodeId} > {winner.NodeId}) — disabling dispatch");
                    _dispatchTimer?.Dispose();
                    _dispatchTimer = null;
                    await _hubContext.Clients.All.SendAsync("ClusterWarning",
                        $"Master conflict: {otherMasters[0].Hostname} has priority. Dispatch disabled on this node.");
                }
            }
        }
    }

    /******************************************************************
     *  Node Status
     ******************************************************************/

    /// <summary> Updates node status and broadcasts the change to all connected clients. </summary>
    public void UpdateNodeStatus(string nodeId, NodeStatus status, string? workItemId = null,
        string? fileName = null, int progress = 0)
    {
        if (_nodes.TryGetValue(nodeId, out var node))
        {
            lock (node)
            {
                node.Status           = status;
                node.ActiveWorkItemId = workItemId;
                node.ActiveFileName   = fileName;
                node.ActiveProgress   = progress;
                node.LastHeartbeat    = DateTime.UtcNow;
            }
            _ = _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
        }
    }

    /******************************************************************
     *  Job Dispatch
     ******************************************************************/

    /// <summary> Dequeues pending work items and dispatches them to the best available worker nodes. </summary>
    private async Task RunDispatchAsync()
    {
        if (!IsMasterMode) return;
        if (_transcodingService.IsPaused) return;
        if (!await _dispatchLock.WaitAsync(0)) return;

        try
        {
            // Clean up completed dispatch tasks
            foreach (var kvp in _activeDispatchTasks)
            {
                if (kvp.Value.IsCompleted)
                    _activeDispatchTasks.TryRemove(kvp.Key, out _);
            }

            // Skip dispatch entirely if no worker nodes are available
            bool hasAvailableNodes = _nodes.Values.Any(n =>
                n.Role == "node" && n.Status == NodeStatus.Online
                && !n.IsPaused && n.ActiveWorkItemId == null
                && !_activeDispatchTasks.ContainsKey(n.NodeId));
            if (!hasAvailableNodes) return;

            var options = LoadEncoderOptions();

            while (true)
            {
                var workItem = _transcodingService.DequeueForRemoteProcessing();
                if (workItem == null) break;

                var bestNode = FindBestWorker(workItem, options);
                if (bestNode == null)
                {
                    _transcodingService.RequeueWorkItem(workItem, silent: true);
                    break;
                }

                // Acquire per-node dispatch lock to serialize state changes
                var nodeLock = _nodeDispatchLocks.GetOrAdd(bestNode.NodeId, _ => new SemaphoreSlim(1, 1));
                if (!await nodeLock.WaitAsync(0))
                {
                    // Node is busy with another dispatch — requeue and try next
                    _transcodingService.RequeueWorkItem(workItem, silent: true);
                    break;
                }

                // Mark as uploading immediately to prevent double-dispatch
                bestNode.ActiveWorkItemId = workItem.Id;
                bestNode.Status           = NodeStatus.Uploading;

                // Track the dispatch task instead of fire-and-forget
                var dispatchTask = Task.Run(() => DispatchToNodeAsync(bestNode, workItem, options, nodeLock));
                _activeDispatchTasks[bestNode.NodeId] = dispatchTask;
            }
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    /// <summary>
    ///     Uploads source file with embedded metadata, verifies the upload.
    ///     The worker node begins encoding autonomously upon successful upload.
    /// </summary>
    private async Task DispatchToNodeAsync(ClusterNode node, WorkItem workItem, EncoderOptions options, SemaphoreSlim nodeLock)
    {
        if (!_activeUploads.TryAdd(workItem.Id, true))
        {
            Console.WriteLine($"Cluster: Upload already in progress for {workItem.FileName} — skipping");
            UpdateNodeStatus(node.NodeId, NodeStatus.Online);
            _transcodingService.RequeueWorkItem(workItem, silent: true);
            nodeLock.Release();
            return;
        }

        // Pre-dispatch check: ask the node what it's doing before committing.
        // The master's in-memory state can drift from reality (e.g., node is still
        // retrying a failed encode that the master already re-queued). The node is
        // the source of truth — if it says it's busy, don't dispatch.
        var preCheckUrl = NodeBaseUrl(node);
        try
        {
            var hbResponse = await _discovery.CreateAuthenticatedClient()
                .GetAsync($"{preCheckUrl}/api/cluster/heartbeat");
            if (hbResponse.IsSuccessStatusCode)
            {
                var hbBody = await hbResponse.Content.ReadAsStringAsync();
                var hbData = JsonSerializer.Deserialize<JsonElement>(hbBody);

                bool nodeIsBusy = false;
                if (hbData.TryGetProperty("currentJobId", out var curJob) && curJob.ValueKind != JsonValueKind.Null)
                    nodeIsBusy = true;
                else if (hbData.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "busy")
                    nodeIsBusy = true;

                if (nodeIsBusy)
                {
                    var busyJobId = curJob.ValueKind != JsonValueKind.Null ? curJob.GetString() : "unknown";
                    Console.WriteLine($"Cluster: Node {node.Hostname} is busy (job {busyJobId}) — re-queuing {workItem.FileName}");
                    _activeUploads.TryRemove(workItem.Id, out _);
                    UpdateNodeStatus(node.NodeId, NodeStatus.Busy);
                    _transcodingService.RequeueWorkItem(workItem, silent: true);
                    nodeLock.Release();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Pre-dispatch check failed for {node.Hostname}: {ex.Message} — proceeding with dispatch");
        }

        // Node lock acquired in RunDispatchAsync — release it now that _activeUploads guards us
        nodeLock.Release();

        var baseUrl  = NodeBaseUrl(node);
        var jobCts   = new CancellationTokenSource();
        var uploadId = workItem.Id; // Track the original ID for _activeUploads cleanup
        _jobCts[workItem.Id] = jobCts;

        try
        {
            // Check if we can reuse a previous work item ID for upload resume
            var dbFile = await _mediaFileRepo.GetByPathAsync(Path.GetFullPath(workItem.Path));
            if (dbFile?.RemoteWorkItemId != null)
            {
                // Verify the node still has partial data before committing to the old ID
                long partialBytes = 0;
                try
                {
                    var checkClient = _discovery.CreateAuthenticatedClient();
                    partialBytes = await _fileTransfer.GetNodeReceivedBytesAsync(checkClient, baseUrl, dbFile.RemoteWorkItemId);
                }
                catch { }

                if (partialBytes > 0)
                {
                    // Node has partial data — reuse the ID for resume
                    var oldId = workItem.Id;
                    workItem.Id = dbFile.RemoteWorkItemId;
                    _transcodingService.ReplaceWorkItemId(oldId, workItem.Id, workItem);
                    _jobCts.TryRemove(oldId, out _);
                    _jobCts[workItem.Id] = jobCts;
                    _activeUploads.TryRemove(oldId, out _);
                    _activeUploads.TryAdd(workItem.Id, true);
                    uploadId = workItem.Id;
                    Console.WriteLine($"Cluster: Reusing previous job ID {workItem.Id} for {workItem.FileName} (node has {partialBytes} bytes)");
                }
                else
                {
                    // Node doesn't have this job anymore — clear stale ID and start fresh
                    Console.WriteLine($"Cluster: Stale RemoteWorkItemId {dbFile.RemoteWorkItemId} for {workItem.FileName} — node has no data, starting fresh");
                    await _mediaFileRepo.ClearRemoteWorkItemIdAsync(Path.GetFullPath(workItem.Path));
                }
            }

            // WAL: Queued → Uploading
            var uploadScope = await _stateTransitions.BeginAsync(workItem.Id, "Queued", "Uploading");
            _activeTransitions[workItem.Id] = uploadScope;

            workItem.Status           = WorkItemStatus.Uploading;
            workItem.AssignedNodeId   = node.NodeId;
            workItem.AssignedNodeName = node.Hostname;
            workItem.RemoteJobPhase   = "Uploading";
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            await _mediaFileRepo.AssignToRemoteNodeAsync(
                Path.GetFullPath(workItem.Path), workItem.Id, node.NodeId, node.Hostname,
                node.IpAddress, node.Port, "Uploading");
            _remoteJobs[workItem.Id] = workItem;
            UpdateNodeStatus(node.NodeId, NodeStatus.Uploading, workItem.Id, workItem.FileName);

            await uploadScope.CompleteAsync();
            _activeTransitions.TryRemove(workItem.Id, out _);

            // Lazy probe: items restored from DB on startup don't have probe data yet
            if (workItem.Probe == null)
                workItem.Probe = await _ffprobeService.ProbeAsync(workItem.Path, jobCts.Token);

            // Build metadata for autonomous encoding on the worker
            var metadata = new JobMetadata
            {
                JobId    = workItem.Id,
                FileName = workItem.FileName,
                FileSize = workItem.Size,
                Options  = CloneOptions(options),
                Probe    = workItem.Probe,
                Duration = workItem.Length,
                Bitrate  = workItem.Bitrate,
                IsHevc   = workItem.IsHevc
            };

            var client = _discovery.CreateAuthenticatedClient();
            // Register metadata on worker before uploading
            await _fileTransfer.RegisterMetadataAsync(client, baseUrl, metadata, jobCts.Token);
            // Upload file as pure binary chunks
            await _fileTransfer.UploadFileToNodeAsync(client, baseUrl, workItem, jobCts.Token);

            // Verify upload
            var receivedBytes = await _fileTransfer.GetNodeReceivedBytesAsync(client, baseUrl, workItem.Id);
            if (receivedBytes != workItem.Size)
            {
                Console.WriteLine($"Cluster: Upload verification failed for {workItem.FileName} — expected {workItem.Size}, node has {receivedBytes}");
                try { await client.DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }
                throw new Exception("Upload verification failed");
            }

            // WAL: Uploading → Encoding
            var encodeScope = await _stateTransitions.BeginAsync(workItem.Id, "Uploading", "Encoding");
            _activeTransitions[workItem.Id] = encodeScope;

            workItem.Status          = WorkItemStatus.Processing;
            workItem.RemoteJobPhase  = "Encoding";
            workItem.TransferProgress = 0;
            await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Encoding");
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            await encodeScope.CompleteAsync();
            _activeTransitions.TryRemove(workItem.Id, out _);

            // Node is no longer receiving — transition from Uploading to Busy
            UpdateNodeStatus(node.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

            // Dispatch succeeded — clear any cooldowns for this node
            _nodeConsecutiveFailures.TryRemove(node.NodeId, out _);
            _nodeDispatchCooldowns.TryRemove(node.NodeId, out _);

            Console.WriteLine($"Cluster: Job {workItem.FileName} dispatched to {node.Hostname} (autonomous encoding)");
        }
        catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
        {
            if (node.Status == NodeStatus.Unreachable)
            {
                // Node timed out mid-upload — requeue the work item
                Console.WriteLine($"Cluster: Upload cancelled due to node timeout for {workItem.FileName} — re-queuing");
                await CompleteActiveTransitionAsync(workItem.Id);
                try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }
                workItem.AssignedNodeId   = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase   = null;
                workItem.TransferProgress = 0;
                _remoteJobs.TryRemove(workItem.Id, out _);
                if (_jobCts.TryRemove(workItem.Id, out var timeoutCts))
                    timeoutCts.Dispose();
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                _transcodingService.RequeueWorkItem(workItem);
                ApplyDispatchCooldown(node.NodeId);
                UpdateNodeStatus(node.NodeId, NodeStatus.Unreachable);
            }
            else
            {
                // User-initiated cancellation — don't re-queue, let CancelWorkItemAsync handle status
                Console.WriteLine($"Cluster: Upload cancelled by user for {workItem.FileName}");
                await CompleteActiveTransitionAsync(workItem.Id);
                try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }
                _remoteJobs.TryRemove(workItem.Id, out _);
                UpdateNodeStatus(node.NodeId, NodeStatus.Online);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to dispatch {workItem.FileName} to {node.Hostname}: {ex.Message}");
            Console.WriteLine($"Cluster: Dispatch stack trace: {ex}");
            workItem.ErrorMessage = $"Dispatch failed: {ex.Message}";
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            await CompleteActiveTransitionAsync(workItem.Id);
            try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }

            workItem.AssignedNodeId   = null;
            workItem.AssignedNodeName = null;
            workItem.RemoteJobPhase   = null;
            workItem.TransferProgress = 0;
            _remoteJobs.TryRemove(workItem.Id, out _);
            if (_jobCts.TryRemove(workItem.Id, out var failedCts))
                failedCts.Dispose();
            await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
            _transcodingService.RequeueWorkItem(workItem);
            ApplyDispatchCooldown(node.NodeId);
            UpdateNodeStatus(node.NodeId, NodeStatus.Online);
        }
        finally
        {
            _activeUploads.TryRemove(uploadId, out _);
            // Don't remove/dispose the CTS here — the download phase still needs it.
            // It is cleaned up in HandleRemoteCompletionAsync or HandleNodeFailureAsync.
        }
    }

    /******************************************************************
     *  Remote Job Completion
     ******************************************************************/

    /// <summary> Applies an encoding progress update received from a worker node. </summary>
    public async Task HandleRemoteProgressAsync(string jobId, JobProgress progress)
    {
        if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;

        workItem.Progress       = progress.Progress;
        workItem.RemoteJobPhase = progress.Phase ?? "Encoding";
        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

        if (!string.IsNullOrEmpty(progress.LogLine))
        {
            await _hubContext.Clients.All.SendAsync("TranscodingLog", jobId, progress.LogLine);

            // Persist remote logs to disk so they survive page refreshes and restarts
            try
            {
                var logPath = _transcodingService.BuildLogFilePath(jobId, workItem.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                await File.AppendAllTextAsync(logPath, progress.LogLine.EndsWith('\n')
                    ? progress.LogLine
                    : progress.LogLine + "\n");
            }
            catch { }
        }
    }

    /// <summary>
    ///     Handles completion of a remote job: downloads the output, validates it,
    ///     and performs file placement. Separates download from validation so that
    ///     download failures trigger re-downloads, not re-encodes.
    /// </summary>
    public async Task HandleRemoteCompletionAsync(string jobId, string nodeBaseUrl, bool noSavings = false)
    {
        if (!_remoteJobs.TryGetValue(jobId, out var workItem))
        {
            // Job was cancelled/removed — release the node if it's stuck on this job
            var stuckNode = _nodes.Values.FirstOrDefault(n => n.ActiveWorkItemId == jobId);
            if (stuckNode != null)
            {
                stuckNode.ActiveWorkItemId = null;
                if (stuckNode.Status == NodeStatus.Downloading)
                    stuckNode.Status = NodeStatus.Online;
            }
            return;
        }

        // Encoding produced no savings — skip download and mark as skipped
        if (noSavings)
        {
            Console.WriteLine($"Cluster: No savings for {workItem.FileName} — skipping download");
            workItem.Status         = WorkItemStatus.Completed;
            workItem.CompletedAt    = DateTime.UtcNow;
            workItem.Progress       = 100;
            workItem.ErrorMessage   = null;
            workItem.RemoteJobPhase = null;
            await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Skipped);
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            _remoteJobs.TryRemove(jobId, out _);
            _downloadRetryCounts.TryRemove(jobId, out _);
            if (_jobCts.TryRemove(jobId, out var skipCts)) skipCts.Dispose();

            if (workItem.AssignedNodeId != null && _nodes.TryGetValue(workItem.AssignedNodeId, out var skipNode))
            {
                skipNode.CompletedJobs++;
                if (skipNode.ActiveWorkItemId == jobId)
                {
                    skipNode.ActiveWorkItemId = null;
                    skipNode.Status = NodeStatus.Online;
                }
            }
            return;
        }

        if (!_activeDownloads.TryAdd(jobId, true)) return;

        try
        {
            if (!File.Exists(workItem.Path))
            {
                Console.WriteLine($"Cluster: Source file {workItem.Path} no longer exists — discarding remote result");
                workItem.Status       = WorkItemStatus.Failed;
                workItem.ErrorMessage = "Source file was removed during encoding";
                workItem.CompletedAt  = DateTime.UtcNow;
                _remoteJobs.TryRemove(jobId, out _);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Failed);
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }
                return;
            }

            // Phase 1: Download
            var downloadSucceeded = await DownloadOutputAsync(jobId, nodeBaseUrl, workItem);
            if (!downloadSucceeded) return; // Will retry via pending completions

            // Phase 2: Validate
            var options = _transcodingService.GetLastOptions() ?? new EncoderOptions();
            var ext     = options.Format == "mp4" ? ".mp4" : ".mkv";
            var baseName = Path.GetFileNameWithoutExtension(workItem.FileName);
            var outputFileName = $"{baseName} [snacks]{ext}";
            string outputDir;
            if (!string.IsNullOrEmpty(options.EncodeDirectory))
                outputDir = options.EncodeDirectory;
            else if (!string.IsNullOrEmpty(options.OutputDirectory))
                outputDir = options.OutputDirectory;
            else
                outputDir = _fileService.GetDirectory(workItem.Path);
            var outputPath = Path.Combine(outputDir, outputFileName);

            var validation = await ValidateOutputAsync(jobId, workItem, outputPath);

            if (validation == OutputValidation.Success)
            {
                await FinalizeCompletionAsync(jobId, workItem, outputPath, options, nodeBaseUrl);
            }
            else if (validation == OutputValidation.DownloadCorrupt)
            {
                // Re-download, NOT re-encode
                await RetryDownloadAsync(jobId, nodeBaseUrl, workItem);
            }
            else // validation == OutputValidation.EncodeCorrupt
            {
                // Output is fundamentally broken — re-encode
                await RequeueForEncodingAsync(jobId, workItem, nodeBaseUrl);
            }
        }
        finally
        {
            _activeDownloads.TryRemove(jobId, out _);

            // Only release the node if the job is fully done (removed from _remoteJobs).
            // If it's still tracked, a retry is pending and the node should stay reserved.
            if (!_remoteJobs.ContainsKey(jobId)
                && workItem.AssignedNodeId != null
                && _nodes.TryGetValue(workItem.AssignedNodeId, out var dlNode)
                && dlNode.ActiveWorkItemId == jobId)
            {
                dlNode.ActiveWorkItemId = null;
                dlNode.Status = NodeStatus.Online;
            }
        }
    }

    /// <summary>Result of output validation.</summary>
    private enum OutputValidation { Success, DownloadCorrupt, EncodeCorrupt }

    /// <summary>Downloads the encoded output from the worker node. Returns true on success.</summary>
    private async Task<bool> DownloadOutputAsync(string jobId, string nodeBaseUrl, WorkItem workItem)
    {
        try
        {
            // WAL: Encoding → Downloading (only on first attempt, not retries)
            if (workItem.RemoteJobPhase != "Downloading")
            {
                var dlScope = await _stateTransitions.BeginAsync(jobId, workItem.RemoteJobPhase ?? "Encoding", "Downloading");

                workItem.Status           = WorkItemStatus.Downloading;
                workItem.RemoteJobPhase   = "Downloading";
                workItem.TransferProgress = 0;
                workItem.ErrorMessage     = null;

                // Update node status to Downloading (direct completion path bypasses heartbeat)
                if (workItem.AssignedNodeId != null && _nodes.TryGetValue(workItem.AssignedNodeId, out var dlStatusNode))
                    UpdateNodeStatus(dlStatusNode.NodeId, NodeStatus.Downloading, workItem.Id, workItem.FileName);

                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Downloading");

                await dlScope.CompleteAsync();
            }
            else
            {
                workItem.TransferProgress = 0;
                workItem.ErrorMessage     = null;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            }

            var options  = _transcodingService.GetLastOptions() ?? new EncoderOptions();
            var ext      = options.Format == "mp4" ? ".mp4" : ".mkv";
            var baseName = Path.GetFileNameWithoutExtension(workItem.FileName);
            var outputFileName = $"{baseName} [snacks]{ext}";

            string outputDir;
            if (!string.IsNullOrEmpty(options.EncodeDirectory))
                outputDir = options.EncodeDirectory;
            else if (!string.IsNullOrEmpty(options.OutputDirectory))
                outputDir = options.OutputDirectory;
            else
                outputDir = _fileService.GetDirectory(workItem.Path);

            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, outputFileName);

            _jobCts.TryGetValue(jobId, out var dlCts);
            var dlClient = _discovery.CreateAuthenticatedClient();
            await _fileTransfer.DownloadFileFromNodeAsync(
                dlClient, nodeBaseUrl, jobId, outputPath, workItem, dlCts?.Token ?? CancellationToken.None);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Download failed for {workItem.FileName}: {ex.Message}");

            var retryCount = _downloadRetryCounts.AddOrUpdate(jobId, 1, (_, c) => c + 1);
            const int MaxDownloadRetries = 10;

            if (retryCount >= MaxDownloadRetries)
            {
                Console.WriteLine($"Cluster: Download failed after {MaxDownloadRetries} attempts — re-queuing for fresh encode");
                // Release the node before clearing the assignment
                if (workItem.AssignedNodeId != null && _nodes.TryGetValue(workItem.AssignedNodeId, out var maxRetryNode)
                    && maxRetryNode.ActiveWorkItemId == jobId)
                {
                    maxRetryNode.ActiveWorkItemId = null;
                    maxRetryNode.Status = NodeStatus.Online;
                }
                _remoteJobs.TryRemove(jobId, out _);
                _downloadRetryCounts.TryRemove(jobId, out _);
                if (_jobCts.TryRemove(jobId, out var maxRetryCts))
                    maxRetryCts.Dispose();
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                workItem.AssignedNodeId   = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase   = null;
                workItem.ErrorMessage     = null;
                _transcodingService.RequeueWorkItem(workItem);
            }
            else
            {
                workItem.RemoteJobPhase = "Downloading";
                workItem.ErrorMessage   = $"Download retry {retryCount}/{MaxDownloadRetries} — {ex.Message}";
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Downloading");

                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(60));
                    if (_jobCts.TryGetValue(jobId, out var retryCts) && retryCts.IsCancellationRequested) return;
                    if (_remoteJobs.TryGetValue(jobId, out var retryItem) && retryItem.RemoteJobPhase == "Downloading")
                    {
                        Console.WriteLine($"Cluster: Retrying download for {retryItem.FileName} (attempt {retryCount + 1})...");
                        retryItem.ErrorMessage = null;
                        await HandleRemoteCompletionAsync(jobId, nodeBaseUrl);
                    }
                });
            }
            return false;
        }
    }

    /// <summary>Validates the downloaded output against the source probe data.</summary>
    private async Task<OutputValidation> ValidateOutputAsync(string jobId, WorkItem workItem, string outputPath)
    {
        if (!File.Exists(outputPath))
            return OutputValidation.DownloadCorrupt;

        var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
        if (workItem.Probe != null && !_ffprobeService.ConvertedSuccessfully(workItem.Probe, outputProbe))
        {
            var failCount = _downloadRetryCounts.AddOrUpdate($"_validation_{jobId}", 1, (_, c) => c + 1);

            if (failCount >= 3)
            {
                Console.WriteLine($"Cluster: Output validation failed {failCount} times for {workItem.FileName} — output corrupt, need re-encode");
                try { File.Delete(outputPath); } catch { }
                return OutputValidation.EncodeCorrupt;
            }

            // Transient validation failure — try re-downloading
            Console.WriteLine($"Cluster: Validation failed ({failCount}) — duration mismatch, retrying download");
            try { File.Delete(outputPath); } catch { }
            return OutputValidation.DownloadCorrupt;
        }

        return OutputValidation.Success;
    }

    /// <summary>Finalizes a successfully validated output.</summary>
    private async Task FinalizeCompletionAsync(string jobId, WorkItem workItem, string outputPath, EncoderOptions options, string nodeBaseUrl)
    {
        // WAL: Downloading → Completed
        var completeScope = await _stateTransitions.BeginAsync(jobId, "Downloading", "Completed");

        await _transcodingService.HandleRemoteCompletion(workItem, outputPath, options);
        await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);

        await completeScope.CompleteAsync();
        _activeTransitions.TryRemove(jobId, out _);

        try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }

        if (workItem.AssignedNodeId != null)
        {
            UpdateNodeStatus(workItem.AssignedNodeId, NodeStatus.Online);
            if (_nodes.TryGetValue(workItem.AssignedNodeId, out var node))
            {
                node.CompletedJobs++;
                // Release the node so dispatch can assign new work
                if (node.ActiveWorkItemId == jobId)
                    node.ActiveWorkItemId = null;
            }
        }

        _remoteJobs.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove($"_validation_{jobId}", out _);
        if (_jobCts.TryRemove(jobId, out var completedCts))
            completedCts.Dispose();
        Console.WriteLine($"Cluster: Remote job {workItem.FileName} completed successfully");
    }

    /// <summary>Schedules a re-download of a corrupt output file without deleting the node's copy.</summary>
    private async Task RetryDownloadAsync(string jobId, string nodeBaseUrl, WorkItem workItem)
    {
        workItem.RemoteJobPhase = "Downloading";
        workItem.ErrorMessage   = "Output corrupt — re-downloading";
        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

        // Schedule retry after the caller's finally block releases _activeDownloads.
        // Do NOT delete the node's output — we need it for the re-download.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            await HandleRemoteCompletionAsync(jobId, nodeBaseUrl);
        });
    }

    /// <summary>Re-queues a job for fresh encoding due to corrupt output.</summary>
    private async Task RequeueForEncodingAsync(string jobId, WorkItem workItem, string nodeBaseUrl)
    {
        await CompleteActiveTransitionAsync(jobId);
        try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }
        var prevNodeId = workItem.AssignedNodeId;

        // Release the node before clearing the assignment
        if (prevNodeId != null && _nodes.TryGetValue(prevNodeId, out var requeueNode) && requeueNode.ActiveWorkItemId == jobId)
            requeueNode.ActiveWorkItemId = null;

        _remoteJobs.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove($"_validation_{jobId}", out _);
        if (_jobCts.TryRemove(jobId, out var cts))
            cts.Dispose();
        await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
        workItem.AssignedNodeId   = null;
        workItem.AssignedNodeName = null;
        workItem.RemoteJobPhase   = null;
        workItem.ErrorMessage     = null;
        _transcodingService.RequeueWorkItem(workItem);
        if (prevNodeId != null) UpdateNodeStatus(prevNodeId, NodeStatus.Online);
    }

    /// <summary>
    ///     Handles a failure report from a worker node. Checks for completed output
    ///     before re-queuing or marking as permanently failed.
    /// </summary>
    public async Task HandleRemoteFailureAsync(string jobId, string? errorMessage)
    {
        if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;

        Console.WriteLine($"Cluster: Remote job {workItem.FileName} failed: {errorMessage}");

        if (workItem.AssignedNodeId != null && _nodes.TryGetValue(workItem.AssignedNodeId, out var node))
        {
            try
            {
                var baseUrl = NodeBaseUrl(node);
                var checkResponse = await _discovery.CreateAuthenticatedClient()
                    .GetAsync($"{baseUrl}/api/cluster/files/{jobId}/output");
                if (checkResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Cluster: Node still has output for {workItem.FileName} — attempting download");
                    await HandleRemoteCompletionAsync(jobId, baseUrl);
                    return;
                }
            }
            catch { }

            node.FailedJobs++;
            UpdateNodeStatus(node.NodeId, NodeStatus.Online);
        }

        await HandleNodeFailureAsync(jobId);
    }

    /// <summary>
    ///     Cancels a remote job on a worker node — called when the user cancels a work item.
    /// </summary>
    public async Task CancelRemoteJobOnNodeAsync(string jobId, string nodeId)
    {
        _remoteJobs.TryRemove(jobId, out _);
        await CompleteActiveTransitionAsync(jobId);

        if (_jobCts.TryRemove(jobId, out var jobCts))
        {
            jobCts.Cancel();
            jobCts.Dispose();
        }

        // Symmetric cleanup — match HandleNodeFailureAsync
        _activeUploads.TryRemove(jobId, out _);
        _activeDownloads.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove($"_idle_grace_{jobId}", out _);
        _downloadRetryCounts.TryRemove($"_validation_{jobId}", out _);

        if (_nodes.TryGetValue(nodeId, out var node))
        {
            try
            {
                var client  = _discovery.CreateAuthenticatedClient();
                var baseUrl = NodeBaseUrl(node);
                await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{jobId}");
                await client.DeleteAsync($"{baseUrl}/api/cluster/files/{jobId}");
                Console.WriteLine($"Cluster: Sent cancel for job {jobId} to {node.Hostname}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Failed to cancel job on node: {ex.Message}");
            }

            UpdateNodeStatus(nodeId, NodeStatus.Online);
        }
    }

    /// <summary>
    ///     Handles node failure: cancels active transfers, increments failure count,
    ///     and re-queues or marks as permanently failed.
    /// </summary>
    private async Task HandleNodeFailureAsync(string jobId)
    {
        // Don't interfere with an active download — let it complete or fail on its own
        if (_activeDownloads.ContainsKey(jobId))
        {
            Console.WriteLine($"Cluster: Skipping failure handling for {jobId} — download in progress");
            return;
        }

        if (!_remoteJobs.TryRemove(jobId, out var workItem)) return;

        await CompleteActiveTransitionAsync(jobId);

        if (_jobCts.TryRemove(jobId, out var jobCts))
        {
            jobCts.Cancel();
            jobCts.Dispose();
        }

        _activeUploads.TryRemove(jobId, out _);
        _activeDownloads.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove(jobId, out _);
        _downloadRetryCounts.TryRemove($"_idle_grace_{jobId}", out _);
        _downloadRetryCounts.TryRemove($"_validation_{jobId}", out _);

        workItem.RemoteFailureCount++;
        workItem.ErrorMessage = $"Remote: failed (attempt {workItem.RemoteFailureCount})";
        await _mediaFileRepo.IncrementRemoteFailureCountAsync(Path.GetFullPath(workItem.Path));

        if (workItem.RemoteFailureCount >= 3)
        {
            workItem.Status           = WorkItemStatus.Failed;
            workItem.CompletedAt      = DateTime.UtcNow;
            workItem.AssignedNodeId   = null;
            workItem.AssignedNodeName = null;
            workItem.RemoteJobPhase   = null;
            await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Failed);
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            await _transcodingService.MarkWorkItemFailed(workItem.Id, workItem.ErrorMessage);
        }
        else
        {
            workItem.AssignedNodeId   = null;
            workItem.AssignedNodeName = null;
            workItem.RemoteJobPhase   = null;
            await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
            _transcodingService.RequeueWorkItem(workItem);
        }
    }

    /******************************************************************
     *  Node Selection
     ******************************************************************/

    /// <summary>
    ///     Selects the best available worker node for a job using a scoring algorithm.
    ///     Among equal-scored nodes, prefers the one idle longest.
    /// </summary>
    public ClusterNode? FindBestWorker(WorkItem workItem, EncoderOptions options)
    {
        var available = _nodes.Values
            .Where(n => n.Role == "node" && n.Status == NodeStatus.Online
                && !n.IsPaused && n.ActiveWorkItemId == null
                && (!_activeDispatchTasks.TryGetValue(n.NodeId, out var dt) || dt.IsCompleted)
                && (!_nodeDispatchCooldowns.TryGetValue(n.NodeId, out var cd) || DateTime.UtcNow >= cd))
            .ToList();

        if (available.Count == 0) return null;

        ClusterNode? best = null;
        int bestScore     = 0;

        foreach (var node in available)
        {
            int score = ScoreNode(node, workItem, options);
            if (score > bestScore || (score == bestScore && best != null &&
                node.LastHeartbeat < best.LastHeartbeat))
            {
                best      = node;
                bestScore = score;
            }
        }

        if (bestScore <= 0)
        {
            foreach (var node in available)
            {
                var caps = node.Capabilities;
                Console.WriteLine($"Cluster: Node {node.Hostname} scored {ScoreNode(node, workItem, options)} — " +
                    $"disk={caps?.AvailableDiskSpaceBytes / (1024 * 1024)}MB, " +
                    $"fileSize={workItem.Size / (1024 * 1024)}MB, " +
                    $"gpu={caps?.GpuVendor ?? "n/a"}");
            }
            return null;
        }

        return best;
    }

    /// <summary> Assigns a numeric score to a candidate worker node for the given job. </summary>
    private static int ScoreNode(ClusterNode node, WorkItem workItem, EncoderOptions options)
    {
        int score = 1;
        var caps  = node.Capabilities;
        if (caps == null) return score; // Capabilities not yet received — allow dispatch with base score

        if (caps.AvailableDiskSpaceBytes < workItem.Size * 2.5)
            return -100;

        var hw = options.HardwareAcceleration?.ToLower() ?? "auto";

        if (hw != "auto" && hw != "none" &&
            string.Equals(caps.GpuVendor, hw, StringComparison.OrdinalIgnoreCase))
            score += 10;

        if (hw == "auto" && caps.GpuVendor != "none" && !string.IsNullOrEmpty(caps.GpuVendor))
            score += 5;

        var encoder = options.Encoder?.ToLower() ?? "";
        if (caps.SupportedEncoders.Any(e => e.Equals(encoder, StringComparison.OrdinalIgnoreCase)))
            score += 3;

        return score;
    }

    /******************************************************************
     *  Dispatch Cooldown & Node Reset
     ******************************************************************/

    /// <summary>
    ///     Applies exponential backoff cooldown to a node after a dispatch failure.
    ///     After reaching the failure threshold, automatically resets the node.
    /// </summary>
    private void ApplyDispatchCooldown(string nodeId)
    {
        var failures = _nodeConsecutiveFailures.AddOrUpdate(nodeId, 1, (_, c) => c + 1);
        var cooldownSeconds = Math.Min(60, 5 * Math.Pow(2, failures - 1));
        _nodeDispatchCooldowns[nodeId] = DateTime.UtcNow.AddSeconds(cooldownSeconds);

        if (failures >= 5)
        {
            Console.WriteLine($"Cluster: Node {nodeId} has failed {failures} consecutive dispatches — triggering auto-reset");
            _ = Task.Run(() => ResetNodeAsync(nodeId));
        }
        else
        {
            var nodeName = _nodes.TryGetValue(nodeId, out var n) ? n.Hostname : nodeId;
            Console.WriteLine($"Cluster: Node {nodeName} dispatch failed ({failures} consecutive) — cooling down for {cooldownSeconds:F0}s");
        }
    }

    /// <summary>
    ///     Force-resets a worker node by cancelling all active jobs, clearing all
    ///     tracking state on both master and worker, and requeuing any in-flight items.
    ///     Triggered automatically when consecutive dispatch failures exceed the threshold.
    /// </summary>
    public async Task ResetNodeAsync(string nodeId)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return;

        Console.WriteLine($"Cluster: Auto-resetting node {node.Hostname} ({nodeId})...");

        // Find any active job for this node
        var activeJobId = node.ActiveWorkItemId;
        WorkItem? activeWorkItem = null;

        if (activeJobId != null)
            _remoteJobs.TryGetValue(activeJobId, out activeWorkItem);

        // Also scan _remoteJobs for anything assigned to this node
        var nodeJobs = _remoteJobs.Where(kvp => kvp.Value.AssignedNodeId == nodeId).ToList();

        foreach (var (jobId, workItem) in nodeJobs)
        {
            // Cancel the CTS
            if (_jobCts.TryRemove(jobId, out var cts))
            {
                try { cts.Cancel(); cts.Dispose(); } catch { }
            }

            // Clear tracking
            await CompleteActiveTransitionAsync(jobId);
            _remoteJobs.TryRemove(jobId, out _);
            _activeUploads.TryRemove(jobId, out _);
            _activeDownloads.TryRemove(jobId, out _);
            _downloadRetryCounts.TryRemove(jobId, out _);
            _downloadRetryCounts.TryRemove($"_idle_grace_{jobId}", out _);
            _downloadRetryCounts.TryRemove($"_validation_{jobId}", out _);

            // Tell the worker to cancel and clean up
            try
            {
                var client  = _discovery.CreateAuthenticatedClient();
                var baseUrl = NodeBaseUrl(node);
                await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{jobId}");
                await client.DeleteAsync($"{baseUrl}/api/cluster/files/{jobId}");
            }
            catch { }

            // Clear DB assignment and requeue
            workItem.AssignedNodeId   = null;
            workItem.AssignedNodeName = null;
            workItem.RemoteJobPhase   = null;
            workItem.TransferProgress = 0;
            workItem.ErrorMessage     = null;
            try
            {
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
            }
            catch { }
            _transcodingService.RequeueWorkItem(workItem);
        }

        // Reset the node state
        node.ActiveWorkItemId = null;
        node.ActiveFileName   = null;
        node.ActiveProgress   = 0;
        node.Status           = NodeStatus.Online;
        await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);

        // Clear dispatch tracking and apply a post-reset cooldown
        _activeDispatchTasks.TryRemove(nodeId, out _);
        _nodeConsecutiveFailures.TryRemove(nodeId, out _);
        _nodeDispatchCooldowns[nodeId] = DateTime.UtcNow.AddSeconds(30);

        Console.WriteLine($"Cluster: Node {node.Hostname} reset complete — {nodeJobs.Count} job(s) requeued, 30s cooldown applied");
    }

    /******************************************************************
     *  WAL Cleanup
     ******************************************************************/

    /// <summary>
    ///     Completes any in-flight WAL transition for a job so that the incomplete
    ///     entry does not trigger spurious recovery on the next startup. Called from
    ///     every failure, cancellation, and re-queue path.
    /// </summary>
    private async Task CompleteActiveTransitionAsync(string jobId)
    {
        if (_activeTransitions.TryRemove(jobId, out var scope))
        {
            try { await scope.CompleteAsync(); }
            catch (Exception ex) { Console.WriteLine($"Cluster: WAL cleanup failed for {jobId}: {ex.Message}"); }
        }
    }

    /******************************************************************
     *  Recovery
     ******************************************************************/

    /// <summary>
    ///     Processes incomplete WAL entries left by the previous session. Each entry
    ///     represents a phase transition that was started but never completed, meaning
    ///     the master crashed mid-operation. Corrects the DB phase so that the
    ///     subsequent node-probing recovery loop starts from the right state.
    /// </summary>
    private async Task RecoverIncompleteTransitionsAsync()
    {
        var incomplete = await _stateTransitions.GetIncompleteTransitionsAsync();
        if (incomplete.Count == 0) return;

        Console.WriteLine($"Cluster: Found {incomplete.Count} incomplete WAL transition(s) — reconciling...");

        foreach (var transition in incomplete)
        {
            var mediaFile = await _mediaFileRepo.GetByRemoteWorkItemIdAsync(transition.WorkItemId);
            if (mediaFile == null)
            {
                Console.WriteLine($"Cluster: WAL entry for unknown job {transition.WorkItemId} ({transition.FromPhase} → {transition.ToPhase}) — discarding");
                await _mediaFileRepo.CompleteTransitionAsync(transition.Id);
                continue;
            }

            Console.WriteLine($"Cluster: WAL interrupted: {mediaFile.FileName} was transitioning {transition.FromPhase} → {transition.ToPhase} (DB phase: {mediaFile.RemoteJobPhase})");

            switch (transition.ToPhase)
            {
                case "Uploading":
                    // Crash during Queued → Uploading: assignment may be half-written.
                    // Reset to Queued so dispatch picks it up cleanly.
                    if (mediaFile.AssignedNodeId != null)
                    {
                        Console.WriteLine($"Cluster: Resetting {mediaFile.FileName} to Queued (interrupted assignment)");
                        await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                    }
                    break;

                case "Encoding":
                    // Crash during Uploading → Encoding: upload finished but phase wasn't
                    // updated. Correct the DB phase so node-probing recovery sees "Encoding".
                    if (mediaFile.RemoteJobPhase == "Uploading" && mediaFile.AssignedNodeId != null)
                    {
                        Console.WriteLine($"Cluster: Advancing {mediaFile.FileName} to Encoding phase (upload was complete)");
                        await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Encoding");
                    }
                    break;

                case "Downloading":
                    // Crash during Encoding → Downloading: encoding done on node, master
                    // didn't start downloading. Correct DB phase so node-probing detects output.
                    if (mediaFile.RemoteJobPhase == "Encoding" && mediaFile.AssignedNodeId != null)
                    {
                        Console.WriteLine($"Cluster: Advancing {mediaFile.FileName} to Downloading phase (encoding was complete)");
                        await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Downloading");
                    }
                    break;

                case "Completed":
                    // Crash during Downloading → Completed: download finished, finalization
                    // didn't complete. Check if the output file exists locally — if so, the
                    // work is done and we just need to clear the assignment.
                    if (mediaFile.AssignedNodeId != null)
                    {
                        var options = LoadEncoderOptions();
                        var ext = options.Format == "mp4" ? ".mp4" : ".mkv";
                        var baseName = Path.GetFileNameWithoutExtension(mediaFile.FileName);
                        var outputFileName = $"{baseName} [snacks]{ext}";

                        string outputDir;
                        if (!string.IsNullOrEmpty(options.EncodeDirectory))
                            outputDir = options.EncodeDirectory;
                        else if (!string.IsNullOrEmpty(options.OutputDirectory))
                            outputDir = options.OutputDirectory;
                        else
                            outputDir = _fileService.GetDirectory(mediaFile.FilePath);

                        var outputPath = Path.Combine(outputDir, outputFileName);

                        if (File.Exists(outputPath))
                        {
                            Console.WriteLine($"Cluster: Output exists for {mediaFile.FileName} — finalizing interrupted completion");
                            await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Completed);
                        }
                        else
                        {
                            // Output didn't survive — fall back to Downloading so node-probing re-downloads
                            Console.WriteLine($"Cluster: Output missing for {mediaFile.FileName} — reverting to Downloading for re-download");
                            await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Downloading");
                        }
                    }
                    break;
            }

            // Mark the WAL entry as resolved regardless of outcome
            await _mediaFileRepo.CompleteTransitionAsync(transition.Id);
        }

        Console.WriteLine("Cluster: WAL recovery complete");
    }

    /// <summary>
    ///     On master startup, checks for remote jobs that were in-flight when the
    ///     previous session ended. Attempts to reconnect to nodes and retrieve results,
    ///     or re-queues for fresh dispatch.
    /// </summary>
    private async Task RecoverRemoteJobsAsync(CancellationToken ct = default)
    {
        try
        {
            // Phase 0: Recover from incomplete WAL transitions (crashes mid-phase-change)
            await RecoverIncompleteTransitionsAsync();

            var activeJobs = await _mediaFileRepo.GetActiveRemoteJobsAsync();
            if (activeJobs.Count == 0) return;

            Console.WriteLine($"Cluster: Recovering {activeJobs.Count} remote job(s) from database...");

            // Phase 0.5: Rebuild node registry from database assignments so that
            // the master's in-memory _nodes dictionary is restored after a restart.
            // Nodes are marked Unreachable until heartbeats confirm they're alive.
            var knownNodeIds = new HashSet<string>();
            foreach (var mediaFile in activeJobs)
            {
                if (string.IsNullOrEmpty(mediaFile.AssignedNodeId) || !knownNodeIds.Add(mediaFile.AssignedNodeId))
                    continue;

                var recoveredNode = new ClusterNode
                {
                    NodeId        = mediaFile.AssignedNodeId,
                    Hostname      = mediaFile.AssignedNodeName ?? "recovered",
                    IpAddress     = mediaFile.AssignedNodeIp ?? "unknown",
                    Port          = mediaFile.AssignedNodePort ?? 6767,
                    Role          = "node",
                    Status        = NodeStatus.Unreachable,
                    LastHeartbeat = DateTime.UtcNow,
                    Capabilities  = new WorkerCapabilities()
                };
                _discovery.RegisterOrUpdateNode(recoveredNode, fromHandshake: false);
                Console.WriteLine($"Cluster: Recovered node {recoveredNode.Hostname} ({recoveredNode.IpAddress}:{recoveredNode.Port}) from database");
            }

            foreach (var mediaFile in activeJobs)
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(mediaFile.FilePath))
                {
                    Console.WriteLine($"Cluster: Source file missing for {mediaFile.FileName} — clearing assignment");
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Unseen);
                    continue;
                }

                if (string.IsNullOrEmpty(mediaFile.RemoteWorkItemId))
                {
                    Console.WriteLine($"Cluster: No RemoteWorkItemId for {mediaFile.FileName} — re-queuing");
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                    continue;
                }

                var jobId    = mediaFile.RemoteWorkItemId;
                var workItem = await _transcodingService.CreateWorkItemWithIdAsync(jobId, mediaFile.FilePath);
                if (workItem == null)
                {
                    Console.WriteLine($"Cluster: Failed to reconstruct {mediaFile.FileName} — clearing assignment");
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Unseen);
                    continue;
                }

                var baseUrl = $"{(_config.UseHttps ? "https" : "http")}://{mediaFile.AssignedNodeIp}:{mediaFile.AssignedNodePort}";
                bool nodeReachable = false;

                for (int attempt = 0; attempt < 12; attempt++)
                {
                    try
                    {
                        var hbResponse = await _discovery.CreateAuthenticatedClient()
                            .GetAsync($"{baseUrl}/api/cluster/heartbeat");
                        if (hbResponse.IsSuccessStatusCode) { nodeReachable = true; break; }
                    }
                    catch { }

                    if (attempt < 11)
                    {
                        Console.WriteLine($"Cluster: Waiting for node at {baseUrl} for {mediaFile.FileName} (attempt {attempt + 1}/12)...");
                        await Task.Delay(TimeSpan.FromSeconds(10));
                    }
                }

                if (!nodeReachable)
                {
                    Console.WriteLine($"Cluster: Node unreachable after 2 minutes for {mediaFile.FileName} — re-queuing");
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                    _transcodingService.RequeueWorkItem(workItem);
                    continue;
                }

                // Check if node is actively encoding or has completed this job
                try
                {
                    var hbBody = await (await _discovery.CreateAuthenticatedClient()
                        .GetAsync($"{baseUrl}/api/cluster/heartbeat")).Content.ReadAsStringAsync();
                    var hbData = JsonSerializer.Deserialize<JsonElement>(hbBody);

                    // Check if encoding already finished — completedJobId means output is ready
                    if (hbData.TryGetProperty("completedJobId", out var completedJob) &&
                        completedJob.ValueKind != JsonValueKind.Null &&
                        completedJob.GetString() == jobId)
                    {
                        Console.WriteLine($"Cluster: Node has completed encoding {mediaFile.FileName} — downloading");
                        var recDlCts1 = new CancellationTokenSource();
                        _jobCts[workItem.Id]        = recDlCts1;
                        workItem.Status             = WorkItemStatus.Downloading;
                        workItem.AssignedNodeId     = mediaFile.AssignedNodeId;
                        workItem.AssignedNodeName   = mediaFile.AssignedNodeName ?? "recovered";
                        workItem.RemoteJobPhase     = "Downloading";
                        workItem.RemoteFailureCount = mediaFile.RemoteFailureCount;
                        workItem.ErrorMessage       = null;
                        _remoteJobs[workItem.Id]    = workItem;
                        if (mediaFile.RemoteFailureCount > 0)
                            _downloadRetryCounts[workItem.Id] = mediaFile.RemoteFailureCount * 3;
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                        // Set node to Downloading so heartbeat won't interfere
                        if (mediaFile.AssignedNodeId != null && _nodes.TryGetValue(mediaFile.AssignedNodeId, out var dlRecNode))
                        {
                            dlRecNode.ActiveWorkItemId = workItem.Id;
                            dlRecNode.Status = NodeStatus.Downloading;
                        }
                        await HandleRemoteCompletionAsync(workItem.Id, baseUrl);
                        continue;
                    }

                    // If node is only receiving (not encoding), fall through to the
                    // partial upload resume path instead of tracking as encoding
                    bool isOnlyReceiving = hbData.TryGetProperty("receivingJobId", out var recvJob) &&
                        recvJob.ValueKind != JsonValueKind.Null && recvJob.GetString() == jobId;

                    if (!isOnlyReceiving &&
                        hbData.TryGetProperty("currentJobId", out var curJob) && curJob.ValueKind != JsonValueKind.Null)
                    {
                        if (curJob.GetString() == jobId)
                        {
                            int recoveredProgress = 0;
                            if (hbData.TryGetProperty("progress", out var progProp))
                                recoveredProgress = progProp.GetInt32();

                            Console.WriteLine($"Cluster: Node is actively encoding {mediaFile.FileName} at {recoveredProgress}% — tracking");
                            var recEncCts = new CancellationTokenSource();
                            _jobCts[workItem.Id]      = recEncCts;
                            workItem.Status           = WorkItemStatus.Processing;
                            workItem.Progress         = recoveredProgress;
                            workItem.AssignedNodeId   = mediaFile.AssignedNodeId;
                            workItem.AssignedNodeName = mediaFile.AssignedNodeName ?? "recovered";
                            workItem.RemoteJobPhase   = "Encoding";
                            workItem.ErrorMessage     = null;
                            _remoteJobs[workItem.Id]  = workItem;
                            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                            continue;
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Cluster: Recovery heartbeat check failed: {ex.Message}"); }

                // Check if node has completed output
                try
                {
                    var outputResponse = await _discovery.CreateAuthenticatedClient()
                        .GetAsync($"{baseUrl}/api/cluster/files/{jobId}/output");
                    if (outputResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Cluster: Node has output for {mediaFile.FileName} — downloading");
                        var recDlCts2 = new CancellationTokenSource();
                        _jobCts[workItem.Id]        = recDlCts2;
                        workItem.Status             = WorkItemStatus.Downloading;
                        workItem.AssignedNodeId     = mediaFile.AssignedNodeId;
                        workItem.AssignedNodeName   = mediaFile.AssignedNodeName ?? "recovered";
                        workItem.RemoteJobPhase     = "Downloading";
                        workItem.RemoteFailureCount = mediaFile.RemoteFailureCount;
                        workItem.ErrorMessage       = null;
                        _remoteJobs[workItem.Id]    = workItem;
                        if (mediaFile.RemoteFailureCount > 0)
                            _downloadRetryCounts[workItem.Id] = mediaFile.RemoteFailureCount * 3;
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                        // Set node to Downloading so heartbeat won't interfere
                        if (mediaFile.AssignedNodeId != null && _nodes.TryGetValue(mediaFile.AssignedNodeId, out var dlRecNode2))
                        {
                            dlRecNode2.ActiveWorkItemId = workItem.Id;
                            dlRecNode2.Status = NodeStatus.Downloading;
                        }
                        await HandleRemoteCompletionAsync(workItem.Id, baseUrl);
                        continue;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Cluster: Recovery output check failed: {ex.Message}"); }

                // Check for partial source file upload
                try
                {
                    var client = _discovery.CreateAuthenticatedClient();
                    var receivedBytes = await _fileTransfer.GetNodeReceivedBytesAsync(client, baseUrl, jobId);
                    if (receivedBytes > 0)
                    {
                        Console.WriteLine($"Cluster: Node has {receivedBytes / 1048576}MB of {mediaFile.FileName} — resuming upload");
                        workItem.AssignedNodeId   = mediaFile.AssignedNodeId;
                        workItem.AssignedNodeName = mediaFile.AssignedNodeName ?? "recovered";
                        workItem.RemoteJobPhase   = "Uploading";
                        _remoteJobs[workItem.Id]  = workItem;
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                        var options = LoadEncoderOptions();
                        var nodeForDispatch = _nodes.Values.FirstOrDefault(n => n.NodeId == mediaFile.AssignedNodeId)
                            ?? new ClusterNode
                            {
                                NodeId    = mediaFile.AssignedNodeId!,
                                IpAddress = mediaFile.AssignedNodeIp!,
                                Port      = mediaFile.AssignedNodePort ?? 6767,
                                Hostname  = mediaFile.AssignedNodeName ?? "recovered"
                            };

                        _ = Task.Run(async () =>
                        {
                            var recoveryCts = new CancellationTokenSource();
                            _jobCts[workItem.Id] = recoveryCts;
                            try
                            {
                                if (!_activeUploads.TryAdd(workItem.Id, true))
                                {
                                    Console.WriteLine($"Cluster: Upload already active for {workItem.FileName}");
                                    UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Online);
                                    return;
                                }

                                workItem.Status         = WorkItemStatus.Uploading;
                                workItem.RemoteJobPhase = "Uploading";
                                workItem.ErrorMessage   = null;
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Uploading, workItem.Id, workItem.FileName);

                                // Build metadata for autonomous encoding on the worker
                                var metadata = new JobMetadata
                                {
                                    JobId    = workItem.Id,
                                    FileName = workItem.FileName,
                                    FileSize = workItem.Size,
                                    Options  = CloneOptions(options),
                                    Probe    = workItem.Probe,
                                    Duration = workItem.Length,
                                    Bitrate  = workItem.Bitrate,
                                    IsHevc   = workItem.IsHevc
                                };

                                var uploadClient = _discovery.CreateAuthenticatedClient();
                                await _fileTransfer.RegisterMetadataAsync(uploadClient, baseUrl, metadata, recoveryCts.Token);
                                await _fileTransfer.UploadFileToNodeAsync(uploadClient, baseUrl, workItem, recoveryCts.Token);

                                var finalSize = await _fileTransfer.GetNodeReceivedBytesAsync(uploadClient, baseUrl, workItem.Id);
                                if (finalSize != workItem.Size)
                                {
                                    Console.WriteLine($"Cluster: Resumed upload verification failed");
                                    _remoteJobs.TryRemove(workItem.Id, out _);
                                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                                    _transcodingService.RequeueWorkItem(workItem);
                                    UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Online);
                                    return;
                                }

                                // WAL: Uploading → Encoding (recovery path)
                                var recEncScope = await _stateTransitions.BeginAsync(workItem.Id, "Uploading", "Encoding");

                                workItem.RemoteJobPhase  = "Encoding";
                                workItem.TransferProgress = 0;
                                await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Encoding");
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                                await recEncScope.CompleteAsync();

                                // Node is no longer receiving — transition from Uploading to Busy
                                UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

                                Console.WriteLine($"Cluster: Recovered job {workItem.FileName} dispatched to {nodeForDispatch.Hostname} (autonomous encoding)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Cluster: Recovery upload failed for {workItem.FileName}: {ex.Message}");
                                await CompleteActiveTransitionAsync(workItem.Id);
                                _remoteJobs.TryRemove(workItem.Id, out _);
                                try { await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued); } catch { }
                                _transcodingService.RequeueWorkItem(workItem);
                                UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Online);
                            }
                            finally
                            {
                                _activeUploads.TryRemove(workItem.Id, out _);
                                if (_jobCts.TryRemove(workItem.Id, out var rCts)) rCts.Dispose();
                            }
                        });
                        continue;
                    }
                }
                catch (Exception ex) { Console.WriteLine($"Cluster: Recovery partial upload check failed: {ex.Message}"); }

                Console.WriteLine($"Cluster: Could not recover {mediaFile.FileName} — re-queuing");
                await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                _transcodingService.RequeueWorkItem(workItem);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Error recovering remote jobs: {ex.Message}");
        }
        finally
        {
            _recoveryComplete.TrySetResult();
            Console.WriteLine("Cluster: Recovery complete — heartbeat and dispatch now active");
        }
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    /// <summary> Loads encoder options from the transcoding service or disk settings. </summary>
    private EncoderOptions LoadEncoderOptions()
    {
        var options = _transcodingService.GetLastOptions();
        if (options == null)
        {
            try
            {
                var settingsPath = Path.Combine(_workDir, "config", "settings.json");
                if (File.Exists(settingsPath))
                    options = JsonSerializer.Deserialize<EncoderOptions>(
                        File.ReadAllText(settingsPath), _jsonOptions);
            }
            catch { }
            options ??= new EncoderOptions();
        }
        return options;
    }

    /// <summary> Builds the base URL for inter-node HTTP communication. </summary>
    private string NodeBaseUrl(ClusterNode node) =>
        $"{(_config.UseHttps ? "https" : "http")}://{node.IpAddress}:{node.Port}";

    /// <summary> Creates a deep copy of encoder options via JSON round-trip serialization. </summary>
    private EncoderOptions CloneOptions(EncoderOptions options)
    {
        var json = JsonSerializer.Serialize(options, _jsonOptions);
        return JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions) ?? new EncoderOptions();
    }

    /// <summary> Loads cluster configuration from disk. </summary>
    private void LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                _config = JsonSerializer.Deserialize<ClusterConfig>(json, _jsonOptions) ?? new ClusterConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Failed to load config: {ex.Message}");
                _config = new ClusterConfig();
            }
        }

        _discovery.Config = _config;
        _nodeJobs.Config  = _config;
    }

    /// <summary> Persists the current configuration to disk as JSON. </summary>
    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, _jsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to save config: {ex.Message}");
        }
    }
}
