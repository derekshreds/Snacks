using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data
{
    public class MediaFileRepository
    {
        private readonly IDbContextFactory<SnacksDbContext> _contextFactory;

        public MediaFileRepository(IDbContextFactory<SnacksDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        public async Task InitializeAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await context.Database.MigrateAsync();

            // Enable WAL mode and relaxed sync for crash resilience + performance
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
        }

        public async Task<MediaFile?> GetByPathAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
        }

        public async Task<MediaFile?> GetByBaseNameInDirectoryAsync(string directory, string baseName)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .FirstOrDefaultAsync(f => f.Directory == directory && f.BaseName == baseName);
        }

        public async Task UpsertAsync(MediaFile file)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var existing = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == file.FilePath);

            if (existing != null)
            {
                existing.FileSize = file.FileSize;
                existing.Bitrate = file.Bitrate;
                existing.Codec = file.Codec;
                existing.Width = file.Width;
                existing.Height = file.Height;
                existing.PixelFormat = file.PixelFormat;
                existing.Duration = file.Duration;
                existing.IsHevc = file.IsHevc;
                existing.Is4K = file.Is4K;
                existing.Status = file.Status;
                existing.LastScannedAt = file.LastScannedAt;
                existing.FileMtime = file.FileMtime;
                // Don't overwrite failure info on re-scan
            }
            else
            {
                context.MediaFiles.Add(file);
            }

            await context.SaveChangesAsync();
        }

        public async Task SetStatusAsync(string normalizedPath, MediaFileStatus status, string? failureReason = null)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);

            if (file != null)
            {
                file.Status = status;
                if (failureReason != null)
                    file.FailureReason = failureReason;
                if (status == MediaFileStatus.Completed)
                    file.CompletedAt = DateTime.UtcNow;
                await context.SaveChangesAsync();
            }
        }

        public async Task IncrementFailureCountAsync(string normalizedPath, string reason)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);

            if (file != null)
            {
                file.Status = MediaFileStatus.Failed;
                file.FailureCount++;
                file.FailureReason = reason.Length > 2048 ? reason[..2048] : reason;
                await context.SaveChangesAsync();
            }
        }

        public async Task<List<MediaFile>> GetFilesWithStatusAsync(MediaFileStatus status)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == status)
                .ToListAsync();
        }

        /// <summary>
        /// Returns true if the file (by path or base name in same directory) has a terminal status.
        /// </summary>
        public async Task<bool> IsFileKnownAsync(string normalizedPath, string directory, string baseName)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.AnyAsync(f =>
                (f.FilePath == normalizedPath ||
                 (f.Directory == directory && f.BaseName == baseName)) &&
                f.Status != MediaFileStatus.Unseen);
        }

        /// <summary>
        /// Loads all known file paths with status, size, and mtime for the given directories
        /// into memory for fast batch lookups during scanning.
        /// </summary>
        public async Task<Dictionary<string, (MediaFileStatus Status, long FileSize, double Duration)>> GetFileInfoBatchAsync(IEnumerable<string> directories)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var dirList = directories.ToList();
            return await context.MediaFiles
                .Where(f => dirList.Contains(f.Directory))
                .ToDictionaryAsync(
                    f => f.FilePath,
                    f => (f.Status, f.FileSize, f.Duration));
        }

        /// <summary>
        /// Loads all known base names (directory + base name → status) for batch lookups.
        /// </summary>
        public async Task<Dictionary<string, MediaFileStatus>> GetBaseNameStatusBatchAsync(IEnumerable<string> directories)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var dirList = directories.ToList();
            return await context.MediaFiles
                .Where(f => dirList.Contains(f.Directory))
                .ToDictionaryAsync(
                    f => $"{f.Directory}|{f.BaseName}".ToLowerInvariant(),
                    f => f.Status);
        }

        public async Task ResetAllStatusesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await context.MediaFiles.ExecuteUpdateAsync(s =>
                s.SetProperty(f => f.Status, MediaFileStatus.Unseen)
                 .SetProperty(f => f.FailureCount, 0)
                 .SetProperty(f => f.FailureReason, (string?)null)
                 .SetProperty(f => f.CompletedAt, (DateTime?)null));
        }

        public async Task ResetFileAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);

            if (file != null)
            {
                file.Status = MediaFileStatus.Unseen;
                file.FailureCount = 0;
                file.FailureReason = null;
                await context.SaveChangesAsync();
            }
        }

        public async Task BulkInsertSeenFilesAsync(IEnumerable<string> paths)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var existingPaths = (await context.MediaFiles
                .Select(f => f.FilePath)
                .ToListAsync())
                .ToHashSet();

            var newFiles = paths
                .Where(p => !existingPaths.Contains(p))
                .Select(p => new MediaFile
                {
                    FilePath = p,
                    Directory = Path.GetDirectoryName(p) ?? "",
                    FileName = Path.GetFileName(p),
                    BaseName = Path.GetFileNameWithoutExtension(p),
                    Status = MediaFileStatus.Completed,
                    CreatedAt = DateTime.UtcNow,
                    LastScannedAt = DateTime.UtcNow
                })
                .ToList();

            // Batch insert in chunks to avoid huge transactions
            foreach (var chunk in newFiles.Chunk(500))
            {
                context.MediaFiles.AddRange(chunk);
                await context.SaveChangesAsync();
            }
        }

        public async Task PruneDeletedFilesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var batchSize = 1000;
            int pruned;

            do
            {
                var batch = await context.MediaFiles
                    .Take(batchSize)
                    .ToListAsync();

                if (batch.Count == 0) break;

                var toRemove = batch.Where(f => !File.Exists(f.FilePath)).ToList();
                pruned = toRemove.Count;

                if (pruned > 0)
                {
                    context.MediaFiles.RemoveRange(toRemove);
                    await context.SaveChangesAsync();
                }
            }
            while (pruned > 0);
        }

        public async Task<List<MediaFile>> GetFailedFilesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Failed)
                .OrderByDescending(f => f.FailureCount)
                .ToListAsync();
        }

        public async Task<int> GetCountByStatusAsync(MediaFileStatus status)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.CountAsync(f => f.Status == status);
        }

        // --- Cluster remote job tracking ---

        public async Task AssignToRemoteNodeAsync(string normalizedPath, string workItemId,
            string nodeId, string nodeName, string nodeIp, int nodePort, string phase)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file == null) return;

            file.Status = MediaFileStatus.Processing;
            file.RemoteWorkItemId = workItemId;
            file.AssignedNodeId = nodeId;
            file.AssignedNodeName = nodeName;
            file.AssignedNodeIp = nodeIp;
            file.AssignedNodePort = nodePort;
            file.RemoteJobPhase = phase;
            await context.SaveChangesAsync();
        }

        public async Task UpdateRemoteJobPhaseAsync(string normalizedPath, string phase)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file != null)
            {
                file.RemoteJobPhase = phase;
                await context.SaveChangesAsync();
            }
        }

        public async Task ClearRemoteAssignmentAsync(string normalizedPath, MediaFileStatus newStatus)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file == null) return;

            // Keep RemoteWorkItemId so the node's partial temp files can be found on retry.
            // Only clear it on successful completion (Completed status).
            if (newStatus == MediaFileStatus.Completed)
                file.RemoteWorkItemId = null;
            file.AssignedNodeId = null;
            file.AssignedNodeName = null;
            file.AssignedNodeIp = null;
            file.AssignedNodePort = null;
            file.RemoteJobPhase = null;
            file.Status = newStatus;
            await context.SaveChangesAsync();
        }

        public async Task IncrementRemoteFailureCountAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file != null)
            {
                file.RemoteFailureCount++;
                await context.SaveChangesAsync();
            }
        }

        public async Task<List<MediaFile>> GetActiveRemoteJobsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.AssignedNodeId != null)
                .ToListAsync();
        }

        public async Task<bool> IsRemoteJobAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.AnyAsync(f =>
                f.FilePath == normalizedPath &&
                f.AssignedNodeId != null);
        }
    }
}
