using FluentAssertions;
using Snacks.Controllers;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

namespace Snacks.Tests.Security;

/// <summary>
///     File names, settings values, and cluster upload names are attacker-influenced. None of
///     them may turn into extra FFmpeg arguments or overwrite Snacks' own job control files.
/// </summary>
public sealed class ArgumentInjectionTests
{
    [Theory]
    [InlineData("libx265")]
    [InlineData("hevc_nvenc")]
    [InlineData("veryslow")]
    [InlineData("p7")]
    [InlineData("5.1")]
    public void Plain_encoder_preset_and_level_values_are_accepted(string value)
        => FfmpegArgumentList.IsPlainValue(value).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("medium -y")]
    [InlineData("-filter_complex")]
    [InlineData("libx265\t-f")]
    [InlineData("\"slow\"")]
    [InlineData("slow;rm")]
    public void Values_that_could_become_extra_options_are_rejected(string? value)
        => FfmpegArgumentList.IsPlainValue(value).Should().BeFalse();

    [Fact]
    public void An_unrecognized_encoder_passed_through_by_GetEncoder_is_refused()
    {
        // No hardware mapping matches, so GetEncoder hands back the raw setting.
        var options = new EncoderOptions { Encoder = "libx265 -f image2", HardwareAcceleration = "none" };
        var encoder = TranscodingService.GetEncoder(options, isWindows: false, linuxIntelQsv: false);

        var act = () => TranscodingService.RequirePlainEncoder(encoder);

        act.Should().Throw<InvalidOperationException>();
        TranscodingService.RequirePlainEncoder("libx265").Should().Be("libx265");
    }

    [Fact]
    public void Music_paths_with_quotes_and_spaces_stay_single_arguments()
    {
        const string input  = "/media/Artist/a\" -f image2 \"/tmp/x.flac";
        const string output = "/media/out/b\" -y \"c [snacks].m4a";
        var probe = new ProbeBuilder().Audio(codec: "flac").Build();

        var args = MusicEncoderArgs.Build(input, output, new MusicEncoderOptions(), probe).Arguments;

        args.Should().ContainInOrder("-i", input);
        args[^1].Should().Be(output);
        args.Should().NotContain("image2");
    }

    [Theory]
    [InlineData("-filter")]
    [InlineData("-filter:v:0")]
    [InlineData("-filter_script:v")]
    [InlineData("-c:v:0")]
    [InlineData("-pass:v")]
    [InlineData("-/x265-params")]
    public void Alternate_spellings_of_reserved_options_are_rejected(string option)
    {
        var profile = new VideoEncodingProfile { Name = "p", Codec = "hevc", Encoder = "libx265" };
        profile.CustomOptions.Add(new CustomVideoOption { Option = option, Values = ["x"] });

        var result = AdvancedVideoValidator.Validate(new AdvancedVideoOptions { Enabled = true, Profiles = [profile] });

        result.Errors.Should().Contain(d => d.Code == "option_reserved");
    }

    [Theory]
    [InlineData("-aom-params", "tune=ssim:cq-level=35")]
    [InlineData("-x265-params", "aq-mode=3:psy-rd=1.0")]
    [InlineData("-tag:v", "hvc1")]
    [InlineData("-b:v", "4M")]
    [InlineData("-tune", "ssim")]
    public void Ordinary_encoder_options_are_still_accepted(string option, string value)
    {
        var profile = new VideoEncodingProfile { Name = "p", Codec = "hevc", Encoder = "libx265" };
        profile.CustomOptions.Add(new CustomVideoOption { Option = option, Values = [value] });

        var result = AdvancedVideoValidator.Validate(new AdvancedVideoOptions { Enabled = true, Profiles = [profile] });

        result.Errors.Should().NotContain(d => d.Code == "option_reserved" || d.Code == "option_value_reserved");
    }

    [Fact]
    public void A_file_sourced_option_cannot_ride_in_as_a_value()
    {
        var profile = new VideoEncodingProfile { Name = "p", Codec = "hevc", Encoder = "libx265" };
        profile.CustomOptions.Add(new CustomVideoOption { Option = "-tune", Values = ["-/filter_complex"] });

        var result = AdvancedVideoValidator.Validate(new AdvancedVideoOptions { Enabled = true, Profiles = [profile] });

        result.Errors.Should().Contain(d => d.Code == "option_value_reserved");
    }

    [Theory]
    [InlineData("_shared.json", "input_shared.json")]
    [InlineData("_METADATA.json", "input_METADATA.json")]
    [InlineData("../../_shared.json", "input_shared.json")]
    [InlineData("Movie (2024).mkv", "Movie (2024).mkv")]
    [InlineData("_Movie (2024).mkv", "_Movie (2024).mkv")]
    [InlineData("_shared.json.", "input_shared.json.")]
    [InlineData("_shared.json .", "input_shared.json .")]
    [InlineData("_shared.json::$DATA", "input_shared.json::$DATA")]
    [InlineData("_metadata.json:x", "input_metadata.json:x")]
    [InlineData("_SHARE~1.JSO", "input_SHARE~1.JSO")]
    [InlineData("_METAD~2.JSO", "input_METAD~2.JSO")]
    [InlineData("Movie~Cut (2024).mkv", "Movie~Cut (2024).mkv")]
    [InlineData("", null)]
    public void Upload_names_cannot_replace_job_control_files(string raw, string? expected)
        => ClusterController.ToUploadFileName(raw).Should().Be(expected);

    [Theory]
    [InlineData("_shared.json.")]
    [InlineData("_shared.json ")]
    [InlineData("_shared.json::$DATA")]
    [InlineData("_SHARE~1.JSO")]
    public void Windows_aliases_of_a_control_file_are_stored_elsewhere(string raw)
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Directory.CreateTempSubdirectory("snacks-upload-").FullName;
        try
        {
            var control = Path.Combine(dir, "_shared.json");
            File.WriteAllText(control, "control");

            File.WriteAllText(Path.Combine(dir, ClusterController.ToUploadFileName(raw)!), "upload");

            File.ReadAllText(control).Should().Be("control");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
