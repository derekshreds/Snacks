namespace Snacks.Models
{
    public class ClusterNode
    {
        public string NodeId { get; set; } = Guid.NewGuid().ToString();
        public string Hostname { get; set; } = "";
        public string IpAddress { get; set; } = "";
        public int Port { get; set; } = 6767;
        public string Role { get; set; } = "standalone";
        public NodeStatus Status { get; set; } = NodeStatus.Online;
        public string Version { get; set; } = "";
        public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
        public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;
        public WorkerCapabilities? Capabilities { get; set; }
        public string? ActiveWorkItemId { get; set; }
        public string? ActiveFileName { get; set; }
        public int ActiveProgress { get; set; }
        public int CompletedJobs { get; set; }
        public int FailedJobs { get; set; }
        public bool IsPaused { get; set; }
    }

    public class WorkerCapabilities
    {
        public string? GpuVendor { get; set; }
        public List<string> SupportedEncoders { get; set; } = new();
        public string OsPlatform { get; set; } = "";
        public long AvailableDiskSpaceBytes { get; set; }
        public bool CanAcceptJobs { get; set; } = true;
    }

    public enum NodeStatus
    {
        Online,
        Busy,
        Offline,
        Unreachable,
        Paused
    }
}
