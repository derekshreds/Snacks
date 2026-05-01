using Microsoft.AspNetCore.Mvc;
using Snacks.Models;
using Snacks.Models.Requests;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Directory/file browsing and queue-addition endpoints backing the library modal.
/// </summary>
[Route("api/library")]
[ApiController]
public sealed class LibraryController : ControllerBase
{
    private readonly FileService        _fileService;
    private readonly TranscodingService _transcodingService;
    private readonly AutoScanService    _autoScanService;

    public LibraryController(
        FileService fileService,
        TranscodingService transcodingService,
        AutoScanService autoScanService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(autoScanService);
        _fileService        = fileService;
        _transcodingService = transcodingService;
        _autoScanService    = autoScanService;
    }

    /******************************************************************
     *  Directory Browsing
     ******************************************************************/

    /// <summary>
    ///     Returns the top-level library directories available for browsing. In desktop mode
    ///     all ready drives are returned; in container mode only subdirectories of the
    ///     configured uploads root are returned.
    /// </summary>
    [HttpGet("directories")]
    public IActionResult GetAvailableDirectories()
    {
        try
        {
            if (_fileService.AllowAllPaths())
            {
                var directories = DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    .Select(d => new
                    {
                        path       = d.RootDirectory.FullName,
                        name       = d.RootDirectory.FullName,
                        videoCount = 0
                    })
                    .OrderBy(d => d.name)
                    .ToList<object>();
                return new JsonResult(new { directories, rootPath = "" });
            }

            var inputDir     = _fileService.GetUploadsDirectory();
            var directories2 = new List<object>();

            if (Directory.Exists(inputDir))
            {
                var topLevelDirs = Directory.GetDirectories(inputDir)
                    .Select(dir => new
                    {
                        path       = dir,
                        name       = Path.GetFileName(dir),
                        videoCount = 0
                    })
                    .OrderBy(d => d.name)
                    .ToList();
                directories2.AddRange(topLevelDirs);
            }

            return new JsonResult(new { directories = directories2, rootPath = inputDir });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting directories: {ex}");
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Returns the immediate subdirectories of the given path, along with the parent path
    ///     for up-navigation (clamped to the library root in container mode).
    /// </summary>
    /// <param name="directoryPath"> The directory to list subdirectories for. </param>
    [HttpGet("subdirectories")]
    public IActionResult GetSubdirectories([FromQuery] string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return BadRequest("Directory does not exist");
            if (!_fileService.IsPathAllowed(directoryPath))
                return BadRequest("Directory is not within allowed library path");

            var dirs = Directory.GetDirectories(directoryPath)
                .Select(d => new { path = d, name = Path.GetFileName(d) })
                .OrderBy(d => d.name)
                .ToArray();

            string? parentPath = _fileService.GetParentPathForBrowsing(directoryPath);
            return new JsonResult(new { directories = dirs, parentPath });
        }
        catch (UnauthorizedAccessException)
        {
            return new JsonResult(new { directories = Array.Empty<object>(), parentPath = (string?)null });
        }
        catch (Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    ///     Returns all video files found under the given directory, optionally recursing into
    ///     subdirectories.
    /// </summary>
    /// <param name="directoryPath"> The root directory to search. </param>
    /// <param name="recursive"> Whether to recurse into subdirectories. Defaults to <see langword="true"/>. </param>
    [HttpGet("files")]
    public IActionResult GetDirectoryFiles([FromQuery] string directoryPath, [FromQuery] bool recursive = true)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return BadRequest("Directory does not exist");
            if (!_fileService.IsPathAllowed(directoryPath))
                return BadRequest("Directory is not within allowed library path");

            var allDirs = new List<string> { directoryPath };
            if (recursive)
                allDirs.AddRange(Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories));

            var relativeRoot = _fileService.AllowAllPaths() ? directoryPath : _fileService.GetUploadsDirectory();
            var videoFiles   = _fileService.GetAllVideoFiles(allDirs)
                .Select(f => new
                {
                    path         = f,
                    name         = Path.GetFileName(f),
                    size         = new FileInfo(f).Length,
                    modified     = new FileInfo(f).LastWriteTime,
                    relativePath = Path.GetRelativePath(relativeRoot, f)
                })
                .OrderBy(f => f.relativePath)
                .ToArray();

            return new JsonResult(new { files = videoFiles });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Get directory files error: {ex}");
            return BadRequest(ex.Message);
        }
    }

