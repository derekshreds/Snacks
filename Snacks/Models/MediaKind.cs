using System.Text.Json.Serialization;

namespace Snacks.Models;

/// <summary>
///     Discriminator that distinguishes video files from music (audio-only)
///     files throughout the queue, scheduler, settings, and analytics layers.
///     Set at the scan boundary in <c>FileService</c> from the file extension
///     and propagated unchanged through <see cref="MediaFile"/>, <see cref="WorkItem"/>,
///     and <see cref="EncodeHistory"/> rows.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MediaKind
{
    /// <summary> Video file (mkv, mp4, avi, ...). Routed through <c>ConvertVideoAsync</c>. </summary>
    Video,

    /// <summary> Audio-only file (mp3, m4a, flac, ...). Routed through <c>ConvertMusicAsync</c>. </summary>
    Music,
}
