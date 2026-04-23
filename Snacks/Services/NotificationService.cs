using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Dispatches notifications to configured destinations (generic webhook, ntfy, Apprise)
///     when encode / scan / cluster events fire. Load-on-demand from notifications.json.
/// </summary>
public sealed class NotificationService
{
    private readonly ConfigFileService  _configFileService;
    private readonly IHttpClientFactory _httpClientFactory;
    private NotificationConfig          _config;
    private readonly object             _lock = new();

    public NotificationService(ConfigFileService configFileService, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configFileService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _configFileService = configFileService;
        _httpClientFactory = httpClientFactory;
        _config            = _configFileService.Load<NotificationConfig>("notifications.json");
    }

    /******************************************************************
     *  Public API
     ******************************************************************/

    /// <summary> Returns the current notification configuration. </summary>
    public NotificationConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    /// <summary> Replaces the active notification configuration and persists it to disk. </summary>
    /// <param name="config"> The new configuration to apply. </param>
    public void SaveConfig(NotificationConfig config)
    {
        lock (_lock)
        {
            _config = config;
            _configFileService.Save("notifications.json", config);
        }
    }

    /******************************************************************
     *  Event Dispatch
     ******************************************************************/

    /// <summary> Dispatches an encode-started notification for the given file. </summary>
    /// <param name="filename"> The source file name being encoded. </param>
    public Task NotifyEncodeStartedAsync(string filename) =>
        DispatchIfEnabledAsync(
            c => c.EncodeStarted,
            "EncodeStarted",
            $"Encoding started: {filename}",
            new { file = filename });

    /// <summary> Dispatches an encode-completed notification for the given file. </summary>
    /// <param name="filename"> The source file name that was encoded. </param>
    /// <param name="outputSize"> The byte size of the encoded output, if available. </param>
    public Task NotifyEncodeCompletedAsync(string filename, long? outputSize) =>
        DispatchIfEnabledAsync(
            c => c.EncodeCompleted,
            "EncodeCompleted",
            $"Encoded: {filename}",
            new { file = filename, size = outputSize });

    /// <summary> Dispatches an encode-failed notification for the given file. </summary>
    /// <param name="filename"> The source file name that failed to encode. </param>
    /// <param name="error"> The error message describing the failure. </param>
    public Task NotifyEncodeFailedAsync(string filename, string error) =>
        DispatchIfEnabledAsync(
            c => c.EncodeFailed,
            "EncodeFailed",
            $"Encode failed: {filename} — {error}",
            new { file = filename, error });

    /// <summary> Dispatches a scan-completed notification. </summary>
    /// <param name="newFiles"> The number of new files discovered in the scan. </param>
    public Task NotifyScanCompletedAsync(int newFiles) =>
        DispatchIfEnabledAsync(
            c => c.ScanCompleted,
            "ScanCompleted",
            $"Auto-scan found {newFiles} new file(s)",
            new { newFiles });

    /// <summary> Dispatches a node-offline notification. </summary>
    /// <param name="nodeName"> The name of the cluster node that went offline. </param>
    public Task NotifyNodeOfflineAsync(string nodeName) =>
        DispatchIfEnabledAsync(
            c => c.NodeOffline,
            "NodeOffline",
            $"Node offline: {nodeName}",
            new { node = nodeName });

    /// <summary> Dispatches a node-online notification when a previously-unreachable node recovers. </summary>
    /// <param name="nodeName"> The name of the cluster node that came back online. </param>
    public Task NotifyNodeOnlineAsync(string nodeName) =>
        DispatchIfEnabledAsync(
            c => c.NodeOnline,
            "NodeOnline",
            $"Node online: {nodeName}",
            new { node = nodeName });

    /******************************************************************
     *  Internals
     ******************************************************************/

    private async Task DispatchIfEnabledAsync(
        Func<NotificationEventToggles, bool> eventCheck,
        string eventName,
        string message,
        object payload)
    {
        NotificationConfig config;
        lock (_lock) config = _config;

