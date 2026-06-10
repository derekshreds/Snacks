using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Cluster;

/// <summary>
///     Pins the rotating discovery-token scheme that replaced broadcasting a
///     bare SHA-256 of the cluster shared secret over UDP. The token must be
///     deterministic per (secret, nodeId, bucket), differ across all three
///     inputs, and never equal the legacy direct hash (which an offline
///     attacker could brute-force).
/// </summary>
public sealed class DiscoveryTokenTests
{
    [Fact]
    public void Token_is_deterministic_for_same_inputs()
    {
        var a = ClusterDiscoveryService.ComputeDiscoveryToken("secret", "node-1", 123);
        var b = ClusterDiscoveryService.ComputeDiscoveryToken("secret", "node-1", 123);
        a.Should().Be(b).And.NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("other-secret", "node-1", 123L)]
    [InlineData("secret",       "node-2", 123L)]
    [InlineData("secret",       "node-1", 124L)]
    public void Token_changes_when_any_input_changes(string secret, string nodeId, long bucket)
    {
        var baseline = ClusterDiscoveryService.ComputeDiscoveryToken("secret", "node-1", 123);
        ClusterDiscoveryService.ComputeDiscoveryToken(secret, nodeId, bucket)
            .Should().NotBe(baseline);
    }

    [Fact]
    public void Token_is_not_the_legacy_secret_hash()
    {
        var token = ClusterDiscoveryService.ComputeDiscoveryToken("secret", "node-1", 123);
        token.Should().NotBe(ClusterDiscoveryService.HashSecret("secret"));
    }

    [Fact]
    public void Empty_secret_yields_empty_token()
    {
        ClusterDiscoveryService.ComputeDiscoveryToken("", "node-1", 123).Should().BeEmpty();
    }
}
