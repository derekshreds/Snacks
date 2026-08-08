using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Cluster;

public sealed class ClusterCapacityPolicyTests
{
    [Fact]
    public void CpuCapacity_IsAlwaysOne()
    {
        var cpu = new HardwareDevice { DeviceId = "cpu", DefaultConcurrency = 12 };
        var settings = Settings("cpu", enabled: true, maxConcurrency: 8);

        ClusterCapacityPolicy.EffectiveDeviceCapacity(cpu, settings, false, null)
            .Should().Be(1);
    }

    [Fact]
    public void DeviceOverride_CanDisableOrCapADevice()
    {
        var gpu = new HardwareDevice { DeviceId = "nvidia", DefaultConcurrency = 4 };
        var settings = Settings("nvidia", enabled: false, maxConcurrency: 2);

        ClusterCapacityPolicy.IsDeviceEnabled(settings, gpu.DeviceId).Should().BeFalse();
        ClusterCapacityPolicy.EffectiveDeviceCapacity(gpu, settings, false, null)
            .Should().Be(2);
    }

    [Fact]
    public void MasterMusicLimit_DoesNotLeakToWorkers()
    {
        var music = new HardwareDevice { DeviceId = "music", DefaultConcurrency = 3 };

        ClusterCapacityPolicy.EffectiveDeviceCapacity(music, null, true, 0)
            .Should().Be(0);
        ClusterCapacityPolicy.EffectiveDeviceCapacity(music, null, false, 0)
            .Should().Be(3);
    }

    [Fact]
    public void HasFreeSlot_UsesCapacityAndCurrentUsage()
    {
        var devices = new[]
        {
            new HardwareDevice { DeviceId = "nvidia", DefaultConcurrency = 2 },
            new HardwareDevice { DeviceId = "intel", DefaultConcurrency = 1 },
        };
        var used = new Dictionary<string, int> { ["nvidia"] = 2, ["intel"] = 0 };

        ClusterCapacityPolicy.HasFreeSlot(
                devices,
                settings: null,
                isMaster: false,
                masterMusicConcurrency: null,
                deviceId => used[deviceId])
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(NodeStatus.Online, true)]
    [InlineData(NodeStatus.Busy, true)]
    [InlineData(NodeStatus.Uploading, true)]
    [InlineData(NodeStatus.Downloading, true)]
    [InlineData(NodeStatus.Offline, false)]
    public void DispatchableStates_AreExplicit(NodeStatus status, bool expected)
    {
        ClusterCapacityPolicy.IsDispatchableStatus(status).Should().Be(expected);
    }

    private static NodeSettings Settings(string id, bool enabled, int? maxConcurrency) =>
        new()
        {
            DeviceSettings = new Dictionary<string, DeviceConcurrencySetting>
            {
                [id] = new() { Enabled = enabled, MaxConcurrency = maxConcurrency },
            },
        };
}
