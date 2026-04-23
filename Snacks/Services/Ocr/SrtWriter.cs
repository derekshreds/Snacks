using System.Globalization;
using System.Text;

namespace Snacks.Services.Ocr;

/// <summary>
///     Builds an SRT file incrementally from (start, end, text) tuples. Skips empty/whitespace
///     cues and collapses adjacent cues that share the same text into a single cue spanning
///     both — avoids the flicker a naive OCR pipeline produces when a sub persists across
///     multiple rendering updates.
/// </summary>
public sealed class SrtWriter
{
    private readonly StringBuilder _sb = new();
    private int       _index;
    private TimeSpan? _pendingStart;
    private TimeSpan  _pendingEnd;
    private string?   _pendingText;

    public void Add(TimeSpan start, TimeSpan end, string text)
    {
        if (end <= start) return;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;

        if (_pendingText != null
            && string.Equals(_pendingText, trimmed, StringComparison.Ordinal)
            && start <= _pendingEnd + TimeSpan.FromMilliseconds(250))
        {
            // Same text, adjacent in time — extend.
            if (end > _pendingEnd) _pendingEnd = end;
            return;
        }

        Flush();
        _pendingStart = start;
        _pendingEnd   = end;
        _pendingText  = trimmed;
    }

    public void Flush()
    {
        if (_pendingStart is null || _pendingText is null) return;
        _index++;
        _sb.Append(_index.ToString(CultureInfo.InvariantCulture)).Append('\n');
        _sb.Append(Format(_pendingStart.Value)).Append(" --> ").Append(Format(_pendingEnd)).Append('\n');
        _sb.Append(_pendingText).Append("\n\n");
        _pendingStart = null;
        _pendingText  = null;
    }

    public async Task WriteAsync(string path, CancellationToken ct)
    {
        Flush();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        // UTF-8 with BOM — most media players accept it and it disambiguates from ANSI.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(_sb.ToString());
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    public int CueCount
    {
        get
        {
            int pending = (_pendingStart is not null && _pendingText is not null) ? 1 : 0;
            return _index + pending;
        }
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return string.Format(CultureInfo.InvariantCulture,
            "{0:D2}:{1:D2}:{2:D2},{3:D3}",
            (int)t.TotalHours, t.Minutes, t.Seconds, t.Milliseconds);
    }
}
