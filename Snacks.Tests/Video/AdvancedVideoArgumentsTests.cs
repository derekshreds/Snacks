using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Video;

public sealed class AdvancedVideoArgumentsTests
{
    [Fact]
    public void Libaom_quality_profile_emits_requester_controls_as_literal_tokens()
    {
        var profile = new VideoEncodingProfile
        {
            Codec = "av1",
            EncoderSelection = VideoEncoderSelectionMode.Explicit,
            Encoder = "libaom-av1",
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = 35 },
            Preset = "4",
            Threads = 8,
            PixelFormat = "yuv420p10le",
            GopSize = 300,
            CustomOptions =
            [
                new CustomVideoOption { Option = "-lag-in-frames", Values = ["35"] },
                new CustomVideoOption { Option = "-arnr-max-frames", Values = ["15"] },
                new CustomVideoOption { Option = "-arnr-strength", Values = ["4"] },
                new CustomVideoOption { Option = "-aom-params", Values = ["tune=ssim:cq-level=35:cpu-used=4:enable-qm=1"] },
            ],
        };

        var args = VideoEncoderRegistry.BuildProfileArguments(profile, "libaom-av1");

        args.Should().ContainInOrder(
            "-crf", "35", "-b:v", "0", "-cpu-used", "4", "-threads", "8",
            "-pix_fmt", "yuv420p10le", "-g", "300", "-lag-in-frames", "35",
            "-arnr-max-frames", "15", "-arnr-strength", "4", "-aom-params",
            "tune=ssim:cq-level=35:cpu-used=4:enable-qm=1");
    }

    [Theory]
    [InlineData("-i")]
    [InlineData("-map")]
    [InlineData("-c:a")]
    [InlineData("-b:a")]
    [InlineData("-filter_complex")]
    [InlineData("-vf")]
    [InlineData("-movflags")]
    [InlineData("-progress")]
    public void Structural_custom_options_are_rejected(string option)
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].CustomOptions.Add(new CustomVideoOption { Option = option, Values = ["value"] });

        AdvancedVideoValidator.Validate(advanced).Errors.Should().Contain(d => d.Code == "option_reserved");
    }

    [Fact]
    public void Encoder_private_options_are_allowed_and_typed_duplicates_warn()
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].CustomOptions =
        [
            new CustomVideoOption { Option = "-aom-params", Values = ["tune=ssim:cq-level=35"] },
            new CustomVideoOption { Option = "-crf", Values = ["32"] },
        ];

        var result = AdvancedVideoValidator.Validate(advanced);

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().Contain(d => d.Code == "option_override");
    }

    [Fact]
    public void Multi_output_filter_topology_is_rejected()
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].AdditionalVideoFilters = ["split[a][b]"];

        AdvancedVideoValidator.Validate(advanced).Errors.Should().Contain(d => d.Code == "filter_topology");
    }

    [Fact]
    public void Input_producing_filter_is_rejected_even_later_in_the_chain()
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].AdditionalVideoFilters = ["scale=1280:720,movie=/tmp/other.mkv"];

        AdvancedVideoValidator.Validate(advanced).Errors.Should().Contain(d => d.Code == "filter_source");
    }

    [Fact]
    public void Final_argument_vector_preserves_paths_and_custom_values_as_single_literal_tokens()
    {
        const string input = "/media/O'Brien/$HOME (4K)/映像.mkv";
        const string output = "/output/$(touch nope)/finished file.mkv";
        const string aom = "tune=ssim:cq-level=35:note=one two:'literal'";

        var args = TranscodingService.BuildFfmpegArguments(
            "mkv", "-y", "-analyzeduration 10M ", input, "",
            "-map 0:0 -c:v libaom-av1 ", "", "-an ", "-sn ", output,
            ["-crf", "35", "-b:v", "0", "-aom-params", aom]);

        args.Arguments.Should().ContainInOrder("-i", input, "-map", "0:0", "-c:v", "libaom-av1");
        args.Arguments.Should().ContainInOrder("-aom-params", aom);
        args.Arguments.TakeLast(3).Should().Equal("-f", "matroska", output);
        args.Arguments.Count(token => token == input).Should().Be(1);
        args.Arguments.Count(token => token == output).Should().Be(1);
    }

    [Fact]
    public void Filter_graph_and_ocr_input_paths_remain_literal_argument_tokens()
    {
        const string filter = "drawtext=text=hello world:fontfile=/fonts/My Font.ttf";
        const string subtitle = "/tmp/OCR $(not-a-command)/English captions.srt";

        var args = TranscodingService.BuildFfmpegArguments(
            "mkv", "", "", "/media/input.mkv", "",
            "-map 0:v:0 -c:v libaom-av1 ", "", "-an ", "-sn ", "/output/result.mkv",
            ["-vf", filter], [subtitle]);

        args.Arguments.Should().ContainInOrder("-i", "/media/input.mkv", "-i", subtitle);
        args.Arguments.Should().ContainInOrder("-vf", filter);
        args.Arguments.Should().NotContain("world:fontfile=/fonts/My");
    }

    [Fact]
    public void Runtime_inventory_keeps_unknown_av1_encoder_in_custom_mode()
    {
        const string inventory = """
         V....D libaom-av1           libaom AV1 (codec av1)
         V..... libsvtav1            SVT-AV1 (codec av1)
         V..... future_av1_encoder   Future AV1 encoder (codec av1)
         A..... aac                  AAC (Advanced Audio Coding)
        """;

        var parsed = FfmpegCapabilityService.ParseEncoderList(inventory);

        parsed.Select(item => item.Encoder).Should().Contain(["libaom-av1", "libsvtav1", "future_av1_encoder"]);
        parsed.Single(item => item.Encoder == "future_av1_encoder").RateControlModes.Should().Equal("Custom");
    }

    [Fact]
    public void Encoder_help_enriches_runtime_pixel_formats_and_private_options()
    {
        var capability = FfmpegCapabilityService.ParseEncoderList(
            " V..... libaom-av1 libaom AV1 (codec av1)").Single();
        const string help = """
            Encoder libaom-av1 [libaom AV1]:
                Supported pixel formats: yuv420p yuv420p10le gray
              -cpu-used         <int>        E..V....... Quality/Speed ratio
              -aom-params       <dictionary> E..V....... Set libaom options
            """;

        var enriched = FfmpegCapabilityService.EnrichFromHelp(capability, help);

        enriched.PixelFormats.Should().Equal("yuv420p", "yuv420p10le", "gray");
        enriched.SupportedOptions.Should().Contain(["-cpu-used", "-aom-params"]);
    }

    [Fact]
    public void Unknown_encoder_adapter_rejects_generated_rate_control()
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].Encoder = "future_av1_encoder";
        advanced.Profiles[0].RateControl.Mode = VideoRateControlMode.Bitrate;

        AdvancedVideoValidator.Validate(advanced).Errors
            .Should().Contain(d => d.Code == "rate_control_adapter");
    }

    [Fact]
    public void Missing_rate_control_is_a_stable_validation_error()
    {
        var advanced = ValidAdvanced();
        advanced.Profiles[0].RateControl = null!;

        AdvancedVideoValidator.Validate(advanced).Errors
            .Should().Contain(d => d.Path == "advancedVideo.profiles[0].rateControl"
                                   && d.Code == "rate_control_required");
    }

    [Fact]
    public void Rule_validation_rejects_incompatible_operators_and_boolean_values()
    {
        var advanced = ValidAdvanced();
        advanced.Rules =
        [
            new VideoRule
            {
                Name = "bad conditions",
                Action = AdvancedVideoAction.Skip,
                Conditions =
                [
                    new() { Field = VideoRuleField.Codec, Operator = VideoRuleOperator.GreaterThan, Values = ["av1"] },
                    new() { Field = VideoRuleField.IsHdr, Operator = VideoRuleOperator.Is, Values = ["sometimes"] },
                ],
            },
        ];

        var errors = AdvancedVideoValidator.Validate(advanced).Errors.ToList();
        errors.Should().Contain(d => d.Code == "condition_operator");
        errors.Should().Contain(d => d.Code == "boolean_value");
    }

    [Theory]
    [InlineData("libx264", "-crf")]
    [InlineData("libx265", "-crf")]
    [InlineData("libsvtav1", "-crf")]
    [InlineData("libaom-av1", "-crf")]
    [InlineData("librav1e", "-qp")]
    [InlineData("av1_nvenc", "-cq")]
    [InlineData("av1_qsv", "-global_quality")]
    [InlineData("av1_vaapi", "-global_quality:v")]
    [InlineData("av1_amf", "-qp_i")]
    [InlineData("av1_videotoolbox", "-q:v")]
    public void Known_encoder_families_emit_their_native_quality_control(string encoder, string expectedOption)
    {
        var profile = new VideoEncodingProfile
        {
            Codec = encoder.Contains("264") ? "h264" : encoder.Contains("265") ? "h265" : "av1",
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = 30 },
            Preset = null,
        };

        VideoEncoderRegistry.BuildProfileArguments(profile, encoder).Should().Contain(expectedOption);
    }

    [Theory]
    [InlineData("libaom-av1", "4", "-cpu-used")]
    [InlineData("librav1e", "4", "-speed")]
    [InlineData("av1_amf", "quality", "-quality")]
    [InlineData("av1_nvenc", "p5", "-preset")]
    public void Adapter_maps_native_preset_or_speed_option(string encoder, string preset, string expectedOption)
    {
        var profile = new VideoEncodingProfile
        {
            Codec = "av1",
            Preset = preset,
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Custom },
        };

        VideoEncoderRegistry.BuildProfileArguments(profile, encoder).Should().Contain(expectedOption);
    }

    [Theory]
    [InlineData("av1_vaapi")]
    [InlineData("hevc_videotoolbox")]
    public void Adapter_omits_unsupported_preset_switch(string encoder)
    {
        var profile = new VideoEncodingProfile
        {
            Codec = encoder.StartsWith("hevc", StringComparison.Ordinal) ? "h265" : "av1",
            Preset = "slow",
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Custom },
        };

        VideoEncoderRegistry.BuildProfileArguments(profile, encoder).Should().NotContain("-preset");
    }

    private static AdvancedVideoOptions ValidAdvanced()
    {
        var profile = new VideoEncodingProfile
        {
            Name = "AV1",
            Codec = "av1",
            EncoderSelection = VideoEncoderSelectionMode.Explicit,
            Encoder = "libaom-av1",
            RateControl = new VideoRateControlOptions { Mode = VideoRateControlMode.Quality, Quality = 35 },
            OutputRetention = VideoOutputRetention.AlwaysKeep,
        };
        return new AdvancedVideoOptions { Enabled = true, Profiles = [profile] };
    }
}
