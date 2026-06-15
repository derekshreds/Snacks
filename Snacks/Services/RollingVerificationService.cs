using System.Text.Json;
using Snacks.Data;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Background rotation that deep-verifies library files a few at a time —
///     ffmpeg decode samples via <see cref="FileHealthService"/> — oldest-verified
///     first, so silent corruption (bit rot, truncated transfers, bad sectors)
///     surfaces on the Library Health page instead of at playback time. Budgeted
///     by <see cref="EncoderOptions.VerifyFilesPerDay"/> (0 = off, the default):
///     each hourly tick verifies 1/24th of the daily budget, so the I/O cost is
///     a gentle trickle rather than a nightly storm.
/// </summary>
public sealed class RollingVerificationService : IHostedService, IDisposable
{
    private readonly MediaFileRepository _mediaFileRepo;
    private readonly FileHealthService   _fileHealth;
    private readonly FileService         _fileService;
    private readonly ClusterService      _clusterService;
    private Timer? _timer;
    private int _running;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public RollingVerificationService(
        MediaFileRepository mediaFileRepo,
        FileHealthService fileHealth,
        FileService fileService,
        ClusterService clusterService)
    {
        ArgumentNullException.ThrowIfNull(mediaFileRepo);
        ArgumentNullException.ThrowIfNull(fileHealth);
        ArgumentNullException.ThrowIfNull(fileService);
        ArgumentNullException.ThrowIfNull(clusterService);
        _mediaFileRepo  = mediaFileRepo;
        _fileHealth     = fileHealth;
        _fileService    = fileService;
        _clusterService = clusterService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // First tick after 10 minutes (let startup scans settle), then hourly.
        _timer = new Timer(_ => _ = RunTickAsync(), null,
            TimeSpan.FromMinutes(10), TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    /// <summary> One hourly verification pass. Single-flight; skipped entirely on worker nodes. </summary>
    internal async Task RunTickAsync()
    {
        if (_clusterService.IsNodeMode) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        try
        {
            int perDay = LoadVerifyBudget();
            if (perDay <= 0) return;

            int quota = Math.Max(1, perDay / 24);
            var candidates = await _mediaFileRepo.GetVerificationCandidatesAsync(quota);

            int consecutiveMissing = 0;
            foreach (var row in candidates)
            {
                if (!File.Exists(row.FilePath))
                {
                    // A run of consecutive misses means the library mount is down —
                    // abort the tick rather than churn through (and mis-stamp) the
                    // whole batch against dead storage.
                    if (++consecutiveMissing >= 5)
                    {
                        Console.WriteLine("RollingVerify: library storage appears offline — skipping this tick");
                        return;
                    }

                    // Move the rotation along with a "missing" marker (NOT "ok" —
                    // that would claim a file we couldn't read is healthy), and
                    // never overwrite a previously recorded decode failure.
                    var marker = row.LastVerifyResult is { } prior && prior != "ok" ? prior : "missing";
                    await _mediaFileRepo.SetVerifyResultAsync(row.FilePath, marker);
                    continue;
                }
                consecutiveMissing = 0;

                try
                {
                    var result = await _fileHealth.VerifyAsync(row.FilePath);
                    var summary = result.Ok
                        ? "ok"
                        : string.Join(" | ", result.Issues.Take(5));
                    if (summary.Length > 2000) summary = summary[..2000];
                    await _mediaFileRepo.SetVerifyResultAsync(row.FilePath, summary);

                    if (!result.Ok)
                        Console.WriteLine($"RollingVerify: problems in {row.FileName}: {summary}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"RollingVerify: verify of {row.FileName} failed: {ex.Message}");
                }
            }

            if (candidates.Count > 0)
                Console.WriteLine($"RollingVerify: checked {candidates.Count} file(s) this hour (budget {perDay}/day)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"RollingVerify: tick failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary> Reads <c>VerifyFilesPerDay</c> from the persisted settings (0 when unset/unreadable). </summary>
    private int LoadVerifyBudget()
    {
        try
        {
            var path = Path.Combine(_fileService.GetWorkingDirectory(), "config", "settings.json");
            if (!File.Exists(path)) return 0;
            var parsed = JsonSerializer.Deserialize<EncoderOptions>(File.ReadAllText(path), _jsonOptions);
            return Math.Max(0, parsed?.VerifyFilesPerDay ?? 0);
        }
        catch
        {
            return 0;
        }
    }
}
