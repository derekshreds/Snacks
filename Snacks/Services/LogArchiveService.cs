using System.IO.Compression;
using System.Text;

namespace Snacks.Services;

/// <summary>
///     Reads and exports operations logs for diagnostics — the tail of the latest
///     <c>snacks-*.log</c> for live viewing, and a streaming ZIP of the whole
///     <c>logs/</c> directory for download. Used by both the browser-facing
///     <c>/api/diagnostics/*</c> endpoints and the worker-side
///     <c>/api/cluster/diagnostics/*</c> mirrors so the master can re-stream a
///     remote node's logs without duplicating the read logic.
/// </summary>
public sealed class LogArchiveService
{
    // 7 daily rolls × 10 MB cap + per-job FFmpeg logs is ~70 MB worst case;
    // a single file over this almost certainly indicates a misconfiguration
    // (verbose logging left on, runaway loop) and shouldn't be carried inside
    // a diagnostics bundle.
    private const long MaxIncludedFileBytes = 50L * 1024 * 1024;

    /// <summary>
    ///     Reads the tail of the most recent <c>snacks-*.log</c> file under
    ///     <paramref name="logsDir"/>. Returns <c>(null, null)</c> when the directory
    ///     doesn't exist or no rolling log file is present.
    /// </summary>
    /// <param name="logsDir"> Absolute path to the logs directory. </param>
    /// <param name="lines">   Maximum number of trailing lines to return. </param>
    public (FileInfo? logFile, string[]? lines) ReadLatestLogTail(string logsDir, int lines)
    {
        ArgumentException.ThrowIfNullOrEmpty(logsDir);

        if (!Directory.Exists(logsDir)) return (null, null);

        var latest = Directory.EnumerateFiles(logsDir, "snacks-*.log")
            .Select(p => new FileInfo(p))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest == null) return (null, null);

        // Read with shared access so we don't fight Serilog's writer. The 10MB
        // rolling cap means worst case we touch ~10MB — fine for a diagnostic
        // endpoint.
        using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content  = reader.ReadToEnd();
        var allLines = content.Split('\n');
        var tail = allLines.Length <= lines
            ? allLines
            : allLines.Skip(allLines.Length - lines).ToArray();
        return (latest, tail);
    }

    /// <summary>
    ///     Streams a ZIP of every <c>*.log</c> file under <paramref name="logsDir"/> into
    ///     <paramref name="output"/>. Each entry is opened with <c>FileShare.ReadWrite</c>
    ///     so Serilog's active writer doesn't block the export. The output stream is left
    ///     open for the caller to flush/dispose.
    /// </summary>
    public void WriteLogsZip(Stream output, string logsDir)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(logsDir);

        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        if (!Directory.Exists(logsDir)) return;

        foreach (var path in Directory.EnumerateFiles(logsDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }

            if (info.Length > MaxIncludedFileBytes) continue;

            var entry = archive.CreateEntry(info.Name, CompressionLevel.Optimal);
            entry.LastWriteTime = info.LastWriteTimeUtc;

            try
            {
                using var src   = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var entry_ = entry.Open();
                src.CopyTo(entry_);
            }
            catch
            {
                // A file disappearing mid-enumeration (Serilog rolling at exactly the
                // wrong moment) shouldn't kill the whole export. Skip and continue.
            }
        }
    }
}
