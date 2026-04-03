using Microsoft.AspNetCore.SignalR;
using Snacks.Data;
using Snacks.Hubs;
using Snacks.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snacks.Services
{
    public class AutoScanService : IHostedService, IDisposable
    {
        private readonly FileService _fileService;
        private readonly FfprobeService _ffprobeService;
        private readonly TranscodingService _transcodingService;
        private readonly MediaFileRepository _mediaFileRepo;
        private readonly IHubContext<TranscodingHub> _hubContext;
        private readonly ClusterService _clusterService;
        private readonly SemaphoreSlim _scanLock = new(1, 1);
        private readonly string _configPath;
        private readonly string _settingsPath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private AutoScanConfig _config = new();
        private Timer? _timer;

        public AutoScanService(FileService fileService, FfprobeService ffprobeService,
            TranscodingService transcodingService, MediaFileRepository mediaFileRepo,
            IHubContext<TranscodingHub> hubContext, ClusterService clusterService)
        {
            _fileService = fileService;
            _ffprobeService = ffprobeService;
            _transcodingService = transcodingService;
            _mediaFileRepo = mediaFileRepo;
            _hubContext = hubContext;
            _clusterService = clusterService;

            var workDir = _fileService.GetWorkingDirectory();
            var configDir = Path.Combine(workDir, "config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);

            _configPath = Path.Combine(configDir, "autoscan.json");
            _settingsPath = Path.Combine(configDir, "settings.json");
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            LoadConfig();
            MigrateSeenFilesIfNeeded();
            ScheduleTimer();

            // Restore pause state from last session
            if (_config.QueuePaused)
            {
                _transcodingService.SetPaused(true);
                Console.WriteLine("Queue was paused when app last shut down — staying paused.");
            }

            // Resume any items that were queued or mid-encode when the app last shut down
            // (skipped in node mode by ResumeQueueFromDatabaseAsync guard)
            _ = Task.Run(ResumeQueueFromDatabaseAsync);

            // Run an immediate scan on startup if enabled, so files pending
            // from before a restart get re-queued without waiting for the interval
            // (skipped in node mode by RunScan guard)
            if (_config.Enabled && _config.Directories.Count > 0)
            {
                _ = Task.Run(() => TriggerScanNow());
            }

            // In node mode, also clean up orphaned remote job files
            if (_clusterService.IsNodeMode)
                _clusterService.CleanupAllRemoteJobs();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }

        public AutoScanConfig GetConfig()
        {
            return _config;
        }

        public void AddDirectory(string path)
        {
            var normalized = Path.GetFullPath(path);
            if (!_config.Directories.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                _config.Directories.Add(normalized);
                SaveConfig();
            }
        }

        public void RemoveDirectory(string path)
        {
            var normalized = Path.GetFullPath(path);
            _config.Directories.RemoveAll(d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
            SaveConfig();
        }

        public void SetEnabled(bool enabled)
        {
            _config.Enabled = enabled;
            SaveConfig();
            ScheduleTimer();
        }

        public void SetInterval(int minutes)
        {
            if (minutes < 1) minutes = 1;
            _config.IntervalMinutes = minutes;
            SaveConfig();
            ScheduleTimer();
        }

        public void SetQueuePaused(bool paused)
        {
            _config.QueuePaused = paused;
            SaveConfig();
        }

        public bool IsQueuePaused => _config.QueuePaused;

        public async Task TriggerScanNow()
        {
            await RunScan();
        }

        /// <summary>
        /// Restores the queue from the database on startup. Files that were Queued or Processing
        /// when the app last shut down get re-added to the in-memory queue.
        /// </summary>
        private async Task ResumeQueueFromDatabaseAsync()
        {
            // In node mode, don't resume local queue
            if (_clusterService.IsNodeMode)
            {
                Console.WriteLine("Node mode: Skipping queue resume from database");
                return;
            }

            try
            {
                // Wait for cluster recovery to finish so we don't re-queue files it's handling
                if (_clusterService.IsMasterMode)
                {
                    try { await _clusterService.RecoveryCompleteTask.WaitAsync(TimeSpan.FromMinutes(3)); }
                    catch (TimeoutException) { Console.WriteLine("Resume: Cluster recovery timed out after 3 minutes — proceeding"); }
                }

                var options = LoadEncoderOptions();
                var queued = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Queued);
                var processing = await _mediaFileRepo.GetFilesWithStatusAsync(MediaFileStatus.Processing);
                var toResume = queued.Concat(processing).ToList();

                if (toResume.Count == 0)
                    return;

                Console.WriteLine($"Resuming {toResume.Count} items from database...");

                foreach (var file in toResume)
                {
                    // Skip files assigned to a remote node — cluster recovery handles those
                    if (!string.IsNullOrEmpty(file.AssignedNodeId))
                    {
                        Console.WriteLine($"Resume: Skipping {file.FileName} — assigned to remote node {file.AssignedNodeName}");
                        continue;
                    }

                    if (!File.Exists(file.FilePath))
                    {
                        // File no longer exists — mark as unseen so it gets pruned
                        await _mediaFileRepo.SetStatusAsync(file.FilePath, MediaFileStatus.Unseen);
                        continue;
                    }

                    // Reset to Unseen so AddFileAsync can pick it up fresh
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

        public async Task ClearHistoryAsync()
        {
            await _mediaFileRepo.ResetAllStatusesAsync();
            _config.LastScanTime = null;
            _config.LastScanNewFiles = 0;
            SaveConfig();
        }

        private void ScheduleTimer()
        {
            _timer?.Dispose();
            if (_config.Enabled)
            {
                var interval = TimeSpan.FromMinutes(_config.IntervalMinutes);
                _timer = new Timer(async _ => await RunScan(), null, interval, interval);
            }
        }

        private async Task RunScan()
        {
            // In node mode, don't scan for local files
            if (_clusterService.IsNodeMode)
                return;

            if (!_scanLock.Wait(0))
                return; // Already scanning

            try
            {
                if (!_config.Enabled && _config.Directories.Count == 0)
                    return;

                var options = LoadEncoderOptions();

                var allVideoFiles = new List<string>();
                foreach (var dir in _config.Directories)
                {
                    var directories = _fileService.RecursivelyFindDirectories(dir);
                    var files = _fileService.GetAllVideoFiles(directories);
                    allVideoFiles.AddRange(files);
                }

                // Load all known file info from DB in one batch for performance
                var scannedDirs = allVideoFiles
                    .Select(f => Path.GetDirectoryName(f) ?? "")
                    .Distinct()
                    .ToList();
                var knownPaths = await _mediaFileRepo.GetFileInfoBatchAsync(scannedDirs);
                var knownBaseNames = await _mediaFileRepo.GetBaseNameStatusBatchAsync(scannedDirs);

                var newFiles = new List<string>();
                foreach (var file in allVideoFiles)
                {
                    var normalizedPath = Path.GetFullPath(file);

                    // Check by exact path
                    if (knownPaths.TryGetValue(normalizedPath, out var info) &&
                        info.Status is not MediaFileStatus.Unseen)
                    {
                        // Change detection: if size changed significantly, file was likely replaced.
                        // Small changes (metadata edits, remux) are ignored.
                        try
                        {
                            var fi = new FileInfo(normalizedPath);
                            double sizeDelta = info.FileSize > 0 ? Math.Abs(1.0 - (double)fi.Length / info.FileSize) : 0;
                            if (sizeDelta > 0.10) // >10% size change
                            {
                                Console.WriteLine($"AutoScan: File changed on disk: {Path.GetFileName(file)} ({sizeDelta:P0} size change) — re-queuing");
                                await _mediaFileRepo.ResetFileAsync(normalizedPath);
                                newFiles.Add(file);
                            }
                        }
                        catch { }
                        continue;
                    }

                    // Check by base name in same directory (handles extension changes after replace-original)
                    var dir = Path.GetDirectoryName(normalizedPath) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(normalizedPath);
                    var baseKey = $"{dir}|{baseName}".ToLowerInvariant();
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
                    // Skip files that were recently modified — they may still be transferring
                    try
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (DateTime.UtcNow - lastWrite < TimeSpan.FromMinutes(30))
                        {
                            Console.WriteLine($"AutoScan: Skipping {Path.GetFileName(file)}: modified {(int)(DateTime.UtcNow - lastWrite).TotalMinutes}m ago, may still be transferring");
                            continue;
                        }
                    }
                    catch { continue; }

                    // Before queueing, check if a [snacks] file already exists and validate it
                    var dir = Path.GetDirectoryName(file) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var snacksFiles = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, $"{baseName} [snacks].*")
                            .Where(f => _fileService.IsVideoFile(f))
                            .ToList()
                        : new List<string>();

                    if (snacksFiles.Count > 0)
                    {
                        // Validate the [snacks] file — it may be a partial from an interrupted encode
                        bool validSnacksExists = false;
                        foreach (var snacksFile in snacksFiles)
                        {
                            try
                            {
                                var originalProbe = await _ffprobeService.ProbeAsync(file);
                                var snacksProbe = await _ffprobeService.ProbeAsync(snacksFile);
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
                            // Mark as completed in DB — valid output already exists
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

                // Prune DB entries for files that no longer exist on disk
                await _mediaFileRepo.PruneDeletedFilesAsync();

                _config.LastScanTime = DateTime.UtcNow;
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

        /// <summary>
        /// One-time migration: moves SeenFiles from the old autoscan.json into the database.
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

                    // Re-save config without SeenFiles (the property no longer exists on the model)
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AutoScan: SeenFiles migration failed (non-fatal): {ex.Message}");
            }
        }

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

        private void LoadConfig()
        {
            if (File.Exists(_configPath))
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AutoScanConfig>(json, _jsonOptions) ?? new AutoScanConfig();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"AutoScan: Failed to load config: {ex.Message}");
                    _config = new AutoScanConfig();
                }
            }
        }

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
}
