using System.Net;
using System.Text;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Integration;

/// <summary>
///     Pins the failure behavior of the Sonarr/Radarr original-language lookup that the
///     dispatch path depends on: an HttpClient timeout (a TaskCanceledException with an
///     un-cancelled caller token) must degrade to null — NOT propagate and unwind the
///     scheduler — and failures must be negative-cached so a struggling Arr instance
///     isn't re-fetched on every dispatch. Mutates the process-wide SNACKS_WORK_DIR,
///     so it must share the EnvConfigOverrides collection with every other suite
///     that touches that env var — xUnit runs distinct collections in parallel, and
///     an un-collected class racing ApiKeyAuthTests flips the work dir mid-test
///     (auth.json written to one dir, validated against another).
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class OriginalLanguageLookupTests : IDisposable
{
    private const string TvPath = "/tv/Some Show (2020)/Season 01/Some Show - S01E01.mkv";

    private readonly string  _workDir;
    private readonly string? _priorWorkDir;

    public OriginalLanguageLookupTests()
    {
        _workDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"snacks-lookup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static IntegrationService NewService(StubHandler handler)
    {
        var svc = new IntegrationService(new ConfigFileService(new FileService()), new StubFactory(handler));
        svc.SaveConfig(new IntegrationConfig
        {
            Sonarr = new ArrIntegration { Enabled = true, BaseUrl = "http://sonarr.test:8989", ApiKey = "k" },
            Radarr = new ArrIntegration { Enabled = true, BaseUrl = "http://radarr.test:7878", ApiKey = "k" },
        });
        return svc;
    }

    private static StubHandler CatalogHandler(string json) => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });


    [Fact]
    public async Task Timeout_with_uncancelled_token_returns_null_instead_of_throwing()
    {
        // HttpClient timeouts surface as TaskCanceledException even when the caller
        // passed CancellationToken.None — the scheduler's exact call shape.
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        var svc = NewService(handler);

        var result = await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None);

        result.Should().BeNull();
        handler.Calls.Should().Be(1);
    }


    [Fact]
    public async Task Genuine_caller_cancellation_still_propagates()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("cancelled"));
        var svc = NewService(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.LookupOriginalLanguageAsync(TvPath, "sonarr", cts.Token));
    }


    [Fact]
    public async Task Failed_fetch_is_negative_cached()
    {
        var handler = new StubHandler(_ => throw new TaskCanceledException("timed out"));
        var svc = NewService(handler);

        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().BeNull();
        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().BeNull();

        // Second lookup within the failure TTL must not re-hit the endpoint.
        handler.Calls.Should().Be(1);
    }


    [Fact]
    public async Task NonSuccess_status_is_negative_cached()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var svc = NewService(handler);

        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().BeNull();
        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().BeNull();

        handler.Calls.Should().Be(1);
    }


    [Fact]
    public async Task Successful_fetch_resolves_language_and_caches_the_catalogue()
    {
        const string json = """[{"path":"/tv/Some Show (2020)","originalLanguage":{"name":"English"}}]""";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var svc = NewService(handler);

        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().Be("en");
        (await svc.LookupOriginalLanguageAsync(TvPath, "sonarr", CancellationToken.None)).Should().Be("en");

        handler.Calls.Should().Be(1);
    }


    /******************************************************************
     *  Folder-name fallback (Arr paths that don't prefix the local path)
     ******************************************************************/

    [Theory]
    [InlineData("sonarr", """[{"path":"/tv/Mr. Robot (2015)","originalLanguage":{"name":"English"}}]""",
                "/mnt/media/TV/Mr. Robot (2015)/Season 01/Mr. Robot - S01E01.mkv", "en")]
    [InlineData("radarr", """[{"path":"/movies/Mr. Nobody (2009)","originalLanguage":{"name":"French"}}]""",
                "/mnt/media/Movies/Mr. Nobody (2009)/Mr. Nobody (2009) Bluray-1080p.mkv", "fr")]
    public async Task Fallback_matches_folder_names_containing_dots(string provider, string catalog, string path, string expected)
    {
        // A dot in a directory name is not a file extension: "Mr. Robot (2015)" must not
        // be compared as "Mr".
        var svc = NewService(CatalogHandler(catalog));

        (await svc.LookupOriginalLanguageAsync(path, provider, CancellationToken.None)).Should().Be(expected);
    }


    [Theory]
    [InlineData(@"D:\Media\Movies\TRON - Legacy (2010).mkv")]
    [InlineData("/mnt/media/Movies/TRON - Legacy (2010)/TRON - Legacy (2010).mkv")]
    public async Task Fallback_still_matches_a_movie_file_named_like_the_arr_folder(string path)
    {
        // Flat layout (file directly in Movies) relies on the file name minus its extension.
        var svc = NewService(CatalogHandler("""[{"path":"/movies/TRON - Legacy (2010)","originalLanguage":{"name":"English"}}]"""));

        (await svc.LookupOriginalLanguageAsync(path, "radarr", CancellationToken.None)).Should().Be("en");
    }


    /******************************************************************
     *  HTTP stubs
     ******************************************************************/

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;
        public StubFactory(StubHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
