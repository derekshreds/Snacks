using Snacks.Models;

namespace Snacks.Services.Routing;

/// <summary>
///     Router for <see cref="MediaKind.Music"/>. Music jobs target a synthetic
///     CPU-only <c>"music"</c> device id that's not part of any worker's hardware
///     probe — that isolates audio encodes from the GPU video pool so a queue
///     full of music can't starve a 4K HEVC encode and vice versa.
/// </summary>
public sealed class MusicJobRouter : IJobKindRouter
{
    private readonly TranscodingService _transcoder;

    public MusicJobRouter(TranscodingService transcoder) => _transcoder = transcoder;

    public MediaKind Kind => MediaKind.Music;

    public string? SyntheticDeviceId => "music";

    public void PinRemoteEncoderOptions(EncoderOptions options, string deviceId, Func<string, string?> devicePathResolver)
    {
        // No-op. Music encodes are pure CPU; pinning HardwareAcceleration to
        // "music" or any video device would either crash ffmpeg ("-hwaccel music")
        // or silently route audio through a video-only code path.
    }

    public int ScoreSlot(ClusterNode node, HardwareDevice device, WorkItem item, EncoderOptions options, ScoreContext ctx)
    {
        if (device.DeviceId != "music") return -100;
        // Default to enabled when the Music section is missing — legacy
        // settings.json files don't include a Music block, and a null
        // dereference here would unwind ScoreSlot mid-loop and silently
        // exclude every music slot from the score map.
        if (options.Music?.DispatchToCluster == false) return -100;
        if (node.Capabilities?.SupportsMusic != true) return -100;

        // Prefer least-loaded music nodes; the score decreases as occupancy rises.
        int used = ctx.UsedDeviceSlots(node, "music");
        return Math.Max(1, 100 - used * 10);
    }

    public Task EncodeRemoteAsync(WorkItem item, EncoderOptions options, CancellationToken ct)
        => _transcoder.ConvertMusicForRemoteAsync(item, options, ct);

    public string ExpectedOutputExtension(EncoderOptions options) =>
        "." + Snacks.Services.MusicEncoderArgs.ExtensionForFormat(options.Music?.Format ?? "m4a");
}
