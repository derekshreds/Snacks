namespace Snacks.Models.Requests;

/// <summary> Request body for deleting per-node encoding override settings. </summary>
public sealed class DeleteNodeSettingsRequest
{
    /// <summary> The unique identifier of the cluster node whose settings should be deleted. </summary>
    public string NodeId { get; set; } = "";
}
