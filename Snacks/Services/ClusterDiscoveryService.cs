namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
///     Handles UDP discovery, handshake negotiation, and node registration for
///     the distributed encoding cluster. Created and owned by
///     <see cref="ClusterService"/>, which delegates all discovery-related
///     responsibilities here.
///
///     <para><b>Responsibilities:</b></para>
///     <list type="bullet">
///       <item><description>UDP broadcast announcements every 15 seconds</description></item>
///       <item><description>Listening for peer announcements and initiating handshakes</description></item>
///       <item><description>Manual node connection and master registration</description></item>
///       <item><description>Node identity construction and capability reporting</description></item>
///       <item><description>Shared-secret hashing and version compatibility checks</description></item>
///     </list>
/// </summary>
public sealed class ClusterDiscoveryService
{
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly IHttpClientFactory         _httpClientFactory;
    private readonly TranscodingService         _transcodingService;
    private readonly ConcurrentDictionary<string, ClusterNode> _nodes;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented               = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase
    };

    /// <summary> Protocol version for cluster inter-node communication. </summary>
    internal const string ClusterVersion = "2.5.1";

    private volatile ClusterConfig       _config;
    private UdpClient?                   _udpListener;
    private CancellationTokenSource?     _cts;
    private CancellationTokenSource?     _linkedCts;
    private Task?                        _discoveryTask;
    private Task?                        _manualNodesTask;
    private Task?                        _registerTask;
    private volatile string?             _detectedGpuVendor;
    private volatile List<string>?       _supportedEncoders;
    private volatile int                 _resolvedPort;

    /******************************************************************
     *  Properties
     ******************************************************************/

    /// <summary> The shared node registry, passed in from <see cref="ClusterService"/>. </summary>
    internal ConcurrentDictionary<string, ClusterNode> Nodes => _nodes;

    /// <summary> The current cluster configuration. Settable so <see cref="ClusterService"/> can push config changes. </summary>
    internal ClusterConfig Config
    {
        get => _config;
        set => _config = value;
    }

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Initializes the discovery service with shared dependencies from <see cref="ClusterService"/>.
    ///     The node dictionary is shared by reference so both services see the same state.
    /// </summary>
    /// <param name="config">Initial cluster configuration (owned by ClusterService).</param>
    /// <param name="hubContext">SignalR hub for broadcasting node events to connected clients.</param>
    /// <param name="httpClientFactory">Factory for creating HTTP clients used in handshakes.</param>
    /// <param name="transcodingService">Provides hardware detection and active work item state.</param>
    /// <param name="nodes">Shared node dictionary, owned and passed in by ClusterService.</param>
    public ClusterDiscoveryService(
        ClusterConfig config,
        IHubContext<TranscodingHub> hubContext,
        IHttpClientFactory httpClientFactory,
        TranscodingService transcodingService,
        ConcurrentDictionary<string, ClusterNode> nodes)
    {
        _config             = config;
        _hubContext         = hubContext;
        _httpClientFactory  = httpClientFactory;
        _transcodingService = transcodingService;
        _nodes              = nodes;
    }

    /******************************************************************
     *  Lifecycle
     ******************************************************************/

    /// <summary>
    ///     Starts UDP discovery, manual node connections, and master registration
    ///     based on the current configuration.
    /// </summary>
    /// <param name="ct">Cancelled when the owning <see cref="ClusterService"/> is stopping.</param>
    public void Start(CancellationToken ct)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _linkedCts?.Dispose();
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

        bool needsDiscovery = _config.AutoDiscovery ||
            (_config.Role == "node" && string.IsNullOrEmpty(_config.MasterUrl));

        if (needsDiscovery)
            _discoveryTask = Task.Run(() => RunDiscoveryAsync(_linkedCts.Token));

        if (_config.ManualNodes.Count > 0)
            _manualNodesTask = Task.Run(() => ConnectToManualNodesAsync(_linkedCts.Token));

        if (_config.Role == "node" && !string.IsNullOrEmpty(_config.MasterUrl))
            _registerTask = Task.Run(() => RegisterWithMasterAsync(_linkedCts.Token));

        var localIp = GetLocalIpAddress();
        var port    = GetListeningPort();
        Console.WriteLine($"ClusterDiscovery: Started — localIp={localIp}, port={port}, udp={needsDiscovery}");
    }

    /// <summary> Stops all discovery operations, closes the UDP socket, and awaits pending tasks. </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();

        if (_discoveryTask != null)
        {
            try { await _discoveryTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _discoveryTask = null;
        }

        if (_manualNodesTask != null)
        {
            try { await _manualNodesTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _manualNodesTask = null;
        }

        if (_registerTask != null)
        {
            try { await _registerTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { }
            _registerTask = null;
        }

        _udpListener?.Close();
        _udpListener?.Dispose();
        _udpListener = null;

        _cts?.Dispose();
        _cts = null;
        _linkedCts?.Dispose();
        _linkedCts = null;

        Console.WriteLine("ClusterDiscovery: Stopped");
    }

    /******************************************************************
     *  UDP Discovery
     ******************************************************************/

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

            Console.WriteLine("ClusterDiscovery: UDP listening on port 6768");

            var broadcastTask = BroadcastLoopAsync(ct);
            var listenTask    = ListenLoopAsync(ct);
            await Task.WhenAll(broadcastTask, listenTask);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"ClusterDiscovery: Discovery error: {ex.Message}");
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
                    proto      = "snacks-v1",
                    nodeId     = _config.NodeId,
                    role       = _config.Role,
                    port       = GetListeningPort(),
                    version    = ClusterVersion,
                    secretHash = HashSecret(_config.SharedSecret)
                };

                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, _jsonOptions));

                var broadcastAddresses = GetBroadcastAddresses();
                foreach (var addr in broadcastAddresses)
                {
                    try
                    {
                        await broadcastClient.SendAsync(bytes, bytes.Length, new IPEndPoint(addr, 6768));
                    }
                    catch { }
                }

                try
                {
                    await broadcastClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, 6768));
                }
                catch { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"ClusterDiscovery: Broadcast error: {ex.Message}");
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
        Console.WriteLine("ClusterDiscovery: Listening for UDP announcements on port 6768");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpListener!.ReceiveAsync(ct);
                var json   = Encoding.UTF8.GetString(result.Buffer);

                JsonElement announcement;
                try
                {
                    announcement = JsonSerializer.Deserialize<JsonElement>(json);
                }
                catch
                {
                    continue;
                }

                if (!announcement.TryGetProperty("proto", out var proto) || proto.GetString() != "snacks-v1")
                    continue;

                var nodeId = announcement.GetProperty("nodeId").GetString();
                if (nodeId == _config.NodeId)
                    continue;

                var secretHash = announcement.GetProperty("secretHash").GetString();
                if (secretHash != HashSecret(_config.SharedSecret))
                {
                    Console.WriteLine($"ClusterDiscovery: Ignored announcement from {result.RemoteEndPoint} — secret mismatch");
                    continue;
                }

                var port          = announcement.GetProperty("port").GetInt32();
                var role          = announcement.TryGetProperty("role", out var roleProp) ? roleProp.GetString() : "unknown";
                var remoteVersion = announcement.TryGetProperty("version", out var versionProp) ? versionProp.GetString() : null;
                var senderIp      = result.RemoteEndPoint.Address.ToString();

                if (!IsVersionCompatible(remoteVersion))
                {
                    Console.WriteLine($"ClusterDiscovery: Ignored announcement from {senderIp} — incompatible version {remoteVersion ?? "unknown"} (ours: {ClusterVersion})");
                    continue;
                }

                if (_nodes.ContainsKey(nodeId!))
                    continue;

                if (port < 1 || port > 65535)
                    continue;

                // Reject second master via discovery
                if (role == "master" && (_config.Role == "master" || _nodes.Values.Any(n => n.Role == "master")))
                {
                    Console.WriteLine($"ClusterDiscovery: Ignoring master announcement from {senderIp}:{port} — a master already exists in the cluster");
                    continue;
                }

                Console.WriteLine($"ClusterDiscovery: Discovered {role} at {senderIp}:{port} — performing handshake");
                var scheme = _config.UseHttps ? "https" : "http";
                _ = Task.Run(() => PerformHandshakeAsync($"{scheme}://{senderIp}:{port}", ct));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"ClusterDiscovery: Listen error: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Gets the directed broadcast address for each active network interface.
    ///     For example, 192.168.1.0/24 yields 192.168.1.255.
    /// </summary>
    private static List<IPAddress> GetBroadcastAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in iface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    var ipBytes        = unicast.Address.GetAddressBytes();
                    var maskBytes      = unicast.IPv4Mask.GetAddressBytes();
                    var broadcastBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                        broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);

                    addresses.Add(new IPAddress(broadcastBytes));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ClusterDiscovery: Error enumerating network interfaces: {ex.Message}");
        }
        return addresses;
    }

    /******************************************************************
     *  Manual Nodes and Master Registration
     ******************************************************************/

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
                Console.WriteLine($"ClusterDiscovery: Manual node connect to {node.Name} failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Keeps re-handshaking with the configured master on a loop.
    ///     Retries every 10 seconds while unregistered, then slows to every
    ///     30 seconds to re-announce in case the master restarted and lost
    ///     its in-memory node list.
    /// </summary>
    /// <param name="ct">Cancelled when the cluster is stopping.</param>
    private async Task RegisterWithMasterAsync(CancellationToken ct)
    {
        bool registered = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PerformHandshakeAsync(_config.MasterUrl!, ct);
                if (!registered)
                {
                    registered = true;
                    Console.WriteLine("ClusterDiscovery: Registered with master — switching to periodic re-handshake");
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (Exception ex)
            {
                if (registered)
                {
                    Console.WriteLine($"ClusterDiscovery: Lost contact with master — {ex.Message}");
                    registered = false;
                }
                else
                {
                    Console.WriteLine($"ClusterDiscovery: Failed to register with master: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }
    }

    /******************************************************************
     *  Handshake
     ******************************************************************/

    /// <summary>
    ///     Posts this node's info to the remote node's handshake endpoint and registers
    ///     the remote node's response locally.
    /// </summary>
    /// <param name="baseUrl">Base URL of the remote node (e.g. <c>http://192.168.1.5:6767</c>).</param>
    /// <param name="ct">Cancelled when the cluster is stopping.</param>
    public async Task PerformHandshakeAsync(string baseUrl, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/api/cluster/handshake";
        try
        {
            var client   = CreateAuthenticatedClient();
            var selfNode = BuildSelfNode();

            var content = new StringContent(
                JsonSerializer.Serialize(selfNode, _jsonOptions),
                Encoding.UTF8, "application/json");

            var response = await client.PostAsync(url, content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"ClusterDiscovery: Handshake rejected by {baseUrl} — {body}");
                Console.WriteLine("ClusterDiscovery: Another master exists in the cluster. Reconfigure this node as 'node' or 'standalone'.");
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"ClusterDiscovery: Handshake with {baseUrl} failed — {response.StatusCode}: {body}");
                return;
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var remoteNode   = JsonSerializer.Deserialize<ClusterNode>(responseBody, _jsonOptions);

            if (remoteNode != null)
            {
                // The peer's self-reported IP may be unreachable (e.g. an internal
                // container IP from Docker Desktop on Windows). The URL we just used
                // to reach them works by definition — trust it over the self-report.
                try
                {
                    var uri = new Uri(baseUrl);
                    if (!string.IsNullOrEmpty(uri.Host))
                    {
                        if (!string.Equals(uri.Host, remoteNode.IpAddress, StringComparison.OrdinalIgnoreCase))
                        {
                            Console.WriteLine(
                                $"ClusterDiscovery: Overriding self-reported IP {remoteNode.IpAddress} " +
                                $"with {uri.Host} from handshake URL");
                            remoteNode.IpAddress = uri.Host;
                        }
                        remoteNode.Port = uri.Port;
                    }
                }
                catch { }

                RegisterOrUpdateNode(remoteNode, fromHandshake: true);
                Console.WriteLine($"ClusterDiscovery: Handshake with {remoteNode.Hostname} ({baseUrl}) successful");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ClusterDiscovery: Handshake with {baseUrl} failed — {ex.Message}");
        }
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
    public (bool Accepted, string? RejectReason) RegisterOrUpdateNode(ClusterNode node, bool fromHandshake = false)
    {
        // Reject a second master joining the cluster
        if (node.Role == "master")
        {
            if (_config.Role == "master")
            {
                Console.WriteLine($"ClusterDiscovery: Rejecting master {node.Hostname} ({node.IpAddress}:{node.Port}) — this node is already a master");
                return (false, "A master already exists in the cluster");
            }

            var existingMaster = _nodes.Values.FirstOrDefault(n => n.Role == "master");
            if (existingMaster != null && existingMaster.NodeId != node.NodeId)
            {
                Console.WriteLine($"ClusterDiscovery: Rejecting master {node.Hostname} — master {existingMaster.Hostname} already in cluster");
                return (false, $"A master already exists in the cluster: {existingMaster.Hostname}");
            }
        }

        var isNew = !_nodes.TryGetValue(node.NodeId, out var existingNode);
        if (isNew || fromHandshake)
            node.LastHeartbeat = DateTime.UtcNow;
        else
            node.LastHeartbeat = existingNode!.LastHeartbeat;

        _nodes[node.NodeId] = node;

        if (isNew)
        {
            Console.WriteLine($"ClusterDiscovery: Node joined — {node.Hostname} ({node.IpAddress}:{node.Port}) [{node.Role}]");
            _ = _hubContext.Clients.All.SendAsync("WorkerConnected", node);
        }
        else
        {
            _ = _hubContext.Clients.All.SendAsync("WorkerUpdated", node);
        }

        return (true, null);
    }

    /******************************************************************
     *  Self-Description
     ******************************************************************/

    /// <summary>
    ///     Builds a <see cref="ClusterNode"/> representing this instance.
    ///     Used during handshake and heartbeat responses.
    /// </summary>
    public ClusterNode BuildSelfNode()
    {
        return new ClusterNode
        {
            NodeId        = _config.NodeId,
            Hostname      = _config.NodeName,
            IpAddress     = GetLocalIpAddress(),
            Port          = GetListeningPort(),
            Role          = _config.Role,
            Status        = _transcodingService.GetActiveWorkItem() != null ? NodeStatus.Busy : NodeStatus.Online,
            Version       = ClusterVersion,
            LastHeartbeat = DateTime.UtcNow,
            Capabilities  = GetCapabilities()
        };
    }

    /// <summary>
    ///     Returns the hardware and software capabilities of this node.
    ///     Re-checks hardware detection each time since eager detection
    ///     may not have finished on first call.
    /// </summary>
    public WorkerCapabilities GetCapabilities()
    {
        var hw = _transcodingService.GetDetectedHardware();
        if (hw != null && hw != _detectedGpuVendor)
        {
            _detectedGpuVendor = hw;
            _supportedEncoders = null;
        }

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");

        var diskSpace = GetAvailableDiskSpace(workDir);

        return new WorkerCapabilities
        {
            GpuVendor              = _detectedGpuVendor ?? "none",
            SupportedEncoders      = _supportedEncoders ?? BuildSupportedEncodersList(),
            OsPlatform             = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
                                   : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? "macOS"
                                   : "Linux",
            AvailableDiskSpaceBytes = diskSpace,
            CanAcceptJobs          = _transcodingService.GetActiveWorkItem() == null
        };
    }

    /// <summary> Builds and caches the list of encoder identifiers supported by this node's hardware. </summary>
    private List<string> BuildSupportedEncodersList()
    {
        var encoders = new List<string> { "libx265", "libx264", "libsvtav1" };
        var hw = _detectedGpuVendor;

        if (hw == "nvidia")
        {
            encoders.AddRange(new[] { "hevc_nvenc", "h264_nvenc" });
        }
        else if (hw == "intel")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                encoders.AddRange(new[] { "hevc_qsv", "h264_qsv" });
            else
                encoders.AddRange(new[] { "hevc_vaapi", "h264_vaapi" });
        }
        else if (hw == "amd")
        {
            encoders.AddRange(new[] { "hevc_amf", "h264_amf" });
        }
        else if (hw == "apple")
        {
            encoders.AddRange(new[] { "hevc_videotoolbox", "h264_videotoolbox" });
        }

        _supportedEncoders = encoders;
        return encoders;
    }

    /******************************************************************
     *  Authentication
     ******************************************************************/

    /// <summary> Creates an <see cref="HttpClient"/> with the shared secret set in the authentication header. </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(30);
        client.DefaultRequestHeaders.Add("X-Snacks-Secret", ClusterAuthFilter.EncodeSecretForHeader(_config.SharedSecret));
        return client;
    }

    /******************************************************************
     *  Utilities
     ******************************************************************/

    /// <summary> Returns the lowercase hex SHA-256 hash of the shared secret for safe logging and comparison. </summary>
    public static string HashSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hash).ToLower();
    }

    /// <summary>
    ///     Checks whether a remote node's version is compatible with ours.
    ///     Compatible means the same major version — breaking protocol changes bump major.
    /// </summary>
    /// <param name="remoteVersion">The version string reported by the remote node, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the major versions match.</returns>
    public static bool IsVersionCompatible(string? remoteVersion)
    {
        if (string.IsNullOrEmpty(remoteVersion)) return false;
        var localMajor  = ClusterVersion.Split('.')[0];
        var remoteMajor = remoteVersion.Split('.')[0];
        return localMajor == remoteMajor;
    }

    /// <summary>
    ///     Resolves the machine's primary outbound IPv4 address by opening a UDP socket toward a
    ///     public DNS server. Falls back to <c>127.0.0.1</c> if the network is unavailable.
    /// </summary>
    public static string GetLocalIpAddress()
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

    /// <summary>
    ///     Returns the HTTP port this instance is listening on.
    ///     Checks <c>ASPNETCORE_URLS</c> first, then falls back to <c>appsettings.json</c> Kestrel config,
    ///     then defaults to 6767.
    /// </summary>
    public int GetListeningPort()
    {
        if (_resolvedPort > 0) return _resolvedPort;

        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        if (!string.IsNullOrEmpty(urls))
        {
            try
            {
                var raw = urls.Split(';')[0].Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0");
                var uri = new Uri(raw);
                _resolvedPort = uri.Port;
                return _resolvedPort;
            }
            catch { }
        }

        try
        {
            var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(appSettingsPath))
            {
                var json = File.ReadAllText(appSettingsPath);
                using var doc = JsonDocument.Parse(json);
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

    /// <summary> Returns free disk space in bytes on the drive containing the working directory, or 0 on error. </summary>
    /// <param name="workDir">The working directory whose drive is inspected.</param>
    private long GetAvailableDiskSpace(string workDir)
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(_config.NodeTempDirectory) ? workDir : _config.NodeTempDirectory;
            Directory.CreateDirectory(dir);
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root)) return 0;
            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }
}
