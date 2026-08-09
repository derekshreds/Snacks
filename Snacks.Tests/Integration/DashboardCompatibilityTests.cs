using System.Text.Json;
using FluentAssertions;
using Snacks.Controllers;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Integration;

/// <summary>Contract coverage for the dashboard read model and Homarr's Tdarr adapter.</summary>
public sealed class DashboardCompatibilityTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public void Queue_mapping_preserves_av1_instead_of_guessing_h264()
    {
        var item = new WorkItem
        {
            Id = "job-1",
            FileName = "movie.mkv",
            Path = "/library/movie.mkv",
            SourceCodec = "av1",
            SourceWidth = 3840,
            SourceHeight = 2160,
            Status = WorkItemStatus.Pending,
        };

        var mapped = DashboardIntegrationService.FromWorkItem(item);

        mapped.VideoCodec.Should().Be("av1");
        mapped.VideoResolution.Should().Be("4K");
        mapped.Decision.Should().Be("Queued");
    }

    [Fact]
    public void Iframe_tab_links_retain_and_escape_the_scoped_token()
    {
        var model = new HomarrIframeModel
        {
            Theme = "dark",
            Limit = 10,
            Refresh = 30,
            EmbedToken = "token +/value",
        };

        model.GetTabHref("queue").Should().Be(
            "?theme=dark&tab=queue&limit=10&refresh=30&embedToken=token%20%2B%2Fvalue");
    }

    [Fact]
    public void Tdarr_queue_row_uses_exact_case_sensitive_fields_and_mb_units()
    {
        var row = TdarrCompatibilityController.ToTdarrStatusRow(new DashboardQueueItem
        {
            Id = "job-1",
            FilePath = "/library/movie.mkv",
            SizeBytes = 2_500_000,
            Container = "mkv",
            VideoCodec = "av1",
            VideoResolution = "4K",
            Decision = "Processing",
        });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(row, JsonOptions));
        var root = json.RootElement;
        root.GetProperty("_id").GetString().Should().Be("job-1");
        root.GetProperty("HealthCheck").GetString().Should().BeEmpty();
        root.GetProperty("TranscodeDecisionMaker").GetString().Should().Be("Processing");
        root.GetProperty("file_size").GetDouble().Should().Be(2.5);
        root.GetProperty("video_codec_name").GetString().Should().Be("av1");
        root.TryGetProperty("healthCheck", out _).Should().BeFalse();
    }

    [Fact]
    public void Tdarr_worker_uses_the_schema_homarr_parses()
    {
        var node = TdarrCompatibilityController.ToTdarrNode(
            "node-1",
            "Worker One",
            paused: false,
            new[]
            {
                new ActiveJobInfo
                {
                    JobId = "job-1",
                    FileName = "movie.mkv",
                    DeviceId = "nvidia",
                    Progress = 42,
                    Phase = "Encoding",
                },
            });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(node, JsonOptions));
        var worker = json.RootElement.GetProperty("workers").GetProperty("job-1");
        worker.GetProperty("_id").GetString().Should().Be("job-1");
        worker.GetProperty("ETA").GetString().Should().Be("0:00:00");
        worker.GetProperty("job").GetProperty("type").GetString().Should().Be("transcode");
        worker.GetProperty("originalfileSizeInGbytes").GetDouble().Should().Be(0);
        worker.GetProperty("workerType").GetString().Should().Be("nvidia");
    }

    [Fact]
    public void Tdarr_statistics_envelope_keeps_required_empty_arrays()
    {
        var response = new TdarrStatisticsResponse
        {
            PieStats = new TdarrPieStats
            {
                Status = new TdarrStatusGroups(),
                Video = new TdarrVideoStatistics(),
                Audio = new TdarrAudioStatistics(),
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, JsonOptions));
        var pies = json.RootElement.GetProperty("pieStats");
        pies.GetProperty("status").GetProperty("transcode").GetArrayLength().Should().Be(0);
        pies.GetProperty("status").GetProperty("healthcheck").GetArrayLength().Should().Be(0);
        pies.GetProperty("video").GetProperty("containers").GetArrayLength().Should().Be(0);
        pies.GetProperty("audio").GetProperty("codecs").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Tdarr_zero_value_pie_segments_are_omitted_to_avoid_nan_percentages_in_homarr()
    {
        TdarrCompatibilityController.NonZeroSegments(
                new TdarrPieSegment("Queued", 0),
                new TdarrPieSegment("Transcoding", 2))
            .Should().ContainSingle()
            .Which.Name.Should().Be("Transcoding");
    }
}
