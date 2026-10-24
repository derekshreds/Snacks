using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Auth;

/// <summary>
///     Changing the password must end every existing session — otherwise whoever prompted
///     the change keeps a working cookie for the rest of its 14-day lifetime. Mutates
///     SNACKS_WORK_DIR, so it joins the serialized env collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class SessionRotationTests : IDisposable
{
    private readonly string  _workDir = Directory.CreateTempSubdirectory("snacks-session-").FullName;
    private readonly string? _priorWorkDir;

    public SessionRotationTests()
    {
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static AuthService NewAuthService() => new(new ConfigFileService(new FileService()));

    [Fact]
    public void Password_change_invalidates_existing_sessions()
    {
        var auth = NewAuthService();
        auth.UpdateConfig(enabled: true, "admin", "first-password");
        var token = auth.IssueToken("admin");
        auth.ValidateToken(token, out _).Should().BeTrue();

        auth.UpdateConfig(enabled: true, "admin", "second-password");

        auth.ValidateToken(token, out _).Should().BeFalse();
        auth.ValidateToken(auth.IssueToken("admin"), out _).Should().BeTrue();
    }

    [Fact]
    public void Saving_without_a_new_password_keeps_sessions()
    {
        var auth = NewAuthService();
        auth.UpdateConfig(enabled: true, "admin", "first-password");
        var token = auth.IssueToken("admin");

        auth.UpdateConfig(enabled: true, "admin", newPassword: null);

        auth.ValidateToken(token, out _).Should().BeTrue();
    }
}
