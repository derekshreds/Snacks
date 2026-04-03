namespace Snacks.Models
{
    public class ClusterConfig
    {
        public bool Enabled { get; set; } = false;
        public string Role { get; set; } = "standalone"; // standalone, master, node
        public string NodeName { get; set; } = Environment.MachineName;
        public string SharedSecret { get; set; } = "";
        public bool AutoDiscovery { get; set; } = true;
        public List<ManualNodeEntry> ManualNodes { get; set; } = new();
        public int HeartbeatIntervalSeconds { get; set; } = 10;
        public int NodeTimeoutSeconds { get; set; } = 30;
        public bool LocalEncodingEnabled { get; set; } = true;
        public string? NodeTempDirectory { get; set; }
        public string? MasterUrl { get; set; }
        public string NodeId { get; set; } = Guid.NewGuid().ToString();
    }

    public class ManualNodeEntry
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
