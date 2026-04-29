namespace Snacks.Models;

/// <summary>
///     A discrete encode resource on a worker node — one entry per hardware
///     family the node can drive concurrently (e.g. NVIDIA NVENC, Intel QSV,
///     CPU). Each device exposes a configurable <see cref="DefaultConcurrency"/>
///     that the master uses as a slot-pool capacity when scheduling jobs.
///
///     <para>Device families are intentionally coarse — a node with two NVIDIA
///     cards still reports a single <c>nvidia</c> device with a higher default
///     concurrency. This keeps dispatch decisions simple while still allowing
///     simultaneous jobs across different vendor families on the same machine.</para>
/// </summary>
public sealed class HardwareDevice
{
    /// <summary>
    ///     Stable identifier for this device family. Lower-case, single token:
    ///     <c>nvidia</c>, <c>intel</c>, <c>amd</c>, <c>apple</c>, or <c>cpu</c>.
    ///     Used as the key in <see cref="NodeSettings.DeviceSettings"/> and
    ///     in slot-allocation tracking on the master.
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary> Human-readable label for the cluster UI (e.g. "NVIDIA NVENC", "Intel QSV"). </summary>
    public string DisplayName { get; set; } = "";

    /// <summary>
    ///     Logical codecs this device can encode (e.g. <c>["h264", "h265", "av1"]</c>).
    ///     Used by the master to filter slots when matching a job's codec to a device.
    /// </summary>
    public List<string> SupportedCodecs { get; set; } = new();

    /// <summary>
    ///     FFmpeg encoder names backed by this device (e.g. <c>hevc_nvenc</c>,
    ///     <c>h264_nvenc</c>). Informational — slot selection uses
    ///     <see cref="SupportedCodecs"/> plus the device's vendor mapping.
    /// </summary>
    public List<string> Encoders { get; set; } = new();

    /// <summary>
    ///     Concurrency capacity reported by the worker. NVIDIA defaults to 2,
    ///     QSV/AMF/VideoToolbox to 1, CPU to a fraction of logical cores.
    ///     The user can override this via per-node device settings.
    /// </summary>
    public int DefaultConcurrency { get; set; } = 1;

    /// <summary> <see langword="true"/> for hardware-backed devices, <see langword="false"/> for the CPU device. </summary>
    public bool IsHardware { get; set; } = true;
}

/// <summary>
///     A per-job snapshot reported by a worker node in its heartbeat. Lets the
///     master track multiple in-flight jobs per node and reconcile its own
///     optimistic slot accounting against ground truth.
/// </summary>
public sealed class ActiveJobInfo
{
    /// <summary> The work item ID this slot is encoding. </summary>
    public string JobId { get; set; } = "";

    /// <summary>
    ///     The <see cref="HardwareDevice.DeviceId"/> consuming this slot. The master
    ///     decremented this device's free-slot count when it dispatched the job.
    /// </summary>
    public string DeviceId { get; set; } = "";

    /// <summary> Display name of the file being encoded. </summary>
    public string? FileName { get; set; }

    /// <summary> Encoding progress percentage (0–100). </summary>
    public int Progress { get; set; }

    /// <summary>
    ///     Pipeline phase: <c>"Receiving"</c>, <c>"Encoding"</c>, or
    ///     <c>"Downloading"</c>. Allows the UI to render the right progress bar.
    /// </summary>
    public string? Phase { get; set; }
}
