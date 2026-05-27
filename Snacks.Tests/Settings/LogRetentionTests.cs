using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Pins the behavior of <see cref="LogRetentionService.Sweep"/>:
///     per-job FFmpeg logs older than the retention window are deleted,
///     Serilog's rolling app log (<c>snacks-*.log</c>) is never touched,
///     and a zero/negative retention disables the sweep entirely.
/// </summary>
public sealed class LogRetentionTests : IDisposable
{
    private readonly string _logsDir;

    public LogRetentionTests()
    {
        _logsDir = Path.Combine(Path.GetTempPath(), "snacks-logretention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logsDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_logsDir, recursive: true); } catch { }
    }

    [Fact]
    public void Sweep_deletes_per_job_logs_older_than_retention()
    {
        var now = new DateTime(2025, 06, 15, 12, 0, 0, DateTimeKind.Utc);
        var old = WriteLog("Movie.2020_abcd1234.log", now.AddDays(-45));
        var fresh = WriteLog("Movie.2021_efef5678.log", now.AddDays(-5));

        var deleted = LogRetentionService.Sweep(_logsDir, retentionDays: 30, nowUtc: now);

        deleted.Should().Be(1);
        File.Exists(old).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void Sweep_preserves_serilog_rolling_app_logs()
    {
        var now = new DateTime(2025, 06, 15, 12, 0, 0, DateTimeKind.Utc);
        var serilogOld = WriteLog("snacks-20250101.log", now.AddDays(-180));
        var jobOld     = WriteLog("Title_aaaa1111.log",  now.AddDays(-180));

        LogRetentionService.Sweep(_logsDir, retentionDays: 30, nowUtc: now);

        File.Exists(serilogOld).Should().BeTrue("Serilog owns its own retention");
        File.Exists(jobOld).Should().BeFalse();
    }

    [Fact]
    public void Sweep_with_non_positive_retention_is_a_noop()
    {
        var now = new DateTime(2025, 06, 15, 12, 0, 0, DateTimeKind.Utc);
        var oldLog = WriteLog("Old_aaaa1111.log", now.AddDays(-365));

        LogRetentionService.Sweep(_logsDir, retentionDays: 0,  nowUtc: now).Should().Be(0);
        LogRetentionService.Sweep(_logsDir, retentionDays: -5, nowUtc: now).Should().Be(0);

        File.Exists(oldLog).Should().BeTrue();
    }

    [Fact]
    public void Sweep_missing_directory_is_a_noop()
    {
        var missing = Path.Combine(_logsDir, "does-not-exist");
        var deleted = LogRetentionService.Sweep(missing, retentionDays: 30, nowUtc: DateTime.UtcNow);

        deleted.Should().Be(0);
    }

    [Fact]
    public void Sweep_only_touches_log_files()
    {
        var now = new DateTime(2025, 06, 15, 12, 0, 0, DateTimeKind.Utc);
        var oldLog = WriteLog("Job_aaaa1111.log", now.AddDays(-90));
        var oldTxt = WriteLog("notes_old.txt",    now.AddDays(-90));

        LogRetentionService.Sweep(_logsDir, retentionDays: 30, nowUtc: now);

        File.Exists(oldLog).Should().BeFalse();
        File.Exists(oldTxt).Should().BeTrue();
    }

    private string WriteLog(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_logsDir, name);
        File.WriteAllText(path, "stub");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
