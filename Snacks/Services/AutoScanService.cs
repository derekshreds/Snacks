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
    private readonly FileService         _fileService;
    private readonly FfprobeService      _ffprobeService;
    private readonly TranscodingService  _transcodingService;
    private readonly MediaFileRepository _mediaFileRepo;
    private readonly IHubContext<TranscodingHub> _hubContext;
    private readonly ClusterService      _clusterService;
    private readonly SemaphoreSlim       _scanLock = new(1, 1);
    private readonly string              _configPath;
    private readonly string              _settingsPath;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private AutoScanConfig _config = new();
    private Timer?         _timer;

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
        ClusterService clusterService)
    {
        _fileService        = fileService;
        _ffprobeService     = ffprobeService;
        _transcodingService = transcodingService;
        _mediaFileRepo      = mediaFileRepo;
        _hubContext         = hubContext;
        _clusterService     = clusterService;

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
    /// <param name="cancellationToken">Triggered when the host is starting a shutdown.</param>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        LoadConfig();
        MigrateSeenFilesIfNeeded();
        ScheduleTimer();

        if (_config.QueuePaused)
        {
            _transcodingService.SetPaused(true);
            Console.WriteLine("Queue was paused when app last shut down — staying paused.");
        }

        _ = Task.Run(ResumeQueueFromDatabaseAsync);

        if (_config.Enabled && _config.Directories.Count > 0)
            _ = Task.Run(TriggerScanNowAsync);

        if (_clusterService.IsNodeMode)
            _clusterService.CleanupAllRemoteJobs();

        return Task.CompletedTask;
    }

    /// <summary> Stops the scan timer. Active scans finish naturally. </summary>
    /// <param name="cancellationToken">Triggered when the host needs a forced shutdown.</param>
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
    /// <param name="path">The directory path to add.</param>
    public void AddDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!_config.Directories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            _config.Directories.Add(normalized);
            SaveConfig();
        }
    }

    /// <summary>
    ///     Removes a directory from the auto-scan list (case-insensitive) and persists the change immediately.
    /// </summary>
    /// <param name="path">The directory path to remove.</param>
    public void RemoveDirectory(string path)
    {
        var normalized = Path.GetFullPath(path);
        _config.Directories.RemoveAll(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
        SaveConfig();
    }

    /// <summary> Enables or disables automatic scanning and reschedules the timer accordingly. </summary>
    /// <param name="enabled">Whether auto-scan should be active.</param>
    public void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        SaveConfig();
        ScheduleTimer();
    }

    /// <summary> Updates the scan interval (minimum 1 minute) and reschedules the timer. </summary>
    /// <param name="minutes">The new scan interval in minutes.</param>
    public void SetInterval(int minutes)
    {
        if (minutes < 1) minutes = 1;
        _config.IntervalMinutes = minutes;
        SaveConfig();
        ScheduleTimer();
    }

    /// <summary> Persists the queue paused state to disk so it survives restarts. </summary>
    /// <param name="paused">Whether the encoding queue should be paused.</param>
    public void SetQueuePaused(bool paused)
    {
        _config.QueuePaused = paused;
        SaveConfig();
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
        await _mediaFileRepo.ResetAllStatusesAsync();
        _config.LastScanTime     = null;
        _config.LastScanNewFiles = 0;
        SaveConfig();
    }

    /******************************************************************
     *  Startup Helpers
     ******************************************************************/

    /// <summary>
    ///     Restores the queue from the database on startup. Files that were Queued or Processing
    ///     when the app last shut down get re-added to the in-memory queue.
    /// </summary>
    private async Task ResumeQueueFromDatabaseAsync()
    {
        if (_clusterService.IsNodeMode)
        {
            Console.WriteLine("Node mode: Skipping queue resume from database");
            return;
        }

        try
        {
            if (_clusterService.IsMasterMode)
            {
                try
                {
                    await _clusterService.RecoveryCompleteTask.WaitAsync(TimeSpan.FromMinutes(3));
                }
                catch (TimeoutException)
                {
                    Console.WriteLine("Resume: Cluster recovery timed out after 3 minutes — proceeding");
                }
            }

            var options    = LoadEncoderOptions();
            var queued     = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Queued);
            var processing = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Processing);
            var toResume   = queued.Concat(processing).ToList();

            if (toResume.Count == 0)
                return;

            Console.WriteLine($"Resuming {toResume.Count} items from database...");

            foreach (var file in toResume)
            {
                if (!string.IsNullOrEmpty(file.AssignedNodeId))
                {
                    Console.WriteLine($"Resume: Skipping {file.FileName} — assigned to remote node {file.AssignedNodeName}");
                    continue;
                }

                if (!File.Exists(file.FilePath))
                {
                    await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                    continue;
                }

                await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);

                try
                {
                    await _transcodingService.AddFileAsync(file.FilePath, options);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Resume: Failed to re-add {file.FileName}: {ex.Message}");
                }
            }

            Console.WriteLine("Queue resume complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Resume: Error restoring queue: {ex.Message}");
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

        try
        {
            if (!_config.Enabled && _config.Directories.Count == 0)
                return;

            var options = LoadEncoderOptions();

            var allVideoFiles = new List<string>();
            foreach (var dir in _config.Directories)
            {
                var directories = _fileService.RecursivelyFindDirectories(dir);
                var files       = _fileService.GetAllVideoFiles(directories);
                allVideoFiles.AddRange(files);
            }

            var scannedDirs = allVideoFiles
                .Select(f => Path.GetDirectoryName(f) ?? "")
                .Distinct()
                .ToList();
            var knownPaths     = await _mediaFileRepo.GetFileInfoBatchAsync(scannedDirs);
            var knownBaseNames = await _mediaFileRepo.GetBaseNameStatusBatchAsync(scannedDirs);

            var newFiles = new List<string>();
            foreach (var file in allVideoFiles)
            {
                var normalizedPath = Path.GetFullPath(file);

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

                var dir        = Path.GetDirectoryName(file) ?? "";
                var baseName   = Path.GetFileNameWithoutExtension(file);
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
                            var originalProbe = await _ffprobeService.ProbeAsync(file);
                            var snacksProbe   = await _ffprobeService.ProbeAsync(snacksFile);
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
                    await _transcodingService.AddFileAsync(file, options);
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
    private void MigrateSeenFilesIfNeeded()
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
                _mediaFileRepo.BulkInsertSeenFilesAsync(seenFiles).GetAwaiter().GetResult();
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
