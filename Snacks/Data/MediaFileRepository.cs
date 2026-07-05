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
                existing.IsHdr = file.IsHdr;
                existing.Is4K = file.Is4K;
                // Kind must follow the caller's classification. BulkInsertSeenFilesAsync
                // inserts every scanned path with the default Kind=Video; AddMusicFileAsync
                // later re-upserts music files with Kind=Music. Without this assignment,
                // the row stays Video forever, RestoreToQueueAsync rebuilds music WorkItems
                // with Kind=Video on master restart, and the cluster dispatcher routes them
                // through the video filter / video device path — they never reach a worker's
                // music slot.
                existing.Kind = file.Kind;
                existing.Status = file.Status;
                // Non-zero only: AddFileAsync stamps the folder-override base priority
                // (a feature that must reach pre-existing rows, not just first-ever
                // inserts), but most callers construct rows with the default 0 — copying
                // that unconditionally would erase a user's "move to front" bump.
                if (file.Priority != 0) existing.Priority = file.Priority;
                // Sticky-true: a manual "Process Item/Directory" sets ForceMux on the queued
                // row, and a concurrent auto-scan re-upsert (which constructs rows with the
                // default false) must not erase that intent before the item dispatches. The
                // flag is cleared explicitly on terminal completion and on file reset.
                if (file.ForceMux) existing.ForceMux = true;
                existing.LastScannedAt = file.LastScannedAt;
                existing.FileMtime = file.FileMtime;
                // Only overwrite stream summaries when the caller actually has them — a
                // partial upsert without probe data shouldn't wipe previously-captured
                // summaries that the Mux re-evaluation still needs.
                if (file.AudioStreams    != null) existing.AudioStreams    = file.AudioStreams;
                if (file.SubtitleStreams != null) existing.SubtitleStreams = file.SubtitleStreams;
                // Same null-aware policy: a re-scan that didn't run a fresh language lookup
                // (KeepOriginalLanguage off, no integration provider, or transient network
                // failure) shouldn't wipe a value that was successfully resolved earlier.
                if (file.OriginalLanguage != null) existing.OriginalLanguage = file.OriginalLanguage;
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
        ///     Status setter that also stamps <see cref="MediaFile.LastEncodedAt"/>. Used by the
        ///     completion paths (both keep and no-savings) so Re-evaluate can reason about
        ///     "we already tried this" — the empirical anchor that prevents the NoSavings →
        ///     Unseen → re-encode loop.
        /// </summary>
        public async Task SetStatusAndLastEncodedAtAsync(string normalizedPath, MediaFileStatus status, DateTime lastEncodedAt)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var file = await context.MediaFiles
                .FirstOrDefaultAsync(f => f.FilePath == normalizedPath);

            if (file != null)
            {
                file.Status = status;
                file.LastEncodedAt = lastEncodedAt;
                // A force-mux request is satisfied once the encode reaches a terminal outcome —
                // clear it so a future normal re-queue of this file isn't silently remuxed.
                file.ForceMux = false;
                if (status == MediaFileStatus.Completed)
                    file.CompletedAt = lastEncodedAt;
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

        #region DB-First Pending Queue

        /*
         * The pending queue lives HERE, not in memory: pending work = rows with
         * Status == Queued and no remote assignment, ordered by Priority desc then
         * the policy tiebreaker. The scheduler hydrates only the top of this order
         * into its in-memory working window; the queue UI pages it directly. The
         * (Status, Priority, Bitrate) index makes both O(log n) at 500k rows.
         */

        /// <summary> Local-pending queue rows: queued and not assigned to a cluster node. </summary>
        private static IQueryable<MediaFile> QueuedLocal(SnacksDbContext context) =>
            context.MediaFiles.Where(f => f.Status == MediaFileStatus.Queued && f.AssignedNodeId == null);

        /// <summary> Applies the canonical queue order: user priority, then the policy tiebreaker. </summary>
        private static IQueryable<MediaFile> InQueueOrder(IQueryable<MediaFile> q, bool newestFirst) =>
            newestFirst
                ? q.OrderByDescending(f => f.Priority).ThenByDescending(f => f.CreatedAt).ThenBy(f => f.Id)
                : q.OrderByDescending(f => f.Priority).ThenByDescending(f => f.Bitrate).ThenBy(f => f.Id);

        /// <summary>
        ///     The top of the pending queue — what the scheduler's working window should
        ///     contain. <paramref name="skip"/> is non-zero only during window rotation
        ///     (the scheduler walking past a locally-unservable head of the queue).
        ///     <paramref name="kind"/> narrows to one media kind — the window sync uses it
        ///     to guarantee music representation when high-bitrate video would otherwise
        ///     monopolize every window slot and starve the dedicated music lanes.
        /// </summary>
        public async Task<List<MediaFile>> GetQueueWindowAsync(int take, bool newestFirst = false, int skip = 0, MediaKind? kind = null)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var query = QueuedLocal(context);
            if (kind != null) query = query.Where(f => f.Kind == kind);
            return await InQueueOrder(query, newestFirst).Skip(skip).Take(take).ToListAsync();
        }

        /// <summary> One page of the pending queue for the UI, plus the total pending count. </summary>
        public async Task<(List<MediaFile> Rows, int Total)> GetQueuedPageAsync(int skip, int take, bool newestFirst = false)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var total = await QueuedLocal(context).CountAsync();
            var rows  = await InQueueOrder(QueuedLocal(context), newestFirst).Skip(skip).Take(take).ToListAsync();
            return (rows, total);
        }

        /// <summary> Number of locally-pending queue rows. </summary>
        public async Task<int> CountQueuedLocalAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await QueuedLocal(context).CountAsync();
        }

        /// <summary> Fetches a row by primary key. </summary>
        public async Task<MediaFile?> GetByIdAsync(int id)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.FindAsync(id);
        }

        /// <summary>
        ///     Moves a queued row to the front of the pending order by setting its
        ///     priority above the current queued maximum. Returns the new priority,
        ///     or null when the row is unknown or no longer queued.
        /// </summary>
        public async Task<int?> BumpPriorityToFrontAsync(int id)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var row = await context.MediaFiles.FindAsync(id);
            if (row == null || row.Status != MediaFileStatus.Queued) return null;

            var max = await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Queued)
                .MaxAsync(f => (int?)f.Priority) ?? 0;
            row.Priority = max + 1;
            await context.SaveChangesAsync();
            return row.Priority;
        }

        /// <summary>
        ///     Flips a row's status by primary key (used by queue actions on rows that
        ///     aren't hydrated into the scheduler's window). Guarded: only acts when the
        ///     row is currently Queued, so a just-dispatched item can't be yanked back.
        /// </summary>
        public async Task<bool> SetQueuedRowStatusAsync(int id, MediaFileStatus status)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var row = await context.MediaFiles.FindAsync(id);
            if (row == null || row.Status != MediaFileStatus.Queued) return false;
            row.Status = status;
            await context.SaveChangesAsync();
            return true;
        }

        /// <summary>
        ///     Walks every locally-pending queue row in id-ordered pages and flips the
        ///     ones <paramref name="wouldSkipNow"/> says no longer need encoding to
        ///     Skipped. Paged with a fresh context per page so a 500k-row backlog
        ///     never materializes (or stays change-tracked) all at once. Rows without
        ///     cached stream summaries are left alone — conservative, the next scan
        ///     re-evaluates them.
        /// </summary>
        /// <returns> Number of rows flipped to Skipped. </returns>
        public async Task<int> ReevaluateQueuedAsync(Func<MediaFile, bool> wouldSkipNow)
        {
            const int PageSize = 2000;
            int flipped = 0, lastId = 0;

            while (true)
            {
                using var context = await _contextFactory.CreateDbContextAsync();
                var page = await QueuedLocal(context)
                    .Where(f => f.Id > lastId)
                    .OrderBy(f => f.Id)
                    .Take(PageSize)
                    .ToListAsync();
                if (page.Count == 0) break;
                lastId = page[^1].Id;

                int pageFlipped = 0;
                foreach (var mf in page)
                {
                    if (mf.AudioStreams == null && mf.SubtitleStreams == null) continue;
                    if (!wouldSkipNow(mf)) continue;
                    mf.Status = MediaFileStatus.Skipped;
                    pageFlipped++;
                }

                if (pageFlipped > 0)
                {
                    await SaveChangesWithRetryAsync(context);
                    flipped += pageFlipped;
                }
            }
            return flipped;
        }

        /// <summary>
        ///     Sets one status on a batch of paths in a single transaction. The sweep
        ///     collects companion-validated "already completed" marks per chunk and
        ///     flushes them through here — one commit instead of a write per file.
        /// </summary>
        /// <returns> Number of rows updated. </returns>
        public async Task<int> SetStatusBatchAsync(IEnumerable<string> normalizedPaths, MediaFileStatus status)
        {
            var paths = normalizedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) return 0;

            using var context = await _contextFactory.CreateDbContextAsync();
            int updated = 0;
            // SQLite parameter limit is ~999 — chunk the IN list defensively.
            foreach (var chunk in paths.Chunk(500))
            {
                updated += await context.MediaFiles
                    .Where(f => chunk.Contains(f.FilePath))
                    .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, status));
            }
            return updated;
        }

        /// <summary>
        ///     Startup recovery: rows a crashed run left in Processing with no remote
        ///     assignment go back to Queued so the scheduler's window picks them up.
        ///     One bulk UPDATE — replaces the per-row restore loop.
        /// </summary>
        /// <returns> Number of rows requeued. </returns>
        public async Task<int> RequeueOrphanedLocalProcessingAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Processing && f.AssignedNodeId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, MediaFileStatus.Queued));
        }

        /// <summary>
        ///     Bulk-resets every locally-pending queue row to Unseen and clears its
        ///     priority. Backs "stop and clear queue" — the in-memory window only
        ///     holds the top ~50 items, so clearing memory alone would leave the
        ///     rest of a deep backlog re-hydrating right back into the window.
        /// </summary>
        /// <returns> Number of rows reset. </returns>
        public async Task<int> ResetAllQueuedAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await QueuedLocal(context)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.Status, MediaFileStatus.Unseen)
                    .SetProperty(f => f.Priority, 0)
                    .SetProperty(f => f.ForceMux, false));
        }

        #endregion

        #region Rolling Verification

        /// <summary>
        ///     Next candidates for the rolling deep-verifier: scanned rows that aren't
        ///     actively queued/processing, ordered never-verified first (SQLite sorts
        ///     NULL first ascending) then oldest-verified — so the whole library is
        ///     continuously re-checked in rotation.
        /// </summary>
        public async Task<List<MediaFile>> GetVerificationCandidatesAsync(int take)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles
                .Where(f => f.LastScannedAt != null
                            && f.Status != MediaFileStatus.Queued
                            && f.Status != MediaFileStatus.Processing)
                .OrderBy(f => f.LastVerifiedAt)
                .ThenBy(f => f.Id)
                .Take(take)
                .ToListAsync();
        }

        /// <summary> Records a deep-verification outcome ("ok" or the truncated issue summary). </summary>
        public async Task SetVerifyResultAsync(string normalizedPath, string result)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            await context.MediaFiles
                .Where(f => f.FilePath == normalizedPath)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.LastVerifiedAt, DateTime.UtcNow)
                    .SetProperty(f => f.LastVerifyResult, result));
        }

        /// <summary> Verification coverage stats for the health page: verified count, failed count, oldest pass age. </summary>
        public async Task<(int Verified, int FailedVerify, int Total)> GetVerificationStatsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var scanned = context.MediaFiles.Where(f => f.LastScannedAt != null);
            return (
                await scanned.CountAsync(f => f.LastVerifiedAt != null),
                await scanned.CountAsync(f => f.LastVerifyResult != null && f.LastVerifyResult != "ok" && f.LastVerifyResult != "missing"),
                await scanned.CountAsync());
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
        ///     Total number of media-file rows known to this install, regardless of status.
        ///     Used to distinguish a genuine first run (the DB has never seen a file) from an
        ///     established library that simply has nothing queued right now — so the queue's
        ///     "Welcome to Snacks" onboarding hero only appears on a true first run.
        /// </summary>
        public async Task<int> CountAllAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await context.MediaFiles.CountAsync();
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
        ///     Aggregate library composition for the insights panel: file/byte totals
        ///     plus codec, resolution-bucket, and status distributions. All grouping
        ///     happens in SQL — nothing row-shaped crosses into memory, so this stays
        ///     cheap on 100k-row libraries.
        /// </summary>
        public async Task<LibraryInsights> GetLibraryInsightsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var files = context.MediaFiles.Where(f => f.LastScannedAt != null);

            var codecs = await files
                .Where(f => f.Codec != "")
                .GroupBy(f => f.Codec)
                .Select(g => new LibraryInsights.Slice(g.Key, g.Count(), g.Sum(f => f.FileSize)))
                .ToListAsync();

            // Width buckets follow the app's own 4K rule (Width > 1920).
            var resolutions = new List<LibraryInsights.Slice>
            {
                new("4K",    await files.CountAsync(f => f.Kind == MediaKind.Video && f.Width > 1920), 0),
                new("1080p", await files.CountAsync(f => f.Kind == MediaKind.Video && f.Width > 1280 && f.Width <= 1920), 0),
                new("720p",  await files.CountAsync(f => f.Kind == MediaKind.Video && f.Width > 960  && f.Width <= 1280), 0),
                new("SD",    await files.CountAsync(f => f.Kind == MediaKind.Video && f.Width > 0    && f.Width <= 960), 0),
            };

            var statuses = await files
                .GroupBy(f => f.Status)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync();

            return new LibraryInsights
            {
                TotalFiles  = await files.CountAsync(),
                TotalBytes  = await files.SumAsync(f => (long?)f.FileSize) ?? 0,
                HdrFiles    = await files.CountAsync(f => f.IsHdr),
                MusicFiles  = await files.CountAsync(f => f.Kind == MediaKind.Music),
                Codecs      = codecs.OrderByDescending(c => c.Count).ToList(),
                Resolutions = resolutions.Where(r => r.Count > 0).ToList(),
                Statuses    = statuses.OrderByDescending(s => s.Count)
                                      .Select(s => new LibraryInsights.Slice(s.Key.ToString(), s.Count, 0))
                                      .ToList(),
            };
        }

        /*
         * Library-health predicates. A file can match several categories:
         *  - no-audio:      video file whose stream summary records zero audio tracks
         *                   ("[]" — explicitly empty; null means "scanned before
         *                   summaries existed" and is NOT flagged)
         *  - no-video:      probed video file (codec known) with zero dimensions
         *  - no-duration:   probed file with zero/unknown duration (truncated container)
         *  - failed:        encode permanently failed
         *  - verify-failed: the rolling deep-verifier found decode problems
         */

        private static IQueryable<MediaFile> ApplyHealthCategory(IQueryable<MediaFile> scanned, string? category) => category switch
        {
            "no-audio"      => scanned.Where(f => f.Kind == MediaKind.Video && f.AudioStreams == "[]"),
            "no-video"      => scanned.Where(f => f.Kind == MediaKind.Video && f.Codec != "" && (f.Width <= 0 || f.Height <= 0)),
            "no-duration"   => scanned.Where(f => f.Codec != "" && f.Duration <= 0),
            "failed"        => scanned.Where(f => f.Status == MediaFileStatus.Failed),
            // "missing" is the rotation's skip marker (file unreachable at verify
            // time, often a transient mount issue) — not a decode failure.
            "verify-failed" => scanned.Where(f => f.LastVerifyResult != null && f.LastVerifyResult != "ok" && f.LastVerifyResult != "missing"),
            _ => scanned.Where(f =>
                   (f.Kind == MediaKind.Video && f.AudioStreams == "[]")
                || (f.Kind == MediaKind.Video && f.Codec != "" && (f.Width <= 0 || f.Height <= 0))
                || (f.Codec != "" && f.Duration <= 0)
                || f.Status == MediaFileStatus.Failed
                || (f.LastVerifyResult != null && f.LastVerifyResult != "ok" && f.LastVerifyResult != "missing")),
        };

        /// <summary>
        ///     Authoritative per-category issue counts for the health page's summary
        ///     cards — SQL COUNTs over the whole library, never derived from a capped
        ///     item list (which would silently undercount on big libraries).
        /// </summary>
        public async Task<(int NoAudio, int NoVideo, int NoDuration, int Failed, int VerifyFailed, int Total)> GetHealthSummaryAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var scanned = context.MediaFiles.Where(f => f.LastScannedAt != null);
            return (
                await ApplyHealthCategory(scanned, "no-audio").CountAsync(),
                await ApplyHealthCategory(scanned, "no-video").CountAsync(),
                await ApplyHealthCategory(scanned, "no-duration").CountAsync(),
                await ApplyHealthCategory(scanned, "failed").CountAsync(),
                await ApplyHealthCategory(scanned, "verify-failed").CountAsync(),
                await ApplyHealthCategory(scanned, null).CountAsync());
        }

        /// <summary>
        ///     One server-side page of flagged files for the health table, optionally
        ///     narrowed to a category and a name/path search. Returns the page plus the
        ///     total for the active filter so the UI can paginate honestly.
        /// </summary>
        public async Task<(List<MediaFile> Rows, int Total)> GetHealthPageAsync(
            string? category, string? search, int skip, int take)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var query = ApplyHealthSearch(
                ApplyHealthCategory(context.MediaFiles.Where(f => f.LastScannedAt != null), category),
                search);

            var total = await query.CountAsync();
            var rows  = await query
                .OrderByDescending(f => f.Status == MediaFileStatus.Failed)
                .ThenBy(f => f.FilePath)
                .Skip(skip)
                .Take(take)
                .ToListAsync();
            return (rows, total);
        }

        /// <summary>
        ///     File paths of every flagged file matching the given health category + search —
        ///     the same set the health table shows, but the whole set (not one page), capped at
        ///     <paramref name="cap"/>. Used by the "delete all" cleanup to act on every match
        ///     across pages, not just what's currently on screen.
        /// </summary>
        public async Task<List<string>> GetHealthPathsAsync(string? category, string? search, int cap)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyHealthSearch(
                    ApplyHealthCategory(context.MediaFiles.Where(f => f.LastScannedAt != null), category),
                    search)
                .OrderByDescending(f => f.Status == MediaFileStatus.Failed)
                .ThenBy(f => f.FilePath)
                .Take(cap)
                .Select(f => f.FilePath)
                .ToListAsync();
        }

        /// <summary>
        ///     Clears the stored deep-verification state (result + timestamp) for every file
        ///     matching the given health category + search — the same set the health table shows.
        ///     A file with no stored result drops off the failed-verification list immediately,
        ///     and its null verify timestamp sorts it to the FRONT of the rolling-verification
        ///     rotation (<see cref="GetVerificationCandidatesAsync"/> orders nulls first), so the
        ///     corrected verifier re-adjudicates it soon rather than months later. Only rows that
        ///     actually carry a stored result are touched.
        /// </summary>
        /// <returns>The number of rows reset.</returns>
        public async Task<int> ResetVerifyForHealthAsync(string? category, string? search)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            return await ApplyHealthSearch(
                    ApplyHealthCategory(context.MediaFiles.Where(f => f.LastScannedAt != null), category),
                    search)
                .Where(f => f.LastVerifyResult != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.LastVerifyResult, (string?)null)
                    .SetProperty(f => f.LastVerifiedAt, (DateTime?)null));
        }

        /// <summary>
        ///     Clears the stored deep-verification state for a single file by path — the per-row
        ///     "reset" on the health page. Same effect as <see cref="ResetVerifyForHealthAsync"/>,
        ///     scoped to one row. Returns <see langword="false"/> when no row matched the path.
        /// </summary>
        public async Task<bool> ResetVerifyForPathAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var updated = await context.MediaFiles
                .Where(f => f.FilePath == normalizedPath)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(f => f.LastVerifyResult, (string?)null)
                    .SetProperty(f => f.LastVerifiedAt, (DateTime?)null));
            return updated > 0;
        }

        /// <summary>
        ///     Applies the optional name/path substring filter shared by the health page and
        ///     the delete-all path query. LIKE metacharacters in the term ("%", "_") are
        ///     escaped so they match literally — common in media filenames.
        /// </summary>
        private static IQueryable<MediaFile> ApplyHealthSearch(IQueryable<MediaFile> query, string? search)
        {
            if (string.IsNullOrWhiteSpace(search)) return query;
            var term = search.Trim()
                .Replace("\\", "\\\\")
                .Replace("%", "\\%")
                .Replace("_", "\\_");
            return query.Where(f => EF.Functions.Like(f.FilePath, $"%{term}%", "\\"));
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
                 .SetProperty(f => f.DispatchedDeviceId, (string?)null)
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
                // Keep LastScannedAt — the cached AudioStreams/SubtitleStreams/Bitrate/Codec are still
                // on the row, and the scanner's freshness check uses LastScannedAt to skip re-probing.
                // Wiping it forced a probe storm on every Re-evaluate, which is what made this an
                // expensive recovery action when settings changed.
                flipped++;
            }

            if (flipped > 0) await SaveChangesWithRetryAsync(context);
            return flipped;
        }

        /// <summary>
        ///     Flips every <see cref="MediaFileStatus.NoSavings"/> row back to
        ///     <see cref="MediaFileStatus.Unseen"/> so the next scan re-queues it.
        ///     Opt-in only — invoked from the Re-evaluate endpoint when the user ticks the
        ///     "Retry no-savings encodes" checkbox. Default Re-evaluate behavior leaves
        ///     these rows alone, since "we already ran ffmpeg and it didn't shrink" is an
        ///     empirical truth that shouldn't be overridden by the same prediction that
        ///     queued the file last time.
        /// </summary>
        /// <returns>The number of rows flipped.</returns>
        public async Task<int> ReevaluateNoSavingsAsync()
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var rows = await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.NoSavings)
                .ToListAsync();

            foreach (var mf in rows)
                mf.Status = MediaFileStatus.Unseen;

            if (rows.Count > 0) await SaveChangesWithRetryAsync(context);
            return rows.Count;
        }

        /// <summary>
        ///     Reverse of <see cref="ReevaluateSkippedAsync"/>: re-evaluates every <see cref="MediaFileStatus.Unseen" />
        ///     row against the supplied predicate. Files for which <paramref name="shouldBeSkipped" /> returns
        ///     <see langword="true" /> are flipped back to <see cref="MediaFileStatus.Skipped" />. Used when encoder
        ///     settings change in the "no longer needs encoding" direction — e.g., the user added an audio output
        ///     that re-queued a batch of files, then removed it. Without this method, those files stay queued.
        /// </summary>
        /// <param name="shouldBeSkipped">
        ///     Pure predicate over the DB-stored fields (no probing). Return <see langword="true" /> to flip
        ///     the row back to Skipped, <see langword="false" /> to leave it Unseen.
        /// </param>
        /// <returns> The number of rows whose status was flipped. </returns>
        public async Task<int> ReevaluateUnseenAsync(Func<MediaFile, bool> shouldBeSkipped)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            // Only consider rows we have enough data on to make a decision. A null AudioStreams /
            // SubtitleStreams blob means the file was scanned before stream-summary persistence
            // existed; we can't safely re-evaluate without re-probing, so leave it for the next scan.
            var unseen = await context.MediaFiles
                .Where(f => f.Status == MediaFileStatus.Unseen
                         && (f.AudioStreams != null || f.SubtitleStreams != null))
                .ToListAsync();

            int flipped = 0;
            foreach (var mf in unseen)
            {
                if (!shouldBeSkipped(mf)) continue;
                mf.Status = MediaFileStatus.Skipped;
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
                // The file changed on disk — drop any prior force-mux intent so it's
                // re-evaluated normally on the next scan.
                file.ForceMux = false;
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
        ///     Deletes the row for a single file by its normalized path. Used by the
        ///     mid-cycle missing-source cleanup so callers don't have to wait for the
        ///     next scan's <see cref="PruneDeletedFilesAsync" /> sweep.
        /// </summary>
        /// <param name="normalizedPath"> The normalized absolute path of the row to remove. </param>
        /// <returns> <see langword="true" /> if a row was deleted, <see langword="false" /> if no row matched. </returns>
        public async Task<bool> RemoveByPathAsync(string normalizedPath)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var deleted = await context.MediaFiles
                .Where(f => f.FilePath == normalizedPath)
                .ExecuteDeleteAsync();
            return deleted > 0;
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
            string nodeId, string nodeName, string nodeIp, int nodePort, string phase,
            string? dispatchedDeviceId = null)
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
            // Persisted so SlotLedger recovery rebuilds the same per-device
            // occupancy after a master restart. Only updated when caller
            // supplies a value — older callers (or callers that don't yet
            // know the device) leave the field untouched.
            if (!string.IsNullOrEmpty(dispatchedDeviceId))
                file.DispatchedDeviceId = dispatchedDeviceId;
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
            // DispatchedDeviceId is cleared on every clear-assignment so a
            // re-queued job picks a fresh slot on the next dispatch tick
            // rather than pinning to whatever the previous attempt used.
            file.DispatchedDeviceId = null;
            file.Status = newStatus;
            // Stamp LastEncodedAt for the empirical-outcome statuses so re-evaluate / auto-scan
            // can reason about "we just tried this" — same anchor SetStatusAndLastEncodedAtAsync
            // uses on the local-completion path.
            if (newStatus is MediaFileStatus.Completed or MediaFileStatus.NoSavings or MediaFileStatus.Skipped)
                file.LastEncodedAt = DateTime.UtcNow;
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
