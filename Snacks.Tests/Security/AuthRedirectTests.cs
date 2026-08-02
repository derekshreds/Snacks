using FluentAssertions;
using Snacks.Controllers;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>Regression coverage for the login return-URL open-redirect guard.</summary>
public sealed class AuthRedirectTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("   ", "/")]
    [InlineData("/", "/")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/library-health?filter=failed", "/library-health?filter=failed")]
    [InlineData("~/dashboard", "/dashboard")]
    public void NormalizeReturnUrl_accepts_only_local_paths(string? input, string expected)
    {
        AuthController.NormalizeReturnUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("http://example.test/path")]
    [InlineData("//example.test/path")]
    [InlineData("/\\example.test/path")]
    [InlineData("~/\\example.test/path")]
    [InlineData("dashboard")]
    public void NormalizeReturnUrl_rejects_external_or_ambiguous_paths(string input)
    {
        AuthController.NormalizeReturnUrl(input).Should().Be("/");
    }
}
