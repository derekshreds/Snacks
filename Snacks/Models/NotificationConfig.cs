namespace Snacks.Models;

/// <summary>
///     Outbound notification destinations and per-event toggles.
///     Serialized to notifications.json in the config directory.
/// </summary>
public sealed class NotificationConfig
{
    /// <summary> The list of outbound notification destinations. </summary>
    public List<NotificationDestination> Destinations { get; set; } = new();

    /// <summary> Per-event flags controlling which events trigger notifications. </summary>
    public NotificationEventToggles Events { get; set; } = new();
}

/// <summary> A single outbound notification target (webhook, ntfy, or Apprise endpoint). </summary>
public sealed class NotificationDestination
{
    /// <summary> Target URL. Apprise URLs must start with "apprise://" (stripped before POST). </summary>
    public string Url { get; set; } = "";

    /// <summary> Optional friendly label displayed in the UI. </summary>
    public string Name { get; set; } = "";

    /// <summary> Destination transport: "webhook", "ntfy", or "apprise". </summary>
    public string Type { get; set; } = "webhook";

    /// <summary> Whether this destination is active. Disabled destinations are skipped during dispatch. </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Optional shared secret. When set, webhook POSTs are signed with
    ///     HMAC-SHA256 over "{timestamp}.{body}" and the signature is sent as
    ///     X-Snacks-Signature: sha256=&lt;hex&gt;. Null/empty disables signing
    ///     (backwards-compatible). Ignored for ntfy/apprise destinations.
    /// </summary>
    public string? Secret { get; set; }
}

/// <summary> Per-event toggles controlling which encoding and cluster events trigger notifications. </summary>
public sealed class NotificationEventToggles
{
    /// <summary> Send a notification when an encode job starts. </summary>
    public bool EncodeStarted { get; set; } = false;

    /// <summary> Send a notification when an encode job completes successfully. </summary>
    public bool EncodeCompleted { get; set; } = true;

    /// <summary> Send a notification when an encode job fails. </summary>
    public bool EncodeFailed { get; set; } = true;

    /// <summary> Send a notification when an auto-scan completes. </summary>
    public bool ScanCompleted { get; set; } = false;

    /// <summary> Send a notification when a cluster node goes offline. </summary>
    public bool NodeOffline { get; set; } = true;

    /// <summary> Send a notification when a cluster node comes back online after being unreachable. </summary>
    public bool NodeOnline { get; set; } = true;
}
