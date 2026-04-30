using System.Text.RegularExpressions;
using FluentAssertions;
using Snacks.Models;
using Snacks.Services;
using Snacks.Tests.Fixtures;
using Xunit;
using Stream = Snacks.Models.Stream;

namespace Snacks.Tests.Pipeline;

/// <summary>
///     End-to-end scenario tests: drive the same helpers <c>ConvertVideoAsync</c> uses
///     (<see cref="FfprobeService.MapVideo"/>, <see cref="FfprobeService.MapAudio"/>,
///     <see cref="FfprobeService.MapSub"/>, <see cref="TranscodingService.GetEncoder"/>,
///     <see cref="TranscodingService.GetInitFlags"/>,
///     <see cref="TranscodingService.CalculateBitrates"/>,
///     <see cref="TranscodingService.GetForcedReencodeCompressionFlags"/>) through
///     <see cref="TranscodingService.BuildFfmpegCommand"/> and assert the resulting
///     full command string is well-formed and carries the right semantic flags.
///
///     Tests assert via <c>Contain</c> on key fragments rather than full-string
///     equality so legitimate refactors don't churn the suite, but the assertions
///     are tight enough to catch a missing flag, wrong codec name, or reordered
///     argument that would change ffmpeg's behavior.
/// </summary>
public sealed class FullCommandScenarioTests
{
    private readonly FfprobeService _ffprobe = new();


    // ---------------------------------------------------------------------
    //  Generic structural assertions every well-formed command must satisfy.
    // ---------------------------------------------------------------------

    private static void AssertWellFormed(string command, string format, string inputPath, string outputPath)
    {
        // The command must:
        //  1. Begin with the hwaccel init token.
        command.Should().Contain("-y");

        //  2. Carry the input path (quoted).
        command.Should().Contain($"\"{inputPath}\"");

        //  3. Carry the output path (quoted) at the very end.
        command.TrimEnd().Should().EndWith($"\"{outputPath}\"");

        //  4. Specify the muxer with -f.
        var expectedMuxer = format == "mkv" ? "matroska" : "mp4";
        command.Should().Contain($"-f {expectedMuxer}");

        //  5. The -f muxer flag comes BEFORE the output path (ffmpeg arg order requirement).
        var muxerIdx  = command.IndexOf($"-f {expectedMuxer}", StringComparison.Ordinal);
        var outputIdx = command.LastIndexOf($"\"{outputPath}\"", StringComparison.Ordinal);
        muxerIdx.Should().BeLessThan(outputIdx);

        //  6. The input -i path comes before the output path.
        var inputIdx = command.IndexOf($"-i \"{inputPath}\"", StringComparison.Ordinal);
        inputIdx.Should().BeGreaterThan(-1);
        inputIdx.Should().BeLessThan(outputIdx);

        //  7. Mux queue size bumped (load-bearing for sources with many streams).
        command.Should().Contain("-max_muxing_queue_size 9999");

        //  8. MP4-specific faststart flag presence/absence matches the format.
        if (format == "mp4")
            command.Should().Contain("-movflags +faststart");
        else
            command.Should().NotContain("-movflags +faststart");
    }


