using FluentAssertions;
using Snacks.Models;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the multi-pair shared-storage rewrite map: longest prefix wins,
///     the legacy single From/To pair is folded in (after explicit entries),
///     and malformed entries are dropped.
/// </summary>
public sealed class SharedStorageRewriteTests
{
    [Fact]
    public void Effective_rewrites_order_longest_prefix_first()
    {
        var config = new ClusterConfig
        {
            SharedStoragePathRewrites =
            {
                new SharedStorageRewrite { From = "/shared",        To = "/mnt/nas" },
                new SharedStorageRewrite { From = "/shared/movies", To = "/mnt/movies" },
            },
        };

        var effective = config.EffectiveRewrites();
        effective.Select(r => r.From).Should().Equal("/shared/movies", "/shared");
    }

    [Fact]
    public void Legacy_single_pair_is_folded_in_and_still_honored()
    {
        var config = new ClusterConfig
        {
            SharedStoragePathRewriteFrom = "/legacy",
            SharedStoragePathRewriteTo   = "/mnt/legacy",
        };

        var effective = config.EffectiveRewrites();
        effective.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { From = "/legacy", To = "/mnt/legacy" });
    }

    [Fact]
    public void Multiple_shares_each_get_their_own_translation()
    {
        var config = new ClusterConfig
        {
            SharedStoragePathRewrites =
            {
                new SharedStorageRewrite { From = "/shared/movies", To = "/mnt/nas/movies" },
                new SharedStorageRewrite { From = "/shared/tv",     To = "/mnt/nas2/tv" },
            },
        };

        // Validate the prefix-first-match semantics ApplyRewrite relies on.
        var effective = config.EffectiveRewrites();
        Rewrite("/shared/movies/Heat (1995).mkv", effective).Should().Be("/mnt/nas/movies/Heat (1995).mkv");
        Rewrite("/shared/tv/Show/S01E01.mkv", effective).Should().Be("/mnt/nas2/tv/Show/S01E01.mkv");
        Rewrite("/elsewhere/file.mkv", effective).Should().Be("/elsewhere/file.mkv");
    }

    [Fact]
    public void Blank_or_partial_entries_are_dropped()
    {
        var config = new ClusterConfig
        {
            SharedStoragePathRewrites =
            {
                new SharedStorageRewrite { From = "",      To = "/mnt/x" },
                new SharedStorageRewrite { From = "/ok",   To = "/mnt/ok" },
            },
        };

        config.EffectiveRewrites().Should().ContainSingle().Which.From.Should().Be("/ok");
    }

    /// <summary> Mirrors SharedStoragePathValidator.ApplyRewrite's first-match-wins loop. </summary>
    private static string Rewrite(string raw, IReadOnlyList<SharedStorageRewrite> rewrites)
    {
        foreach (var r in rewrites)
            if (raw.StartsWith(r.From, StringComparison.Ordinal))
                return r.To + raw[r.From.Length..];
        return raw;
    }
}
