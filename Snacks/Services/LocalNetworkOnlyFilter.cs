using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Two-gate defense-in-depth filter for endpoints that hand out sensitive cluster
///     data (integration credentials). Applied in addition to
///     <see cref="ClusterAuthFilter"/>:
///     <list type="number">
///         <item>Source IP must be in a loopback, RFC1918, CGNAT, link-local, or
///               unique-local range — i.e. a LAN-reachable peer, never a public host.</item>
///         <item>Source IP must match a <see cref="ClusterNode"/> currently registered
///               with the master whose status is anything other than Offline/Unreachable.</item>
///     </list>
///     If the shared secret ever leaks, neither gate can be satisfied remotely, so
///     credentials stay inside the LAN and only reachable from an actively-connected
///     cluster member.
/// </summary>
public sealed class LocalNetworkOnlyFilter : IActionFilter
{
    private readonly ClusterService _clusterService;

    public LocalNetworkOnlyFilter(ClusterService clusterService)
    {
        ArgumentNullException.ThrowIfNull(clusterService);
        _clusterService = clusterService;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote == null || !IsLocalNetwork(remote))
        {
            Console.WriteLine($"LocalNetworkOnly: rejected request from non-LAN source {remote}");
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
            Console.WriteLine($"LocalNetworkOnly: rejected request from {remoteStr} — not a currently-connected node");
            context.Result = new ObjectResult(null) { StatusCode = 403 };
            return;
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    /// <summary>
    ///     Returns <c>true</c> for addresses in any of the LAN/overlay ranges common
    ///     to Snacks deployments: loopback, RFC1918, CGNAT (Tailscale / overlay), link-local,
    ///     or IPv6 unique-local. Explicitly rejects publicly routable addresses.
    /// </summary>
    private static bool IsLocalNetwork(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        // Normalize ::ffff:a.b.c.d → a.b.c.d so IPv4 checks apply.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            // 10.0.0.0/8 — RFC1918; common Docker / k8s / Unraid bridges.
            if (b[0] == 10) return true;
            // 172.16.0.0/12 — RFC1918; default Docker bridge 172.17/16 falls here.
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168.0.0/16 — RFC1918; most home routers.
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254.0.0/16 — link-local.
            if (b[0] == 169 && b[1] == 254) return true;
            // 100.64.0.0/10 — CGNAT; Tailscale lives here.
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            // fe80::/10 — link-local.
            if (ip.IsIPv6LinkLocal) return true;
            // fc00::/7 — unique-local (ULA).
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            return false;
        }

        return false;
    }
}
