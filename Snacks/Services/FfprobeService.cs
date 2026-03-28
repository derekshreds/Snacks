using Newtonsoft.Json;
using Snacks.Models;
using System.Diagnostics;

namespace Snacks.Services
{
    /// <summary> The class that handles ffprobe related logic </summary>
    public class FfprobeService
    {
        private readonly string _ffprobePath;

        public FfprobeService()
        {
            _ffprobePath = Environment.GetEnvironmentVariable("FFPROBE_PATH") ?? "ffprobe";
        }

        /// <summary> Probe a file and return all relevant stream information </summary>
        /// <param name="fileInput"> The file to probe </param>
        /// <returns> A ProbeResult of the file </returns>
        public async Task<ProbeResult> ProbeAsync(string fileInput)
        {
            string flags = "-v quiet -print_format json -show_streams -show_format";
            string command = $"{flags} \"{fileInput}\"";

            var processStartInfo = new ProcessStartInfo(_ffprobePath)
            {
                Arguments = command,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            
            var outputBuilder = new System.Text.StringBuilder();
            var errorBuilder = new System.Text.StringBuilder();

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            // Ffprobe sometimes outputs to error stream
            string correctOutput = outputBuilder.Length > errorBuilder.Length 
                ? outputBuilder.ToString() 
                : errorBuilder.ToString();

            try
            {
                int jsonIndex = correctOutput.IndexOf('{');
                if (jsonIndex >= 0)
                {
                    correctOutput = correctOutput.Substring(jsonIndex);
                    return JsonConvert.DeserializeObject<ProbeResult>(correctOutput) ?? new ProbeResult();
                }
            }
            catch
            {
                // Return empty result on failure
            }

            return new ProbeResult();
        }

        /// <summary> Find and map necessary video streams </summary>
        /// <param name="probe"> The ProbeResult to map the video string from </param>
        /// <returns> The video mapping string </returns>
        public string MapVideo(ProbeResult probe)
        {
            var videoStream = probe.streams.FirstOrDefault(s => s.codec_type == "video");
            return videoStream != null ? $"-map 0:{videoStream.index}" : "";
        }

        /// <summary> Find and map necessary audio streams </summary>
        /// <param name="probe">The ProbeResult to map audio from </param>
        /// <param name="englishOnly"> Whether only English audio should be kept </param>
        /// <param name="twoChannels"> Whether audio should be downmixed to stereo </param>
        /// <param name="isMatroska"> Whether the container is of matroska format </param>
        /// <returns> The audio mapping string </returns>
        public string MapAudio(ProbeResult probe, bool englishOnly, bool twoChannels, bool isMatroska)
        {
            var audioStreams = probe.streams.Where(s => s.codec_type == "audio").ToList();
            if (!audioStreams.Any())
                return "";

            if (englishOnly)
            {
                var englishAudioStreams = audioStreams
                    .Where(s => s.tags?.language == "eng" && 
                               (s.tags?.title == null || !s.tags.title.ToLower().Contains("comm")))
                    .ToList();

                if (englishAudioStreams.Any())
                {
                    var selectedStream = englishAudioStreams.FirstOrDefault(s => 
                        twoChannels && s.channels == 2 && isMatroska) ?? englishAudioStreams.First();
                    
                    return twoChannels && selectedStream.channels == 2 && isMatroska
                        ? $"-map 0:{selectedStream.index} -c:a copy"
                        : $"-map 0:{selectedStream.index} -c:a aac -ac 2 -vbr 5";
                }
            }

            if (twoChannels)
                return "-map 0:a -c:a aac -ac 2 -vbr 5";

            return isMatroska ? "-map 0:a -c:a copy" : "-map 0:a -c:a aac -vbr 5";
        }

        /// <summary> Find and map necessary subtitles </summary>
        /// <param name="probe"> The ProbeResult to map the subtitles from </param>
        /// <param name="englishOnly"> Whether only English subtitles should be mapped </param>
        /// <param name="isMatroska"> Whether the container is of matroska format </param>
        /// <returns> The subtitle mapping string </returns>
        public string MapSub(ProbeResult probe, bool englishOnly, bool isMatroska)
        {
            if (!isMatroska)
                return "-sn";

            var subtitleStreams = probe.streams.Where(s => s.codec_type == "subtitle").ToList();

            if (englishOnly)
            {
                var englishSubtitles = subtitleStreams.Where(s => s.tags?.language == "eng").ToList();
                if (englishSubtitles.Any())
                {
                    var maps = string.Join(" ", englishSubtitles.Select(s => $"-map 0:{s.index}"));
                    return $"{maps} -c:s copy";
                }
            }

            if (subtitleStreams.Any())
            {
                var maps = string.Join(" ", subtitleStreams.Select(s => $"-map 0:{s.index}"));
                return $"{maps} -c:s copy";
            }

            return "";
        }

        /// <summary> Compares duration to verify there was no corruption </summary>
        /// <param name="input"> The ProbeResult of the input file </param>
        /// <param name="output"> The ProbeResult of the output file </param>
        /// <returns> Boolean value of the conversion success </returns>
        public bool ConvertedSuccessfully(ProbeResult input, ProbeResult output)
        {
            try
            {
                double inputDuration = GetVideoDuration(input);
                double outputDuration = GetVideoDuration(output);
                double durationDifference = Math.Abs(inputDuration - outputDuration);

                // Check for 10 seconds of difference for now
                return durationDifference < 10;
            }
            catch 
            { 
                return false; 
            }
        }

        /// <summary> Gets the duration of a video stream </summary>
        /// <param name="probe"> The ProbeResult to get the duration from </param>
        /// <returns> The duration of a video in seconds </returns>
        public double GetVideoDuration(ProbeResult probe)
        {
            try
            {
                foreach (var stream in probe.streams)
                {
                    if (stream.codec_type == "video")
                    {
                        double formatDuration = DurationStringToSeconds(probe.format.duration);
                        double streamDuration = DurationStringToSeconds(stream.duration);
                        return formatDuration > streamDuration ? formatDuration : streamDuration;
                    }
                }
            }
            catch 
            { 
                // Return 0 on error
            }

            return 0;
        }

        /// <summary> Convert "00:00:00" duration format to total seconds </summary>
        /// <param name="input"> The input string to convert </param>
        /// <returns> The total duration in seconds </returns>
        public double DurationStringToSeconds(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return 0;

            try
            {
                string[] split = input.Split(new char[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length >= 3)
                {
                    // Hour, Minute, Second
                    return double.Parse(split[0]) * 3600 + double.Parse(split[1]) * 60 + double.Parse(split[2]);
                }
                else
                {
                    return double.Parse(input);
                }
            }
            catch 
            { 
                return 0; 
            }
        }

        /// <summary> Convert total seconds to "00:00:00" duration format </summary>
        /// <param name="input"> The seconds to convert </param>
        /// <returns> The duration format string </returns>
        public string SecondsToDurationString(double input)
        {
            int hours = (int)(input / 3600);
            int minutes = (int)((input % 3600) / 60);
            int seconds = (int)(input % 60);

            return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
        }
    }
}