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
}
