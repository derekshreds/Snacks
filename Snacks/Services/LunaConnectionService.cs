using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Snacks.Models;

namespace Snacks.Services;

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

public sealed class LunaConnectionException(string message) : Exception(message);

/// <summary>
/// Owns Snacks' narrow Luna session. Passwords exist only in the connect call;
/// only a remote-agent scoped refresh token is persisted. Access tokens remain
/// in memory and are renewed before expiry.
/// </summary>
public sealed class LunaConnectionService(
    ConfigFileService configFiles,
    IntegrationService integrations,
    LunaTaskExecutor executor,
    IHttpClientFactory httpClientFactory)
{
    private const string StateFile = "luna-connection.json";
    public const string OfficialBaseUrl = "https://veryluna.com";
    public const string AllowCustomUrlEnvironmentVariable = "SNACKS_LUNA_ALLOW_CUSTOM_URL";
    public const string AllowInsecureHttpEnvironmentVariable = "SNACKS_LUNA_ALLOW_INSECURE_HTTP";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private LunaConnectionState _state = NormalizeState(configFiles.Load<LunaConnectionState>(StateFile));
    private string? _accessToken;
    private DateTime _accessExpiresAt;
    private DateTime? _lastSuccessfulPoll;
    private string? _lastError;
    private DateTime _lastRegistrationAt;
    private string? _registeredCapabilitiesSignature;
    private int _pollIntervalSeconds = 5;

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

        var config = integrations.GetConfig().Luna;
        var capabilities = executor.GetCapabilities();
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

    /// <summary>Polls and, when present, executes exactly one leased task.</summary>
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

    private async Task<bool> PollOnceCoreAsync(CancellationToken cancellationToken)
    {
        var config = integrations.GetConfig().Luna;
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
            var execution = await executor.ExecuteAsync(lease.Action, lease.Arguments, cancellationToken);
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

    public int PollIntervalSeconds => Math.Clamp(_pollIntervalSeconds, 2, 30);

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

    public static bool CustomUrlAllowed =>
        IsTruthy(Environment.GetEnvironmentVariable(AllowCustomUrlEnvironmentVariable));

    private static bool IsTruthy(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

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

    private async Task EnsureRegisteredAsync(
        string accessToken,
        string configuredBaseUrl,
        CancellationToken cancellationToken)
    {
        var signature = string.Join('\n', executor.GetCapabilities().OrderBy(x => x, StringComparer.Ordinal));
        var state = SnapshotState();
        if (!string.IsNullOrWhiteSpace(state.AgentId)
            && _lastRegistrationAt > DateTime.UtcNow - TimeSpan.FromMinutes(1)
            && string.Equals(signature, _registeredCapabilitiesSignature, StringComparison.Ordinal))
            return;

        await RegisterWithAccessAsync(accessToken, configuredBaseUrl, null, cancellationToken);
    }

    private async Task RegisterWithAccessAsync(
        string accessToken,
        string baseUrl,
        string? deviceName,
        CancellationToken cancellationToken)
    {
        baseUrl = NormalizeBaseUrl(baseUrl);
        var state = SnapshotState();
        var capabilities = executor.GetCapabilities().OrderBy(x => x, StringComparer.Ordinal).ToArray();
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

    private void MarkPollSucceeded()
    {
        lock (_stateLock)
        {
            _lastSuccessfulPoll = DateTime.UtcNow;
            _lastError = null;
        }
    }

    private void InvalidateAccessToken()
    {
        lock (_stateLock)
        {
            _accessToken = null;
            _accessExpiresAt = default;
        }
    }

    private LunaConnectionState SnapshotState()
    {
        lock (_stateLock) return Clone(_state);
    }

    private void SaveStateLocked(bool mirrorBackup = false, bool purgeBackup = false)
    {
        configFiles.SaveSecret(StateFile, _state);
        var path = configFiles.GetConfigPath(StateFile);
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

    private HttpClient CreateHttp(TimeSpan timeout)
    {
        var http = httpClientFactory.CreateClient("LunaConnector");
        http.Timeout = timeout;
        return http;
    }

    private static string Endpoint(string baseUrl, string path) =>
        NormalizeBaseUrl(baseUrl) + "/" + path.TrimStart('/');

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);

    private static string AuthenticationError(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) return "Luna rejected that email or password.";
        if (response.StatusCode == HttpStatusCode.Forbidden) return "Luna requires account or email confirmation before connecting.";
        if ((int)response.StatusCode == 429) return "The Luna account is temporarily locked. Try again later.";
        return $"Luna login returned HTTP {(int)response.StatusCode}.";
    }

    private static string SafeError(Exception exception) => exception switch
    {
        LunaConnectionException => exception.Message,
        HttpRequestException => "Snacks could not reach Luna.",
        TaskCanceledException => "The Luna request timed out.",
        _ => "The Luna connector encountered an unexpected error.",
    };

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

    private static LunaConnectionState NormalizeState(LunaConnectionState state)
    {
        if (string.IsNullOrWhiteSpace(state.InstanceId))
            state.InstanceId = Guid.NewGuid().ToString("N");
        return state;
    }

    private sealed record AuthWireResponse(
        string AccessToken,
        int ExpiresIn,
        string RefreshToken,
        DateTime RefreshExpiresAt,
        AuthUserWire? User);

    private sealed record AuthUserWire(string? Email);
    private sealed record RegisterWireResponse(string AgentId, int PollIntervalSeconds);
    private sealed record LeaseWireResponse(
        string Id,
        string LeaseToken,
        string Action,
        JsonElement Arguments,
        DateTime LeaseExpiresAt,
        int Attempt);
}

public sealed class LunaTaskWorker(
    LunaConnectionService connection,
    ILogger<LunaTaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(10);
            try
            {
                var handledTask = await connection.PollOnceAsync(stoppingToken);
                failures = 0;
                delay = handledTask
                    ? TimeSpan.FromMilliseconds(100)
                    : TimeSpan.FromSeconds(connection.PollIntervalSeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 5))));
                logger.LogWarning(ex, "Luna task polling failed; retrying in {DelaySeconds:n0}s", delay.TotalSeconds);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}
