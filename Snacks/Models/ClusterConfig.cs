namespace Snacks.Models;

/// <summary>
///     Configuration for the distributed encoding cluster.
///     Persisted to disk as cluster.json and loaded on startup.
///     Controls whether this instance acts as a master coordinator,
///     a worker node, or runs standalone without clustering.
/// </summary>
public sealed class ClusterConfig
{
    /// <summary> Whether clustering is enabled. When false, all encoding is done locally. </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    ///     Role of this instance: "standalone", "master", or "node".
    ///     <list type="bullet">
    ///       <item><description>standalone: No clustering, all encoding is local</description></item>
    ///       <item><description>master: Coordinates jobs, dispatches to worker nodes</description></item>
    ///       <item><description>node: Receives and processes jobs from a master</description></item>
    ///     </list>
    /// </summary>
    public string Role { get; set; } = "standalone";

    /// <summary>
    ///     Display name for this node in the cluster UI.
    ///     Defaults to the machine name.
    /// </summary>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>
    ///     Shared secret used for inter-node authentication.
    ///     All nodes in the cluster must have the same secret.
    ///     Transmitted as a SHA256 hash in discovery, plaintext in HTTP headers.
    /// </summary>
    public string SharedSecret { get; set; } = "";

    /// <summary>
    ///     Whether UDP broadcast discovery is enabled.
    ///     When true, nodes announce themselves on the local network
    ///     via UDP broadcast on port 6768 every 15 seconds.
    /// </summary>
    public bool AutoDiscovery { get; set; } = true;

    /// <summary>
    ///     Manually configured nodes — used when discovery is disabled
    ///     or nodes are on different subnets.
    /// </summary>
    public List<ManualNodeEntry> ManualNodes { get; set; } = new();

    /// <summary> Interval in seconds between heartbeat checks. Default: 10 seconds. </summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>
    ///     Seconds without a successful heartbeat before a node is marked unreachable.
    ///     Default: 30 seconds.
    /// </summary>
    public int NodeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    ///     Whether the master should also encode files locally.
    ///     When false, the master only dispatches jobs to worker nodes.
    /// </summary>
    public bool LocalEncodingEnabled { get; set; } = true;

    /// <summary>
    ///     Custom directory for storing temp files on worker nodes.
    ///     If null, defaults to %LOCALAPPDATA%/Snacks/work/remote-jobs.
    /// </summary>
    public string? NodeTempDirectory { get; set; }

    /// <summary>
    ///     URL of the master node (for worker nodes).
    ///     Example: "http://192.168.1.100:6767"
    /// </summary>
    public string? MasterUrl { get; set; }

    /// <summary>
    ///     Unique identifier for this node (GUID format).
    ///     Used to distinguish nodes during discovery and heartbeat.
    /// </summary>
    public string NodeId { get; set; } = Guid.NewGuid().ToString();
}

/// <summary>
///     Entry for a manually configured cluster node.
///     Used when UDP discovery is unavailable or nodes are on different subnets.
/// </summary>
public sealed class ManualNodeEntry
{
    /// <summary> Display name for this node in the UI. </summary>
    public string Name { get; set; } = "";

    /// <summary> Full HTTP URL of the node (e.g., "http://192.168.1.50:6767"). </summary>
    public string Url { get; set; } = "";
}
