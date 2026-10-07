using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Pins the hand-off from placement to the Arr rescans: a kept output that replaced its
///     original asks the owning Arr (Lidarr for music, Sonarr/Radarr for video) to rescan,
///     and a keep-both output asks nothing. Driven through
///     <see cref="TranscodingService.HandleRemoteCompletion"/>, the master's post-download step.
///     In the EnvConfigOverrides collection because it mutates SNACKS_WORK_DIR.
/// </summary>
[Collection("EnvConfigOverrides")]
public sealed class ReplacementRescanWiringTests : IDisposable
{
    private readonly InMemoryDb _db = new();
    private readonly string     _root;
    private readonly string?    _priorWorkDir;

    public ReplacementRescanWiringTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"snacks-rescan-wiring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "encode"));
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        _priorWorkDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR");
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", Path.Combine(_root, "work"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SNACKS_WORK_DIR", _priorWorkDir);
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (TranscodingService svc, RecordingArrs arrs) NewService()
    {
        var arrs        = new RecordingArrs();
        var integration = new IntegrationService(new ConfigFileService(new FileService()), new StubFactory(arrs));
        integration.SaveConfig(new IntegrationConfig
        {
            Lidarr = new ArrIntegration { Enabled = true, BaseUrl = "http://lidarr.test:8686", ApiKey = "k" },
            Sonarr = new ArrIntegration { Enabled = true, BaseUrl = "http://sonarr.test:8989", ApiKey = "k" },
        });
        integration.RescanQuietPeriod = TimeSpan.FromMilliseconds(50);

        var svc = new TranscodingService(new FileService(), new FfprobeService(), new NullHubContext(),
                                         _db.CreateRepository(), integrationService: integration);
        return (svc, arrs);
    }

    /// <summary> Writes a source file and its smaller staged encode; returns the work item and output path. </summary>
    private (WorkItem item, string outputPath) Stage(string relativeSource, string outputExt, MediaKind kind)
    {
        var sourcePath = Path.Combine(_root, "library", relativeSource);
        Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
        File.WriteAllBytes(sourcePath, new byte[4096]);

        var outputPath = Path.Combine(_root, "encode", $"{Path.GetFileNameWithoutExtension(sourcePath)} [snacks]{outputExt}");
        File.WriteAllText(outputPath, "encoded");

        return (new WorkItem
        {
            FileName = Path.GetFileName(sourcePath),
            Path     = sourcePath,
            Size     = 4096,
            Kind     = kind,
            Status   = WorkItemStatus.Processing,
        }, outputPath);
    }

    private static async Task WaitForAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(25);
        await Task.Delay(300);
    }


    [Fact]
    public async Task Replaced_music_encode_rescans_its_album_in_lidarr()
    {
        var (svc, arrs)    = NewService();
        var (item, output) = Stage("Music/Some Artist/Some Album/05 - Song.flac", ".m4a", MediaKind.Music);
        var options = new EncoderOptions();
        options.Music.DeleteOriginalFile = true;

        await svc.HandleRemoteCompletion(item, output, options);
        await WaitForAsync(() => !arrs.Commands.IsEmpty);

        arrs.Commands.Should().ContainSingle()
            .Which.Should().Be("lidarr.test {\"name\":\"RescanFolders\",\"folders\":[\"/music/Some Artist/Some Album\"],\"addNewArtists\":false}");
    }


    [Fact]
    public async Task Replaced_episode_rescans_its_series_in_sonarr()
    {
        var (svc, arrs)    = NewService();
        var (item, output) = Stage("TV/Some Show (2020)/Season 01/Some Show - S01E01.mp4", ".mkv", MediaKind.Video);
        var options = new EncoderOptions { DeleteOriginalFile = true };

        await svc.HandleRemoteCompletion(item, output, options);
        await WaitForAsync(() => !arrs.Commands.IsEmpty);

        File.Exists(Path.ChangeExtension(item.Path, ".mkv")).Should().BeTrue();
        arrs.Commands.Should().ContainSingle()
            .Which.Should().Be("sonarr.test {\"name\":\"RescanSeries\",\"seriesId\":7}");
    }


    [Fact]
    public async Task Keep_both_encode_asks_no_arr_to_rescan()
    {
        var (svc, arrs)    = NewService();
        var (item, output) = Stage("TV/Some Show (2020)/Season 01/Some Show - S01E02.mp4", ".mkv", MediaKind.Video);
        var options = new EncoderOptions { DeleteOriginalFile = false };

        await svc.HandleRemoteCompletion(item, output, options);
        await Task.Delay(500);

        File.Exists(item.Path).Should().BeTrue();
        arrs.Commands.Should().BeEmpty();
    }


    /******************************************************************
     *  HTTP stubs
     ******************************************************************/

    /// <summary> Serves Lidarr root folders and the Sonarr series list; records commands as "host body". </summary>
    private sealed class RecordingArrs : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Commands = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri  = request.RequestUri!;
            var json = uri.AbsolutePath switch
            {
                "/api/v1/rootfolder"                          => """[{"id":1,"path":"/music"}]""",
                "/api/v3/series"                              => """[{"id":7,"path":"/tv/Some Show (2020)"}]""",
                "/api/v1/command" or "/api/v3/command"        => """{"id":1,"status":"queued"}""",
                _                                             => null,
            };
            if (json == null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Post)
                Commands.Enqueue($"{uri.Host} {await request.Content!.ReadAsStringAsync(ct)}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class StubFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
