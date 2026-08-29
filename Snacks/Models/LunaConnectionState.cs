namespace Snacks.Models;

/// <summary>
/// Private on-disk state for Luna's daemon-scoped session. This model is never
/// returned by an API controller.
/// </summary>
public sealed class LunaConnectionState
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");
    public string? AgentId { get; set; }
    public string? Email { get; set; }
    public string? BaseUrl { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? RefreshExpiresAt { get; set; }
    public DateTime? ConnectedAt { get; set; }
}
