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
///     Pins the rescans that follow a replaced original. Each command is scoped to one item —
///     a Lidarr album folder, a Sonarr series, or a Radarr movie, never a library-wide scan —
///     and replacements within the same item coalesce into one command. A request the Arr
///     merged into a scan that was already running is re-armed so the replacement isn't
///     missed, and a movie Radarr unmonitored during the rescan is monitored again. Mutates
///     the process-wide SNACKS_WORK_DIR, so it shares the EnvConfigOverrides collection.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class ArrRescanTests : IDisposable
{
    private const string AlbumDir = "/mnt/tank/Music/Some Artist/Some Album (2001)";
    private const string ShowDir  = "/mnt/tank/TV/Some Show (2020)";
    private const string MovieDir = "/mnt/tank/Movies/Some Movie (2010)";

    private const string SeriesJson = """[{"id":5,"path":"/tv/Other Show (2019)"},{"id":7,"path":"/tv/Some Show (2020)"}]""";
    private const string MoviesJson = """[{"id":3,"path":"/movies/Other Movie (2001)"},{"id":9,"path":"/movies/Some Movie (2010)"}]""";

    private readonly string  _workDir;
    private readonly string? _priorWorkDir;

    public ArrRescanTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"snacks-arr-rescan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _workDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        try { Directory.Delete(_workDir, recursive: true); } catch { }
    }

    private static IntegrationService NewService(FakeServers servers, bool lidarr = true, bool sonarr = true, bool radarr = true)
    {
        var svc = new IntegrationService(new ConfigFileService(new FileService()), new StubFactory(servers));
        svc.SaveConfig(new IntegrationConfig
        {
            Lidarr = new ArrIntegration { Enabled = lidarr, BaseUrl = "http://lidarr.test:8686/", ApiKey = "lidarr-key" },
            Sonarr = new ArrIntegration { Enabled = sonarr, BaseUrl = "http://sonarr.test:8989",  ApiKey = "sonarr-key" },
            Radarr = new ArrIntegration { Enabled = radarr, BaseUrl = "http://radarr.test:7878",  ApiKey = "radarr-key" },
        });
        svc.RescanQuietPeriod      = TimeSpan.FromMilliseconds(150);
        svc.ArrCommandPollInterval = TimeSpan.FromMilliseconds(20);
        return svc;
    }

    // Burst tests queue items 30 ms apart; a window this much wider keeps a slow CI runner
    // from stretching one gap past it and splitting the burst into two commands.
    private static readonly TimeSpan BurstQuietPeriod = TimeSpan.FromSeconds(1);

    /// <summary> Polls until <paramref name="done"/> holds, then waits out stragglers. </summary>
    private static async Task SettleAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(600);
    }


    /******************************************************************
     *  Lidarr
     ******************************************************************/

    [Fact]
    public async Task Lidarr_rescan_targets_the_album_folder_under_its_root()
    {
        var servers = new FakeServers(lidarrRoots: """[{"id":1,"path":"/music/"}]""");
        var svc     = NewService(servers);

        svc.QueueArrRescan(AlbumDir + "/01 - Track.flac", MediaKind.Music);
        await SettleAsync(() => servers.Commands.Count >= 1);

        var cmd = servers.Commands.Should().ContainSingle().Subject;
        cmd.Host.Should().Be("lidarr.test");
        cmd.Path.Should().Be("/api/v1/command");
        cmd.ApiKey.Should().Be("lidarr-key");
        cmd.Body.GetProperty("name").GetString().Should().Be("RescanFolders");
        cmd.Body.GetProperty("folders").EnumerateArray().Select(e => e.GetString())
           .Should().Equal("/music/Some Artist/Some Album (2001)");
        cmd.Body.GetProperty("addNewArtists").GetBoolean().Should().BeFalse();
    }


    [Fact]
    public async Task Lidarr_completions_in_one_folder_coalesce_into_a_single_scan()
    {
        var servers = new FakeServers(lidarrRoots: """[{"id":1,"path":"/music"}]""");
        var svc     = NewService(servers);
        svc.RescanQuietPeriod = BurstQuietPeriod;

        for (var i = 1; i <= 5; i++)
        {
            svc.QueueArrRescan($"{AlbumDir}/0{i} - Track.flac", MediaKind.Music);
            await Task.Delay(30);
        }
        svc.QueueArrRescan("/mnt/tank/Music/Other Artist/Other Album/01 - Track.flac", MediaKind.Music);
        await SettleAsync(() => servers.Commands.Count >= 2);

        servers.Commands.Select(c => c.Body.GetProperty("folders")[0].GetString()).Should().BeEquivalentTo(
            "/music/Some Artist/Some Album (2001)",
            "/music/Other Artist/Other Album");
    }


    [Fact]
    public async Task Lidarr_windows_root_keeps_its_own_separators()
    {
        var servers = new FakeServers(lidarrRoots: """[{"id":3,"path":"D:\\Media\\Music"}]""");
        var svc     = NewService(servers);

        svc.QueueArrRescan(AlbumDir + "/01 - Track.flac", MediaKind.Music);
        await SettleAsync(() => servers.Commands.Count >= 1);

        servers.Commands.Should().ContainSingle()
               .Which.Body.GetProperty("folders")[0].GetString()
               .Should().Be(@"D:\Media\Music\Some Artist\Some Album (2001)");
    }


    [Fact]
    public async Task Lidarr_no_matching_root_folder_sends_no_command()
    {
        var servers = new FakeServers(lidarrRoots: """[{"id":1,"path":"/audiobooks"}]""");
        var svc     = NewService(servers);

        svc.QueueArrRescan(AlbumDir + "/01 - Track.flac", MediaKind.Music);
        await SettleAsync(() => servers.Reads("/api/v1/rootfolder") >= 1);

        servers.Reads("/api/v1/rootfolder").Should().Be(1);
        servers.Commands.Should().BeEmpty();
    }


    [Fact]
    public async Task Music_goes_only_to_lidarr_and_disabled_lidarr_makes_no_requests()
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers, lidarr: false);

        svc.QueueArrRescan(AlbumDir + "/01 - Track.flac", MediaKind.Music);
        await Task.Delay(600);

        servers.Requests.Should().Be(0);
    }


    /******************************************************************
     *  Sonarr / Radarr
     ******************************************************************/

    [Fact]
    public async Task Replaced_episode_rescans_only_its_series()
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers);

        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E01.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);

        var cmd = servers.Commands.Should().ContainSingle().Subject;
        cmd.Host.Should().Be("sonarr.test");
        cmd.Path.Should().Be("/api/v3/command");
        cmd.ApiKey.Should().Be("sonarr-key");
        cmd.Body.GetProperty("name").GetString().Should().Be("RescanSeries");
        cmd.Body.GetProperty("seriesId").GetInt32().Should().Be(7);
    }


    [Fact]
    public async Task Replaced_movie_rescans_only_that_movie()
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers);

        svc.QueueArrRescan(MovieDir + "/Some Movie (2010).mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);

        var cmd = servers.Commands.Should().ContainSingle().Subject;
        cmd.Host.Should().Be("radarr.test");
        cmd.Body.GetProperty("name").GetString().Should().Be("RescanMovie");
        cmd.Body.GetProperty("movieId").GetInt32().Should().Be(9);
    }


    [Fact]
    public async Task A_whole_series_across_seasons_coalesces_into_one_rescan()
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers);
        svc.RescanQuietPeriod = BurstQuietPeriod;

        foreach (var ep in new[] { "Season 01/Some Show - S01E01.mkv", "Season 01/Some Show - S01E02.mkv",
                                   "Season 02/Some Show - S02E01.mkv", "Season 02/Some Show - S02E02.mkv" })
        {
            svc.QueueArrRescan($"{ShowDir}/{ep}", MediaKind.Video);
            await Task.Delay(30);
        }
        await SettleAsync(() => servers.Commands.Count >= 1);

        servers.Commands.Should().ContainSingle()
               .Which.Body.GetProperty("seriesId").GetInt32().Should().Be(7);
    }


    [Fact]
    public async Task Episode_without_episode_naming_falls_back_to_sonarr()
    {
        // No SxxEyy / Season folder: the path classifies as a movie, Radarr has no match,
        // and Sonarr — tried second — owns it.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers);

        svc.QueueArrRescan(ShowDir + "/Some Show - Pilot.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);

        servers.Commands.Should().ContainSingle()
               .Which.Body.GetProperty("seriesId").GetInt32().Should().Be(7);
        servers.Reads("/api/v3/movie").Should().Be(1, "Radarr was asked first");
    }


    [Fact]
    public async Task Deepest_folder_match_wins_between_same_named_series()
    {
        var servers = new FakeServers(
            series: """[{"id":1,"path":"/anime/Some Show (2020)"},{"id":2,"path":"/tv/Some Show (2020)"}]""",
            movies: "[]");
        var svc = NewService(servers);

        svc.QueueArrRescan("/mnt/media/tv/Some Show (2020)/Season 01/Some Show - S01E01.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);

        servers.Commands.Should().ContainSingle()
               .Which.Body.GetProperty("seriesId").GetInt32().Should().Be(2);
    }


    [Fact]
    public async Task Unmatched_video_sends_no_command()
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson);
        var svc     = NewService(servers);

        svc.QueueArrRescan("/mnt/tank/Home Videos/Birthday.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Reads("/api/v3/series") >= 1);

        servers.Commands.Should().BeEmpty();
    }


    [Fact]
    public async Task A_request_merged_into_a_running_scan_is_rearmed()
    {
        // The Arr answers a duplicate command with the one already queued or running.
        // The second replacement gets id 7 back while it's "started", so its file may
        // not have been listed — Snacks must ask again once the scope is quiet.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson,
            responses: [(7, "started"), (7, "started"), (8, "queued")]);
        var svc = NewService(servers);

        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E01.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);
        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E02.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 3);

        servers.Commands.Should().HaveCount(3);
    }


    [Fact]
    public async Task A_fresh_command_that_is_already_started_is_not_rearmed()
    {
        // Arr executors often pick a new command up before the POST response is built —
        // a new id reported as "started" is still our own scan.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson,
            responses: [(7, "started"), (8, "started")]);
        var svc = NewService(servers);

        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E01.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);
        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E02.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 2);

        servers.Commands.Should().HaveCount(2);
    }


    [Fact]
    public async Task Connection_test_uses_the_v1_api_for_lidarr()
    {
        var servers = new FakeServers();
        var svc     = NewService(servers);

        var (ok, _) = await svc.TestArrAsync("http://lidarr.test:8686", "lidarr-key", "Lidarr", apiVersion: "v1");

        ok.Should().BeTrue();
        servers.RequestPaths.Should().Equal("/api/v1/system/status");
    }


    /******************************************************************
     *  Radarr monitoring guard
     ******************************************************************/

    [Fact]
    public async Task Radarr_movie_unmonitored_by_the_rescan_is_monitored_again()
    {
        // Monitored before; Radarr's "Unmonitor Deleted Movies" flips it during the scan.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson,
            monitoredReads: [true, false], commandStatuses: ["started", "completed"]);
        var svc = NewService(servers);

        svc.QueueArrRescan(MovieDir + "/Some Movie (2010).mkv", MediaKind.Video);
        await SettleAsync(() => servers.EditorPuts.Count >= 1);

        var put = servers.EditorPuts.Should().ContainSingle().Subject;
        put.GetProperty("movieIds").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(9);
        put.GetProperty("monitored").GetBoolean().Should().BeTrue();
        put.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["movieIds", "monitored"],
            "the editor endpoint changes only the fields it is given");
    }


    [Theory]
    [InlineData(true,  true)]   // still monitored after the scan — nothing to restore
    [InlineData(false, false)]  // the user had it unmonitored — leave it that way
    [InlineData(null,  false)]  // monitored flag unreadable before the scan — no guard
    public async Task Radarr_monitoring_is_left_alone_unless_the_rescan_flipped_it(bool? before, bool? after)
    {
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson, monitoredReads: [before, after]);
        var svc     = NewService(servers);

        svc.QueueArrRescan(MovieDir + "/Some Movie (2010).mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);

        servers.Commands.Should().ContainSingle("the rescan is sent whatever the monitored flag reads");
        servers.EditorPuts.Should().BeEmpty();
    }


    [Fact]
    public async Task Radarr_scattered_status_read_failures_do_not_end_the_wait_early()
    {
        // Three failed reads spread across a long scan must not count as three in a row:
        // giving up early checks the flag before Radarr unmonitors the movie.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson, unmonitorAfterScan: true,
            commandStatuses: [null, "started", null, "started", null, "started", "completed"]);
        var svc = NewService(servers);

        svc.QueueArrRescan(MovieDir + "/Some Movie (2010).mkv", MediaKind.Video);
        await SettleAsync(() => servers.EditorPuts.Count >= 1);

        servers.EditorPuts.Should().ContainSingle();
    }


    [Fact]
    public async Task A_command_response_without_an_id_is_never_treated_as_merged()
    {
        // Two id-less "started" answers must not look like the same command handed back.
        var servers = new FakeServers(series: SeriesJson, movies: MoviesJson,
            responses: [(null, "started"), (null, "started")]);
        var svc = NewService(servers);

        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E01.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 1);
        svc.QueueArrRescan(ShowDir + "/Season 01/Some Show - S01E02.mkv", MediaKind.Video);
        await SettleAsync(() => servers.Commands.Count >= 2);

        servers.Commands.Should().HaveCount(2);
    }


    /******************************************************************
     *  HTTP stubs
     ******************************************************************/

    /// <summary> One recorded Arr command post. </summary>
    private sealed record PostedCommand(string Host, string Path, string ApiKey, JsonElement Body);

    /// <summary>
    ///     Minimal Lidarr / Sonarr / Radarr. Serves catalogues and root folders,
    ///     records each command, and answers commands with the next scripted (id, status) —
    ///     fresh incrementing ids once the script runs out.
    /// </summary>
    private sealed class FakeServers : HttpMessageHandler
    {
        private readonly string? _lidarrRoots, _series, _movies;
        private readonly ConcurrentQueue<(int?, string)> _responses;
        private readonly ConcurrentQueue<bool?>          _monitoredReads;
        private readonly ConcurrentQueue<string?>        _commandStatuses;
        private readonly bool                            _unmonitorAfterScan;
        private volatile bool                            _scanCompleted;
        private int _nextId = 100;

        private readonly ConcurrentQueue<PostedCommand> _commands     = new();
        private readonly ConcurrentQueue<string>        _paths        = new();
        private readonly ConcurrentQueue<JsonElement>   _editorPuts   = new();
        public int Requests;

        /// <param name="monitoredReads">  Successive answers for a Radarr movie's <c>monitored</c> flag (last one repeats; <see langword="null"/> = HTTP 500). </param>
        /// <param name="commandStatuses">    Successive answers for a command's status (last one repeats; default "completed"; <see langword="null"/> = HTTP 500). </param>
        /// <param name="unmonitorAfterScan"> Like Radarr with "Unmonitor Deleted Movies": the movie reads monitored until a status poll reports "completed". Overrides <paramref name="monitoredReads"/>. </param>
        public FakeServers(string? lidarrRoots = null, string? series = null, string? movies = null,
                           (int? id, string status)[]? responses = null,
                           bool?[]? monitoredReads = null, string?[]? commandStatuses = null,
                           bool unmonitorAfterScan = false)
        {
            _lidarrRoots        = lidarrRoots;
            _series             = series;
            _movies             = movies;
            _responses          = new ConcurrentQueue<(int?, string)>(responses ?? []);
            _monitoredReads     = new ConcurrentQueue<bool?>(monitoredReads ?? [false]);
            _commandStatuses    = new ConcurrentQueue<string?>(commandStatuses ?? ["completed"]);
            _unmonitorAfterScan = unmonitorAfterScan;
        }

        public IReadOnlyList<PostedCommand> Commands          => _commands.ToList();
        public IReadOnlyList<string>        RequestPaths      => _paths.ToList();
        public IReadOnlyList<JsonElement>   EditorPuts        => _editorPuts.ToList();
        public int Reads(string path) => _paths.Count(p => p == path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var uri = request.RequestUri!;
            _paths.Enqueue(uri.AbsolutePath);

            if (request.Method == HttpMethod.Post && uri.AbsolutePath.EndsWith("/command"))
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                var key  = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : "";
                _commands.Enqueue(new PostedCommand(uri.Host, uri.AbsolutePath, key, body));
                var (id, status) = _responses.TryDequeue(out var scripted)
                    ? scripted
                    : (Interlocked.Increment(ref _nextId), "queued");
                return Json(id is int i ? JsonSerializer.Serialize(new { id = i, status }) : JsonSerializer.Serialize(new { status }));
            }

            if (uri.AbsolutePath == "/api/v3/movie/editor" && request.Method == HttpMethod.Put)
            {
                _editorPuts.Enqueue(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone());
                return Json("[]");
            }

            if (uri.AbsolutePath.StartsWith("/api/v3/movie/") && request.Method == HttpMethod.Get)
            {
                var monitored = _unmonitorAfterScan
                    ? !_scanCompleted
                    : _monitoredReads.Count > 1 && _monitoredReads.TryDequeue(out var m) ? m : _monitoredReads.TryPeek(out var last) ? last : null;
                return monitored == null
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    : Json(JsonSerializer.Serialize(new { id = 9, monitored = monitored.Value }));
            }

            if (uri.AbsolutePath.StartsWith("/api/v3/command/") && request.Method == HttpMethod.Get)
            {
                var status = _commandStatuses.Count > 1 && _commandStatuses.TryDequeue(out var st) ? st : _commandStatuses.TryPeek(out var lastSt) ? lastSt : "completed";
                if (status == null) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                if (status == "completed") _scanCompleted = true;
                return Json(JsonSerializer.Serialize(new { id = 1, status }));
            }

            switch (uri.AbsolutePath)
            {
                case "/api/v1/rootfolder" when _lidarrRoots != null: return Json(_lidarrRoots);
                case "/api/v3/series"     when _series      != null: return Json(_series);
                case "/api/v3/movie"      when _movies      != null: return Json(_movies);
                case "/api/v1/system/status":
                case "/api/v3/system/status":                        return Json("""{"appName":"Fake"}""");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
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
