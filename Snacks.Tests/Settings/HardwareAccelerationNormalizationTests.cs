using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Settings;

/// <summary>
///     Legacy hardware-acceleration aliases must be normalized on the backend, not
///     just in the browser form — headless and cluster nodes deserialize
///     settings.json without ever opening Settings, and an unmapped alias fell
///     through <see cref="TranscodingService.GetEncoder"/> /
///     <see cref="TranscodingService.GetInitFlags"/> to silent software encoding.
/// </summary>
public sealed class HardwareAccelerationNormalizationTests
{
    [Theory]
    [InlineData("nvenc",  "nvidia")]
    [InlineData("cuda",   "nvidia")]
    [InlineData("qsv",    "intel")]
    [InlineData("amf",    "amd")]
    // vendor-ambiguous: Intel AND AMD use VAAPI on Linux — detection must resolve it
    [InlineData("vaapi",  "auto")]
    [InlineData("",       "auto")]
    [InlineData("Intel",  "intel")]  // case-normalized
    [InlineData("intel",  "intel")]
    [InlineData("amd",    "amd")]
    [InlineData("nvidia", "nvidia")]
    [InlineData("apple",  "apple")]
    [InlineData("none",   "none")]
    [InlineData("auto",   "auto")]
    public void Setter_normalizes_legacy_aliases(string stored, string expected)
    {
        new EncoderOptions { HardwareAcceleration = stored }
            .HardwareAcceleration.Should().Be(expected);
    }


    [Fact]
    public void Normalized_legacy_value_reaches_a_hardware_encoder()
    {
        // The end-to-end consequence: a settings.json carrying "nvenc" must produce
        // an NVENC encoder, not fall through the switch to the raw software encoder.
        var opts = new EncoderOptions { Encoder = "libx265", HardwareAcceleration = "nvenc" };

        TranscodingService.GetEncoder(opts, isWindows: false, linuxIntelQsv: false)
            .Should().Be("hevc_nvenc");
    }


    [Theory]
    [InlineData("veryslow", "slow")]
    [InlineData("veryfast", "fast")]
    [InlineData("slow",     "slow")]
    [InlineData("medium",   "medium")]
    [InlineData("fast",     "fast")]
    [InlineData("",         "medium")]
    public void Nvenc_preset_map_avoids_unsupported_names(string uiPreset, string expected)
    {
        // NVENC rejects libx264-style veryslow/veryfast with "Error setting option
        // preset" before the encoder opens.
        TranscodingService.MapNvencPreset(uiPreset).Should().Be(expected);
    }
}
