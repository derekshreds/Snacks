namespace Snacks.Models.Requests;

/// <summary>
///     Request body for the "delete all flagged files" cleanup on the Library Health page.
///     Mirrors the health listing's narrowing so the bulk delete acts on exactly the set the
///     user is looking at: the active category and the optional name/path search.
/// </summary>
public sealed class HealthDeleteAllRequest
{
    /// <summary> Health category: no-audio | no-video | no-duration | failed | verify-failed, or null for all issues. </summary>
    public string? Filter { get; set; }

    /// <summary> Optional name/path substring filter. </summary>
    public string? Q { get; set; }
}
