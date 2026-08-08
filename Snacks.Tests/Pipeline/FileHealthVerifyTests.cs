using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     Tests for <see cref="FileHealthService.IsBenignVerifyNoise"/> — the filter that
///     keeps the deep-verifier from reporting muxer-timestamp artifacts of its own
///     <c>-ss</c>/<c>-f null</c> sampling as file corruption.
/// </summary>
public sealed class FileHealthVerifyTests
{
    [Theory]
    // The muxer-DTS complaint produced by input-seeking into open-GOP HEVC — benign:
    // the frames decoded, only the null muxer's timestamp bookkeeping objected.
    [InlineData("[null @ 0x562058305180] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 2 >= 2")]
    [InlineData("Application provided invalid, non monotonically increasing DTS to muxer in stream 0: 16 >= 16")]
    public void IsBenignVerifyNoise_matches_muxer_dts_noise(string line)
    {
        FileHealthService.IsBenignVerifyNoise(line).Should().BeTrue();
    }

    [Theory]
    // Genuine decode/corruption signals must NOT be filtered.
    [InlineData("[hevc @ 0x1] Invalid data found when processing input")]
    [InlineData("[hevc @ 0x1] error while decoding MB 12 34")]
    [InlineData("Invalid NAL unit size")]
    [InlineData("co located POCs unavailable")]
    [InlineData("")]
    public void IsBenignVerifyNoise_keeps_real_errors(string line)
    {
        FileHealthService.IsBenignVerifyNoise(line).Should().BeFalse();
    }
}
