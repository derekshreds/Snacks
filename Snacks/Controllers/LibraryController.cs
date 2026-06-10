using Microsoft.AspNetCore.Mvc;
using Snacks.Data;
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
    private readonly FileService               _fileService;
    private readonly TranscodingService        _transcodingService;
    private readonly AutoScanService           _autoScanService;
    private readonly LibraryAnalysisJobService _analysisJobs;
    private readonly MediaFileRepository       _mediaFileRepo;
    private readonly FileHealthService         _fileHealth;

    public LibraryController(
        FileService fileService,
        TranscodingService transcodingService,
        AutoScanService autoScanService,
        LibraryAnalysisJobService analysisJobs,
        MediaFileRepository mediaFileRepo,
        FileHealthService fileHealth)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(transcodingService);
        ArgumentNullException.ThrowIfNull(autoScanService);
        ArgumentNullException.ThrowIfNull(analysisJobs);
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        ArgumentNullException.ThrowIfNull(fileHealth);
        _fileService        = fileService;
        _transcodingService = transcodingService;
        _autoScanService    = autoScanService;
        _analysisJobs       = analysisJobs;
        _mediaFileRepo      = mediaFileRepo;
        _fileHealth         = fileHealth;
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
    ///     Returns all media files (video + music) found under the given directory,
    ///     optionally recursing into subdirectories. Each entry carries a <c>kind</c>
    ///     field (<c>"Video"</c> or <c>"Music"</c>) so the browser can show distinct
    ///     icons and counts.
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
            var mediaFiles   = _fileService.GetAllMediaFiles(allDirs)
                .Select(t =>
                {
                    // One FileInfo per file (NAS metadata I/O is slow — the previous
                    // code stat'ed each file twice), and skip entries deleted between
                    // enumeration and projection instead of failing the whole listing.
                    var fi = new FileInfo(t.Path);
                    if (!fi.Exists) return null;
                    return new
                    {
                        path         = t.Path,
                        name         = fi.Name,
                        size         = fi.Length,
                        modified     = fi.LastWriteTime,
                        relativePath = Path.GetRelativePath(relativeRoot, t.Path),
                        kind         = t.Kind.ToString(),
                    };
                })
                .Where(f => f != null)
                .OrderBy(f => f!.relativePath)
                .ToArray();

            return new JsonResult(new { files = mediaFiles });
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
    ///     Starts a dry-run preview as a background job: per-file Queue/Mux/Skip predictions
    ///     for a directory under the supplied options, without writing to the DB or queueing
    ///     any work. Returns a job ID immediately — whole-library analyses used to run inside
    ///     this request and time out on large libraries. The UI polls
    ///     <c>analyze-status</c> and fetches <c>analyze-results</c> when the job completes.
    /// </summary>
    /// <param name="request"> Directory path, encoder options, and recursion flag. </param>
    [HttpPost("analyze-directory")]
    public IActionResult AnalyzeDirectory([FromBody] ProcessDirectoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.DirectoryPath)) return BadRequest("Directory path is required");
        if (request.Options == null) return BadRequest("Encoder options are required");
        if (!Directory.Exists(request.DirectoryPath)) return BadRequest($"Directory does not exist: {request.DirectoryPath}");
        if (!_fileService.IsPathAllowed(request.DirectoryPath))
            return BadRequest("Directory is not within allowed library path");

        try
        {
            var job = _analysisJobs.Start(request.DirectoryPath, request.Options, request.Recursive);
            return new JsonResult(new { success = true, jobId = job.Id });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
    }

    /// <summary> Progress of a running (or finished) analysis job. </summary>
    /// <param name="jobId"> Job ID returned by <c>analyze-directory</c>. </param>
    [HttpGet("analyze-status/{jobId}")]
    public IActionResult AnalyzeStatus(string jobId)
    {
        var job = _analysisJobs.Get(jobId);
        if (job == null) return NotFound("Unknown or expired analysis job");
        return new JsonResult(new
        {
            state     = job.State,
            processed = job.Processed,
            total     = job.Total,
            error     = job.Error,
        });
    }

    /// <summary> Result set of a completed analysis job. 409 while the job is still running. </summary>
    /// <param name="jobId"> Job ID returned by <c>analyze-directory</c>. </param>
    [HttpGet("analyze-results/{jobId}")]
    public IActionResult AnalyzeResults(string jobId)
    {
        var job = _analysisJobs.Get(jobId);
        if (job == null) return NotFound("Unknown or expired analysis job");
        if (job.State == LibraryAnalysisJobService.JobState.Running)
            return Conflict("Analysis still running");
        return new JsonResult(new { state = job.State, error = job.Error, results = job.Results });
    }

    /// <summary> Cancels a running analysis job. No-op (success: false) when already finished. </summary>
    /// <param name="jobId"> Job ID returned by <c>analyze-directory</c>. </param>
    [HttpPost("analyze-cancel/{jobId}")]
    public IActionResult AnalyzeCancel(string jobId)
        => new JsonResult(new { success = _analysisJobs.Cancel(jobId) });

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

    /******************************************************************
     *  Library Health
     ******************************************************************/

    /// <summary>
    ///     Library health report: every scanned file with a file-level problem —
    ///     no audio track, no decodable video, zero duration, or a failed encode —
    ///     with the issue list computed per file so the UI can filter by category.
    ///     Sourced entirely from scan-time DB data; no files are touched.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> GetHealthReport()
    {
        var files = await _mediaFileRepo.GetHealthIssuesAsync();
        var items = files.Select(f =>
        {
            var issues = new List<string>();
            if (f.Kind == MediaKind.Video && f.AudioStreams == "[]")                       issues.Add("no-audio");
            if (f.Kind == MediaKind.Video && f.Codec != "" && (f.Width <= 0 || f.Height <= 0)) issues.Add("no-video");
            if (f.Codec != "" && f.Duration <= 0)                                          issues.Add("no-duration");
            if (f.Status == MediaFileStatus.Failed)                                        issues.Add("failed");
            return new
            {
                filePath      = f.FilePath,
                fileName      = f.FileName,
                directory     = f.Directory,
                sizeBytes     = f.FileSize,
                codec         = f.Codec,
                width         = f.Width,
                height        = f.Height,
                duration      = f.Duration,
                kind          = f.Kind.ToString(),
                status        = f.Status.ToString(),
                failureReason = f.FailureReason,
                lastScannedAt = f.LastScannedAt,
                issues,
            };
        }).ToList();

        return new JsonResult(new
        {
            items,
            summary = new
            {
                noAudio    = items.Count(i => i.issues.Contains("no-audio")),
                noVideo    = items.Count(i => i.issues.Contains("no-video")),
                noDuration = items.Count(i => i.issues.Contains("no-duration")),
                failed     = items.Count(i => i.issues.Contains("failed")),
                total      = items.Count,
            },
        });
    }

    /// <summary>
    ///     Aggregate library composition (codec / resolution / status distributions
    ///     plus totals) for the insights panel on the Library Health page.
    /// </summary>
    [HttpGet("insights")]
    public async Task<IActionResult> GetInsights()
        => new JsonResult(await _mediaFileRepo.GetLibraryInsightsAsync());

    /// <summary>
    ///     Deep verification of a single file: ffmpeg decode samples at the start,
    ///     middle, and end. Returns <c>{ ok, issues }</c> where issues are the
    ///     decoder error lines. Bounded (~30s of decoding) — not a full-file pass.
    /// </summary>
    /// <param name="request"> The file to verify. </param>
    [HttpPost("health/verify")]
    public async Task<IActionResult> VerifyFile([FromBody] ProcessFileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.FilePath)) return BadRequest("File path is required");
        if (!_fileService.IsFilePathAllowed(request.FilePath))
            return BadRequest("File is not within allowed library path");

        var result = await _fileHealth.VerifyAsync(request.FilePath, HttpContext.RequestAborted);
        return new JsonResult(new { ok = result.Ok, issues = result.Issues });
    }
}
