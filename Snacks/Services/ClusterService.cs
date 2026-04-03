using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Snacks.Services;

/// <summary>
///     Manages the distributed encoding cluster — node discovery, heartbeat monitoring,
///     job dispatch, file transfer, and crash recovery.
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
    private readonly TranscodingService  _transcodingService;
    private readonly FfprobeService      _ffprobeService;
    private readonly FileService         _fileService;
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly IHttpClientFactory  _httpClientFactory;
    private readonly MediaFileRepository _mediaFileRepo;
    private readonly ConcurrentDictionary<string, ClusterNode>              _nodes              = new();
    private readonly ConcurrentDictionary<string, WorkItem>                 _remoteJobs         = new();
    private readonly ConcurrentDictionary<string, bool>                     _activeDownloads    = new();
    private readonly ConcurrentDictionary<string, bool>                     _activeUploads      = new();
    private readonly ConcurrentDictionary<string, int>                      _downloadRetryCounts = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource>  _jobCts             = new();
    private readonly string _configPath;
    private readonly string _workDir;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    private ClusterConfig            _config          = new();
    private Timer?                   _heartbeatTimer;
    private Timer?                   _dispatchTimer;
    private UdpClient?               _udpListener;
    private CancellationTokenSource? _cts;
    private Task?                    _discoveryTask;
    private string?                  _detectedGpuVendor;
    private List<string>?            _supportedEncoders;
    private TaskCompletionSource     _recoveryComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary> Completes when crash recovery of remote jobs has finished on startup. </summary>
    public Task RecoveryCompleteTask => _recoveryComplete.Task;

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Initializes the service, resolves configuration file paths, and wires up
    ///     the pending-completions persistence path used by node-side job tracking.
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
        _configPath             = Path.Combine(configDir, "cluster.json");
        _pendingCompletionsPath = Path.Combine(_workDir, "config", "pending-completions.json");
    }

    /******************************************************************
     *  IHostedService
     ******************************************************************/

    /// <summary>
    ///     Called when the application starts. Loads cluster configuration and begins
    ///     cluster operations if enabled. For master nodes, triggers crash recovery
    ///     of any in-flight remote jobs from the previous session.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfig();
            Console.WriteLine($"Cluster: Config loaded — enabled={_config.Enabled}, role={_config.Role}, nodeId={_config.NodeId}");

            if (_config.Enabled && _config.Role != "standalone")
            {
                // Create an unresolved TCS for master recovery — heartbeat and dispatch
                // will skip until recovery completes, preventing premature reconciliation
                if (_config.Role == "master")
                    _recoveryComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                else
                    _recoveryComplete.TrySetResult(); // Non-master: no recovery needed

                StartClusterOperations();

                // Recover any remote jobs from a previous session (runs before queue resume)
                if (_config.Role == "master")
                    // Recovery runs in the background — IsRemoteJob checks DB directly so no race with resume
                    _ = Task.Run(() => RecoverRemoteJobsAsync());
            }
            else
            {
                _recoveryComplete.TrySetResult(); // Not clustering: no recovery needed
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
        ///     Initiates a graceful or immediate shutdown. In graceful mode, pauses all nodes, waits
        ///     for active transfers to complete up to <paramref name="timeoutSeconds"/>, then stops.
        /// </summary>
        /// <param name="graceful">When <see langword="true"/>, waits for active transfers to finish.</param>
        /// <param name="timeoutSeconds">Maximum seconds to wait for transfers before forcing shutdown.</param>
        public async Task InitiateShutdownAsync(bool graceful, int timeoutSeconds)
        {
            Console.WriteLine($"Cluster: Initiating {(graceful ? "graceful" : "immediate")} shutdown (timeout: {timeoutSeconds}s)");

            if (graceful && IsMasterMode)
            {
                // Tell all nodes to pause so they can finish current work
                foreach (var node in _nodes.Values.Where(n => n.Role == "node"))
                {
                    try
                    {
                        await SetRemoteNodePausedAsync(node.NodeId, true);
                    }
                    catch { }
                }

                // Wait for active transfers to complete (up to timeout)
                var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    var activeTransfers = _activeUploads.Count + _activeDownloads.Count;
                    if (activeTransfers == 0) break;
                    Console.WriteLine($"Cluster: Waiting for {activeTransfers} active transfer(s) to complete...");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }

            // Persist all state to DB — cleanup old transitions
            try { await _mediaFileRepo.CleanupOldTransitionsAsync(); } catch { }

            await StopClusterOperationsAsync();
        }

        /// <summary> Deletes remote job temp directories that are older than <paramref name="ttlHours"/> hours. </summary>
        /// <param name="ttlHours">Directories last written more than this many hours ago are deleted.</param>
        public void CleanupOldRemoteJobs(int ttlHours)
        {
            var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
            var baseDir = string.IsNullOrWhiteSpace(_config.NodeTempDirectory)
                ? Path.Combine(workDir, "remote-jobs")
                : _config.NodeTempDirectory;

            if (!Directory.Exists(baseDir)) return;

            var cutoff = DateTime.UtcNow.AddHours(-ttlHours);
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

        /// <summary> Disposes the service, stopping all cluster operations. </summary>
        public void Dispose()
        {
            _cts?.Cancel();
            _heartbeatTimer?.Dispose();
            _dispatchTimer?.Dispose();
            _udpListener?.Close();
            _udpListener?.Dispose();
            _cts?.Dispose();
        }

    /******************************************************************
     *  Configuration
     ******************************************************************/

        /// <summary> Returns the current cluster configuration. </summary>
        public ClusterConfig GetConfig() => _config;

        /// <summary>
        ///     Persists a new cluster configuration and restarts cluster operations immediately.
        ///     Handles role transitions, such as switching from master to node mode.
        /// </summary>
        /// <param name="newConfig">The new configuration to apply.</param>
        public async Task SaveConfigAndApplyAsync(ClusterConfig newConfig)
        {
            var oldRole = _config.Role;
            var wasEnabled = _config.Enabled;
            _config = newConfig;
            SaveConfig();

            // Handle mode transitions
            if (newConfig.Enabled && newConfig.Role != "standalone")
            {
                if (newConfig.Role == "node" && oldRole != "node")
                {
                    // Transitioning to node mode: stop processing, clear queue
                    await _transcodingService.StopAndClearQueue();
                }
                await StopClusterOperationsAsync();
                StartClusterOperations();
            }
            else
            {
                await StopClusterOperationsAsync();
                // Standalone mode: always allow local encoding
                _transcodingService.SetLocalEncodingPaused(false);
            }

            // Broadcast config change WITHOUT the secret
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

        /// <summary>
        ///     Returns the phase of a remote job, or <see langword="null"/> if the job is not tracked.
        ///     Used for idempotency checks on completion reports.
        /// </summary>
        /// <param name="jobId">The job ID to look up.</param>
        /// <returns>The current phase string (e.g. "Uploading", "Encoding"), or <see langword="null"/>.</returns>
        public string? GetRemoteJobPhase(string jobId)
        {
            return _remoteJobs.TryGetValue(jobId, out var w) ? w.RemoteJobPhase : null;
        }

        /// <summary>
        ///     Checks if a file path is currently being handled as a remote job.
        ///     Uses the in-memory cache for speed, then the database for an authoritative answer.
        /// </summary>
        /// <param name="filePath">The absolute file path to check.</param>
        /// <returns><see langword="true"/> if the file is assigned to a remote node.</returns>
        public bool IsRemoteJob(string filePath)
        {
            var normalized = Path.GetFullPath(filePath);

            if (_remoteJobs.Values.Any(w =>
                Path.GetFullPath(w.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                return true;

            // DB is the authoritative source — in-memory cache may not yet reflect a recovered job.
            return _mediaFileRepo.IsRemoteJobAsync(normalized).GetAwaiter().GetResult();
        }

    /******************************************************************
     *  Node Pause
     ******************************************************************/

        private bool _nodePaused = false;

        /// <summary> Whether this node is paused and not accepting new jobs. </summary>
        public bool IsNodePaused => _nodePaused;

        /// <summary> Job ID retained after encoding completes, until the master acknowledges and cleans up. </summary>
        private string? _completedJobId;

        /// <summary> Returns the current remote job ID, spanning the active, receiving, and completed states. </summary>
        /// <returns>The job ID, or <see langword="null"/> if no remote job is tracked.</returns>
        public string? GetCurrentRemoteJobId() => _currentRemoteJob?.Id ?? _receivingJobId ?? _completedJobId;

        /// <summary> Returns the encoding progress percentage of the current remote job. </summary>
        /// <returns>A value from 0 to 100, or 0 if no job is active.</returns>
        public int GetCurrentRemoteJobProgress() => _currentRemoteJob?.Progress ?? 0;

        /// <summary> Pauses or resumes this node's ability to accept new jobs. </summary>
        /// <param name="paused">When <see langword="true"/>, the node will not accept new job offers.</param>
        public void SetNodePaused(bool paused)
        {
            _nodePaused = paused;
            Console.WriteLine($"Cluster: Node {(paused ? "paused" : "resumed")}");
            // Broadcast update so the node's UI reflects the state
            _ = _hubContext.Clients.All.SendAsync("ClusterNodePaused", paused);
        }

        /// <summary>
        ///     Sends a pause or resume command to a specific remote node and updates its entry
        ///     in the local node registry.
        /// </summary>
        /// <param name="nodeId">The ID of the node to pause or resume.</param>
        /// <param name="paused">When <see langword="true"/>, the node will stop accepting new jobs.</param>
        public async Task SetRemoteNodePausedAsync(string nodeId, bool paused)
        {
            if (!_nodes.TryGetValue(nodeId, out var node)) return;

            try
            {
                var client = CreateAuthenticatedClient();
                var baseUrl = $"http://{node.IpAddress}:{node.Port}";
                var content = new StringContent(
                    JsonSerializer.Serialize(new { paused }, _jsonOptions),
                    Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{baseUrl}/api/cluster/pause", content);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Failed to pause node {node.Hostname}: {ex.Message}");
            }

            node.IsPaused = paused;
            node.Status = paused ? NodeStatus.Paused : NodeStatus.Online;
            await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
            Console.WriteLine($"Cluster: Node {node.Hostname} {(paused ? "paused" : "resumed")} by master");
        }

    /******************************************************************
     *  Discovery
     ******************************************************************/

        /// <summary>
        ///     Starts UDP discovery, heartbeat monitoring, job dispatch (master only),
        ///     and manual node connections based on the current configuration.
        /// </summary>
        private void StartClusterOperations()
        {
            if (string.IsNullOrEmpty(_config.SharedSecret))
            {
                Console.WriteLine("Cluster: Cannot start — no shared secret configured");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            // UDP discovery — always on for nodes without a master URL, otherwise respect the setting
            bool needsDiscovery = _config.AutoDiscovery ||
                (_config.Role == "node" && string.IsNullOrEmpty(_config.MasterUrl));
            if (needsDiscovery)
                _discoveryTask = Task.Run(() => RunDiscoveryAsync(_cts.Token));

            // Heartbeat monitoring — add jitter to the initial delay to prevent thundering herd
            // when multiple nodes start simultaneously
            var heartbeatInterval = TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds);
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)heartbeatInterval.TotalMilliseconds));
            _heartbeatTimer = new Timer(async _ => await RunHeartbeatAsync(), null, jitter, heartbeatInterval);

            // Job dispatch (master only)
            if (_config.Role == "master")
            {
                // Pause local encoding if the user disabled it
                _transcodingService.SetLocalEncodingPaused(!_config.LocalEncodingEnabled);

                // Wire up remote job cancellation and duplicate checking
                _transcodingService.SetRemoteJobCanceller(CancelRemoteJobOnNodeAsync);
                _transcodingService.SetRemoteJobChecker(IsRemoteJob);

                _dispatchTimer = new Timer(async _ => await RunDispatchAsync(), null,
                    TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2));
            }
            else
            {
                _transcodingService.SetLocalEncodingPaused(false);
            }

            // Connect to manual nodes
            if (_config.ManualNodes.Count > 0)
                _ = Task.Run(() => ConnectToManualNodesAsync(_cts.Token));

            // Node: register with master
            if (_config.Role == "node" && !string.IsNullOrEmpty(_config.MasterUrl))
                _ = Task.Run(() => RegisterWithMasterAsync(_cts.Token));

            var localIp = GetLocalIpAddress();
            var port = GetListeningPort();
            Console.WriteLine($"Cluster started: role={_config.Role}, localIp={localIp}, port={port}, discovery={needsDiscovery}");
        }

        /// <summary> Cancels all timers, closes the UDP listener, and awaits the discovery task. </summary>
        private async Task StopClusterOperationsAsync()
        {
            _cts?.Cancel();

            // Wait for the discovery task to finish so the UDP socket is fully released
            if (_discoveryTask != null)
            {
                try { await _discoveryTask.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { }
                _discoveryTask = null;
            }

            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            _dispatchTimer?.Dispose();
            _dispatchTimer = null;

            _udpListener?.Close();
            _udpListener?.Dispose();
            _udpListener = null;

            _cts?.Dispose();
            _cts = null;

            Console.WriteLine("Cluster: Stopped");
        }

        /// <summary> Binds the UDP socket and runs the broadcast and listen loops in parallel. </summary>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task RunDiscoveryAsync(CancellationToken ct)
        {
            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, 6768));
                _udpListener.EnableBroadcast = true;

                Console.WriteLine("Cluster: UDP discovery started on port 6768");

                // Start broadcast and listen in parallel
                var broadcastTask = BroadcastLoopAsync(ct);
                var listenTask = ListenLoopAsync(ct);
                await Task.WhenAll(broadcastTask, listenTask);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Discovery error: {ex.Message}");
            }
        }

        /// <summary>
        ///     Announces this node's presence via UDP every 15 seconds, sending to all subnet
        ///     broadcast addresses to maximise reachability across multi-adapter hosts.
        /// </summary>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task BroadcastLoopAsync(CancellationToken ct)
        {
            using var broadcastClient = new UdpClient();
            broadcastClient.EnableBroadcast = true;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var announcement = new
                    {
                        proto = "snacks-v1",
                        nodeId = _config.NodeId,
                        role = _config.Role,
                        port = GetListeningPort(),
                        version = "2.1.0",
                        secretHash = HashSecret(_config.SharedSecret)
                    };

                    var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));

                    // Send to all subnet broadcast addresses — 255.255.255.255 alone
                    // often fails on Windows when multiple adapters are present
                    var broadcastAddresses = GetBroadcastAddresses();
                    foreach (var addr in broadcastAddresses)
                    {
                        try
                        {
                            await broadcastClient.SendAsync(bytes, bytes.Length, new IPEndPoint(addr, 6768));
                        }
                        catch { }
                    }

                    // Also send to the generic broadcast as a fallback
                    try
                    {
                        await broadcastClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, 6768));
                    }
                    catch { }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"Cluster: Broadcast error: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }

        /// <summary>
        ///     Listens for UDP announcements from peer nodes and initiates a handshake
        ///     with any newly discovered node whose secret hash matches.
        /// </summary>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task ListenLoopAsync(CancellationToken ct)
        {
            Console.WriteLine("Cluster: Listening for UDP announcements on port 6768");

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpListener!.ReceiveAsync(ct);
                    var json = Encoding.UTF8.GetString(result.Buffer);

                    JsonElement announcement;
                    try
                    {
                        announcement = JsonSerializer.Deserialize<JsonElement>(json);
                    }
                    catch
                    {
                        continue; // Not valid JSON
                    }

                    if (!announcement.TryGetProperty("proto", out var proto) || proto.GetString() != "snacks-v1")
                        continue;

                    var nodeId = announcement.GetProperty("nodeId").GetString();
                    if (nodeId == _config.NodeId)
                        continue; // Ignore our own broadcast

                    var secretHash = announcement.GetProperty("secretHash").GetString();
                    if (secretHash != HashSecret(_config.SharedSecret))
                    {
                        Console.WriteLine($"Cluster: Ignored announcement from {result.RemoteEndPoint} — secret mismatch");
                        continue;
                    }

                    var port = announcement.GetProperty("port").GetInt32();
                    var role = announcement.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : "unknown";
                    var senderIp = result.RemoteEndPoint.Address.ToString();

                    // Skip if we already know this node — don't reset LastHeartbeat here,
                    // only successful HTTP heartbeats should do that
                    if (_nodes.ContainsKey(nodeId!))
                        continue;

                    // Validate port is in reasonable range
                    if (port < 1 || port > 65535) continue;

                    Console.WriteLine($"Cluster: Discovered {role} at {senderIp}:{port} — performing handshake");
                    _ = Task.Run(() => PerformHandshakeAsync($"http://{senderIp}:{port}", ct));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cluster: Listen error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets the directed broadcast address for each active network interface.
        /// For example, 192.168.1.0/24 yields 192.168.1.255.
        /// </summary>
        private static List<IPAddress> GetBroadcastAddresses()
        {
            var addresses = new List<IPAddress>();
            try
            {
                foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (iface.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                        continue;

                    foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;

                        var ipBytes = unicast.Address.GetAddressBytes();
                        var maskBytes = unicast.IPv4Mask.GetAddressBytes();
                        var broadcastBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                            broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                        addresses.Add(new IPAddress(broadcastBytes));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Error enumerating network interfaces: {ex.Message}");
            }
            return addresses;
        }

        /// <summary> Attempts a handshake with each manually configured node URL. </summary>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task ConnectToManualNodesAsync(CancellationToken ct)
        {
            foreach (var node in _config.ManualNodes)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await PerformHandshakeAsync(node.Url, ct);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Manual node connect to {node.Name} failed: {ex.Message}");
                }
            }
        }

        /// <summary> Repeatedly attempts to register with the master, retrying every 10 seconds until successful or cancelled. </summary>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task RegisterWithMasterAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PerformHandshakeAsync(_config.MasterUrl!, ct);
                    return; // Success
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to register with master: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
            }
        }

        /// <summary>
        ///     Posts this node's info to the remote node's handshake endpoint and registers
        ///     the remote node's response locally.
        /// </summary>
        /// <param name="baseUrl">Base URL of the remote node (e.g. <c>http://192.168.1.5:6767</c>).</param>
        /// <param name="ct">Cancelled when the cluster is stopping.</param>
        private async Task PerformHandshakeAsync(string baseUrl, CancellationToken ct)
        {
            var url = $"{baseUrl.TrimEnd('/')}/api/cluster/handshake";
            try
            {
                var client = CreateAuthenticatedClient();
                var selfNode = BuildSelfNode();

                var content = new StringContent(
                    JsonSerializer.Serialize(selfNode, _jsonOptions),
                    Encoding.UTF8, "application/json");

                var response = await client.PostAsync(url, content, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"Cluster: Handshake with {baseUrl} failed — {response.StatusCode}: {body}");
                    return;
                }

                var responseBody = await response.Content.ReadAsStringAsync(ct);
                var remoteNode = JsonSerializer.Deserialize<ClusterNode>(responseBody, _jsonOptions);

                if (remoteNode != null)
                {
                    RegisterOrUpdateNode(remoteNode, fromHandshake: true);
                    Console.WriteLine($"Cluster: Handshake with {remoteNode.Hostname} ({baseUrl}) successful");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Handshake with {baseUrl} failed — {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a <see cref="ClusterNode"/> representing this instance.
        /// Used during handshake and heartbeat responses.
        /// </summary>
        public ClusterNode BuildSelfNode()
        {
            return new ClusterNode
            {
                NodeId = _config.NodeId,
                Hostname = _config.NodeName,
                IpAddress = GetLocalIpAddress(),
                Port = GetListeningPort(),
                Role = _config.Role,
                Status = _transcodingService.GetActiveWorkItem() != null ? NodeStatus.Busy : NodeStatus.Online,
                Version = "2.1.0",
                LastHeartbeat = DateTime.UtcNow,
                Capabilities = GetCapabilities()
            };
        }

        /// <summary>
        /// Returns the hardware and software capabilities of this node.
        /// Re-checks hardware detection each time since eager detection
        /// may not have finished on first call.
        /// </summary>
        public WorkerCapabilities GetCapabilities()
        {
            // Re-check hardware detection each time — the eager detection in
            // TranscodingService runs async and may not have finished on first call
            var hw = _transcodingService.GetDetectedHardware();
            if (hw != null && hw != _detectedGpuVendor)
            {
                _detectedGpuVendor = hw;
                _supportedEncoders = null; // Force rebuild with new GPU info
            }

            return new WorkerCapabilities
            {
                GpuVendor = _detectedGpuVendor ?? "none",
                SupportedEncoders = _supportedEncoders ?? BuildSupportedEncodersList(),
                OsPlatform = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : "Linux",
                AvailableDiskSpaceBytes = GetAvailableDiskSpace(),
                CanAcceptJobs = !IsProcessingRemoteJob() && !_nodePaused
            };
        }

        /// <summary> Builds and caches the list of encoder identifiers supported by this node's hardware. </summary>
        private List<string> BuildSupportedEncodersList()
        {
            var encoders = new List<string> { "libx265", "libx264", "libsvtav1" };
            var hw = _detectedGpuVendor;
            if (hw == "nvidia") encoders.AddRange(new[] { "hevc_nvenc", "h264_nvenc" });
            else if (hw == "intel")
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    encoders.AddRange(new[] { "hevc_qsv", "h264_qsv" });
                else
                    encoders.AddRange(new[] { "hevc_vaapi", "h264_vaapi" });
            }
            else if (hw == "amd") encoders.AddRange(new[] { "hevc_amf", "h264_amf" });
            _supportedEncoders = encoders;
            return encoders;
        }

    /******************************************************************
     *  Node Registration
     ******************************************************************/

        /// <summary>
        ///     Registers a new node or refreshes an existing one. A handshake always updates
        ///     <see cref="ClusterNode.LastHeartbeat"/>; a non-handshake update preserves the
        ///     existing timestamp so only successful HTTP heartbeats advance it.
        /// </summary>
        /// <param name="node">The node data received from the remote peer.</param>
        /// <param name="fromHandshake">
        ///     When <see langword="true"/>, resets <see cref="ClusterNode.LastHeartbeat"/> to now
        ///     because a successful handshake confirms the node is alive.
        /// </param>
        public void RegisterOrUpdateNode(ClusterNode node, bool fromHandshake = false)
        {
            var isNew = !_nodes.ContainsKey(node.NodeId);
            if (isNew || fromHandshake)
                node.LastHeartbeat = DateTime.UtcNow; // Handshake = confirmed alive
            else
                node.LastHeartbeat = _nodes[node.NodeId].LastHeartbeat; // Preserve — only HTTP heartbeat updates this

            _nodes[node.NodeId] = node;

            if (isNew)
            {
                Console.WriteLine($"Cluster: Node joined — {node.Hostname} ({node.IpAddress}:{node.Port}) [{node.Role}]");
                _ = _hubContext.Clients.All.SendAsync("WorkerConnected", node);
            }
            else
            {
                _ = _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
            }
        }

        /// <summary>
        /// Updates the status of a cluster node and broadcasts the change to all connected clients.
        /// </summary>
        public void UpdateNodeStatus(string nodeId, NodeStatus status, string? workItemId = null,
            string? fileName = null, int progress = 0)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                node.Status = status;
                node.ActiveWorkItemId = workItemId;
                node.ActiveFileName = fileName;
                node.ActiveProgress = progress;
                node.LastHeartbeat = DateTime.UtcNow;
                _ = _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
            }
        }

    /******************************************************************
     *  Heartbeat
     ******************************************************************/

        /// <summary>
        /// Runs the heartbeat monitoring loop. Pings all nodes periodically,
        /// detects timeouts, and reconciles job state.
        /// </summary>
        private async Task RunHeartbeatAsync()
        {
            // Don't run reconciliation until recovery is complete — recovery may not have
            // added jobs to _remoteJobs yet, causing heartbeat to cancel legitimate jobs
            if (!_recoveryComplete.Task.IsCompleted) return;

            var now = DateTime.UtcNow;
            var timeout = TimeSpan.FromSeconds(_config.NodeTimeoutSeconds);
            var removeAfter = TimeSpan.FromMinutes(5);

            foreach (var kvp in _nodes)
            {
                var node = kvp.Value;
                var timeSinceHeartbeat = now - node.LastHeartbeat;

                if (timeSinceHeartbeat > removeAfter && node.Status == NodeStatus.Unreachable)
                {
                    // Remove nodes that have been unreachable for 5+ minutes
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

                    // Re-queue any job this node was working on — but not if a transfer
                    // is actively retrying. Uploads/downloads have their own resilient retry
                    // with resume support; let them handle brief node outages.
                    if (node.ActiveWorkItemId != null)
                    {
                        if (!_activeUploads.ContainsKey(node.ActiveWorkItemId) &&
                            !_activeDownloads.ContainsKey(node.ActiveWorkItemId))
                        {
                            await HandleNodeFailure(node.ActiveWorkItemId);
                        }
                        else
                        {
                            Console.WriteLine($"Cluster: Node {node.Hostname} unreachable but transfer active for {node.ActiveWorkItemId} — letting transfer handle retry");
                        }
                        node.ActiveWorkItemId = null;
                    }
                    continue;
                }

                // Ping the node
                try
                {
                    var client = CreateAuthenticatedClient();
                    var baseUrl = $"http://{node.IpAddress}:{node.Port}";
                    var response = await client.GetAsync($"{baseUrl}/api/cluster/heartbeat");

                    if (response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync();
                        var heartbeat = JsonSerializer.Deserialize<JsonElement>(body);

                        node.LastHeartbeat = DateTime.UtcNow;
                        if (node.Status == NodeStatus.Unreachable)
                        {
                            node.Status = NodeStatus.Online;
                            Console.WriteLine($"Cluster: Node {node.Hostname} reconnected");
                        }

                        // Sync pause state from node
                        if (heartbeat.TryGetProperty("isPaused", out var pausedProp))
                        {
                            node.IsPaused = pausedProp.GetBoolean();
                        }

                        if (node.IsPaused)
                        {
                            node.Status = NodeStatus.Paused;
                        }
                        else if (heartbeat.TryGetProperty("currentJobId", out var jobId) && jobId.ValueKind != JsonValueKind.Null)
                        {
                            var nodeJobId = jobId.GetString();
                            node.Status = NodeStatus.Busy;
                            node.ActiveWorkItemId = nodeJobId;

                            // Sync encoding progress from the node's heartbeat — acts as a fallback
                            // for when progress POST callbacks are missed during reconnection
                            if (nodeJobId != null && heartbeat.TryGetProperty("progress", out var progProp) &&
                                _remoteJobs.TryGetValue(nodeJobId, out var trackedJob))
                            {
                                trackedJob.Progress = progProp.GetInt32();
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", trackedJob);
                            }

                            // Reconciliation: node says it's working on a job the master doesn't know about
                            // CRITICAL: Check DB before cancelling — recovery may be in progress for this job.
                            // Don't cancel jobs that have a DB record with AssignedNodeId set.
                            if (nodeJobId != null && !_remoteJobs.ContainsKey(nodeJobId))
                            {
                                var dbFile = await _mediaFileRepo.GetByRemoteWorkItemIdAsync(nodeJobId);
                                if (dbFile?.AssignedNodeId != null)
                                {
                                    // Job is in DB as assigned — don't cancel, recovery may be in progress
                                    Console.WriteLine($"Cluster: Node {node.Hostname} is working on DB-assigned job {nodeJobId} — skipping cancellation (recovery in progress)");
                                }
                                else
                                {
                                    Console.WriteLine($"Cluster: Node {node.Hostname} is working on unknown job {nodeJobId} — telling it to cancel");
                                    try
                                    {
                                        await client.DeleteAsync($"{baseUrl}/api/cluster/jobs/{nodeJobId}");
                                        await client.DeleteAsync($"{baseUrl}/api/cluster/files/{nodeJobId}");
                                    }
                                    catch (Exception ex) { Console.WriteLine($"Cluster: Failed to cancel unknown job {nodeJobId} on {node.Hostname}: {ex.Message}"); }
                                }
                            }
                        }
                        else
                        {
                            // Reconciliation: master thinks the node has a job but the node is idle
                            // Don't interfere with active transfers — the node may have just restarted
                            // and the upload/download retry will re-establish the connection
                            if (node.ActiveWorkItemId != null && _remoteJobs.ContainsKey(node.ActiveWorkItemId)
                                && !_activeUploads.ContainsKey(node.ActiveWorkItemId)
                                && !_activeDownloads.ContainsKey(node.ActiveWorkItemId))
                            {
                                Console.WriteLine($"Cluster: Node {node.Hostname} is idle but master expected job {node.ActiveWorkItemId} — re-queuing");
                                await HandleNodeFailure(node.ActiveWorkItemId);
                            }

                            node.Status = NodeStatus.Online;
                            node.ActiveWorkItemId = null;
                        }

                        if (heartbeat.TryGetProperty("diskSpace", out var diskSpace))
                        {
                            if (node.Capabilities != null)
                                node.Capabilities.AvailableDiskSpaceBytes = diskSpace.GetInt64();
                        }

                        // Update capabilities (GPU may be detected after initial handshake)
                        if (heartbeat.TryGetProperty("capabilities", out var caps))
                        {
                            try
                            {
                                var updated = JsonSerializer.Deserialize<WorkerCapabilities>(caps.GetRawText(), _jsonOptions);
                                if (updated != null)
                                    node.Capabilities = updated;
                            }
                            catch { }
                        }

                        await _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
                    }
                }
                catch
                {
                    // Heartbeat HTTP failed — don't update LastHeartbeat so timeout logic kicks in
                }
            }
        }

    /******************************************************************
     *  Job Dispatch
     ******************************************************************/

        private readonly SemaphoreSlim _dispatchLock = new(1, 1);

        /// <summary> Dequeues pending work items and dispatches one to each available worker node. </summary>
        private async Task RunDispatchAsync()
        {
            if (!IsMasterMode) return;
            if (!_recoveryComplete.Task.IsCompleted) return; // Wait for recovery before dispatching
            if (_transcodingService.IsPaused) return; // Respect master's queue pause
            if (!await _dispatchLock.WaitAsync(0)) return;

            try
            {
                var availableNodes = _nodes.Values
                    .Where(n => n.Role == "node" && n.Status == NodeStatus.Online && !n.IsPaused && n.ActiveWorkItemId == null)
                    .ToList();

                if (availableNodes.Count == 0) return;

                var options = _transcodingService.GetLastOptions();
                if (options == null)
                {
                    // After restart, _lastOptions isn't set until local encoding runs.
                    // Load settings from disk so dispatch doesn't stall.
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

                foreach (var node in availableNodes)
                {
                    var workItem = _transcodingService.DequeueForRemoteProcessing();
                    if (workItem == null) break; // No more pending items

                    _ = Task.Run(() => DispatchToNodeAsync(node, workItem, options));
                }
            }
            finally
            {
                _dispatchLock.Release();
            }
        }

        /// <summary>
        /// Maximum number of upload retry attempts.
        /// </summary>
        private const int MaxUploadRetries = 5;

        /// <summary>
        /// Chunk size for file transfers (50MB).
        /// </summary>
        private const int ChunkSize = 50 * 1024 * 1024;

        /// <summary>
        /// Uploads a source file to a worker node in 50MB chunks with SHA256 verification.
        /// Supports resume from last received byte.
        /// </summary>
        private async Task UploadFileToNodeAsync(HttpClient client, string baseUrl, WorkItem workItem, CancellationToken ct = default)
        {
            var totalSize = workItem.Size;
            Console.WriteLine($"Cluster: Uploading {workItem.FileName} ({totalSize / 1048576}MB) to {workItem.AssignedNodeName} in {ChunkSize / 1048576}MB chunks...");

            // Check how much the node already has (resume support)
            // Round down to the nearest chunk boundary to discard any partially-written
            // chunk from a killed process — the last partial chunk may be corrupt
            long rawOffset = await GetNodeReceivedBytesAsync(client, baseUrl, workItem.Id);
            long offset = (rawOffset / ChunkSize) * ChunkSize; // Align to chunk boundary
            if (rawOffset >= totalSize)
            {
                Console.WriteLine($"Cluster: Node already has the complete file");
                return;
            }
            if (offset > 0)
                Console.WriteLine($"Cluster: Resuming upload at {offset / 1048576}MB (aligned from {rawOffset / 1048576}MB)");

            int consecutiveFailures = 0;
            const int MaxConsecutiveFailures = 60; // ~10 minutes at escalating intervals

            using var fileStream = new FileStream(workItem.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileStream.Seek(offset, SeekOrigin.Begin);

            while (offset < totalSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunkLength = (int)Math.Min(ChunkSize, totalSize - offset);
                var chunkBuffer = new byte[chunkLength];
                var bytesRead = 0;
                while (bytesRead < chunkLength)
                {
                    var read = await fileStream.ReadAsync(chunkBuffer.AsMemory(bytesRead, chunkLength - bytesRead));
                    if (read == 0) break;
                    bytesRead += read;
                }

                var chunkHash = Convert.ToHexString(SHA256.HashData(chunkBuffer.AsSpan(0, bytesRead))).ToLower();

                // Send this chunk — retry persistently with backoff
                while (true)
                {
                    try
                    {
                        var chunkContent = new ByteArrayContent(chunkBuffer, 0, bytesRead);
                        chunkContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/cluster/files/{workItem.Id}");
                        request.Content = chunkContent;
                        request.Headers.Add("X-Snacks-Secret", _config.SharedSecret);
                        request.Headers.Add("X-Original-FileName", workItem.FileName);
                        request.Headers.Add("X-Total-Size", totalSize.ToString());
                        request.Headers.Add("X-Bitrate", workItem.Bitrate.ToString());
                        request.Headers.Add("X-Duration", workItem.Length.ToString());
                        request.Headers.Add("Range", $"bytes={offset}-");
                        request.Headers.Add("X-Chunk-Hash", chunkHash);

                        var response = await CreateAuthenticatedClient().SendAsync(request);

                        if (!response.IsSuccessStatusCode)
                        {
                            var errorBody = await response.Content.ReadAsStringAsync();
                            throw new Exception($"HTTP {(int)response.StatusCode}: {errorBody}");
                        }

                        if (response.Headers.TryGetValues("X-Hash-Match", out var hashMatch) &&
                            hashMatch.FirstOrDefault() == "false")
                        {
                            throw new Exception("Chunk hash mismatch — data corrupted in transit");
                        }

                        offset += bytesRead;
                        consecutiveFailures = 0;

                        workItem.TransferProgress = (int)(offset * 100 / totalSize);
                        workItem.Status = WorkItemStatus.Processing;
                        workItem.RemoteJobPhase = "Uploading";
                        workItem.ErrorMessage = null;
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                        break; // Chunk sent successfully
                    }
                    catch (Exception ex)
                    {
                        consecutiveFailures++;
                        var delay = 5;

                        Console.WriteLine($"Cluster: Upload chunk failed at {offset / 1048576}MB (failure {consecutiveFailures}/{MaxConsecutiveFailures}): {ex.Message} — retrying in {delay}s...");

                        workItem.ErrorMessage = $"Upload retry {consecutiveFailures} — {ex.Message}";
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                        if (consecutiveFailures >= MaxConsecutiveFailures)
                            throw new Exception($"Upload failed at {offset / 1048576}MB after {MaxConsecutiveFailures} consecutive failures");

                        await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                    }
                }
            }

            // Verify final size
            var finalSize = await GetNodeReceivedBytesAsync(CreateAuthenticatedClient(), baseUrl, workItem.Id);
            if (finalSize != totalSize)
                throw new Exception($"Upload size mismatch — sent {totalSize}, node has {finalSize}");

            Console.WriteLine($"Cluster: Upload of {workItem.FileName} complete ({totalSize / 1048576}MB)");
        }

        /// <summary>
        /// Downloads an encoded output file from a worker node in 50MB chunks with SHA256 verification.
        /// Supports resume from last received byte.
        /// </summary>
        private async Task DownloadFileFromNodeAsync(string nodeBaseUrl, string jobId, string outputPath, WorkItem workItem, CancellationToken ct = default)
        {
            long offset = 0;
            long totalSize = 0;
            int consecutiveFailures = 0;
            const int MaxConsecutiveFailures = 120; // 10 minutes at 5s intervals before giving up
            string? expectedFileHash = null;

            // If a partial download exists, resume from the last chunk boundary.
            // Round down to discard any partially-written chunk from a killed process —
            // the last partial chunk may be corrupt (same approach as upload resume).
            if (File.Exists(outputPath))
            {
                long rawOffset = new FileInfo(outputPath).Length;
                offset = (rawOffset / ChunkSize) * ChunkSize;
                if (offset > 0)
                {
                    // Truncate the file to the aligned offset so we re-download the last partial chunk
                    using (var truncStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write))
                        truncStream.SetLength(offset);
                    Console.WriteLine($"Cluster: Resuming download at {offset / 1048576}MB (aligned from {rawOffset / 1048576}MB)");
                }
            }

            while (true)
            {
                try
                {
                    var client = CreateAuthenticatedClient();
                    var request = new HttpRequestMessage(HttpMethod.Get, $"{nodeBaseUrl}/api/cluster/files/{jobId}/output");
                    request.Headers.Add("X-Snacks-Secret", _config.SharedSecret);
                    if (offset > 0)
                        request.Headers.Add("Range", $"bytes={offset}-");

                    var response = await client.SendAsync(request);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        throw new Exception($"HTTP {(int)response.StatusCode}: {errorBody}");
                    }

                    // Get total size from header
                    if (response.Headers.TryGetValues("X-Total-Size", out var sizeValues) &&
                        long.TryParse(sizeValues.FirstOrDefault(), out var ts))
                        totalSize = ts;

                    // Get full file hash from first response for end-to-end verification
                    if (expectedFileHash == null && response.Headers.TryGetValues("X-File-Hash", out var fileHashValues))
                        expectedFileHash = fileHashValues.FirstOrDefault();

                    // Get chunk hash for verification
                    string? expectedHash = null;
                    if (response.Headers.TryGetValues("X-Chunk-Hash", out var hashValues))
                        expectedHash = hashValues.FirstOrDefault();

                    var chunkData = await response.Content.ReadAsByteArrayAsync();
                    if (chunkData.Length == 0)
                    {
                        if (totalSize > 0 && offset < totalSize)
                            throw new Exception("Empty chunk received before download complete");
                        break;
                    }

                    // Verify hash
                    if (!string.IsNullOrEmpty(expectedHash))
                    {
                        var actualHash = Convert.ToHexString(SHA256.HashData(chunkData)).ToLower();
                        if (actualHash != expectedHash)
                            throw new Exception("Chunk hash mismatch during download");
                    }

                    // Write chunk to disk
                    var mode = offset > 0 ? FileMode.OpenOrCreate : FileMode.Create;
                    using (var fs = new FileStream(outputPath, mode, FileAccess.Write))
                    {
                        if (offset > 0)
                            fs.Seek(offset, SeekOrigin.Begin);
                        await fs.WriteAsync(chunkData);
                    }

                    offset += chunkData.Length;
                    consecutiveFailures = 0; // Reset on success

                    // Update progress
                    if (totalSize > 0)
                    {
                        workItem.TransferProgress = (int)(offset * 100 / totalSize);
                        await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                    }

                    // Check if we've downloaded everything
                    if (totalSize > 0 && offset >= totalSize) break;
                }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    var delay = Math.Min(consecutiveFailures * 10, 60); // 10s, 20s, 30s... up to 60s

                    Console.WriteLine($"Cluster: Download chunk failed at {offset / 1048576}MB (failure {consecutiveFailures}/{MaxConsecutiveFailures}): {ex.Message} — retrying in {delay}s...");

                    workItem.ErrorMessage = $"Download retry {consecutiveFailures} — {ex.Message}";
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                        throw new Exception($"Download failed after {MaxConsecutiveFailures} consecutive failures at {offset / 1048576}MB");

                    await Task.Delay(TimeSpan.FromSeconds(delay), ct);

                    // Re-check if partial file still exists (could have been cleaned up)
                    // Align to chunk boundary to discard any partially-written chunk
                    if (File.Exists(outputPath))
                        offset = (new FileInfo(outputPath).Length / ChunkSize) * ChunkSize;
                }
            }

            // Verify complete file hash for end-to-end integrity
            if (!string.IsNullOrEmpty(expectedFileHash))
            {
                using var verifyFs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actualFileHash = Convert.ToHexString(await SHA256.HashDataAsync(verifyFs)).ToLower();
                if (actualFileHash != expectedFileHash)
                {
                    try { File.Delete(outputPath); } catch { }
                    throw new Exception($"File hash mismatch after download — expected {expectedFileHash}, got {actualFileHash}");
                }
                Console.WriteLine($"Cluster: File hash verified for {workItem.FileName}");
            }

            workItem.ErrorMessage = null;
            Console.WriteLine($"Cluster: Download of result complete ({offset / 1048576}MB)");
        }

        /// <summary>
        /// Checks how many bytes of a file have been received by a node.
        /// Used for resume support.
        /// </summary>
        private async Task<long> GetNodeReceivedBytesAsync(HttpClient client, string baseUrl, string jobId)
        {
            try
            {
                var headRequest = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/api/cluster/files/{jobId}");
                headRequest.Headers.Add("X-Snacks-Secret", _config.SharedSecret);
                var response = await client.SendAsync(headRequest);
                if (response.IsSuccessStatusCode &&
                    response.Headers.TryGetValues("X-Received-Bytes", out var values) &&
                    long.TryParse(values.FirstOrDefault(), out var received))
                {
                    return received;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Dispatches a work item to a worker node: uploads the source file,
        /// verifies the upload, and sends the job assignment.
        /// </summary>
        private async Task DispatchToNodeAsync(ClusterNode node, WorkItem workItem, EncoderOptions options)
        {
            // Prevent concurrent dispatches for the same job
            if (!_activeUploads.TryAdd(workItem.Id, true))
            {
                Console.WriteLine($"Cluster: Upload already in progress for {workItem.FileName} — skipping duplicate dispatch");
                return;
            }

            var baseUrl = $"http://{node.IpAddress}:{node.Port}";
            var jobCts = new CancellationTokenSource();
            _jobCts[workItem.Id] = jobCts;

            try
            {
                // Reuse the previous work item ID if one exists in the DB — the node may
                // have partial data stored under that ID from a previous upload attempt
                var dbFile = await _mediaFileRepo.GetByPathAsync(Path.GetFullPath(workItem.Path));
                if (dbFile?.RemoteWorkItemId != null)
                {
                    var oldId = workItem.Id;
                    workItem.Id = dbFile.RemoteWorkItemId;
                    _transcodingService.ReplaceWorkItemId(oldId, workItem.Id, workItem);
                    // Re-key the CTS under the new ID
                    _jobCts.TryRemove(oldId, out _);
                    _jobCts[workItem.Id] = jobCts;
                    Console.WriteLine($"Cluster: Reusing previous job ID {workItem.Id} for {workItem.FileName}");
                }

                workItem.AssignedNodeId = node.NodeId;
                workItem.AssignedNodeName = node.Hostname;
                workItem.RemoteJobPhase = "Uploading";
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                // Track remote job — DB first (source of truth for crash recovery),
                // then in-memory cache. If DB write fails, we never populate the cache.
                await _mediaFileRepo.AssignToRemoteNodeAsync(
                    Path.GetFullPath(workItem.Path), workItem.Id, node.NodeId, node.Hostname,
                    node.IpAddress, node.Port, "Uploading");
                _remoteJobs[workItem.Id] = workItem;
                UpdateNodeStatus(node.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

                await UploadFileToNodeAsync(CreateAuthenticatedClient(), baseUrl, workItem, jobCts.Token);

                // Confirm byte count before submitting the job offer — prevents encoding corrupt/partial uploads.
                var receivedBytes = await GetNodeReceivedBytesAsync(CreateAuthenticatedClient(), baseUrl, workItem.Id);
                if (receivedBytes != workItem.Size)
                {
                    // Upload incomplete — clean up and re-queue
                    Console.WriteLine($"Cluster: Upload verification failed for {workItem.FileName} — expected {workItem.Size}, node has {receivedBytes}");
                    try { await CreateAuthenticatedClient().DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }
                    throw new Exception("Upload verification failed");
                }

                workItem.RemoteJobPhase = "Encoding";
                workItem.TransferProgress = 0;
                await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Encoding");
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                // Compute source file hash for end-to-end integrity verification
                var sourceFileHash = await ComputeFileHashAsync(workItem.Path);

                var assignment = new JobAssignment
                {
                    JobId = workItem.Id,
                    FileName = workItem.FileName,
                    FileSize = workItem.Size,
                    Options = CloneOptions(options),
                    Probe = workItem.Probe,
                    Duration = workItem.Length,
                    Bitrate = workItem.Bitrate,
                    IsHevc = workItem.IsHevc,
                    SourceFileHash = sourceFileHash
                };

                var assignContent = new StringContent(
                    JsonSerializer.Serialize(assignment, _jsonOptions),
                    Encoding.UTF8, "application/json");

                // Retry the job offer — node might briefly reject if it's still processing the upload
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var offerResponse = await CreateAuthenticatedClient().PostAsync($"{baseUrl}/api/cluster/jobs/offer", assignContent);
                    if (offerResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Cluster: Job {workItem.FileName} dispatched to {node.Hostname}");
                        return; // Success
                    }

                    var errorBody = await offerResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"Cluster: Job offer rejected (attempt {attempt + 1}): {errorBody}");

                    if (attempt < 2)
                        await Task.Delay(TimeSpan.FromSeconds(2));
                }

                throw new Exception("Job offer rejected after 3 attempts");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Failed to dispatch {workItem.FileName} to {node.Hostname}: {ex.Message}");

                // Clean up node temp files on failure
                try { await CreateAuthenticatedClient().DeleteAsync($"{baseUrl}/api/cluster/files/{workItem.Id}"); } catch { }

                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                workItem.TransferProgress = 0;
                _remoteJobs.TryRemove(workItem.Id, out _);
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                _transcodingService.RequeueWorkItem(workItem);
                UpdateNodeStatus(node.NodeId, NodeStatus.Online);
            }
            finally
            {
                _activeUploads.TryRemove(workItem.Id, out _);
                if (_jobCts.TryRemove(workItem.Id, out var removedCts))
                    removedCts.Dispose();
            }
        }

    /******************************************************************
     *  Remote Job Completion
     ******************************************************************/

        /// <summary> Applies an encoding progress update received from a worker node. </summary>
        /// <param name="jobId">The job ID reported by the node.</param>
        /// <param name="progress">The progress payload from the node.</param>
        public async Task HandleRemoteProgressAsync(string jobId, JobProgress progress)
        {
            if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;

            workItem.Progress = progress.Progress;
            workItem.RemoteJobPhase = progress.Phase ?? "Encoding";
            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

            if (!string.IsNullOrEmpty(progress.LogLine))
                await _hubContext.Clients.All.SendAsync("TranscodingLog", jobId, progress.LogLine);
        }

        /// <summary>
        /// Handles completion of a remote job: downloads the output,
        /// validates it, and performs file placement.
        /// </summary>
        public async Task HandleRemoteCompletionAsync(string jobId, string nodeBaseUrl)
        {
            if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;

            // Prevent concurrent downloads for the same job
            if (!_activeDownloads.TryAdd(jobId, true)) return;

            try
            {
                // Check source file still exists on master before downloading the result
                if (!File.Exists(workItem.Path))
                {
                    Console.WriteLine($"Cluster: Source file {workItem.Path} no longer exists — discarding remote result");
                    workItem.Status = WorkItemStatus.Failed;
                    workItem.ErrorMessage = "Source file was removed during encoding";
                    workItem.CompletedAt = DateTime.UtcNow;
                    _remoteJobs.TryRemove(jobId, out _);
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Failed);
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                    // Still clean up the node
                    try
                    {
                        var cleanupClient = CreateAuthenticatedClient();
                        await cleanupClient.DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}");
                    }
                    catch { }
                    return;
                }

                workItem.RemoteJobPhase = "Downloading";
                workItem.TransferProgress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                // Determine output path
                var options = _transcodingService.GetLastOptions() ?? new EncoderOptions();
                var ext = options.Format == "mp4" ? ".mp4" : ".mkv";
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

                // Download encoded file from node in chunks with persistent retry.
                // The encoded file exists on the node — never re-encode, just keep trying.
                _jobCts.TryGetValue(jobId, out var dlCts);
                await DownloadFileFromNodeAsync(nodeBaseUrl, jobId, outputPath, workItem, dlCts?.Token ?? CancellationToken.None);

                // Validate output
                var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
                if (!_ffprobeService.ConvertedSuccessfully(workItem.Probe!, outputProbe))
                {
                    try { File.Delete(outputPath); } catch { }

                    // Track validation failures — don't retry the same bad file forever
                    var validationKey = $"_validation_{jobId}";
                    var failCount = 1;
                    if (workItem.ErrorMessage?.StartsWith("Validation failed") == true)
                    {
                        int.TryParse(workItem.ErrorMessage.Split('(', ')').ElementAtOrDefault(1), out failCount);
                        failCount++;
                    }

                    if (failCount >= 3)
                    {
                        // Output is persistently bad — clean up node and re-queue for fresh encode
                        Console.WriteLine($"Cluster: Output validation failed {failCount} times for {workItem.FileName} — re-queuing for fresh encode");
                        try { await CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }
                        var prevNodeId = workItem.AssignedNodeId;
                        _remoteJobs.TryRemove(jobId, out _);
                        await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                        workItem.AssignedNodeId = null;
                        workItem.AssignedNodeName = null;
                        workItem.RemoteJobPhase = null;
                        workItem.ErrorMessage = null;
                        _transcodingService.RequeueWorkItem(workItem);
                        if (prevNodeId != null) UpdateNodeStatus(prevNodeId, NodeStatus.Online);
                        return;
                    }

                    throw new Exception($"Validation failed ({failCount}) — duration mismatch in remote output");
                }

                // Handle output placement (same as local)
                await _transcodingService.HandleRemoteCompletion(workItem, outputPath, options);

                // Clear DB assignment columns now that the job is fully complete
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Completed);

                // Clean up node temp files
                try { await CreateAuthenticatedClient().DeleteAsync($"{nodeBaseUrl}/api/cluster/files/{jobId}"); } catch { }

                // Update node status
                UpdateNodeStatus(workItem.AssignedNodeId!, NodeStatus.Online);
                if (_nodes.TryGetValue(workItem.AssignedNodeId!, out var node))
                    node.CompletedJobs++;

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
                    // Exhausted retries — fall back to re-encode
                    Console.WriteLine($"Cluster: Download failed after {MaxDownloadRetries} attempts — re-queuing for fresh encode");
                    _remoteJobs.TryRemove(jobId, out _);
                    _downloadRetryCounts.TryRemove(jobId, out _);
                    await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                    workItem.AssignedNodeId = null;
                    workItem.AssignedNodeName = null;
                    workItem.RemoteJobPhase = null;
                    workItem.ErrorMessage = null;
                    _transcodingService.RequeueWorkItem(workItem);
                }
                else
                {
                    workItem.RemoteJobPhase = "Downloading";
                    workItem.ErrorMessage = $"Download retry {retryCount}/{MaxDownloadRetries} — {ex.Message}";
                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                    await _mediaFileRepo.UpdateRemoteJobPhaseAsync(Path.GetFullPath(workItem.Path), "Downloading");

                    // Schedule a single retry after 60 seconds
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60));
                        // Don't retry if the job was cancelled or removed
                        if (_jobCts.TryGetValue(jobId, out var retryCts) && retryCts.IsCancellationRequested) return;
                        if (_remoteJobs.TryGetValue(jobId, out var retryItem) &&
                            retryItem.RemoteJobPhase == "Downloading")
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
        ///     Handles a failure report from a worker node. Checks whether a completed output
        ///     already exists on the node before re-queuing or marking the job as permanently failed.
        /// </summary>
        /// <param name="jobId">The ID of the failed job.</param>
        /// <param name="errorMessage">The error message reported by the node, if any.</param>
        public async Task HandleRemoteFailureAsync(string jobId, string? errorMessage)
        {
            if (!_remoteJobs.TryGetValue(jobId, out var workItem)) return;

            Console.WriteLine($"Cluster: Remote job {workItem.FileName} failed: {errorMessage}");

            // Check if the node still has a completed output before giving up
            if (workItem.AssignedNodeId != null && _nodes.TryGetValue(workItem.AssignedNodeId, out var node))
            {
                try
                {
                    var baseUrl = $"http://{node.IpAddress}:{node.Port}";
                    var checkResponse = await CreateAuthenticatedClient().GetAsync(
                        $"{baseUrl}/api/cluster/files/{jobId}/output");
                    if (checkResponse.IsSuccessStatusCode)
                    {
                        // Output exists — the encoding succeeded, just the reporting timed out
                        Console.WriteLine($"Cluster: Node still has output for {workItem.FileName} — attempting download instead of re-encode");
                        await HandleRemoteCompletionAsync(jobId, baseUrl);
                        return;
                    }
                }
                catch { }

                node.FailedJobs++;
                UpdateNodeStatus(node.NodeId, NodeStatus.Online);
            }

            await HandleNodeFailure(jobId);
        }

        /// <summary>
        /// Handles node failure: cancels active transfers, increments failure count,
        /// and re-queues or marks as permanently failed.
        /// </summary>
        private async Task HandleNodeFailure(string jobId)
        {
            if (!_remoteJobs.TryRemove(jobId, out var workItem)) return;

            // Cancel any running upload/download for this job
            if (_jobCts.TryRemove(jobId, out var jobCts))
            {
                jobCts.Cancel();
                jobCts.Dispose();
            }

            // Release transfer locks so the job can be re-dispatched
            _activeUploads.TryRemove(jobId, out _);
            _activeDownloads.TryRemove(jobId, out _);

            workItem.RemoteFailureCount++;
            workItem.ErrorMessage = $"Remote: failed (attempt {workItem.RemoteFailureCount})";
            await _mediaFileRepo.IncrementRemoteFailureCountAsync(Path.GetFullPath(workItem.Path));

            if (workItem.RemoteFailureCount >= 3)
            {
                workItem.Status = WorkItemStatus.Failed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Failed);
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await _transcodingService.MarkWorkItemFailed(workItem.Id, workItem.ErrorMessage);
            }
            else
            {
                workItem.AssignedNodeId = null;
                workItem.AssignedNodeName = null;
                workItem.RemoteJobPhase = null;
                await _mediaFileRepo.ClearRemoteAssignmentAsync(Path.GetFullPath(workItem.Path), MediaFileStatus.Queued);
                _transcodingService.RequeueWorkItem(workItem);
            }
        }

        /// <summary>
        /// Cancels a remote job — called from master when user cancels/stops a work item.
        /// Sends DELETE to the node to kill its FFmpeg process and clean up.
        /// </summary>
        public async Task CancelRemoteJobOnNodeAsync(string jobId, string nodeId)
        {
            _remoteJobs.TryRemove(jobId, out _);

            // Cancel any running upload/download for this job
            if (_jobCts.TryRemove(jobId, out var jobCts))
            {
                jobCts.Cancel();
                jobCts.Dispose();
            }

            if (_nodes.TryGetValue(nodeId, out var node))
            {
                try
                {
                    var client = CreateAuthenticatedClient();
                    var baseUrl = $"http://{node.IpAddress}:{node.Port}";
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
        /// Cancels a job running locally on this node — called via DELETE /api/cluster/jobs/{id}
        /// </summary>
        public void CancelRemoteJob(string jobId)
        {
            if (_currentRemoteJob?.Id == jobId)
            {
                Console.WriteLine($"Cluster: Cancelling remote job {jobId}");
                _remoteJobCts?.Cancel();
                // The encoding loop will catch the cancellation and clean up
            }
        }

    /******************************************************************
     *  Node-Side Job Handling
     ******************************************************************/

        private WorkItem?                   _currentRemoteJob;
        private CancellationTokenSource?    _remoteJobCts;
        private string?                     _completedJobId;

        private string? _receivingJobId;
        private readonly string _pendingCompletionsPath;

        /// <summary>
        /// Whether this node is currently processing a remote job.
        /// </summary>
        public bool IsProcessingRemoteJob() => _currentRemoteJob != null || _receivingJobId != null || _completedJobId != null;

        /// <summary>
        /// Sets the job ID being received. Used to track node status during file transfer.
        /// </summary>
        public void SetReceivingJob(string? jobId)
        {
            var oldJobId = _receivingJobId;
            _receivingJobId = jobId;

            // If a new job ID is replacing an old one (master restarted with different ID),
            // remove the old item from the node's UI. Don't delete temp files — only the
            // master's explicit DELETE /api/cluster/files/{id} should do that.
            if (oldJobId != null && oldJobId != jobId)
            {
                _ = _hubContext.Clients.All.SendAsync("WorkItemUpdated", new
                {
                    id = oldJobId,
                    status = "Completed",
                    progress = 100,
                    remoteJobPhase = (string?)null,
                    completedAt = DateTime.UtcNow
                });
            }
        }

        /// <summary>
        /// Accepts or rejects a job offer from the master.
        /// Validates file existence, size, and hash before accepting.
        /// </summary>
        public async Task<bool> AcceptJobOfferAsync(JobAssignment assignment)
        {
            if (_nodePaused)
                return false; // Paused — not accepting jobs

            if (_currentRemoteJob != null)
                return false; // Already busy

            var tempDir = GetNodeTempDirectory(assignment.JobId);
            var inputPath = Path.Combine(tempDir, assignment.FileName);

            if (!File.Exists(inputPath))
                return false; // Source file not uploaded yet

            // Check if we already have the encoded output — skip encoding
            var existingOutput = GetOutputFileForJob(assignment.JobId);
            if (existingOutput != null)
            {
                Console.WriteLine($"Cluster: Output already exists for {assignment.FileName} — skipping encode, ready for download");
                _completedJobId = assignment.JobId;
                _receivingJobId = null;
                return true;
            }

            // Verify uploaded file size matches expected
            var actualSize = new FileInfo(inputPath).Length;
            if (actualSize != assignment.FileSize)
            {
                Console.WriteLine($"Cluster: File size mismatch for {assignment.FileName} — expected {assignment.FileSize}, got {actualSize}. Rejecting job.");
                try { File.Delete(inputPath); } catch { }
                return false;
            }

            // Verify source file hash for end-to-end integrity
            if (!string.IsNullOrEmpty(assignment.SourceFileHash))
            {
                var actualHash = await ComputeFileHashAsync(inputPath);
                if (!string.Equals(actualHash, assignment.SourceFileHash, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Cluster: Source file hash mismatch for {assignment.FileName} — expected {assignment.SourceFileHash}, got {actualHash}. Rejecting job.");
                    try { File.Delete(inputPath); } catch { }
                    return false;
                }
                Console.WriteLine($"Cluster: Source file hash verified for {assignment.FileName}");
            }

            var workItem = new WorkItem
            {
                Id = assignment.JobId,
                FileName = assignment.FileName,
                Path = inputPath,
                Size = assignment.FileSize,
                Bitrate = assignment.Bitrate,
                Length = assignment.Duration,
                IsHevc = assignment.IsHevc,
                Probe = assignment.Probe,
                Status = WorkItemStatus.Processing,
                StartedAt = DateTime.UtcNow
            };

            _currentRemoteJob = workItem;
            _receivingJobId = null; // Encoding takes over from receiving
            _remoteJobCts = new CancellationTokenSource();

            // Run encoding in background
            _ = Task.Run(() => ExecuteRemoteJobAsync(workItem, assignment.Options));

            return true;
        }

        /// <summary>
        /// Executes a remote job: encodes the file and reports progress/completion to the master.
        /// </summary>
        private async Task ExecuteRemoteJobAsync(WorkItem workItem, EncoderOptions options)
        {
            var masterUrl = _config.MasterUrl?.TrimEnd('/');
            if (string.IsNullOrEmpty(masterUrl))
            {
                // Find master from nodes
                var masterNode = _nodes.Values.FirstOrDefault(n => n.Role == "master");
                if (masterNode != null)
                    masterUrl = $"http://{masterNode.IpAddress}:{masterNode.Port}";
            }

            var encodingSucceeded = false;
            try
            {
                // Override output path to node temp directory
                var tempDir = GetNodeTempDirectory(workItem.Id);
                options.OutputDirectory = null;
                options.EncodeDirectory = tempDir;
                options.DeleteOriginalFile = false; // Never delete on node side

                // Hook into log reporting to master — buffer and send every 2 seconds
                var logBuffer = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var lastLogSend = DateTime.MinValue;

                _transcodingService.SetLogCallback(async (id, message) =>
                {
                    logBuffer.Enqueue(message);

                    // Throttle: only send every 2 seconds
                    var now = DateTime.UtcNow;
                    if ((now - lastLogSend).TotalSeconds < 2) return;
                    lastLogSend = now;

                    // Drain buffer into a single request
                    var lines = new List<string>();
                    while (logBuffer.TryDequeue(out var line)) lines.Add(line);
                    if (lines.Count == 0 || masterUrl == null) return;

                    try
                    {
                        var client = CreateAuthenticatedClient();
                        var progressReport = new JobProgress
                        {
                            JobId = id,
                            Progress = _currentRemoteJob?.Progress ?? 0,
                            Phase = "Encoding",
                            LogLine = string.Join("\n", lines)
                        };
                        var content = new StringContent(
                            JsonSerializer.Serialize(progressReport, _jsonOptions),
                            Encoding.UTF8, "application/json");
                        await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                    }
                    catch { }
                });

                _transcodingService.SetProgressCallback(async (id, progress) =>
                {
                    if (masterUrl != null)
                    {
                        try
                        {
                            var client = CreateAuthenticatedClient();
                            var progressReport = new JobProgress
                            {
                                JobId = id,
                                Progress = progress,
                                Phase = "Encoding"
                            };
                            var content = new StringContent(
                                JsonSerializer.Serialize(progressReport, _jsonOptions),
                                Encoding.UTF8, "application/json");
                            await client.PostAsync($"{masterUrl}/api/cluster/jobs/{id}/progress", content);
                        }
                        catch { } // Best-effort progress reporting
                    }
                });

                // Use the transcoding service to encode
                await _transcodingService.ConvertVideoForRemoteAsync(workItem, options, _remoteJobCts?.Token ?? CancellationToken.None);
                encodingSucceeded = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Cluster: Remote job encoding failed: {ex.Message}");
                if (masterUrl != null)
                {
                    try
                    {
                        var client = CreateAuthenticatedClient();
                        var failure = new { jobId = workItem.Id, errorMessage = ex.Message };
                        var content = new StringContent(
                            JsonSerializer.Serialize(failure, _jsonOptions),
                            Encoding.UTF8, "application/json");
                        await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/failed", content);
                    }
                    catch { }
                }
            }
            finally
            {
                // Keep reporting this job ID until master downloads and cleans up
                _completedJobId = encodingSucceeded ? _currentRemoteJob?.Id : null;
                _currentRemoteJob = null;
                _transcodingService.SetProgressCallback(null);
                _transcodingService.SetLogCallback(null);
            }

            // Report completion OUTSIDE the try/catch — encoding succeeded,
            // so even if this POST fails, the output file still exists on disk
            // and the master can discover it via heartbeat or recovery
            if (encodingSucceeded && masterUrl != null)
            {
                // Persist the completed job ID so we can retry on every heartbeat
                // until the master acknowledges receipt
                await PersistCompletedJobAsync(workItem.Id, masterUrl, selfUrl: null);

                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        var client = CreateAuthenticatedClient();
                        var selfUrl = $"http://{GetLocalIpAddress()}:{GetListeningPort()}";
                        var completion = new JobCompletion
                        {
                            JobId = workItem.Id,
                            Success = true,
                            OutputFileName = workItem.FileName
                        };
                        var content = new StringContent(
                            JsonSerializer.Serialize(new { completion, nodeBaseUrl = selfUrl }, _jsonOptions),
                            Encoding.UTF8, "application/json");
                        await client.PostAsync($"{masterUrl}/api/cluster/jobs/{workItem.Id}/complete", content);
                        Console.WriteLine($"Cluster: Reported completion for {workItem.FileName} to master");

                        // Remove from pending completions — master acknowledged
                        await RemoveCompletedJobAsync(workItem.Id);
                        break; // Success
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Cluster: Failed to report completion (attempt {attempt + 1}): {ex.Message}");
                        if (attempt < 9)
                            await Task.Delay(TimeSpan.FromSeconds(10));
                        // Don't report failure — the output file exists, master can recover it
                        // The persisted job ID will be retried on next heartbeat
                    }
                }
            }
        }

        /// <summary>
        ///     Appends a completed job record to the pending-completions file so the completion
        ///     can be re-reported on every heartbeat until the master acknowledges it.
        /// </summary>
        /// <param name="jobId">The completed job ID to persist.</param>
        /// <param name="masterUrl">The master's base URL, stored for retry requests.</param>
        /// <param name="selfUrl">This node's base URL, included in completion callbacks.</param>
        private async Task PersistCompletedJobAsync(string jobId, string masterUrl, string? selfUrl)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_pendingCompletionsPath)!);
                var completions = await LoadPendingCompletionsAsync();
                if (!completions.ContainsKey(jobId))
                {
                    completions[jobId] = new PendingCompletion
                    {
                        JobId = jobId,
                        MasterUrl = masterUrl,
                        OutputFileName = _currentRemoteJob?.FileName ?? "",
                        Timestamp = DateTime.UtcNow
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
        }

        /// <summary> Removes a job from the pending-completions file once the master has acknowledged it. </summary>
        /// <param name="jobId">The acknowledged job ID to remove.</param>
        private async Task RemoveCompletedJobAsync(string jobId)
        {
            try
            {
                var completions = await LoadPendingCompletionsAsync();
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
        }

        /// <summary> Loads the pending-completions file, returning an empty dictionary if the file does not exist or is corrupt. </summary>
        private async Task<Dictionary<string, PendingCompletion>> LoadPendingCompletionsAsync()
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
        ///     Re-posts all persisted pending completions to the master. Called on each heartbeat
        ///     cycle to recover from lost completion notifications.
        /// </summary>
        public async Task RetryPendingCompletionsAsync()
        {
            var completions = await LoadPendingCompletionsAsync();
            foreach (var kvp in completions.ToList())
            {
                var completion = kvp.Value;
                try
                {
                    var client = CreateAuthenticatedClient();
                    var selfUrl = $"http://{GetLocalIpAddress()}:{GetListeningPort()}";
                    var jobCompletion = new JobCompletion
                    {
                        JobId = completion.JobId,
                        Success = true,
                        OutputFileName = completion.OutputFileName
                    };
                    var content = new StringContent(
                        JsonSerializer.Serialize(new { jobCompletion, nodeBaseUrl = selfUrl }, _jsonOptions),
                        Encoding.UTF8, "application/json");
                    var response = await client.PostAsync($"{completion.MasterUrl}/api/cluster/jobs/{completion.JobId}/complete", content);
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

        /// <summary>
        /// Returns the temp directory path for a remote job, creating it if needed.
        /// Sanitizes the job ID to prevent path traversal attacks.
        /// </summary>
        /// <param name="jobId">The job ID to build the temp directory for.</param>
        /// <returns>The absolute path to the temp directory for this job.</returns>
        public string GetNodeTempDirectory(string jobId)
        {
            // Sanitize jobId to prevent path traversal — only allow GUID characters
            var safeJobId = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
            if (string.IsNullOrEmpty(safeJobId)) throw new ArgumentException("Invalid job ID");

            // Use the reliable work dir path (same as Program.cs), not _workDir from FileService
            var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
            var baseDir = string.IsNullOrWhiteSpace(_config.NodeTempDirectory)
                ? Path.Combine(workDir, "remote-jobs")
                : _config.NodeTempDirectory;
            var dir = Path.Combine(baseDir, safeJobId);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary> Returns the path to the encoded output file for a job, or <see langword="null"/> if no output exists yet. </summary>
        /// <param name="jobId">The job ID to look up the output for.</param>
        public string? GetOutputFileForJob(string jobId)
        {
            var tempDir = GetNodeTempDirectory(jobId);
            var files = Directory.GetFiles(tempDir, "*[snacks]*");
            return files.FirstOrDefault();
        }

        /// <summary>
        /// Deletes all temp files for a completed or cancelled job and clears the receiving/completed job ID references.
        /// </summary>
        /// <param name="jobId">The job ID to clean up.</param>
        public void CleanupJobFiles(string jobId)
        {
            if (_receivingJobId == jobId) _receivingJobId = null;
            if (_completedJobId == jobId) _completedJobId = null;


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
        /// Deletes all remote job temp directories unconditionally.
        /// Called on node startup to reclaim disk space from a previous crashed session.
        /// </summary>
        public void CleanupAllRemoteJobs()
        {
            var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
            var baseDir = string.IsNullOrWhiteSpace(_config.NodeTempDirectory)
                ? Path.Combine(workDir, "remote-jobs")
                : _config.NodeTempDirectory;
            if (Directory.Exists(baseDir))
            {
                try
                {
                    int cleaned = 0;
                    foreach (var dir in Directory.GetDirectories(baseDir))
                    {
                        try
                        {
                            Directory.Delete(dir, true);
                            cleaned++;
                        }
                        catch (IOException ex)
                        {
                            // File locked by zombie process — try deleting individual files
                            Console.WriteLine($"Cluster: Could not delete {dir}: {ex.Message} — trying individual files");
                            foreach (var file in Directory.GetFiles(dir))
                            {
                                try { File.Delete(file); } catch { }
                            }
                        }
                    }
                    if (cleaned > 0)
                        Console.WriteLine($"Cluster: Cleaned up {cleaned} orphaned remote job directories");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Cluster: Cleanup error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Selects the best available worker node for a job using a scoring algorithm.
        /// Scores nodes based on hardware match, GPU availability, and supported encoders.
        /// Among equal-scored nodes, prefers the one that has been idle longest.
        /// Returns <c>null</c> if no node scores above 0 (e.g., all have insufficient disk space).
        /// </summary>
        public ClusterNode? FindBestWorker(WorkItem workItem, EncoderOptions options)
        {
            var available = _nodes.Values
                .Where(n => n.Role == "node" && n.Status == NodeStatus.Online && n.ActiveWorkItemId == null)
                .ToList();

            if (available.Count == 0) return null;

            ClusterNode? best = null;
            int bestScore = 0;

            foreach (var node in available)
            {
                int score = ScoreNode(node, workItem, options);
                if (score > bestScore || (score == bestScore && best != null &&
                    node.LastHeartbeat < best.LastHeartbeat)) // Prefer node idle longest
                {
                    best = node;
                    bestScore = score;
                }
            }

            return bestScore > 0 ? best : null;
        }

        /// <summary>
        /// Assigns a numeric score to a candidate worker node for the given job.
        /// Returns a negative value if the node lacks sufficient disk space.
        /// Higher scores indicate a better fit (hardware match, encoder support).
        /// </summary>
        /// <param name="node">The candidate worker node to score.</param>
        /// <param name="workItem">The work item to be dispatched.</param>
        /// <param name="options">Encoder options describing the desired hardware and encoder.</param>
        /// <returns>A score ≥ 1 for viable nodes, or a large negative value if the node should be skipped.</returns>
        private int ScoreNode(ClusterNode node, WorkItem workItem, EncoderOptions options)
        {
            int score = 1; // Base score for being available
            var caps = node.Capabilities;
            if (caps == null) return score;

            // Check disk space (need ~2.5x source file for input + output + headroom)
            if (caps.AvailableDiskSpaceBytes < workItem.Size * 2.5)
                return -100;

            var hw = options.HardwareAcceleration?.ToLower() ?? "auto";

            // Exact hardware match
            if (hw != "auto" && hw != "none" &&
                string.Equals(caps.GpuVendor, hw, StringComparison.OrdinalIgnoreCase))
                score += 10;

            // Any GPU when auto mode
            if (hw == "auto" && caps.GpuVendor != "none" && !string.IsNullOrEmpty(caps.GpuVendor))
                score += 5;

            // Supports the specific encoder
            var encoder = options.Encoder?.ToLower() ?? "";
            if (caps.SupportedEncoders.Any(e => e.Equals(encoder, StringComparison.OrdinalIgnoreCase)))
                score += 3;

            return score;
        }

        /// <summary> Loads cluster configuration from disk into <see cref="_config"/>. No-ops if the file does not exist. </summary>
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
        }

        /// <summary> Persists the current <see cref="_config"/> to disk as JSON. </summary>
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

        /// <summary>
        /// Creates an <see cref="HttpClient"/> pre-configured with the cluster shared secret header
        /// and a generous timeout suitable for large file transfers over slow networks.
        /// </summary>
        private HttpClient CreateAuthenticatedClient()
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(30); // Large chunks + slow networks need generous timeout
            client.DefaultRequestHeaders.Add("X-Snacks-Secret", _config.SharedSecret);
            return client;
        }

        /// <summary>
        /// Computes the SHA256 hash of a file. Used for end-to-end source file integrity verification.
        /// </summary>
        private static async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hash).ToLower();
        }

        // Remote job state is now persisted in the SQLite MediaFiles table.
        // No JSON files, no HashSets, no flags.

        /// <summary>
        /// On master startup, check if any remote jobs were in-flight when we last shut down.
        /// Try to reconnect to the node and retrieve the result, or re-queue the job.
        /// </summary>
        private async Task RecoverRemoteJobsAsync()
        {
            try
            {
                var activeJobs = await _mediaFileRepo.GetActiveRemoteJobsAsync();
                if (activeJobs.Count == 0) return;

                Console.WriteLine($"Cluster: Recovering {activeJobs.Count} remote job(s) from database...");

                foreach (var mediaFile in activeJobs)
                {
                    if (!File.Exists(mediaFile.FilePath))
                    {
                        Console.WriteLine($"Cluster: Source file missing for {mediaFile.FileName} — clearing assignment");
                        await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Unseen);
                        continue;
                    }

                    // Use the SAME work item ID that was used for the original dispatch
                    // This is critical — the node has temp files stored under this ID
                    if (string.IsNullOrEmpty(mediaFile.RemoteWorkItemId))
                    {
                        Console.WriteLine($"Cluster: No RemoteWorkItemId for {mediaFile.FileName} — clearing and re-queuing for fresh dispatch");
                        await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                        continue;
                    }
                    var jobId = mediaFile.RemoteWorkItemId;
                    var workItem = await _transcodingService.CreateWorkItemWithIdAsync(jobId, mediaFile.FilePath);
                    if (workItem == null)
                    {
                        Console.WriteLine($"Cluster: Failed to reconstruct {mediaFile.FileName} — clearing assignment");
                        await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Unseen);
                        continue;
                    }

                    var baseUrl = $"http://{mediaFile.AssignedNodeIp}:{mediaFile.AssignedNodePort}";
                    bool nodeReachable = false;

                    // Wait for the node to become reachable — it may not have reconnected yet after master restart
                    for (int attempt = 0; attempt < 12; attempt++) // ~2 minutes (12 x 10s)
                    {
                        try
                        {
                            var hbResponse = await CreateAuthenticatedClient().GetAsync($"{baseUrl}/api/cluster/heartbeat");
                            if (hbResponse.IsSuccessStatusCode)
                            {
                                nodeReachable = true;
                                break;
                            }
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

                    // Check if the node is actively encoding THIS specific job
                    try
                    {
                        var hbBody = await (await CreateAuthenticatedClient().GetAsync($"{baseUrl}/api/cluster/heartbeat")).Content.ReadAsStringAsync();
                        var hbData = JsonSerializer.Deserialize<JsonElement>(hbBody);
                        if (hbData.TryGetProperty("currentJobId", out var curJob) && curJob.ValueKind != JsonValueKind.Null)
                        {
                            var nodeCurrentJob = curJob.GetString();
                            if (nodeCurrentJob == jobId)
                            {
                                // Recover progress from the node's heartbeat
                                int recoveredProgress = 0;
                                if (hbData.TryGetProperty("progress", out var progProp))
                                    recoveredProgress = progProp.GetInt32();

                                Console.WriteLine($"Cluster: Node is actively encoding {mediaFile.FileName} at {recoveredProgress}% — tracking and waiting for completion");
                                workItem.Status = WorkItemStatus.Processing;
                                workItem.Progress = recoveredProgress;
                                workItem.AssignedNodeId = mediaFile.AssignedNodeId;
                                workItem.AssignedNodeName = mediaFile.AssignedNodeName ?? "recovered";
                                workItem.RemoteJobPhase = "Encoding";
                                workItem.ErrorMessage = null;
                                _remoteJobs[workItem.Id] = workItem;
                                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                continue;
                            }
                            // Node is busy with a DIFFERENT job — fall through to check output/partial
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Cluster: Recovery heartbeat check failed for {mediaFile.FileName}: {ex.Message}"); }

                    // Check if the node has a completed output
                    try
                    {
                        var outputResponse = await CreateAuthenticatedClient().GetAsync($"{baseUrl}/api/cluster/files/{jobId}/output");
                        if (outputResponse.IsSuccessStatusCode)
                        {
                            Console.WriteLine($"Cluster: Node has output for {mediaFile.FileName} — downloading");
                            workItem.Status = WorkItemStatus.Processing;
                            workItem.AssignedNodeId = mediaFile.AssignedNodeId;
                            workItem.AssignedNodeName = mediaFile.AssignedNodeName ?? "recovered";
                            workItem.RemoteJobPhase = "Downloading";
                            workItem.RemoteFailureCount = mediaFile.RemoteFailureCount;
                            workItem.ErrorMessage = null;
                            _remoteJobs[workItem.Id] = workItem;
                            // Seed download retry counter from prior failures so restart doesn't allow infinite retries
                            if (mediaFile.RemoteFailureCount > 0)
                                _downloadRetryCounts[workItem.Id] = mediaFile.RemoteFailureCount * 3;
                            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                            await HandleRemoteCompletionAsync(workItem.Id, baseUrl);
                            continue;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Cluster: Recovery output check failed for {mediaFile.FileName}: {ex.Message}"); }

                    // Check if the node has a partial source file (upload was in progress)
                    try
                    {
                        var headRequest = new HttpRequestMessage(HttpMethod.Head, $"{baseUrl}/api/cluster/files/{jobId}");
                        headRequest.Headers.Add("X-Snacks-Secret", _config.SharedSecret);
                        var headResponse = await CreateAuthenticatedClient().SendAsync(headRequest);
                        if (headResponse.IsSuccessStatusCode &&
                            headResponse.Headers.TryGetValues("X-Received-Bytes", out var vals) &&
                            long.TryParse(vals.FirstOrDefault(), out var receivedBytes) && receivedBytes > 0)
                        {
                            Console.WriteLine($"Cluster: Node has {receivedBytes / 1048576}MB of {mediaFile.FileName} — resuming upload");
                            workItem.AssignedNodeId = mediaFile.AssignedNodeId;
                            workItem.AssignedNodeName = mediaFile.AssignedNodeName ?? "recovered";
                            workItem.RemoteJobPhase = "Uploading";
                            _remoteJobs[workItem.Id] = workItem;
                            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                            // Load settings from disk — _lastOptions isn't set yet after restart
                            EncoderOptions? options = null;
                            try
                            {
                                var settingsPath = Path.Combine(_workDir, "config", "settings.json");
                                if (File.Exists(settingsPath))
                                    options = JsonSerializer.Deserialize<EncoderOptions>(File.ReadAllText(settingsPath), _jsonOptions);
                            }
                            catch { }
                            options ??= new EncoderOptions();

                            var nodeForDispatch = _nodes.Values.FirstOrDefault(n => n.NodeId == mediaFile.AssignedNodeId)
                                ?? new ClusterNode
                                {
                                    NodeId = mediaFile.AssignedNodeId!,
                                    IpAddress = mediaFile.AssignedNodeIp!,
                                    Port = mediaFile.AssignedNodePort ?? 6767,
                                    Hostname = mediaFile.AssignedNodeName ?? "recovered"
                                };

                            // Resume directly — don't go through DispatchToNodeAsync which
                            // would re-do DB writes and potentially fail on redundant operations
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

                                    Console.WriteLine($"Cluster: Starting resumed upload for {workItem.FileName}...");
                                    workItem.Status = WorkItemStatus.Processing;
                                    workItem.RemoteJobPhase = "Uploading";
                                    workItem.ErrorMessage = null;
                                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                    UpdateNodeStatus(nodeForDispatch.NodeId, NodeStatus.Busy, workItem.Id, workItem.FileName);

                                    await UploadFileToNodeAsync(CreateAuthenticatedClient(), baseUrl, workItem, recoveryCts.Token);

                                    // Verify upload
                                    var finalSize = await GetNodeReceivedBytesAsync(CreateAuthenticatedClient(), baseUrl, workItem.Id);
                                    if (finalSize != workItem.Size)
                                    {
                                        Console.WriteLine($"Cluster: Resumed upload verification failed — expected {workItem.Size}, got {finalSize}");
                                        _remoteJobs.TryRemove(workItem.Id, out _);
                                        await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued);
                                        _transcodingService.RequeueWorkItem(workItem);
                                        return;
                                    }

                                    // Send job offer
                                    workItem.RemoteJobPhase = "Encoding";
                                    workItem.TransferProgress = 0;
                                    await _mediaFileRepo.UpdateRemoteJobPhaseAsync(mediaFile.FilePath, "Encoding");
                                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                                    var assignment = new JobAssignment
                                    {
                                        JobId = workItem.Id,
                                        FileName = workItem.FileName,
                                        FileSize = workItem.Size,
                                        Options = CloneOptions(options),
                                        Probe = workItem.Probe,
                                        Duration = workItem.Length,
                                        Bitrate = workItem.Bitrate,
                                        IsHevc = workItem.IsHevc
                                    };

                                    var assignContent = new StringContent(
                                        JsonSerializer.Serialize(assignment, _jsonOptions),
                                        Encoding.UTF8, "application/json");

                                    for (int attempt = 0; attempt < 3; attempt++)
                                    {
                                        var offerResponse = await CreateAuthenticatedClient().PostAsync(
                                            $"{baseUrl}/api/cluster/jobs/offer", assignContent);
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
                                    try { await _mediaFileRepo.ClearRemoteAssignmentAsync(mediaFile.FilePath, MediaFileStatus.Queued); } catch (Exception dbEx) { Console.WriteLine($"Cluster: Failed to clear assignment for {mediaFile.FileName}: {dbEx.Message}"); }
                                    _transcodingService.RequeueWorkItem(workItem);
                                }
                                finally
                                {
                                    _activeUploads.TryRemove(workItem.Id, out _);
                                    if (_jobCts.TryRemove(workItem.Id, out var rCts))
                                        rCts.Dispose();
                                }
                            });
                            continue;
                        }
                    }
                    catch (Exception ex) { Console.WriteLine($"Cluster: Recovery partial upload check failed for {mediaFile.FileName}: {ex.Message}"); }

                    // Nothing recoverable on the node — re-queue for fresh dispatch
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

        /// <summary> Returns the lowercase hex SHA-256 hash of the shared secret for safe logging and comparison. </summary>
        private static string HashSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hash).ToLower();
        }

        /// <summary>
        ///     Resolves the machine's primary outbound IPv4 address by opening a UDP socket toward a
        ///     public DNS server. Falls back to <c>127.0.0.1</c> if the network is unavailable.
        /// </summary>
        private static string GetLocalIpAddress()
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
                socket.Connect("8.8.8.8", 65530);
                var endpoint = socket.LocalEndPoint as IPEndPoint;
                return endpoint?.Address.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        /// <summary> Cached result of <see cref="GetListeningPort"/> so the environment variables are only read once. </summary>
        private int _resolvedPort = 0;

        /// <summary>
        /// Returns the HTTP port this instance is listening on.
        /// Checks <c>ASPNETCORE_URLS</c> first, then falls back to <c>appsettings.json</c> Kestrel config,
        /// then defaults to 6767.
        /// </summary>
        private int GetListeningPort()
        {
            if (_resolvedPort > 0) return _resolvedPort;

            // Check ASPNETCORE_URLS first (set by Electron and some Docker configs)
            var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
            if (!string.IsNullOrEmpty(urls))
            {
                try
                {
                    var uri = new Uri(urls.Split(';')[0]);
                    _resolvedPort = uri.Port;
                    return _resolvedPort;
                }
                catch { }
            }

            // Fall back to Kestrel config in appsettings.json
            try
            {
                var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(appSettingsPath))
                {
                    var json = File.ReadAllText(appSettingsPath);
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("Kestrel", out var kestrel) &&
                        kestrel.TryGetProperty("Endpoints", out var endpoints) &&
                        endpoints.TryGetProperty("Http", out var http) &&
                        http.TryGetProperty("Url", out var url))
                    {
                        var uri = new Uri(url.GetString()!);
                        _resolvedPort = uri.Port;
                        return _resolvedPort;
                    }
                }
            }
            catch { }

            _resolvedPort = 6767;
            return _resolvedPort;
        }

        /// <summary> Returns free disk space in bytes on the drive containing the node temp directory, or 0 on error. </summary>
        private long GetAvailableDiskSpace()
        {
            try
            {
                var dir = _config.NodeTempDirectory ?? _workDir;
                var drive = new DriveInfo(Path.GetPathRoot(dir) ?? dir);
                return drive.AvailableFreeSpace;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary> Creates a shallow copy of an <see cref="EncoderOptions"/> instance for safe per-job mutation. </summary>
        private static EncoderOptions CloneOptions(EncoderOptions options)
        {
            return new EncoderOptions
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
        }

    }

    /// <summary> A read-only stream wrapper that reports upload progress as data is read. </summary>
    internal class ProgressStream : System.IO.Stream
    {
        private readonly System.IO.Stream _inner;
        private readonly long _totalLength;
        /// <summary> Callback invoked with the current upload percentage (0–100) at most once per second. </summary>
        private readonly Func<int, Task> _onProgress;
        private long _bytesRead;
        private int _lastPercent;
        private DateTime _lastReport = DateTime.MinValue;

        /// <summary>
        /// Wraps <paramref name="inner"/> and reports read progress through <paramref name="onProgress"/>.
        /// </summary>
        /// <param name="inner">The underlying stream to read from.</param>
        /// <param name="totalLength">Total byte length used to compute percentage.</param>
        /// <param name="onProgress">Async callback receiving the current completion percentage (0–100).</param>
        /// <param name="initialOffset">Bytes already transferred before this stream started (for resume support).</param>
        public ProgressStream(System.IO.Stream inner, long totalLength, Func<int, Task> onProgress, long initialOffset = 0)
        {
            _inner = inner;
            _totalLength = totalLength;
            _onProgress = onProgress;
            _bytesRead = initialOffset;
        }

        /// <inheritdoc/>
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            _bytesRead += read;
            await ReportProgress();
            return read;
        }

        /// <inheritdoc/>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, cancellationToken);
            _bytesRead += read;
            await ReportProgress();
            return read;
        }

        /// <summary> Fires <see cref="_onProgress"/> when the completion percentage changes, rate-limited to once per second. </summary>
        private async Task ReportProgress()
        {
            if (_totalLength <= 0) return;
            int percent = (int)(_bytesRead * 100 / _totalLength);
            var now = DateTime.UtcNow;
            if (percent != _lastPercent && (now - _lastReport).TotalSeconds >= 1)
            {
                _lastPercent = percent;
                _lastReport = now;
                await _onProgress(percent);
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _totalLength;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
