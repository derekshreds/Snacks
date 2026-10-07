using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Handles third-party integration persistence plus live test-connection calls,
///     library-rescan triggers for Plex / Jellyfin, and per-item rescans in Sonarr / Radarr /
///     Lidarr after an encode replaces its original. Sonarr / Radarr catalogues also feed the
///     original-language lookup.
/// </summary>
public sealed class IntegrationService
{
    private readonly ConfigFileService  _configFileService;
    private readonly IHttpClientFactory _httpClientFactory;
    private IntegrationConfig           _config;
    private readonly object             _lock = new();

    // Library-root caches. 10-minute TTL is long enough to avoid per-encode
    // lookups on a busy queue, short enough that library edits pick up on a
    // reasonable timescale.
    private static readonly TimeSpan _rootCacheTtl = TimeSpan.FromMinutes(10);

    // Failure suppression for the Arr catalogue fetch. Dispatches are serialized
    // in the scheduler, so without this a slow/unreachable Sonarr/Radarr costs a
    // full HttpClient timeout on every single dispatch of a busy queue.
    private static readonly TimeSpan _arrFailureCacheTtl = TimeSpan.FromMinutes(3);
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<LibraryRoot> roots)> _plexRootsCache     = new();
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<LibraryRoot> roots)> _jellyfinRootsCache = new();
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<LibraryRoot> roots)> _lidarrRootsCache   = new();
    private readonly object _rootsLock = new();

    public IntegrationService(ConfigFileService configFileService, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configFileService);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _configFileService = configFileService;
        _httpClientFactory = httpClientFactory;
        _config            = EnvConfigOverrides.Apply(
            _configFileService.Load<IntegrationConfig>("integrations.json"),
            EnvConfigOverrides.IntegrationsPrefix);
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
            // Fields driven by SNACKS_INTEG_* env vars keep their on-disk values in the
            // file (unsetting a var reverts cleanly); the live config gets env re-applied.
            var fileState = _configFileService.Load<IntegrationConfig>("integrations.json");
            EnvConfigOverrides.RestoreLockedValues(config, fileState, EnvConfigOverrides.IntegrationsPrefix);
            _configFileService.Save("integrations.json", config);
            _config = EnvConfigOverrides.Apply(config, EnvConfigOverrides.IntegrationsPrefix);
            // Credentials or base URL may have changed — drop the library-root caches.
            lock (_rootsLock)
            {
                _plexRootsCache.Clear();
                _jellyfinRootsCache.Clear();
                _lidarrRootsCache.Clear();
                _arrCatalogCache.Clear();
            }
            // The TVDB bearer token isn't keyed to the API key — a changed key would
            // otherwise keep using the previous token until it expired or 401'd.
            lock (_tvdbLock) _tvdbToken = null;
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
            AddJellyfinAuth(req, apiKey);
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
    ///     Sends a test request to the Sonarr, Radarr, or Lidarr system-status endpoint and
    ///     returns whether the connection succeeded along with a status message.
    /// </summary>
    /// <param name="baseUrl"> The base URL of the Arr instance. </param>
    /// <param name="apiKey"> The Arr API key. </param>
    /// <param name="flavor"> Display name for logging ("Sonarr", "Radarr", or "Lidarr"). </param>
    /// <param name="apiVersion"> API path version — <c>"v3"</c> for Sonarr/Radarr, <c>"v1"</c> for Lidarr. </param>
    public async Task<(bool ok, string message)> TestArrAsync(string baseUrl, string apiKey, string flavor, string apiVersion = "v3")
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            return (false, "Base URL and API key required");
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url      = baseUrl.TrimEnd('/') + $"/api/{apiVersion}/system/status";
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
    ///     Triggers a library rescan on any enabled media server with rescan-on-complete
    ///     enabled. When <paramref name="completedFilePath"/> is provided, each server
    ///     attempts a per-item scan scoped to that file (remapping the path through the
    ///     server's own library roots so Docker/bind-mount layouts work); if the scoped
    ///     call fails or no matching library root is found, falls back to a full refresh.
    ///     Only libraries of the file's kind are considered (music libraries for music), and
    ///     a server with no library of that kind is skipped rather than fully refreshed.
    ///     Best-effort: logs and swallows individual errors.
    /// </summary>
    /// <param name="completedFilePath">
    ///     Absolute path of the file that just finished encoding <i>as Snacks sees it</i>.
    ///     <see langword="null"/> or empty triggers a full library refresh on each server.
    /// </param>
    /// <param name="kind"> Whether the file is music or video, which decides the libraries it can belong to. </param>
    public async Task TriggerRescansAsync(string? completedFilePath, MediaKind kind)
    {
        IntegrationConfig cfg;
        lock (_lock) cfg = _config;

        var tasks = new List<Task>();
        if (cfg.Plex.Enabled && cfg.Plex.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Plex.BaseUrl))
            tasks.Add(PlexRescanAsync(cfg.Plex, completedFilePath, kind));
        if (cfg.Jellyfin.Enabled && cfg.Jellyfin.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Jellyfin.BaseUrl))
            tasks.Add(JellyfinRescanAsync(cfg.Jellyfin, completedFilePath, kind));

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

    private async Task PlexRescanAsync(MediaServerIntegration p, string? snacksFilePath, MediaKind kind)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl  = p.BaseUrl.TrimEnd('/');

            // Try scoped: remap the Snacks file path into a path within one of
            // Plex's library roots, then refresh only that directory.
            if (!string.IsNullOrWhiteSpace(snacksFilePath))
            {
                var roots = RootsForKind(await GetPlexRootsAsync(http, baseUrl, p.Token), kind, PlexMusicSectionType);
                if (roots == null)
                {
                    Log.Information($"Plex: no {KindLabel(kind)} library; skipping rescan for '{snacksFilePath}'");
                    return;
                }

                var hit = MapPath(snacksFilePath, roots);
                if (hit != null)
                {
                    var scopeDir = ParentDirectory(hit.MappedPath) ?? hit.Root.Path;
                    var scoped   = $"{baseUrl}/library/sections/{hit.Root.Id}/refresh?path={Uri.EscapeDataString(scopeDir)}";
                    var sReq     = new HttpRequestMessage(HttpMethod.Get, scoped);
                    sReq.Headers.TryAddWithoutValidation("X-Plex-Token", p.Token);
                    using var sResp = await http.SendAsync(sReq);
                    if (sResp.IsSuccessStatusCode) return;
                    Log.Warning($"Plex scoped rescan failed (HTTP {(int)sResp.StatusCode}); falling back to full refresh");
                }
                else
                {
                    Log.Information($"Plex: no library root matched '{snacksFilePath}'; falling back to full refresh");
                }
            }

            // Fallback: refresh every section.
            var url = baseUrl + "/library/sections/all/refresh";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", p.Token);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                Log.Warning($"Plex rescan failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Plex rescan error: {ex.Message}");
        }
    }

    private async Task JellyfinRescanAsync(MediaServerIntegration j, string? snacksFilePath, MediaKind kind)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var baseUrl  = j.BaseUrl.TrimEnd('/');

            // Try scoped: remap the Snacks file path into a path within one of
            // Jellyfin's library roots, then notify Jellyfin about that single file.
            if (!string.IsNullOrWhiteSpace(snacksFilePath))
            {
                var roots = RootsForKind(await GetJellyfinRootsAsync(http, baseUrl, j.Token), kind, JellyfinMusicCollectionType);
                if (roots == null)
                {
                    Log.Information($"Jellyfin: no {KindLabel(kind)} library; skipping rescan for '{snacksFilePath}'");
                    return;
                }

                var hit = MapPath(snacksFilePath, roots);
                if (hit != null)
                {
                    var body = JsonSerializer.Serialize(new
                    {
                        Updates = new[] { new { Path = hit.MappedPath, UpdateType = "Modified" } }
                    });
                    var url = baseUrl + "/Library/Media/Updated";
                    var req = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    AddJellyfinAuth(req, j.Token);
                    using var resp = await http.SendAsync(req);
                    if (resp.IsSuccessStatusCode) return;
                    Log.Warning($"Jellyfin scoped rescan failed (HTTP {(int)resp.StatusCode}); falling back to full refresh");
                }
                else
                {
                    Log.Information($"Jellyfin: no library root matched '{snacksFilePath}'; falling back to full refresh");
                }
            }

            // Fallback: full library refresh.
            var fullUrl = baseUrl + "/Library/Refresh";
            var fullReq = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            AddJellyfinAuth(fullReq, j.Token);
            using var fullResp = await http.SendAsync(fullReq);
            if (!fullResp.IsSuccessStatusCode)
                Log.Warning($"Jellyfin rescan failed: HTTP {(int)fullResp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Jellyfin rescan error: {ex.Message}");
        }
    }

    /******************************************************************
     *  Library Roots (shared shape)
     ******************************************************************/

    /// <summary> A single filesystem root scanned by a media server library/section. </summary>
    /// <param name="Id">   Opaque identifier used in scoped-refresh URLs (Plex section key; Jellyfin virtual-folder name). </param>
    /// <param name="Path"> Filesystem path as the media server sees it.                                                   </param>
    /// <param name="Type"> The library's content type as the server reports it (Plex section type; Jellyfin CollectionType). </param>
    private sealed record LibraryRoot(string Id, string Path, string? Type = null);

    // Content types that mark a music library: Plex section type, Jellyfin CollectionType.
    private const string PlexMusicSectionType        = "artist";
    private const string JellyfinMusicCollectionType = "music";

    /// <summary>
    ///     The library roots a file of <paramref name="kind"/> can live in: music libraries for
    ///     music, every other library for video. <see langword="null"/> when the server lists
    ///     libraries but none of that kind, so the file can't be in it and a full refresh would
    ///     rescan everything for nothing. An empty list (the lookup failed) passes through so
    ///     the caller keeps its full-refresh fallback.
    /// </summary>
    private static IReadOnlyList<LibraryRoot>? RootsForKind(IReadOnlyList<LibraryRoot> roots, MediaKind kind, string musicType)
    {
        if (roots.Count == 0) return roots;
        var forKind = roots
            .Where(r => string.Equals(r.Type, musicType, StringComparison.OrdinalIgnoreCase) == (kind == MediaKind.Music))
            .ToList();
        return forKind.Count > 0 ? forKind : null;
    }

    private static string KindLabel(MediaKind kind) => kind == MediaKind.Music ? "music" : "video";

    /// <summary>
    ///     Authenticates a Jellyfin request. Current Jellyfin ships with legacy authorization
    ///     disabled and answers the old <c>X-Emby-Token</c> header with 401; this header form
    ///     works on current and older releases alike.
    /// </summary>
    private static void AddJellyfinAuth(HttpRequestMessage req, string apiKey) =>
        req.Headers.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{apiKey}\"");

    /// <summary> Result of a path-remap attempt. </summary>
    /// <param name="Root">       The library root that matched.                 </param>
    /// <param name="MappedPath"> The Snacks file path translated into that root. </param>
    private sealed record PathMapping(LibraryRoot Root, string MappedPath);

    /// <summary>
    ///     Given a Snacks file path, finds the <paramref name="roots"/> entry whose
    ///     trailing path segments also appear in the Snacks path, and returns the
    ///     Snacks path rewritten onto that root. Handles Docker/bind-mount layouts
    ///     where e.g. Snacks sees <c>/media/movies/X.mkv</c> and Plex sees
    ///     <c>/data/movies/X.mkv</c> — both share the <c>movies</c> anchor segment.
    /// </summary>
    /// <returns> The best mapping, or <see langword="null"/> if no root shares a segment with the Snacks path. </returns>
    private static PathMapping? MapPath(string snacksFilePath, IReadOnlyList<LibraryRoot> roots)
    {
        if (string.IsNullOrWhiteSpace(snacksFilePath) || roots.Count == 0) return null;

        var snacksSegs = SplitPathSegments(snacksFilePath);
        if (snacksSegs.Length == 0) return null;

        PathMapping? best  = null;
        var          bestK = 0;

        foreach (var root in roots)
        {
            var rootSegs = SplitPathSegments(root.Path);
            if (rootSegs.Length == 0) continue;

            // Walk the root's trailing-segment count k from largest possible down
            // to 1. The longest k that still appears contiguously in the Snacks
            // path wins — that's the deepest anchor we can align on.
            var maxK = Math.Min(rootSegs.Length, snacksSegs.Length);
            for (var k = maxK; k >= 1; k--)
            {
                if (k <= bestK) break; // can't improve

                var tail        = rootSegs[^k..];
                var idx         = FindSubsequence(snacksSegs, tail);
                if (idx < 0) continue;

                // Remaining Snacks segments after the matched anchor become the
                // suffix we append to the root (which already ends in the anchor).
                var remainder = snacksSegs[(idx + tail.Length)..];
                var rooted    = JoinOntoRoot(root.Path, remainder);
                best          = new PathMapping(root, rooted);
                bestK         = k;
                break;
            }
        }

        return best;
    }

    /// <summary> Splits a filesystem path into non-empty segments, normalizing both separators. </summary>
    private static string[] SplitPathSegments(string path) =>
        path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    ///     Case-insensitive contiguous-subsequence search. Returns the index of
    ///     <paramref name="needle"/>'s first occurrence in <paramref name="haystack"/>, or -1.
    /// </summary>
    private static int FindSubsequence(string[] haystack, string[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (!string.Equals(haystack[i + j], needle[j], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    ///     Joins <paramref name="remainder"/> onto <paramref name="rootPath"/>, preserving
    ///     the root's separator style so the media server sees a path it recognizes.
    /// </summary>
    private static string JoinOntoRoot(string rootPath, string[] remainder)
    {
        if (remainder.Length == 0) return rootPath;
        var sep      = rootPath.Contains('\\') && !rootPath.Contains('/') ? '\\' : '/';
        var trimmed  = rootPath.TrimEnd('/', '\\');
        return trimmed + sep + string.Join(sep, remainder);
    }

    /******************************************************************
     *  Plex Library Roots
     ******************************************************************/

    /// <summary>
    ///     Returns the Plex library sections (one <see cref="LibraryRoot"/> per
    ///     Location entry), cached for <see cref="_rootCacheTtl"/>. Empty list on
    ///     failure (caller then falls back to a full refresh).
    /// </summary>
    private async Task<IReadOnlyList<LibraryRoot>> GetPlexRootsAsync(HttpClient http, string baseUrl, string token)
    {
        var cacheKey = baseUrl + "|" + token;
        lock (_rootsLock)
        {
            if (_plexRootsCache.TryGetValue(cacheKey, out var entry) && entry.expires > DateTime.UtcNow)
                return entry.roots;
        }

        IReadOnlyList<LibraryRoot> fetched = Array.Empty<LibraryRoot>();
        try
        {
            var url = baseUrl + "/library/sections";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Plex-Token", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                fetched  = ParsePlexRoots(json);
            }
            else
            {
                Log.Warning($"Plex section lookup failed: HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Plex section lookup error: {ex.Message}");
        }

        lock (_rootsLock)
        {
            _plexRootsCache[cacheKey] = (DateTime.UtcNow + _rootCacheTtl, fetched);
        }
        return fetched;
    }

    /// <summary>
    ///     Flattens <c>MediaContainer.Directory[*].Location[*].path</c> into one
    ///     <see cref="LibraryRoot"/> per (section, location) pair.
    /// </summary>
    private static IReadOnlyList<LibraryRoot> ParsePlexRoots(string json)
    {
        var list = new List<LibraryRoot>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("MediaContainer", out var mc)) return list;
            if (!mc.TryGetProperty("Directory", out var dirs) || dirs.ValueKind != JsonValueKind.Array) return list;

            foreach (var d in dirs.EnumerateArray())
            {
                var key  = d.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                var type = d.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!d.TryGetProperty("Location", out var locs) || locs.ValueKind != JsonValueKind.Array) continue;

                foreach (var loc in locs.EnumerateArray())
                    if (loc.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path)
                        list.Add(new LibraryRoot(key!, path, type));
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Plex section parse error: {ex.Message}");
        }
        return list;
    }

    /******************************************************************
     *  Jellyfin Library Roots
     ******************************************************************/

    /// <summary>
    ///     Returns Jellyfin's virtual-folder roots (one <see cref="LibraryRoot"/>
    ///     per Locations entry), cached for <see cref="_rootCacheTtl"/>. Empty
    ///     list on failure (caller then falls back to a full refresh).
    /// </summary>
    private async Task<IReadOnlyList<LibraryRoot>> GetJellyfinRootsAsync(HttpClient http, string baseUrl, string apiKey)
    {
        var cacheKey = baseUrl + "|" + apiKey;
        lock (_rootsLock)
        {
            if (_jellyfinRootsCache.TryGetValue(cacheKey, out var entry) && entry.expires > DateTime.UtcNow)
                return entry.roots;
        }

        IReadOnlyList<LibraryRoot> fetched = Array.Empty<LibraryRoot>();
        try
        {
            var url = baseUrl + "/Library/VirtualFolders";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            AddJellyfinAuth(req, apiKey);
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                fetched  = ParseJellyfinRoots(json);
            }
            else
            {
                Log.Warning($"Jellyfin library lookup failed: HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Jellyfin library lookup error: {ex.Message}");
        }

        lock (_rootsLock)
        {
            _jellyfinRootsCache[cacheKey] = (DateTime.UtcNow + _rootCacheTtl, fetched);
        }
        return fetched;
    }

    /// <summary>
    ///     Flattens the <c>/Library/VirtualFolders</c> response into one
    ///     <see cref="LibraryRoot"/> per (virtual-folder, location) pair. The
    ///     Id field holds the virtual-folder name, which isn't used in scoped
    ///     calls but lets us log which library matched.
    /// </summary>
    private static IReadOnlyList<LibraryRoot> ParseJellyfinRoots(string json)
    {
        var list = new List<LibraryRoot>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var folder in doc.RootElement.EnumerateArray())
            {
                var name = folder.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                var type = folder.TryGetProperty("CollectionType", out var ct) && ct.ValueKind == JsonValueKind.String
                    ? ct.GetString()
                    : null;
                if (!folder.TryGetProperty("Locations", out var locs) || locs.ValueKind != JsonValueKind.Array) continue;

                foreach (var loc in locs.EnumerateArray())
                    if (loc.GetString() is { Length: > 0 } path)
                        list.Add(new LibraryRoot(name, path, type));
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Jellyfin library parse error: {ex.Message}");
        }
        return list;
    }

    /******************************************************************
     *  Arr Rescans
     ******************************************************************/

    /// <summary>
    ///     How long a series, movie, or album folder must go without another replaced file
    ///     before its Arr is asked to rescan it, so a season or album coalesces into one
    ///     scan. Settable for tests.
    /// </summary>
    internal TimeSpan RescanQuietPeriod { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary> Gap between Radarr command-status polls while waiting to restore monitoring. Settable for tests. </summary>
    internal TimeSpan ArrCommandPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    // Bounds the re-arms after an Arr merges a request into a scan that's already running.
    private const int ArrRescanMaxRearms = 5;

    // How long to wait for a Radarr rescan (which may queue behind other Radarr work)
    // before giving up on restoring the movie's monitored flag.
    private static readonly TimeSpan _arrCommandWaitLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     A resolved rescan: the Arr to ask and the command that rescans only the replaced
    ///     file's series, movie, or album folder.
    /// </summary>
    /// <param name="Flavor">        "Sonarr", "Radarr", or "Lidarr" (logging).                  </param>
    /// <param name="Arr">           Connection the command is posted to.                       </param>
    /// <param name="ApiVersion">    <c>"v3"</c> for Sonarr/Radarr, <c>"v1"</c> for Lidarr.        </param>
    /// <param name="Scope">         What gets rescanned, as the Arr names it (series/movie/album folder). </param>
    /// <param name="Command">       Body for <c>POST /api/{ApiVersion}/command</c>.                </param>
    /// <param name="RadarrMovieId"> The Radarr movie being rescanned, so its monitored flag can be guarded. </param>
    private sealed record ArrRescanTarget(string Flavor, ArrIntegration Arr, string ApiVersion, string Scope, object Command,
                                          int? RadarrMovieId = null);

    // Debounce state per scope key ("{flavor}|{baseUrl}|{scope}"). Ticket identifies the newest
    // pending request (only it fires); LastCommandId is the command an Arr last returned for
    // the scope, which tells a merged response apart from a freshly queued scan.
    private sealed class RescanState
    {
        public long     Ticket;
        public int?     LastCommandId;
        public DateTime Touched;
    }
    private readonly Dictionary<string, RescanState> _rescanStates = new();
    private readonly object _rescanLock = new();
    private long _rescanTicketSeq;

    /// <summary>
    ///     Asks the owning Arr to rescan the series, movie, or album of a file whose original
    ///     was just replaced. The Arrs track individual files, so a replacement (new extension,
    ///     or new size and codec under the same name) reads as missing or stale until that folder
    ///     is rescanned; the scan drops the old file record and imports the new file in place.
    ///     Each command is scoped to one item — never a library-wide scan — and debounced per
    ///     item by <see cref="RescanQuietPeriod"/>. Fire-and-forget; no-op when no relevant
    ///     Arr is enabled.
    /// </summary>
    /// <param name="originalPath"> Path of the replaced original <i>as Snacks sees it</i>. </param>
    /// <param name="kind">         Music goes to Lidarr; video to Sonarr or Radarr.     </param>
    public void QueueArrRescan(string originalPath, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(originalPath)) return;

        IntegrationConfig cfg;
        lock (_lock) cfg = _config;
        _ = RescanAsync(originalPath, kind, cfg);
    }

    private async Task RescanAsync(string path, MediaKind kind, IntegrationConfig cfg)
    {
        // The filename pattern decides which of Sonarr/Radarr is asked first; the other is
        // tried when the first has no match, so an episode without SxxEyy naming (or a
        // movie that looks like one) still finds its owner.
        (string flavor, ArrIntegration arr)[] candidates = kind == MediaKind.Music
            ? [("Lidarr", cfg.Lidarr)]
            : MediaTypeDetector.Classify(path) == MediaTypeDetector.MediaKind.Tv
                ? [("Sonarr", cfg.Sonarr), ("Radarr", cfg.Radarr)]
                : [("Radarr", cfg.Radarr), ("Sonarr", cfg.Sonarr)];
        var usable = candidates
            .Where(c => c.arr.Enabled && !string.IsNullOrWhiteSpace(c.arr.BaseUrl) && !string.IsNullOrWhiteSpace(c.arr.ApiKey))
            .ToList();
        if (usable.Count == 0) return;

        try
        {
            ArrRescanTarget? target = null;
            foreach (var (flavor, arr) in usable)
            {
                target = await ResolveRescanTargetAsync(flavor, arr, path);
                if (target != null) break;
            }

            if (target == null)
            {
                // No library-wide fallback as with Plex/Jellyfin: a full scan per encode would
                // be heavy on large libraries, and an unmatched path isn't in the Arr anyway.
                var sought = usable.Select(c => c.flavor switch
                {
                    "Sonarr" => "Sonarr series",
                    "Radarr" => "Radarr movie",
                    _        => "Lidarr root folder",
                });
                Log.Information($"No {string.Join(" or ", sought)} matched '{path}'; skipping rescan");
                return;
            }

            await DebouncedRescanAsync(target, rearms: 0);
        }
        catch (Exception ex)
        {
            Log.Warning($"Arr rescan error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Finds what <paramref name="flavor"/> should rescan for <paramref name="path"/>:
    ///     Lidarr's album folder (by mapping onto its root folders) or the Sonarr series /
    ///     Radarr movie whose folder lines up with the path. <see langword="null"/> when nothing matches.
    /// </summary>
    private async Task<ArrRescanTarget?> ResolveRescanTargetAsync(string flavor, ArrIntegration arr, string path)
    {
        if (flavor == "Lidarr")
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var roots    = await GetLidarrRootsAsync(http, arr.BaseUrl.TrimEnd('/'), arr.ApiKey);
            var hit      = MapPath(path, roots);
            if (hit == null) return null;

            var folder = ParentDirectory(hit.MappedPath) ?? hit.Root.Path;
            return new ArrRescanTarget(flavor, arr, "v1", folder, new
            {
                name          = "RescanFolders",
                folders       = new[] { folder },
                // The command's own default is true (the scheduled-task setting); rescanning an
                // existing album folder must never add artists as a side effect.
                addNewArtists = false,
            });
        }

        // Same anchor matching as library roots, with each series/movie folder as a root:
        // the entry whose trailing directory names appear deepest in the Snacks path wins,
        // so mounts that differ between Snacks and the Arr still line up.
        var isSeries = flavor == "Sonarr";
        var catalog  = await GetArrCatalogAsync(arr, isSeries, CancellationToken.None);
        var match    = MapPath(path, catalog.Select(e => new LibraryRoot(e.Id.ToString(), e.Path)).ToList());
        if (match == null) return null;

        var id = int.Parse(match.Root.Id);
        return isSeries
            ? new ArrRescanTarget(flavor, arr, "v3", match.Root.Path, new { name = "RescanSeries", seriesId = id })
            : new ArrRescanTarget(flavor, arr, "v3", match.Root.Path, new { name = "RescanMovie",  movieId  = id },
                                  RadarrMovieId: id);
    }

    /// <summary>
    ///     Waits out <see cref="RescanQuietPeriod"/> for <paramref name="key"/>.
    /// </summary>
    /// <returns> <see langword="false"/> when a later request for the same key re-armed the timer — that one fires instead. </returns>
    private async Task<bool> WaitForQuietAsync(string key)
    {
        long ticket;
        lock (_rescanLock)
        {
            var stale = _rescanStates.Where(kv => kv.Value.Touched < DateTime.UtcNow.AddHours(-1)).Select(kv => kv.Key).ToList();
            foreach (var k in stale) _rescanStates.Remove(k);

            if (!_rescanStates.TryGetValue(key, out var state)) _rescanStates[key] = state = new RescanState();
            state.Ticket  = ticket = ++_rescanTicketSeq;
            state.Touched = DateTime.UtcNow;
        }

        await Task.Delay(RescanQuietPeriod);

        lock (_rescanLock)
            return _rescanStates.TryGetValue(key, out var state) && state.Ticket == ticket;
    }

    private async Task DebouncedRescanAsync(ArrRescanTarget target, int rearms)
    {
        var key = $"{target.Flavor}|{target.Arr.BaseUrl.TrimEnd('/')}|{target.Scope}";
        if (!await WaitForQuietAsync(key)) return;

        int? lastCommandId;
        lock (_rescanLock)
            lastCommandId = _rescanStates.TryGetValue(key, out var state) ? state.LastCommandId : null;

        // With "Unmonitor Deleted Movies" on, Radarr unmonitors a movie as soon as its old
        // file is found missing — even though this same scan imports the replacement
        // (Sonarr defers that decision until after the scan; Radarr doesn't). Note the
        // flag first so it can be put back.
        var restoreMonitoring = target.RadarrMovieId is int movieId
                                && await GetRadarrMonitoredAsync(target.Arr, movieId) == true;

        var command = await PostArrCommandAsync(target);
        if (command == null) return;

        lock (_rescanLock)
        {
            if (_rescanStates.TryGetValue(key, out var state))
            {
                state.LastCommandId = command.Value.id;
                state.Touched       = DateTime.UtcNow;
            }
        }

        // Without a command id the scan can't be followed, so there's nothing to wait on.
        if (restoreMonitoring && command.Value.id is int commandId)
            await RestoreRadarrMonitoringAsync(target, commandId);

        // The Arrs fold a new command into an identical one that is queued *or already
        // running* and answer with that command. Getting our previous id back as "started"
        // means the scan may have listed the folder before this file landed, so re-arm for
        // a fresh scan once it's done.
        if (command.Value.id is int id && id == lastCommandId
            && string.Equals(command.Value.status, "started", StringComparison.OrdinalIgnoreCase)
            && rearms < ArrRescanMaxRearms)
        {
            await DebouncedRescanAsync(target, rearms + 1);
        }
    }

    /// <summary> Posts <paramref name="target"/>'s command. </summary>
    /// <returns>
    ///     The command id (<see langword="null"/> if the response had none) and status the Arr
    ///     answered with, or <see langword="null"/> when the post failed.
    /// </returns>
    private async Task<(int? id, string status)?> PostArrCommandAsync(ArrRescanTarget target)
    {
        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);

        var req = new HttpRequestMessage(HttpMethod.Post, $"{target.Arr.BaseUrl.TrimEnd('/')}/api/{target.ApiVersion}/command")
        {
            Content = new StringContent(JsonSerializer.Serialize(target.Command), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Api-Key", target.Arr.ApiKey);
        using var resp = await http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            Log.Warning($"{target.Flavor} rescan of '{target.Scope}' failed: HTTP {(int)resp.StatusCode}");
            return null;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        int? id    = doc.RootElement.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var i) ? i : null;
        var status = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() ?? "" : "";
        Log.Information($"{target.Flavor}: rescan of '{target.Scope}' requested (command {id?.ToString() ?? "without id"}, {status})");
        return (id, status);
    }

    /// <summary>
    ///     Waits for Radarr's rescan to finish, then re-monitors the movie if the scan
    ///     unmonitored it. Only called when the movie was monitored before the rescan.
    /// </summary>
    private async Task RestoreRadarrMonitoringAsync(ArrRescanTarget target, int commandId)
    {
        var movieId  = target.RadarrMovieId!.Value;
        var baseUrl  = target.Arr.BaseUrl.TrimEnd('/');
        var deadline = DateTime.UtcNow + _arrCommandWaitLimit;

        string? status = null;
        var misses     = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(ArrCommandPollInterval);
            status = await GetArrJsonStringAsync(target.Arr, $"{baseUrl}/api/v3/command/{commandId}", "status");
            if (status == null)
            {
                if (++misses < 3) continue; // transient read failure — ask again
            }
            else
            {
                misses = 0;                 // only failures in a row count
            }
            if (status is not ("queued" or "started")) break;
        }

        if (status is "queued" or "started")
        {
            Log.Warning($"Radarr: rescan of '{target.Scope}' still {status} after {_arrCommandWaitLimit.TotalMinutes:F0} min; " +
                        "not checking whether it unmonitored the movie");
            return;
        }

        if (await GetRadarrMonitoredAsync(target.Arr, movieId) != false) return;

        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var req = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/v3/movie/editor")
        {
            // The editor endpoint changes only the fields it is given.
            Content = new StringContent(JsonSerializer.Serialize(new { movieIds = new[] { movieId }, monitored = true }),
                                        Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("X-Api-Key", target.Arr.ApiKey);
        using var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
            Log.Information($"Radarr: re-monitored '{target.Scope}' after the rescan unmonitored it for the replaced file");
        else
            Log.Warning($"Radarr: could not re-monitor '{target.Scope}': HTTP {(int)resp.StatusCode}");
    }

    /// <summary>
    ///     Reads a Radarr movie's monitored flag; <see langword="null"/> when it can't be read,
    ///     so a failed read skips the monitoring guard instead of the rescan.
    /// </summary>
    private async Task<bool?> GetRadarrMonitoredAsync(ArrIntegration arr, int movieId)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var req = new HttpRequestMessage(HttpMethod.Get, $"{arr.BaseUrl.TrimEnd('/')}/api/v3/movie/{movieId}");
            req.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("monitored", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? m.GetBoolean()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary> GETs <paramref name="url"/> and returns a top-level string property, lower-cased; <see langword="null"/> on failure. </summary>
    private async Task<string?> GetArrJsonStringAsync(ArrIntegration arr, string url, string property)
    {
        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty(property, out var v) ? v.GetString()?.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Returns Lidarr's root folders, cached for <see cref="_rootCacheTtl"/>. Empty list on
    ///     failure (the caller then skips the rescan).
    /// </summary>
    private async Task<IReadOnlyList<LibraryRoot>> GetLidarrRootsAsync(HttpClient http, string baseUrl, string apiKey)
    {
        var cacheKey = baseUrl + "|" + apiKey;
        lock (_rootsLock)
        {
            if (_lidarrRootsCache.TryGetValue(cacheKey, out var entry) && entry.expires > DateTime.UtcNow)
                return entry.roots;
        }

        IReadOnlyList<LibraryRoot> fetched = Array.Empty<LibraryRoot>();
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/rootfolder");
            req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                fetched = ParseLidarrRoots(await resp.Content.ReadAsStringAsync());
            else
                Log.Warning($"Lidarr root folder lookup failed: HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Lidarr root folder lookup error: {ex.Message}");
        }

        lock (_rootsLock)
        {
            _lidarrRootsCache[cacheKey] = (DateTime.UtcNow + _rootCacheTtl, fetched);
        }
        return fetched;
    }

    /// <summary> Flattens the <c>/api/v1/rootfolder</c> array into one <see cref="LibraryRoot"/> per root. </summary>
    private static IReadOnlyList<LibraryRoot> ParseLidarrRoots(string json)
    {
        var list = new List<LibraryRoot>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var root in doc.RootElement.EnumerateArray())
            {
                var id = root.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                if (root.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path)
                    list.Add(new LibraryRoot(id, path));
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Lidarr root folder parse error: {ex.Message}");
        }
        return list;
    }

    /// <summary>
    ///     Parent of a remote server's path, kept in that server's own separator style.
    ///     <see cref="Path.GetDirectoryName(string?)"/> applies Snacks' host rules instead: on
    ///     Windows it rewrites <c>/</c> to <c>\</c> (a Linux Plex answers 200 and scans a folder
    ///     that doesn't exist), and on Linux it finds no parent in a <c>\</c> path at all.
    /// </summary>
    private static string? ParentDirectory(string path)
    {
        var cut = path.TrimEnd('/', '\\').LastIndexOfAny(['/', '\\']);
        return cut > 0 ? path[..cut] : null;
    }

    /******************************************************************
     *  Original-Language Lookups
     ******************************************************************/

    /// <summary> One Sonarr series or Radarr movie: its id, its folder as the Arr sees it, and its original language when reported. </summary>
    private sealed record ArrCatalogEntry(int Id, string Path, string? Lang);

    // Sonarr series / Radarr movie catalogue per (provider, baseUrl+key). Shared by the
    // original-language lookup and the post-replacement rescans.
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<ArrCatalogEntry> entries)> _arrCatalogCache = new();
    private (string token, DateTime expires)? _tvdbToken;
    private readonly object _tvdbLock = new();

    /// <summary>
    ///     Resolves the original language of a media file (ISO 639-1, 2-letter).
    ///     Provider can be <c>"None"</c>, <c>"Auto"</c>, <c>"Sonarr"</c>, <c>"Radarr"</c>,
    ///     <c>"TVDB"</c>, or <c>"TMDb"</c>. Returns <c>null</c> on any failure; callers
    ///     are expected to proceed with the configured keep-list unchanged in that case.
    /// </summary>
    public async Task<string?> LookupOriginalLanguageAsync(string filePath, string provider, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("None", StringComparison.OrdinalIgnoreCase)) return null;

        var kind = MediaTypeDetector.Classify(filePath);

        IntegrationConfig cfg;
        lock (_lock) cfg = _config;

        // "Auto": prefer a configured Arr instance for the media kind, then TVDB (TV) or TMDb.
        if (provider.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            provider = kind == MediaTypeDetector.MediaKind.Tv
                ? (cfg.Sonarr.Enabled ? "Sonarr"
                    : cfg.Tvdb.Enabled  ? "TVDB"
                    : cfg.Tmdb.Enabled  ? "TMDb" : "None")
                : (cfg.Radarr.Enabled ? "Radarr"
                    : cfg.Tmdb.Enabled  ? "TMDb" : "None");
            if (provider == "None") return null;
        }

        try
        {
            return provider.ToLowerInvariant() switch
            {
                "sonarr" when kind == MediaTypeDetector.MediaKind.Tv    => await LookupArrLangAsync(filePath, cfg.Sonarr, isSeries: true,  ct),
                "radarr" when kind == MediaTypeDetector.MediaKind.Movie => await LookupArrLangAsync(filePath, cfg.Radarr, isSeries: false, ct),
                "tvdb"   when kind == MediaTypeDetector.MediaKind.Tv    => await LookupTvdbLangAsync(filePath, cfg.Tvdb, ct),
                "tmdb"                                                  => await LookupTmdbLangAsync(filePath, cfg.Tmdb, kind, ct),
                _                                                       => null,
            };
        }
        // Rethrow only genuine caller cancellation. HttpClient timeouts surface as
        // TaskCanceledException (an OCE) even when the caller passed
        // CancellationToken.None — those must degrade to null like any other
        // lookup failure, not unwind the dispatcher.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning($"Original-language lookup error ({provider}): {ex.Message}");
            return null;
        }
    }

    private async Task<string?> LookupArrLangAsync(string filePath, ArrIntegration arr, bool isSeries, CancellationToken ct)
    {
        if (!arr.Enabled || string.IsNullOrWhiteSpace(arr.BaseUrl) || string.IsNullOrWhiteSpace(arr.ApiKey))
            return null;

        var cached = (await GetArrCatalogAsync(arr, isSeries, ct))
            .Where(e => !string.IsNullOrEmpty(e.Lang))
            .Select(e => (path: e.Path, lang: e.Lang!))
            .ToList();

        // Longest-prefix match so nested series roots pick the deepest entry.
        var norm  = filePath.Replace('\\', '/');
        var hit   = cached
            .Where(e => norm.StartsWith(e.path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.path.Length)
            .FirstOrDefault();

        // Fallback: Radarr/Sonarr often run in containers or on a different host, so their
        // stored paths (e.g. "/movies/TRON - Legacy (2010)") won't prefix the local path
        // (e.g. "D:\Media\Movies\TRON - Legacy (2010).mkv"). When prefix matching misses,
        // compare by the movie/series folder name — it's part of the Arr naming convention
        // ("Title (year)") and collides rarely in practice.
        if (string.IsNullOrEmpty(hit.lang))
        {
            // Only the file name loses its extension (the flat "Movies/Title (year).mkv"
            // layout above); directory names are compared whole — a dot in "Mr. Robot (2015)"
            // isn't an extension.
            var parts          = norm.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var sourceSegments = parts.SkipLast(1)
                .Append(parts.Length > 0 ? Path.GetFileNameWithoutExtension(parts[^1]) : "")
                .Where(s => !string.IsNullOrEmpty(s))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            hit = cached
                .Select(e => (e.path, e.lang,
                              folder: Path.GetFileName(e.path.Replace('\\', '/').TrimEnd('/')) ?? ""))
                .Where(e => e.folder.Length > 0 && sourceSegments.Contains(e.folder))
                .OrderByDescending(e => e.folder.Length)
                .Select(e => (e.path, e.lang))
                .FirstOrDefault();
        }

        return string.IsNullOrEmpty(hit.lang) ? null : LanguageMatcher.ToTwoLetter(hit.lang);
    }

    /// <summary>
    ///     Returns the Sonarr series or Radarr movie catalogue, cached for <see cref="_rootCacheTtl"/>.
    ///     A failed fetch yields an empty, negative-cached list; genuine caller cancellation propagates.
    /// </summary>
    private async Task<IReadOnlyList<ArrCatalogEntry>> GetArrCatalogAsync(ArrIntegration arr, bool isSeries, CancellationToken ct)
    {
        var cacheKey = (isSeries ? "sonarr|" : "radarr|") + arr.BaseUrl + "|" + arr.ApiKey;
        lock (_rootsLock)
        {
            if (_arrCatalogCache.TryGetValue(cacheKey, out var entry) && entry.expires > DateTime.UtcNow)
                return entry.entries;
        }

        try
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var baseUrl  = arr.BaseUrl.TrimEnd('/');
            var url      = baseUrl + (isSeries ? "/api/v3/series" : "/api/v3/movie");
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return CacheArrFailure(cacheKey, isSeries, $"HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);

            var list = new List<ArrCatalogEntry>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;
                    var id = item.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var i) ? i : 0;

                    string? lang = null;
                    if (item.TryGetProperty("originalLanguage", out var ol))
                    {
                        if (ol.ValueKind == JsonValueKind.Object && ol.TryGetProperty("name", out var nEl))
                            lang = nEl.GetString();
                        else if (ol.ValueKind == JsonValueKind.String)
                            lang = ol.GetString();
                    }
                    list.Add(new ArrCatalogEntry(id, path!, string.IsNullOrEmpty(lang) ? null : lang));
                }
            }
            lock (_rootsLock)
            {
                _arrCatalogCache[cacheKey] = (DateTime.UtcNow + _rootCacheTtl, list);
            }
            return list;
        }
        // Genuine caller cancellation propagates; a timeout (also an OCE when the
        // token isn't cancelled), connection failure, or malformed payload is
        // negative-cached so a struggling Arr instance isn't re-fetched on every
        // dispatch of a busy queue.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return CacheArrFailure(cacheKey, isSeries, ex.Message);
        }
    }

    /// <summary>
    ///     Negative-caches a failed Arr catalogue fetch: an empty list under the short
    ///     failure TTL makes every lookup and rescan miss (lookups then fall back to the
    ///     configured keep lists) without re-hitting the endpoint per dispatch.
    ///     A config save clears the cache, so credential/URL fixes apply immediately.
    /// </summary>
    private IReadOnlyList<ArrCatalogEntry> CacheArrFailure(string cacheKey, bool isSeries, string reason)
    {
        Log.Warning($"{(isSeries ? "Sonarr" : "Radarr")} catalogue fetch failed ({reason}) — " +
                    $"suppressing original-language lookups and rescans for {_arrFailureCacheTtl.TotalMinutes:F0} min");
        lock (_rootsLock)
        {
            _arrCatalogCache[cacheKey] = (DateTime.UtcNow + _arrFailureCacheTtl, Array.Empty<ArrCatalogEntry>());
        }
        return Array.Empty<ArrCatalogEntry>();
    }

    private async Task<string?> LookupTmdbLangAsync(string filePath, TmdbIntegration tmdb, MediaTypeDetector.MediaKind kind, CancellationToken ct)
    {
        if (!tmdb.Enabled || string.IsNullOrWhiteSpace(tmdb.ApiKey)) return null;

        string query, searchPath;
        int? year;
        if (kind == MediaTypeDetector.MediaKind.Movie)
        {
            (query, year) = MediaTypeDetector.ExtractMovieTitle(filePath);
            searchPath = "/3/search/movie";
        }
        else
        {
            query = MediaTypeDetector.ExtractSeriesTitle(filePath);
            year  = null;
            searchPath = "/3/search/tv";
        }
        if (string.IsNullOrWhiteSpace(query)) return null;

        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var url = $"https://api.themoviedb.org{searchPath}?api_key={Uri.EscapeDataString(tmdb.ApiKey)}&query={Uri.EscapeDataString(query)}"
                + (year.HasValue ? $"&year={year.Value}" : "");
        using var resp = await http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)
         || results.ValueKind != JsonValueKind.Array) return null;

        var first = results.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        if (!first.TryGetProperty("original_language", out var olEl)) return null;
        return LanguageMatcher.ToTwoLetter(olEl.GetString());
    }

    private async Task<string?> LookupTvdbLangAsync(string filePath, TvdbIntegration cfg, CancellationToken ct)
    {
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.ApiKey)) return null;

        var token = await GetTvdbTokenAsync(cfg, ct);
        if (string.IsNullOrEmpty(token)) return null;

        var query = MediaTypeDetector.ExtractSeriesTitle(filePath);
        if (string.IsNullOrWhiteSpace(query)) return null;

        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var url = $"https://api4.thetvdb.com/v4/search?query={Uri.EscapeDataString(query)}&type=series";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            lock (_tvdbLock) _tvdbToken = null;
            return null;
        }
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        var first = data.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object) return null;
        string? lang = first.TryGetProperty("primary_language", out var plEl) ? plEl.GetString() : null;
        if (string.IsNullOrEmpty(lang) && first.TryGetProperty("originalLanguage", out var olEl))
            lang = olEl.GetString();
        return LanguageMatcher.ToTwoLetter(lang);
    }

    private async Task<string?> GetTvdbTokenAsync(TvdbIntegration cfg, CancellationToken ct)
    {
        lock (_tvdbLock)
        {
            if (_tvdbToken is { } t && t.expires > DateTime.UtcNow.AddMinutes(5))
                return t.token;
        }

        var http     = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(10);
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            apikey = cfg.ApiKey,
            pin    = cfg.Pin ?? "",
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api4.thetvdb.com/v4/login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)
         || !data.TryGetProperty("token", out var tokEl)) return null;

        var token = tokEl.GetString();
        if (string.IsNullOrEmpty(token)) return null;
        lock (_tvdbLock) _tvdbToken = (token, DateTime.UtcNow.AddDays(30));
        return token;
    }

    /// <summary> Probes TMDb with the configured key. </summary>
    public async Task<(bool ok, string message)> TestTmdbAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "API key required");
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var url = $"https://api.themoviedb.org/3/configuration?api_key={Uri.EscapeDataString(apiKey)}";
            using var resp = await http.GetAsync(url);
            return resp.IsSuccessStatusCode ? (true, "TMDb connection OK") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary> Probes TVDB login with the configured key (and optional PIN). </summary>
    public async Task<(bool ok, string message)> TestTvdbAsync(string apiKey, string? pin)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "API key required");
        try
        {
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            var body = System.Text.Json.JsonSerializer.Serialize(new { apikey = apiKey, pin = pin ?? "" });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api4.thetvdb.com/v4/login")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? (true, "TVDB connection OK") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
