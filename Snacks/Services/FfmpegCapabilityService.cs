using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Snacks.Models;

namespace Snacks.Services;

public sealed class VideoEncoderCapability
{
    public string Encoder { get; init; } = "";
    public string Codec { get; init; } = "";
    public string Family { get; init; } = "";
    public string DeviceId { get; init; } = "cpu";
    public string QualityLabel { get; init; } = "Custom";
    public double QualityMin { get; init; }
    public double QualityMax { get; init; }
    public bool SupportsTypedQuality { get; init; }
    public bool SupportsQualityConstraints { get; init; }
    public IReadOnlyList<string> RateControlModes { get; init; } = [];
    public IReadOnlyList<string> Presets { get; init; } = [];
    public IReadOnlyList<string> PixelFormats { get; init; } = [];
    public IReadOnlyList<string> SupportedOptions { get; init; } = [];
}

/// <summary>Cached inventory of video encoders exposed by the configured FFmpeg binary.</summary>
public sealed class FfmpegCapabilityService
{
    private readonly string _ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _helpConcurrency = new(4, 4);
    private readonly ConcurrentDictionary<string, string> _encoderHelp = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<VideoEncoderCapability>? _cache;
    private DateTime _cacheTime;

    public async Task<IReadOnlyList<VideoEncoderCapability>> GetVideoEncodersAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && _cache != null && DateTime.UtcNow - _cacheTime < TimeSpan.FromMinutes(10)) return _cache;
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _cache != null && DateTime.UtcNow - _cacheTime < TimeSpan.FromMinutes(10)) return _cache;
            if (refresh) _encoderHelp.Clear();
            _cache = await DetectAsync(cancellationToken);
            _cacheTime = DateTime.UtcNow;
            return _cache;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate()
    {
        _cache = null;
        _cacheTime = DateTime.MinValue;
        _encoderHelp.Clear();
    }

    private async Task<IReadOnlyList<VideoEncoderCapability>> DetectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var start = new ProcessStartInfo(_ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-encoders");
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Log.Warning("FFmpeg encoder inventory timed out after 10 seconds.");
                return [];
            }
            var output = await stdoutTask;
            if (string.IsNullOrWhiteSpace(output)) output = await stderrTask;
            var inventory = ParseEncoderList(output);
            var enriched = await Task.WhenAll(inventory.Select(async capability =>
            {
                var help = await GetEncoderHelpAsync(capability.Encoder, cancellationToken);
                return EnrichFromHelp(capability, help);
            }));
            return enriched.OrderBy(e => e.Codec).ThenBy(e => e.Encoder).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning($"FFmpeg encoder inventory failed: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<VideoEncoderCapability> ParseEncoderList(string output)
    {
        var found = new Dictionary<string, VideoEncoderCapability>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (output ?? "").Split('\n'))
        {
            // FFmpeg rows look like: " V....D libx264              libx264 H.264 ... (codec h264)"
            var match = Regex.Match(raw, @"^\s*V\S*\s+(?<name>\S+)\s+(?<description>.+)$", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            var description = match.Groups["description"].Value;
            var codec = VideoEncoderRegistry.EncoderCodec(name) ?? CodecFromDescription(description);
            if (codec is not ("h264" or "h265" or "av1")) continue;
            var descriptor = VideoEncoderRegistry.Describe(name, codec);
            found[name] = new VideoEncoderCapability
            {
                Encoder              = name,
                Codec                = codec,
                Family               = descriptor.Family,
                DeviceId             = descriptor.DeviceId,
                QualityLabel         = descriptor.QualityLabel,
                QualityMin           = descriptor.QualityMin,
                QualityMax           = descriptor.QualityMax,
                SupportsTypedQuality = descriptor.SupportsTypedQuality,
                SupportsQualityConstraints = descriptor.SupportsQualityConstraints,
                RateControlModes     = descriptor.SupportsTypedQuality ? ["Bitrate", "Quality", "Custom"] : ["Custom"],
                Presets              = descriptor.Presets,
                PixelFormats         = descriptor.PixelFormats,
            };
        }
        return found.Values.OrderBy(e => e.Codec).ThenBy(e => e.Encoder).ToList();
    }

    private async Task<string> GetEncoderHelpAsync(string encoder, CancellationToken cancellationToken)
    {
        if (_encoderHelp.TryGetValue(encoder, out var cached)) return cached;
        await _helpConcurrency.WaitAsync(cancellationToken);
        try
        {
            if (_encoderHelp.TryGetValue(encoder, out cached)) return cached;
            var start = new ProcessStartInfo(_ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-h");
            start.ArgumentList.Add($"encoder={encoder}");

            using var process = new Process { StartInfo = start };
            process.Start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var help = (await stdoutTask) + "\n" + (await stderrTask);
                _encoderHelp[encoder] = help;
                return help;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                _encoderHelp[encoder] = "";
                return "";
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Debug($"FFmpeg encoder help unavailable for {encoder}: {ex.Message}");
            _encoderHelp[encoder] = "";
            return "";
        }
        finally
        {
            _helpConcurrency.Release();
        }
    }

    internal static VideoEncoderCapability EnrichFromHelp(VideoEncoderCapability capability, string help)
    {
        var formatsMatch = Regex.Match(help ?? "", @"Supported pixel formats:\s*(?<formats>[^\r\n]+)", RegexOptions.IgnoreCase);
        var pixelFormats = formatsMatch.Success
            ? formatsMatch.Groups["formats"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        var supportedOptions = Regex.Matches(help ?? "", @"^\s+-(?<option>[A-Za-z0-9_.:-]+)\s", RegexOptions.Multiline)
            .Select(match => "-" + match.Groups["option"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VideoEncoderCapability
        {
            Encoder = capability.Encoder,
            Codec = capability.Codec,
            Family = capability.Family,
            DeviceId = capability.DeviceId,
            QualityLabel = capability.QualityLabel,
            QualityMin = capability.QualityMin,
            QualityMax = capability.QualityMax,
            SupportsTypedQuality = capability.SupportsTypedQuality,
            SupportsQualityConstraints = capability.SupportsQualityConstraints,
            RateControlModes = capability.RateControlModes,
            Presets = capability.Presets,
            PixelFormats = pixelFormats.Length > 0 ? pixelFormats : capability.PixelFormats,
            SupportedOptions = supportedOptions,
        };
    }

    private static string? CodecFromDescription(string description)
    {
        var text = description.ToLowerInvariant();
        if (text.Contains("(codec av1)") || Regex.IsMatch(text, @"\bav1\b")) return "av1";
        if (text.Contains("(codec hevc)") || text.Contains("h.265") || Regex.IsMatch(text, @"\bhevc\b")) return "h265";
        if (text.Contains("(codec h264)") || text.Contains("h.264") || text.Contains("avc")) return "h264";
        return null;
    }
}
