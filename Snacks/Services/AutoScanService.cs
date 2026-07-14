namespace Snacks.Services;

using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
///     Background service that periodically scans configured directories for new video files
///     and adds them to the transcoding queue. Also handles queue resume on startup and
///     one-time migration of legacy seen-file data to the database.
/// </summary>
public sealed class AutoScanService : IHostedService, IDisposable
{
    private readonly FileService                 _fileService;
    private readonly FfprobeService              _ffprobeService;
    private readonly TranscodingService          _transcodingService;
    private readonly MediaFileRepository         _mediaFileRepo;
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly ClusterService              _clusterService;
    private readonly NotificationService         _notificationService;
    private readonly SemaphoreSlim               _scanLock = new(1, 1);
    private readonly string                      _configPath;
    private readonly string                      _settingsPath;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _configLock = new();
    private AutoScanConfig  _config = new();
    private Timer?          _timer;
    private CancellationTokenSource? _scanCts;

    /******************************************************************
     *  Constructor
     ******************************************************************/

    /// <summary>
    ///     Initializes the service and resolves the paths to the <c>autoscan.json</c>
    ///     and <c>settings.json</c> config files, creating the config directory if needed.
    /// </summary>
    public AutoScanService(
        FileService fileService,
        FfprobeService ffprobeService,
        TranscodingService transcodingService,
        MediaFileRepository mediaFileRepo,
        IHubContext<TranscodingHub> hubContext,
        ClusterService clusterService,
        NotificationService notificationService)
    {
        _fileService         = fileService;
        _ffprobeService      = ffprobeService;
        _transcodingService  = transcodingService;
        _mediaFileRepo       = mediaFileRepo;
        _hubContext          = hubContext;
        _clusterService      = clusterService;
        _notificationService = notificationService;

        var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        _configPath   = Path.Combine(configDir, "autoscan.json");
        _settingsPath = Path.Combine(configDir, "settings.json");
    }

    /******************************************************************
     *  IHostedService
     ******************************************************************/

    /// <summary>
    ///     Starts the service: loads config, migrates legacy data, restores queue pause state,
    ///     resumes interrupted queue items from the database, and triggers an immediate scan if enabled.
    /// </summary>
    /// <param name="cancellationToken"> Triggered when the host is starting a shutdown. </param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfig();
        await MigrateSeenFilesIfNeededAsync();
        ScheduleTimer();

        // Wire up folder override resolver for cluster dispatch (avoids circular DI).
        // Local dispatch needs the same resolver so per-folder overrides are applied to
        // the encoder, not just to the queue-time skip ladder.
        _clusterService.FolderOverrideResolver = FindFolderOverride;
        _transcodingService.SetFolderOverrideResolver(FindFolderOverride);

        // Feed library exclusion rules into the transcoding service so manual adds honor
        // the same filename/size/resolution filters the auto-scanner applies.
        _transcodingService.SetExclusionRulesProvider(() =>
        {
            lock (_configLock) { return _config.ExclusionRules; }
        });

        if (_config.QueuePaused)
        {
            _transcodingService.SetPaused(true);
            Console.WriteLine("Queue was paused when app last shut down — staying paused.");
        }

        _ = Task.Run(async () =>
        {
            await ResumeLocalQueueItemsAsync();
            await ResumeRemoteQueueItemsAsync();
        });

        if (_config.Enabled && _config.Directories.Count > 0)
            _ = Task.Run(TriggerScanNowAsync);

