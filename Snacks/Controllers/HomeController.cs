using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Services;

namespace Snacks.Controllers
{
    public class HomeController : Controller
    {
        private readonly TranscodingService _transcodingService;
        private readonly FileService _fileService;
        private readonly AutoScanService _autoScanService;

        public HomeController(TranscodingService transcodingService, FileService fileService, AutoScanService autoScanService)
        {
            _transcodingService = transcodingService;
            _fileService = fileService;
            _autoScanService = autoScanService;
        }

        public IActionResult Index()
        {
            var workItems = _transcodingService.GetAllWorkItems();
            return View(workItems);
        }

        [HttpGet]
        public IActionResult Health()
        {
            return Json(new { 
                status = "healthy", 
                timestamp = DateTime.UtcNow,
                version = "1.0.0"
            });
        }

        [HttpGet]
        public IActionResult GetAvailableDirectories()
        {
            try
            {
                if (_fileService.AllowAllPaths())
                {
                    // Desktop mode: list available drive roots
                    var directories = DriveInfo.GetDrives()
                        .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                        .Select(d => new
                        {
                            path = d.RootDirectory.FullName,
                            name = d.RootDirectory.FullName,
                            videoCount = 0
                        })
                        .OrderBy(d => d.name)
                        .ToList<object>();

                    return Json(new { directories, rootPath = "" });
                }

                var inputDir = _fileService.GetUploadsDirectory(); // This will be our input/library directory
                var directories2 = new List<object>();

                if (Directory.Exists(inputDir))
                {
                    // Show only top-level directories with recursive video counts
                    var topLevelDirs = Directory.GetDirectories(inputDir)
                        .Select(dir => new
                        {
                            path = dir,
                            name = Path.GetFileName(dir),
                            videoCount = CountVideoFilesRecursive(dir)
                        })
                        .Where(d => d.videoCount > 0)
                        .OrderBy(d => d.name)
                        .ToList();

                    directories2.AddRange(topLevelDirs);
                }

                return Json(new { directories = directories2, rootPath = inputDir });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting directories: {ex}");
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ProcessDirectory([FromBody] ProcessDirectoryRequest request)
        {
            try
            {
                // Add null checks
                if (request == null)
                {
                    return BadRequest("Request is null");
                }

                if (string.IsNullOrEmpty(request.DirectoryPath))
                {
                    return BadRequest("Directory path is required");
                }

                if (request.Options == null)
                {
                    return BadRequest("Encoder options are required");
                }

                if (!Directory.Exists(request.DirectoryPath))
                {
                    return BadRequest($"Directory does not exist: {request.DirectoryPath}");
                }

                // Security check - ensure directory is within allowed paths
                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.DirectoryPath);
                    var fullInputDir = Path.GetFullPath(inputDir);

                    if (!fullRequestPath.StartsWith(fullInputDir))
                    {
                        return BadRequest("Directory is not within allowed library path");
                    }
                }

                var result = await _transcodingService.AddDirectoryAsync(request.DirectoryPath, request.Options, request.Recursive);
                return Json(new { success = true, message = result });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process directory error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ProcessSingleFile([FromBody] ProcessFileRequest request)
        {
            try
            {
                if (request == null)
                {
                    return BadRequest("Request is null");
                }

                if (string.IsNullOrEmpty(request.FilePath))
                {
                    return BadRequest("File path is required");
                }

                if (request.Options == null)
                {
                    return BadRequest("Encoder options are required");
                }

                if (!System.IO.File.Exists(request.FilePath))
                {
                    return BadRequest($"File does not exist: {request.FilePath}");
                }

                // Security check - ensure file is within allowed paths
                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.FilePath);
                    var fullInputDir = Path.GetFullPath(inputDir);

                    if (!fullRequestPath.StartsWith(fullInputDir))
                    {
                        return BadRequest("File is not within allowed library path");
                    }
                }

                var workItemId = await _transcodingService.AddFileAsync(request.FilePath, request.Options);
                return Json(new { success = true, workItemId });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Process file error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> CancelWorkItem(string id)
        {
            try
            {
                await _transcodingService.CancelWorkItemAsync(id);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public IActionResult GetWorkItems(int? limit = null, int skip = 0)
        {
            // Processing first, then pending by highest bitrate, then completed/failed
            var workItems = _transcodingService.GetAllWorkItems();
            workItems.Sort((a, b) =>
            {
                int StatusPriority(WorkItemStatus s) => s switch
                {
                    WorkItemStatus.Processing => 0,
                    WorkItemStatus.Pending => 1,
                    WorkItemStatus.Completed => 2,
                    WorkItemStatus.Failed => 3,
                    WorkItemStatus.Cancelled => 4,
                    _ => 5
                };

                int cmp = StatusPriority(a.Status).CompareTo(StatusPriority(b.Status));
                if (cmp != 0) return cmp;

                // Within same status, sort by bitrate descending
                return b.Bitrate.CompareTo(a.Bitrate);
            });

            var total = workItems.Count;
            workItems = workItems.Skip(skip).ToList();
            if (limit.HasValue)
                workItems = workItems.Take(limit.Value).ToList();
            return Json(new { items = workItems, total });
        }

        [HttpGet]
        public IActionResult GetWorkStats()
        {
            var workItems = _transcodingService.GetAllWorkItems();
            return Json(new
            {
                pending = workItems.Count(w => w.Status == WorkItemStatus.Pending),
                processing = workItems.Count(w => w.Status == WorkItemStatus.Processing),
                completed = workItems.Count(w => w.Status == WorkItemStatus.Completed),
                failed = workItems.Count(w => w.Status == WorkItemStatus.Failed),
                total = workItems.Count
            });
        }

        [HttpGet]
        public IActionResult GetWorkItem(string id)
        {
            var workItem = _transcodingService.GetWorkItem(id);
            if (workItem == null)
            {
                return NotFound();
            }
            return Json(workItem);
        }

        [HttpGet]
        public IActionResult GetSubdirectories(string directoryPath)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                    return BadRequest("Directory does not exist");

                // Security check — in container mode, only allow browsing within uploads
                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(directoryPath);
                    var fullInputDir = Path.GetFullPath(inputDir);
                    if (!fullRequestPath.StartsWith(fullInputDir))
                        return BadRequest("Directory is not within allowed library path");
                }

                var dirs = Directory.GetDirectories(directoryPath)
                    .Select(d => new
                    {
                        path = d,
                        name = Path.GetFileName(d)
                    })
                    .OrderBy(d => d.name)
                    .ToArray();

                return Json(new { directories = dirs, parentPath = directoryPath });
            }
            catch (UnauthorizedAccessException)
            {
                return Json(new { directories = Array.Empty<object>(), parentPath = directoryPath });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public IActionResult GetDirectoryFiles(string directoryPath, bool recursive = true)
        {
            try
            {
                if (!Directory.Exists(directoryPath))
                {
                    return BadRequest("Directory does not exist");
                }

                // Security check
                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(directoryPath);
                    var fullInputDir = Path.GetFullPath(inputDir);

                    if (!fullRequestPath.StartsWith(fullInputDir))
                    {
                        return BadRequest("Directory is not within allowed library path");
                    }
                }

                // Get video files — recursive for NAS mode, shallow for desktop browsing
                var allDirs = new List<string> { directoryPath };
                if (recursive)
                    allDirs.AddRange(Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories));

                var relativeRoot = _fileService.AllowAllPaths() ? directoryPath : _fileService.GetUploadsDirectory();
                var videoFiles = _fileService.GetAllVideoFiles(allDirs)
                    .Select(f => new
                    {
                        path = f,
                        name = Path.GetFileName(f),
                        size = new FileInfo(f).Length,
                        modified = new FileInfo(f).LastWriteTime,
                        relativePath = Path.GetRelativePath(relativeRoot, f)
                    })
                    .OrderBy(f => f.relativePath)
                    .ToArray();

                return Json(new { files = videoFiles });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Get directory files error: {ex}");
                return BadRequest(ex.Message);
            }
        }

        private int CountVideoFilesRecursive(string directory)
        {
            try
            {
                var allDirs = new List<string> { directory };
                allDirs.AddRange(Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));
                return _fileService.GetAllVideoFiles(allDirs).Count;
            }
            catch
            {
                return 0;
            }
        }