        if (!eventCheck(config.Events)) return;
        if (config.Destinations.Count == 0) return;

        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        var tasks = new List<Task>();
        foreach (var dest in config.Destinations.Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Url)))
            tasks.Add(SendAsync(http, dest, eventName, message, payload));

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            /* individual failures are logged inside SendAsync */
        }
    }

    private static async Task SendAsync(
        HttpClient http,
        NotificationDestination dest,
        string eventName,
        string message,
        object payload)
    {
        try
        {
            var url  = dest.Url;
            var type = string.IsNullOrWhiteSpace(dest.Type) ? InferType(url) : dest.Type.ToLowerInvariant();

            HttpRequestMessage request;
            switch (type)
            {
                case "ntfy":
                    request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(message, Encoding.UTF8, "text/plain"),
                    };
                    request.Headers.TryAddWithoutValidation("Title", "Snacks");
                    request.Headers.TryAddWithoutValidation("Tags", eventName);
                    break;

                case "apprise":
                    // Strip optional apprise:// prefix; remainder is the Apprise API endpoint.
                    var appriseUrl = url.StartsWith("apprise://", StringComparison.OrdinalIgnoreCase)
                        ? url.Substring("apprise://".Length) : url;
                    var json = JsonSerializer.Serialize(new { body = message, title = "Snacks", type = "info" });
                    request = new HttpRequestMessage(HttpMethod.Post, appriseUrl)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    };
                    break;

                default: // webhook
                    // Discord rejects any POST that doesn't match its own schema, so a
                    // user-pasted Discord webhook URL needs the Discord payload shape
                    // (username + embed) instead of the generic {event,message,...}.
                    // HMAC signing is skipped here because Discord doesn't verify it and
                    // the shared-secret feature is for user-owned webhook receivers.
                    if (IsDiscordWebhook(url))
                    {
                        var discordBody = JsonSerializer.Serialize(new
                        {
                            username = "Snacks",
                            embeds = new[]
                            {
                                new
                                {
                                    title       = PrettyEventName(eventName),
                                    description = TruncateDiscordDescription(message),
                                    color       = DiscordColorForEvent(eventName),
                                    timestamp   = DateTime.UtcNow.ToString("o"),
                                },
                            },
                        });
                        request = new HttpRequestMessage(HttpMethod.Post, url)
                        {
                            Content = new StringContent(discordBody, Encoding.UTF8, "application/json"),
                        };
                        break;
                    }

                    var body = JsonSerializer.Serialize(new
                    {
                        @event  = eventName,
                        message,
                        payload,
                        timestamp = DateTime.UtcNow
                    });
                    request = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    if (!string.IsNullOrEmpty(dest.Secret))
                    {
                        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        var signingInput = Encoding.UTF8.GetBytes($"{unix}.{body}");
                        var secretBytes  = Encoding.UTF8.GetBytes(dest.Secret);
                        using var hmac   = new HMACSHA256(secretBytes);
                        var hex          = Convert.ToHexString(hmac.ComputeHash(signingInput)).ToLowerInvariant();
                        request.Headers.TryAddWithoutValidation("X-Snacks-Timestamp", unix);
                        request.Headers.TryAddWithoutValidation("X-Snacks-Signature", $"sha256={hex}");
                    }
                    break;
            }

            using var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                Console.WriteLine($"Notification {eventName} → {url} failed: {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Notification dispatch error ({dest.Url}): {ex.Message}");
        }
    }

    private static string InferType(string url)
    {
        if (url.StartsWith("apprise://", StringComparison.OrdinalIgnoreCase)) return "apprise";
        if (url.Contains("/ntfy.") || url.Contains("ntfy.sh")) return "ntfy";
        return "webhook";
    }

    /// <summary>
    ///     Recognises both canonical Discord webhook hosts. The legacy
    ///     <c>discordapp.com</c> aliases still resolve and some users save URLs
    ///     from old tutorials, so we accept either.
    /// </summary>
    private static bool IsDiscordWebhook(string url) =>
        url.Contains("discord.com/api/webhooks",    StringComparison.OrdinalIgnoreCase) ||
        url.Contains("discordapp.com/api/webhooks", StringComparison.OrdinalIgnoreCase);

    /// <summary> Splits <c>EncodeCompleted</c> → <c>"Encode Completed"</c> for the embed title. </summary>
    private static string PrettyEventName(string pascalCase) =>
        Regex.Replace(pascalCase, "(?<=[a-z])([A-Z])", " $1");

    /// <summary>
    ///     Discord embed <c>description</c> has a 4096-character hard limit. Our messages
    ///     are almost always short, but a pathological error string from ffmpeg can blow
    ///     past it; truncate with an ellipsis rather than getting the whole POST rejected.
    /// </summary>
    private static string TruncateDiscordDescription(string s) =>
        s.Length <= 4096 ? s : string.Concat(s.AsSpan(0, 4093), "...");

    /// <summary>
    ///     Sidebar color for the Discord embed, keyed to the event's sentiment so
    ///     failures pop visually (red) vs successes (green) vs routine events (blue).
    /// </summary>
    private static int DiscordColorForEvent(string eventName) => eventName switch
    {
        "EncodeCompleted" => 0x2ecc71, // green
        "NodeOnline"      => 0x2ecc71, // green
        "EncodeFailed"    => 0xe74c3c, // red
        "NodeOffline"     => 0xe67e22, // orange
        "EncodeStarted"   => 0x3498db, // blue
        "ScanCompleted"   => 0x9b59b6, // purple
        _                 => 0x95a5a6, // neutral gray
    };
}
