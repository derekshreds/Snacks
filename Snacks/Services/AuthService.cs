using System.Security.Cryptography;
using System.Text;
using Snacks.Models;

namespace Snacks.Services;

/// <summary>
///     Password hashing (PBKDF2/SHA-256) plus HMAC-signed session tokens.
///     Tokens live in a cookie; format is "username.issuedUnix.hmac".
/// </summary>
public sealed class AuthService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes        = 16;
    private const int HashBytes        = 32;

    public const string CookieName = "snacks_session";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    private readonly ConfigFileService _configFileService;
    private AuthConfig                 _config;
    private readonly object            _lock = new();

    public AuthService(ConfigFileService configFileService)
    {
        ArgumentNullException.ThrowIfNull(configFileService);
        _configFileService = configFileService;
        _config            = _configFileService.Load<AuthConfig>("auth.json");
        if (string.IsNullOrEmpty(_config.SessionSecret))
        {
            _config.SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _configFileService.Save("auth.json", _config);
        }
    }

    /******************************************************************
     *  Config Access
     ******************************************************************/

    /// <summary> Returns the full auth configuration including the password hash and session secret. </summary>
    public AuthConfig GetConfig()
    {
        lock (_lock) return _config;
    }

    /// <summary>
    ///     Public view of auth config — never exposes the password hash, session secret, or API key.
    /// </summary>
    public object GetPublicConfig()
    {
        lock (_lock)
        {
            return new
            {
                enabled     = _config.Enabled,
                username    = _config.Username,
                hasPassword = !string.IsNullOrEmpty(_config.PasswordHash),
                hasApiKey   = !string.IsNullOrEmpty(_config.ApiKey),
            };
        }
    }

    /// <summary>
    ///     Updates auth config. If <paramref name="newPassword"/> is non-empty it is hashed;
    ///     otherwise the existing hash is preserved. Disabling auth clears the credentials.
    /// </summary>
    /// <param name="enabled"> Whether authentication is required. </param>
    /// <param name="username"> The username to require at login. </param>
    /// <param name="newPassword"> A new plain-text password to hash, or <see langword="null"/> to keep the existing hash. </param>
    public void UpdateConfig(bool enabled, string username, string? newPassword)
    {
        lock (_lock)
        {
            var cfg = new AuthConfig
            {
                Enabled       = enabled,
                Username      = username ?? "",
                PasswordHash  = _config.PasswordHash,
                SessionSecret = _config.SessionSecret,
            };

            if (!string.IsNullOrEmpty(newPassword))
                cfg.PasswordHash = HashPassword(newPassword);

            if (!enabled)
            {
                // Rotate session secret on disable so existing cookies are invalidated.
                cfg.SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            }

            _config = cfg;
            _configFileService.Save("auth.json", cfg);
        }
    }

    /******************************************************************
     *  Authentication
     ******************************************************************/

    /// <summary>
    ///     Returns <see langword="true"/> if <paramref name="password"/> matches the stored hash.
    /// </summary>
    /// <param name="password"> The plain-text password to verify. </param>
    public bool VerifyPassword(string password)
    {
        AuthConfig cfg;
        lock (_lock) cfg = _config;
        if (string.IsNullOrEmpty(cfg.PasswordHash)) return false;
        return VerifyHash(password, cfg.PasswordHash);
    }

    /// <summary>
    ///     Issues a signed session token for the given username in the format
    ///     <c>username.issuedUnix.hmac</c>.
    /// </summary>
    /// <param name="username"> The authenticated username to embed in the token. </param>
    public string IssueToken(string username)
    {
        AuthConfig cfg;
        lock (_lock) cfg = _config;
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var body   = $"{username}.{issued}";
        var hmac   = ComputeHmac(body, cfg.SessionSecret);
        return $"{body}.{hmac}";
    }

    /// <summary>
    ///     Validates a session token. Returns <see langword="true"/> when the token is
    ///     cryptographically valid, unexpired, and issued for the currently configured username.
    ///     Always returns <see langword="true"/> when auth is disabled.
    /// </summary>
    /// <param name="token"> The token string from the session cookie. </param>
    /// <param name="username"> The username extracted from the token on success. </param>
    public bool ValidateToken(string? token, out string username)
    {
        username = "";
        if (string.IsNullOrEmpty(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        AuthConfig cfg;
        lock (_lock) cfg = _config;
        if (!cfg.Enabled) return true; // auth disabled — everyone passes

        var body     = $"{parts[0]}.{parts[1]}";
        var expected = ComputeHmac(body, cfg.SessionSecret);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2]))) return false;

        if (!long.TryParse(parts[1], out var issued)) return false;
        var age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - issued;
        if (age < 0 || age > (long)SessionLifetime.TotalSeconds) return false;

        if (!string.Equals(parts[0], cfg.Username, StringComparison.Ordinal)) return false;

        username = parts[0];
        return true;
    }

    /// <summary>
    ///     Returns <see langword="true"/> when authentication is both enabled and a password
    ///     hash is configured, meaning the login gate is active.
    /// </summary>
    public bool IsAuthRequired()
    {
        lock (_lock) return _config.Enabled && !string.IsNullOrEmpty(_config.PasswordHash);
    }

    /******************************************************************
     *  API Key
     ******************************************************************/

    /// <summary>
    ///     Generates a fresh 32-byte random key, base64url-encoded (no padding), persists
    ///     it to <c>auth.json</c>, and returns it once. Caller is expected to surface the
    ///     value to the user immediately — it is not retrievable after this call.
    /// </summary>
    public string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var key   = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');

        lock (_lock)
        {
            _config.ApiKey = key;
            _configFileService.Save("auth.json", _config);
        }
        return key;
    }

    /// <summary>
    ///     Clears the stored API key. Subsequent <see cref="ValidateApiKey"/> calls will
    ///     reject every candidate until <see cref="GenerateApiKey"/> is called again.
    /// </summary>
    public void ClearApiKey()
    {
        lock (_lock)
        {
            _config.ApiKey = null;
            _configFileService.Save("auth.json", _config);
        }
    }

    /// <summary>
    ///     Constant-time compare of <paramref name="key"/> against the stored API key.
    ///     Returns <see langword="false"/> when no key is configured server-side, when the
    ///     candidate is empty, or when the lengths differ — the key path is opt-in and
    ///     never grants implicit access.
    /// </summary>
    /// <param name="key"> The candidate key from the <c>X-Api-Key</c> header or <c>?apiKey=</c> query. </param>
    public bool ValidateApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        string? stored;
        lock (_lock) stored = _config.ApiKey;
        if (string.IsNullOrEmpty(stored)) return false;

        var candidateBytes = Encoding.UTF8.GetBytes(key);
        var storedBytes    = Encoding.UTF8.GetBytes(stored);
        if (candidateBytes.Length != storedBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(candidateBytes, storedBytes);
    }

    /// <summary>
    ///     CSP <c>frame-ancestors</c> directive value derived from the configured
    ///     iframe allowlist. Returns <c>"*"</c> when the allowlist is empty (permissive
    ///     default) or a space-joined list of origins otherwise.
    /// </summary>
    public string GetIframeFrameAncestors()
    {
        lock (_lock)
        {
            var origins = _config.IframeAllowedOrigins;
            if (origins == null || origins.Count == 0) return "*";
            return string.Join(' ', origins.Where(o => !string.IsNullOrWhiteSpace(o)));
        }
    }

    /******************************************************************
     *  Cryptographic Helpers
     ******************************************************************/

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Pbkdf2Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyHash(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], out var iterations)) return false;
        var salt     = Convert.FromBase64String(parts[1]);
        var expected = Convert.FromBase64String(parts[2]);
        var actual   = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string ComputeHmac(string body, string secretBase64)
    {
        var key = Convert.FromBase64String(secretBase64);
        using var hmac = new HMACSHA256(key);
        var sig = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(sig).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }
}
