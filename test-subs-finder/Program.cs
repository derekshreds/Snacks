using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// ============================================================================
// Scans a directory tree for video files with image-based subtitle tracks
// (PGS / VobSub / DVB / XSUB) — the codecs Snacks' native OCR pipeline targets.
// Prints a grouped report and writes a plain list of matching paths that can
// be fed back into Snacks for OCR testing.
// ============================================================================

// Edit these three lines to point at your setup.
var searchRoot = @"Z:\Media\Movies";
var ffprobe    = Path.GetFullPath("electron-app/ffmpeg/ffprobe.exe");
var outList    = Path.Combine(Path.GetTempPath(), "snacks-bitmap-subs.txt");

var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    ".mkv", ".mp4", ".m2ts", ".ts", ".mov", ".avi", ".webm", ".vob",
};

// Same set as Snacks.FfprobeService._bitmapSubCodecs — if that list changes, update here.
var bitmapCodecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
    "hdmv_pgs_subtitle", "pgssub",
    "dvd_subtitle",      "dvdsub",
    "dvb_subtitle",      "dvbsub",
    "xsub",
};

if (!File.Exists(ffprobe)) { Console.Error.WriteLine($"ffprobe not found: {ffprobe}"); return 1; }
if (!Directory.Exists(searchRoot)) { Console.Error.WriteLine($"Search root not found: {searchRoot}"); return 1; }

Console.WriteLine($"Scanning {searchRoot}");
Console.WriteLine($"ffprobe: {ffprobe}");
Console.WriteLine();

var files = Directory
    .EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories)
    .Where(p => extensions.Contains(Path.GetExtension(p)))
    .ToList();

Console.WriteLine($"Found {files.Count} candidate files. Probing in parallel...");
Console.WriteLine();

var results = new ConcurrentBag<Hit>();
var sw      = Stopwatch.StartNew();
var done    = 0;

await Parallel.ForEachAsync(
    files,
    new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) },
    async (path, ct) =>
    {
        try
        {
            var tracks = await ProbeBitmapTracksAsync(path, ffprobe, ct);
            if (tracks.Count > 0) results.Add(new Hit(path, tracks));
        }
        catch (Exception ex)
        {
            lock (Console.Error) Console.Error.WriteLine($"  [error] {path}: {ex.Message}");
        }

        int n = Interlocked.Increment(ref done);
        if (n % 25 == 0 || n == files.Count)
            Console.WriteLine($"  ...{n}/{files.Count} probed  ({sw.Elapsed:mm\\:ss})");
    });

Console.WriteLine();
Console.WriteLine("========================================================================");
Console.WriteLine($"  {results.Count} file(s) with image-based subtitles");
Console.WriteLine("========================================================================");
Console.WriteLine();

// Group output by codec so it's easy to pick OCR test cases: "give me a PGS one", etc.
foreach (var codec in bitmapCodecs.OrderBy(c => c))
{
    var forCodec = results
        .Where(h => h.Tracks.Any(t => string.Equals(t.Codec, codec, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(h => h.Path)
        .ToList();
    if (forCodec.Count == 0) continue;

    Console.WriteLine($"--- {codec}  ({forCodec.Count} file{(forCodec.Count == 1 ? "" : "s")}) ---");
    foreach (var hit in forCodec)
    {
        var langs = string.Join(",", hit.Tracks
            .Where(t => string.Equals(t.Codec, codec, StringComparison.OrdinalIgnoreCase))
            .Select(t => string.IsNullOrEmpty(t.Lang) ? "?" : t.Lang));
        Console.WriteLine($"  [{langs}]  {hit.Path}");
    }
    Console.WriteLine();
}

await File.WriteAllLinesAsync(
    outList,
    results.OrderBy(h => h.Path).Select(h => h.Path));

Console.WriteLine($"List written to: {outList}");
Console.WriteLine($"Total time: {sw.Elapsed:mm\\:ss}");
return 0;


static async Task<List<Track>> ProbeBitmapTracksAsync(string path, string ffprobe, CancellationToken ct)
{
    var psi = new ProcessStartInfo(ffprobe)
    {
        Arguments              = $"-v error -select_streams s -show_entries stream=index,codec_name:stream_tags=language -of json \"{path}\"",
        UseShellExecute        = false,
        RedirectStandardOutput = true,
        RedirectStandardError  = true,
        CreateNoWindow         = true,
    };
    using var proc = new Process { StartInfo = psi };
    proc.Start();
    var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
    _ = proc.StandardError.ReadToEndAsync(ct);
    await proc.WaitForExitAsync(ct);

    var json = await stdoutTask;
    if (string.IsNullOrWhiteSpace(json)) return new List<Track>();

    using var doc = JsonDocument.Parse(json);
    var hits = new List<Track>();
    if (!doc.RootElement.TryGetProperty("streams", out var streams)) return hits;

    foreach (var s in streams.EnumerateArray())
    {
        string codec = s.TryGetProperty("codec_name", out var c) ? (c.GetString() ?? "") : "";
        if (!BitmapCodecs.Contains(codec)) continue;

        string lang = "";
        if (s.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty("language", out var l))
            lang = l.GetString() ?? "";

        hits.Add(new Track(codec, lang));
    }
    return hits;
}

// Kept in sync with Snacks.FfprobeService._bitmapSubCodecs.
static class BitmapCodecs
{
    private static readonly HashSet<string> _set = new(StringComparer.OrdinalIgnoreCase) {
        "hdmv_pgs_subtitle", "pgssub",
        "dvd_subtitle",      "dvdsub",
        "dvb_subtitle",      "dvbsub",
        "xsub",
    };
    public static bool Contains(string codec) => _set.Contains(codec);
}

record Track(string Codec, string Lang);
record Hit(string Path, List<Track> Tracks);
