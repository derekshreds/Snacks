using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Integration;

/// <summary>
///     Pins the Plex / Jellyfin rescans after an encode. A file is matched only against
///     libraries of its kind (music libraries for music, the rest for video); a server with
///     no library of that kind is skipped instead of being fully refreshed per file; scoped
///     Plex paths keep Plex's own separators; and Jellyfin is authenticated with the header
///     current Jellyfin accepts. Mutates the process-wide SNACKS_WORK_DIR, so it shares the
///     EnvConfigOverrides collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class MediaServerRescanTests : IDisposable
{
    private const string TrackPath = "/mnt/tank/media/music/Some Artist/Some Album (2001)/01 - Track.flac";
    private const string MoviePath = "/mnt/tank/Movies/Some Movie (2010)/Some Movie (2010).mkv";

    private readonly string  _workDir;
    private readonly string? _priorWorkDir;

    public MediaServerRescanTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"snacks-media-rescan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static IntegrationService NewService(FakeMediaServers servers, bool plex = false, bool jellyfin = false)
    {
        var svc = new IntegrationService(new ConfigFileService(new FileService()), new StubFactory(servers));
        svc.SaveConfig(new IntegrationConfig
        {
            Plex     = new MediaServerIntegration { Enabled = plex,     RescanOnComplete = true, BaseUrl = "http://plex.test:32400",    Token = "plex-token" },
            Jellyfin = new MediaServerIntegration { Enabled = jellyfin, RescanOnComplete = true, BaseUrl = "http://jellyfin.test:8096", Token = "jf-key" },
        });
        return svc;
    }


    /******************************************************************
     *  Plex
     ******************************************************************/

    [Theory]
    [InlineData("/data/movies",     "/data/movies/Some Movie (2010)")]
    [InlineData(@"D:\Media\Movies", @"D:\Media\Movies\Some Movie (2010)")]
    public async Task Plex_scoped_refresh_keeps_plexs_own_separators(string plexRoot, string expectedScope)
    {
        // The refresh path must be in Plex's style whatever OS Snacks runs on. A Linux
        // Plex answers 200 to a backslash path and scans a folder that doesn't exist.
        var servers = new FakeMediaServers(plexSections: [("1", "movie", plexRoot)]);

        await NewService(servers, plex: true).TriggerRescansAsync(MoviePath, MediaKind.Video);

        servers.PlexRefreshes.Should().Equal($"1 {expectedScope}");
    }


    [Fact]
    public async Task Plex_without_a_music_library_is_not_refreshed_for_music()
    {
        // Before: no root matched, so every track asked Plex to rescan every library.
        var servers = new FakeMediaServers(plexSections: [("1", "movie", "/data/movies"), ("2", "show", "/data/tv")]);

        await NewService(servers, plex: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.PlexRefreshes.Should().BeEmpty();
    }


    [Fact]
    public async Task Plex_maps_music_only_onto_music_libraries()
    {
        // "/data/media" (movies) shares the "media" folder name with the track's path and is
        // listed first, so unfiltered it would win and refresh a folder that isn't there.
        var servers = new FakeMediaServers(plexSections: [("1", "movie", "/data/media"), ("7", "artist", "/data/music")]);

        await NewService(servers, plex: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.PlexRefreshes.Should().Equal("7 /data/music/Some Artist/Some Album (2001)");
    }


    [Fact]
    public async Task Plex_video_is_not_mapped_onto_music_libraries()
    {
        var servers = new FakeMediaServers(plexSections: [("7", "artist", "/data/Movies"), ("1", "movie", "/data/films")]);

        await NewService(servers, plex: true).TriggerRescansAsync(MoviePath, MediaKind.Video);

        // The music library's "Movies" folder would be the only name match; the video
        // library doesn't line up, so this falls back to a full refresh as before.
        servers.PlexRefreshes.Should().Equal("all");
    }


    [Fact]
    public async Task Plex_music_library_that_does_not_line_up_still_gets_a_full_refresh()
    {
        var servers = new FakeMediaServers(plexSections: [("7", "artist", "/data/audio")]);

        await NewService(servers, plex: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.PlexRefreshes.Should().Equal("all");
    }


    [Fact]
    public async Task Plex_library_lookup_failure_keeps_the_full_refresh()
    {
        var servers = new FakeMediaServers(plexSections: null);

        await NewService(servers, plex: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.PlexRefreshes.Should().Equal("all");
    }


    /******************************************************************
     *  Jellyfin
     ******************************************************************/

    [Fact]
    public async Task Jellyfin_requests_use_the_authorization_header_current_jellyfin_accepts()
    {
        // Jellyfin 12 ships with legacy authorization off: X-Emby-Token gets 401.
        var servers = new FakeMediaServers(jellyfinFolders: [("Movies", "movies", "/data/movies")]);
        var svc     = NewService(servers, jellyfin: true);

        var (ok, _) = await svc.TestJellyfinAsync("http://jellyfin.test:8096", "jf-key");
        await svc.TriggerRescansAsync(MoviePath, MediaKind.Video);

        ok.Should().BeTrue();
        servers.JellyfinAuth.Should().NotBeEmpty().And.OnlyContain(a => a == "MediaBrowser Token=\"jf-key\"");
        servers.JellyfinUpdates.Should().Equal("/data/movies/Some Movie (2010)/Some Movie (2010).mkv");
    }


    [Fact]
    public async Task Jellyfin_without_a_music_library_is_not_refreshed_for_music()
    {
        var servers = new FakeMediaServers(jellyfinFolders: [("Movies", "movies", "/data/movies"), ("Shows", "tvshows", "/data/tv")]);

        await NewService(servers, jellyfin: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.JellyfinUpdates.Should().BeEmpty();
        servers.JellyfinFullRefreshes.Should().Be(0);
    }


    [Fact]
    public async Task Jellyfin_maps_music_only_onto_music_libraries()
    {
        var servers = new FakeMediaServers(jellyfinFolders: [("Media", "movies", "/data/media"), ("Music", "music", "/data/music")]);

        await NewService(servers, jellyfin: true).TriggerRescansAsync(TrackPath, MediaKind.Music);

        servers.JellyfinUpdates.Should().Equal("/data/music/Some Artist/Some Album (2001)/01 - Track.flac");
        servers.JellyfinFullRefreshes.Should().Be(0);
    }


    [Fact]
    public async Task Jellyfin_mixed_library_counts_as_video()
    {
        // "Mixed movies and shows" libraries report no CollectionType.
        var servers = new FakeMediaServers(jellyfinFolders: [("Everything", null, "/data/Movies")]);

        await NewService(servers, jellyfin: true).TriggerRescansAsync(MoviePath, MediaKind.Video);

        servers.JellyfinUpdates.Should().Equal("/data/Movies/Some Movie (2010)/Some Movie (2010).mkv");
    }


    /******************************************************************
     *  HTTP stubs
     ******************************************************************/

    /// <summary>
    ///     Minimal Plex and Jellyfin. Serves typed library lists and records each refresh as
    ///     "{sectionKey} {path}" (Plex scoped), "all" (Plex full), the paths in Jellyfin's
    ///     media-updated notices, and Jellyfin's Authorization headers.
    /// </summary>
    private sealed class FakeMediaServers : HttpMessageHandler
    {
        private readonly (string key, string type, string path)[]?   _plexSections;
        private readonly (string name, string? type, string path)[]? _jellyfinFolders;

        private readonly ConcurrentQueue<string> _plexRefreshes   = new();
        private readonly ConcurrentQueue<string> _jellyfinUpdates = new();
        private readonly ConcurrentQueue<string> _jellyfinAuth    = new();
        public int JellyfinFullRefreshes;

        public FakeMediaServers((string key, string type, string path)[]? plexSections = null,
                                (string name, string? type, string path)[]? jellyfinFolders = null)
        {
            _plexSections    = plexSections;
            _jellyfinFolders = jellyfinFolders;
        }

        public IReadOnlyList<string> PlexRefreshes   => _plexRefreshes.ToList();
        public IReadOnlyList<string> JellyfinUpdates => _jellyfinUpdates.ToList();
        public IReadOnlyList<string> JellyfinAuth    => _jellyfinAuth.ToList();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;

            if (uri.Host == "plex.test")
            {
                if (uri.AbsolutePath == "/library/sections")
                {
                    if (_plexSections == null) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                    return Json(JsonSerializer.Serialize(new
                    {
                        MediaContainer = new
                        {
                            Directory = _plexSections.Select(s => new { key = s.key, type = s.type, Location = new[] { new { path = s.path } } }),
                        },
                    }));
                }
                if (uri.AbsolutePath == "/library/sections/all/refresh")
                {
                    _plexRefreshes.Enqueue("all");
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                var parts = uri.AbsolutePath.Split('/');
                if (parts is ["", "library", "sections", var key, "refresh"])
                {
                    _plexRefreshes.Enqueue($"{key} {System.Web.HttpUtility.ParseQueryString(uri.Query)["path"]}");
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            if (uri.Host == "jellyfin.test")
            {
                if (request.Headers.TryGetValues("X-Emby-Token", out _))
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                if (request.Headers.TryGetValues("Authorization", out var auth))
                    _jellyfinAuth.Enqueue(auth.Single());

                switch (uri.AbsolutePath)
                {
                    case "/System/Info":
                        return Json("""{"Version":"12.2.0"}""");
                    case "/Library/VirtualFolders" when _jellyfinFolders != null:
                        return Json(JsonSerializer.Serialize(_jellyfinFolders.Select(f =>
                            new { Name = f.name, CollectionType = f.type, Locations = new[] { f.path } })));
                    case "/Library/Media/Updated":
                        using (var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)))
                            foreach (var u in doc.RootElement.GetProperty("Updates").EnumerateArray())
                                _jellyfinUpdates.Enqueue(u.GetProperty("Path").GetString()!);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case "/Library/Refresh":
                        Interlocked.Increment(ref JellyfinFullRefreshes);
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
