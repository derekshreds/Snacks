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
}

/// <summary>
///     Container for all per-node settings. Serialized to node-settings.json.
/// </summary>
public sealed class NodeSettingsConfig
{
    /// <summary> Per-node settings keyed by NodeId. </summary>
    public Dictionary<string, NodeSettings> Nodes { get; set; } = new();
}
