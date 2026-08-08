using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Periodically prunes per-job FFmpeg log files in <c>{workdir}/logs/</c> that are
///     older than <see cref="EncoderOptions.EncodingLogRetentionDays"/>. Long-running
///     instances accumulate one log file per encode and there's no other cleanup —
///     a heavy library can land in the six-figure file count over a year, which
///     slows directory listing and wastes disk.
///
///     <para>The Serilog rolling app log (<c>snacks-*.log</c>) has its own caps
///     and is explicitly excluded from this sweep.</para>
/// </summary>
public sealed class LogRetentionService : IHostedService, IDisposable
{
    /// <summary>How often the sweep runs after the initial pass.</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(24);

    /// <summary>
    ///     Brief startup grace so we don't pile log IO onto an already-busy boot.
    ///     30 seconds is enough for queue restore and the first auto-scan to settle.
    /// </summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    private readonly FileService _fileService;
    private readonly ILogger<LogRetentionService> _logger;
    private readonly string _settingsPath;
    private Timer? _timer;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LogRetentionService(FileService fileService, ILogger<LogRetentionService> logger)
    {
        _fileService  = fileService;
        _logger       = logger;
        _settingsPath = Path.Combine(_fileService.GetWorkingDirectory(), "config", "settings.json");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => RunSweep(), state: null, dueTime: StartupDelay, period: SweepInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void RunSweep()
    {
        try
        {
            var retentionDays = ReadRetentionDays();
            if (retentionDays <= 0) return;

            var logsDir = Path.Combine(_fileService.GetWorkingDirectory(), "logs");
            var deleted = Sweep(logsDir, retentionDays, DateTime.UtcNow);
            if (deleted > 0)
                _logger.LogInformation("LogRetention: deleted {Count} per-job log files older than {Days}d.", deleted, retentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LogRetention: sweep failed.");
        }
    }

    /// <summary>
    ///     Reads <c>EncodingLogRetentionDays</c> from <c>settings.json</c> without
    ///     reaching for the full <see cref="EncoderOptions"/> deserializer — keeps
    ///     this service decoupled from the legacy-audio migration path and lets it
    ///     run before any other component has touched settings.
    /// </summary>
    private int ReadRetentionDays()
    {
        const int DefaultDays = 7;
        if (!File.Exists(_settingsPath)) return DefaultDays;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_settingsPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return DefaultDays;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "EncodingLogRetentionDays", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var v)) return v;
                return DefaultDays;
            }
            return DefaultDays;
        }
        catch
        {
            return DefaultDays;
        }
    }

    /// <summary>
    ///     Deletes every <c>*.log</c> file in <paramref name="logsDir"/> whose last-write
    ///     timestamp is older than <paramref name="retentionDays"/>, excluding files that
    ///     match Serilog's rolling pattern (<c>snacks-*.log</c>). Returns the count of
    ///     files removed. Per-file errors are swallowed — a transient lock shouldn't
    ///     abort the whole sweep.
    /// </summary>
    /// <param name="logsDir">Directory to sweep. Missing directory is a no-op.</param>
    /// <param name="retentionDays">Max age in days. Values <= 0 are a no-op.</param>
    /// <param name="nowUtc">Reference time; injected so tests can pin a deterministic clock.</param>
    public static int Sweep(string logsDir, int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0) return 0;
        if (!Directory.Exists(logsDir)) return 0;

        var cutoff = nowUtc - TimeSpan.FromDays(retentionDays);
        var deleted = 0;

        foreach (var path in Directory.EnumerateFiles(logsDir, "*.log", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            // Serilog owns its own retention for the app log — never touch it here.
            if (name.StartsWith("snacks-", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc >= cutoff) continue;
                info.Delete();
                deleted++;
            }
            catch
            {
                // File in use, permissions, or vanished mid-enumeration — next sweep will retry.
            }
        }

        return deleted;
    }
}
