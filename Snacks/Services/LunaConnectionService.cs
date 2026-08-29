using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

/// <summary> Safe, token-free snapshot of the Luna connector for status APIs and the UI. </summary>
/// <param name="Connected"> Whether a scoped refresh token is currently persisted. </param>
/// <param name="Enabled"> Whether the background task worker is allowed to poll. </param>
/// <param name="Online"> Whether a poll or registration succeeded recently enough to call the link live. </param>
/// <param name="BaseUrl"> The configured Luna service root. </param>
/// <param name="Email"> The account email reported by Luna at connect time. </param>
/// <param name="AgentId"> Luna's identifier for this registered connector. </param>
/// <param name="ConnectedAt"> When the current session was created (UTC). </param>
/// <param name="LastSuccessfulPoll"> Last successful Luna round-trip (UTC). </param>
/// <param name="LastError"> Sanitized message of the most recent failure, if any. </param>
/// <param name="Capabilities"> Action names currently advertised to Luna. </param>
public sealed record LunaConnectionStatus(
    bool Connected,
    bool Enabled,
    bool Online,
    string BaseUrl,
    string? Email,
    string? AgentId,
    DateTime? ConnectedAt,
    DateTime? LastSuccessfulPoll,
    string? LastError,
    IReadOnlyList<string> Capabilities);

/// <summary> Raised for expected connector failures whose message is safe to surface in the UI. </summary>
public sealed class LunaConnectionException : Exception
{
    public LunaConnectionException(string message) : base(message) { }
}

/// <summary>
///     Owns Snacks' narrow Luna session. Passwords exist only in the connect call;
///     only a remote-agent scoped refresh token is persisted. Access tokens remain
///     in memory and are renewed before expiry.
/// </summary>
public sealed class LunaConnectionService
{
    /// <summary> Config file holding the scoped session; written via <see cref="ConfigFileService.SaveSecret"/>. </summary>
    private const string StateFile = "luna-connection.json";

    /// <summary> The only Luna endpoint production Snacks connects to. </summary>
    public const string OfficialBaseUrl = "https://veryluna.com";

    /// <summary> Env var permitting non-official Luna URLs for the project owner's local integration testing. </summary>
    public const string AllowCustomUrlEnvironmentVariable = "SNACKS_LUNA_ALLOW_CUSTOM_URL";

    /// <summary> Env var permitting plain-HTTP Luna URLs beyond loopback (isolated test networks only). </summary>
    public const string AllowInsecureHttpEnvironmentVariable = "SNACKS_LUNA_ALLOW_INSECURE_HTTP";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConfigFileService  _configFiles;
    private readonly IntegrationService _integrations;
    private readonly LunaTaskExecutor   _executor;
    private readonly IHttpClientFactory _httpClientFactory;

    // Serialization guards. _stateLock protects every mutable field below;
    // _authGate ensures only one login/refresh credential exchange is in
    // flight; _operationGate serializes connect/disconnect/poll so a
    // disconnect can never interleave with a running task.
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    // Only _state (the scoped session) is persisted. The access token and the
    // poll/registration bookkeeping are in-memory only and reset on restart —
    // the token deliberately never touches disk.
    private LunaConnectionState _state;
    private string? _accessToken;
    private DateTime _accessExpiresAt;
    private DateTime? _lastSuccessfulPoll;
    private string? _lastError;
    private DateTime _lastRegistrationAt;
    private string? _registeredCapabilitiesSignature;
    private int _pollIntervalSeconds = 5;

    public LunaConnectionService(
        ConfigFileService configFiles,
        IntegrationService integrations,
        LunaTaskExecutor executor,
        IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(configFiles);
        ArgumentNullException.ThrowIfNull(integrations);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _configFiles       = configFiles;
        _integrations      = integrations;
        _executor          = executor;
        _httpClientFactory = httpClientFactory;
        _state             = NormalizeState(_configFiles.Load<LunaConnectionState>(StateFile));
    }

    /******************************************************************
     *  Status
     ******************************************************************/

