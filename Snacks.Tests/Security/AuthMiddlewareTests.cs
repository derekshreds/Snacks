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
    [InlineData("/transcodingHub")]
    [InlineData("/transcodingHub/negotiate")]
    public void User_data_and_realtime_routes_require_the_session_when_auth_is_enabled(string path)
    {
        AuthMiddleware.IsAllowlisted(path).Should().BeFalse();
    }
}
