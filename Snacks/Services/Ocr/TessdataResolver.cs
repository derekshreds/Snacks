using Snacks.Services;

namespace Snacks.Services.Ocr;

/// <summary>
///     Finds (and, if needed, downloads) a <c>{lang}.traineddata</c> file for the
///     Tesseract engine. Resolution order:
///     <list type="number">
///         <item><c>SNACKS_TESSDATA_PATH</c> env var</item>
///         <item><c>{AppContext.BaseDirectory}/tessdata</c> (bundled)</item>
///         <item><c>{SNACKS_WORK_DIR}/tools/tessdata</c> (on-demand download target)</item>
///     </list>
///     Returns a directory path that contains (or, after first-use, will contain) the requested
///     <c>.traineddata</c> file. Tesseract wants the directory, not the file.
/// </summary>
public sealed class TessdataResolver
{
    // tessdata_best gives materially better OCR on stylised / low-contrast subtitles than
    // tessdata_fast at the cost of ~2-3× engine throughput — a non-issue for a batch job that
    // finishes a full movie in well under two minutes either way. It's what pgsrip and the
    // rest of the serious subtitle-rip ecosystem default to.
    private const string TESSDATA_URL = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim      _downloadLock = new(1, 1);

    public TessdataResolver(IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _httpClientFactory = httpClientFactory;
    }

    public async Task<string> ResolveAsync(string lang, Func<string, Task> log, CancellationToken ct)
    {
        var overrideDir = Environment.GetEnvironmentVariable("SNACKS_TESSDATA_PATH");
        if (!string.IsNullOrWhiteSpace(overrideDir) && File.Exists(Path.Combine(overrideDir, $"{lang}.traineddata")))
            return overrideDir;

        var bundledDir = Path.Combine(AppContext.BaseDirectory, "tessdata");
        if (File.Exists(Path.Combine(bundledDir, $"{lang}.traineddata")))
            return bundledDir;

        // Fall back to the work-dir cache. Download the language pack if absent.
        var workDir = Environment.GetEnvironmentVariable("SNACKS_WORK_DIR")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snacks", "work");
        var cacheDir = Path.Combine(workDir, "tools", "tessdata");
        var cachedFile = Path.Combine(cacheDir, $"{lang}.traineddata");

        if (File.Exists(cachedFile)) return cacheDir;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (File.Exists(cachedFile)) return cacheDir;

            Directory.CreateDirectory(cacheDir);
            await log($"Downloading Tesseract language pack '{lang}' (~15-25 MB, one-time)...");

            var url = TESSDATA_URL + lang + ".traineddata";
            var http = _httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(5);

            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            // Write to a temp file and move into place so a crashed download doesn't leave
            // a half-written file that the Tesseract engine will happily load and fail on.
            var tmp = cachedFile + ".tmp";
            await using (var fs = File.Create(tmp))
                await resp.Content.CopyToAsync(fs, ct);
            File.Move(tmp, cachedFile, overwrite: true);

            await log($"Language pack '{lang}' installed.");
            return cacheDir;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    /// <summary>
    ///     Two-letter ISO → Tesseract 3-letter code. Tesseract uses the ISO 639-2/T form
    ///     for almost everything, with one wrinkle: Chinese is split into <c>chi_sim</c>
    ///     (Simplified) and <c>chi_tra</c> (Traditional) rather than using a single ISO
    ///     code. We default <c>zh</c> → Simplified since Simplified is the majority of
    ///     subtitle content; users needing Traditional can override by setting
    ///     <c>SNACKS_TESSDATA_PATH</c> and dropping <c>chi_tra.traineddata</c> there.
    /// </summary>
    public static string MapToTesseractLang(string twoLetter)
    {
        if (twoLetter.Equals("zh", StringComparison.OrdinalIgnoreCase)) return "chi_sim";
        return LanguageMatcher.ToThreeLetterT(twoLetter) ?? "eng";
    }
}
