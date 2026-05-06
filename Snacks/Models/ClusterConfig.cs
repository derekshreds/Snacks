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
    ///     Seconds without a chunk arriving before the worker clears its receivingJobId.
    ///     Kept short (and independent of <see cref="NodeTimeoutSeconds"/>) so the worker
    ///     self-clears stale receive state between dispatch ticks if the master's cleanup
    ///     DELETE was lost. Default: 8 seconds.
    /// </summary>
    public int StaleReceiveTimeoutSeconds { get; set; } = 8;

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

    /// <summary>
    ///     Whether inter-node communication should use HTTPS.
    ///     When <see langword="false" />, the shared secret is transmitted in plaintext
    ///     over HTTP and a security warning is logged on startup.
    /// </summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>
    ///     When true, master and node skip uploading/downloading the source and output
    ///     and instead read/write directly from a path both sides can see (e.g. an NFS
    ///     or SMB share). Symmetric flag — both sides must enable it. The master only
    ///     offers shared mode when its own flag is on; the node only honors a shared
    ///     dispatch when its own flag is on. If either side is off (or the node rejects
    ///     the path) the regular upload/download flow runs, so this is safe to flip on
    ///     without coordination.
    /// </summary>
    public bool SharedStorageEnabled { get; set; } = false;

    /// <summary>
    ///     Node-side allowlist of base directories the master may ask this node to read
    ///     source files from in shared mode. Empty list rejects all shared-mode dispatches
    ///     (fail closed). The check uses canonicalized prefix matching after symlink
    ///     resolution to defend against traversal and symlink-escape.
    ///     <para>Master ignores this field — it only applies on the receiving side.</para>
    /// </summary>
    public List<string> SharedStorageInputPaths { get; set; } = new();

    /// <summary>
    ///     Node-side allowlist of base directories the master may ask this node to write
    ///     final output into in shared mode. Separated from
    ///     <see cref="SharedStorageInputPaths"/> so a read-only NAS export can serve as
    ///     the source while output goes to a different writable location. Empty list
    ///     rejects all shared-mode dispatches.
    /// </summary>
    public List<string> SharedStorageOutputPaths { get; set; } = new();

    /// <summary>
    ///     Optional path translation applied on the node before
    ///     <see cref="SharedStorageInputPaths"/> / <see cref="SharedStorageOutputPaths"/>
    ///     allowlist checks. Lets the master see the share at one mount point while this
    ///     node sees it at another (e.g. master mounts <c>/shared/movies</c>, node mounts
    ///     <c>/mnt/nas/movies</c>). Single from/to pair — set both or neither.
    /// </summary>
    public string? SharedStoragePathRewriteFrom { get; set; }

    /// <summary> Companion to <see cref="SharedStoragePathRewriteFrom"/>. </summary>
    public string? SharedStoragePathRewriteTo { get; set; }
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
