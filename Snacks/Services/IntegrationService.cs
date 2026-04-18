using System.Net.Http.Headers;
using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Handles third-party integration persistence plus live test-connection calls
///     and library-rescan triggers for Plex / Jellyfin. Sonarr / Radarr credentials
///     are stored here so the (future) original-language lookup can consume them.
/// </summary>
public sealed class IntegrationService
{
    private readonly ConfigFileService  _configFileService;
    private readonly IHttpClientFactory _httpClientFactory;
    private IntegrationConfig           _config;
    private readonly object             _lock = new();

    public IntegrationService(ConfigFileService configFileService, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configFileService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _configFileService = configFileService;
        _httpClientFactory = httpClientFactory;
        _config            = _configFileService.Load<IntegrationConfig>("integrations.json");
    }

    /******************************************************************
     *  Config Persistence
     ******************************************************************/

    /// <summary> Returns the current integration configuration. </summary>
    public IntegrationConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    /// <summary> Replaces the active integration configuration and persists it to disk. </summary>
    /// <param name="config"> The new configuration to apply. </param>
    public void SaveConfig(IntegrationConfig config)
    {
        lock (_lock)
        {
            _config = config;
            _configFileService.Save("integrations.json", config);
        }
    }

    /******************************************************************
     *  Test Connections
     ******************************************************************/

    /// <summary>
    ///     Sends a test request to the Plex identity endpoint and returns whether
    ///     the connection succeeded along with a status message.
    /// </summary>
    /// <param name="baseUrl"> The base URL of the Plex server. </param>
    /// <param name="token"> The Plex authentication token. </param>
    public async Task<(bool ok, string message)> TestPlexAsync(string baseUrl, string token)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token))
            return (false, "Base URL and token required");
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = baseUrl.TrimEnd('/') + "/identity";
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            return (true, "Plex connection OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    ///     Sends a test request to the Jellyfin system-info endpoint and returns whether
    ///     the connection succeeded along with a status message.
    /// </summary>
    /// <param name="baseUrl"> The base URL of the Jellyfin server. </param>
    /// <param name="apiKey"> The Jellyfin API key. </param>
    public async Task<(bool ok, string message)> TestJellyfinAsync(string baseUrl, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return (false, "Base URL and API key required");
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = baseUrl.TrimEnd('/') + "/System/Info";
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            return (true, "Jellyfin connection OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    ///     Sends a test request to the Sonarr or Radarr system-status endpoint and returns whether
    ///     the connection succeeded along with a status message.
    /// </summary>
    /// <param name="baseUrl"> The base URL of the Arr instance. </param>
    /// <param name="apiKey"> The Arr API key. </param>
    /// <param name="flavor"> Display name for logging ("Sonarr" or "Radarr"). </param>
    public async Task<(bool ok, string message)> TestArrAsync(string baseUrl, string apiKey, string flavor)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return (false, "Base URL and API key required");
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = baseUrl.TrimEnd('/') + "/api/v3/system/status";
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}");
            return (true, $"{flavor} connection OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /******************************************************************
     *  Library Rescans
     ******************************************************************/

    /// <summary>
    ///     Triggers a library rescan on any enabled media server with rescan-on-complete enabled.
    ///     Best-effort: logs and swallows individual errors.
    /// </summary>
    public async Task TriggerRescansAsync()
    {
        IntegrationConfig cfg;
        lock (_lock) cfg = _config;

        var tasks = new List<Task>();
        if (cfg.Plex.Enabled && cfg.Plex.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Plex.BaseUrl))
            tasks.Add(PlexRescanAsync(cfg.Plex));
        if (cfg.Jellyfin.Enabled && cfg.Jellyfin.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Jellyfin.BaseUrl))
            tasks.Add(JellyfinRescanAsync(cfg.Jellyfin));

        if (tasks.Count == 0) return;
        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            /* individual errors are logged inside PlexRescanAsync / JellyfinRescanAsync */
        }
    }

    private async Task PlexRescanAsync(MediaServerIntegration p)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = p.BaseUrl.TrimEnd('/') + "/library/sections/all/refresh";
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", p.Token);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"Plex rescan failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Plex rescan error: {ex.Message}");
        }
    }

    private async Task JellyfinRescanAsync(MediaServerIntegration j)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = j.BaseUrl.TrimEnd('/') + "/Library/Refresh";
            var req      = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.TryAddWithoutValidation("X-Emby-Token", j.Token);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                Console.WriteLine($"Jellyfin rescan failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Jellyfin rescan error: {ex.Message}");
        }
    }
}
