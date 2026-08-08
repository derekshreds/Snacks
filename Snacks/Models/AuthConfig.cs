namespace Snacks.Models;

/// <summary>
///     Control-panel authentication configuration. Serialized to auth.json.
///     Password is stored as a PBKDF2 hash in the format "iterations:saltBase64:hashBase64".
/// </summary>
public sealed class AuthConfig
{
    /// <summary> Whether the login gate is active. When <see langword="false"/>, all requests are allowed. </summary>
    public bool Enabled { get; set; } = false;

    /// <summary> The username required at login. </summary>
    public string Username { get; set; } = "";

    /// <summary> PBKDF2/SHA-256 password hash in "iterations:saltBase64:hashBase64" format. </summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>
    ///     Per-install random secret used to sign session cookies. Generated on first save.
    /// </summary>
    public string SessionSecret { get; set; } = "";

    /// <summary>
    ///     Optional API key accepted on <c>/api/*</c> via the <c>X-Api-Key</c> header, an
    ///     <c>Authorization: Bearer</c> token, or a <c>?apiKey=</c> query string (the
    ///     Sonarr/Radarr convention, for dashboards like Homarr that can only set a URL).
    ///     Empty = no stored key. Plaintext, like the cluster shared secret — the config
    ///     directory is the trust boundary. A key set via the <c>SNACKS_API_KEY</c> env var
    ///     is honored independently of this field.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    ///     CSP <c>frame-ancestors</c> allowlist for the <c>/iframe/*</c> embed routes.
    ///     Empty list ⇒ permissive (any origin may embed). Populate with concrete
    ///     origins (e.g. <c>"https://homarr.local"</c>) to lock embedding down.
    /// </summary>
    public List<string> IframeAllowedOrigins { get; set; } = new();
}
