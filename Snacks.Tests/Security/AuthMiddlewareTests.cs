using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>Regression coverage for routes that intentionally bypass UI authentication.</summary>
public sealed class AuthMiddlewareTests
{
    [Theory]
    [InlineData("/Auth/Login")]
    [InlineData("/api/health")]
    [InlineData("/api/cluster/heartbeat")]
    [InlineData("/metrics")]
    [InlineData("/css/site.css")]
    [InlineData("/docs/index.html")]
    public void Public_infrastructure_routes_remain_allowlisted(string path)
    {
        AuthMiddleware.IsAllowlisted(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/queue/items")]
    [InlineData("/api/settings")]
    [InlineData("/iframe/homarr")]
    [InlineData("/transcodingHub")]
    [InlineData("/transcodingHub/negotiate")]
    public void User_data_and_realtime_routes_require_the_session_when_auth_is_enabled(string path)
    {
        AuthMiddleware.IsAllowlisted(path).Should().BeFalse();
    }

    [Theory]
    [InlineData("/api/v1/queue")]
    [InlineData("/api/v2/is-server-alive")]
    [InlineData("/api/v2/stats/get-pies")]
    [InlineData("/api/v2/get-nodes")]
    [InlineData("/api/v2/client/status-tables")]
    public void Query_api_keys_are_limited_to_read_only_integration_routes(string path)
    {
        AuthMiddleware.IsReadOnlyIntegrationPath(path).Should().BeTrue();
    }

    [Theory]
    [InlineData("/api/queue/paused")]
    [InlineData("/api/settings")]
    [InlineData("/api/auth/config")]
    [InlineData("/api/v2/future-mutation")]
    public void Mutation_routes_do_not_accept_query_api_keys(string path)
    {
        AuthMiddleware.IsReadOnlyIntegrationPath(path).Should().BeFalse();
    }
}
