using System.Diagnostics;
using FluentAssertions;
using Snacks.Services;
using Xunit;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     End-to-end coverage for <see cref="FileHealthService.VerifyAsync"/>: encodes a real
///     short open-GOP HEVC clip with ffmpeg, then asserts the decode-sample verifier PASSES a
///     healthy file and FLAGS genuinely-damaged ones — mid-file bit-rot (decoder errors) and a
///     truncated container (unreadable). Complements <see cref="FileHealthVerifyTests"/>, which
///     covers the noise-filter string logic; this proves the whole verify path behaves on real
///     files. The <see cref="FfmpegFactAttribute"/> skips it when ffmpeg/ffprobe with libx265
///     aren't on PATH so minimal CI images don't fail (set <c>FFMPEG_PATH</c>/<c>FFPROBE_PATH</c>
///     to point at a specific build).
/// </summary>
public sealed class FileHealthVerifyIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snacks-health-" + Guid.NewGuid().ToString("N"));
    private readonly FileHealthService _health = new(new FfprobeService());

    public FileHealthVerifyIntegrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [FfmpegFact]
    public async Task VerifyAsync_passes_healthy_clip_and_flags_damaged_files()
    {
        // Control — a freshly-encoded clip decodes cleanly (no issues, Ok = true).
        var healthy = Path.Combine(_dir, "healthy.mp4");
        Encode(healthy);
        var ok = await _health.VerifyAsync(healthy);
        ok.Ok.Should().BeTrue("a clean encode should decode without issues, but got: {0}", string.Join(" | ", ok.Issues));

        // Mid-file bit-rot — overwrite a chunk in the middle (moov sits at the end of a
        // default mp4, so the container still probes) so the start sample is clean but the
        // middle/end samples hit garbage and surface real decoder errors.
        var rotted = Path.Combine(_dir, "rotted.mp4");
        File.Copy(healthy, rotted);
        CorruptMiddle(rotted);
        var rot = await _health.VerifyAsync(rotted);
        rot.Ok.Should().BeFalse("mid-file corruption should surface as decoder errors");
        rot.Issues.Should().NotBeEmpty();
        // The reported issues are genuine decode errors, never the filtered muxer-DTS noise.
        rot.Issues.Should().Contain(i => !FileHealthService.IsBenignVerifyNoise(i));

        // Truncated container — cut the file short so it can't be opened/probed at all.
        var truncated = Path.Combine(_dir, "truncated.mp4");
        var bytes = await File.ReadAllBytesAsync(healthy);
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length * 35 / 100)]);
        var trunc = await _health.VerifyAsync(truncated);
        trunc.Ok.Should().BeFalse("a truncated container is not decodable");
        trunc.Issues.Should().NotBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------------

    /// <summary>
    ///     Encodes ~40s (&gt; the verifier's 32s multi-sample threshold) of open-GOP HEVC with
    ///     an AAC track — a file with no audio would itself be flagged, defeating the control.
    /// </summary>
    private void Encode(string path)
    {
        FfmpegTestSupport.Run(
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "40",
            "-c:v", "libx265", "-x265-params", "open-gop=1:bframes=4:keyint=48:min-keyint=48",
            "-pix_fmt", "yuv420p", "-c:a", "aac", path);
        File.Exists(path).Should().BeTrue("ffmpeg should have produced the test clip");
    }

    /// <summary>
    ///     Overwrites a wide band (~48%–78% of the file) with deterministic garbage. The band
    ///     starts at the middle decode-sample's seek point (~50% ≈ duration/2) and is wide enough
    ///     to overlap both the middle and near-end sample windows — a narrow poke can fall in the
    ///     gap between the verifier's sampled windows and go undetected. It stays clear of the
    ///     trailing <c>moov</c> atom, so the container still probes (this is bit-rot, not truncation).
    /// </summary>
    private static void CorruptMiddle(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        long start = (long)(fs.Length * 0.48);
        int  len   = (int)Math.Min(fs.Length - start, (long)(fs.Length * 0.30));
        var garbage = new byte[len];
        for (int i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i * 37 + 11); // deterministic, no RNG
        fs.Seek(start, SeekOrigin.Begin);
        fs.Write(garbage, 0, len);
    }
}

/// <summary>
///     A <see cref="FactAttribute"/> that skips the test (at discovery time) unless ffmpeg and
///     ffprobe — with the libx265 encoder — are launchable on PATH. Keeps ffmpeg-dependent
///     integration tests green on build agents that ship a minimal or no ffmpeg.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!FfmpegTestSupport.Available)
            Skip = "requires ffmpeg/ffprobe with libx265 on PATH";
    }
}

/// <summary> ffmpeg discovery + process plumbing shared by the ffmpeg-dependent tests. </summary>
internal static class FfmpegTestSupport
{
    private static string FfmpegPath  => Environment.GetEnvironmentVariable("FFMPEG_PATH")  ?? "ffmpeg";
    private static string FfprobePath => Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";

    private static readonly Lazy<bool> _available = new(() =>
    {
        try
        {
            return Capture(FfmpegPath, "-hide_banner", "-encoders")?.Contains("libx265") == true
                && Capture(FfprobePath, "-version") is not null;
        }
        catch
        {
            return false; // executable not found / not launchable
        }
    });

    /// <summary> True when ffmpeg (with libx265) and ffprobe are present and launchable. </summary>
    public static bool Available => _available.Value;

    /// <summary> Runs ffmpeg with the given args, throwing if it can't start or exits non-zero. </summary>
    public static void Run(params string[] args)
    {
        using var p = Start(FfmpegPath, args) ?? throw new InvalidOperationException($"could not start {FfmpegPath}");
        p.StandardOutput.ReadToEnd();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited {p.ExitCode}: {err}");
    }

    private static string? Capture(string exe, params string[] args)
    {
        using var p = Start(exe, args);
        if (p is null) return null;
        string outp = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return outp;
    }

    private static Process? Start(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi);
    }
}
