namespace Snacks.Models;

/// <summary>
///     Private on-disk state for Luna's daemon-scoped session. This model is never
///     returned by an API controller.
/// </summary>
public sealed class LunaConnectionState
{
    /// <summary> Stable per-install id; survives disconnects so a reconnect updates the same Luna connector record. </summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary> Luna's identifier for this registered connector; cleared on disconnect. </summary>
    public string? AgentId { get; set; }

    /// <summary> Account email reported by Luna at connect time (display only). </summary>
    public string? Email { get; set; }

    /// <summary> The Luna service root this session was created against; polling refuses any other URL. </summary>
    public string? BaseUrl { get; set; }

    /// <summary> The daemon-scoped refresh credential — the only secret Snacks persists for Luna. </summary>
    public string? RefreshToken { get; set; }

    /// <summary> Server-reported expiry of <see cref="RefreshToken"/>. </summary>
    public DateTime? RefreshExpiresAt { get; set; }

    /// <summary> When the current session was created (UTC). </summary>
    public DateTime? ConnectedAt { get; set; }
}
