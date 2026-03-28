using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Text.Json;

namespace Snacks.Services
{
    public class AutoScanService : IHostedService, IDisposable
    {
        private readonly FileService _fileService;
        private readonly TranscodingService _transcodingService;
        private readonly IHubContext<TranscodingHub> _hubContext;
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

        public AutoScanService(FileService fileService, TranscodingService transcodingService, IHubContext<TranscodingHub> hubContext)
        {
            _fileService = fileService;
            _transcodingService = transcodingService;
            _hubContext = hubContext;

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
            ScheduleTimer();

            // Run an immediate scan on startup if enabled, so files pending
            // from before a restart get re-queued without waiting for the interval
            if (_config.Enabled && _config.Directories.Count > 0)
            {
                _ = Task.Run(() => TriggerScanNow());
            }

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

        public async Task TriggerScanNow()
        {
            await RunScan();
        }

        public void ClearHistory()
        {
            _config.SeenFiles.Clear();
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

                // A file is "seen" if:
                // 1. Its exact path is in SeenFiles, OR
                // 2. Its base name (without extension) matches a seen file's base name in the same directory
                //    (handles format changes like movie.mp4 → movie.mkv after delete-original)
                var seenBaseNames = new HashSet<string>(
                    _config.SeenFiles.Select(f => Path.Combine(
                        Path.GetDirectoryName(f) ?? "",
                        Path.GetFileNameWithoutExtension(f))),
                    StringComparer.OrdinalIgnoreCase);

                var newFiles = allVideoFiles.Where(f =>
                    !_config.SeenFiles.Contains(f) &&
                    !seenBaseNames.Contains(Path.Combine(
                        Path.GetDirectoryName(f) ?? "",
                        Path.GetFileNameWithoutExtension(f)))
                ).ToList();

                int newFileCount = 0;
                foreach (var file in newFiles)
                {
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

                // Only mark files as seen if they were skipped by AddFileAsync
                // (already meet requirements) or have a [snacks] output.
                // Files that were queued for encoding are NOT marked — if the
                // container restarts before they finish, the next scan re-queues them.
                foreach (var file in allVideoFiles)
                {
                    // File was already in the queue/completed from a prior scan
                    if (_config.SeenFiles.Contains(file))
                        continue;

                    // Check if a [snacks] encoded version already exists
                    var dir = Path.GetDirectoryName(file) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    var snacksExists = Directory.GetFiles(dir, $"{baseName} [snacks].*")
                        .Any(f => _fileService.IsVideoFile(f) || f.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));

                    if (snacksExists)
                    {
                        _config.SeenFiles.Add(file);
                        continue;
                    }

                    // Check if AddFileAsync skipped it (not in the work queue = was filtered out)
                    if (!_transcodingService.IsFileQueued(file))
                    {
                        _config.SeenFiles.Add(file);
                    }
                    // else: file is in the queue — don't mark as seen until it completes
                }

                // Prune SeenFiles entries where the file no longer exists on disk,
                // but first check if a replacement file exists (delete-original renames the output).
                // Add the replacement path so the file stays "seen" after pruning.
                var toAdd = new List<string>();
                foreach (var seenFile in _config.SeenFiles.Where(f => !File.Exists(f)).ToList())
                {
                    var dir = Path.GetDirectoryName(seenFile) ?? "";
                    var baseName = Path.GetFileNameWithoutExtension(seenFile);
                    // Look for any video file with the same base name (different extension = format change)
                    var replacement = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, $"{baseName}.*")
                            .FirstOrDefault(f => _fileService.IsVideoFile(f))
                        : null;
                    if (replacement != null)
                        toAdd.Add(replacement);
                }
                foreach (var f in toAdd)
                    _config.SeenFiles.Add(f);

                _config.SeenFiles.RemoveWhere(f => !File.Exists(f));

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
