namespace Snacks.Models.Requests;

/// <summary> Request body for pausing or resuming the encoding queue. </summary>
public sealed class PauseRequest
{
    /// <summary> Whether the queue should be paused. </summary>
    public bool Paused { get; set; }
}
