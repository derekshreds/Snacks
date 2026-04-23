namespace Snacks.Models.Requests;

/// <summary> Request body for saving the control-panel authentication configuration. </summary>
public sealed class SaveAuthRequest
{
    /// <summary> Whether the login gate should be active after saving. </summary>
    public bool Enabled { get; set; }

    /// <summary> The username to require at login. </summary>
    public string Username { get; set; } = "";

    /// <summary> New plain-text password to hash and store, or <see langword="null"/> to keep the existing hash. </summary>
    public string? Password { get; set; }
}
