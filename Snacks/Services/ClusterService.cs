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
    private readonly ConcurrentDictionary<string, ClusterNode> _nodes = new();
    private readonly ConcurrentDictionary<string, WorkItem>    _remoteJobs         = new();
    private readonly ConcurrentDictionary<string, bool>        _activeDownloads    = new();
    private readonly ConcurrentDictionary<string, bool>        _activeUploads      = new();
    private readonly ConcurrentDictionary<string, int>         _downloadRetryCounts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts = new();
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
        MediaFileRepository mediaFileRepo)
    {
        _transcodingService = transcodingService;
        _ffprobeService     = ffprobeService;
        _fileService        = fileService;
        _hubContext         = hubContext;
        _httpClientFactory  = httpClientFactory;
        _mediaFileRepo      = mediaFileRepo;

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

    /// <summary> Whether this node is currently processing a remote job. </summary>
    public bool IsProcessingRemoteJob() => _nodeJobs.IsProcessingRemoteJob();

    /// <summary> Returns the current remote job ID. </summary>
    public string? GetCurrentRemoteJobId() => _nodeJobs.GetCurrentRemoteJobId();

    /// <summary> Returns the encoding progress of the current remote job. </summary>
    public int GetCurrentRemoteJobProgress() => _nodeJobs.GetCurrentRemoteJobProgress();

    /// <summary> Pauses or resumes this node. </summary>
    public void SetNodePaused(bool paused) => _nodeJobs.SetNodePaused(paused);

    /// <summary> Sets the job ID being received during file transfer. </summary>
    public void SetReceivingJob(string? jobId) => _nodeJobs.SetReceivingJob(jobId);

    /// <summary> Accepts or rejects a job offer from the master. </summary>
    public Task<bool> AcceptJobOfferAsync(JobAssignment assignment) =>
        _nodeJobs.AcceptJobOfferAsync(assignment);

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
    public void RegisterOrUpdateNode(ClusterNode node, bool fromHandshake = false) =>
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
                    if (!_activeUploads.ContainsKey(node.ActiveWorkItemId) &&
                        !_activeDownloads.ContainsKey(node.ActiveWorkItemId))
                        await HandleNodeFailureAsync(node.ActiveWorkItemId);
                    else
                        Console.WriteLine($"Cluster: Node {node.Hostname} unreachable but transfer active for {node.ActiveWorkItemId} — letting transfer handle retry");

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

                    if (node.IsPaused)
                    {
                        node.Status = NodeStatus.Paused;
                    }
                    else if (heartbeat.TryGetProperty("currentJobId", out var jobId) && jobId.ValueKind != JsonValueKind.Null)
                    {
                        var nodeJobId = jobId.GetString();
                        node.Status          = NodeStatus.Busy;
                        node.ActiveWorkItemId = nodeJobId;

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
                        // Node is idle — reconcile if master expected a job
                        if (node.ActiveWorkItemId != null && _remoteJobs.ContainsKey(node.ActiveWorkItemId)
                            && !_activeUploads.ContainsKey(node.ActiveWorkItemId)
                            && !_activeDownloads.ContainsKey(node.ActiveWorkItemId))
                        {
                            Console.WriteLine($"Cluster: Node {node.Hostname} is idle but master expected job {node.ActiveWorkItemId} — re-queuing");
                            await HandleNodeFailureAsync(node.ActiveWorkItemId);
                        }

                        node.Status          = NodeStatus.Online;
                        node.ActiveWorkItemId = null;
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
            node.Status           = status;
            node.ActiveWorkItemId = workItemId;
            node.ActiveFileName   = fileName;
            node.ActiveProgress   = progress;
            node.LastHeartbeat    = DateTime.UtcNow;
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
        if (!_recoveryComplete.Task.IsCompleted) return;
        if (_transcodingService.IsPaused) return;
        if (!await _dispatchLock.WaitAsync(0)) return;

        try
        {
            // Skip dispatch entirely if no worker nodes are available
            bool hasAvailableNodes = _nodes.Values.Any(n =>
                n.Role == "node" && n.Status == NodeStatus.Online
                && !n.IsPaused && n.ActiveWorkItemId == null);
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

                // Mark busy immediately to prevent double-dispatch
                bestNode.ActiveWorkItemId = workItem.Id;
                bestNode.Status           = NodeStatus.Busy;

                _ = Task.Run(() => DispatchToNodeAsync(bestNode, workItem, options));
            }
        }
        finally
        {
            _dispatchLock.Release();
        }
    }

    /// <summary>
    ///     Uploads source file, verifies the upload, and sends the job assignment
    ///     to the worker node.
    /// </summary>
    private async Task DispatchToNodeAsync(ClusterNode node, WorkItem workItem, EncoderOptions options)
    {
        if (!_activeUploads.TryAdd(workItem.Id, true))
        {
            Console.WriteLine($"Cluster: Upload already in progress for {workItem.FileName} — skipping");
            return;
        }

        var baseUrl = NodeBaseUrl(node);
        var jobCts  = new CancellationTokenSource();
        _jobCts[workItem.Id] = jobCts;

        try
        {
            // Reuse previous work item ID if the node has partial data
            var dbFile = await _mediaFileRepo.GetByPathAsync(Path.GetFullPath(workItem.Path));
            if (dbFile?.RemoteWorkItemId != null)
            {
                var oldId = workItem.Id;
                workItem.Id = dbFile.RemoteWorkItemId;
                _transcodingService.ReplaceWorkItemId(oldId, workItem.Id, workItem);
                _jobCts.TryRemove(oldId, out _);
                _jobCts[workItem.Id] = jobCts;
                Console.WriteLine($"Cluster: Reusing previous job ID {workItem.Id} for {workItem.FileName}");
            }

            workItem.AssignedNodeId   = node.NodeId;
            workItem.AssignedNodeName = node.Hostname;
            workItem.RemoteJobPhase   = "Uploading";
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            await _mediaFileRepo.AssignToRemoteNodeAsync(
                Path.GetFullPath(workItem.Path), workItem.Id, node.NodeId, node.Hostname,
                node.IpAddress, node.Port, "Uploading");
            _remoteJobs[workItem.Id] = workItem;
            UpdateNodeStatus(node.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

            var client = _discovery.CreateAuthenticatedClient();
            await _fileTransfer.UploadFileToNodeAsync(client, baseUrl, workItem, jobCts.Token);

            // Verify upload
            var receivedBytes = await _fileTransfer.GetNodeReceivedBytesAsync(client, baseUrl, workItem.Id);
            if (receivedBytes != workItem.Size)
            {
                Console.WriteLine($"Cluster: Upload verification failed for {workItem.FileName} — expected {workItem.Size}, node has {receivedBytes}");
                try { await client.DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }
                throw new Exception("Upload verification failed");
            }

            workItem.RemoteJobPhase  = "Encoding";
            workItem.TransferProgress = 0;
            await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Encoding");
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            var sourceFileHash = await ClusterFileTransferService.ComputeFileHashAsync(workItem.Path);
            var assignment = new JobAssignment
            {
                JobId          = workItem.Id,
                FileName       = workItem.FileName,
                FileSize       = workItem.Size,
                Options        = CloneOptions(options),
                Probe          = workItem.Probe,
                Duration       = workItem.Length,
                Bitrate        = workItem.Bitrate,
                IsHevc         = workItem.IsHevc,
                SourceFileHash = sourceFileHash
            };

            var assignContent = new StringContent(
                JsonSerializer.Serialize(assignment, _jsonOptions),
                Encoding.UTF8, "application/json");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                var offerResponse = await _discovery.CreateAuthenticatedClient()
                    .PostAsync($"{baseUrl}/api/cluster/jobs/offer", assignContent);
                if (offerResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Cluster: Job {workItem.FileName} dispatched to {node.Hostname}");
                    return;
                }

                var errorBody = await offerResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Cluster: Job offer rejected (attempt {attempt + 1}): {errorBody}");
                if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(2));
            }

            throw new Exception("Job offer rejected after 3 attempts");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Failed to dispatch {workItem.FileName} to {node.Hostname}: {ex.Message}");
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
            UpdateNodeStatus(node.NodeId, NodeStatus.Online);
        }
        finally
        {
            _activeUploads.TryRemove(workItem.Id, out _);
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
            await _hubContext.Clients.All.SendAsync("TranscodingLog", jobId, progress.LogLine);
    }

    /// <summary>
    ///     Handles completion of a remote job: downloads the output, validates it,
    ///     and performs file placement.
    /// </summary>
    public async Task HandleRemoteCompletionAsync(string jobId, string nodeBaseUrl)
    {
        if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;
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

            workItem.RemoteJobPhase  = "Downloading";
            workItem.TransferProgress = 0;
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            var options        = _transcodingService.GetLastOptions() ?? new EncoderOptions();
            var ext            = options.Format == "mp4" ? ".mp4" : ".mkv";
            var baseName       = Path.GetFileNameWithoutExtension(workItem.FileName);
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

            // Validate output
            var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
            if (workItem.Probe != null && !_ffprobeService.ConvertedSuccessfully(workItem.Probe, outputProbe))
            {
                try { File.Delete(outputPath); } catch { }

                var failCount = _downloadRetryCounts.AddOrUpdate($"_validation_{jobId}", 1, (_, c) => c + 1);

                if (failCount >= 3)
                {
                    Console.WriteLine($"Cluster: Output validation failed {failCount} times for {workItem.FileName} — re-queuing for fresh encode");
                    try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }
                    var prevNodeId = workItem.AssignedNodeId;
                    _remoteJobs.TryRemove(jobId, out _);
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                    workItem.AssignedNodeId   = null;
                    workItem.AssignedNodeName = null;
                    workItem.RemoteJobPhase   = null;
                    workItem.ErrorMessage     = null;
                    _transcodingService.RequeueWorkItem(workItem);
                    if (prevNodeId != null) UpdateNodeStatus(prevNodeId, NodeStatus.Online);
                    return;
                }

                throw new Exception($"Validation failed ({failCount}) — duration mismatch in remote output");
            }

            await _transcodingService.HandleRemoteCompletion(workItem, outputPath, options);
            await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);
            try { await _discovery.CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }

            if (workItem.AssignedNodeId != null)
            {
                UpdateNodeStatus(workItem.AssignedNodeId, NodeStatus.Online);
                if (_nodes.TryGetValue(workItem.AssignedNodeId, out var node))
                    node.CompletedJobs++;
            }

            _remoteJobs.TryRemove(jobId, out _);
            _downloadRetryCounts.TryRemove(jobId, out _);
            if (_jobCts.TryRemove(jobId, out var completedCts))
                completedCts.Dispose();
            Console.WriteLine($"Cluster: Remote job {workItem.FileName} completed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Cluster: Remote completion failed for {workItem.FileName}: {ex.Message}");

            var retryCount = _downloadRetryCounts.AddOrUpdate(jobId, 1, (_, c) => c + 1);
            const int MaxDownloadRetries = 10;

            if (retryCount >= MaxDownloadRetries)
            {
                Console.WriteLine($"Cluster: Download failed after {MaxDownloadRetries} attempts — re-queuing for fresh encode");
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
        }
        finally
        {
            _activeDownloads.TryRemove(jobId, out _);
        }
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

        if (_jobCts.TryRemove(jobId, out var jobCts))
        {
            jobCts.Cancel();
            jobCts.Dispose();
        }

        if (_nodes.TryGetValue(nodeId, out var node))
        {
            try
            {
                var client  = _discovery.CreateAuthenticatedClient();
                var baseUrl = NodeBaseUrl(node);
                await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{jobId}");
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
        if (!_remoteJobs.TryRemove(jobId, out var workItem)) return;

        if (_jobCts.TryRemove(jobId, out var jobCts))
        {
            jobCts.Cancel();
            jobCts.Dispose();
        }

        _activeUploads.TryRemove(jobId, out _);
        _activeDownloads.TryRemove(jobId, out _);

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
                && !n.IsPaused && n.ActiveWorkItemId == null)
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
     *  Recovery
     ******************************************************************/

    /// <summary>
    ///     On master startup, checks for remote jobs that were in-flight when the
    ///     previous session ended. Attempts to reconnect to nodes and retrieve results,
    ///     or re-queues for fresh dispatch.
    /// </summary>
    private async Task RecoverRemoteJobsAsync(CancellationToken ct = default)
    {
        try
        {
            var activeJobs = await _mediaFileRepo.GetActiveRemoteJobsAsync();
            if (activeJobs.Count == 0) return;

            Console.WriteLine($"Cluster: Recovering {activeJobs.Count} remote job(s) from database...");

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

                // Check if node is actively encoding this job
                try
                {
                    var hbBody = await (await _discovery.CreateAuthenticatedClient()
                        .GetAsync($"{baseUrl}/api/cluster/heartbeat")).Content.ReadAsStringAsync();
                    var hbData = JsonSerializer.Deserialize<JsonElement>(hbBody);
                    if (hbData.TryGetProperty("currentJobId", out var curJob) && curJob.ValueKind != JsonValueKind.Null)
                    {
                        if (curJob.GetString() == jobId)
                        {
                            int recoveredProgress = 0;
                            if (hbData.TryGetProperty("progress", out var progProp))
                                recoveredProgress = progProp.GetInt32();

                            Console.WriteLine($"Cluster: Node is actively encoding {mediaFile.FileName} at {recoveredProgress}% — tracking");
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
                        workItem.Status             = WorkItemStatus.Processing;
                        workItem.AssignedNodeId     = mediaFile.AssignedNodeId;
                        workItem.AssignedNodeName   = mediaFile.AssignedNodeName ?? "recovered";
                        workItem.RemoteJobPhase     = "Downloading";
                        workItem.RemoteFailureCount = mediaFile.RemoteFailureCount;
                        workItem.ErrorMessage       = null;
                        _remoteJobs[workItem.Id]    = workItem;
                        if (mediaFile.RemoteFailureCount > 0)
                            _downloadRetryCounts[workItem.Id] = mediaFile.RemoteFailureCount * 3;
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
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
                                    return;
                                }

                                workItem.Status         = WorkItemStatus.Processing;
                                workItem.RemoteJobPhase = "Uploading";
                                workItem.ErrorMessage   = null;
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

                                var uploadClient = _discovery.CreateAuthenticatedClient();
                                await _fileTransfer.UploadFileToNodeAsync(uploadClient, baseUrl, workItem, recoveryCts.Token);

                                var finalSize = await _fileTransfer.GetNodeReceivedBytesAsync(uploadClient, baseUrl, workItem.Id);
                                if (finalSize != workItem.Size)
                                {
                                    Console.WriteLine($"Cluster: Resumed upload verification failed");
                                    _remoteJobs.TryRemove(workItem.Id, out _);
                                    await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                                    _transcodingService.RequeueWorkItem(workItem);
                                    return;
                                }

                                workItem.RemoteJobPhase  = "Encoding";
                                workItem.TransferProgress = 0;
                                await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Encoding");
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                                var sourceFileHash = await ClusterFileTransferService.ComputeFileHashAsync(workItem.Path);
                                var assignment = new JobAssignment
                                {
                                    JobId          = workItem.Id,
                                    FileName       = workItem.FileName,
                                    FileSize       = workItem.Size,
                                    Options        = CloneOptions(options),
                                    Probe          = workItem.Probe,
                                    Duration       = workItem.Length,
                                    Bitrate        = workItem.Bitrate,
                                    IsHevc         = workItem.IsHevc,
                                    SourceFileHash = sourceFileHash
                                };

                                var assignContent = new StringContent(
                                    JsonSerializer.Serialize(assignment, _jsonOptions),
                                    Encoding.UTF8, "application/json");

                                for (int attempt = 0; attempt < 3; attempt++)
                                {
                                    var offerResponse = await _discovery.CreateAuthenticatedClient()
                                        .PostAsync($"{baseUrl}/api/cluster/jobs/offer", assignContent);
                                    if (offerResponse.IsSuccessStatusCode)
                                    {
                                        Console.WriteLine($"Cluster: Recovered job {workItem.FileName} dispatched to {nodeForDispatch.Hostname}");
                                        return;
                                    }
                                    if (attempt < 2) await Task.Delay(2000);
                                }

                                Console.WriteLine($"Cluster: Job offer rejected after recovery — re-queuing {workItem.FileName}");
                                _remoteJobs.TryRemove(workItem.Id, out _);
                                await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                                _transcodingService.RequeueWorkItem(workItem);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Cluster: Recovery upload failed for {workItem.FileName}: {ex.Message}");
                                _remoteJobs.TryRemove(workItem.Id, out _);
                                try { await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued); } catch { }
                                _transcodingService.RequeueWorkItem(workItem);
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
