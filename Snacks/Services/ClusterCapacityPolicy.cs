using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Pure cluster scheduling rules for device enablement, slot capacity,
///     and dispatchable node states.
/// </summary>
internal static class ClusterCapacityPolicy
{
    internal static bool IsDeviceEnabled(NodeSettings? settings, string deviceId)
    {
        if (settings?.DeviceSettings == null) return true;
        return !settings.DeviceSettings.TryGetValue(deviceId, out var device) || device.Enabled;
    }

    internal static int EffectiveDeviceCapacity(
        HardwareDevice device,
        NodeSettings? settings,
        bool isMaster,
        int? masterMusicConcurrency)
    {
        if (device.DeviceId == "cpu") return 1;

        if (settings?.DeviceSettings != null
            && settings.DeviceSettings.TryGetValue(device.DeviceId, out var configured)
            && configured.MaxConcurrency.HasValue)
        {
            return Math.Max(0, configured.MaxConcurrency.Value);
        }

        if (device.DeviceId == "music" && isMaster && masterMusicConcurrency.HasValue)
            return Math.Max(0, masterMusicConcurrency.Value);

        return Math.Max(0, device.DefaultConcurrency);
    }

    internal static bool HasFreeSlot(
        IReadOnlyCollection<HardwareDevice>? devices,
        NodeSettings? settings,
        bool isMaster,
        int? masterMusicConcurrency,
        Func<string, int> usedSlots)
    {
        if (devices == null || devices.Count == 0) return false;

        foreach (var device in devices)
        {
            if (!IsDeviceEnabled(settings, device.DeviceId)) continue;

            int capacity = EffectiveDeviceCapacity(
                device,
                settings,
                isMaster,
                masterMusicConcurrency);

            if (capacity > 0 && usedSlots(device.DeviceId) < capacity)
                return true;
        }

        return false;
    }

    internal static bool IsDispatchableStatus(NodeStatus status) =>
        status is NodeStatus.Online
            or NodeStatus.Busy
            or NodeStatus.Uploading
            or NodeStatus.Downloading;
}