        private string GetSettingsPath()
        {
            var configDir = Path.Combine(_fileService.GetWorkingDirectory(), "config");
            if (!Directory.Exists(configDir))
                Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, "settings.json");
        }

        [HttpGet]
        public IActionResult GetSettings()
        {
            var settingsPath = GetSettingsPath();
            if (System.IO.File.Exists(settingsPath))
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                return Content(json, "application/json");
            }
            return Json(new { });
        }

        [HttpPost]
        public IActionResult SaveSettings([FromBody] object settings)
        {
            try
            {
                var settingsPath = GetSettingsPath();
                var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                System.IO.File.WriteAllText(settingsPath, json);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public IActionResult GetAutoScanConfig()
        {
            return Json(_autoScanService.GetConfig());
        }

        [HttpPost]
        public IActionResult SetAutoScanEnabled([FromBody] AutoScanEnabledRequest request)
        {
            try
            {
                _autoScanService.SetEnabled(request.Enabled);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult AddAutoScanDirectory([FromBody] AutoScanDirectoryRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.Path))
                    return BadRequest("Path is required");

                if (!Directory.Exists(request.Path))
                    return BadRequest($"Directory does not exist: {request.Path}");

                // Security check - ensure directory is within allowed paths
                if (!_fileService.AllowAllPaths())
                {
                    var inputDir = _fileService.GetUploadsDirectory();
                    var fullRequestPath = Path.GetFullPath(request.Path);
                    var fullInputDir = Path.GetFullPath(inputDir);

                    if (!fullRequestPath.StartsWith(fullInputDir))
                        return BadRequest("Directory is not within allowed library path");
                }

                _autoScanService.AddDirectory(request.Path);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult RemoveAutoScanDirectory([FromBody] AutoScanDirectoryRequest request)
        {
            try
            {
                _autoScanService.RemoveDirectory(request.Path);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult SetAutoScanInterval([FromBody] AutoScanIntervalRequest request)
        {
            try
            {
                if (request.IntervalMinutes < 1 || request.IntervalMinutes > 1440)
                    return BadRequest("Interval must be between 1 and 1440 minutes");

                _autoScanService.SetInterval(request.IntervalMinutes);
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult TriggerAutoScan()
        {
            try
            {
                _autoScanService.TriggerScanNow();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult ClearAutoScanHistory()
        {
            try
            {
                _autoScanService.ClearHistory();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpPost]
        public IActionResult SetPaused([FromBody] PauseRequest request)
        {
            try
            {
                _transcodingService.SetPaused(request.Paused);
                return Json(new { success = true, paused = _transcodingService.IsPaused });
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet]
        public IActionResult GetPausedState()
        {
            return Json(new { paused = _transcodingService.IsPaused });
        }

        public IActionResult Error()
        {
            return View();
        }
    }

    public class ProcessDirectoryRequest
    {
        public string DirectoryPath { get; set; } = "";
        public bool Recursive { get; set; } = true;
        public EncoderOptions Options { get; set; } = new();
    }

    public class ProcessFileRequest
    {
        public string FilePath { get; set; } = "";
        public EncoderOptions Options { get; set; } = new();
    }

    public class AutoScanEnabledRequest
    {
        public bool Enabled { get; set; }
    }

    public class AutoScanDirectoryRequest
    {
        public string Path { get; set; } = "";
    }

    public class AutoScanIntervalRequest
    {
        public int IntervalMinutes { get; set; }
    }

    public class PauseRequest
    {
        public bool Paused { get; set; }
    }
}