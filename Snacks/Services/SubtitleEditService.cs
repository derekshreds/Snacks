using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Snacks.Services;

// Subtitle Edit is distributed under GPLv3. Snacks does not bundle the SubtitleEdit.exe
// binary in its releases — it is downloaded on demand into the user's Snacks work dir
// (or picked up from a system install on Linux) so that shipping Snacks does not carry
// the copyleft obligation that comes with redistributing the SE binary itself.

/// <summary>
///     Thin wrapper around the Subtitle Edit CLI, used to OCR bitmap subtitle
///     streams (PGS, VobSub, DVB) into SRT sidecar files.
/// </summary>
/// <remarks>
///     Resolution order for the SubtitleEdit executable:
///     <list type="number">
///         <item><c>SNACKS_SUBTITLE_EDIT_PATH</c> env var (explicit override)</item>
///         <item>Linux: <c>/usr/bin/subtitleedit</c> (via <c>apt install subtitleedit</c>)</item>
///         <item>Downloaded portable build under <c>{SNACKS_WORK_DIR}/tools/subtitle-edit/</c></item>
///         <item>Unavailable — the service logs a warning and returns <c>null</c> from conversion calls</item>
///     </list>
/// </remarks>
public sealed class SubtitleEditService
{
    // Pinned SE release; bump when upstream fixes OCR bugs that affect Snacks users.
    private const string SE_VERSION = "4.0.13";
    private const string SE_WINDOWS_ZIP_URL = "https://github.com/SubtitleEdit/subtitleedit/releases/download/" + SE_VERSION + "/SubtitleEdit-" + SE_VERSION + "-Setup.zip";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim      _downloadLock = new(1, 1);
    private string?                     _resolvedPath;
    private bool                        _resolutionAttempted;

    public SubtitleEditService(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    ///     OCRs a single bitmap subtitle stream into an SRT file. Returns the output
    ///     path on success, <c>null</c> on skip/failure (logged via <paramref name="log"/>).
    /// </summary>
    /// <param name="sourceFile">     The source media file (Subtitle Edit reads streams directly). </param>
    /// <param name="streamIndex">    FFmpeg stream index of the bitmap track. </param>
    /// <param name="lang">           2-letter ISO code used to pick a Tesseract language pack. </param>
    /// <param name="codecName">      Source codec (e.g. <c>hdmv_pgs_subtitle</c>, <c>dvd_subtitle</c>). </param>
    /// <param name="outputPath">     Target <c>.srt</c> path. </param>
    /// <param name="log">            Async logger. </param>
    /// <param name="ct">             Cancellation token. </param>
    public async Task<string?> ConvertBitmapToSrtAsync(
        string            sourceFile,
        int               streamIndex,
        string            lang,
        string            codecName,
        string            outputPath,
        Func<string,Task> log,
        CancellationToken ct)
    {
        var exe = await ResolveExecutableAsync(log, ct);
        if (exe == null)
        {
            await log("Subtitle Edit not available — skipping OCR for bitmap subtitle stream.");
            return null;
        }

        // VobSub / DVD subs: low-res, binary-image-compare beats Tesseract reliably.
        // PGS / HDMV: Tesseract with the right language pack is the better pick.
        bool isDvdSub = string.Equals(codecName, "dvd_subtitle", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(codecName, "dvdsub",       StringComparison.OrdinalIgnoreCase);

        string tessLang = MapToTesseractLang(lang);
        string outDir   = Path.GetDirectoryName(outputPath) ?? ".";
        string outName  = Path.GetFileNameWithoutExtension(outputPath);

        // SubtitleEdit CLI writes "{sourceBasename}.srt" into outputfolder by default;
        // we pass /outputfilename:{name} to force the final name and avoid collisions.
        string args = isDvdSub
            ? $"/convert \"{sourceFile}\" srt /track:{streamIndex} /ocrdb:binary-image-compare /outputfolder:\"{outDir}\" /outputfilename:\"{outName}\" /overwrite"
            : $"/convert \"{sourceFile}\" srt /track:{streamIndex} /ocrengine:tesseract /ocrlanguage:{tessLang} /outputfolder:\"{outDir}\" /outputfilename:\"{outName}\" /overwrite";

        await log($"OCR (stream {streamIndex}, {lang}) → {Path.GetFileName(outputPath)}");

        var psi = new ProcessStartInfo(exe)
        {
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            var stdOutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
            {
                var tail = (await stdErrTask + "\n" + await stdOutTask).Split('\n').TakeLast(5);
                await log($"Subtitle Edit exited {proc.ExitCode}: {string.Join(" ", tail)}");
                return null;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await log($"Subtitle Edit invocation failed: {ex.Message}");
            return null;
        }

        return File.Exists(outputPath) ? outputPath : null;
    }

    private async Task<string?> ResolveExecutableAsync(Func<string,Task> log, CancellationToken ct)
    {
        if (_resolutionAttempted) return _resolvedPath;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_resolutionAttempted) return _resolvedPath;

            // 1) Explicit env-var override.
            var overridePath = Environment.GetEnvironmentVariable("SNACKS_SUBTITLE_EDIT_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                _resolvedPath = overridePath;
            }
            // 2) Linux system install.
            else if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                  && File.Exists("/usr/bin/subtitleedit"))
            {
                _resolvedPath = "/usr/bin/subtitleedit";
            }
            else
            {
                // 3) Try the downloaded portable build. On-demand download on Windows only —
                // Linux users should install via apt (step 2); we don't try to run Windows SE under Mono.
                _resolvedPath = await TryDownloadedBuildAsync(log, ct);
            }

            _resolutionAttempted = true;
            return _resolvedPath;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    private async Task<string?> TryDownloadedBuildAsync(Func<string,Task> log, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var seDir = Path.Combine(workDir, "tools", "subtitle-edit");
        var exe   = Path.Combine(seDir, "SubtitleEdit.exe");

        if (File.Exists(exe)) return exe;

        try
        {
            Directory.CreateDirectory(seDir);
            var zipPath = Path.Combine(seDir, "se.zip");

            await log($"Downloading Subtitle Edit {SE_VERSION} for OCR (one-time, ~30 MB)...");
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            using (var resp = await http.GetAsync(SE_WINDOWS_ZIP_URL, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            ZipFile.ExtractToDirectory(zipPath, seDir, overwriteFiles: true);
            File.Delete(zipPath);

            if (File.Exists(exe))
            {
                await log("Subtitle Edit installed.");
                return exe;
            }

            await log("Subtitle Edit archive extracted but SubtitleEdit.exe not found.");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            await log($"Failed to install Subtitle Edit: {ex.Message}");
            return null;
        }
    }

    /// <summary> Two-letter ISO → Tesseract 3-letter code mapping for the languages Snacks exposes. </summary>
    private static string MapToTesseractLang(string twoLetter) => twoLetter.ToLowerInvariant() switch
    {
        "en" => "eng",
        "es" => "spa",
        "fr" => "fra",
        "de" => "deu",
        "it" => "ita",
        "pt" => "por",
        "ru" => "rus",
        "ja" => "jpn",
        "ko" => "kor",
        "zh" => "chi_sim",
        "ar" => "ara",
        "hi" => "hin",
        "nl" => "nld",
        "sv" => "swe",
        "no" => "nor",
        "da" => "dan",
        "fi" => "fin",
        "pl" => "pol",
        "tr" => "tur",
        "cs" => "ces",
        "hu" => "hun",
        "el" => "ell",
        "he" => "heb",
        "th" => "tha",
        "vi" => "vie",
        "id" => "ind",
        "uk" => "ukr",
        _    => "eng",
    };
}
