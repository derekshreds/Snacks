namespace Snacks.Models;

/// <summary>
///     Represents a node in the distributed encoding cluster.
///     Tracks identity, status, capabilities, and current workload.
///     Nodes can be discovered via UDP broadcast or configured manually.
/// </summary>
public sealed class ClusterNode
{
    /// <summary> Unique identifier for this node (GUID format). </summary>
    public string NodeId { get; set; } = Guid.NewGuid().ToString();

    /// <summary> Display name for this node (usually the machine name). </summary>
    public string Hostname { get; set; } = "";

    /// <summary> IP address for HTTP communication with this node. </summary>
    public string IpAddress { get; set; } = "";

    /// <summary> HTTP port this node is listening on (default: 6767). </summary>
    public int Port { get; set; } = 6767;

    /// <summary> Role of this node: "master" or "node". </summary>
    public string Role { get; set; } = "standalone";

    /// <summary> Current operational status of this node. </summary>
    public NodeStatus Status { get; set; } = NodeStatus.Online;

    /// <summary> Software version running on this node. </summary>
    public string Version { get; set; } = "";

    /// <summary> UTC timestamp of the last successful heartbeat. </summary>
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;

    /// <summary> UTC timestamp when this node first joined the cluster. </summary>
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Hardware and software capabilities of this node.
    ///     Used by the master for intelligent job dispatch.
    /// </summary>
    public WorkerCapabilities? Capabilities { get; set; }

    /// <summary>
    ///     ID of the first occupied slot's job. Convenience accessor kept in
    ///     sync with the head of <see cref="ActiveJobs"/> for UI bindings
    ///     that want a single value; the authoritative per-slot list is
    ///     <see cref="ActiveJobs"/>.
    /// </summary>
    public string? ActiveWorkItemId { get; set; }

    /// <summary> Display name of the file in the first occupied slot. </summary>
    public string? ActiveFileName { get; set; }

    /// <summary> Encoding progress percentage (0–100) for the first active job. </summary>
    public int ActiveProgress { get; set; }

    /// <summary>
    ///     All in-flight remote jobs on this node, one entry per occupied
    ///     slot. Maintained by the master from heartbeat reports plus
    ///     optimistic book-keeping on dispatch.
    /// </summary>
    public List<ActiveJobInfo> ActiveJobs { get; set; } = new();

    /// <summary> Total number of jobs successfully completed by this node. </summary>
    public int CompletedJobs { get; set; }

    /// <summary> Total number of jobs that failed on this node. </summary>
    public int FailedJobs { get; set; }

    /// <summary> Whether this node is paused and not accepting new jobs. </summary>
    public bool IsPaused { get; set; }
}

/// <summary>
///     Describes the hardware and software capabilities of a cluster node.
///     Used by the master for intelligent job dispatch — matching jobs to
///     nodes with the right GPU, encoders, and disk space.
/// </summary>
public sealed class WorkerCapabilities
{
    /// <summary> Detected GPU vendor: "nvidia", "intel", "amd", or "none". </summary>
    public string? GpuVendor { get; set; }

    /// <summary>
    ///     List of FFmpeg encoder names supported by this node.
    ///     Includes both software (libx265, libx264, libsvtav1)
    ///     and hardware encoders (hevc_nvenc, hevc_vaapi, etc.).
    /// </summary>
    public List<string> SupportedEncoders { get; set; } = new();

    /// <summary> Operating system platform: "Windows", "macOS", or "Linux". </summary>
    public string OsPlatform { get; set; } = "";

    /// <summary>
    ///     Available disk space in bytes on the partition used for temp files.
    ///     Used to reject jobs that would exceed available space.
    /// </summary>
    public long AvailableDiskSpaceBytes { get; set; }

    /// <summary>
    ///     Whether this node can accept new jobs.
    ///     False when busy, paused, or processing a remote job.
    /// </summary>
    public bool CanAcceptJobs { get; set; } = true;

    /// <summary>
    ///     Hardware devices this node can drive concurrently. One entry per
    ///     vendor family the worker detected. Each device contributes its
    ///     <see cref="HardwareDevice.DefaultConcurrency"/> slots to the master's
    ///     dispatch pool, optionally overridden by
    ///     <see cref="NodeSettings.DeviceSettings"/>.
    /// </summary>
    public List<HardwareDevice> Devices { get; set; } = new();
}

/// <summary> Operational status of a cluster node. </summary>
public enum NodeStatus
{
    /// <summary> Node is online and available for work. </summary>
    Online,

    /// <summary> Node is actively processing a job. </summary>
    Busy,

    /// <summary> Master is uploading a job to this node. </summary>
    Uploading,

    /// <summary> Master is downloading a completed job from this node. </summary>
    Downloading,

    /// <summary> Node has been gracefully taken offline. </summary>
    Offline,

    /// <summary>
    ///     Node has not responded to heartbeats within the timeout period.
    ///     Will be removed from the cluster after 5 minutes.
    /// </summary>
    Unreachable,

    /// <summary> Node is paused by user request and not accepting new jobs. </summary>
    Paused
}
