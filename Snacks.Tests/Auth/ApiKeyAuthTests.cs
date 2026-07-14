using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Auth;

/// <summary>
///     API key validation: stored key, SNACKS_API_KEY env key, empty-key rules, and the
///     carry-forward of the stored key through <see cref="AuthService.UpdateConfig"/>
///     (which rebuilds AuthConfig from scratch — a missed field there silently wipes the
///     key on any username/password save). Mutates process env vars and SNACKS_WORK_DIR,
///     so it joins the serialized env collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class ApiKeyAuthTests : IDisposable
{
    private readonly string  _workDir = Directory.CreateTempSubdirectory("snacks-apikey-").FullName;
    private readonly string? _priorWorkDir;
    private readonly string? _priorApiKey;

    public ApiKeyAuthTests()
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

    /******************************************************************
     *  SecretCompare
     ******************************************************************/

    [Theory]
    [InlineData("same-key", "same-key", true)]
    [InlineData("same-key", "other-key", false)]
    [InlineData("short", "a-much-longer-secret-value", false)]
    [InlineData("", "", true)]
    public void Constant_time_compare_matches_string_equality(string a, string b, bool expected)
        => SecretCompare.ConstantTimeEquals(a, b).Should().Be(expected);

    /******************************************************************
     *  ValidateApiKey
     ******************************************************************/

    [Fact]
    public void Stored_key_validates_and_wrong_or_empty_keys_do_not()
    {
        var auth = NewAuthService();
        var key  = auth.GenerateApiKey();

        key.Should().StartWith("snk_");
        auth.ValidateApiKey(key).Should().BeTrue();
        auth.ValidateApiKey("wrong").Should().BeFalse();
        auth.ValidateApiKey("").Should().BeFalse();
        auth.ValidateApiKey(null).Should().BeFalse();
    }

    [Fact]
    public void Empty_presented_key_never_matches_an_unconfigured_key()
    {
        var auth = NewAuthService(); // no stored key, no env key

        auth.ValidateApiKey("").Should().BeFalse();
        auth.ValidateApiKey(null).Should().BeFalse();
    }

    [Fact]
    public void Env_key_validates_alongside_the_stored_key()
    {
        Environment.SetEnvironmentVariable("SNACKS_API_KEY", "env-key");
        try
        {
            var auth   = NewAuthService();
            var stored = auth.GenerateApiKey();

            auth.HasEnvApiKey.Should().BeTrue();
            auth.ValidateApiKey("env-key").Should().BeTrue();
            auth.ValidateApiKey(stored).Should().BeTrue();
            auth.ValidateApiKey("neither").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SNACKS_API_KEY", null);
        }
    }

    /******************************************************************
     *  Persistence
     ******************************************************************/

    [Fact]
    public void Generated_key_round_trips_through_auth_json()
    {
        var key = NewAuthService().GenerateApiKey();

        // Fresh service = fresh load from disk.
        NewAuthService().ValidateApiKey(key).Should().BeTrue();
    }

    [Fact]
    public void UpdateConfig_preserves_the_stored_api_key()
    {
        var auth = NewAuthService();
        var key  = auth.GenerateApiKey();

        auth.UpdateConfig(enabled: true, username: "derek", newPassword: "hunter2");

        auth.ValidateApiKey(key).Should().BeTrue();
        auth.GetConfig().ApiKey.Should().Be(key);
    }

    [Fact]
    public void ClearApiKey_removes_only_the_stored_key()
    {
        Environment.SetEnvironmentVariable("SNACKS_API_KEY", "env-key");
        try
        {
            var auth = NewAuthService();
            var key  = auth.GenerateApiKey();

            auth.ClearApiKey();

            auth.ValidateApiKey(key).Should().BeFalse();
            auth.ValidateApiKey("env-key").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SNACKS_API_KEY", null);
        }
    }
}
