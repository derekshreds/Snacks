using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>
///     With no login configured, Snacks serves local-network clients only. Covers the
///     client classification (including clients relayed by a reverse proxy) and the
///     middleware decision. Mutates SNACKS_WORK_DIR, so it joins the serialized env collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class RemoteAccessGateTests : IDisposable
{
    private readonly string  _workDir = Directory.CreateTempSubdirectory("snacks-remote-gate-").FullName;
    private readonly string? _priorWorkDir;
    private readonly string? _priorApiKey;

    public RemoteAccessGateTests()
    {
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        _priorApiKey  = Environment.GetEnvironmentVariable("SNACKS_API_KEY");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
        Environment.SetEnvironmentVariable("SNACKS_API_KEY", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        Environment.SetEnvironmentVariable("SNACKS_API_KEY", _priorApiKey);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static AuthService NewAuthService() => new(new ConfigFileService(new FileService()));

    private static DefaultHttpContext Request(string path, string remote, params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        foreach (var (name, value) in headers) ctx.Request.Headers.Append(name, value);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<bool> PassesGate(AuthService auth, HttpContext ctx)
    {
        var reachedNext = false;
        await new AuthMiddleware(_ => { reachedNext = true; return Task.CompletedTask; }).InvokeAsync(ctx, auth);
        return reachedNext;
    }

    /******************************************************************
     *  Client Classification
     ******************************************************************/

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:192.168.1.20")]
    [InlineData("10.0.0.4")]
    [InlineData("172.17.0.2")]
    [InlineData("100.101.102.103")]
    [InlineData("fd00::12")]
    public void Lan_and_overlay_peers_are_local(string remote)
        => ClientNetwork.IsLocalClient(IPAddress.Parse(remote), new HeaderDictionary()).Should().BeTrue();

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("2001:db8::1")]
    [InlineData("172.32.0.1")]
    public void Public_peers_are_remote(string remote)
        => ClientNetwork.IsLocalClient(IPAddress.Parse(remote), new HeaderDictionary()).Should().BeFalse();

    [Theory]
    [InlineData("X-Forwarded-For", "203.0.113.9")]
    [InlineData("X-Forwarded-For", "192.168.1.7, 203.0.113.9")]
    [InlineData("X-Real-IP", "203.0.113.9")]
    [InlineData("CF-Connecting-IP", "2001:db8::1")]
    [InlineData("Forwarded", "for=203.0.113.9;proto=https")]
    [InlineData("Forwarded", "for=\"[2001:db8::1]:4711\"")]
    [InlineData("X-Forwarded-For", "203.0.113.9:51000")]
    [InlineData("X-Forwarded-For", "unknown")]
    public void A_lan_proxy_relaying_a_public_client_is_remote(string header, string value)
    {
        var headers = new HeaderDictionary { [header] = value };
        ClientNetwork.IsLocalClient(IPAddress.Parse("172.17.0.5"), headers).Should().BeFalse();
    }

    [Fact]
    public void Configured_networks_extend_the_local_ranges()
    {
        var extra = ClientNetwork.ParseNetworks("2001:db8:1::/48; 198.51.100.7, 192.0.2.99/24, not-a-network");

        extra.Should().HaveCount(3);
        ClientNetwork.IsLocalNetwork(IPAddress.Parse("2001:db8:1:42::9"), extra).Should().BeTrue();
        ClientNetwork.IsLocalNetwork(IPAddress.Parse("198.51.100.7"), extra).Should().BeTrue();
        ClientNetwork.IsLocalNetwork(IPAddress.Parse("::ffff:192.0.2.10"), extra).Should().BeTrue();
        ClientNetwork.IsLocalNetwork(IPAddress.Parse("198.51.100.8"), extra).Should().BeFalse();
        ClientNetwork.IsLocalNetwork(IPAddress.Parse("2001:db8:2::1"), extra).Should().BeFalse();
    }

    [Fact]
    public void A_lan_proxy_relaying_a_lan_client_is_local()
    {
        var headers = new HeaderDictionary { ["X-Forwarded-For"] = "192.168.1.30", ["Forwarded"] = "for=192.168.1.30" };
        ClientNetwork.IsLocalClient(IPAddress.Parse("172.17.0.5"), headers).Should().BeTrue();
    }

    [Fact]
    public void Rate_limit_key_uses_the_proxy_appended_client_behind_a_lan_proxy()
    {
        var behindProxy = Request("/Auth/Login", "172.17.0.5", ("X-Forwarded-For", "1.1.1.1, 203.0.113.9"));
        ClientNetwork.GetRateLimitKey(behindProxy).Should().Be("203.0.113.9");

        // A direct public peer can't pick its own bucket with a forged header.
        var direct = Request("/Auth/Login", "198.51.100.4", ("X-Forwarded-For", "10.9.9.9"));
        ClientNetwork.GetRateLimitKey(direct).Should().Be("198.51.100.4");
    }

    /******************************************************************
     *  Middleware Decision (no login configured)
     ******************************************************************/

    [Fact]
    public async Task Lan_clients_keep_open_access_without_a_login()
    {
        var auth = NewAuthService();
        (await PassesGate(auth, Request("/api/settings", "192.168.1.20"))).Should().BeTrue();
        (await PassesGate(auth, Request("/", "127.0.0.1"))).Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/config")]
    [InlineData("/")]
    [InlineData("/transcodingHub/negotiate")]
    public async Task Public_clients_are_refused_without_a_login(string path)
    {
        var ctx = Request(path, "203.0.113.9");
        (await PassesGate(NewAuthService(), ctx)).Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Proxied_public_clients_are_refused_without_a_login()
    {
        var ctx = Request("/api/library/directories", "172.17.0.5", ("X-Forwarded-For", "203.0.113.9"));
        (await PassesGate(NewAuthService(), ctx)).Should().BeFalse();
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/api/cluster/heartbeat")]
    [InlineData("/metrics")]
    [InlineData("/Auth/Login")]
    public async Task Infrastructure_routes_stay_reachable_for_public_clients(string path)
        => (await PassesGate(NewAuthService(), Request(path, "203.0.113.9"))).Should().BeTrue();

    [Fact]
    public async Task A_valid_api_key_admits_public_clients_to_the_api_only()
    {
        var auth = NewAuthService();
        var key  = auth.GenerateApiKey();

        (await PassesGate(auth, Request("/api/queue/items", "203.0.113.9", ("X-Api-Key", key)))).Should().BeTrue();
        (await PassesGate(auth, Request("/api/queue/items", "203.0.113.9", ("X-Api-Key", "snk_wrong")))).Should().BeFalse();
        (await PassesGate(auth, Request("/", "203.0.113.9", ("X-Api-Key", key)))).Should().BeFalse();
    }

    [Fact]
    public async Task A_valid_embed_token_admits_public_clients_to_iframe_pages_only()
    {
        var auth  = NewAuthService();
        var token = auth.GenerateEmbedToken();

        var iframe = Request("/iframe/homarr", "203.0.113.9");
        iframe.Request.QueryString = new QueryString("?embedToken=" + token);
        (await PassesGate(auth, iframe)).Should().BeTrue();

        var api = Request("/api/queue/items", "203.0.113.9");
        api.Request.QueryString = new QueryString("?embedToken=" + token);
        (await PassesGate(auth, api)).Should().BeFalse();
    }
}
