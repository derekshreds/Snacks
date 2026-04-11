using System.Diagnostics;

var ffmpeg  = Path.GetFullPath("electron-app/ffmpeg/ffmpeg.exe");
var ffprobe = Path.GetFullPath("electron-app/ffmpeg/ffprobe.exe");
var input   = @"D:\test-video.mkv";
var outFile = Path.Combine(Path.GetTempPath(), "snacks-rc-test.mkv");

const int duration = 480;
const string seek  = "00:00:00";

if (!File.Exists(ffmpeg))  { Console.WriteLine($"ffmpeg not found: {ffmpeg}");  return; }
if (!File.Exists(ffprobe)) { Console.WriteLine($"ffprobe not found: {ffprobe}"); return; }
if (!File.Exists(input))   { Console.WriteLine($"Input not found: {input}");    return; }

// Two rounds at different bitrates to validate rate control actually scales
var rounds = new[] {
    (target: 7000, max: 7500, bufsize: 15000),
    (target: 14000, max: 14500, bufsize: 29000),
};

// Test configurations — flags use {T}, {M}, {B} as placeholders for target/max/bufsize
var tests = new (string name, string encoder, string flagTemplate, string hwaccel, bool calibrated)[] {
    // Software
    ("libx265_vbr", "libx265", "-preset medium -g 25 -b:v {T}k -maxrate {M}k -bufsize {B}k", "-y", false),
    ("svtav1_crf_cap", "libsvtav1", "-preset 6 -crf 30 -maxrate {M}k -bufsize {B}k", "-y", false),
    ("svtav1_vbr", "libsvtav1", "-preset 6 -svtav1-params rc=1 -b:v {T}k", "-y", false),
    ("svtav1_cal_vbr", "libsvtav1", "-preset 6 -svtav1-params rc=1 -b:v {T}k", "-y", true),

    // NVENC
    ("nv_vbr", "hevc_nvenc", "-preset medium -g 25 -rc vbr -b:v {T}k -maxrate {M}k -bufsize {B}k", "-y -hwaccel cuda", false),
    ("nv_vbr_la_aq", "hevc_nvenc", "-preset medium -g 25 -rc vbr -rc-lookahead 32 -spatial_aq 1 -temporal_aq 1 -b:v {T}k -maxrate {M}k -bufsize {B}k", "-y -hwaccel cuda", false),
    ("nv_cbr", "hevc_nvenc", "-preset medium -g 25 -rc cbr -b:v {T}k -maxrate {M}k -bufsize {B}k", "-y -hwaccel cuda", false),
    ("nv_vbr_hq", "hevc_nvenc", "-preset medium -g 25 -rc vbr_hq -b:v {T}k -maxrate {M}k -bufsize {B}k", "-y -hwaccel cuda", false),
};

Console.WriteLine();
Console.WriteLine("========================================================================");
Console.WriteLine("  ENCODING RATE CONTROL TEST SUITE");
Console.WriteLine($"  Sample: {duration}s at {seek}");
Console.WriteLine("========================================================================");

const int nameWidth = 20;
const int colWidth = 16;

Console.WriteLine();
var header = "  " + "Test".PadRight(nameWidth);
foreach (var r in rounds)
    header += $"target {r.target}k".PadRight(colWidth);
Console.WriteLine(header);

var sep = "  " + new string('-', 4).PadRight(nameWidth);
foreach (var r in rounds)
    sep += new string('-', $"target {r.target}k".Length).PadRight(colWidth);
Console.WriteLine(sep);

