namespace Snacks.Models
{
    public class WorkItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string FileName { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; } = 0;
        public long Bitrate { get; set; } = 0;
        public double Length { get; set; } = 0;
        public bool IsHevc { get; set; } = false;
        public ProbeResult? Probe { get; set; }
        public WorkItemStatus Status { get; set; } = WorkItemStatus.Pending;
        public int Progress { get; set; } = 0;
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }

    public enum WorkItemStatus
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cancelled
    }
}