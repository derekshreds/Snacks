using System.Text.Json;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

/// <summary>
///     Pins the quick-start contract through the generated fixture
///     (Video/Fixtures/quick-start-templates.json), which
///     scripts/validate-advanced-templates.mjs emits from the settings UI's
///     template definitions — the JS is the single source of truth, the script's
///     check mode fails the build on drift, and these tests exercise the real
///     shipped values. A template that loads with any diagnostic reads as broken,
///     so warnings are treated as failures here, not noise.
/// </summary>
public sealed class AdvancedVideoTemplateTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class FixtureFile
    {
        public List<FixtureTemplate> Templates { get; set; } = new();
    }

    private sealed class FixtureTemplate
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public AdvancedVideoOptions AdvancedVideo { get; set; } = new();
    }

    private static List<FixtureTemplate> LoadFixture() =>
        JsonSerializer.Deserialize<FixtureFile>(
            File.ReadAllText(LocateRepoFile("Snacks.Tests/Video/Fixtures/quick-start-templates.json")), Json)!.Templates;

    private static AdvancedVideoOptions Template(string key) =>
        LoadFixture().Single(t => t.Key == key).AdvancedVideo;

    [Fact]
    public void Shipped_example_policy_deserializes_and_validates_without_diagnostics()
    {
        var path = LocateRepoFile("examples/advanced-video-policy.json");
        var options = JsonSerializer.Deserialize<EncoderOptions>(File.ReadAllText(path), Json)!;

        var advanced = options.AdvancedVideo;
        advanced.Enabled.Should().BeTrue();
        advanced.Profiles.Should().HaveCount(2);
        advanced.Rules.Should().HaveCount(2);

        AdvancedVideoValidator.Validate(advanced).Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Every_quick_start_template_validates_without_diagnostics()
    {
        var templates = LoadFixture();
        templates.Should().HaveCountGreaterThanOrEqualTo(4);
        foreach (var template in templates)
            AdvancedVideoValidator.Validate(template.AdvancedVideo).Diagnostics
                .Should().BeEmpty($"template '{template.Key}' must load clean");
    }

    [Fact]
    public void Quality_mode_template_recipes_pin_cpu_encoding_and_always_keep()
    {
        // A quality number only has a stable visual meaning within one encoder
        // family, and quality output size is intentionally unpredictable — every
        // template recipe in Quality mode must encode on CPU and keep its output.
        foreach (var template in LoadFixture())
        foreach (var profile in template.AdvancedVideo.Profiles.Where(p => p.RateControl.Mode == VideoRateControlMode.Quality))
        {
            profile.HardwareAcceleration.Should().Be("none", $"{template.Key}/{profile.Name}");
            profile.OutputRetention.Should().Be(VideoOutputRetention.AlwaysKeep, $"{template.Key}/{profile.Name}");
        }
    }

    [Theory]
    [InlineData("h264", "AV1 quality (CRF 32)")]
    [InlineData("hevc", "AV1 quality (CRF 32)")]
    public void Av1_everything_template_transcodes_non_av1_sources(string codec, string expectedProfile)
    {
        var resolved = VideoPolicyResolver.Resolve(
            new EncoderOptions { AdvancedVideo = Template("av1-everything") }, null, null, Facts(codec, 1920, 1080));

        resolved.Plan.Action.Should().Be(AdvancedVideoAction.TranscodeWithProfile);
        resolved.Plan.ProfileName.Should().Be(expectedProfile);
    }

    [Fact]
    public void Av1_everything_template_skips_av1_sources()
    {
        VideoPolicyResolver.Resolve(
                new EncoderOptions { AdvancedVideo = Template("av1-everything") }, null, null, Facts("av1", 1920, 1080))
            .Plan.Action.Should().Be(AdvancedVideoAction.Skip);
    }

    [Theory]
    [InlineData(3840, 2160, "AV1 4K (CRF 32)")]
    [InlineData(1920, 1080, "AV1 1080p and below (CRF 35)")]
    [InlineData(1280, 720, "AV1 1080p and below (CRF 35)")]
    public void Tiered_template_routes_by_resolution_class(int width, int height, string expectedProfile)
    {
        VideoPolicyResolver.Resolve(
                new EncoderOptions { AdvancedVideo = Template("av1-tiered") }, null, null, Facts("h264", width, height))
            .Plan.ProfileName.Should().Be(expectedProfile);
    }

    [Theory]
    [InlineData("h264", AdvancedVideoAction.TranscodeWithProfile)]
    [InlineData("mpeg2video", AdvancedVideoAction.TranscodeWithProfile)]
    [InlineData("hevc", AdvancedVideoAction.Skip)]
    [InlineData("av1", AdvancedVideoAction.Skip)]
    public void Hevc_saver_template_only_touches_older_codecs(string codec, AdvancedVideoAction expected)
    {
        VideoPolicyResolver.Resolve(
                new EncoderOptions { AdvancedVideo = Template("hevc-saver") }, null, null, Facts(codec, 1920, 1080))
            .Plan.Action.Should().Be(expected);
    }

    [Fact]
    public void Expert_template_selects_exact_libaom_with_the_requester_controls()
    {
        var advanced = Template("libaom-expert");
        var resolved = VideoPolicyResolver.Resolve(
            new EncoderOptions { AdvancedVideo = advanced }, null, null, Facts("h264", 3840, 2160));

        resolved.Plan.ExplicitEncoder.Should().Be("libaom-av1");
        resolved.Plan.Profile!.PixelFormat.Should().Be("yuv420p10le");
        resolved.Plan.Profile.GopSize.Should().Be(300);
        resolved.Plan.Profile.CustomOptions.Should().Contain(o => o.Option == "-aom-params");
    }

    private static VideoSourceFacts Facts(string codec, int width, int height) => new()
    {
        Codec = VideoSourceFacts.NormalizeCodec(codec),
        Width = width,
        Height = height,
        ResolutionClass = Math.Min(width, height) >= 2160 ? "2160p+"
            : Math.Min(width, height) >= 1080 ? "1080p" : "720p",
        BitrateKbps = 8000,
        Is4K = width > 1920,
        IsHdr = false,
    };

    private static string LocateRepoFile(string repoRelativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Snacks.sln")))
                return Path.Combine(dir, repoRelativePath);
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new FileNotFoundException($"Could not locate Snacks.sln above {AppContext.BaseDirectory}");
    }
}