foreach (var (name, encoder, flagTemplate, hwaccel, calibrated) in tests)
{
    Console.Write("  " + name.PadRight(nameWidth));

    foreach (var (targetKbps, maxKbps, bufsizeKbps) in rounds)
    {
        int kbps;

        if (calibrated)
        {
            // Calibration: two 10s samples at different positions, measure actual bitrate,
            // compute correction factor, re-encode with adjusted target
            kbps = RunCalibratedEncode(ffmpeg, ffprobe, input, outFile,
                hwaccel, encoder, flagTemplate, targetKbps, maxKbps, bufsizeKbps, duration, seek);
        }
        else
        {
            var flags = flagTemplate
                .Replace("{T105}", ((int)(targetKbps * 1.05)).ToString())
                .Replace("{T}", targetKbps.ToString())
                .Replace("{M}", maxKbps.ToString())
                .Replace("{B}", bufsizeKbps.ToString());
            kbps = RunEncode(ffmpeg, ffprobe, input, outFile, hwaccel, encoder, flags, duration, seek);
        }

        if (kbps < 0)
        {
            Console.Write("SKIP".PadRight(colWidth));
        }
        else
        {
            string verdict = kbps <= maxKbps ? "PASS" : kbps <= maxKbps + 500 ? "CLOSE" : "FAIL";
            Console.Write($"{verdict} {kbps}k".PadRight(colWidth));
        }
    }
    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("========================================================================");
Console.WriteLine("  PASS = at or under maxrate, CLOSE = within 500 over, FAIL = way over");
Console.WriteLine("========================================================================");
Console.WriteLine();

// =========================================================================
// Calibrated encode: iterative two-sample calibration to find the -b:v
// value that makes SVT-AV1 actually produce the target bitrate.
// SVT-AV1's VBR consistently undershoots, so we measure the shortfall
// and keep inflating the requested bitrate until output matches target.
// =========================================================================
static int RunCalibratedEncode(string ffmpeg, string ffprobe, string input, string outFile,
    string hwaccel, string encoder, string flagTemplate,
    int targetKbps, int maxKbps, int bufsizeKbps, int duration, string finalSeek)
{
    const int sampleDuration = 30;
    const int maxIterations = 6;
    const double tolerance = 0.05; // within 5% of target

    // Two samples at ~25% and ~60% into the file
    var sampleSeeks = new[] { "00:32:00", "01:17:00" };

    // Start with the target itself, measure the ratio, and use that to
    // compute the initial inflated -b:v. Then iterate to fine-tune.
    int currentBv = targetKbps;

    for (int iter = 1; iter <= maxIterations; iter++)
    {
        var sampleBitrates = new List<int>();
        foreach (var sampleSeek in sampleSeeks)
        {
            var sampleFlags = flagTemplate
                .Replace("{T}", currentBv.ToString())
                .Replace("{M}", maxKbps.ToString())
                .Replace("{B}", bufsizeKbps.ToString());

            int measured = RunEncode(ffmpeg, ffprobe, input, outFile, hwaccel, encoder,
                sampleFlags, sampleDuration, sampleSeek, $"cal{iter}.{sampleBitrates.Count + 1} ");
            if (measured > 0)
                sampleBitrates.Add(measured);
        }

        if (sampleBitrates.Count < 2)
            return -1;

        int lo = sampleBitrates.Min();
        int hi = sampleBitrates.Max();
        int avg = (lo + hi) / 2;

        // Done when both samples are within 10% of target individually,
        // and the average is within 5% (ensures they're centered, not lopsided)
        double loError = Math.Abs((double)(lo - targetKbps) / targetKbps);
        double hiError = Math.Abs((double)(hi - targetKbps) / targetKbps);
        double avgError = Math.Abs((double)(avg - targetKbps) / targetKbps);
        if (loError <= 0.10 && hiError <= 0.10 && avgError <= tolerance)
            break;

        // Use the ratio between what we asked for and what we got to scale
        // the -b:v directly. This accounts for SVT-AV1's non-linear undershoot.
        // target/avg gives us how much the encoder undershoots at this -b:v,
        // so we multiply currentBv by that ratio to compensate.
        double correctionRatio = (double)targetKbps / avg;
        currentBv = (int)(currentBv * correctionRatio);
        currentBv = Math.Clamp(currentBv, targetKbps, targetKbps * 4);
    }

    // Log the calibration result to stderr so it doesn't break the table
    Console.Error.WriteLine($"    [calibration: target={targetKbps}k, final bv={currentBv}k]");

    // Final encode with the calibrated -b:v
    var finalFlags = flagTemplate
        .Replace("{T}", currentBv.ToString())
        .Replace("{M}", maxKbps.ToString())
        .Replace("{B}", bufsizeKbps.ToString());

    return RunEncode(ffmpeg, ffprobe, input, outFile, hwaccel, encoder, finalFlags, duration, finalSeek, "enc ");
}

// =========================================================================
// Single encode + measure — shows live progress % in the current column
// =========================================================================
static int RunEncode(string ffmpeg, string ffprobe, string input, string outFile,
    string hwaccel, string encoder, string flags, int duration, string seek,
    string? progressPrefix = null)
{
    if (File.Exists(outFile)) File.Delete(outFile);

    var encArgs = $"{hwaccel} -ss {seek} -i \"{input}\" -t {duration} -map 0:0 -c:v {encoder} {flags} -an -sn -f matroska \"{outFile}\"";

    var psi = new ProcessStartInfo(ffmpeg)
    {
        Arguments = encArgs,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var proc = Process.Start(psi)!;
    proc.StandardOutput.ReadToEndAsync();

    // Read stderr to show live progress and collect bitrate readings
    int cursorLeft = Console.CursorLeft;
    var stderrReader = proc.StandardError;
    var buffer = new char[4096];
    var partial = "";
    var bitrateReadings = new List<double>();

    while (!proc.HasExited || stderrReader.Peek() >= 0)
    {
        int read = 0;
        try { read = stderrReader.Read(buffer, 0, buffer.Length); } catch { break; }
        if (read == 0) break;

        partial += new string(buffer, 0, read);
        var lines = partial.Split('\r');
        partial = lines[^1];

        for (int i = 0; i < lines.Length - 1; i++)
        {
            // Parse progress percentage
            var timeMatch = System.Text.RegularExpressions.Regex.Match(lines[i], @"time=(\d+):(\d+):(\d+)\.(\d+)");
            if (timeMatch.Success)
            {
                int h = int.Parse(timeMatch.Groups[1].Value);
                int m = int.Parse(timeMatch.Groups[2].Value);
                int s = int.Parse(timeMatch.Groups[3].Value);
                double elapsed = h * 3600 + m * 60 + s;
                int pct = Math.Min(99, (int)(elapsed / duration * 100));
                string label = progressPrefix != null ? $"{progressPrefix}{pct}%" : $"{pct}%";
                Console.SetCursorPosition(cursorLeft, Console.CursorTop);
                Console.Write(label.PadRight(16));
                Console.SetCursorPosition(cursorLeft, Console.CursorTop);
            }

            // Collect bitrate from FFmpeg's running average (bitrate=XXXXkbits/s)
            var brMatch = System.Text.RegularExpressions.Regex.Match(lines[i], @"bitrate=\s*([\d.]+)kbits/s");
            if (brMatch.Success && double.TryParse(brMatch.Groups[1].Value, out double br))
                bitrateReadings.Add(br);
        }
    }
    proc.WaitForExit(600_000);

    // Clear the progress text
    Console.SetCursorPosition(cursorLeft, Console.CursorTop);
    Console.Write(new string(' ', 16));
    Console.SetCursorPosition(cursorLeft, Console.CursorTop);

    // Average the last 5 bitrate readings from FFmpeg's progress output
    int kbps = 0;
    if (bitrateReadings.Count >= 5)
    {
        kbps = (int)bitrateReadings.Skip(bitrateReadings.Count - 5).Average();
    }
    else if (bitrateReadings.Count > 0)
    {
        kbps = (int)bitrateReadings.Last();
    }

    try { File.Delete(outFile); } catch { }
    return kbps;
}
