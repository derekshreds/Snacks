using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Snacks.Services;

/// <summary>
///     Classifies where an HTTP request came from. Shared by the remote-access gate in
///     <see cref="AuthMiddleware"/>, the login rate limiter, and <see cref="LocalNetworkOnlyFilter"/>.
/// </summary>
/// <remarks>
///     Forwarding headers are only ever used to make a request <em>less</em> trusted. A client
///     can forge them, but a forged header can only push its own request to the "remote" side
///     of a check — so the gates can see past a LAN reverse proxy (whose socket address would
///     otherwise make every internet client look local) without a trusted-proxy list.
/// </remarks>
internal static class ClientNetwork
{
    /// <summary> Extra CIDR ranges to treat as local, e.g. "203.0.113.0/24, 2001:db8:1::/48". </summary>
    public const string LocalNetworksVariable = "SNACKS_LOCAL_NETWORKS";

    private static readonly string[] ForwardingHeaders =
    {
        "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP", "True-Client-IP", "Forwarded",
    };

    private static readonly IReadOnlyList<IPNetwork> ConfiguredNetworks =
        ParseNetworks(Environment.GetEnvironmentVariable(LocalNetworksVariable));

    private static IReadOnlyList<IPNetwork> _interfaceNetworks = [];
    private static long _interfaceNetworksRefreshed;   // Environment.TickCount64 of the last scan; 0 = never

    /******************************************************************
     *  Classification
     ******************************************************************/

    /// <summary>
    ///     <see langword="true"/> when the socket peer and every address named in a forwarding
    ///     header are on a local network. An unparseable forwarded address counts as remote.
    /// </summary>
    public static bool IsLocalClient(HttpContext ctx) =>
        IsLocalClient(ctx.Connection.RemoteIpAddress, ctx.Request.Headers);

