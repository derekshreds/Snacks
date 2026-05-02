using System.Text;
using Microsoft.AspNetCore.Mvc;
using Snacks.Services;

namespace Snacks.Controllers;

/// <summary>
///     Read-only operations diagnostics. Surfaces the persistent ops log written by
///     Serilog so the user can audit recent activity without ssh-ing into the host —
///     specifically aimed at the "queue items vanished overnight" case where every prior
///     diagnosis had to rely on guesswork because <c>Console.WriteLine</c> didn't survive
///     the process.
/// </summary>
[Route("api/diagnostics")]
[ApiController]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly FileService _fileService;

    public DiagnosticsController(FileService fileService)
    {
        ArgumentNullException.ThrowIfNull(fileService);
        _fileService = fileService;
    }

    /// <summary>
    ///     Returns the tail of the most recent operations log file under
    ///     <c>${SNACKS_WORK_DIR}/logs/snacks-*.log</c>.
    /// </summary>
    /// <param name="lines">
    ///     Maximum number of lines to return from the end of the latest log file.
    ///     Clamped to [1, 5000] so a single request can't pin the host on a large log.
    /// </param>
    [HttpGet("log")]
    public IActionResult GetLogTail([FromQuery] int lines = 200)
    {
        var clamped = Math.Clamp(lines, 1, 5000);
        var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");

        if (!Directory.Exists(logsDir))
            return new JsonResult(new { logsDir, available = false, lines = Array.Empty<string>() });

        var latest = Directory.EnumerateFiles(logsDir, "snacks-*.log")
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest == null)
            return new JsonResult(new { logsDir, available = false, lines = Array.Empty<string>() });

        // Read with shared access so we don't fight Serilog's writer. Tail by reading the
        // whole file and taking the last N lines — the 10MB rolling cap means worst case
        // we touch ~10MB, which is fine for a diagnostic endpoint.
        try
        {
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = reader.ReadToEnd();
            var allLines = content.Split('\n');
            var tail = allLines.Length <= clamped
                ? allLines
                : allLines.Skip(allLines.Length - clamped).ToArray();
            return new JsonResult(new
            {
                logsDir,
                available = true,
                logFile = latest.Name,
                lastWriteUtc = latest.LastWriteTimeUtc,
                lineCount = tail.Length,
                lines = tail,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
