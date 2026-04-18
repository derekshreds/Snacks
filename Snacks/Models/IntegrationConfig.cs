namespace Snacks.Models;

/// <summary>
///     Third-party integration credentials. Serialized to integrations.json.
/// </summary>
public sealed class IntegrationConfig
{
    /// <summary> Plex Media Server integration settings. </summary>
    public MediaServerIntegration Plex { get; set; } = new();

    /// <summary> Jellyfin server integration settings. </summary>
    public MediaServerIntegration Jellyfin { get; set; } = new();

    /// <summary> Sonarr integration settings. </summary>
    public ArrIntegration Sonarr { get; set; } = new();

    /// <summary> Radarr integration settings. </summary>
    public ArrIntegration Radarr { get; set; } = new();
}

/// <summary> Connection settings for a Plex or Jellyfin media server. </summary>
public sealed class MediaServerIntegration
{
    /// <summary> Base URL of the media server (e.g. "http://192.168.1.10:32400"). </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary> Authentication token (Plex) or API key (Jellyfin). Stored in plaintext — protected by the auth feature. </summary>
    public string Token { get; set; } = "";

    /// <summary> When <see langword="true"/>, triggers a library rescan on this server after each successful encode. </summary>
    public bool RescanOnComplete { get; set; } = false;

    /// <summary> Whether this integration is active. </summary>
    public bool Enabled { get; set; } = false;
}

/// <summary> Connection settings for a Sonarr or Radarr instance. </summary>
public sealed class ArrIntegration
{
    /// <summary> Base URL of the Arr instance (e.g. "http://localhost:8989"). </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary> API key for authenticating with the Arr instance. </summary>
    public string ApiKey { get; set; } = "";

    /// <summary> Whether this integration is active. </summary>
    public bool Enabled { get; set; } = false;
}
