using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Snacks.Models;

namespace Snacks.Services;

/// <summary> Sanitized outcome of one remote task, in the shape Luna's completion API expects. </summary>
/// <param name="Succeeded"> Whether the local operation completed. </param>
/// <param name="Result"> Path-free result payload returned to Luna on success. </param>
/// <param name="ErrorCode"> Stable machine-readable failure code (e.g. <c>configuration_required</c>). </param>
/// <param name="ErrorMessage"> Sanitized human-readable failure description. </param>
public sealed record LunaTaskExecutionResult(
    bool Succeeded,
    JsonElement? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary> Wraps a sanitized payload as a success result. </summary>
    public static LunaTaskExecutionResult Ok(object value) =>
        new(true, JsonSerializer.SerializeToElement(value, JsonOptions));

    /// <summary> Builds a failure result from a stable code and a sanitized message. </summary>
    public static LunaTaskExecutionResult Fail(string code, string message) =>
        new(false, null, code, message);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>
///     Executes the deliberately small remote-task allow-list. Task arguments can
///     select ids and ordinary options, but can never supply a URL, API key, path,
///     HTTP verb, or raw request body.
/// </summary>
public sealed class LunaTaskExecutor
{
    // Hard result bounds. Page size and search count cap what a single task can
    // exfiltrate; the byte cap keeps a huge Arr library from being buffered into
    // memory wholesale for one remote request.
    private const int MaxLibraryPageSize  = 200;
    private const int MaxSearchResults    = 20;
    private const int MaxArrResponseBytes = 16 * 1024 * 1024;

    private readonly IntegrationService _integrations;
    private readonly IHttpClientFactory _httpClientFactory;

    public LunaTaskExecutor(IntegrationService integrations, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(integrations);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _integrations      = integrations;
        _httpClientFactory = httpClientFactory;
    }

    /******************************************************************
     *  Capabilities & Dispatch
     ******************************************************************/

    /// <summary>
    ///     Returns the action names currently advertisable to Luna, derived from the
    ///     live Arr configuration and the per-permission Luna toggles.
    /// </summary>
    public IReadOnlyList<string> GetCapabilities()
    {
        var config = _integrations.GetConfig();
        if (!config.Luna.Enabled) return Array.Empty<string>();

        var capabilities = new List<string>();
        AddArrCapabilities(capabilities, "radarr", config.Radarr, config.Luna);
        AddArrCapabilities(capabilities, "sonarr", config.Sonarr, config.Luna);
        return capabilities;
    }

    /// <summary>
    ///     Runs one leased task against the local Arr APIs and returns a sanitized
    ///     result. The live capability list is rechecked first, so disabling a
    ///     permission blocks the action immediately. Never throws for task-level
    ///     failures — those map to typed error codes safe to return to Luna.
    /// </summary>
    /// <param name="action"> The capability name Luna leased (e.g. <c>radarr.movie.add</c>). </param>
    /// <param name="arguments"> The task's JSON arguments; ids and bounded options only. </param>
    /// <param name="cancellationToken"> Cancels the local Arr requests. </param>
    public async Task<LunaTaskExecutionResult> ExecuteAsync(
        string action,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        action = action?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!GetCapabilities().Contains(action, StringComparer.Ordinal))
            return LunaTaskExecutionResult.Fail(
                "capability_disabled",
                $"'{action}' is not enabled in Snacks.");

        var config = _integrations.GetConfig();
        try
        {
            return action switch
            {
                "radarr.library.read" => await ReadLibraryAsync(config.Radarr, false, arguments, cancellationToken),
                "radarr.catalog.search" => await SearchCatalogAsync(config.Radarr, false, arguments, cancellationToken),
                "radarr.movie.add" => await AddMovieAsync(config.Radarr, arguments, cancellationToken),
                "sonarr.library.read" => await ReadLibraryAsync(config.Sonarr, true, arguments, cancellationToken),
                "sonarr.catalog.search" => await SearchCatalogAsync(config.Sonarr, true, arguments, cancellationToken),
                "sonarr.series.add" => await AddSeriesAsync(config.Sonarr, arguments, cancellationToken),
                _ => LunaTaskExecutionResult.Fail("unsupported_action", "Snacks does not support that action."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException)
        {
            return LunaTaskExecutionResult.Fail("invalid_response", "The local media service returned invalid data.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Local Arr request for Luna failed ({Action})", action);
            return LunaTaskExecutionResult.Fail("service_unreachable", "Snacks could not reach the local media service.");
        }
        catch (TaskCanceledException)
        {
            return LunaTaskExecutionResult.Fail("service_timeout", "The local media service timed out.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Luna task execution failed ({Action})", action);
            return LunaTaskExecutionResult.Fail("execution_failed", "Snacks could not complete the local task.");
        }
    }

    /******************************************************************
     *  Library Reads & Catalog Search
     ******************************************************************/

    /// <summary>
    ///     Returns one sanitized page of the movie/series library with offset-based
    ///     continuation, plus a privacy note describing what was withheld.
    /// </summary>
    private async Task<LunaTaskExecutionResult> ReadLibraryAsync(
        ArrIntegration arr,
        bool isSeries,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var offset = Math.Max(0, GetInt(arguments, "offset") ?? 0);
        var limit = Math.Clamp(GetInt(arguments, "limit") ?? 100, 1, MaxLibraryPageSize);
        var endpoint = isSeries ? "/api/v3/series" : "/api/v3/movie";
        var response = await SendArrAsync(arr, HttpMethod.Get, endpoint, null, cancellationToken);
        if (!response.Succeeded)
            return ArrFailure(response, isSeries ? "Sonarr" : "Radarr", "read the library");
        if (response.Body is not { ValueKind: JsonValueKind.Array } body)
            return LunaTaskExecutionResult.Fail("invalid_response", "The local media service returned an unexpected library response.");

        var all = body.EnumerateArray().ToList();
        var page = all.Skip(offset).Take(limit)
            .Select(item => isSeries ? SanitizeSeries(item, includeOverview: false) : SanitizeMovie(item, includeOverview: false))
            .ToArray();
        var nextOffset = offset + page.Length < all.Count ? offset + page.Length : (int?)null;

        return LunaTaskExecutionResult.Ok(new
        {
            items = page,
            total = all.Count,
            offset,
            returned = page.Length,
            nextOffset,
            privacy = "Paths, file names, sizes, and service credentials were omitted by Snacks.",
        });
    }

    /// <summary> Runs a bounded Arr catalog lookup and returns sanitized candidates with overviews. </summary>
    private async Task<LunaTaskExecutionResult> SearchCatalogAsync(
        ArrIntegration arr,
        bool isSeries,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = GetString(arguments, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            return LunaTaskExecutionResult.Fail("invalid_arguments", "A query of at most 200 characters is required.");

        var endpoint = (isSeries ? "/api/v3/series/lookup?term=" : "/api/v3/movie/lookup?term=")
            + Uri.EscapeDataString(query);
        var response = await SendArrAsync(arr, HttpMethod.Get, endpoint, null, cancellationToken);
        if (!response.Succeeded)
            return ArrFailure(response, isSeries ? "Sonarr" : "Radarr", "search the catalog");
        if (response.Body is not { ValueKind: JsonValueKind.Array } body)
            return LunaTaskExecutionResult.Fail("invalid_response", "The local media service returned an unexpected search response.");

        var results = body.EnumerateArray().Take(MaxSearchResults)
            .Select(item => isSeries ? SanitizeSeries(item, includeOverview: true) : SanitizeMovie(item, includeOverview: true))
            .ToArray();
        return LunaTaskExecutionResult.Ok(new { items = results, returned = results.Length });
    }

    /******************************************************************
     *  Idempotent Adds
     ******************************************************************/

    /// <summary>
    ///     Adds a movie to Radarr by TMDb id. Idempotent across lease retries: an
    ///     existing match returns <c>alreadyExists</c> both before the add and after
    ///     a failed post (in case Radarr committed but the completion was lost).
    /// </summary>
    private async Task<LunaTaskExecutionResult> AddMovieAsync(
        ArrIntegration arr,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var tmdbId = GetInt(arguments, "tmdbId") ?? 0;
        if (tmdbId <= 0)
            return LunaTaskExecutionResult.Fail("invalid_arguments", "A positive tmdbId is required.");

        var existing = await FindExistingAsync(arr, $"/api/v3/movie?tmdbId={tmdbId}", cancellationToken);
        if (existing is { } found)
            return LunaTaskExecutionResult.Ok(new { alreadyExists = true, item = SanitizeMovie(found, false) });

        var lookup = await SendArrAsync(
            arr,
            HttpMethod.Get,
            $"/api/v3/movie/lookup/tmdb?tmdbId={tmdbId}",
            null,
            cancellationToken);
        if (!lookup.Succeeded || lookup.Body is not { ValueKind: JsonValueKind.Object } movie)
            return ArrFailure(lookup, "Radarr", "look up that movie");

        var selection = await ResolveAddSelectionAsync(arr, arguments, cancellationToken);
        if (!selection.Succeeded) return selection.Error!;

        var payload = JsonNode.Parse(movie.GetRawText())!.AsObject();
        PrepareAddPayload(payload, selection, arguments);
        payload["monitored"] = GetBool(arguments, "monitored") ?? true;
        payload["addOptions"] = new JsonObject
        {
            ["searchForMovie"] = GetBool(arguments, "searchForMovie") ?? true,
            ["monitor"] = "movieOnly",
        };

        var added = await SendArrAsync(arr, HttpMethod.Post, "/api/v3/movie", payload, cancellationToken);
        if (!added.Succeeded)
        {
            // A retried lease may race after Radarr committed but before Luna
            // accepted our completion. Re-read by stable external id before
            // reporting failure so the operation is effectively idempotent.
            existing = await FindExistingAsync(arr, $"/api/v3/movie?tmdbId={tmdbId}", cancellationToken);
            if (existing is { } raced)
                return LunaTaskExecutionResult.Ok(new { alreadyExists = true, item = SanitizeMovie(raced, false) });
            return ArrFailure(added, "Radarr", "add that movie");
        }

        return LunaTaskExecutionResult.Ok(new
        {
            alreadyExists = false,
            item = SanitizeMovie(added.Body!.Value, false),
            qualityProfile = selection.QualityProfileName,
        });
    }

    /// <summary>
    ///     Adds a series to Sonarr by TVDb id with a validated monitor mode. Same
    ///     idempotency contract as <see cref="AddMovieAsync"/>.
    /// </summary>
    private async Task<LunaTaskExecutionResult> AddSeriesAsync(
        ArrIntegration arr,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var tvdbId = GetInt(arguments, "tvdbId") ?? 0;
        if (tvdbId <= 0)
            return LunaTaskExecutionResult.Fail("invalid_arguments", "A positive tvdbId is required.");

        var existing = await FindExistingAsync(arr, $"/api/v3/series?tvdbId={tvdbId}", cancellationToken);
        if (existing is { } found)
            return LunaTaskExecutionResult.Ok(new { alreadyExists = true, item = SanitizeSeries(found, false) });

        var lookup = await SendArrAsync(
            arr,
            HttpMethod.Get,
            "/api/v3/series/lookup?term=" + Uri.EscapeDataString($"tvdb:{tvdbId}"),
            null,
            cancellationToken);
        if (!lookup.Succeeded || lookup.Body is not { ValueKind: JsonValueKind.Array } candidates)
            return ArrFailure(lookup, "Sonarr", "look up that series");

        var series = candidates.EnumerateArray()
            .FirstOrDefault(item => GetInt(item, "tvdbId") == tvdbId);
        if (series.ValueKind != JsonValueKind.Object)
            return LunaTaskExecutionResult.Fail("not_found", "Sonarr could not find that TVDb series.");

        var selection = await ResolveAddSelectionAsync(arr, arguments, cancellationToken);
        if (!selection.Succeeded) return selection.Error!;

        var requestedMonitor = GetString(arguments, "monitor") ?? "all";
        var allowedMonitorValues = new[]
        {
            "all", "future", "missing", "existing", "firstSeason", "lastSeason",
            "latestSeason", "pilot", "recent", "monitorSpecials", "unmonitorSpecials", "none"
        };
        var monitor = allowedMonitorValues.FirstOrDefault(
            value => string.Equals(value, requestedMonitor, StringComparison.OrdinalIgnoreCase));
        if (monitor is null)
            return LunaTaskExecutionResult.Fail("invalid_arguments", "The requested Sonarr monitor mode is not supported.");

        var payload = JsonNode.Parse(series.GetRawText())!.AsObject();
        PrepareAddPayload(payload, selection, arguments);
        payload["monitored"] = GetBool(arguments, "monitored") ?? true;
        payload["seasonFolder"] = GetBool(arguments, "seasonFolder") ?? true;
        payload["addOptions"] = new JsonObject
        {
            ["monitor"] = monitor,
            ["searchForMissingEpisodes"] = GetBool(arguments, "searchForMissingEpisodes") ?? true,
            ["searchForCutoffUnmetEpisodes"] = false,
        };

        var added = await SendArrAsync(arr, HttpMethod.Post, "/api/v3/series", payload, cancellationToken);
        if (!added.Succeeded)
        {
            existing = await FindExistingAsync(arr, $"/api/v3/series?tvdbId={tvdbId}", cancellationToken);
            if (existing is { } raced)
                return LunaTaskExecutionResult.Ok(new { alreadyExists = true, item = SanitizeSeries(raced, false) });
            return ArrFailure(added, "Sonarr", "add that series");
        }

        return LunaTaskExecutionResult.Ok(new
        {
            alreadyExists = false,
            item = SanitizeSeries(added.Body!.Value, false),
            qualityProfile = selection.QualityProfileName,
        });
    }

    /// <summary>
    ///     Chooses the root folder and quality profile for an add — the requested id
    ///     when it exists locally, otherwise the first accessible entry — without ever
    ///     letting the task supply a path.
    /// </summary>
    private async Task<AddSelection> ResolveAddSelectionAsync(
        ArrIntegration arr,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var roots = await SendArrAsync(arr, HttpMethod.Get, "/api/v3/rootfolder", null, cancellationToken);
        if (!roots.Succeeded || roots.Body is not { ValueKind: JsonValueKind.Array } rootArray)
            return AddSelection.Fail(ArrFailure(roots, "The media service", "load root-folder settings"));
        var requestedRootId = GetInt(arguments, "rootFolderId");
        var root = SelectById(rootArray, requestedRootId, requireAccessible: true);
        var rootPath = root is { } r ? GetString(r, "path") : null;
        if (string.IsNullOrWhiteSpace(rootPath))
            return AddSelection.Fail(LunaTaskExecutionResult.Fail(
                "configuration_required",
                requestedRootId is null
                    ? "No accessible root folder is configured in the local media service."
                    : "The selected root folder is not available."));

        var profiles = await SendArrAsync(arr, HttpMethod.Get, "/api/v3/qualityprofile", null, cancellationToken);
        if (!profiles.Succeeded || profiles.Body is not { ValueKind: JsonValueKind.Array } profileArray)
            return AddSelection.Fail(ArrFailure(profiles, "The media service", "load quality profiles"));
        var requestedProfileId = GetInt(arguments, "qualityProfileId");
        var profile = SelectById(profileArray, requestedProfileId, requireAccessible: false);
        var profileId = profile is { } p ? GetInt(p, "id") : null;
        if (profileId is null or <= 0)
            return AddSelection.Fail(LunaTaskExecutionResult.Fail(
                "configuration_required",
                requestedProfileId is null
                    ? "No quality profile is configured in the local media service."
                    : "The selected quality profile is not available."));

        return new AddSelection(
            true,
            rootPath,
            profileId.Value,
            profile is { } selected ? GetString(selected, "name") : null,
            null);
    }

    /// <summary> Strips lookup-only fields from the Arr payload and applies the locally resolved root/profile. </summary>
    private static void PrepareAddPayload(JsonObject payload, AddSelection selection, JsonElement arguments)
    {
        payload.Remove("id");
        payload.Remove("path");
        payload.Remove("movieFile");
        payload.Remove("statistics");
        payload["rootFolderPath"] = selection.RootFolderPath;
        payload["qualityProfileId"] = selection.QualityProfileId;
    }

    /// <summary> Looks up an existing library entry by stable external id; null when absent or on any failure. </summary>
    private async Task<JsonElement?> FindExistingAsync(
        ArrIntegration arr,
        string endpoint,
        CancellationToken cancellationToken)
    {
        var response = await SendArrAsync(arr, HttpMethod.Get, endpoint, null, cancellationToken);
        if (!response.Succeeded || response.Body is not { ValueKind: JsonValueKind.Array } body)
            return null;
        var first = body.EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? first.Clone() : null;
    }

    /******************************************************************
     *  Local Arr HTTP
     ******************************************************************/

    /// <summary>
    ///     Sends one authenticated request to the local Arr API with a bounded
    ///     response read. A status code of 0 in the result means the integration
    ///     itself is missing/disabled rather than an HTTP failure.
    /// </summary>
    private async Task<ArrResponse> SendArrAsync(
        ArrIntegration arr,
        HttpMethod method,
        string endpoint,
        JsonNode? body,
        CancellationToken cancellationToken)
    {
        if (!arr.Enabled || string.IsNullOrWhiteSpace(arr.BaseUrl) || string.IsNullOrWhiteSpace(arr.ApiKey))
            return new ArrResponse(false, null, 0);

        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(method, arr.BaseUrl.TrimEnd('/') + endpoint);
        request.Headers.TryAddWithoutValidation("X-Api-Key", arr.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > MaxArrResponseBytes)
            return new ArrResponse(false, null, (int)response.StatusCode);
        var bytes = await ReadBoundedAsync(response.Content, MaxArrResponseBytes, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Local Arr request failed: {Method} {Endpoint} returned HTTP {Status}",
                method, endpoint.Split('?')[0], (int)response.StatusCode);
            return new ArrResponse(false, null, (int)response.StatusCode);
        }

        if (bytes.Length == 0)
            return new ArrResponse(true, JsonSerializer.SerializeToElement(new { }), (int)response.StatusCode);
        using var document = JsonDocument.Parse(bytes);
        return new ArrResponse(true, document.RootElement.Clone(), (int)response.StatusCode);
    }

    /// <summary> Streams a response body up to <paramref name="maxBytes"/>, throwing instead of buffering past the cap. </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new HttpRequestException("Local media service response exceeded the safe size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    /// <summary> Maps a failed Arr response to a typed error that names the service but withholds paths and payloads. </summary>
    private static LunaTaskExecutionResult ArrFailure(ArrResponse response, string service, string operation)
    {
        if (response.StatusCode == 0)
            return LunaTaskExecutionResult.Fail("configuration_required", $"{service} is not fully configured in Snacks.");
        return LunaTaskExecutionResult.Fail(
            $"arr_http_{response.StatusCode}",
            $"{service} could not {operation} (HTTP {response.StatusCode}). Local paths and response details were withheld.");
    }

    /******************************************************************
     *  Privacy Sanitizers & JSON Helpers
     ******************************************************************/

    /// <summary>
    ///     Rebuilds a movie as an explicit allow-list of public metadata — anything
    ///     not named here (paths, files, sizes) can never leak by omission.
    /// </summary>
    private static object SanitizeMovie(JsonElement movie, bool includeOverview)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = GetInt(movie, "id"),
            ["title"] = GetString(movie, "title"),
            ["originalTitle"] = GetString(movie, "originalTitle"),
            ["year"] = GetInt(movie, "year"),
            ["tmdbId"] = GetInt(movie, "tmdbId"),
            ["imdbId"] = GetString(movie, "imdbId"),
            ["genres"] = GetStrings(movie, "genres"),
            ["studio"] = GetString(movie, "studio"),
            ["certification"] = GetString(movie, "certification"),
            ["runtimeMinutes"] = GetInt(movie, "runtime"),
            ["status"] = GetString(movie, "status"),
            ["monitored"] = GetBool(movie, "monitored"),
            ["hasFile"] = GetBool(movie, "hasFile"),
            ["ratings"] = SanitizeRatings(movie),
        };
        if (includeOverview) result["overview"] = Truncate(GetString(movie, "overview"), 1_200);
        return result;
    }

    /// <summary> Series counterpart of <see cref="SanitizeMovie"/>, adding season/episode counts from statistics. </summary>
    private static object SanitizeSeries(JsonElement series, bool includeOverview)
    {
        var statistics = TryGetObject(series, "statistics");
        var result = new Dictionary<string, object?>
        {
            ["id"] = GetInt(series, "id"),
            ["title"] = GetString(series, "title"),
            ["year"] = GetInt(series, "year"),
            ["tvdbId"] = GetInt(series, "tvdbId"),
            ["tmdbId"] = GetInt(series, "tmdbId"),
            ["imdbId"] = GetString(series, "imdbId"),
            ["genres"] = GetStrings(series, "genres"),
            ["network"] = GetString(series, "network"),
            ["certification"] = GetString(series, "certification"),
            ["runtimeMinutes"] = GetInt(series, "runtime"),
            ["status"] = GetString(series, "status"),
            ["seriesType"] = GetString(series, "seriesType"),
            ["monitored"] = GetBool(series, "monitored"),
            ["seasonCount"] = statistics is { } s ? GetInt(s, "seasonCount") : null,
            ["episodeFileCount"] = statistics is { } s2 ? GetInt(s2, "episodeFileCount") : null,
            ["totalEpisodeCount"] = statistics is { } s3 ? GetInt(s3, "totalEpisodeCount") : null,
            ["ratings"] = SanitizeRatings(series),
        };
        if (includeOverview) result["overview"] = Truncate(GetString(series, "overview"), 1_200);
        return result;
    }

    /// <summary> Flattens known rating sources to plain numbers, accepting both bare and {value} shapes. </summary>
    private static IReadOnlyDictionary<string, double> SanitizeRatings(JsonElement item)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        var ratings = TryGetObject(item, "ratings");
        if (ratings is null) return result;
        foreach (var source in new[] { "imdb", "tmdb", "trakt", "rottenTomatoes", "value" })
        {
            if (!ratings.Value.TryGetProperty(source, out var rating)) continue;
            if (rating.ValueKind == JsonValueKind.Number && rating.TryGetDouble(out var direct))
                result[source] = direct;
            else if (rating.ValueKind == JsonValueKind.Object
                     && rating.TryGetProperty("value", out var value)
                     && value.TryGetDouble(out var nested))
                result[source] = nested;
        }
        return result;
    }

    /// <summary>
    ///     Picks the entry matching <paramref name="requestedId"/> (or the first
    ///     entry when the task supplied none), optionally skipping roots the Arr
    ///     instance reports as inaccessible.
    /// </summary>
    private static JsonElement? SelectById(JsonElement array, int? requestedId, bool requireAccessible)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (requestedId is not null && GetInt(item, "id") != requestedId) continue;
            if (requireAccessible && GetBool(item, "accessible") == false) continue;
            return item.Clone();
        }
        return null;
    }

    /// <summary> Adds the capability names one configured Arr instance earns under the current Luna permissions. </summary>
    private static void AddArrCapabilities(
        ICollection<string> capabilities,
        string prefix,
        ArrIntegration arr,
        LunaIntegration luna)
    {
        if (!arr.Enabled || string.IsNullOrWhiteSpace(arr.BaseUrl) || string.IsNullOrWhiteSpace(arr.ApiKey)) return;
        capabilities.Add(prefix + ".catalog.search");
        if (luna.AllowLibraryReads) capabilities.Add(prefix + ".library.read");
        if (luna.AllowLibraryChanges)
            capabilities.Add(prefix == "radarr" ? "radarr.movie.add" : "sonarr.series.add");
    }

    // Null-safe JSON property getters. Arr payloads vary by version, so every
    // read tolerates a missing property or unexpected value kind.

    private static JsonElement? TryGetObject(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? GetString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static bool? GetBool(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string[] GetStrings(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Take(20)
                .Select(x => x!)
                .ToArray()
            : Array.Empty<string>();

    /// <summary> Caps free-text fields (overviews) so a result stays a bounded payload. </summary>
    private static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];

    /// <summary> Outcome of one local Arr call; StatusCode 0 means the integration is missing/disabled. </summary>
    private sealed record ArrResponse(bool Succeeded, JsonElement? Body, int StatusCode);

    /// <summary> Locally resolved root folder and quality profile for an add, or the typed error to return. </summary>
    private sealed record AddSelection(
        bool Succeeded,
        string? RootFolderPath,
        int QualityProfileId,
        string? QualityProfileName,
        LunaTaskExecutionResult? Error)
    {
        /// <summary> Wraps a typed error as a failed selection. </summary>
        public static AddSelection Fail(LunaTaskExecutionResult error) =>
            new(false, null, 0, null, error);
    }
}
