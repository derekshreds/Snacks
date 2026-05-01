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

        foreach (var folder in _config.Directories)
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
    ///     Resets all file statuses in the database and clears the last-scan timestamp.
    ///     The next scan will treat all files as new.
    /// </summary>
    public async Task ClearHistoryAsync()
    {
        Console.WriteLine("ClearHistory: Starting full state reset...");

        // Cancel any in-flight scan before acquiring the lock to avoid deadlock.
        _scanCts?.Cancel();

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

            _config.LastScanTime     = null;
            _config.LastScanNewFiles = 0;
            SaveConfig();

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
    ///     Immediately restores local (non-remote) queue items on startup. Runs without
    ///     waiting for cluster recovery so the UI shows the pending queue right away.
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
            var options    = LoadEncoderOptions();
            var queued     = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Queued);
            var processing = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Processing);
            var localItems = queued.Concat(processing)
                .Where(f => string.IsNullOrEmpty(f.AssignedNodeId))
                .ToList();

            if (localItems.Count == 0)
                return;

            Console.WriteLine($"Resume: Immediately restoring {localItems.Count} local item(s) to queue (no re-probe)");

            foreach (var file in localItems)
            {
                if (!File.Exists(file.FilePath))
                {
                    await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                    continue;
                }

                try
                {
                    var id = await _transcodingService.RestoreToQueueAsync(file, options);
                    if (id == null)
                        await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Resume: Failed to re-add {file.FileName}: {ex.Message}");
                }
            }

            Console.WriteLine("Resume: Local queue restore complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Resume: Error restoring local queue: {ex.Message}");
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

                Console.WriteLine($"Resume: Orphaned remote item {file.FileName} — clearing assignment and re-queuing");
                await _mediaFileRepo.ClearRemoteAssignmentAsync(file.FilePath, MediaFileStatus.Unseen);

                try
                {
                    var id = await _transcodingService.RestoreToQueueAsync(file, options);
                    if (id == null)
                        await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Resume: Failed to re-add {file.FileName}: {ex.Message}");
                }
            }

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

            var allVideoFiles = new List<string>();
            foreach (var dir in _config.Directories)
            {
                var directories = _fileService.RecursivelyFindDirectories(dir.Path);
                var files       = _fileService.GetAllVideoFiles(directories);
                allVideoFiles.AddRange(files);
            }

            var scannedDirs = allVideoFiles
                .Select(f => Path.GetDirectoryName(f) ?? "")
                .Distinct()
                .ToList();
            var knownPaths     = await _mediaFileRepo.GetFileInfoBatchAsync(scannedDirs);
            var knownBaseNames = await _mediaFileRepo.GetBaseNameStatusBatchAsync(scannedDirs);

            // Snapshot the exclusion rules once per scan — using the same rules for every file
            // in a pass keeps behavior predictable even if the user edits exclusions mid-scan.
            ExclusionRules exclusions;
            lock (_configLock) { exclusions = _config.ExclusionRules; }

            var newFiles = new List<string>();
            foreach (var file in allVideoFiles)
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

            int newFileCount = 0;
            foreach (var file in newFiles)
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (DateTime.UtcNow - lastWrite < TimeSpan.FromMinutes(30))
                    {
                        Console.WriteLine(
                            $"AutoScan: Skipping {Path.GetFileName(file)}: " +
                            $"modified {(int)(DateTime.UtcNow - lastWrite).TotalMinutes}m ago, may still be transferring");
                        continue;
                    }
                }
                catch { continue; }

                var dir         = Path.GetDirectoryName(file) ?? "";
                var baseName    = Path.GetFileNameWithoutExtension(file);
                var snacksFiles = Directory.Exists(dir)
                    ? Directory.GetFiles(dir, $"{baseName} [snacks].*")
                        .Where(f => _fileService.IsVideoFile(f))
                        .ToList()
                    : new List<string>();

                if (snacksFiles.Count > 0)
                {
                    bool validSnacksExists = false;
                    foreach (var snacksFile in snacksFiles)
                    {
                        try
                        {
                            var originalProbe = await _ffprobeService.ProbeAsync(file, ct);
                            var snacksProbe   = await _ffprobeService.ProbeAsync(snacksFile, ct);
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
                        catch
                        {
                            Console.WriteLine($"AutoScan: Corrupt [snacks] file detected, deleting: {snacksFile}");
                            try { File.Delete(snacksFile); } catch { }
                        }
                    }

                    if (validSnacksExists)
                    {
                        await _mediaFileRepo.SetStatusAsync(Path.GetFullPath(file), MediaFileStatus.Completed);
                        continue;
                    }
                }

                try
                {
                    var folderOverride = FindFolderOverride(file);
                    var fileOptions    = EncoderOptionsOverride.ApplyOverrides(globalOptions, folderOverride, null);
                    await _transcodingService.AddFileAsync(file, fileOptions, cancellationToken: ct);
                    newFileCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AutoScan: Failed to add {file}: {ex.Message}");
                }
            }

            await _mediaFileRepo.PruneDeletedFilesAsync();

            _config.LastScanTime     = DateTime.UtcNow;
            _config.LastScanNewFiles = newFileCount;
            SaveConfig();

            await _hubContext.Clients.All.SendAsync("AutoScanCompleted", newFileCount, allVideoFiles.Count);

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
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<EncoderOptions>(json, _jsonOptions) ?? new EncoderOptions();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AutoScan: Failed to load settings: {ex.Message}");
            }
        }

        return new EncoderOptions();
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
    }

    /// <summary> Serializes the current auto-scan config to <c>autoscan.json</c>. </summary>
    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_config, _jsonOptions);
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"AutoScan: Failed to save config: {ex.Message}");
        }
    }
}
