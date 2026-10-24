using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Two-gate defense-in-depth filter for endpoints that hand out sensitive cluster
///     data (integration credentials). Applied in addition to
///     <see cref="ClusterAuthFilter"/>:
///     <list type="number">
///         <item>Source IP — and every address a forwarding header names — must be in a
///               loopback, RFC1918, CGNAT, link-local, or unique-local range, i.e. a
///               LAN-reachable peer, never a public host or a client relayed by a proxy.</item>
///         <item>Source IP must match a <see cref="ClusterNode"/> currently registered
///               with the master whose status is anything other than Offline/Unreachable.</item>
///     </list>
///     If the shared secret ever leaks, neither gate can be satisfied remotely, so
///     credentials stay inside the LAN and only reachable from an actively-connected
///     cluster member. Runs as an authorization filter so rejected callers never reach
///     model binding.
/// </summary>
public sealed class LocalNetworkOnlyFilter : IAuthorizationFilter
{
    private readonly ClusterService _clusterService;

    public LocalNetworkOnlyFilter(ClusterService clusterService)
    {
        ArgumentNullException.ThrowIfNull(clusterService);
        _clusterService = clusterService;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote == null || !ClientNetwork.IsLocalClient(context.HttpContext))
        {
            Log.Warning($"LocalNetworkOnly: rejected request from non-LAN source {remote}");
            context.Result = new ObjectResult(null) { StatusCode = 403 };
            return;
        }

        // Layer 2: must match a currently-connected node in the registry.
        var remoteStr = remote.ToString();
        var mappedStr = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : null;

        var hit = _clusterService.GetNodes().FirstOrDefault(n =>
            (n.IpAddress == remoteStr || (mappedStr != null && n.IpAddress == mappedStr))
            && n.Status != NodeStatus.Offline
            && n.Status != NodeStatus.Unreachable);

        if (hit == null)
        {
            Log.Warning($"LocalNetworkOnly: rejected request from {remoteStr} — not a currently-connected node");
            context.Result = new ObjectResult(null) { StatusCode = 403 };
            return;
        }
    }
}
