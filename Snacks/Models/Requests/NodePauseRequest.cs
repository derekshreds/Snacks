namespace Snacks.Models.Requests;

/// <summary> Request body for remotely pausing or resuming a specific cluster node. </summary>
public sealed class NodePauseRequest
{
    /// <summary> The unique identifier of the cluster node to target. </summary>
    public string NodeId { get; set; } = "";

    /// <summary> Whether the node should be paused. </summary>
    public bool Paused { get; set; }
}
