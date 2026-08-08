using FluentAssertions;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Pins the Advanced Video provenance labels on the encode-history ledger.
///     The full encode pipeline needs ffmpeg and a live queue, so like
///     <c>RemoteConversionPlacementTests</c> this reads the two writer sites
///     verbatim: if either stops stamping the plan's profile/rule onto the
///     record, the "Measured so far" panel silently goes dark for that path —
///     exactly the regression these assertions make loud. (The full pipeline
///     was additionally verified end-to-end against a live instance: scan →
///     rules → libsvtav1 encode → in-place replacement → labeled ledger row.)
/// </summary>
public sealed class AdvancedVideoHistoryLabelTests
{
    [Theory]
    [InlineData("Snacks/Services/TranscodingService.cs")]
    [InlineData("Snacks/Services/ClusterService.cs")]
    public void Both_history_writers_stamp_the_video_plan_labels(string sourceFile)
    {
        var src = File.ReadAllText(LocateRepoFile(sourceFile));

        var recordIdx = src.IndexOf("new EncodeHistory", StringComparison.Ordinal);
        recordIdx.Should().BeGreaterThan(-1, $"{sourceFile} must construct an EncodeHistory record");

        // A generous window after the constructor covers the initializer block.
        var window = src.Substring(recordIdx, Math.Min(3000, src.Length - recordIdx));

        window.Should().Contain("AdvancedProfileId   = workItem.VideoPlan?.ProfileId");
        window.Should().Contain("AdvancedProfileName = workItem.VideoPlan?.ProfileName");
        window.Should().Contain("AdvancedRuleName    = workItem.VideoPlan?.RuleName");
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
