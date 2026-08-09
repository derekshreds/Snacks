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
        var configChanged = false;
        if (string.IsNullOrEmpty(_config.SessionSecret))
        {
            _config.SessionSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            configChanged = true;
        }
        var normalizedOrigins = NormalizeIframeOrigins(_config.IframeAllowedOrigins, throwOnInvalid: false);
        if (!normalizedOrigins.SequenceEqual(_config.IframeAllowedOrigins ?? [], StringComparer.Ordinal))
        {
            _config.IframeAllowedOrigins = normalizedOrigins;
            configChanged = true;
        }
        if (configChanged) _configFileService.Save("auth.json", _config);
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
                enabled      = _config.Enabled,
                username     = _config.Username,
                hasPassword  = !string.IsNullOrEmpty(_config.PasswordHash),
                hasApiKey    = !string.IsNullOrEmpty(_config.ApiKey),
                hasEmbedToken = !string.IsNullOrEmpty(_config.EmbedToken),
                iframeAllowedOrigins = _config.IframeAllowedOrigins?.ToArray() ?? [],
                envApiKeySet = HasEnvApiKey,
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
                PasswordHash         = _config.PasswordHash,
                SessionSecret        = _config.SessionSecret,
                ApiKey               = _config.ApiKey,
                EmbedToken           = _config.EmbedToken,
                IframeAllowedOrigins = _config.IframeAllowedOrigins?.ToList() ?? [],
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

    /// <summary> Whether an API key is configured via the SNACKS_API_KEY environment variable. </summary>
    public bool HasEnvApiKey =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNACKS_API_KEY"));

    /// <summary>
    ///     Validates a presented API key against the SNACKS_API_KEY env var and the stored
    ///     key, both via constant-time comparison. Empty presented or configured keys never
    ///     match — an unconfigured key must not accept an empty header.
    /// </summary>
    /// <param name="presented"> The key from the X-Api-Key header, Bearer token, or ?apiKey= query. </param>
    public bool ValidateApiKey(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var envKey = Environment.GetEnvironmentVariable("SNACKS_API_KEY");
        if (!string.IsNullOrEmpty(envKey) && SecretCompare.ConstantTimeEquals(envKey, presented))
            return true;

        string stored;
        lock (_lock) stored = _config.ApiKey;
        return !string.IsNullOrEmpty(stored) && SecretCompare.ConstantTimeEquals(stored, presented);
    }

    /// <summary>
    ///     Generates, persists, and returns a new stored API key, replacing any previous one.
    ///     An env-provided SNACKS_API_KEY is unaffected and stays valid alongside it.
    /// </summary>
    public string GenerateApiKey()
    {
        var key = "snk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        lock (_lock)
        {
            _config.ApiKey = key;
            _configFileService.Save("auth.json", _config);
        }
        return key;
    }

    /// <summary> Returns the stored API key ("" when none). The env key is never exposed. </summary>
    public string GetStoredApiKey()
    {
        lock (_lock) return _config.ApiKey;
    }

    /// <summary> Removes the stored API key. An env-provided SNACKS_API_KEY is unaffected. </summary>
    public void ClearApiKey()
    {
        lock (_lock)
        {
            _config.ApiKey = "";
            _configFileService.Save("auth.json", _config);
        }
    }

    /// <summary>
    ///     Generates a scoped token for server-rendered iframe pages. Unlike the API key,
    ///     this credential is never accepted on <c>/api/*</c>.
    /// </summary>
    public string GenerateEmbedToken()
    {
        var token = "snk_embed_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');
        lock (_lock)
        {
            _config.EmbedToken = token;
            _configFileService.Save("auth.json", _config);
        }
        return token;
    }

    /// <summary>Validates a scoped iframe token using a constant-time comparison.</summary>
    public bool ValidateEmbedToken(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        string stored;
        lock (_lock) stored = _config.EmbedToken;
        return !string.IsNullOrEmpty(stored) && SecretCompare.ConstantTimeEquals(stored, presented);
    }

    /// <summary>Returns the persisted iframe token, or an empty string when unset.</summary>
    public string GetStoredEmbedToken()
    {
        lock (_lock) return _config.EmbedToken;
    }

    /// <summary>Revokes the persisted iframe token.</summary>
    public void ClearEmbedToken()
    {
        lock (_lock)
        {
            _config.EmbedToken = "";
            _configFileService.Save("auth.json", _config);
        }
    }

    /// <summary>
    ///     Replaces the iframe CSP allowlist after reducing each entry to a concrete
    ///     HTTP(S) origin. Wildcards and non-web schemes are deliberately rejected.
    /// </summary>
    public void UpdateIframeAllowedOrigins(IEnumerable<string>? origins)
    {
        var normalized = NormalizeIframeOrigins(origins, throwOnInvalid: true);
        lock (_lock)
        {
            _config.IframeAllowedOrigins = normalized;
            _configFileService.Save("auth.json", _config);
        }
    }

    private static List<string> NormalizeIframeOrigins(IEnumerable<string>? origins, bool throwOnInvalid)
    {
        var normalized = new List<string>();
        foreach (var candidate in origins ?? [])
        {
            var value = candidate?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                if (throwOnInvalid) throw new ArgumentException($"Invalid iframe origin: {value}");
                continue;
            }

            normalized.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    ///     CSP <c>frame-ancestors</c> directive value derived from the configured
    ///     iframe allowlist. Same-origin embedding is always allowed; an empty list
    ///     therefore remains locked to <c>'self'</c> rather than allowing every site.
    /// </summary>
    public string GetIframeFrameAncestors()
    {
        lock (_lock)
        {
            var origins = _config.IframeAllowedOrigins;
            if (origins == null || origins.Count == 0) return "'self'";
            return "'self' " + string.Join(' ', origins.Where(o => !string.IsNullOrWhiteSpace(o)));
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