        if (_clusterService.IsNodeMode)
            _clusterService.CleanupOldRemoteJobs(24);
    }

    /// <summary> Stops the scan timer. Active scans finish naturally. </summary>
    /// <param name="cancellationToken"> Triggered when the host needs a forced shutdown. </param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <summary> Disposes the scan timer. </summary>
    public void Dispose()
    {
        _timer?.Dispose();
    }

    /******************************************************************
     *  Public Configuration API
     ******************************************************************/

    /// <summary> Returns the current auto-scan configuration. </summary>
    public AutoScanConfig GetConfig() => _config;

    /// <summary>
    ///     Adds a directory to the auto-scan list if it is not already present.
    ///     Normalizes the path before comparison and persists the change immediately.
    /// </summary>
    /// <param name="path"> The directory path to add. </param>
    public void AddDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        lock (_configLock)
        {
            if (!_config.Directories.Any(d => string.Equals(d.Path, normalized, StringComparison.OrdinalIgnoreCase)))
            {
                _config.Directories.Add(new WatchedFolder { Path = normalized });
                SaveConfig();
            }
        }
    }

    /// <summary>
    ///     Removes a directory from the auto-scan list (case-insensitive) and persists the change immediately.
    /// </summary>
    /// <param name="path"> The directory path to remove. </param>
    public void RemoveDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        lock (_configLock)
        {
            _config.Directories.RemoveAll(d => string.Equals(d.Path, normalized, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }
    }

    /// <summary>
    ///     Updates the encoding overrides for an existing watched folder.
    ///     Pass <see langword="null"/> to clear overrides and revert to global defaults.
    /// </summary>
    /// <param name="path"> The watched folder path to update. </param>
    /// <param name="overrides"> The new encoding overrides, or <see langword="null"/> to clear them. </param>
    public void SaveFolderSettings(string path, EncoderOptionsOverride? overrides)
    {
        var normalized = Path.GetFullPath(path);
        lock (_configLock)
        {
            var folder = _config.Directories
                .FirstOrDefault(d => string.Equals(d.Path, normalized, StringComparison.OrdinalIgnoreCase));
            if (folder != null)
            {
                folder.EncodingOverrides = overrides;
                SaveConfig();
            }
        }
    }

    /// <summary>
    ///     Returns the folder-level encoding override for a file path, or null if none.
    ///     Uses longest-prefix matching when a file is under multiple watched folders.
    /// </summary>
    public EncoderOptionsOverride? FindFolderOverride(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        WatchedFolder? best = null;
        int bestLen = -1;

        // Snapshot under the config lock — this is called from scan/dispatch
        // threads while AddDirectory/RemoveDirectory mutate the list on request
        // threads; enumerating the live list races a concurrent add/remove and
        // throws "Collection was modified", failing that dispatch.
        WatchedFolder[] directories;
        lock (_configLock)
        {
            directories = _config.Directories.ToArray();
        }

        foreach (var folder in directories)
        {
            var folderPath = folder.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(folderPath, StringComparison.OrdinalIgnoreCase) && folderPath.Length > bestLen)
            {
                best    = folder;
                bestLen = folderPath.Length;
            }
        }

        return best?.EncodingOverrides;
    }

    /// <summary> Enables or disables automatic scanning and reschedules the timer accordingly. </summary>
    /// <param name="enabled"> Whether auto-scan should be active. </param>
    public void SetEnabled(bool enabled)
    {
        lock (_configLock)
        {
            _config.Enabled = enabled;
            SaveConfig();
        }
        ScheduleTimer();
    }

    /// <summary> Updates the scan interval (minimum 1 minute) and reschedules the timer. </summary>
    /// <param name="minutes"> The new scan interval in minutes. </param>
    public void SetInterval(int minutes)
    {
        if (minutes < 1) minutes = 1;
        lock (_configLock)
        {
            _config.IntervalMinutes = minutes;
            SaveConfig();
        }
        ScheduleTimer();
    }

    /// <summary> Replaces the exclusion rules sub-config and persists the change immediately. </summary>
    /// <param name="rules"> The new exclusion rules to apply. </param>
    public void UpdateExclusionRules(ExclusionRules rules)
    {
        lock (_configLock)
        {
            _config.ExclusionRules = rules;
            SaveConfig();
        }
    }

    /// <summary>
    ///     Returns a snapshot of the current exclusion rules. Threaded services poll through this
    ///     getter instead of holding a direct <see cref="AutoScanService"/> reference (avoids
    ///     the TranscodingService &#8596; AutoScanService DI cycle).
    /// </summary>
    public ExclusionRules GetCurrentExclusionRules()
    {
        lock (_configLock) { return _config.ExclusionRules; }
    }

    /// <summary> Persists the queue paused state to disk so it survives restarts. </summary>
    /// <param name="paused"> Whether the encoding queue should be paused. </param>
    public void SetQueuePaused(bool paused)
    {
        lock (_configLock)
        {
            _config.QueuePaused = paused;
            SaveConfig();
        }
    }

    /// <summary> Whether the encoding queue is currently marked as paused in the persisted config. </summary>
    public bool IsQueuePaused => _config.QueuePaused;

    /// <summary> Manually triggers an immediate scan outside the scheduled interval. </summary>
    public async Task TriggerScanNowAsync()
    {
        await RunScanAsync();
    }

    /// <summary>
    ///     Re-queues a single file using the current global encoder options merged with any
    ///     folder-level overrides — the same options recipe the periodic scan uses. Bypasses
    ///     the "may still be transferring" mtime guard, since this is an explicit user action.
    ///     Used by the retry button so a failed item goes back into the queue immediately
    ///     instead of waiting for the next scheduled scan.
    /// </summary>
    /// <param name="filePath"> Absolute path to the file to add. </param>
    public async Task AddSingleFileAsync(string filePath)
    {
        var globalOptions  = LoadEncoderOptions();
        var folderOverride = FindFolderOverride(filePath);
        var fileOptions    = EncoderOptionsOverride.ApplyOverrides(globalOptions, folderOverride, null);
        await _transcodingService.AddFileAsync(filePath, fileOptions);
    }

    /// <summary>
    ///     Resets all file statuses in the database and clears the last-scan timestamp.
    ///     The next scan will treat all files as new.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        Console.WriteLine("ClearHistory: Starting full state reset...");

        // Cancel any in-flight scan before acquiring the lock to avoid deadlock.
        // RunScanAsync disposes/replaces _scanCts under _scanLock, which we don't
        // hold yet — guard against cancelling a just-disposed CTS, otherwise the
        // ObjectDisposedException propagates to the controller as a 500 AND the
        // in-flight scan keeps running, blocking the WaitAsync below.
        try { _scanCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        await _scanLock.WaitAsync();
        try
        {
            if (_clusterService.IsMasterMode)
                await _clusterService.ClearAllRemoteStateAsync();

            // Kill any active local FFmpeg process and drain the in-memory queue.
            await _transcodingService.ClearAllInMemoryState();

            await _mediaFileRepo.ResetAllStatusesAsync();

            try
            {
                await _mediaFileRepo.ClearAllTransitionsAsync();
            }
            catch { }

            lock (_configLock)
            {
                _config.LastScanTime     = null;
                _config.LastScanNewFiles = 0;
                SaveConfig();
            }

            // Drop the interrupted-sweep checkpoint too — the user just asked for
            // a from-scratch rescan, so resuming mid-tree would silently skip the
            // portion the cancelled sweep had already covered.
            _scanCheckpoint = null;
            try { File.Delete(ScanCheckpointPath); } catch { }

            await _hubContext.Clients.All.SendAsync("HistoryCleared");

            Console.WriteLine("ClearHistory: Full state reset complete");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /******************************************************************
     *  Startup Helpers
     ******************************************************************/

    /// <summary>
    ///     Resumes the local queue on startup. The pending queue lives in the DB now,
    ///     so there is no per-item restore loop — Queued rows hydrate into the
    ///     scheduler's working window on demand. The only real work here is flipping
    ///     rows a crashed run left in Processing back to Queued, then kicking the
    ///     scheduler. Instant regardless of backlog depth (a 500k-row queue used to
    ///     replay one restore per row before any encode could start).
    /// </summary>
    private async Task ResumeLocalQueueItemsAsync()
    {
        if (_clusterService.IsNodeMode)
        {
            Console.WriteLine("Node mode: Skipping queue resume from database");
            return;
        }

        try
        {
            int requeued = await _mediaFileRepo.RequeueOrphanedLocalProcessingAsync();
            int pending  = await _mediaFileRepo.CountQueuedLocalAsync();
            if (requeued > 0)
                Console.WriteLine($"Resume: {requeued} interrupted local encode(s) returned to the queue");
            if (pending == 0)
                return;

            Console.WriteLine($"Resume: {pending} pending item(s) in the DB queue — hydrating on demand");
            _transcodingService.MarkQueueWindowDirty();
            await _transcodingService.KickQueueAsync(LoadEncoderOptions());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Resume: Error resuming local queue: {ex.Message}");
        }
    }

    /// <summary>
    ///     After cluster recovery completes, picks up any remote-assigned items that
    ///     recovery didn't handle (orphans) and re-queues them locally as a safety net.
    /// </summary>
    private async Task ResumeRemoteQueueItemsAsync()
    {
        if (_clusterService.IsNodeMode) return;
        if (!_clusterService.IsMasterMode) return;

        try
        {
            try
            {
                await _clusterService.RecoveryCompleteTask.WaitAsync(TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException)
            {
                Console.WriteLine("Resume: Cluster recovery timed out after 3 minutes — checking for orphaned remote items");
            }

            var options       = LoadEncoderOptions();
            var queued        = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Queued);
            var processing    = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Processing);
            var remoteOrphans = queued.Concat(processing)
                .Where(f => !string.IsNullOrEmpty(f.AssignedNodeId))
                .ToList();

            if (remoteOrphans.Count == 0)
                return;

            Console.WriteLine($"Resume: Checking {remoteOrphans.Count} remote item(s) not recovered by cluster");

            foreach (var file in remoteOrphans)
            {
                if (!File.Exists(file.FilePath))
                {
                    await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                    continue;
                }

                // Clearing the assignment back to Queued returns the row to the DB
                // pending queue — the scheduler hydrates it when it reaches the top.
                Console.WriteLine($"Resume: Orphaned remote item {file.FileName} — clearing assignment and re-queuing");
                await _mediaFileRepo.ClearRemoteAssignmentAsync(file.FilePath, MediaFileStatus.Queued);
            }

            _transcodingService.MarkQueueWindowDirty();
            await _transcodingService.KickQueueAsync(options);

            Console.WriteLine("Resume: Remote orphan check complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Resume: Error checking remote orphans: {ex.Message}");
        }
    }

    /******************************************************************
     *  Timer and Scan Logic
     ******************************************************************/

    /// <summary>
    ///     Stops the existing timer and starts a new one based on the current interval config.
    ///     Does nothing (leaves timer stopped) if auto-scan is disabled.
    /// </summary>
    private void ScheduleTimer()
    {
        _timer?.Dispose();
        if (_config.Enabled)
        {
            var interval = TimeSpan.FromMinutes(_config.IntervalMinutes);
            _timer = new Timer(async _ => await RunScanAsync(), null, interval, interval);
        }
    }

    /// <summary>
    ///     Performs a full directory scan. Enqueues new and changed video files, validates
    ///     any existing <c>[snacks]</c> outputs, and prunes stale database records.
    ///     Skipped in node mode and when a scan is already in progress.
    /// </summary>
    private async Task RunScanAsync()
    {
        if (_clusterService.IsNodeMode)
            return;

        if (!_scanLock.Wait(0))
            return;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        try
        {
            if (!_config.Enabled || _config.Directories.Count == 0)
                return;

            var globalOptions = LoadEncoderOptions();

            // Snapshot the exclusion rules and directory list once per scan — using
            // the same rules for every file in a pass keeps behavior predictable
            // even if the user edits config mid-scan.
            ExclusionRules exclusions;
            List<WatchedFolder> watched;
            lock (_configLock)
            {
                exclusions = _config.ExclusionRules;
                watched    = _config.Directories.ToList();
            }

            // The sweep walks each watched tree in deterministic subdirectory chunks
            // and checkpoints after every chunk, so an interrupted first sweep of a
            // 500k-file library resumes where it stopped instead of re-walking and
            // re-filtering everything. Chunking also bounds memory: only one chunk's
            // file list and DB lookup dictionaries are alive at a time.
            const int DirChunkSize = 200;
            var checkpoint   = LoadScanCheckpoint();
            int newFileCount = 0, totalSeen = 0;

            foreach (var dirEntry in watched)
            {
                ct.ThrowIfCancellationRequested();

                var subdirs = _fileService.RecursivelyFindDirectories(dirEntry.Path);
                subdirs.Sort(StringComparer.Ordinal); // deterministic order across restarts

                int startOffset = 0;
                if (checkpoint != null
                    && checkpoint.CompletedDirs.TryGetValue(dirEntry.Path, out var done)
                    && done > 0 && done < subdirs.Count)
                {
                    startOffset = done;
                    Console.WriteLine($"AutoScan: Resuming {dirEntry.Path} at subdirectory {done:N0}/{subdirs.Count:N0} (interrupted-sweep checkpoint)");
                }

                for (int offset = startOffset; offset < subdirs.Count; offset += DirChunkSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var chunk      = subdirs.GetRange(offset, Math.Min(DirChunkSize, subdirs.Count - offset));
                    var chunkFiles = _fileService.GetAllMediaFiles(chunk).Select(t => t.Path).ToList();
                    totalSeen     += chunkFiles.Count;

                    if (chunkFiles.Count > 0)
                    {
                        var chunkDirs      = chunkFiles.Select(f => Path.GetDirectoryName(f) ?? "").Distinct().ToList();
                        var knownPaths     = await _mediaFileRepo.GetFileInfoBatchAsync(chunkDirs);
                        var knownBaseNames = await _mediaFileRepo.GetBaseNameStatusBatchAsync(chunkDirs);

                        var newFiles = new List<string>();
                        foreach (var file in chunkFiles)
                        {
                            ct.ThrowIfCancellationRequested();
                            var normalizedPath = Path.GetFullPath(file);

                            // Cheap exclusion filter — filename and size are available without probing.
                            // Resolution-based exclusion is applied downstream in AddFileAsync (needs probe).
                            long? earlySize = null;
                            try { earlySize = new FileInfo(normalizedPath).Length; } catch { }
                            if (exclusions.IsExcluded(Path.GetFileName(normalizedPath), earlySize, resolutionLabel: null))
                            {
                                Console.WriteLine($"AutoScan: Excluded by library rules: {Path.GetFileName(file)}");
                                continue;
                            }

                            if (knownPaths.TryGetValue(normalizedPath, out var info) &&
                                info.Status is not MediaFileStatus.Unseen)
                            {
                                try
                                {
                                    var fi        = new FileInfo(normalizedPath);
                                    double sizeDelta = info.FileSize > 0
                                        ? Math.Abs(1.0 - (double)fi.Length / info.FileSize)
                                        : 0;

                                    if (sizeDelta > 0.10)
                                    {
                                        Console.WriteLine(
                                            $"AutoScan: File changed on disk: {Path.GetFileName(file)} ({sizeDelta:P0} size change) — re-queuing");
                                        await _mediaFileRepo.ResetFileAsync(normalizedPath);
                                        newFiles.Add(file);
                                    }
                                }
                                catch { }
                                continue;
                            }

                            var dir      = Path.GetDirectoryName(normalizedPath) ?? "";
                            var baseName = Path.GetFileNameWithoutExtension(normalizedPath);
                            var baseKey  = $"{dir}|{baseName}".ToLowerInvariant();
                            if (knownBaseNames.TryGetValue(baseKey, out var baseStatus) &&
                                baseStatus is not MediaFileStatus.Unseen)
                            {
                                continue;
                            }

                            newFiles.Add(file);
                        }

                        // Probe-bound per-file work runs with bounded parallelism (same
                        // policy as the dry-run analyzer): each new file costs at least
                        // one ffprobe, and a sequential first sweep of a big library
                        // took days. Companion-completed marks are collected and flushed
                        // as one batched write per chunk instead of a row-trip each.
                        var companionCompleted = new System.Collections.Concurrent.ConcurrentBag<string>();
                        int chunkAdded = 0;
                        await Parallel.ForEachAsync(
                            newFiles,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
                                CancellationToken = ct,
                            },
                            async (file, token) =>
                            {
                                if (await ProcessDiscoveredFileAsync(file, globalOptions, companionCompleted, token))
                                    Interlocked.Increment(ref chunkAdded);
                            });
                        newFileCount += chunkAdded;

                        if (!companionCompleted.IsEmpty)
                            await _mediaFileRepo.SetStatusBatchAsync(companionCompleted, MediaFileStatus.Completed);
                    }

                    SaveScanCheckpoint(dirEntry.Path, offset + chunk.Count);

                    // Aggregate progress for the UI — one event per chunk, not per file.
                    try
                    {
                        await _hubContext.Clients.All.SendAsync("ScanProgress", new
                        {
                            directory     = dirEntry.Path,
                            processedDirs = Math.Min(offset + chunk.Count, subdirs.Count),
                            totalDirs     = subdirs.Count,
                            queued        = newFileCount,
                            seen          = totalSeen,
                        }, ct);
                    }
                    catch { /* SignalR failures are non-fatal */ }
                }

                ClearScanCheckpoint(dirEntry.Path);
            }

            await _mediaFileRepo.PruneDeletedFilesAsync();

            // Mirror the DB prune into the live work queue: PruneDeletedFiles only
            // touches the database, so a queued/failed WorkItem whose source has
            // since vanished would otherwise linger in memory (and in the queue UI)
            // until the next app restart. Skips active encodes — those have their
            // own missing-source handling on the encode path.
            try { await _transcodingService.PruneMissingWorkItemsAsync(); }
            catch (Exception ex) { Console.WriteLine($"AutoScan: PruneMissingWorkItems failed: {ex.Message}"); }

            lock (_configLock)
            {
                _config.LastScanTime     = DateTime.UtcNow;
                _config.LastScanNewFiles = newFileCount;
                SaveConfig();
            }

            await _hubContext.Clients.All.SendAsync("AutoScanCompleted", newFileCount, totalSeen);

            // Auto-scan only runs on the master, so this is naturally master-only.
            _ = _notificationService.NotifyScanCompletedAsync(newFileCount);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("AutoScan: Scan cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: Error during scan: {ex.Message}");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /******************************************************************
     *  Per-file sweep processing + scan checkpoint
     ******************************************************************/

    /// <summary>
    ///     Processes one newly-discovered file: recently-modified guard, [snacks]
    ///     companion validation (collecting completed marks into
    ///     <paramref name="companionCompleted"/> for a batched write), then
    ///     AddFileAsync under the folder's overrides. Returns true when the file
    ///     was queued. Runs on parallel sweep workers — everything here must be
    ///     safe for concurrent invocation on distinct files.
    /// </summary>
    private async Task<bool> ProcessDiscoveredFileAsync(
        string file, EncoderOptions globalOptions,
        System.Collections.Concurrent.ConcurrentBag<string> companionCompleted,
        CancellationToken ct)
    {
        try
        {
            var lastWrite = File.GetLastWriteTimeUtc(file);
            if (DateTime.UtcNow - lastWrite < TimeSpan.FromMinutes(30))
            {
                Console.WriteLine(
                    $"AutoScan: Skipping {Path.GetFileName(file)}: " +
                    $"modified {(int)(DateTime.UtcNow - lastWrite).TotalMinutes}m ago, may still be transferring");
                return false;
            }
        }
        catch { return false; }

        var dir      = Path.GetDirectoryName(file) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(file);
        // Recognize a [snacks] companion of either kind: a music source's
        // companion is in a music container (.m4a/.mp3/...), a video
        // source's is a video container (.mkv/.mp4/...). The IsVideoFile/
        // IsMusicFile helpers both reject [snacks]-suffixed paths, so
        // probe the extension via GetMediaKind directly here.
        var snacksFiles = Directory.Exists(dir)
            ? Directory.GetFiles(dir, $"{baseName} [snacks].*")
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
                    // Mirror the FileService extension lists. We can't call IsVideoFile/
                    // IsMusicFile because those reject the [snacks] suffix outright.
                    return ext is "mkv" or "mp4" or "ts" or "wmv" or "avi" or "m4v"
                                  or "mpeg" or "mov" or "3gp" or "webm" or "flv"
                                  or "mp3" or "m4a" or "flac" or "aac" or "ogg" or "opus"
                                  or "wav" or "wma" or "alac" or "ape" or "aiff" or "dsf"
                                  or "dff" or "mka" or "mp2";
                })
                .ToList()
            : new List<string>();

        if (snacksFiles.Count > 0)
        {
            // Probe the original ONCE — re-probing it per companion both wasted
            // an ffprobe spawn per file and, worse, meant a failed probe of the
            // ORIGINAL deleted a perfectly good [snacks] companion below.
            ProbeResult? originalProbe = null;
            try
            {
                originalProbe = await _ffprobeService.ProbeAsync(file, ct);
                // ProbeAsync returns an EMPTY result (not an exception) when
                // ffprobe produced unparsable output. For video, duration 0
                // would fail the companion tolerance check and delete valid
                // encodes — treat it the same as an unreadable original.
                // (Music probes legitimately have no video duration; their
                // companion check passes through a duration-agnostic path.)
                if (_fileService.GetMediaKind(file) == MediaKind.Video
                    && _ffprobeService.GetVideoDuration(originalProbe) <= 0)
                    originalProbe = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* original unreadable — leave companions alone, fall through to AddFile */ }

            bool validSnacksExists = false;
            if (originalProbe != null)
            {
                foreach (var snacksFile in snacksFiles)
                {
                    try
                    {
                        var snacksProbe = await _ffprobeService.ProbeAsync(snacksFile, ct);
                        if (_ffprobeService.ConvertedSuccessfully(originalProbe, snacksProbe))
                        {
                            validSnacksExists = true;
                            break;
                        }
                        else
                        {
                            Console.WriteLine($"AutoScan: Partial [snacks] file detected, deleting: {snacksFile}");
                            try { File.Delete(snacksFile); } catch { }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        Console.WriteLine($"AutoScan: Corrupt [snacks] file detected, deleting: {snacksFile}");
                        try { File.Delete(snacksFile); } catch { }
                    }
                }
            }

            if (validSnacksExists)
            {
                companionCompleted.Add(Path.GetFullPath(file));
                return false;
            }
        }

        try
        {
            var folderOverride = FindFolderOverride(file);
            var fileOptions    = EncoderOptionsOverride.ApplyOverrides(globalOptions, folderOverride, null);
            await _transcodingService.AddFileAsync(file, fileOptions, cancellationToken: ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: Failed to add {file}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    ///     Per-watched-directory sweep checkpoint: how many (ordinally sorted)
    ///     subdirectories have been fully processed. Persisted after every chunk so
    ///     an interrupted first sweep resumes instead of re-walking the whole tree.
    /// </summary>
    private sealed class ScanCheckpoint
    {
        public DateTime SavedAt { get; set; } = DateTime.UtcNow;
        public Dictionary<string, int> CompletedDirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private ScanCheckpoint? _scanCheckpoint;

    private string ScanCheckpointPath =>
        Path.Combine(Path.GetDirectoryName(_configPath)!, "scan-checkpoint.json");

    /// <summary> Loads the persisted checkpoint; null when missing, corrupt, or stale (>24h — directory trees drift). </summary>
    private ScanCheckpoint? LoadScanCheckpoint()
    {
        try
        {
            if (!File.Exists(ScanCheckpointPath)) return _scanCheckpoint = null;
            var parsed = JsonSerializer.Deserialize<ScanCheckpoint>(File.ReadAllText(ScanCheckpointPath), _jsonOptions);
            if (parsed == null || DateTime.UtcNow - parsed.SavedAt > TimeSpan.FromHours(24))
                return _scanCheckpoint = null;
            return _scanCheckpoint = parsed;
        }
        catch
        {
            return _scanCheckpoint = null;
        }
    }

    /// <summary> Records that the first <paramref name="completedDirs"/> subdirectories of a watched root are done. </summary>
    private void SaveScanCheckpoint(string watchedPath, int completedDirs)
    {
        try
        {
            _scanCheckpoint ??= new ScanCheckpoint();
            _scanCheckpoint.SavedAt = DateTime.UtcNow;
            _scanCheckpoint.CompletedDirs[watchedPath] = completedDirs;
            File.WriteAllText(ScanCheckpointPath, JsonSerializer.Serialize(_scanCheckpoint, _jsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: failed to persist scan checkpoint: {ex.Message}");
        }
    }

    /// <summary> Clears a watched root's checkpoint after its sweep completes; deletes the file when empty. </summary>
    private void ClearScanCheckpoint(string watchedPath)
    {
        try
        {
            if (_scanCheckpoint == null) return;
            _scanCheckpoint.CompletedDirs.Remove(watchedPath);
            if (_scanCheckpoint.CompletedDirs.Count == 0)
            {
                _scanCheckpoint = null;
                File.Delete(ScanCheckpointPath);
            }
            else
            {
                File.WriteAllText(ScanCheckpointPath, JsonSerializer.Serialize(_scanCheckpoint, _jsonOptions));
            }
        }
        catch { /* best-effort — a stale checkpoint expires after 24h anyway */ }
    }

    /******************************************************************
     *  Config and Migration Helpers
     ******************************************************************/

    /// <summary>
    ///     One-time migration: moves <c>SeenFiles</c> from the old <c>autoscan.json</c> into the
    ///     database, then re-saves the config without the obsolete property.
    /// </summary>
    private async Task MigrateSeenFilesIfNeededAsync()
    {
        if (!File.Exists(_configPath))
            return;

        try
        {
            var json = File.ReadAllText(_configPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("SeenFiles", out var seenFilesElement) &&
                !doc.RootElement.TryGetProperty("seenFiles", out seenFilesElement))
                return;

            var seenFiles = new List<string>();
            foreach (var item in seenFilesElement.EnumerateArray())
            {
                var path = item.GetString();
                if (!string.IsNullOrEmpty(path))
                    seenFiles.Add(path);
            }

            if (seenFiles.Count > 0)
            {
                Console.WriteLine($"AutoScan: Migrating {seenFiles.Count} SeenFiles entries to database...");
                await _mediaFileRepo.BulkInsertSeenFilesAsync(seenFiles);
                Console.WriteLine("AutoScan: Migration complete.");
                SaveConfig();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: SeenFiles migration failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    ///     Loads encoder options from <c>settings.json</c>, returning defaults if the file is
    ///     missing or corrupt.
    /// </summary>
    private EncoderOptions LoadEncoderOptions()
    {
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json   = File.ReadAllText(_settingsPath);
                var parsed = JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions) ?? new EncoderOptions();
                return EnvConfigOverrides.Apply(parsed, EnvConfigOverrides.SettingsPrefix);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AutoScan: Failed to load settings: {ex.Message}");
            }
        }

        return EnvConfigOverrides.Apply(new EncoderOptions(), EnvConfigOverrides.SettingsPrefix);
    }

    /// <summary>
    ///     Loads the auto-scan config from <c>autoscan.json</c>, resetting to defaults on failure.
    /// </summary>
    private void LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            try
            {
                var json = File.ReadAllText(_configPath);
                _config  = JsonSerializer.Deserialize<AutoScanConfig>(json, _jsonOptions) ?? new AutoScanConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AutoScan: Failed to load config: {ex.Message}");
                _config = new AutoScanConfig();
            }
        }

        EnvConfigOverrides.Apply(_config, EnvConfigOverrides.AutoScanPrefix);
    }

    /// <summary>
    ///     Serializes the current auto-scan config to <c>autoscan.json</c>. Properties driven
    ///     by SNACKS_SCAN_* env vars are restored from the on-disk file first (via a deep
    ///     copy, so the live config keeps its env values) — unsetting a var reverts cleanly.
    /// </summary>
    private void SaveConfig()
    {
        try
        {
            var copy = JsonSerializer.Deserialize<AutoScanConfig>(
                JsonSerializer.Serialize(_config, _jsonOptions), _jsonOptions) ?? _config;

            var fileState = new AutoScanConfig();
            if (File.Exists(_configPath))
            {
                try
                {
                    fileState = JsonSerializer.Deserialize<AutoScanConfig>(
                        File.ReadAllText(_configPath), _jsonOptions) ?? fileState;
                }
                catch { /* corrupt file — locked keys fall back to defaults */ }
            }
            EnvConfigOverrides.RestoreLockedValues(copy, fileState, EnvConfigOverrides.AutoScanPrefix);

            File.WriteAllText(_configPath, JsonSerializer.Serialize(copy, _jsonOptions));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: Failed to save config: {ex.Message}");
        }
    }
}
