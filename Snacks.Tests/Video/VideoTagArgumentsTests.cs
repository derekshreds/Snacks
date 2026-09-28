namespace Snacks.Tests.Video;

using FluentAssertions;
using Newtonsoft.Json;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;

/// <summary>
///     HEVC in MP4 must carry the <c>hvc1</c> sample entry — ffmpeg defaults to <c>hev1</c>,
///     which Apple's AVFoundation / VideoToolbox players refuse to render.
/// </summary>
public sealed class VideoTagArgumentsTests
{
    [Theory]
    [InlineData("libx265")]
    [InlineData("hevc_nvenc")]
    [InlineData("hevc_qsv")]
    [InlineData("hevc_vaapi")]
    [InlineData("hevc_amf")]
    [InlineData("hevc_videotoolbox")]
    public void Hevc_encode_into_mp4_is_tagged_hvc1(string encoder)
    {
        var probe = new ProbeBuilder().Video(codec: "h264").Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: false, encoder, probe)
            .Should().Equal("-tag:v", "hvc1");
    }

    [Theory]
    [InlineData("libx264")]
    [InlineData("h264_nvenc")]
    [InlineData("libsvtav1")]
    [InlineData("av1_vaapi")]
    public void Non_hevc_encode_into_mp4_is_not_tagged(string encoder)
    {
        var probe = new ProbeBuilder().Video(codec: "hevc").Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: false, encoder, probe)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("webm")]
    public void Non_mp4_containers_are_not_tagged(string format)
    {
        var probe = new ProbeBuilder().Video(codec: "hevc").Build();

        TranscodingService.GetVideoTagArguments(format, isVideoCopy: false, "libx265", probe)
            .Should().BeEmpty();
        TranscodingService.GetVideoTagArguments(format, isVideoCopy: true, "copy", probe)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("dvh1")]
    [InlineData("dvhe")]
    public void Dolby_vision_mp4_copy_into_matroska_uses_a_compatible_codec_tag(string sourceTag)
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", codecTag: sourceTag, dolbyVisionProfile: 5)
            .Build();

        TranscodingService.GetVideoTagArguments("mkv", isVideoCopy: true, "copy", probe)
            .Should().Equal("-tag:v", "hvc1");
        TranscodingService.GetVideoTagArguments("mkv", isVideoCopy: false, "libx265", probe)
            .Should().BeEmpty();
        TranscodingService.GetVideoTagArguments("webm", isVideoCopy: true, "copy", probe)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[0][0][0][0]")]
    [InlineData("hev1")]
    [InlineData("hvc1")]
    public void Hevc_copy_into_mp4_is_tagged_hvc1(string? sourceTag)
    {
        var probe = new ProbeBuilder().Video(codec: "hevc", codecTag: sourceTag).Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().Equal("-tag:v", "hvc1");
    }

    [Theory]
    [InlineData("dvh1")]
    [InlineData("dvhe")]
    public void Dolby_vision_copy_keeps_its_source_tag(string sourceTag)
    {
        var probe = new ProbeBuilder().Video(codec: "hevc", codecTag: sourceTag).Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("[0][0][0][0]")]
    [InlineData("dvh1")]
    public void Dolby_vision_profile_5_copy_keeps_its_record_and_is_tagged_dvh1(string sourceTag)
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", codecTag: sourceTag, dolbyVisionProfile: 5)
            .Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().Equal("-strict", "unofficial", "-tag:v", "dvh1");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[0][0][0][0]")]
    [InlineData("dvh1")]
    [InlineData("hev1")]
    public void Dolby_vision_profile_8_copy_keeps_its_record_and_is_tagged_hvc1(string? sourceTag)
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", codecTag: sourceTag, dolbyVisionProfile: 8)
            .Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().Equal("-strict", "unofficial", "-tag:v", "hvc1");
    }

    [Theory]
    [InlineData(4, "[0][0][0][0]")]
    [InlineData(7, "[0][0][0][0]")]
    public void Other_dolby_vision_profile_copies_are_left_to_the_muxer(
        int profile, string sourceTag)
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", codecTag: sourceTag, dolbyVisionProfile: profile)
            .Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().BeEmpty();
    }

    [Fact]
    public void Probe_side_data_exposes_the_dolby_vision_profile()
    {
        const string json = """
            {
              "streams": [
                {
                  "index": 0,
                  "codec_name": "hevc",
                  "codec_type": "video",
                  "codec_tag_string": "[0][0][0][0]",
                  "side_data_list": [
                    { "side_data_type": "Content light level metadata", "max_content": 1000 },
                    {
                      "side_data_type": "DOVI configuration record",
                      "dv_version_major": 1,
                      "dv_profile": 8,
                      "dv_level": 6,
                      "dv_bl_signal_compatibility_id": 1
                    }
                  ]
                }
              ]
            }
            """;

        var probe = JsonConvert.DeserializeObject<ProbeResult>(json)!;

        probe.Streams[0].SideDataList.Should().HaveCount(2);
        probe.Streams[0].SideDataList![1].DvProfile.Should().Be(8);
        probe.Streams[0].SideDataList![1].DvBlSignalCompatibilityId.Should().Be(1);
        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe)
            .Should().Equal("-strict", "unofficial", "-tag:v", "hvc1");
    }

    [Fact]
    public void Dolby_vision_encode_does_not_keep_the_record()
    {
        var probe = new ProbeBuilder().Video(codec: "hevc", dolbyVisionProfile: 5).Build();

        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: false, "libx265", probe)
            .Should().Equal("-tag:v", "hvc1");
    }

    [Fact]
    public void Non_hevc_copy_into_mp4_is_not_tagged()
    {
        var probe = new ProbeBuilder().Video(codec: "h264").Build();

        // The encoder name is ignored on a copy — only the source codec decides.
        TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "libx265", probe)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData("-tag:v", "dvh1")]
    [InlineData("-tag:v:0", "hvc1")]
    [InlineData("-vtag", "hvc1")]
    [InlineData("-x265-params", "dolby-vision-profile=8.1:dolby-vision-rpu=/tmp/rpu.bin")]
    [InlineData("-dolbyvision", "1")]
    [InlineData("-dolbyvision:v", "auto")]
    [InlineData("-dolbyvision:v:0", "1")]
    [InlineData("-x265-params:v:0", "dolby-vision-profile=8.1")]
    public void Advanced_profile_that_sets_its_own_tag_owns_the_sample_entry(
        string option, string value)
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(["-crf", "20", option, value])
            .Should().BeTrue();
    }

    [Fact]
    public void Advanced_profile_without_a_tag_does_not_own_the_sample_entry()
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(["-crf", "20", "-tag:a", "mp4a"])
            .Should().BeFalse();
        TranscodingService.AdvancedProfileOwnsVideoTag(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("-dolbyvision", "0")]
    [InlineData("-dolbyvision", "false")]
    [InlineData("-dolbyvision", "off")]
    [InlineData("-dolbyvision", "no")]
    [InlineData("-dolbyvision:0", "0")]
    [InlineData("-dolbyvision:v:0", "off")]
    [InlineData("-dolbyvision:v:1", "1")]
    [InlineData("-x265-params", "dolby-vision-profile=0")]
    [InlineData("-x265-params", "dolby-vision-profile=8.1:dolby-vision-profile=0")]
    [InlineData("-x265-params", "stats=/tmp/dolby-analysis.log")]
    public void Disabled_or_unrelated_dolby_vision_options_leave_the_automatic_tag_enabled(
        string option, string value)
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(["-crf", "20", option, value])
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("1", "0", false)]
    [InlineData("0", "1", true)]
    [InlineData("0", "auto", true)]
    public void Repeated_dolby_vision_options_use_the_last_value(string first, string last, bool ownsTag)
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(
                ["-dolbyvision", first, "-dolbyvision", last])
            .Should().Be(ownsTag);
    }

    [Fact]
    public void Disabling_dolby_vision_does_not_override_an_explicit_video_tag()
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(
                ["-dolbyvision", "0", "-tag:v", "hev1"])
            .Should().BeTrue();
    }

    [Fact]
    public void A_later_video_stream_option_can_disable_dolby_vision()
    {
        TranscodingService.AdvancedProfileOwnsVideoTag(
                ["-dolbyvision", "1", "-dolbyvision:v:0", "0"])
            .Should().BeFalse();
    }

    [Fact]
    public void Tag_lands_before_the_output_path()
    {
        var probe = new ProbeBuilder().Video(codec: "h264").Build();
        var tag = TranscodingService.GetVideoTagArguments(
            "mp4", isVideoCopy: false, "hevc_vaapi", probe);

        var args = TranscodingService.BuildFfmpegArguments(
            "mp4", "-y", "", "/media/input.mkv", "",
            "-map 0:0 -c:v hevc_vaapi ", "", "-an ", "-sn ", "/output/out.mp4",
            tag);

        args.Arguments.Should().ContainInOrder("-c:v", "hevc_vaapi", "-tag:v", "hvc1");
        args.Arguments.TakeLast(3).Should().Equal("-f", "mp4", "/output/out.mp4");
    }

    [Fact]
    public void Dolby_vision_muxer_flag_lands_among_the_output_options()
    {
        var probe = new ProbeBuilder().Video(codec: "hevc", dolbyVisionProfile: 5).Build();
        var tag = TranscodingService.GetVideoTagArguments("mp4", isVideoCopy: true, "copy", probe);

        var args = TranscodingService.BuildFfmpegArguments(
            "mp4", "-y", "", "/media/input.mkv", "",
            "-map 0:0 -c:v copy ", "", "-c:a aac ", "-sn ", "/output/out.mp4",
            tag);

        args.Arguments.Should().ContainInOrder(
            "-i", "/media/input.mkv", "-c:v", "copy", "-strict", "unofficial", "-tag:v", "dvh1",
            "-c:a", "aac", "/output/out.mp4");
    }
}
