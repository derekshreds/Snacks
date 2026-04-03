namespace Snacks.Models
{
    public class MediaFile
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = "";
        public string Directory { get; set; } = "";
        public string FileName { get; set; } = "";
        public string BaseName { get; set; } = "";
        public long FileSize { get; set; }
        public long Bitrate { get; set; }
        public string Codec { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string? PixelFormat { get; set; }
        public double Duration { get; set; }
        public bool IsHevc { get; set; }
        public bool Is4K { get; set; }
        public MediaFileStatus Status { get; set; } = MediaFileStatus.Unseen;
        public int FailureCount { get; set; }
        public string? FailureReason { get; set; }
        public DateTime? LastScannedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public long FileMtime { get; set; }

        // Cluster remote job tracking
        public string? RemoteWorkItemId { get; set; } // The WorkItem.Id GUID used for this remote job
        public string? AssignedNodeId { get; set; }
        public string? AssignedNodeName { get; set; }
        public string? RemoteJobPhase { get; set; }
        public int RemoteFailureCount { get; set; }
        public string? AssignedNodeIp { get; set; }
        public int? AssignedNodePort { get; set; }
    }

    public enum MediaFileStatus
    {
        Unseen,
        Queued,
        Processing,
        Completed,
        Failed,
        Skipped,
        Cancelled
    }
}
