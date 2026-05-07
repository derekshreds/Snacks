using Snacks.Models;

namespace Snacks.Services.Routing;

/// <summary>
///     Router for <see cref="MediaKind.Video"/>. Video jobs target real
///     hardware encoder families (cpu / intel / nvidia / amd) and respect
///     the cluster's codec / 4K / hwaccel preferences. Scoring is the
///     pre-extraction logic that previously lived in
///     <c>ClusterService.ScoreSlot</c>.
/// </summary>
public sealed class VideoJobRouter : IJobKindRouter
{
    private readonly TranscodingService _transcoder;

    public VideoJobRouter(TranscodingService transcoder) => _transcoder = transcoder;

    public MediaKind Kind => MediaKind.Video;

    /// <summary> Video uses real hardware devices; no synthetic id. </summary>
    public string? SyntheticDeviceId => null;

    public void PinRemoteEncoderOptions(EncoderOptions options, string deviceId, Func<string, string?> devicePathResolver)
    {
        // The master assigned this job to a specific device family — pin the
        // hardware acceleration so the encode lands where the scheduler intended
        // (and not on whichever device "auto" would have picked locally). CPU
        // jobs map to "none". HardwareDevicePath resolves the local /dev render
        // node so jobs land on the right adapter on hybrid laptops where the
        // discrete GPU may claim renderD128 and the iGPU sits on renderD129.
        options.HardwareAcceleration = deviceId == "cpu" ? "none" : deviceId;
        options.HardwareDevicePath   = deviceId == "cpu" ? null : devicePathResolver(deviceId);
    }

    public int ScoreSlot(ClusterNode node, HardwareDevice device, WorkItem item, EncoderOptions options, ScoreContext ctx)
    {
        // Video never lands on the synthetic music device.
        if (device.DeviceId == "music") return -100;

        var ns = ctx.GetNodeSettings(node.NodeId);
        if (ns != null)
        {
            if (ns.Only4K   == true && !item.Is4K) return -100;
            if (ns.Exclude4K == true && item.Is4K) return -100;
        }

        var caps = node.Capabilities;
        if (caps == null) return 1;

        if (caps.AvailableDiskSpaceBytes < item.Size * 2.5)
            return -100;

        // Codec-to-device match: every device must be able to encode the codec
        // the job is asking for. CPU supports all three; hardware devices may
        // not (e.g. AV1 requires recent silicon).
        var requestedCodec = (options.Codec ?? "").ToLowerInvariant();
        var codecKey = requestedCodec switch
        {
            "h265" or "hevc" => "h265",
            "h264" or "avc"  => "h264",
            "av1"            => "av1",
            _                => requestedCodec,
        };
        if (!device.SupportedCodecs.Any(c => c.Equals(codecKey, StringComparison.OrdinalIgnoreCase)))
            return -50;

        // CPU is reserved for explicit "Software" jobs and for "auto" jobs on
        // nodes with no usable hardware encoder for the requested codec. Under
        // "auto" with hardware that can do the codec, or any specific-vendor
        // preference, CPU is excluded outright — we'd rather queue a job than
        // silently spend it on a slow software encode while a GPU sits idle.
        // "Usable" means present, enabled, and capable of the requested codec —
        // an Intel iGPU without AV1 encode shouldn't lock out the CPU on an AV1
        // job, otherwise the node deadlocks.
        var hw = options.HardwareAcceleration?.ToLower() ?? "auto";
        bool isCpu = device.DeviceId == "cpu";
        bool nodeHasUsableHardware = caps.Devices?.Any(d =>
            d.DeviceId != "cpu"
            && ctx.IsDeviceEnabled(node, d.DeviceId)
            && d.SupportedCodecs.Any(c => c.Equals(codecKey, StringComparison.OrdinalIgnoreCase))) == true;

        if (hw == "none" && !isCpu) return -50;                             // software-only ⇒ CPU only
        if (hw != "none" && hw != "auto" && isCpu) return -50;              // specific vendor ⇒ never CPU
        if (hw == "auto" && isCpu && nodeHasUsableHardware) return -50;     // auto + usable HW ⇒ never CPU
        if (hw != "none" && !isCpu && !string.Equals(device.DeviceId, hw, StringComparison.OrdinalIgnoreCase)
            && hw != "auto") return -50;                                    // specific vendor ⇒ wrong family

        int score = 1;
        if (hw == "none")                                                                  score += 6;
        else if (hw == "auto")                                                             score += isCpu ? 5 : 8;
        else if (string.Equals(device.DeviceId, hw, StringComparison.OrdinalIgnoreCase))   score += 12;

        // Spread load: lightly prefer slots on devices with more headroom so a
        // node with two free NVENC slots takes the next two jobs before we start
        // filling its single QSV slot.
        int cap  = ctx.EffectiveDeviceCapacity(node, device);
        int used = ctx.UsedDeviceSlots(node, device.DeviceId);
        score += Math.Max(0, cap - used);

        return score;
    }

    public Task EncodeRemoteAsync(WorkItem item, EncoderOptions options, CancellationToken ct)
        => _transcoder.ConvertVideoForRemoteAsync(item, options, ct);

    public string ExpectedOutputExtension(EncoderOptions options) =>
        string.Equals(options.Format, "mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mkv";
}
