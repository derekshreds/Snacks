using FluentAssertions;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the worker → master propagation of the encoder actually chosen for a
///     remote job. The worker stamps <c>WorkItem.VideoEncoderName</c> when FFmpeg
///     starts, but the master's queue card renders the master's own work item — if
///     either report site stops sending the name, or the master stops copying it,
///     remote processing cards silently lose their encoder badge while local ones
///     keep it. Like <c>AdvancedVideoHistoryLabelTests</c>, this reads the wiring
///     verbatim because the full path needs two live nodes and ffmpeg.
/// </summary>
public sealed class RemoteProgressEncoderNameTests
{
    [Fact]
    public void Every_worker_progress_report_carries_the_active_encoder_name()
    {
        var src = File.ReadAllText(LocateRepoFile("Snacks/Services/ClusterNodeJobService.cs"));

        var reportSites = 0;
        for (var idx = src.IndexOf("new JobProgress", StringComparison.Ordinal);
             idx >= 0;
             idx = src.IndexOf("new JobProgress", idx + 1, StringComparison.Ordinal))
        {
            reportSites++;
            var window = src.Substring(idx, Math.Min(400, src.Length - idx));
            window.Should().Contain("EncoderName = workItem.VideoEncoderName",
                $"progress report site #{reportSites} must carry the encoder the worker chose");
        }

        reportSites.Should().BeGreaterThan(0, "ClusterNodeJobService must report progress via JobProgress");
    }

    [Fact]
    public void Master_copies_the_reported_encoder_onto_its_own_work_item()
    {
        var src = File.ReadAllText(LocateRepoFile("Snacks/Services/ClusterService.cs"));

        var handlerIdx = src.IndexOf("HandleRemoteProgressAsync", StringComparison.Ordinal);
        handlerIdx.Should().BeGreaterThan(-1, "ClusterService must handle remote progress updates");

        var window = src.Substring(handlerIdx, Math.Min(1200, src.Length - handlerIdx));
        window.Should().Contain("workItem.VideoEncoderName = progress.EncoderName");
    }

    private static string LocateRepoFile(string repoRelativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Snacks.sln")))
                return Path.Combine(dir, repoRelativePath);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Could not locate Snacks.sln above {AppContext.BaseDirectory}");
    }
}
