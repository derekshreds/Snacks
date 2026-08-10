using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

public sealed class AppVersionTests
{
    [Fact]
    public void RuntimeVersion_ComesFromProjectAssemblyMetadata()
    {
        AppVersion.Current.Should().Be("2.18.0");
        ClusterDiscoveryService.ClusterVersion.Should().Be(AppVersion.Current);
    }
}
