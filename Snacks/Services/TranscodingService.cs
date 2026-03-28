using Microsoft.AspNetCore.SignalR;
using Snacks.Hubs;
using Snacks.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Snacks.Services
{
    public class TranscodingService
    {
        private readonly ConcurrentDictionary<string, WorkItem> _workItems = new();
        private readonly List<WorkItem> _workQueue = new();
        private readonly object _queueLock = new();
        private readonly FileService _fileService;
        private readonly FfprobeService _ffprobeService;
        private readonly IHubContext<TranscodingHub> _hubContext;
        private readonly string _ffmpegPath;
        private readonly SemaphoreSlim _processingLock = new(1, 1);
        private Process? _activeProcess;
        private WorkItem? _activeWorkItem;

        public TranscodingService(FileService fileService, FfprobeService ffprobeService, IHubContext<TranscodingHub> hubContext)
        {
            _fileService = fileService;
            _ffprobeService = ffprobeService;
            _hubContext = hubContext;
            _ffmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH") ?? "ffmpeg";
        }

        public async Task<string> AddFileAsync(string filePath, EncoderOptions options)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var probe = await _ffprobeService.ProbeAsync(filePath);

                var length = _ffprobeService.GetVideoDuration(probe);
                var isHevc = false;

                foreach (var stream in probe.streams)
                {
                    if (stream.codec_type == "video")
                    {
                        isHevc = stream.codec_name == "hevc";
                        break;
                    }
                }

                // Calculate bitrate in kbps from file size and duration
                long bitrate = length > 0 ? (long)(fileInfo.Length * 8 / length / 1000) : 0;

                var workItem = new WorkItem
                {
                    FileName = _fileService.GetFileName(filePath),
                    Path = filePath,
                    Size = fileInfo.Length,
                    Bitrate = bitrate,
                    Length = length,
                    IsHevc = isHevc,
                    Probe = probe
                };

                // Don't add items that already meet the requirements.
                // If the target codec is HEVC, non-HEVC files always get queued for re-encoding.
                // If the target codec is H.264, non-H.264 files still get queued (likely already HEVC, skip those only if below target).
                bool targetIsHevc = options.Encoder.Contains("265");
                bool alreadyTargetCodec = targetIsHevc ? isHevc : !isHevc; // H.264 check: !isHevc means it's likely H.264/other
                bool isHighDef = probe.streams.Any(s => s.codec_type == "video" && s.width > 1920);

                if (alreadyTargetCodec && bitrate > 0 && bitrate <= options.TargetBitrate * 1.5 && !isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {(isHevc ? "HEVC" : "H.264")} at {bitrate}kbps (target {options.TargetBitrate}kbps)");
                    return workItem.Id;
                }

                if (alreadyTargetCodec && isHighDef && bitrate > 0 && bitrate <= options.TargetBitrate * 3)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: already {(isHevc ? "HEVC" : "H.264")} 4K at {bitrate}kbps (target {options.TargetBitrate}kbps)");
                    return workItem.Id;
                }

                // Skip low-bitrate non-HEVC files only when using VAAPI CQP — it can't target specific bitrates
                bool isVaapiMode = IsVaapiAcceleration(options.HardwareAcceleration);
                if (isVaapiMode && !isHevc && targetIsHevc && bitrate > 0 && bitrate <= options.TargetBitrate && !isHighDef)
                {
                    Console.WriteLine($"Skipping {workItem.FileName}: VAAPI can't compress {bitrate}kbps H.264 below target");
                    return workItem.Id;
                }

                Console.WriteLine($"Queuing {workItem.FileName}: {(isHevc ? "HEVC" : "H.264")} {bitrate}kbps {(isHighDef ? "4K" : "HD")}");

                // Skip if this file is already queued, processing, or completed
                var normalizedPath = Path.GetFullPath(filePath);
                if (_workItems.Values.Any(w =>
                    Path.GetFullPath(w.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    w.Status is WorkItemStatus.Pending or WorkItemStatus.Processing or WorkItemStatus.Completed))
                {
                    return workItem.Id;
                }

                _workItems[workItem.Id] = workItem;
                lock (_queueLock)
                {
                    _workQueue.Add(workItem);
                    _workQueue.Sort((a, b) => b.Bitrate.CompareTo(a.Bitrate));
                }

                await _hubContext.Clients.All.SendAsync("WorkItemAdded", workItem);

                // Always try to start queue processing — the semaphore ensures only one runs at a time
                _ = Task.Run(() => ProcessQueueAsync(options));

                return workItem.Id;
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to add file: {ex.Message}", ex);
            }
        }

        public async Task<string> AddDirectoryAsync(string directoryPath, EncoderOptions options, bool recursive = true)
        {
            List<string> directories;
            if (recursive)
            {
                directories = _fileService.RecursivelyFindDirectories(directoryPath);
            }
            else
            {
                directories = new List<string> { directoryPath };
            }
            var videoFiles = _fileService.GetAllVideoFiles(directories);

            // Probe files sequentially to avoid overwhelming NAS storage.
            // Each file triggers queue processing via AddFileAsync, so encoding
            // starts as soon as the first file is scanned — no waiting for the full scan.
            int addedCount = 0;
            foreach (var file in videoFiles)
            {
                try
                {
                    await AddFileAsync(file, options);
                    addedCount++;
                }
                catch (Exception ex) { Console.WriteLine($"Failed to add {file}: {ex.Message}"); }
            }

            return $"Added {addedCount} files from directory";
        }

        public WorkItem? GetWorkItem(string id)
        {
            _workItems.TryGetValue(id, out var workItem);
            return workItem;
        }

        public List<WorkItem> GetAllWorkItems()
        {
            return _workItems.Values.OrderByDescending(x => x.CreatedAt).ToList();
        }

        public async Task CancelWorkItemAsync(string id)
        {
            if (!_workItems.TryGetValue(id, out var workItem))
                return;

            if (workItem.Status == WorkItemStatus.Pending)
            {
                workItem.Status = WorkItemStatus.Cancelled;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            }
            else if (workItem.Status == WorkItemStatus.Processing && _activeWorkItem?.Id == id)
            {
                // Kill the active FFmpeg process
                try
                {
                    if (_activeProcess != null && !_activeProcess.HasExited)
                    {
                        _activeProcess.Kill(entireProcessTree: true);
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Encoding cancelled by user.");
                    }
                }
                catch (Exception ex)
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Error killing process: {ex.Message}");
                }

                workItem.Status = WorkItemStatus.Cancelled;
                workItem.CompletedAt = DateTime.UtcNow;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
            }
        }

        private async Task ProcessQueueAsync(EncoderOptions options)
        {
            if (!await _processingLock.WaitAsync(100))
                return; // Already processing

            try
            {
                while (true)
                {
                    WorkItem? workItem = null;
                    lock (_queueLock)
                    {
                        if (_workQueue.Count == 0) break;
                        workItem = _workQueue[0];
                        _workQueue.RemoveAt(0);
                    }

                    if (workItem.Status == WorkItemStatus.Cancelled)
                        continue;

                    // Clone options so retries don't mutate settings for subsequent queue items
                    var itemOptions = new EncoderOptions
                    {
                        Format = options.Format,
                        Codec = options.Codec,
                        Encoder = options.Encoder,
                        TargetBitrate = options.TargetBitrate,
                        TwoChannelAudio = options.TwoChannelAudio,
                        DeleteOriginalFile = options.DeleteOriginalFile,
                        EnglishOnlyAudio = options.EnglishOnlyAudio,
                        EnglishOnlySubtitles = options.EnglishOnlySubtitles,
                        RemoveBlackBorders = options.RemoveBlackBorders,
                        RetryOnFail = options.RetryOnFail,
                        OutputDirectory = options.OutputDirectory,
                        EncodeDirectory = options.EncodeDirectory,
                        StrictBitrate = options.StrictBitrate,
                        HardwareAcceleration = options.HardwareAcceleration
                    };
                    await ProcessWorkItemAsync(workItem, itemOptions);
                }
            }
            finally
            {
                _processingLock.Release();
            }
        }

        private async Task ProcessWorkItemAsync(WorkItem workItem, EncoderOptions options)
        {
            _activeWorkItem = workItem;
            try
            {
                workItem.Status = WorkItemStatus.Processing;
                workItem.StartedAt = DateTime.UtcNow;
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);

                await ConvertVideoAsync(workItem, options);

                workItem.Status = WorkItemStatus.Completed;
                workItem.CompletedAt = DateTime.UtcNow;
                workItem.Progress = 100;
            }
            catch (OperationCanceledException)
            {
                // Cancelled — status already set by CancelWorkItemAsync, just clean up output
                var outputPath = GetOutputPath(workItem, options);
                try { await _fileService.FileDeleteAsync(outputPath); } catch { }
            }
            catch (Exception ex)
            {
                workItem.Status = WorkItemStatus.Failed;
                workItem.ErrorMessage = ex.Message;
                workItem.CompletedAt = DateTime.UtcNow;
            }
            finally
            {
                _activeWorkItem = null;
                _activeProcess = null;
            }

            await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
        }

        private async Task ConvertVideoAsync(WorkItem workItem, EncoderOptions options, bool stripSubtitles = false)
        {
            if (workItem.Probe == null)
                throw new Exception("No probe data available");

            // Resolve "auto" to a concrete hardware type before building the command
            await ResolveHardwareAccelerationAsync(options);
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Hardware acceleration: {options.HardwareAcceleration}");
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Current Bitrate: {workItem.Bitrate}kbps");

            var (targetBitrate, minBitrate, maxBitrate, videoCopy) = CalculateBitrates(workItem, options);

            // Resolve the actual encoder — may fall back to software if VAAPI doesn't support this codec
            string encoder = videoCopy ? "copy" : GetEncoder(options);
            bool useVaapi = !videoCopy && encoder.Contains("vaapi");

            // VAAPI on Elkhart Lake (J6412) only supports CQP reliably.
            // CQP is content-dependent — same QP gives wildly different bitrates per content.
            // Do a 30-second test encode to measure actual output, then adjust QP to hit target.
            string compressionFlags;
            if (videoCopy)
                compressionFlags = "";
            else if (useVaapi)
            {
                long targetKbps = long.Parse(targetBitrate.TrimEnd('k'));
                int quality = await CalibrateVaapiQualityAsync(workItem, options, workItem.Path, targetKbps);

                if (quality < 0)
                {
                    // VAAPI can't encode this file (10-bit, HDR, Dolby Vision, etc.)
                    // Software fallback on a NAS CPU would take days — skip the file
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                        "VAAPI cannot encode this file (likely 10-bit/HDR) — skipping. Use the desktop app for this file.");
                    throw new Exception("VAAPI incompatible — file requires 10-bit encoding which this hardware does not support");
                }
                else
                {
                    compressionFlags = $"-g 25 -rc_mode CQP -global_quality {quality} ";
                }
            }
            else
                compressionFlags = $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrate} ";

            string initFlags = useVaapi ? GetInitFlags(options.HardwareAcceleration) : GetInitFlags("none");
            string hwFilter = useVaapi ? "-vf format=nv12|vaapi,hwupload " : "";
            string presetFlag = useVaapi ? "-low_power 1 " : "-preset medium ";
            string videoFlags = videoCopy ?
                $"{_ffprobeService.MapVideo(workItem.Probe)} -c:v copy " :
                $"{_ffprobeService.MapVideo(workItem.Probe)} -c:v {encoder} {presetFlag}{hwFilter}";

            string audioFlags = _ffprobeService.MapAudio(workItem.Probe, options.EnglishOnlyAudio,
                options.TwoChannelAudio, options.Format == "mkv") + " ";

            // If retrying without subtitles, force -sn to strip all subtitle streams
            string subtitleFlags = stripSubtitles
                ? "-sn "
                : _ffprobeService.MapSub(workItem.Probe, options.EnglishOnlySubtitles, options.Format == "mkv") + " ";

            string varFlags = options.Format == "mkv" ? "-max_muxing_queue_size 9999 " : "-movflags +faststart -max_muxing_queue_size 9999 ";

            string outputPath = GetOutputPath(workItem, options);
            string inputPath = workItem.Path;

            // Verify source file exists
            if (!File.Exists(inputPath))
            {
                throw new Exception($"Source file not found: {inputPath}");
            }

            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                $"Encoding from: {inputPath}");
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                $"Output to: {outputPath}");

            // Handle crop filter properly
            if (options.RemoveBlackBorders)
            {
                var cropFilter = await GetCropParametersAsync(workItem, options, inputPath);
                if (!string.IsNullOrEmpty(cropFilter))
                {
                    if (videoCopy)
                    {
                        // Can't crop with copy, need to re-encode
                        videoCopy = false;
                        string cropHwFilter = useVaapi
                            ? $"-vf {cropFilter.Replace("-vf ", "")},format=nv12|vaapi,hwupload "
                            : $"{cropFilter} ";
                        videoFlags = $"{_ffprobeService.MapVideo(workItem.Probe)} -c:v {encoder} {presetFlag}{cropHwFilter}";
                        compressionFlags = useVaapi
                            ? $"-g 25 -rc_mode CQP -global_quality 25 "
                            : $"-g 25 -b:v {targetBitrate} -minrate {minBitrate} -maxrate {maxBitrate} -bufsize {maxBitrate} ";
                    }
                    else
                    {
                        string cropHwFilter = useVaapi
                            ? $"-vf {cropFilter.Replace("-vf ", "")},format=nv12|vaapi,hwupload "
                            : $"{cropFilter} ";
                        videoFlags = $"{_ffprobeService.MapVideo(workItem.Probe)} -c:v {encoder} {presetFlag}{cropHwFilter}";
                    }
                }
            }

            // -analyzeduration and -probesize handle files with many streams (e.g. 30+ PGS subtitle tracks)
            string analyzeFlags = "-analyzeduration 10M -probesize 50M ";
            string command = $"{initFlags} {analyzeFlags}-i \"{inputPath}\" {videoFlags}{compressionFlags}{audioFlags}{subtitleFlags}" +
                           $"{varFlags}-f {(options.Format == "mkv" ? "matroska" : "mp4")} \"{outputPath}\"";

            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Converting {workItem.FileName}");
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Command: ffmpeg {command}");

            var startTime = DateTime.Now;

            // Run FFmpeg — catch failures so we can retry and clean up
            try
            {
                await RunFfmpegAsync(command, workItem);
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (Exception ex)
            {
                await HandleConversionFailure(workItem, options, outputPath, ex.Message, stripSubtitles);
                return;
            }

            // Verify conversion
            await Task.Delay(5000); // Wait for file to be fully written

            if (!File.Exists(outputPath))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Output file not found", stripSubtitles);
                return;
            }

            var outputProbe = await _ffprobeService.ProbeAsync(outputPath);
            if (!_ffprobeService.ConvertedSuccessfully(workItem.Probe, outputProbe))
            {
                await HandleConversionFailure(workItem, options, outputPath, "Duration mismatch detected", stripSubtitles);
                return;
            }

            // Calculate savings
            var outputSize = new FileInfo(outputPath).Length;
            float savings = (workItem.Size - outputSize) / 1048576f;
            float percent = 1 - ((float)outputSize / workItem.Size);

            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                $"Converted successfully in {DateTime.Now.Subtract(startTime).TotalMinutes:0.00} minutes.");

            if (savings > 0 || videoCopy)
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                    $"{savings:0,0}mb / {percent:P} saved.");

                // Handle output file placement
                await HandleOutputPlacement(outputPath, workItem, options);
            }
            else
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                    "No savings realized. Deleting conversion.");

                try { await _fileService.FileDeleteAsync(outputPath); }
                catch (Exception ex)
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Error cleaning up output: {ex.Message}");
                }
            }
        }

        private (string target, string min, string max, bool copy) CalculateBitrates(WorkItem workItem, EncoderOptions options)
        {
            bool videoCopy = false;
            string targetBitrate, minBitrate, maxBitrate;

            if (options.StrictBitrate)
            {
                targetBitrate = $"{options.TargetBitrate}k";
                minBitrate = targetBitrate;
                maxBitrate = targetBitrate;
            }
            else if (workItem.Probe!.streams.Any(x => x.width > 1920)) // 4k
            {
                int hdBitrate = options.TargetBitrate * 4;
                targetBitrate = $"{hdBitrate}k";
                minBitrate = $"{hdBitrate - 500}k";
                maxBitrate = $"{hdBitrate + 1000}k";
            }
            else if (workItem.Bitrate < options.TargetBitrate + 700 && !workItem.IsHevc)
            {
                targetBitrate = $"{(int)(workItem.Bitrate * 0.7)}k";
                minBitrate = $"{(int)(workItem.Bitrate * 0.6)}k";
                maxBitrate = $"{(int)(workItem.Bitrate * 0.8)}k";
            }
            else
            {
                // Never encode higher than the source bitrate — cap to whichever is lower
                int effectiveTarget = workItem.Bitrate > 0
                    ? (int)Math.Min(options.TargetBitrate, workItem.Bitrate)
                    : options.TargetBitrate;
                targetBitrate = $"{effectiveTarget}k";
                minBitrate = $"{effectiveTarget - 200}k";
                maxBitrate = $"{effectiveTarget + 500}k";
            }

            // If bitrate is already below target and using HEVC, copy instead
            if (workItem.Bitrate < options.TargetBitrate + 700 && workItem.IsHevc && !options.RemoveBlackBorders)
            {
                videoCopy = true;
            }

            return (targetBitrate, minBitrate, maxBitrate, videoCopy);
        }

        private string? _detectedHardware = null;

        /// <summary>
        /// Detects available hardware acceleration by testing encoders.
        /// Result is cached after first detection.
        /// </summary>
        private async Task<string> DetectHardwareAccelerationAsync()
        {
            if (_detectedHardware != null)
                return _detectedHardware;

            // Windows GPU detection
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.WriteLine("Auto-detect: Running on Windows, testing GPU encoders...");

                // Test NVIDIA NVENC
                if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
                {
                    Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                    _detectedHardware = "nvidia";
                    return _detectedHardware;
                }

                // Test Intel QSV
                if (await TestEncoderAsync("-hwaccel qsv -qsv_device auto", "hevc_qsv"))
                {
                    Console.WriteLine("Auto-detect: Intel QSV available");
                    _detectedHardware = "intel";
                    return _detectedHardware;
                }

                // Test AMD AMF
                if (await TestEncoderAsync("-hwaccel auto", "hevc_amf"))
                {
                    Console.WriteLine("Auto-detect: AMD AMF available");
                    _detectedHardware = "amd";
                    return _detectedHardware;
                }

                Console.WriteLine("Auto-detect: No hardware acceleration available on Windows, using software");
                _detectedHardware = "none";
                return _detectedHardware;
            }

            // Linux: Log VAAPI diagnostics
            await LogVaapiInfoAsync();

            // Test VAAPI (Intel and AMD GPUs on Linux)
            // Try both iHD and i965 drivers — QNAP systems may need either one
            if (File.Exists("/dev/dri/renderD128"))
            {
                var driversToTry = new[] { "iHD", "i965" };
                foreach (var driver in driversToTry)
                {
                    Console.WriteLine($"Auto-detect: Trying VAAPI with {driver} driver...");
                    Environment.SetEnvironmentVariable("LIBVA_DRIVER_NAME", driver);

                    var hwInit = "-init_hw_device vaapi=hw:/dev/dri/renderD128 -filter_hw_device hw";
                    bool hevcOk = await TestEncoderAsync(hwInit, "hevc_vaapi");
                    bool h264Ok = await TestEncoderAsync(hwInit, "h264_vaapi");

                    if (hevcOk || h264Ok)
                    {
                        Console.WriteLine($"Auto-detect: VAAPI available with {driver} driver (hevc={hevcOk}, h264={h264Ok})");
                        _detectedHardware = "intel";
                        return _detectedHardware;
                    }
                }
            }

            // Test NVIDIA
            if (await TestEncoderAsync("-hwaccel cuda", "hevc_nvenc"))
            {
                Console.WriteLine("Auto-detect: NVIDIA NVENC available");
                _detectedHardware = "nvidia";
                return _detectedHardware;
            }

            Console.WriteLine("Auto-detect: No hardware acceleration available, using software");
            _detectedHardware = "none";
            return _detectedHardware;
        }

        /// <summary>
        /// Logs VAAPI diagnostic info (vainfo output) for troubleshooting.
        /// </summary>
        private async Task LogVaapiInfoAsync()
        {
            // VAAPI is Linux-only — skip entirely on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                // Check device exists and permissions
                if (File.Exists("/dev/dri/renderD128"))
                    Console.WriteLine("Auto-detect: /dev/dri/renderD128 exists");
                else
                {
                    Console.WriteLine("Auto-detect: /dev/dri/renderD128 NOT FOUND");
                    // List what's in /dev/dri
                    if (Directory.Exists("/dev/dri"))
                    {
                        var entries = Directory.GetFileSystemEntries("/dev/dri");
                        Console.WriteLine($"Auto-detect: /dev/dri contents: {string.Join(", ", entries)}");
                    }
                    else
                        Console.WriteLine("Auto-detect: /dev/dri directory does not exist");
                    return;
                }

                // Run vainfo for diagnostics
                var psi = new ProcessStartInfo("vainfo")
                {
                    Arguments = "--display drm --device /dev/dri/renderD128",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.Environment["LIBVA_DRIVER_NAME"] = Environment.GetEnvironmentVariable("LIBVA_DRIVER_NAME") ?? "iHD";

                using var process = new Process { StartInfo = psi };
                process.Start();
                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                process.WaitForExit(5000);

                var output = !string.IsNullOrEmpty(stdout) ? stdout : stderr;
                Console.WriteLine($"Auto-detect vainfo output:\n{output.Substring(0, Math.Min(output.Length, 1000))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Auto-detect: vainfo failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Tests whether a hardware encoder is functional by running a minimal encode.
        /// </summary>
        private async Task<bool> TestEncoderAsync(string hwFlags, string encoder)
        {
            try
            {
                // Test with the same flags used in actual encoding: CQP mode with low-power for VAAPI
                string vf, extra;
                if (encoder.Contains("vaapi"))
                {
                    vf = "-vf format=nv12|vaapi,hwupload";
                    extra = "-low_power 1 -rc_mode CQP -global_quality 25";
                }
                else
                {
                    vf = "";
                    extra = "";
                }
                var args = $"-y {hwFlags} -f lavfi -i color=c=black:s=256x256:d=0.1 {vf} -c:v {encoder} {extra} -frames:v 1 -f null -";
                var psi = new ProcessStartInfo(_ffmpegPath)
                {
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                var stderr = await process.StandardError.ReadToEndAsync();

                // Give it 10 seconds max
                var completed = process.WaitForExit(10000);
                if (!completed)
                {
                    process.Kill();
                    Console.WriteLine($"Auto-detect: {encoder} test timed out");
                    return false;
                }

                Console.WriteLine($"Auto-detect: {encoder} test exit={process.ExitCode}");
                if (process.ExitCode != 0)
                {
                    // Get last 500 chars of stderr (actual error is at the end, not the build config at the start)
                    var errTail = stderr.Length > 500 ? stderr.Substring(stderr.Length - 500) : stderr;
                    Console.WriteLine($"Auto-detect: {encoder} stderr (tail): {errTail}");
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Resolves "auto" to a concrete hardware acceleration type.
        /// For explicit selections, returns as-is.
        /// </summary>
        private async Task ResolveHardwareAccelerationAsync(EncoderOptions options)
        {
            if (options.HardwareAcceleration.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                options.HardwareAcceleration = await DetectHardwareAccelerationAsync();
            }
        }

        private bool IsVaapiAcceleration(string hardwareAcceleration)
        {
            // VAAPI only exists on Linux — never use VAAPI paths on Windows
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return false;

            return hardwareAcceleration.ToLower() is "intel" or "amd";
        }

        private string GetInitFlags(string hardwareAcceleration)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            return hardwareAcceleration.ToLower() switch
            {
                "intel" when isWindows => "-y -hwaccel qsv -qsv_device auto",
                "amd" when isWindows => "-y -hwaccel auto",
                "intel" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "amd" => "-y -init_hw_device vaapi=hw:/dev/dri/renderD128 -hwaccel vaapi -hwaccel_output_format vaapi -filter_hw_device hw",
                "nvidia" => "-y -hwaccel cuda",
                _ => "-y"
            };
        }

        private string GetEncoder(EncoderOptions options)
        {
            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            return options.HardwareAcceleration.ToLower() switch
            {
                "intel" when isWindows && options.Encoder.Contains("265") => "hevc_qsv",
                "intel" when isWindows && options.Encoder.Contains("264") => "h264_qsv",
                "amd" when isWindows && options.Encoder.Contains("265") => "hevc_amf",
                "amd" when isWindows && options.Encoder.Contains("264") => "h264_amf",
                "intel" when options.Encoder.Contains("265") => "hevc_vaapi",
                "intel" when options.Encoder.Contains("264") => "h264_vaapi",
                "amd" when options.Encoder.Contains("265") => "hevc_vaapi",
                "amd" when options.Encoder.Contains("264") => "h264_vaapi",
                "nvidia" when options.Encoder.Contains("265") => "hevc_nvenc",
                "nvidia" when options.Encoder.Contains("264") => "h264_nvenc",
                _ => options.Encoder
            };
        }

        private string GetOutputPath(WorkItem workItem, EncoderOptions options)
        {
            string fileName = _fileService.RemoveExtension(workItem.FileName);
            string extension = options.Format == "mkv" ? ".mkv" : ".mp4";
            string snacksName = $"{fileName} [snacks]{extension}";

            if (!string.IsNullOrEmpty(options.EncodeDirectory))
            {
                return Path.Combine(options.EncodeDirectory, snacksName);
            }
            else if (!string.IsNullOrEmpty(options.OutputDirectory))
            {
                return Path.Combine(options.OutputDirectory, snacksName);
            }
            else
            {
                string originalDir = _fileService.GetDirectory(workItem.Path);
                return Path.Combine(originalDir, snacksName);
            }
        }

        /// <summary>
        /// Returns the final "clean" path (without [snacks] tag) for output placement.
        /// </summary>
        private string GetCleanOutputName(string snacksPath)
        {
            string dir = Path.GetDirectoryName(snacksPath) ?? "";
            string fileName = Path.GetFileNameWithoutExtension(snacksPath).Replace(" [snacks]", "");
            string extension = Path.GetExtension(snacksPath);
            return Path.Combine(dir, fileName + extension);
        }

        /// <summary>
        /// Does iterative 30-second test encodes to find the right QP for the target bitrate.
        /// Starts at QP 24, measures output, adjusts, and retests until within 15% of target
        /// or max 3 iterations.
        /// </summary>
        private async Task<int> CalibrateVaapiQualityAsync(WorkItem workItem, EncoderOptions options, string inputPath, long targetKbps)
        {
            int currentQp = 24;
            int testDuration = 30;
            int maxIterations = 3;
            double tolerance = 0.15; // within 15% of target

            // Seek to 25% into the file for a representative sample
            int seekSeconds = Math.Max(0, (int)(workItem.Length * 0.25));
            string seekTime = $"{seekSeconds / 3600:D2}:{(seekSeconds % 3600) / 60:D2}:{seekSeconds % 60:D2}";

            string initFlags = GetInitFlags(options.HardwareAcceleration);
            string encoder = GetEncoder(options);
            string hwFilter = "-vf format=nv12|vaapi,hwupload";

            for (int iteration = 1; iteration <= maxIterations; iteration++)
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                    $"Calibration pass {iteration}/{maxIterations} — testing QP {currentQp}...");

                long measuredKbps = await RunTestEncodeAsync(inputPath, initFlags, encoder, hwFilter, currentQp, seekTime, testDuration);

                if (measuredKbps <= 0)
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                        "VAAPI produced no measurable output — encoder incompatible with this file");
                    return -1; // Signal to fall back to software
                }

                // Detect absurd output (more than 5x source bitrate = encoder broken for this file)
                if (workItem.Bitrate > 0 && measuredKbps > workItem.Bitrate * 5)
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                        $"VAAPI output ({measuredKbps}kbps) is absurdly high — encoder broken for this file");
                    return -1;
                }

                double ratio = (double)measuredKbps / targetKbps;
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                    $"Pass {iteration}: QP {currentQp} → {measuredKbps}kbps (target {targetKbps}kbps, ratio {ratio:F2}x)");

                // Close enough — within tolerance
                if (ratio >= (1 - tolerance) && ratio <= (1 + tolerance))
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                        $"QP {currentQp} is within {tolerance:P0} of target. Using QP {currentQp}.");
                    return currentQp;
                }

                // Already below target — don't compress further
                if (measuredKbps <= targetKbps)
                {
                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                        $"QP {currentQp} already below target. Using QP {currentQp}.");
                    return currentQp;
                }

                // Calculate adjustment: each +2 QP ≈ 0.72x bitrate
                double qpDelta = 2.0 * Math.Log((double)targetKbps / measuredKbps) / Math.Log(0.72);
                int adjustment = (int)Math.Round(qpDelta);
                if (adjustment == 0) adjustment = measuredKbps > targetKbps ? 1 : -1;

                currentQp = Math.Clamp(currentQp + adjustment, 18, 40);
            }

            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                $"Calibration complete after {maxIterations} passes. Using QP {currentQp}.");
            return currentQp;
        }

        private async Task<long> RunTestEncodeAsync(string inputPath, string initFlags, string encoder, string hwFilter, int qp, string seekTime, int duration)
        {
            string command = $"{initFlags} -ss {seekTime} -i \"{inputPath}\" -t {duration} " +
                $"-c:v {encoder} -low_power 1 {hwFilter} -g 25 -rc_mode CQP -global_quality {qp} " +
                $"-an -sn -f null -";

            try
            {
                var psi = new ProcessStartInfo(_ffmpegPath)
                {
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = psi };
                var output = new System.Text.StringBuilder();

                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) output.AppendLine(e.Data);
                };

                process.Start();
                process.BeginErrorReadLine();
                await process.StandardOutput.ReadToEndAsync();

                var completed = process.WaitForExit(120000);
                if (!completed)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return -1;
                }

                var sizeMatch = Regex.Match(output.ToString(), @"video:\s*(\d+)\s*kB");
                if (sizeMatch.Success)
                {
                    long outputKb = long.Parse(sizeMatch.Groups[1].Value);
                    return outputKb * 8 / duration; // kbps
                }
            }
            catch { }

            return -1;
        }

        private async Task<string> GetCropParametersAsync(WorkItem workItem, EncoderOptions options, string inputPath)
        {
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Getting crop values.");

            int lengthInMinutes = (int)workItem.Length / 60;
            string startTime = lengthInMinutes > 20 ? "00:10:00" : "00:00:00";
            string duration = lengthInMinutes > 20 ? "00:10:00" : $"00:{Math.Min(lengthInMinutes, 10):D2}:00";
            
            string command = $"{GetInitFlags(options.HardwareAcceleration)} -ss {startTime} -i \"{inputPath}\" " +
                           $"-t {duration} -vf cropdetect=24:2:8 -f null -";

            var cropValues = new ConcurrentDictionary<string, int>();

            var processStartInfo = new ProcessStartInfo(_ffmpegPath)
            {
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null && e.Data.Contains("crop="))
                {
                    var match = Regex.Match(e.Data, @"crop=([^\s]+)");
                    if (match.Success)
                    {
                        string crop = match.Groups[1].Value;
                        cropValues.AddOrUpdate(crop, 1, (_, count) => count + 1);
                    }
                }
            };

            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Timeout cropdetect at 3 minutes to prevent hangs
            var completed = process.WaitForExit(180000);
            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Crop detection timed out, skipping.");
                return "";
            }

            if (cropValues.Count == 0)
                return "";

            string mostCommonCrop = cropValues.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Detected crop: {mostCommonCrop}");
            return $"-vf crop={mostCommonCrop}";
        }

        private async Task RunFfmpegAsync(string command, WorkItem workItem)
        {
            var processStartInfo = new ProcessStartInfo(_ffmpegPath)
            {
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            _activeProcess = process;
            var errorOutput = new ConcurrentQueue<string>();
            var lastProgressUpdate = DateTime.MinValue;
            var lastReportedProgress = -1;
            var lastActivity = DateTime.UtcNow;
            const int stallTimeoutSeconds = 30;

            process.Start();

            // Read stderr manually — FFmpeg uses \r for progress lines which
            // BeginErrorReadLine() doesn't split on Linux .NET
            _ = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[4096];
                    var lineBuilder = new System.Text.StringBuilder();
                    var stream = process.StandardError;

                    while (!stream.EndOfStream)
                    {
                        int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        for (int i = 0; i < read; i++)
                        {
                            if (buffer[i] == '\r' || buffer[i] == '\n')
                            {
                                if (lineBuilder.Length > 0)
                                {
                                    var line = lineBuilder.ToString();
                                    lineBuilder.Clear();

                                    errorOutput.Enqueue(line);
                                    lastActivity = DateTime.UtcNow;

                                    try
                                    {
                                        if (line.Contains("time=") && workItem.Length > 0)
                                        {
                                            var match = Regex.Match(line, @"time=(\d{2}:\d{2}:\d{2}\.\d{2,})");
                                            if (match.Success)
                                            {
                                                var timeStr = match.Groups[1].Value;
                                                var seconds = _ffprobeService.DurationStringToSeconds(timeStr);
                                                var progress = (int)Math.Clamp(Math.Round(seconds / workItem.Length * 100), 0, 99);

                                                workItem.Progress = progress;

                                                var now = DateTime.UtcNow;
                                                if ((now - lastProgressUpdate).TotalSeconds >= 2)
                                                {
                                                    lastProgressUpdate = now;
                                                    await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                                                    await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"FFmpeg: {line}");
                                                }
                                            }
                                        }
                                        else if (!string.IsNullOrWhiteSpace(line))
                                        {
                                            // Forward non-progress lines (errors, warnings, info)
                                            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"FFmpeg: {line}");
                                        }
                                    }
                                    catch { }
                                }
                            }
                            else
                            {
                                lineBuilder.Append(buffer[i]);
                            }
                        }
                    }
                }
                catch { }
            });

            // Discard stdout
            _ = Task.Run(async () =>
            {
                try { await process.StandardOutput.ReadToEndAsync(); } catch { }
            });

            // Wait for exit but kill the process if it stalls (no output for stallTimeoutSeconds)
            var exitTask = process.WaitForExitAsync();
            while (!exitTask.IsCompleted)
            {
                var winner = await Task.WhenAny(exitTask, Task.Delay(30000));
                if (winner != exitTask && !process.HasExited)
                {
                    // Still running — check for stall
                    if ((DateTime.UtcNow - lastActivity).TotalSeconds >= stallTimeoutSeconds)
                    {
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                            $"FFmpeg stalled (no output for {stallTimeoutSeconds} seconds). Killing process.");
                        try { process.Kill(entireProcessTree: true); } catch { }
                        await exitTask; // Wait for kill to complete
                        break;
                    }
                }
            }

            _activeProcess = null;

            if (workItem.Status == WorkItemStatus.Cancelled)
            {
                throw new OperationCanceledException("Encoding was cancelled.");
            }

            if (process.ExitCode != 0)
            {
                var errorText = string.Join("\n", errorOutput.ToArray().TakeLast(10));
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"FFmpeg failed with exit code {process.ExitCode}");
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Last error lines:\n{errorText}");
                throw new Exception($"FFmpeg exited with code {process.ExitCode}. Error: {errorText}");
            }
        }

        private async Task HandleConversionFailure(WorkItem workItem, EncoderOptions options, string outputPath, string reason, bool subtitlesWereStripped)
        {
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Conversion failed: {reason}");

            // Clean up the failed/partial output file
            try
            {
                await _fileService.FileDeleteAsync(outputPath);
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Cleaned up failed output file.");
            }
            catch (Exception ex)
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Warning: Could not clean up output file: {ex.Message}");
            }

            // Retry 1: Strip all subtitles (covers bitmap subs, broken streams, etc.)
            if (!subtitlesWereStripped)
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Retrying without subtitles...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                await ConvertVideoAsync(workItem, options, stripSubtitles: true);
                return;
            }

            // Retry 2: Fall back to software encoding (resets subtitle stripping to try subs first on software)
            if (options.RetryOnFail && !options.Encoder.Contains("libx265"))
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Retrying with software encoding...");
                workItem.Progress = 0;
                await _hubContext.Clients.All.SendAsync("WorkItemUpdated", workItem);
                options.Encoder = "libx265";
                options.HardwareAcceleration = "none";
                await ConvertVideoAsync(workItem, options, stripSubtitles: false);
                return;
            }

            // All retries exhausted — original file is untouched
            await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "All retries exhausted. Original file is unchanged.");
            throw new Exception($"Conversion failed after retries: {reason}");
        }

        private async Task HandleOutputPlacement(string outputPath, WorkItem workItem, EncoderOptions options)
        {
            try
            {
                if (!string.IsNullOrEmpty(options.OutputDirectory))
                {
                    // Output already in the right directory (GetOutputPath used OutputDirectory)
                    // If EncodeDirectory was used, move from there to OutputDirectory
                    if (!string.IsNullOrEmpty(options.EncodeDirectory))
                    {
                        string finalSnacksPath = Path.Combine(options.OutputDirectory, Path.GetFileName(outputPath));
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Moving to output directory: {finalSnacksPath}");
                        await _fileService.FileMoveAsync(outputPath, finalSnacksPath);
                        outputPath = finalSnacksPath;
                    }

                    if (options.DeleteOriginalFile)
                    {
                        // Delete original and remove [snacks] tag from output
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Deleting original file");
                        await _fileService.FileDeleteAsync(workItem.Path);

                        string cleanPath = GetCleanOutputName(outputPath);
                        await _fileService.FileMoveAsync(outputPath, cleanPath);
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Final output: {cleanPath}");
                    }
                    else
                    {
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                            $"Original kept at: {workItem.Path}");
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                            $"Transcoded file at: {outputPath}");
                    }
                }
                else
                {
                    // In-place processing — output is in the same directory as the original with [snacks] tag
                    if (options.DeleteOriginalFile)
                    {
                        // Delete original and rename transcoded file to take its place (without [snacks] tag)
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, "Deleting original and replacing with transcoded version");
                        await _fileService.FileDeleteAsync(workItem.Path);

                        string cleanPath = GetCleanOutputName(outputPath);
                        await _fileService.FileMoveAsync(outputPath, cleanPath);
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Final output: {cleanPath}");
                    }
                    else
                    {
                        // Keep both — original untouched, transcoded file has [snacks] tag
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                            $"Original kept at: {workItem.Path}");
                        await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id,
                            $"Transcoded file at: {outputPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                await _hubContext.Clients.All.SendAsync("TranscodingLog", workItem.Id, $"Error handling output placement: {ex.Message}");
                throw;
            }
        }
    }
}