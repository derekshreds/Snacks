namespace Snacks.Models;

/// <summary>
///     A single audio output variant the user wants emitted per kept language.
///     The transcoding planner expands these per-language: for each language in
///     <see cref="EncoderOptions.AudioLanguagesToKeep"/>, every profile produces
///     either a passthrough (when a source track already matches codec+layout)
///     or a re-encode from the best matching source track.
/// </summary>
public sealed class AudioOutputProfile
{
    /// <summary>
    ///     Logical codec name. Supported: <c>"aac"</c>, <c>"ac3"</c>, <c>"eac3"</c>, <c>"opus"</c>.
    ///     Pass-through of source tracks is controlled separately by
    ///     <see cref="EncoderOptions.PreserveOriginalAudio"/> rather than a "copy" profile here.
    /// </summary>
    public string Codec { get; set; } = "aac";

    /// <summary>
    ///     Target channel layout. Supported: <c>"Source"</c> (no <c>-ac</c> flag), <c>"Mono"</c>,
    ///     <c>"Stereo"</c>, <c>"5.1"</c>, <c>"7.1"</c>.
    /// </summary>
    public string Layout { get; set; } = "Source";

    /// <summary>
    ///     Target bitrate in kbps. <c>0</c> means "use the codec's default"
    ///     (192 for AAC/EAC3/Opus, 448 for AC3). Ignored for codecs that don't take a bitrate.
    /// </summary>
    public int BitrateKbps { get; set; } = 0;

    public AudioOutputProfile Clone() => new()
    {
        Codec       = Codec,
        Layout      = Layout,
        BitrateKbps = BitrateKbps,
    };
}
