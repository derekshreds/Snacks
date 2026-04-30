using Microsoft.EntityFrameworkCore;
using Snacks.Models;

namespace Snacks.Data;

/// <summary>
///     Repository for media file persistence operations.
///     Provides CRUD, batch operations, and cluster job tracking
///     with proper DbContext lifecycle management (create/dispose per operation).
///
///     Uses IDbContextFactory for efficient context pooling and
///     SQLite WAL mode for crash resilience.
/// </summary>
public class MediaFileRepository
{
        #region Construction & Helpers

        private readonly IDbContextFactory<SnacksDbContext> _contextFactory;

        /// <summary> Creates a new repository using the specified context factory. </summary>
        /// <param name="contextFactory"> The EF Core context factory used to create per-operation contexts. </param>
        public MediaFileRepository(IDbContextFactory<SnacksDbContext> contextFactory)
        {
            _contextFactory = contextFactory;
        }

        /// <summary>
        ///     Retries <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)" />
        ///     with exponential backoff when SQLite returns SQLITE_BUSY.
        /// </summary>
        /// <param name="context"> The context whose changes are being saved. </param>
        /// <param name="maxRetries"> Maximum number of retry attempts before propagating the exception. </param>
        private static async Task SaveChangesWithRetryAsync(SnacksDbContext context, int maxRetries = 5)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await context.SaveChangesAsync();
                    return;
                }
                catch (DbUpdateException ex) when (
                    attempt < maxRetries &&
                    (ex.InnerException?.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) == true ||
                     ex.InnerException?.Message.Contains("SQLITE_BUSY", StringComparison.OrdinalIgnoreCase) == true))
                {
                    var delay = (int)Math.Pow(2, attempt) * 50; // 50, 100, 200, 400, 800ms
                    await Task.Delay(delay);
                }
            }
        }

        /// <summary>
        ///     Applies pending migrations and configures WAL mode with NORMAL synchronous writes
        ///     for crash resilience and read concurrency. Must be called once at application startup.
        /// </summary>
        public async Task InitializeAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();

            // Clear any stale EF Core migration lock left by a previous crash.
            // This is safe because Snacks is a single-instance app — if we're
            // starting up, no other instance is legitimately holding the lock.
            try
            {
                await context.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsLock;");
            }
            catch { /* Table may not exist yet on first run */ }

            await context.Database.MigrateAsync();

            // WAL allows concurrent readers during writes. NORMAL synchronous only syncs at
            // checkpoint boundaries — faster than FULL but risks losing the last few transactions
            // on a hard crash, which is an acceptable trade-off for this workload.
            await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
        }

        #endregion

        #region Basic CRUD Operations

        /// <summary> Looks up a media file by its absolute path, or <see langword="null" /> if not found. </summary>
        /// <param name="normalizedPath"> The normalized absolute file path to search for. </param>
        /// <returns> The matching <see cref="MediaFile" />, or <see langword="null" />. </returns>
        public async Task<MediaFile?> GetByPathAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
        }

        /// <summary>
        ///     Looks up a media file by directory and base name (without extension).
        ///     Used to detect format changes (e.g., <c>.avi</c> renamed to <c>.mkv</c>) during scanning.
        /// </summary>
        /// <param name="directory"> The parent directory path to search within. </param>
        /// <param name="baseName"> The file name without its extension. </param>
        /// <returns> The matching <see cref="MediaFile" />, or <see langword="null" />. </returns>
        public async Task<MediaFile?> GetByBaseNameInDirectoryAsync(string directory, string baseName)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .FirstOrDefaultAsync(f => f.Directory == directory && f.BaseName == baseName);
        }

        /// <summary>
        ///     Inserts or updates a <see cref="MediaFile" /> record matched by path.
        ///     Failure fields are intentionally not overwritten on re-scan so that error history is preserved.
        /// </summary>
        /// <param name="file"> The media file record to persist. </param>
        public async Task UpsertAsync(MediaFile file)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var existing = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == file.FilePath);

            if (existing != null)
            {
                // Failure fields are left untouched so error history survives a re-scan.
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
                // Only overwrite stream summaries when the caller actually has them — a
                // partial upsert without probe data shouldn't wipe previously-captured
                // summaries that the Mux re-evaluation still needs.
                if (file.AudioStreams    != null) existing.AudioStreams    = file.AudioStreams;
                if (file.SubtitleStreams != null) existing.SubtitleStreams = file.SubtitleStreams;
            }
            else
            {
                context.MediaFiles.Add(file);
            }

            await SaveChangesWithRetryAsync(context);
        }

        /// <summary>
        ///     Updates the processing status of a file by path, optionally recording a failure reason.
        ///     <see cref="MediaFile.CompletedAt" /> is set automatically when <paramref name="status" />
        ///     is <see cref="MediaFileStatus.Completed" />.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path identifying the file. </param>
        /// <param name="status"> The new processing status to apply. </param>
        /// <param name="failureReason"> Optional description of why the file failed. </param>
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
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary>
        ///     Increments the failure counter, sets the status to <see cref="MediaFileStatus.Failed" />,
        ///     and records the error message, truncated to 2048 characters to fit the column constraint.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path identifying the file. </param>
        /// <param name="reason"> A description of the failure. </param>
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
                await SaveChangesWithRetryAsync(context);
            }
        }

        #endregion

        #region Batch & Query Operations

        /// <summary> Returns all files with a specific processing status. </summary>
        /// <param name="status"> The status to filter by. </param>
        /// <returns> All <see cref="MediaFile" /> records matching <paramref name="status" />. </returns>
        public async Task<List<MediaFile>> GetFilesWithStatusAsync(MediaFileStatus status)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == status)
                .ToListAsync();
        }

        /// <summary>
        ///     Returns <see langword="true" /> if a file matching the given path or base name in the same
        ///     directory already has a non-<see cref="MediaFileStatus.Unseen" /> status.
        ///     Used during scanning to skip files that have already been processed.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path to check. </param>
        /// <param name="directory"> The parent directory to search within. </param>
        /// <param name="baseName"> The file name without extension to match. </param>
        /// <returns> <see langword="true" /> if the file is known and has been processed. </returns>
        public async Task<bool> IsFileKnownAsync(string normalizedPath, string directory, string baseName)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.AnyAsync(f =>
                (f.FilePath == normalizedPath ||
                 (f.Directory == directory && f.BaseName == baseName)) &&
                f.Status != MediaFileStatus.Unseen);
        }

        /// <summary>
        ///     Loads status, file size, and duration for every file in the given directories
        ///     into a path-keyed dictionary for fast in-memory lookups during batch scanning.
        /// </summary>
        /// <param name="directories"> The directory paths whose files should be loaded. </param>
        /// <returns>
        ///     A dictionary keyed by absolute file path, with a tuple of status, file size, and duration.
        /// </returns>
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
        ///     Loads the status of all files in the given directories, keyed by
        ///     <c>"directory|basename"</c> (lowercased) for fast format-change detection during scanning.
        /// </summary>
        /// <param name="directories"> The directory paths whose files should be loaded. </param>
        /// <returns> A dictionary keyed by lowercased <c>"directory|basename"</c> composite. </returns>
        public async Task<Dictionary<string, MediaFileStatus>> GetBaseNameStatusBatchAsync(IEnumerable<string> directories)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var dirList = directories.ToList();
            var files = await context.MediaFiles
                .Where(f => dirList.Contains(f.Directory))
                .Select(f => new { Key = (f.Directory + "|" + f.BaseName).ToLower(), f.Status })
                .ToListAsync();

            var dict = new Dictionary<string, MediaFileStatus>();
            foreach (var f in files)
                dict[f.Key] = f.Status; // last-write wins for duplicate base names
            return dict;
        }

        /// <summary> Returns the number of files with a specific processing status. </summary>
        /// <param name="status"> The status to count. </param>
        /// <returns> The count of matching files. </returns>
        public async Task<int> GetCountByStatusAsync(MediaFileStatus status)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.CountAsync(f => f.Status == status);
        }

        /// <summary>
        ///     Returns all failed files ordered by failure count descending,
        ///     so the most-problematic files surface first in the UI.
        /// </summary>
        /// <returns> All <see cref="MediaFile" /> records with <see cref="MediaFileStatus.Failed" /> status. </returns>
        public async Task<List<MediaFile>> GetFailedFilesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Failed)
                .OrderByDescending(f => f.FailureCount)
                .ToListAsync();
        }

        /// <summary>
        ///     Deletes every <see cref="MediaFile" /> row whose status is
        ///     <see cref="MediaFileStatus.Failed" />. The next library scan re-discovers
        ///     any source file still on disk as <see cref="MediaFileStatus.Unseen" />,
        ///     giving the user a one-click recovery path for legitimate failures and
        ///     for the bogus "Source file was removed during encoding" backlog.
        /// </summary>
        /// <returns>The number of rows deleted.</returns>
        public async Task<int> DeleteAllFailedAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Failed)
                .ExecuteDeleteAsync();
        }

        #endregion

        #region Reset & Maintenance

        /// <summary>
        ///     Resets every file to <see cref="MediaFileStatus.Unseen" /> and clears all failure fields.
        ///     Intended to be called before a full rescan so that all files are re-evaluated from scratch.
        /// </summary>
        public async Task ResetAllStatusesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await context.MediaFiles.ExecuteUpdateAsync(s =>
                s.SetProperty(f => f.Status, MediaFileStatus.Unseen)
                 .SetProperty(f => f.FailureCount, 0)
                 .SetProperty(f => f.FailureReason, (string?)null)
                 .SetProperty(f => f.CompletedAt, (DateTime?)null)
                 .SetProperty(f => f.AssignedNodeId, (string?)null)
                 .SetProperty(f => f.AssignedNodeName, (string?)null)
                 .SetProperty(f => f.AssignedNodeIp, (string?)null)
                 .SetProperty(f => f.AssignedNodePort, (int?)null)
                 .SetProperty(f => f.RemoteWorkItemId, (string?)null)
                 .SetProperty(f => f.RemoteJobPhase, (string?)null)
                 .SetProperty(f => f.RemoteFailureCount, 0));
        }

        /// <summary>
        ///     Clears only the <c>RemoteWorkItemId</c> for a file without changing its status.
        ///     Used when a stale remote ID is detected (node no longer has partial data).
        /// </summary>
        public async Task ClearRemoteWorkItemIdAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file != null)
            {
                file.RemoteWorkItemId = null;
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary> Deletes all entries from the StateTransitions WAL table. </summary>
        public async Task ClearAllTransitionsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await context.StateTransitions.ExecuteDeleteAsync();
        }

        /// <summary>
        ///     Re-evaluates every <see cref="MediaFileStatus.Skipped" /> row against the supplied
        ///     predicate. Files for which <paramref name="shouldStaySkipped" /> returns
        ///     <see langword="false" /> are flipped back to <see cref="MediaFileStatus.Unseen" />
        ///     so the next scan picks them up. Used when encoder settings change to re-queue
        ///     files whose skip decision would no longer hold.
        /// </summary>
        /// <param name="shouldStaySkipped">
        ///     Pure predicate over the DB-stored fields (no probing). Return <see langword="true" />
        ///     to keep the row skipped, <see langword="false" /> to flip it to Unseen.
        /// </param>
        /// <returns> The number of rows whose status was flipped. </returns>
        public async Task<int> ReevaluateSkippedAsync(Func<MediaFile, bool> shouldStaySkipped)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var skipped = await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Skipped)
                .ToListAsync();

            int flipped = 0;
            foreach (var mf in skipped)
            {
                if (shouldStaySkipped(mf)) continue;
                mf.Status = MediaFileStatus.Unseen;
                mf.LastScannedAt = null;
                flipped++;
            }

            if (flipped > 0) await SaveChangesWithRetryAsync(context);
            return flipped;
        }

        /// <summary>
        ///     Resets a single file to <see cref="MediaFileStatus.Unseen" /> and clears its failure fields.
        ///     Used when a file has changed on disk or needs to be retried after a failure.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the file to reset. </param>
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
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary>
        ///     Inserts records for any paths not already in the database, marking them as
        ///     <see cref="MediaFileStatus.Completed" /> so they are skipped by the encoder.
        ///     Inserts in chunks of 500 to keep individual transactions small.
        /// </summary>
        /// <param name="paths"> The file paths observed during scanning. </param>
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

            // Chunking avoids holding a single large write transaction open for the full set.
            foreach (var chunk in newFiles.Chunk(500))
            {
                context.MediaFiles.AddRange(chunk);
                await SaveChangesWithRetryAsync(context);
                context.ChangeTracker.Clear();
            }
        }

        /// <summary>
        ///     Removes database records for files that no longer exist on disk.
        ///     Processes in batches of 1000 to bound per-iteration memory consumption.
        /// </summary>
        public async Task PruneDeletedFilesAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var batchSize = 1000;
            int lastProcessedId = 0;

            while (true)
            {
                var batch = await context.MediaFiles
                    .Where(f => f.Id > lastProcessedId)
                    .OrderBy(f => f.Id)
                    .Take(batchSize)
                    .ToListAsync();

                if (batch.Count == 0) break;

                lastProcessedId = batch[^1].Id;

                var toRemove = batch.Where(f => !File.Exists(f.FilePath)).ToList();

                if (toRemove.Count > 0)
                {
                    context.MediaFiles.RemoveRange(toRemove);
                    await SaveChangesWithRetryAsync(context);
                }
            }
        }

        #endregion

        #region Cluster Remote Job Tracking

        /// <summary>
        ///     Marks a file as <see cref="MediaFileStatus.Processing" /> and records the remote node
        ///     assignment details needed to reconnect or recover after a crash.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the file being assigned. </param>
        /// <param name="workItemId"> The unique ID of the work item on the remote node. </param>
        /// <param name="nodeId"> The unique identifier of the remote node. </param>
        /// <param name="nodeName"> The human-readable name of the remote node. </param>
        /// <param name="nodeIp"> The IP address of the remote node. </param>
        /// <param name="nodePort"> The port the remote node is listening on. </param>
        /// <param name="phase"> The initial phase label for the remote job (e.g., "Uploading"). </param>
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
            await SaveChangesWithRetryAsync(context);
        }

        /// <summary> Updates the current phase label of a remote job (e.g., from "Uploading" to "Encoding"). </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the file being processed remotely. </param>
        /// <param name="phase"> The new phase label to record. </param>
        public async Task UpdateRemoteJobPhaseAsync(string normalizedPath, string phase)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file != null)
            {
                file.RemoteJobPhase = phase;
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary>
        ///     Clears the remote node assignment fields and applies <paramref name="newStatus" />.
        ///     <see cref="MediaFile.RemoteWorkItemId" /> is preserved only when re-queuing
        ///     (<see cref="MediaFileStatus.Queued" />) so the node's partial data can be reused
        ///     on retry. For all other statuses (completed, cancelled, failed, etc.) it is cleared.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the file. </param>
        /// <param name="newStatus"> The status to apply after clearing the assignment. </param>
        public async Task ClearRemoteAssignmentAsync(string normalizedPath, MediaFileStatus newStatus)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file == null) return;

            // Clear RemoteWorkItemId when the job is done (completed or cancelled).
            // Preserve it only for Queued (requeue for retry) so the node's partial data can be reused.
            if (newStatus != MediaFileStatus.Queued)
                file.RemoteWorkItemId = null;
            file.AssignedNodeId = null;
            file.AssignedNodeName = null;
            file.AssignedNodeIp = null;
            file.AssignedNodePort = null;
            file.RemoteJobPhase = null;
            file.Status = newStatus;
            await SaveChangesWithRetryAsync(context);
        }

        /// <summary> Increments the remote failure counter for the specified file. </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the file. </param>
        public async Task IncrementRemoteFailureCountAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles.FirstOrDefaultAsync(f => f.FilePath == normalizedPath);
            if (file != null)
            {
                file.RemoteFailureCount++;
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary>
        ///     Returns all files that currently have an active remote node assignment.
        ///     Queried at master startup to recover jobs that were in-flight before a crash.
        /// </summary>
        /// <returns> All <see cref="MediaFile" /> records with a non-<see langword="null" /> assigned node ID. </returns>
        public async Task<List<MediaFile>> GetActiveRemoteJobsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.AssignedNodeId != null)
                .ToListAsync();
        }

        /// <summary>
        ///     Returns <see langword="true" /> if the file at <paramref name="normalizedPath" />
        ///     is currently assigned to a remote node.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path to check. </param>
        /// <returns> <see langword="true" /> if the file has an active remote node assignment. </returns>
        public async Task<bool> IsRemoteJobAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.AnyAsync(f =>
                f.FilePath == normalizedPath &&
                f.AssignedNodeId != null);
        }

        /// <summary>
        ///     Looks up a media file by its remote work item ID.
        ///     Used during heartbeat reconciliation to find jobs that exist in the database
        ///     but have not yet been loaded into the in-memory cache, preventing spurious cancellations.
        /// </summary>
        /// <param name="remoteWorkItemId"> The remote work item ID to search for. </param>
        /// <returns> The matching <see cref="MediaFile" />, or <see langword="null" />. </returns>
        public async Task<MediaFile?> GetByRemoteWorkItemIdAsync(string remoteWorkItemId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .FirstOrDefaultAsync(f => f.RemoteWorkItemId == remoteWorkItemId);
        }

        #endregion

        #region Distributed Transaction Log

        /// <summary>
        ///     Persists a write-ahead log entry for a state transition BEFORE the actual change occurs,
        ///     so that interrupted operations can be detected and replayed after a crash.
        /// </summary>
        /// <param name="workItemId"> The ID of the job this transition applies to. </param>
        /// <param name="fromPhase"> The current phase; <see langword="null" /> for initial assignments. </param>
        /// <param name="toPhase"> The target phase this transition is moving toward. </param>
        /// <returns> The persisted transition ID, for use with <see cref="CompleteTransitionAsync" />. </returns>
        public async Task<int> BeginTransitionAsync(string workItemId, string fromPhase, string toPhase)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var transition = new StateTransition
            {
                WorkItemId = workItemId,
                FromPhase = fromPhase,
                ToPhase = toPhase,
                Timestamp = DateTime.UtcNow,
                Completed = false
            };
            context.StateTransitions.Add(transition);
            await SaveChangesWithRetryAsync(context);
            return transition.Id;
        }

        /// <summary>
        ///     Marks a previously recorded transition as completed. Must be called AFTER the
        ///     actual state change has been applied successfully.
        /// </summary>
        /// <param name="transitionId"> The ID returned by <see cref="BeginTransitionAsync" />. </param>
        public async Task CompleteTransitionAsync(int transitionId)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var transition = await context.StateTransitions.FindAsync(transitionId);
            if (transition != null)
            {
                transition.Completed = true;
                await SaveChangesWithRetryAsync(context);
            }
        }

        /// <summary>
        ///     Returns all incomplete transitions ordered by timestamp ascending.
        ///     Queried during recovery to identify interrupted operations that require replay or rollback.
        /// </summary>
        /// <returns> Incomplete <see cref="StateTransition" /> records in chronological order. </returns>
        public async Task<List<StateTransition>> GetIncompleteTransitionsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.StateTransitions
                .Where(t => !t.Completed)
                .OrderBy(t => t.Timestamp)
                .ToListAsync();
        }

        /// <summary>
        ///     Deletes completed <see cref="StateTransition" /> records older than 24 hours,
        ///     processing at most 1000 per call to prevent unbounded table growth.
        /// </summary>
        public async Task CleanupOldTransitionsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var cutoff = DateTime.UtcNow.AddHours(-24);
            var oldTransitions = await context.StateTransitions
                .Where(t => t.Completed && t.Timestamp < cutoff)
                .Take(1000)
                .ToListAsync();
            if (oldTransitions.Count > 0)
            {
                context.StateTransitions.RemoveRange(oldTransitions);
                await SaveChangesWithRetryAsync(context);
            }
        }

        #endregion
    }