    /// <summary>
    ///     Builds a command from the configured options + a probe, mirroring the helper
    ///     sequence <c>ConvertVideoAsync</c> uses end-to-end:
    ///     init flags → video map + encoder + preset + <c>-vf</c> filter chain →
    ///     compression flags → audio plan → subtitle plan → final assembly.
    ///     This is the test scaffolding's contract with the production code path; if a
    ///     production refactor changes the order or skips a step, scenarios will diverge
    ///     visibly from <c>ConvertVideoAsync</c>'s output.
    /// </summary>
    private string BuildScenarioCommand(
        ProbeResult     probe,
        EncoderOptions  options,
        WorkItem        workItem,
        string          inputPath        = "/source/in.mkv",
        string          outputPath       = "/output/out.mkv",
        bool            videoCopy        = false,
        bool            stripSubs        = false,
        bool            includeBitmapSubs = false,
        string?         cropExpr         = null)
    {
        var encoder = videoCopy ? "copy" : TranscodingService.GetEncoder(options);
        bool useVaapi = encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        bool isSvtAv1 = encoder == "libsvtav1";

        // VAAPI's init flags depend on whether SW decode is forced — production lowers it
        // when filters are active. Mirror that here so scenarios with filters get the right init.
        bool isHdr     = FfprobeService.IsHdr(probe);
        var  scaleExpr = videoCopy ? null : TranscodingService.ComputeScaleExpr(workItem, options);
        bool tonemap   = !videoCopy && options.TonemapHdrToSdr && isHdr;
        bool hasFilter = cropExpr != null || scaleExpr != null || tonemap;

        string init = useVaapi && hasFilter
            ? TranscodingService.GetInitFlags(options.HardwareAcceleration, hwDecode: false)
            : TranscodingService.GetInitFlags(options.HardwareAcceleration);

        var (target, min, max, _) = TranscodingService.CalculateBitrates(workItem, options);

        string videoMap = _ffprobe.MapVideo(probe);

        // Preset: SVT-AV1 takes a numeric preset, VAAPI takes none, others take the user string.
        string presetFlag = videoCopy
            ? ""
            : useVaapi
                ? ""
                : isSvtAv1
                    ? $"-preset {TranscodingService.MapSvtAv1Preset(options.FfmpegQualityPreset)} "
                    : $"-preset {options.FfmpegQualityPreset} ";

        // Build the -vf filter chain (or empty when nothing applies). This is the missing
        // wiring — without it scale/tonemap/crop wouldn't surface in the assembled command.
        string vfFlag = videoCopy
            ? ""
            : VideoFilterBuilder.Emit(
                  cropExpr:    cropExpr,
                  tonemap:     tonemap,
                  scaleExpr:   scaleExpr,
                  useVaapi:    useVaapi,
                  canHwDecode: useVaapi && !hasFilter,   // SW-decode when filters are active
                  vaapiFormat: tonemap ? "nv12" : "nv12");

        string videoFlags = videoCopy
            ? $"{videoMap} -c:v copy "
            : $"{videoMap} -c:v {encoder} {presetFlag}{vfFlag}";
        string compressionFlags = videoCopy
            ? ""
            : TranscodingService.GetForcedReencodeCompressionFlags(
                  encoder, useVaapi, isSvtAv1, target, min, max, useConservativeHwFlags: false) + " ";

        string audioFlags = _ffprobe.MapAudio(
            probe,
            options.AudioLanguagesToKeep,
            options.PreserveOriginalAudio,
            options.AudioOutputs,
            options.Format == "mkv",
            out _) + " ";

        string subtitleFlags = stripSubs
            ? "-sn "
            : _ffprobe.MapSub(
                  probe,
                  options.SubtitleLanguagesToKeep,
                  options.Format == "mkv",
                  includeBitmaps: includeBitmapSubs) + " ";

        return TranscodingService.BuildFfmpegCommand(
            format:           options.Format,
            initFlags:        init,
            analyzeFlags:     "-analyzeduration 10M -probesize 50M ",
            inputPath:        inputPath,
            extraInputs:      "",
            videoFlags:       videoFlags,
            compressionFlags: compressionFlags,
            audioFlags:       audioFlags,
            subtitleFlags:    subtitleFlags,
            outputPath:       outputPath);
    }


    // =====================================================================
    //  Scenario 1: HD H.264 source → HEVC software encode, MKV.
    //  English AC3 5.1 audio preserved, English subtitles preserved.
    //  Above-target bitrate so the source-cap branch fires.
    // =====================================================================

    [Fact]
    public void Scenario_HD_H264_to_HEVC_software_MKV()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Subtitle(codec: "subrip", lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                  = "mkv",
            Codec                   = "h265",
            Encoder                 = "libx265",
            FfmpegQualityPreset     = "medium",
            TargetBitrate           = 3500,
            HardwareAcceleration    = "none",
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new() { "en" },
            SubtitleLanguagesToKeep = new() { "en" },
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Video: software HEVC encode with the user's preset.
        cmd.Should().Contain("-map 0:0");
        cmd.Should().Contain("-c:v libx265");
        cmd.Should().Contain("-preset medium");

        // Software rate-control: -b:v / -minrate / -maxrate / -bufsize.
        cmd.Should().Contain("-b:v 3500k");
        cmd.Should().Contain("-minrate 3300k");
        cmd.Should().Contain("-maxrate 4000k");

        // Audio: AC3 5.1 preserved as a copy.
        cmd.Should().Contain("-map 0:1");
        cmd.Should().Contain("-c:a:0 copy");

        // Subtitle: English text track passed through.
        cmd.Should().Contain("-map 0:2");
        cmd.Should().Contain("-c:s copy");
    }