    /******************************************************************
     *  Queue Addition
     ******************************************************************/

    /// <summary>
    ///     Enqueues all video files in the given directory (optionally recursive) using the
    ///     supplied encoder options.
    /// </summary>
    /// <param name="request"> Directory path, encoder options, and recursion flag. </param>
    [HttpPost("process-directory")]
    public async Task<IActionResult> ProcessDirectory([FromBody] ProcessDirectoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.DirectoryPath)) return BadRequest("Directory path is required");
        if (request.Options == null) return BadRequest("Encoder options are required");
        if (!Directory.Exists(request.DirectoryPath)) return BadRequest($"Directory does not exist: {request.DirectoryPath}");
        if (!_fileService.IsPathAllowed(request.DirectoryPath))
            return BadRequest("Directory is not within allowed library path");

        var result = await _transcodingService.AddDirectoryAsync(request.DirectoryPath, request.Options, request.Recursive);
        return new JsonResult(new { success = true, message = result });
    }

    /// <summary>
    ///     Dry-run preview: returns per-file Queue/Mux/Skip predictions for a directory under
    ///     the supplied options without writing to the DB or queueing any work. Used by the
    ///     "Analyze (Dry Run)" UI before the user commits to a real <c>process-directory</c>.
    /// </summary>
    /// <param name="request"> Directory path, encoder options, and recursion flag. </param>
    [HttpPost("analyze-directory")]
    public async Task<IActionResult> AnalyzeDirectory([FromBody] ProcessDirectoryRequest request)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrEmpty(request.DirectoryPath)) return BadRequest("Directory path is required");
            if (request.Options == null) return BadRequest("Encoder options are required");
            if (!Directory.Exists(request.DirectoryPath)) return BadRequest($"Directory does not exist: {request.DirectoryPath}");
            if (!_fileService.IsPathAllowed(request.DirectoryPath))
                return BadRequest("Directory is not within allowed library path");

            var results = await _transcodingService.AnalyzeDirectoryAsync(
                request.DirectoryPath, request.Options, request.Recursive, HttpContext.RequestAborted);
            return new JsonResult(new { success = true, results });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Analysis cancelled");
        }
    }

    /// <summary> Enqueues a single video file using the supplied encoder options. </summary>
    /// <param name="request"> File path and encoder options. </param>
    [HttpPost("process-file")]
    public async Task<IActionResult> ProcessFile([FromBody] ProcessFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.FilePath)) return BadRequest("File path is required");
        if (request.Options == null) return BadRequest("Encoder options are required");
        if (!System.IO.File.Exists(request.FilePath)) return BadRequest($"File does not exist: {request.FilePath}");
        if (!_fileService.IsFilePathAllowed(request.FilePath))
            return BadRequest("File is not within allowed library path");

        // Apply per-folder overrides (same path the auto-scanner uses) so a manually queued
        // file under a folder with a Codec / language / target override actually honors that
        // override. Without this, the file is queued under the global options and the local
        // dispatcher's override application can't help — the request.Options it'd see is
        // already the globals.
        var folderOverride = _autoScanService.FindFolderOverride(request.FilePath);
        var fileOptions    = EncoderOptionsOverride.ApplyOverrides(request.Options, folderOverride, null);

        var workItemId = await _transcodingService.AddFileAsync(request.FilePath, fileOptions, force: true);
        return new JsonResult(new { success = true, workItemId });
    }
}
