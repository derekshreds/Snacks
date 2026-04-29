namespace Snacks.Models;

/// <summary>
///     Per-node settings stored on the master, keyed by <see cref="ClusterNode.NodeId"/>.
///     Controls dispatch routing constraints and per-node encoding overrides.
///     Persisted to node-settings.json so settings survive node disconnect/reconnect.
/// </summary>
public sealed class NodeSettings
{
    /// <summary> The NodeId (GUID) this settings entry applies to. </summary>
    public string NodeId { get; set; } = "";

    /// <summary> Optional friendly display name for this node (overrides hostname in the UI). </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    ///     When true, only 4K jobs are dispatched to this node.
    ///     Non-4K jobs will skip this node during worker selection.
    /// </summary>
    public bool? Only4K { get; set; }

    /// <summary>
    ///     When true, 4K jobs are never dispatched to this node.
    ///     Mutually exclusive with <see cref="Only4K"/>.
    /// </summary>
    public bool? Exclude4K { get; set; }

    /// <summary>
    ///     Encoding settings that override the global (and folder-level) defaults
    ///     when this specific node processes a job. Null fields inherit from the
    ///     resolved folder/global options.
    /// </summary>
    public EncoderOptionsOverride? EncodingOverrides { get; set; }

    /// <summary>
    ///     Per-hardware-device dispatch settings keyed by
    ///     <see cref="HardwareDevice.DeviceId"/>. Lets a user enable/disable a
    ///     device entirely or cap how many simultaneous jobs the master may
    ///     send to that device. Devices not listed inherit the worker's
    ///     reported defaults.
    /// </summary>
    public Dictionary<string, DeviceConcurrencySetting>? DeviceSettings { get; set; }
}

/// <summary>
///     Per-device dispatch policy. Stored under
///     <see cref="NodeSettings.DeviceSettings"/> and applied by the master when
///     enumerating available slots on a worker.
/// </summary>
public sealed class DeviceConcurrencySetting
{
    /// <summary>
    ///     When <see langword="false"/>, the master never dispatches a job to
    ///     this device — useful to force everything onto a beefier GPU even if
    ///     the iGPU is technically available.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Slot capacity override. When <see langword="null"/>, the master uses
    ///     the device's reported <see cref="HardwareDevice.DefaultConcurrency"/>.
    /// </summary>
    public int? MaxConcurrency { get; set; }
}

/// <summary>
///     Container for all per-node settings. Serialized to node-settings.json.
/// </summary>
public sealed class NodeSettingsConfig
{
    /// <summary> Per-node settings keyed by NodeId. </summary>
    public Dictionary<string, NodeSettings> Nodes { get; set; } = new();
}