    /// <summary>
    ///     Returns a token-free snapshot of connection health, identity, and the
    ///     currently advertised capabilities.
    /// </summary>
    public LunaConnectionStatus GetStatus()
    {
        LunaConnectionState state;
        DateTime? lastPoll;
        string? lastError;
        lock (_stateLock)
        {
            state = Clone(_state);
            lastPoll = _lastSuccessfulPoll;
            lastError = _lastError;
        }

        var config = _integrations.GetConfig().Luna;
        var capabilities = _executor.GetCapabilities();
        var connected = !string.IsNullOrWhiteSpace(state.RefreshToken);
        var online = connected && lastPoll >= DateTime.UtcNow - TimeSpan.FromSeconds(Math.Max(20, _pollIntervalSeconds * 4));
        return new LunaConnectionStatus(
            connected,
            config.Enabled,
            online,
            config.BaseUrl,
            state.Email,
            state.AgentId,
            state.ConnectedAt,
            lastPoll,
            lastError,
            capabilities);
    }

    /******************************************************************
     *  Connect / Disconnect
     ******************************************************************/

    /// <summary>
    ///     Exchanges the credentials for a daemon-scoped Luna session, persists only
    ///     the scoped refresh token, and registers the connector. The password is
    ///     sent to Luna once and never written to disk.
    /// </summary>
    /// <param name="baseUrl"> The Luna service root (the official URL outside local testing). </param>
    /// <param name="email"> The Luna account email. </param>
    /// <param name="password"> The Luna account password; used for this call only. </param>
    /// <param name="deviceName"> Optional connector display name; defaults to the machine name. </param>
    /// <param name="cancellationToken"> Cancels the sign-in and registration round-trips. </param>
    public async Task<LunaConnectionStatus> ConnectAsync(
        string baseUrl,
        string email,
        string password,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await ConnectCoreAsync(baseUrl, email, password, deviceName, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    ///     Connect body, run under <see cref="_operationGate"/>: logs in, persists
    ///     the new scoped session, registers the connector (best-effort — retried by
    ///     the background poll on failure), then revokes any previous session only
    ///     after its replacement is safely stored.
    /// </summary>
    private async Task<LunaConnectionStatus> ConnectCoreAsync(
        string baseUrl,
        string email,
        string password,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            throw new LunaConnectionException("Luna email and password are required.");

        await _authGate.WaitAsync(cancellationToken);
        string? oldRefresh = null;
        string? oldBaseUrl = null;
        try
        {
            lock (_stateLock)
            {
                oldRefresh = _state.RefreshToken;
                oldBaseUrl = _state.BaseUrl;
            }

            using var http = CreateHttp(TimeSpan.FromSeconds(20));
            using var response = await http.PostAsJsonAsync(
                Endpoint(baseUrl, "/api/v1/auth/agent-login"),
                new
                {
                    email,
                    password,
                    clientId = "snacks",
                    deviceName = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim(),
                },
                JsonOptions,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new LunaConnectionException(AuthenticationError(response));

            var auth = await ReadJsonAsync<AuthWireResponse>(response, cancellationToken)
                ?? throw new LunaConnectionException("Luna returned an invalid authentication response.");
            if (string.IsNullOrWhiteSpace(auth.AccessToken) || string.IsNullOrWhiteSpace(auth.RefreshToken))
                throw new LunaConnectionException("Luna did not return a usable connector session.");

            var now = DateTime.UtcNow;
            lock (_stateLock)
            {
                _state.BaseUrl = baseUrl;
                _state.Email = auth.User?.Email ?? email.Trim();
                _state.RefreshToken = auth.RefreshToken;
                _state.RefreshExpiresAt = auth.RefreshExpiresAt;
                _state.ConnectedAt = now;
                _state.AgentId = null;
                _accessToken = auth.AccessToken;
                _accessExpiresAt = now.AddSeconds(Math.Max(30, auth.ExpiresIn - 30));
                _lastError = null;
                _lastSuccessfulPoll = null;
                SaveStateLocked(mirrorBackup: true);
            }

            try
            {
                await RegisterWithAccessAsync(auth.AccessToken, baseUrl, deviceName, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (_stateLock) _lastError = SafeError(ex);
                Log.Warning(ex, "Luna authenticated, but connector registration will be retried in the background");
            }
        }
        finally
        {
            _authGate.Release();
        }

        // Reconnecting creates a new scoped session. Best-effort revoke the old
        // one only after the replacement is safely persisted and registered.
        if (!string.IsNullOrWhiteSpace(oldRefresh)
            && !string.Equals(oldRefresh, SnapshotState().RefreshToken, StringComparison.Ordinal))
            await TryRevokeAsync(oldBaseUrl, oldRefresh, cancellationToken);

        return GetStatus();
    }

    /// <summary>
    ///     Marks the connector offline, revokes the scoped refresh token, and clears
    ///     every persisted credential (including the on-disk backup).
    /// </summary>
    /// <param name="cancellationToken"> Cancels the offline/revocation round-trips. </param>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    ///     Disconnect body, run under <see cref="_operationGate"/>: best-effort marks
    ///     the connector offline in Luna, revokes the refresh token, then wipes all
    ///     persisted and in-memory session state. Local cleanup proceeds even when
    ///     the remote calls fail so revocation can never be blocked by an outage.
    /// </summary>
    private async Task DisconnectCoreAsync(CancellationToken cancellationToken)
    {
        var registered = SnapshotState();
        if (!string.IsNullOrWhiteSpace(registered.RefreshToken)
            && !string.IsNullOrWhiteSpace(registered.BaseUrl)
            && !string.IsNullOrWhiteSpace(registered.AgentId))
        {
            try
            {
                var accessToken = await EnsureAccessTokenAsync(cancellationToken);
                await TryDisableAgentAsync(
                    registered.BaseUrl,
                    registered.AgentId,
                    accessToken,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                Log.Warning(ex, "Could not mark the Luna connector offline before revocation");
            }
        }

        await _authGate.WaitAsync(cancellationToken);
        string? refresh;
        string? baseUrl;
        try
        {
            lock (_stateLock)
            {
                refresh = _state.RefreshToken;
                baseUrl = _state.BaseUrl;
            }

            if (!string.IsNullOrWhiteSpace(refresh))
                await TryRevokeAsync(baseUrl, refresh, cancellationToken);

            lock (_stateLock)
            {
                // Keep InstanceId stable so a later reconnect updates the same
                // connector record instead of leaving stale device entries.
                _state.AgentId = null;
                _state.Email = null;
                _state.BaseUrl = null;
                _state.RefreshToken = null;
                _state.RefreshExpiresAt = null;
                _state.ConnectedAt = null;
                _accessToken = null;
                _accessExpiresAt = default;
                _lastSuccessfulPoll = null;
                _lastError = null;
                _lastRegistrationAt = default;
                _registeredCapabilitiesSignature = null;
                SaveStateLocked(purgeBackup: true);
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    /******************************************************************
     *  Task Polling
     ******************************************************************/

    /// <summary> Polls and, when present, executes exactly one leased task. </summary>
    /// <param name="cancellationToken"> Cancels the lease poll and any in-flight task. </param>
    /// <returns> <see langword="true"/> when a task was leased and completed; otherwise <see langword="false"/>. </returns>
    public async Task<bool> PollOnceAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await PollOnceCoreAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    ///     Poll body, run under <see cref="_operationGate"/>: verifies the configured
    ///     URL still matches the connected one (a changed URL must never receive an
    ///     existing token), ensures a fresh access token and registration, then leases,
    ///     executes, and completes at most one task.
    /// </summary>
    private async Task<bool> PollOnceCoreAsync(CancellationToken cancellationToken)
    {
        var config = _integrations.GetConfig().Luna;
        var initialState = SnapshotState();
        if (!config.Enabled || string.IsNullOrWhiteSpace(initialState.RefreshToken)) return false;

        try
        {
            var configuredBaseUrl = NormalizeBaseUrl(config.BaseUrl);
            if (!string.Equals(configuredBaseUrl, initialState.BaseUrl, StringComparison.OrdinalIgnoreCase))
                throw new LunaConnectionException("The Luna URL changed. Connect again before polling so no token is sent to the wrong server.");

            var token = await EnsureAccessTokenAsync(cancellationToken);
            await EnsureRegisteredAsync(token, configuredBaseUrl, cancellationToken);
            var state = SnapshotState();
            if (string.IsNullOrWhiteSpace(state.AgentId)) return false;

            using var http = CreateHttp(TimeSpan.FromSeconds(20));
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                Endpoint(configuredBaseUrl, $"/api/v1/remote-agents/{Uri.EscapeDataString(state.AgentId)}/lease"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                InvalidateAccessToken();
                throw new LunaConnectionException("Luna rejected the connector access token; Snacks will refresh it.");
            }
            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                MarkPollSucceeded();
                return false;
            }
            if (!response.IsSuccessStatusCode)
                throw new LunaConnectionException($"Luna task polling returned HTTP {(int)response.StatusCode}.");

            var lease = await ReadJsonAsync<LeaseWireResponse>(response, cancellationToken)
                ?? throw new LunaConnectionException("Luna returned an invalid task lease.");
            var execution = await _executor.ExecuteAsync(lease.Action, lease.Arguments, cancellationToken);
            await CompleteAsync(configuredBaseUrl, state.AgentId, lease, execution, cancellationToken);
            MarkPollSucceeded();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_stateLock) _lastError = SafeError(ex);
            throw;
        }
    }

    /// <summary> Server-suggested lease-poll cadence, clamped to a sane range. </summary>
    public int PollIntervalSeconds => Math.Clamp(_pollIntervalSeconds, 2, 30);

    /******************************************************************
     *  Base-URL Policy
     ******************************************************************/

    /// <summary>
    ///     Validates and canonicalizes a Luna service URL. Anything other than the
    ///     official URL is rejected unless the explicit local-testing overrides are
    ///     set, and plain HTTP is limited to loopback/dev-override scenarios.
    /// </summary>
    /// <param name="baseUrl"> The Luna service URL to validate. </param>
    /// <returns> The canonical base URL with any trailing slash removed. </returns>
    internal static string NormalizeBaseUrl(string baseUrl)
    {
        baseUrl = baseUrl?.Trim().TrimEnd('/') ?? string.Empty;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new LunaConnectionException("Enter a valid Luna server URL.");

        var official = new Uri(OfficialBaseUrl);
        var isOfficial = Uri.Compare(
            uri,
            official,
            UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.SafeUnescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
        if (isOfficial) return OfficialBaseUrl;

        if (!CustomUrlAllowed)
            throw new LunaConnectionException(
                $"Snacks connects to Luna only at {OfficialBaseUrl}. Custom URLs are available only when {AllowCustomUrlEnvironmentVariable}=true is explicitly set for local testing.");

        var loopback = uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
        var explicitDevOverride = IsTruthy(Environment.GetEnvironmentVariable(AllowInsecureHttpEnvironmentVariable));
        if (uri.Scheme != Uri.UriSchemeHttps
            && !(uri.Scheme == Uri.UriSchemeHttp && (loopback || explicitDevOverride)))
            throw new LunaConnectionException(
                $"Luna must use HTTPS (plain HTTP is allowed only for loopback development, or when {AllowInsecureHttpEnvironmentVariable}=true is explicitly set).");
        return baseUrl;
    }

    /// <summary> Whether the local-testing override permitting custom Luna URLs is set. </summary>
    public static bool CustomUrlAllowed =>
        IsTruthy(Environment.GetEnvironmentVariable(AllowCustomUrlEnvironmentVariable));

    /// <summary> Common truthy spellings accepted for the override env vars. </summary>
    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    /******************************************************************
     *  Tokens & Registration
     ******************************************************************/

    /// <summary>
    ///     Returns a valid in-memory access token, refreshing it via the persisted
    ///     scoped refresh token when missing or within 30s of expiry. Double-checked
    ///     under <see cref="_authGate"/> so concurrent callers trigger one refresh.
    /// </summary>
    private async Task<string> EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(_accessToken)
                && _accessExpiresAt > DateTime.UtcNow.AddSeconds(30))
                return _accessToken;
        }

        await _authGate.WaitAsync(cancellationToken);
        try
        {
            lock (_stateLock)
            {
                if (!string.IsNullOrWhiteSpace(_accessToken)
                    && _accessExpiresAt > DateTime.UtcNow.AddSeconds(30))
                    return _accessToken;
            }

            var state = SnapshotState();
            if (string.IsNullOrWhiteSpace(state.RefreshToken) || string.IsNullOrWhiteSpace(state.BaseUrl))
                throw new LunaConnectionException("Connect Snacks to Luna again.");

            using var http = CreateHttp(TimeSpan.FromSeconds(20));
            using var response = await http.PostAsJsonAsync(
                Endpoint(state.BaseUrl, "/api/v1/auth/refresh"),
                new
                {
                    refreshToken = state.RefreshToken,
                    clientId = "snacks",
                    deviceName = Environment.MachineName,
                },
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new LunaConnectionException(
                    response.StatusCode == HttpStatusCode.Unauthorized
                        ? "The Luna connection expired or was revoked. Connect again."
                        : $"Luna token refresh returned HTTP {(int)response.StatusCode}.");

            var auth = await ReadJsonAsync<AuthWireResponse>(response, cancellationToken)
                ?? throw new LunaConnectionException("Luna returned an invalid token refresh response.");
            var now = DateTime.UtcNow;
            lock (_stateLock)
            {
                // Current Luna daemon tokens are stable and sliding. Persist the
                // returned value anyway for forward/backward compatibility with
                // servers that rotate refresh credentials.
                _state.RefreshToken = auth.RefreshToken;
                _state.RefreshExpiresAt = auth.RefreshExpiresAt;
                _accessToken = auth.AccessToken;
                _accessExpiresAt = now.AddSeconds(Math.Max(30, auth.ExpiresIn - 30));
                SaveStateLocked(mirrorBackup: true);
                return _accessToken;
            }
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>
    ///     Re-registers the connector when it has no agent id yet, the advertised
    ///     capability set changed, or the last registration is over a minute old —
    ///     Luna treats periodic registration as the connector's presence heartbeat.
    /// </summary>
    private async Task EnsureRegisteredAsync(
        string accessToken,
        string configuredBaseUrl,
        CancellationToken cancellationToken)
    {
        var signature = string.Join('\n', _executor.GetCapabilities().OrderBy(x => x, StringComparer.Ordinal));
        var state = SnapshotState();
        if (!string.IsNullOrWhiteSpace(state.AgentId)
            && _lastRegistrationAt > DateTime.UtcNow - TimeSpan.FromMinutes(1)
            && string.Equals(signature, _registeredCapabilitiesSignature, StringComparison.Ordinal))
            return;

        await RegisterWithAccessAsync(accessToken, configuredBaseUrl, null, cancellationToken);
    }

    /// <summary>
    ///     Registers (or re-registers) this instance as a remote agent under its
    ///     stable <see cref="LunaConnectionState.InstanceId"/> and records the
    ///     agent id and poll cadence Luna assigns.
    /// </summary>
    private async Task RegisterWithAccessAsync(
        string accessToken,
        string baseUrl,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        var state = SnapshotState();
        var capabilities = _executor.GetCapabilities().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        using var http = CreateHttp(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(baseUrl, "/api/v1/remote-agents/register"))
        {
            Content = JsonContent.Create(new
            {
                instanceId = state.InstanceId,
                name = string.IsNullOrWhiteSpace(deviceName) ? Environment.MachineName : deviceName.Trim(),
                clientType = "snacks",
                clientVersion = AppVersion.Current,
                capabilities,
            }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            InvalidateAccessToken();
        if (!response.IsSuccessStatusCode)
            throw new LunaConnectionException($"Luna connector registration returned HTTP {(int)response.StatusCode}.");

        var registration = await ReadJsonAsync<RegisterWireResponse>(response, cancellationToken)
            ?? throw new LunaConnectionException("Luna returned an invalid connector registration.");
        var registeredAt = DateTime.UtcNow;
        lock (_stateLock)
        {
            _state.AgentId = registration.AgentId;
            _state.BaseUrl = baseUrl;
            _pollIntervalSeconds = Math.Clamp(registration.PollIntervalSeconds, 2, 30);
            _lastRegistrationAt = registeredAt;
            _registeredCapabilitiesSignature = string.Join('\n', capabilities);
            // Registration is an authenticated round-trip to Luna and therefore
            // proves the connector is online immediately. Do not make the UI wait
            // for the background lease poll before showing Connected.
            _lastSuccessfulPoll = registeredAt;
            _lastError = null;
            SaveStateLocked();
        }
    }

    /******************************************************************
     *  Task Completion & Session Cleanup
     ******************************************************************/

    /// <summary>
    ///     Reports a task's sanitized result back to Luna under its lease token.
    ///     A 409 means the lease expired and was re-issued elsewhere — logged and
    ///     swallowed, since the retry will complete it.
    /// </summary>
    private async Task CompleteAsync(
        string baseUrl,
        string agentId,
        LeaseWireResponse lease,
        LunaTaskExecutionResult execution,
        CancellationToken cancellationToken)
    {
        var token = await EnsureAccessTokenAsync(cancellationToken);
        using var http = CreateHttp(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            Endpoint(baseUrl,
                $"/api/v1/remote-agents/{Uri.EscapeDataString(agentId)}/tasks/{Uri.EscapeDataString(lease.Id)}/complete"))
        {
            Content = JsonContent.Create(new
            {
                leaseToken = lease.LeaseToken,
                succeeded = execution.Succeeded,
                result = execution.Result,
                errorCode = execution.ErrorCode,
                errorMessage = execution.ErrorMessage,
            }, options: JsonOptions),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            InvalidateAccessToken();
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            Log.Warning("Luna rejected completion for task {TaskId} because its lease changed", lease.Id);
            return;
        }
        if (!response.IsSuccessStatusCode)
            throw new LunaConnectionException($"Luna task completion returned HTTP {(int)response.StatusCode}.");
    }

    /// <summary> Best-effort server-side revocation of a refresh token; failures are logged, never thrown. </summary>
    private async Task TryRevokeAsync(
        string? baseUrl,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return;
        try
        {
            using var http = CreateHttp(TimeSpan.FromSeconds(10));
            using var response = await http.PostAsJsonAsync(
                Endpoint(baseUrl, "/api/v1/auth/logout"),
                new { refreshToken },
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
                Log.Warning("Luna connector token revocation returned HTTP {Status}", (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "Could not revoke the old Luna connector token");
        }
    }

    /// <summary> Best-effort deregistration so Luna shows the connector offline immediately after disconnect. </summary>
    private async Task TryDisableAgentAsync(
        string baseUrl,
        string agentId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var http = CreateHttp(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            Endpoint(baseUrl, $"/api/v1/remote-agents/{Uri.EscapeDataString(agentId)}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            InvalidateAccessToken();
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            Log.Warning("Marking the Luna connector offline returned HTTP {Status}", (int)response.StatusCode);
    }

    /******************************************************************
     *  State Persistence
     ******************************************************************/

    /// <summary> Records a successful Luna round-trip; drives the Online flag and clears the last error. </summary>
    private void MarkPollSucceeded()
    {
        lock (_stateLock)
        {
            _lastSuccessfulPoll = DateTime.UtcNow;
            _lastError = null;
        }
    }

    /// <summary> Drops the in-memory access token after a 401 so the next call refreshes it. </summary>
    private void InvalidateAccessToken()
    {
        lock (_stateLock)
        {
            _accessToken = null;
            _accessExpiresAt = default;
        }
    }

    /// <summary> Returns a defensive copy of the session state for use outside <see cref="_stateLock"/>. </summary>
    private LunaConnectionState SnapshotState()
    {
        lock (_stateLock) return Clone(_state);
    }

    /// <summary>
    ///     Persists the session state (owner-only on Unix). Must be called while
    ///     holding <see cref="_stateLock"/>.
    /// </summary>
    /// <param name="mirrorBackup"> Copies the fresh file over the <c>.bak</c> so both hold the new token. </param>
    /// <param name="purgeBackup"> Deletes the <c>.bak</c> so no revoked token survives a disconnect. </param>
    private void SaveStateLocked(bool mirrorBackup = false, bool purgeBackup = false)
    {
        _configFiles.SaveSecret(StateFile, _state);
        var path = _configFiles.GetConfigPath(StateFile);
        var backup = path + ".bak";
        if (purgeBackup)
        {
            try { if (File.Exists(backup)) File.Delete(backup); }
            catch (Exception ex) { Log.Warning(ex, "Could not remove the revoked Luna connector backup"); }
        }
        else if (mirrorBackup)
        {
            try { File.Copy(path, backup, overwrite: true); }
            catch (Exception ex) { Log.Warning(ex, "Could not mirror Luna connector state backup"); }
        }
        ProtectSecretFile(path);
        ProtectSecretFile(backup);
    }

    /// <summary> Tightens a credential file to owner read/write on Unix; no-op on Windows or when the file is absent. </summary>
    private static void ProtectSecretFile(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not restrict Luna connector state permissions ({Path})", path);
        }
    }

    /// <summary> Field-by-field copy so snapshots can't observe mid-update state. </summary>
    private static LunaConnectionState Clone(LunaConnectionState state) => new()
    {
        InstanceId = state.InstanceId,
        AgentId = state.AgentId,
        Email = state.Email,
        BaseUrl = state.BaseUrl,
        RefreshToken = state.RefreshToken,
        RefreshExpiresAt = state.RefreshExpiresAt,
        ConnectedAt = state.ConnectedAt,
    };

    /// <summary> Backfills the stable per-install InstanceId on first load or after a corrupted file. </summary>
    private static LunaConnectionState NormalizeState(LunaConnectionState state)
    {
        if (string.IsNullOrWhiteSpace(state.InstanceId))
            state.InstanceId = Guid.NewGuid().ToString("N");
        return state;
    }

    /******************************************************************
     *  HTTP Helpers & Wire Models
     ******************************************************************/

    /// <summary> Creates the named "LunaConnector" client (redirects disabled in Program.cs) with a per-call timeout. </summary>
    private HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = _httpClientFactory.CreateClient("LunaConnector");
        http.Timeout = timeout;
        return http;
    }

    /// <summary> Joins a path onto a base URL, re-validating the URL so no request can bypass the policy. </summary>
    private static string Endpoint(string baseUrl, string path) =>
        NormalizeBaseUrl(baseUrl) + "/" + path.TrimStart('/');

    /// <summary> Deserializes a Luna response body with the shared web-defaults options. </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);

    /// <summary> Maps login failure statuses to user-facing messages that never echo server details. </summary>
    private static string AuthenticationError(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return "Luna rejected that email or password.";
        if (response.StatusCode == HttpStatusCode.Forbidden) return "Luna requires account or email confirmation before connecting.";
        if ((int)response.StatusCode == 429) return "The Luna account is temporarily locked. Try again later.";
        return $"Luna login returned HTTP {(int)response.StatusCode}.";
    }

    /// <summary>
    ///     Reduces an exception to a message safe to store and show in the UI —
    ///     only <see cref="LunaConnectionException"/> messages pass through verbatim.
    /// </summary>
    private static string SafeError(Exception exception) => exception switch
    {
        LunaConnectionException => exception.Message,
        HttpRequestException => "Snacks could not reach Luna.",
        TaskCanceledException => "The Luna request timed out.",
        _ => "The Luna connector encountered an unexpected error.",
    };

    /// <summary> Luna's login/refresh response: a short-lived access token plus the scoped refresh token. </summary>
    private sealed record AuthWireResponse(
        string AccessToken,
        int ExpiresIn,
        string RefreshToken,
        DateTime RefreshExpiresAt,
        AuthUserWire? User);

    /// <summary> Account subobject of <see cref="AuthWireResponse"/>. </summary>
    private sealed record AuthUserWire(string? Email);

    /// <summary> Luna's registration response: the assigned agent id and suggested poll cadence. </summary>
    private sealed record RegisterWireResponse(string AgentId, int PollIntervalSeconds);

    /// <summary> One leased task: the action to run, its arguments, and the lease token to complete under. </summary>
    private sealed record LeaseWireResponse(
        string Id,
        string LeaseToken,
        string Action,
        JsonElement Arguments,
        DateTime LeaseExpiresAt,
        int Attempt);
}

/// <summary>
///     Background loop that drives <see cref="LunaConnectionService.PollOnceAsync"/>:
///     fast follow-up after a handled task, the server-suggested cadence when idle,
///     and exponential backoff (capped at 60s) after failures.
/// </summary>
public sealed class LunaTaskWorker : BackgroundService
{
    private readonly LunaConnectionService _connection;
    private readonly ILogger<LunaTaskWorker> _logger;

    public LunaTaskWorker(LunaConnectionService connection, ILogger<LunaTaskWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _logger     = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(10);
            try
            {
                var handledTask = await _connection.PollOnceAsync(stoppingToken);
                failures = 0;
                delay = handledTask
                    ? TimeSpan.FromMilliseconds(100)
                    : TimeSpan.FromSeconds(_connection.PollIntervalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 5))));
                _logger.LogWarning(ex, "Luna task polling failed; retrying in {DelaySeconds:n0}s", delay.TotalSeconds);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