    // =====================================================================
    //  Scenario 2: 4K HEVC source → NVIDIA NVENC HEVC, MP4.
    //  Audio fan-out: AAC stereo + AC3 5.1 outputs (no preserve).
    //  HEVC source above target+700 so no copy.
    // =====================================================================

    [Fact]
    public void Scenario_4K_HEVC_to_NVENC_with_audio_fanout_MP4()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 3840, height: 2160)
            .Audio(codec: "truehd", channels: 8, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                 = "mp4",
            Codec                  = "h265",
            Encoder                = "libx265",
            FfmpegQualityPreset    = "slow",
            TargetBitrate          = 3500,
            FourKBitrateMultiplier = 4,
            HardwareAcceleration   = "nvidia",
            PreserveOriginalAudio  = false,
            AudioOutputs           = new()
            {
                new AudioOutputProfile { Codec = "aac", Layout = "Stereo", BitrateKbps = 192 },
                new AudioOutputProfile { Codec = "ac3", Layout = "5.1",    BitrateKbps = 448 },
            },
            AudioLanguagesToKeep = new() { "en" },
        };
        var item = new WorkItem { Bitrate = 25000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item,
            inputPath: "/m/4k.mkv", outputPath: "/m/out.mp4");

        AssertWellFormed(cmd, "mp4", "/m/4k.mkv", "/m/out.mp4");

        // Video: nvenc encoder, slow preset, NVIDIA hwaccel decode.
        cmd.Should().Contain("-hwaccel cuda");
        cmd.Should().Contain("-c:v hevc_nvenc");
        cmd.Should().Contain("-preset slow");

        // 4K rate-control: target = 3500 × 4 = 14000.
        cmd.Should().Contain("-b:v 14000k");
        cmd.Should().Contain("-rc vbr");
        cmd.Should().Contain("-spatial_aq 1");

        // Audio fan-out: two re-encodes, both from source #1 (the TrueHD).
        cmd.Should().Contain("-c:a:0 aac");
        cmd.Should().Contain("-b:a:0 192k");
        cmd.Should().Contain("-ac:a:0 2");
        cmd.Should().Contain("-c:a:1 ac3");
        cmd.Should().Contain("-b:a:1 448k");
        cmd.Should().Contain("-ac:a:1 6");

        // The TrueHD source is mapped twice (one per output) — count must be exactly 2.
        Regex.Matches(cmd, @"-map 0:1\b").Count.Should().Be(2);

        // MP4 strips subtitles unconditionally.
        cmd.Should().Contain("-sn");
    }


    // =====================================================================
    //  Scenario 3: VAAPI HEVC encode with HEVC source under target → copy.
    //  Audio preserved, MKV out.
    // =====================================================================

    [Fact]
    public void Scenario_HEVC_under_target_takes_videoCopy_path()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            HardwareAcceleration  = "intel",
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 3000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item, videoCopy: true);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Video copy path: -c:v copy, no encoder-specific flags.
        cmd.Should().Contain("-c:v copy");
        cmd.Should().NotContain("-c:v hevc_vaapi");
        cmd.Should().NotContain("-rc_mode");
        cmd.Should().NotContain("-preset ");

        // Audio: AC3 source preserved.
        cmd.Should().Contain("-c:a:0 copy");
    }


    // =====================================================================
    //  Scenario 4: 4K HDR HEVC source → 1080p downscale on Apple
    //  VideoToolbox. Downscale-below-4K disables the multiplier so the
    //  bitrate is the user's HD target, not 14000k.
    // =====================================================================

    [Fact]
    public void Scenario_4K_HDR_downscale_to_1080p_on_videotoolbox_MKV()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 3840, height: 2160, colorTransfer: "smpte2084")
            .Audio(codec: "eac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                 = "mkv",
            Encoder                = "libx265",
            FfmpegQualityPreset    = "medium",
            HardwareAcceleration   = "apple",
            TargetBitrate          = 5000,
            FourKBitrateMultiplier = 4,
            DownscalePolicy        = "Always",
            DownscaleTarget        = "1080p",
            PreserveOriginalAudio  = true,
            AudioOutputs           = new(),
        };
        var item = new WorkItem { Bitrate = 25000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // VideoToolbox HEVC encoder + the videotoolbox hwaccel.
        cmd.Should().Contain("-hwaccel videotoolbox");
        cmd.Should().Contain("-c:v hevc_videotoolbox");

        // Downscale-below-4K: bitrate is the HD target, NOT 4K's 20000k.
        cmd.Should().Contain("-b:v 5000k");
        cmd.Should().NotContain("-b:v 20000k");

        // The -vf chain emits the scale expression for downscale.
        cmd.Should().Contain("-vf scale=w=-2:h=1080:flags=lanczos");

        // VideoToolbox emits the bitrate triple but no -rc flag.
        cmd.Should().Contain("-maxrate");
        cmd.Should().Contain("-bufsize");
        cmd.Should().NotContain("-rc ");
    }


    // =====================================================================
    //  Scenario 5: Multi-language source with sub language drop, MKV.
    //  EN + JA audio kept (preserve only); EN subs kept, JA subs dropped.
    // =====================================================================

    [Fact]
    public void Scenario_multi_language_with_subtitle_drop_MKV()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "jpn")
            .Subtitle(codec: "subrip", lang: "eng")
            .Subtitle(codec: "subrip", lang: "jpn")
            .Build();

        var opts = new EncoderOptions
        {
            Format                  = "mkv",
            Encoder                 = "libx265",
            FfmpegQualityPreset     = "medium",
            HardwareAcceleration    = "none",
            TargetBitrate           = 3500,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new() { "en", "ja" },
            SubtitleLanguagesToKeep = new() { "en" },   // drops the JA sub
        };
        var item = new WorkItem { Bitrate = 6000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Both audio tracks copy through — one per language.
        cmd.Should().Contain("-map 0:1");
        cmd.Should().Contain("-map 0:2");
        Regex.Matches(cmd, @"-c:a:\d+ copy").Count.Should().Be(2);

        // Only English subtitle (#3) is mapped. Japanese (#4) is dropped.
        cmd.Should().Contain("-map 0:3");
        cmd.Should().NotContain("-map 0:4");
        cmd.Should().Contain("-c:s copy");
    }


    // =====================================================================
    //  Scenario 6: VAAPI software-decode-then-hardware-encode init flags.
    //  When hwDecode=false, -hwaccel vaapi is omitted but the device + filter
    //  device are still set up (for the -vf format=...|vaapi,hwupload terminator).
    //  Pin that the init-flag string composes correctly into the command.
    // =====================================================================

    [Fact]
    public void Scenario_vaapi_sw_decode_hw_encode_init_flags_compose()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            HardwareAcceleration  = "intel",
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = true, Probe = probe };

        // Build the command with the SW-decode VAAPI init explicitly (the path the encoder
        // takes when filters are active — the -vf chain forces frames to system memory).
        var swDecodeInit = TranscodingService.GetInitFlags("intel", hwDecode: false);
        var (target, min, max, _) = TranscodingService.CalculateBitrates(item, opts);
        var cmd = TranscodingService.BuildFfmpegCommand(
            format:           "mkv",
            initFlags:        swDecodeInit,
            analyzeFlags:     "-analyzeduration 10M -probesize 50M ",
            inputPath:        "/m/in.mkv",
            extraInputs:      "",
            videoFlags:       _ffprobe.MapVideo(probe) + " -c:v hevc_vaapi ",
            compressionFlags: TranscodingService.GetForcedReencodeCompressionFlags(
                                  "hevc_vaapi", useVaapi: true, isSvtAv1: false,
                                  target, min, max, useConservativeHwFlags: false) + " ",
            audioFlags:       _ffprobe.MapAudio(probe, opts.AudioLanguagesToKeep,
                                  opts.PreserveOriginalAudio, opts.AudioOutputs,
                                  isMatroska: true, out _) + " ",
            subtitleFlags:    "-sn ",
            outputPath:       "/m/out.mkv");

        AssertWellFormed(cmd, "mkv", "/m/in.mkv", "/m/out.mkv");

        // SW-decode VAAPI: device init flags present, but -hwaccel vaapi is NOT.
        cmd.Should().Contain("-init_hw_device vaapi=hw:/dev/dri/renderD128");
        cmd.Should().Contain("-filter_hw_device hw");
        cmd.Should().NotContain("-hwaccel vaapi");

        // VAAPI rate-control uses CQP, not -b:v.
        cmd.Should().Contain("-rc_mode CQP");
        cmd.Should().Contain("-global_quality 25");
    }


    // =====================================================================
    //  Format-toggle pinning: same inputs, only Format differs → MKV gets
    //  matroska + no faststart; MP4 gets mp4 + faststart.
    // =====================================================================

    [Theory]
    [InlineData("mkv", "matroska", false)]
    [InlineData("mp4", "mp4",      true)]
    public void Format_toggles_muxer_and_faststart(string format, string muxer, bool expectFaststart)
    {
        var cmd = TranscodingService.BuildFfmpegCommand(
            format:           format,
            initFlags:        "-y",
            analyzeFlags:     "",
            inputPath:        "/in.mkv",
            extraInputs:      "",
            videoFlags:       "-map 0:0 -c:v libx265 ",
            compressionFlags: "-b:v 3500k ",
            audioFlags:       "-map 0:1 -c:a:0 copy ",
            subtitleFlags:    "-sn ",
            outputPath:       "/out");

        cmd.Should().Contain($"-f {muxer}");
        if (expectFaststart) cmd.Should().Contain("-movflags +faststart");
        else                 cmd.Should().NotContain("-movflags +faststart");
    }


    // =====================================================================
    //  Scenario 7: AMD VAAPI HEVC hardware encode, no filters → hwaccel
    //  decode active, VAAPI rate-control path.
    // =====================================================================

    [Fact]
    public void Scenario_AMD_VAAPI_hevc_with_hwaccel_decode_MKV()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            FfmpegQualityPreset   = "medium",
            HardwareAcceleration  = "amd",
            TargetBitrate         = 4000,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // VAAPI device init + hwaccel decode (no filters → hw decode is left enabled).
        cmd.Should().Contain("-init_hw_device vaapi=hw:/dev/dri/renderD128");
        cmd.Should().Contain("-hwaccel vaapi");
        cmd.Should().Contain("-hwaccel_output_format vaapi");

        // Encoder + VAAPI-only rate-control (CQP, no -b:v).
        cmd.Should().Contain("-c:v hevc_vaapi");
        cmd.Should().Contain("-rc_mode CQP");
        cmd.Should().Contain("-global_quality 25");
        cmd.Should().NotContain("-b:v ");

        // VAAPI takes no -preset.
        cmd.Should().NotContain("-preset medium");
    }


    // =====================================================================
    //  Scenario 8: AV1 software encode (libsvtav1) — numeric preset, AV1
    //  rate-control, no `-preset slow` (svt uses numbers).
    // =====================================================================

    [Fact]
    public void Scenario_AV1_libsvtav1_uses_numeric_preset_and_svt_params()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libsvtav1",
            FfmpegQualityPreset   = "slow",   // → 4 in the SVT-AV1 ladder
            HardwareAcceleration  = "none",
            TargetBitrate         = 2500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 6000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        cmd.Should().Contain("-c:v libsvtav1");
        // SVT-AV1 takes a numeric preset. "slow" → 4.
        cmd.Should().Contain("-preset 4");
        cmd.Should().NotContain("-preset slow");

        // SVT-AV1 rate-control: -svtav1-params rc=1, -b:v at +5%.
        cmd.Should().Contain("-svtav1-params");
        cmd.Should().Contain("rc=1");
        cmd.Should().Contain("-b:v 2625k");   // 2500 × 1.05
    }


    // =====================================================================
    //  Scenario 9: HDR (PQ) source with TonemapHdrToSdr=true → -vf chain
    //  contains the zscale tonemap recipe.
    // =====================================================================

    [Fact]
    public void Scenario_HDR_software_tonemap_emits_vf_chain()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080, colorTransfer: "smpte2084")
            .Audio(codec: "eac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            FfmpegQualityPreset   = "medium",
            HardwareAcceleration  = "none",
            TargetBitrate         = 5000,
            TonemapHdrToSdr       = true,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 12000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Tonemap chain present in -vf.
        cmd.Should().Contain("-vf ");
        cmd.Should().Contain("zscale=t=linear:npl=100");
        cmd.Should().Contain("tonemap=tonemap=hable:desat=0");
        cmd.Should().Contain("zscale=t=bt709:m=bt709:r=tv");
        // Final format conversion to 8-bit yuv420p (sdr output).
        cmd.Should().Contain("format=yuv420p");
    }


    // =====================================================================
    //  Scenario 10: Hybrid mux pass — at-target HEVC + audio language drop.
    //  Video is copied (videoCopy=true); audio gets re-mapped to drop the
    //  un-kept language. This is the "remux without re-encoding" path.
    // =====================================================================

    [Fact]
    public void Scenario_Hybrid_mux_pass_video_copy_with_audio_language_drop()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Audio(codec: "ac3", channels: 6, lang: "fre")
            .Subtitle(codec: "subrip", lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                  = "mkv",
            Encoder                 = "libx265",
            HardwareAcceleration    = "none",
            TargetBitrate           = 3500,
            EncodingMode            = EncodingMode.Hybrid,
            MuxStreams              = MuxStreams.Both,
            PreserveOriginalAudio   = true,
            AudioOutputs            = new(),
            AudioLanguagesToKeep    = new() { "en" },     // drops the FR audio
            SubtitleLanguagesToKeep = new() { "en" },
        };
        var item = new WorkItem { Bitrate = 3000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item, videoCopy: true);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Video is copied — no encoder-specific flags, no rate-control flags.
        cmd.Should().Contain("-c:v copy");
        cmd.Should().NotContain("-preset");
        cmd.Should().NotContain("-b:v");
        cmd.Should().NotContain("-rc ");

        // Only the EN audio (#1) is mapped. FR audio (#2) is dropped.
        cmd.Should().Contain("-map 0:1");
        cmd.Should().NotContain("-map 0:2");

        // EN subtitle (#3) survives.
        cmd.Should().Contain("-map 0:3");
        cmd.Should().Contain("-c:s copy");
    }


    // =====================================================================
    //  Scenario 11: MKV with bitmap (PGS) subs — dropped by default.
    // =====================================================================

    [Fact]
    public void Scenario_MKV_drops_bitmap_subs_by_default()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Subtitle(codec: "subrip",            lang: "eng")
            .Subtitle(codec: "hdmv_pgs_subtitle", lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                       = "mkv",
            Encoder                      = "libx265",
            FfmpegQualityPreset          = "medium",
            HardwareAcceleration         = "none",
            TargetBitrate                = 3500,
            PreserveOriginalAudio        = true,
            AudioOutputs                 = new(),
            SubtitleLanguagesToKeep      = new() { "en" },
            PassThroughImageSubtitlesMkv = false,
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Text sub (#2) kept; bitmap PGS sub (#3) dropped.
        cmd.Should().Contain("-map 0:2");
        cmd.Should().NotContain("-map 0:3");
        cmd.Should().Contain("-c:s copy");
    }


    // =====================================================================
    //  Scenario 12: MKV with bitmap (PGS) subs — passed through when the
    //  PassThroughImageSubtitlesMkv flag is on.
    // =====================================================================

    [Fact]
    public void Scenario_MKV_passes_through_bitmap_subs_when_flag_is_on()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Subtitle(codec: "subrip",            lang: "eng")
            .Subtitle(codec: "hdmv_pgs_subtitle", lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                       = "mkv",
            Encoder                      = "libx265",
            FfmpegQualityPreset          = "medium",
            HardwareAcceleration         = "none",
            TargetBitrate                = 3500,
            PreserveOriginalAudio        = true,
            AudioOutputs                 = new(),
            SubtitleLanguagesToKeep      = new() { "en" },
            PassThroughImageSubtitlesMkv = true,
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = false, Probe = probe };

        // includeBitmapSubs: pass the production flag through to MapSub.
        var cmd = BuildScenarioCommand(probe, opts, item, includeBitmapSubs: true);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        // Both subs survive — text and PGS.
        cmd.Should().Contain("-map 0:2");
        cmd.Should().Contain("-map 0:3");
        cmd.Should().Contain("-c:s copy");
    }


    // =====================================================================
    //  Scenario 13: StrictBitrate pins target = min = max regardless of source.
    // =====================================================================

    [Fact]
    public void Scenario_StrictBitrate_pins_target_min_max()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            FfmpegQualityPreset   = "medium",
            HardwareAcceleration  = "none",
            TargetBitrate         = 4500,
            StrictBitrate         = true,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 9000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        cmd.Should().Contain("-b:v 4500k");
        cmd.Should().Contain("-minrate 4500k");
        cmd.Should().Contain("-maxrate 4500k");
    }


    // =====================================================================
    //  Scenario 14: TrueHD source + MP4 output → audio container fallback.
    //  TrueHD can't be copied to MP4, so the planner emits an AAC re-encode.
    // =====================================================================

    [Fact]
    public void Scenario_TrueHD_to_MP4_falls_back_to_aac_re_encode()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "hevc", width: 1920, height: 1080)
            .Audio(codec: "truehd", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mp4",
            Encoder               = "libx265",
            FfmpegQualityPreset   = "medium",
            HardwareAcceleration  = "none",
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 8000, IsHevc = true, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item,
            inputPath: "/m/in.mkv", outputPath: "/m/out.mp4");

        AssertWellFormed(cmd, "mp4", "/m/in.mkv", "/m/out.mp4");

        // Audio is re-encoded to AAC because MP4 can't carry TrueHD as copy.
        cmd.Should().Contain("-c:a:0 aac");
        cmd.Should().NotContain("-c:a:0 copy");
        // Channel count preserved (6 — no Layout override).
        cmd.Should().Contain("-ac:a:0 6");
    }


    // =====================================================================
    //  Scenario 15: Low-bitrate H.264 source → 70% compression branch.
    //  Source bitrate 3000, target 3500 → bitrate < target+700, !IsHevc →
    //  target = 70% of source = 2100k.
    // =====================================================================

    [Fact]
    public void Scenario_low_bitrate_h264_uses_70_percent_compression()
    {
        var probe = new ProbeBuilder()
            .Video(codec: "h264", width: 1920, height: 1080)
            .Audio(codec: "ac3", channels: 6, lang: "eng")
            .Build();

        var opts = new EncoderOptions
        {
            Format                = "mkv",
            Encoder               = "libx265",
            FfmpegQualityPreset   = "medium",
            HardwareAcceleration  = "none",
            TargetBitrate         = 3500,
            PreserveOriginalAudio = true,
            AudioOutputs          = new(),
        };
        var item = new WorkItem { Bitrate = 3000, IsHevc = false, Probe = probe };

        var cmd = BuildScenarioCommand(probe, opts, item);

        AssertWellFormed(cmd, "mkv", "/source/in.mkv", "/output/out.mkv");

        cmd.Should().Contain("-b:v 2100k");   // 3000 × 0.7
        cmd.Should().Contain("-minrate 1800k");
        cmd.Should().Contain("-maxrate 2400k");
    }


    [Fact]
    public void Build_command_quotes_paths_with_spaces()
    {
        var cmd = TranscodingService.BuildFfmpegCommand(
            format:           "mkv",
            initFlags:        "-y",
            analyzeFlags:     "",
            inputPath:        "/path with spaces/in file.mkv",
            extraInputs:      "",
            videoFlags:       "-map 0:0 -c:v copy ",
            compressionFlags: "",
            audioFlags:       "-map 0:1 -c:a:0 copy ",
            subtitleFlags:    "-sn ",
            outputPath:       "/path with spaces/out file.mkv");

        cmd.Should().Contain("\"/path with spaces/in file.mkv\"");
        cmd.Should().Contain("\"/path with spaces/out file.mkv\"");
    }
}