    /// <inheritdoc cref="IsLocalClient(HttpContext)"/>
    public static bool IsLocalClient(IPAddress? remote, IHeaderDictionary headers)
    {
        // Kestrel always fills in the peer for TCP connections; only in-process hosts
        // (TestServer) leave it null. Note this treats a null peer as local, whereas
        // LocalNetworkOnlyFilter rejects it — revisit if a Unix-socket or named-pipe
        // listener is ever added.
        if (remote != null && !IsLocalNetwork(remote)) return false;

        foreach (var name in ForwardingHeaders)
        {
            foreach (var value in headers[name])
            {
                if (string.IsNullOrWhiteSpace(value)) continue;

                foreach (var element in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var token = element;
                    if (name == "Forwarded")
                    {
                        // RFC 7239: for=192.0.2.60;proto=https;by=203.0.113.43
                        var forPair = element.Split(';', StringSplitOptions.TrimEntries)
                            .FirstOrDefault(p => p.StartsWith("for=", StringComparison.OrdinalIgnoreCase));
                        if (forPair == null) continue;
                        token = forPair[4..];
                    }

                    if (!TryParseAddress(token, out var forwarded) || !IsLocalNetwork(forwarded)) return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     Returns <see langword="true"/> for addresses in any of the LAN/overlay ranges common
    ///     to Snacks deployments: loopback, RFC1918, CGNAT (Tailscale / overlay), link-local,
    ///     IPv6 unique-local, an IPv6 prefix this machine is attached to, or a range listed in
    ///     <see cref="LocalNetworksVariable"/>. Other publicly routable addresses are remote.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress ip) =>
        IsLocalNetwork(ip, ConfiguredNetworks) || InNetworks(ip, GetInterfaceNetworks());

    /// <summary> Testable core: the fixed private ranges plus <paramref name="extraNetworks"/>. </summary>
    internal static bool IsLocalNetwork(IPAddress ip, IReadOnlyList<IPNetwork> extraNetworks)
    {
        // Normalize ::ffff:a.b.c.d → a.b.c.d so IPv4 checks apply.
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (InNetworks(ip, extraNetworks)) return true;

        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
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

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
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

    /// <summary>
    ///     Parses a comma/semicolon/space separated CIDR list. A bare address counts as a single
    ///     host, host bits in "192.168.1.5/24" are masked off, and malformed entries are skipped
    ///     with a warning.
    /// </summary>
    internal static IReadOnlyList<IPNetwork> ParseNetworks(string? value)
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in (value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash   = entry.IndexOf('/');
            var address = slash < 0 ? entry : entry[..slash];
            if (IPAddress.TryParse(address, out var ip))
            {
                if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
                var maxPrefix = ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
                var prefix    = maxPrefix;
                if (slash < 0 || (int.TryParse(entry[(slash + 1)..], out prefix) && prefix >= 0 && prefix <= maxPrefix))
                {
                    networks.Add(new IPNetwork(MaskToPrefix(ip, prefix), prefix));
                    continue;
                }
            }
            Log.Warning("{Variable}: ignoring '{Entry}' — not an address or CIDR range", LocalNetworksVariable, entry);
        }
        return networks;
    }

    private static bool InNetworks(IPAddress ip, IReadOnlyList<IPNetwork> networks)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        foreach (var network in networks)
            if (network.Contains(ip)) return true;
        return false;
    }

    /// <summary>
    ///     Global IPv6 prefixes (/64 or narrower) assigned to this machine's interfaces, refreshed
    ///     every minute. Dual-stack home networks hand clients public IPv6 addresses from the same
    ///     prefix, and browsers prefer IPv6 when a hostname resolves to both families — without
    ///     this, LAN clients browsing by hostname would be treated as remote. IPv4 subnets aren't
    ///     included: cloud hosts often sit in large shared public IPv4 subnets.
    /// </summary>
    private static IReadOnlyList<IPNetwork> GetInterfaceNetworks()
    {
        var now       = Environment.TickCount64;
        var refreshed = Interlocked.Read(ref _interfaceNetworksRefreshed);
        if (refreshed != 0 && now - refreshed < 60_000) return Volatile.Read(ref _interfaceNetworks);

        var networks = new List<IPNetwork>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetworkV6
                        || IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) continue;

                    int prefixLength;
                    try { prefixLength = unicast.PrefixLength; }
                    catch (PlatformNotSupportedException) { continue; }
                    if (prefixLength < 64 || prefixLength > 128) continue;

                    networks.Add(new IPNetwork(MaskToPrefix(address, prefixLength), prefixLength));
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Warning(ex, "Could not enumerate network interfaces for local-network detection");
        }

        Volatile.Write(ref _interfaceNetworks, networks);
        Interlocked.Exchange(ref _interfaceNetworksRefreshed, now);
        return networks;
    }

    private static IPAddress MaskToPrefix(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bitsInByte = Math.Clamp(prefixLength - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bitsInByte));
        }
        return new IPAddress(bytes);
    }

    /******************************************************************
     *  Rate-Limit Identity
     ******************************************************************/

    /// <summary>
    ///     Best-effort client identity for rate limiting. Behind a LAN reverse proxy the socket
    ///     peer is the proxy, so the right-most <c>X-Forwarded-For</c> entry — the one the proxy
    ///     appended, which the client cannot choose — identifies the client instead.
    /// </summary>
    public static string GetRateLimitKey(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote != null && IsLocalNetwork(remote))
        {
            var proxyAppended = ctx.Request.Headers["X-Forwarded-For"].ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault();
            if (proxyAppended != null && TryParseAddress(proxyAppended, out var client))
                return client.ToString();
        }

        return remote?.ToString() ?? "unknown";
    }

    /******************************************************************
     *  Helpers
     ******************************************************************/

    /// <summary>
    ///     Parses a forwarded node: bare IPv4/IPv6, quoted, bracketed IPv6 with optional port,
    ///     or IPv4 with port. Obfuscated identifiers and "unknown" do not parse.
    /// </summary>
    internal static bool TryParseAddress(string token, [NotNullWhen(true)] out IPAddress? address)
    {
        token = token.Trim().Trim('"');

        if (token.StartsWith('['))
        {
            var close = token.IndexOf(']');
            if (close > 0) token = token[1..close];
        }
        else if (token.Count(c => c == ':') == 1)
        {
            token = token[..token.IndexOf(':')];
        }

        return IPAddress.TryParse(token, out address);
    }
}
