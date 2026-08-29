using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Integration;

/// <summary>
///     Pins the Luna connector's privacy and security contract: library reads and
///     adds must never leak local paths, file names, sizes, or API keys; adds must
///     stay idempotent across lease retries; capabilities must follow each explicit
///     permission toggle; custom/insecure URLs must require the explicit local-testing
///     overrides; and connect/disconnect must persist only the scoped refresh token
///     (owner-only on Unix) and remove it — backup included — on revocation.
///     Mutates the process-wide SNACKS_WORK_DIR, so it must share the
///     EnvConfigOverrides collection with every other suite that touches that env var.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class LunaIntegrationTests : IDisposable
{
    private readonly string  _workDir;
    private readonly string? _priorWorkDir;
    private readonly string? _priorCustomUrl;

    public LunaIntegrationTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"snacks-luna-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        _priorCustomUrl = Environment.GetEnvironmentVariable(LunaConnectionService.AllowCustomUrlEnvironmentVariable);
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
        Environment.SetEnvironmentVariable(LunaConnectionService.AllowCustomUrlEnvironmentVariable, "true");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        Environment.SetEnvironmentVariable(LunaConnectionService.AllowCustomUrlEnvironmentVariable, _priorCustomUrl);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Library_read_is_paginated_and_removes_local_paths_and_files()
    {
        const string responseJson = """
            [
              {"id":1,"title":"Alien","year":1979,"tmdbId":348,"genres":["Horror","Science Fiction"],"path":"/private/movies/Alien","movieFile":{"relativePath":"Alien.mkv","size":123456},"hasFile":true},
              {"id":2,"title":"Arrival","year":2016,"tmdbId":329865,"genres":["Drama","Science Fiction"],"path":"/private/movies/Arrival","hasFile":true}
            ]
            """;
        var handler = new StubHandler(_ => Json(responseJson));
        var (integrations, executor, _) = Build(handler, new IntegrationConfig
        {
            Luna = new LunaIntegration { Enabled = true, AllowLibraryReads = true },
            Radarr = EnabledArr("http://radarr.test:7878"),
        });

        using var args = JsonDocument.Parse("""{"offset":1,"limit":1}""");
        var execution = await executor.ExecuteAsync(
            "radarr.library.read",
            args.RootElement,
            CancellationToken.None);

        execution.Succeeded.Should().BeTrue();
        var result = execution.Result!.Value;
        result.GetProperty("total").GetInt32().Should().Be(2);
        result.GetProperty("returned").GetInt32().Should().Be(1);
        result.GetProperty("items")[0].GetProperty("title").GetString().Should().Be("Arrival");
        var serialized = result.GetRawText();
        serialized.Should().NotContain("/private");
        serialized.Should().NotContain("relativePath");
        serialized.Should().NotContain("123456");
        serialized.Should().NotContain(integrations.GetConfig().Radarr.ApiKey);
    }

    [Fact]
    public async Task Movie_add_uses_local_defaults_but_never_returns_the_root_path()
    {
        string? posted = null;
        var handler = new StubHandler(async request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (request.Method == HttpMethod.Get && path.StartsWith("/api/v3/movie?tmdbId=603")) return Json("[]");
            if (path.StartsWith("/api/v3/movie/lookup/tmdb"))
                return Json("""{"title":"The Matrix","year":1999,"tmdbId":603,"genres":["Action","Science Fiction"]}""");
            if (path == "/api/v3/rootfolder")
                return Json("""[{"id":7,"path":"/secret/media/movies","accessible":true}]""");
            if (path == "/api/v3/qualityprofile")
                return Json("""[{"id":3,"name":"HD-1080p"}]""");
            if (request.Method == HttpMethod.Post && path == "/api/v3/movie")
            {
                posted = await request.Content!.ReadAsStringAsync();
                return Json("""{"id":91,"title":"The Matrix","year":1999,"tmdbId":603,"hasFile":false}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var (_, executor, _) = Build(handler, new IntegrationConfig
        {
            Luna = new LunaIntegration { Enabled = true, AllowLibraryChanges = true },
            Radarr = EnabledArr("http://radarr.test:7878"),
        });

        using var args = JsonDocument.Parse("""{"tmdbId":603,"searchForMovie":true}""");
        var execution = await executor.ExecuteAsync("radarr.movie.add", args.RootElement, CancellationToken.None);

        execution.Succeeded.Should().BeTrue();
        posted.Should().NotBeNull();
        using var payload = JsonDocument.Parse(posted!);
        payload.RootElement.GetProperty("rootFolderPath").GetString().Should().Be("/secret/media/movies");
        payload.RootElement.GetProperty("qualityProfileId").GetInt32().Should().Be(3);
        payload.RootElement.GetProperty("addOptions").GetProperty("searchForMovie").GetBoolean().Should().BeTrue();
        execution.Result!.Value.GetRawText().Should().NotContain("/secret/media/movies");
    }

    [Fact]
    public async Task Existing_series_makes_add_retry_idempotent()
    {
        var handler = new StubHandler(request =>
        {
            request.Method.Should().Be(HttpMethod.Get);
            request.RequestUri!.PathAndQuery.Should().Be("/api/v3/series?tvdbId=121361");
            return Json("""[{"id":12,"title":"Game of Thrones","year":2011,"tvdbId":121361}]""");
        });
        var (_, executor, _) = Build(handler, new IntegrationConfig
        {
            Luna = new LunaIntegration { Enabled = true, AllowLibraryChanges = true },
            Sonarr = EnabledArr("http://sonarr.test:8989"),
        });

        using var args = JsonDocument.Parse("""{"tvdbId":121361}""");
        var execution = await executor.ExecuteAsync("sonarr.series.add", args.RootElement, CancellationToken.None);

        execution.Succeeded.Should().BeTrue();
        execution.Result!.Value.GetProperty("alreadyExists").GetBoolean().Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public void Capabilities_follow_each_explicit_privacy_permission()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var (_, executor, _) = Build(handler, new IntegrationConfig
        {
            Luna = new LunaIntegration
            {
                Enabled = true,
                AllowLibraryReads = false,
                AllowLibraryChanges = true,
            },
            Radarr = EnabledArr("http://radarr.test:7878"),
        });

        executor.GetCapabilities().Should().BeEquivalentTo(
            new[] { "radarr.catalog.search", "radarr.movie.add" });
        executor.GetCapabilities().Should().NotContain("radarr.library.read");
    }

    [Fact]
    public void Production_uses_only_the_official_URL_and_local_testing_requires_both_overrides()
    {
        var customVariable = LunaConnectionService.AllowCustomUrlEnvironmentVariable;
        var insecureVariable = LunaConnectionService.AllowInsecureHttpEnvironmentVariable;
        var previousCustom = Environment.GetEnvironmentVariable(customVariable);
        var previousInsecure = Environment.GetEnvironmentVariable(insecureVariable);
        try
        {
            Environment.SetEnvironmentVariable(customVariable, null);
            Environment.SetEnvironmentVariable(insecureVariable, null);
            LunaConnectionService.NormalizeBaseUrl("https://veryluna.com/")
                .Should().Be(LunaConnectionService.OfficialBaseUrl);
            Action customHttpsWithoutOverride = () => LunaConnectionService.NormalizeBaseUrl("https://luna.test");
            customHttpsWithoutOverride.Should().Throw<LunaConnectionException>()
                .WithMessage("*only at https://veryluna.com*");
            Action customHttpWithoutOverride = () => LunaConnectionService.NormalizeBaseUrl("http://luna:8080");
            customHttpWithoutOverride.Should().Throw<LunaConnectionException>();

            Environment.SetEnvironmentVariable(customVariable, "true");
            Action insecureWithoutOverride = () => LunaConnectionService.NormalizeBaseUrl("http://luna:8080");
            insecureWithoutOverride.Should().Throw<LunaConnectionException>()
                .WithMessage("*must use HTTPS*");
            LunaConnectionService.NormalizeBaseUrl("https://luna.test")
                .Should().Be("https://luna.test");

            Environment.SetEnvironmentVariable(insecureVariable, "true");
            LunaConnectionService.NormalizeBaseUrl("http://luna:8080")
                .Should().Be("http://luna:8080");
        }
        finally
        {
            Environment.SetEnvironmentVariable(customVariable, previousCustom);
            Environment.SetEnvironmentVariable(insecureVariable, previousInsecure);
        }
    }

    [Fact]
    public async Task Connect_persists_only_the_scoped_session_and_polls_outbound()
    {
        var seenPaths = new List<string>();
        var agentDisabled = false;
        var handler = new StubHandler(async request =>
        {
            seenPaths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/agent-login"))
            {
                var loginBody = await request.Content!.ReadAsStringAsync();
                loginBody.Should().Contain("correct horse battery staple");
                return Json(JsonSerializer.Serialize(new
                {
                    accessToken = "access-token",
                    expiresIn = 900,
                    refreshToken = "daemon-refresh",
                    refreshExpiresAt = DateTime.UtcNow.AddDays(60),
                    user = new { email = "person@example.test" },
                }));
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/remote-agents/register"))
            {
                request.Headers.Authorization!.Scheme.Should().Be("Bearer");
                request.Headers.Authorization.Parameter.Should().Be("access-token");
                var registrationBody = await request.Content!.ReadAsStringAsync();
                registrationBody.Should().Contain("radarr.catalog.search");
                registrationBody.Should().Contain("radarr.library.read");
                return Json("""{"agentId":"agent-123","pollIntervalSeconds":2,"serverTimeUtc":"2026-08-28T00:00:00Z","capabilities":[]}""");
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/lease"))
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Delete
                && request.RequestUri.AbsolutePath.EndsWith("/remote-agents/agent-123"))
            {
                agentDisabled = true;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/auth/logout"))
                return Json("{}", HttpStatusCode.OK);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var (integrations, executor, factory) = Build(handler, new IntegrationConfig
        {
            Luna = new LunaIntegration
            {
                Enabled = true,
                BaseUrl = "https://luna.test",
                AllowLibraryReads = true,
            },
            Radarr = EnabledArr("http://radarr.test:7878"),
        });
        var configFiles = new ConfigFileService(new FileService());
        var connection = new LunaConnectionService(configFiles, integrations, executor, factory);

        var status = await connection.ConnectAsync(
            "https://luna.test",
            "person@example.test",
            "correct horse battery staple",
            "Test Snacks",
            CancellationToken.None);

        status.Connected.Should().BeTrue();
        status.Online.Should().BeTrue("successful registration is already a live Luna round-trip");
        status.AgentId.Should().Be("agent-123");
        status.Capabilities.Should().BeEquivalentTo(
            new[] { "radarr.catalog.search", "radarr.library.read" });
        (await connection.PollOnceAsync(CancellationToken.None)).Should().BeFalse();
        connection.GetStatus().Online.Should().BeTrue();

        var statePath = configFiles.GetConfigPath("luna-connection.json");
        var disk = await File.ReadAllTextAsync(statePath);
        disk.Should().Contain("daemon-refresh");
        disk.Should().NotContain("correct horse battery staple");
        disk.Should().NotContain("access-token");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(statePath);
            (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite))
                .Should().Be((UnixFileMode)0);
        }

        await connection.DisconnectAsync(CancellationToken.None);
        connection.GetStatus().Connected.Should().BeFalse();
        agentDisabled.Should().BeTrue();
        (await File.ReadAllTextAsync(statePath)).Should().NotContain("daemon-refresh");
        File.Exists(statePath + ".bak").Should().BeFalse("disconnect must not leave a recoverable token backup");
        seenPaths.Should().Contain(path => path.EndsWith("/auth/agent-login"));
        seenPaths.Should().Contain(path => path.EndsWith("/remote-agents/register"));
        seenPaths.Should().Contain(path => path.EndsWith("/lease"));
        seenPaths.Should().Contain(path => path.EndsWith("/auth/logout"));
    }

    /******************************************************************
     *  Fixtures & HTTP stubs
     ******************************************************************/

    private (IntegrationService Integrations, LunaTaskExecutor Executor, StubFactory Factory) Build(
        StubHandler handler,
        IntegrationConfig config)
    {
        var factory = new StubFactory(handler);
        var integrations = new IntegrationService(new ConfigFileService(new FileService()), factory);
        integrations.SaveConfig(config);
        return (integrations, new LunaTaskExecutor(integrations, factory), factory);
    }

    private static ArrIntegration EnabledArr(string url) => new()
    {
        Enabled = true,
        BaseUrl = url,
        ApiKey = "local-secret-key",
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;
        public int Calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this(request => Task.FromResult(respond(request))) { }

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            cancellationToken.ThrowIfCancellationRequested();
            return await _respond(request);
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        public StubFactory(StubHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
