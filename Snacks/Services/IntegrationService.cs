using System.Net.Http.Headers;
using System.Text;
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

    // Library-root caches. 10-minute TTL is long enough to avoid per-encode
    // lookups on a busy queue, short enough that library edits pick up on a
    // reasonable timescale.
    private static readonly TimeSpan _rootCacheTtl = TimeSpan.FromMinutes(10);
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<LibraryRoot> roots)> _plexRootsCache     = new();
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<LibraryRoot> roots)> _jellyfinRootsCache = new();
    private readonly object _rootsLock = new();

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
            // Credentials or base URL may have changed — drop the library-root caches.
            lock (_rootsLock)
            {
                _plexRootsCache.Clear();
                _jellyfinRootsCache.Clear();
            }
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
    ///     Triggers a library rescan on any enabled media server with rescan-on-complete
    ///     enabled. When <paramref name="completedFilePath"/> is provided, each server
    ///     attempts a per-item scan scoped to that file (remapping the path through the
    ///     server's own library roots so Docker/bind-mount layouts work); if the scoped
    ///     call fails or no matching library root is found, falls back to a full refresh.
    ///     Best-effort: logs and swallows individual errors.
    /// </summary>
    /// <param name="completedFilePath">
    ///     Absolute path of the file that just finished encoding <i>as Snacks sees it</i>.
    ///     <see langword="null"/> or empty triggers a full library refresh on each server.
    /// </param>
    public async Task TriggerRescansAsync(string? completedFilePath = null)
    {
        IntegrationConfig cfg;
        lock (_lock) cfg = _config;

        var tasks = new List<Task>();
        if (cfg.Plex.Enabled && cfg.Plex.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Plex.BaseUrl))
            tasks.Add(PlexRescanAsync(cfg.Plex, completedFilePath));
        if (cfg.Jellyfin.Enabled && cfg.Jellyfin.RescanOnComplete && !string.IsNullOrWhiteSpace(cfg.Jellyfin.BaseUrl))
            tasks.Add(JellyfinRescanAsync(cfg.Jellyfin, completedFilePath));

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

    private async Task PlexRescanAsync(MediaServerIntegration p, string? snacksFilePath)
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
                var roots = await GetPlexRootsAsync(http, baseUrl, p.Token);
                var hit   = MapPath(snacksFilePath, roots);
                if (hit != null)
                {
                    var scopeDir = GetDirectorySafe(hit.MappedPath) ?? hit.Root.Path;
                    var scoped   = $"{baseUrl}/library/sections/{hit.Root.Id}/refresh?path={Uri.EscapeDataString(scopeDir)}";
                    var sReq     = new HttpRequestMessage(HttpMethod.Get, scoped);
                    sReq.Headers.TryAddWithoutValidation("X-Plex-Token", p.Token);
                    using var sResp = await http.SendAsync(sReq);
                    if (sResp.IsSuccessStatusCode) return;
                    Console.WriteLine($"Plex scoped rescan failed (HTTP {(int)sResp.StatusCode}); falling back to full refresh");
                }
                else
                {
                    Console.WriteLine($"Plex: no library root matched '{snacksFilePath}'; falling back to full refresh");
                }
            }

            // Fallback: refresh every section.
            var url = baseUrl + "/library/sections/all/refresh";
            var req = new HttpRequestMessage(HttpMethod.Get, url);
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

    private async Task JellyfinRescanAsync(MediaServerIntegration j, string? snacksFilePath)
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
                var roots = await GetJellyfinRootsAsync(http, baseUrl, j.Token);
                var hit   = MapPath(snacksFilePath, roots);
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
                    req.Headers.TryAddWithoutValidation("X-Emby-Token", j.Token);
                    using var resp = await http.SendAsync(req);
                    if (resp.IsSuccessStatusCode) return;
                    Console.WriteLine($"Jellyfin scoped rescan failed (HTTP {(int)resp.StatusCode}); falling back to full refresh");
                }
                else
                {
                    Console.WriteLine($"Jellyfin: no library root matched '{snacksFilePath}'; falling back to full refresh");
                }
            }

            // Fallback: full library refresh.
            var fullUrl = baseUrl + "/Library/Refresh";
            var fullReq = new HttpRequestMessage(HttpMethod.Post, fullUrl);
            fullReq.Headers.TryAddWithoutValidation("X-Emby-Token", j.Token);
            using var fullResp = await http.SendAsync(fullReq);
            if (!fullResp.IsSuccessStatusCode)
                Console.WriteLine($"Jellyfin rescan failed: HTTP {(int)fullResp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Jellyfin rescan error: {ex.Message}");
        }
    }

    /******************************************************************
     *  Library Roots (shared shape)
     ******************************************************************/

    /// <summary> A single filesystem root scanned by a media server library/section. </summary>
    /// <param name="Id">   Opaque identifier used in scoped-refresh URLs (Plex section key; Jellyfin virtual-folder name). </param>
    /// <param name="Path"> Filesystem path as the media server sees it.                                                   </param>
    private sealed record LibraryRoot(string Id, string Path);

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

    private static string? GetDirectorySafe(string filePath)
    {
        try
        {
            // Path.GetDirectoryName strips a trailing separator and handles both
            // Windows and POSIX layouts; it returns null/empty if there's no parent.
            var d = Path.GetDirectoryName(filePath);
            return string.IsNullOrEmpty(d) ? null : d;
        }
        catch { return null; }
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
                Console.WriteLine($"Plex section lookup failed: HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Plex section lookup error: {ex.Message}");
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
                var key = d.TryGetProperty("key", out var k) ? k.GetString() : null;
                if (string.IsNullOrEmpty(key)) continue;
                if (!d.TryGetProperty("Location", out var locs) || locs.ValueKind != JsonValueKind.Array) continue;

                foreach (var loc in locs.EnumerateArray())
                    if (loc.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path)
                        list.Add(new LibraryRoot(key!, path));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Plex section parse error: {ex.Message}");
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
            req.Headers.TryAddWithoutValidation("X-Emby-Token", apiKey);
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                fetched  = ParseJellyfinRoots(json);
            }
            else
            {
                Console.WriteLine($"Jellyfin library lookup failed: HTTP {(int)resp.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Jellyfin library lookup error: {ex.Message}");
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
                if (!folder.TryGetProperty("Locations", out var locs) || locs.ValueKind != JsonValueKind.Array) continue;

                foreach (var loc in locs.EnumerateArray())
                    if (loc.GetString() is { Length: > 0 } path)
                        list.Add(new LibraryRoot(name, path));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Jellyfin library parse error: {ex.Message}");
        }
        return list;
    }

    /******************************************************************
     *  Original-Language Lookups
     ******************************************************************/

    // Cache prefix-map from integration (provider, baseUrl+key) to {series/movie path, originalLanguage}.
    private readonly Dictionary<string, (DateTime expires, IReadOnlyList<(string path, string lang)> map)> _arrLangCache = new();
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
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"Original-language lookup error ({provider}): {ex.Message}");
            return null;
        }
    }

    private async Task<string?> LookupArrLangAsync(string filePath, ArrIntegration arr, bool isSeries, CancellationToken ct)
    {
        if (!arr.Enabled || string.IsNullOrWhiteSpace(arr.BaseUrl) || string.IsNullOrWhiteSpace(arr.ApiKey))
            return null;

        var cacheKey = (isSeries ? "sonarr|" : "radarr|") + arr.BaseUrl + "|" + arr.ApiKey;
        IReadOnlyList<(string path, string lang)>? cached = null;
        lock (_rootsLock)
        {
            if (_arrLangCache.TryGetValue(cacheKey, out var entry) && entry.expires > DateTime.UtcNow)
                cached = entry.map;
        }

        if (cached == null)
        {
            var http     = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var baseUrl  = arr.BaseUrl.TrimEnd('/');
            var url      = baseUrl + (isSeries ? "/api/v3/series" : "/api/v3/movie");
            var req      = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);

            var list = new List<(string path, string lang)>();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var pEl) ? pEl.GetString() : null;
                    if (string.IsNullOrEmpty(path)) continue;

                    string? lang = null;
                    if (item.TryGetProperty("originalLanguage", out var ol))
                    {
                        if (ol.ValueKind == JsonValueKind.Object && ol.TryGetProperty("name", out var nEl))
                            lang = nEl.GetString();
                        else if (ol.ValueKind == JsonValueKind.String)
                            lang = ol.GetString();
                    }
                    if (!string.IsNullOrEmpty(lang))
                        list.Add((path!, lang));
                }
            }
            cached = list;
            lock (_rootsLock)
            {
                _arrLangCache[cacheKey] = (DateTime.UtcNow + _rootCacheTtl, cached);
            }
        }

        // Longest-prefix match so nested series roots pick the deepest entry.
        var norm  = filePath.Replace('\\', '/');
        var hit   = cached
            .Where(e => norm.StartsWith(e.path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.path.Length)
            .FirstOrDefault();

        return string.IsNullOrEmpty(hit.lang) ? null : LanguageMatcher.ToTwoLetter(hit.lang);
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
