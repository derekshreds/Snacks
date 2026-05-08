using Snacks.Models;

namespace Snacks.Services.Routing;

/// <summary>
///     Registry of all <see cref="IJobKindRouter"/> implementations, looked up
///     by <see cref="MediaKind"/>. Constructed once at DI time and shared by
///     <c>ClusterService</c> (slot scoring, dispatch) and
///     <c>ClusterNodeJobService</c> (synthetic device resolution, encode
///     dispatch).
/// </summary>
public sealed class JobKindRouters
{
    private readonly IReadOnlyDictionary<MediaKind, IJobKindRouter> _byKind;
    private readonly HashSet<string> _syntheticDeviceIds;

    public JobKindRouters(IEnumerable<IJobKindRouter> routers)
    {
        _byKind = routers.ToDictionary(r => r.Kind);
        _syntheticDeviceIds = new HashSet<string>(
            routers.Select(r => r.SyntheticDeviceId).Where(s => !string.IsNullOrEmpty(s))!,
            StringComparer.Ordinal);
    }

    /// <summary>
    ///     Resolves the router for a given kind. Throws if no router has been
    ///     registered for the kind — kinds without routers are programming
    ///     errors, not runtime conditions.
    /// </summary>
    public IJobKindRouter For(MediaKind kind)
    {
        if (_byKind.TryGetValue(kind, out var r)) return r;
        throw new InvalidOperationException(
            $"No IJobKindRouter registered for MediaKind.{kind}. Register one in Program.cs DI.");
    }

    /// <summary>
    ///     True if <paramref name="deviceId"/> is the synthetic id owned by some
    ///     router (e.g. <c>"music"</c>). Used by the worker's
    ///     <c>ResolveDeviceId</c> to short-circuit the hardware probe — synthetic
    ///     devices don't appear in <c>GetDetectedDevices()</c>.
    /// </summary>
    public bool IsSyntheticDevice(string deviceId) =>
        !string.IsNullOrEmpty(deviceId) && _syntheticDeviceIds.Contains(deviceId);
}
