using Newtonsoft.Json;
using System;
using System.Diagnostics;
using System.Linq;
using static Snacks.Tools;

namespace Snacks
{
    /// <summary> The class that handles ffprobe related logic </summary>
    public static class Ffprobe
    {
        /// <summary> Json deserialization structure for ffprobe tags </summary>
        public class Tags
        {
            public string language;
            public string title;
            public string handler_name;
            public string vendor_id;
        }

        /// <summary> Json deserialization structure for ffprobe streams </summary>
        public class Stream
        {
            public int index;
            public string codec_name;
            public string codec_long_name;
            public string profile;
            public string codec_type;
            public string codec_time_base;
            public string codec_tag_string;
            public string codec_tag;
            public int channels;
            public string channel_layout;
            public int width;
            public int height;
            public int coded_width;
            public int coded_height;
            public int has_b_frames;
            public string sample_aspect_ratio;
            public string display_aspect_ratio;
            public string pix_fmt;
            public int level;
            public string color_range;
            public string color_space;
            public string color_transfer;
            public string color_primaries;
            public string chroma_location;
            public string field_order;
            public string refs;
            public string is_avc;
            public string nal_length_size;
            public string r_frame_rate;
            public string avg_grame_rate;
            public string time_base;
            public string start_pts;
            public string start_time;
            public long duration_ts;
            public string duration;
            public string bit_rate;
            public string nb_frames;
            public string bits_per_raw_sample;
            public Tags tags;
        }

        /// <summary> Json deserialization structure for ffprobe format </summary>
        public class Format
        {
            public string filename;
            public int nb_streams;
            public int nb_programs;
            public string format_name;
            public string format_long_name;
            public string start_time;
            public string duration;
            public string size;
            public string bit_rate;
            public int probe_score;
        }

        /// <summary> Json deserialization structure for the probe result </summary>
        public class ProbeResult
        {
            public Stream[] streams;
            public Format format;
        }

        /// <summary> Probe a file and return all relevant stream information </summary>
        /// <param name="fileInput"> The file to probe </param>
        /// <returns> A ProbeResult of the file </returns>
        public static ProbeResult Probe(string fileInput)
        {
            string flags = "-v quiet -print_format json -show_streams -show_format \"";
            string command = flags + fileInput + "\"";

            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffprobe.exe");

            cmdsi.Arguments = command;
            cmdsi.UseShellExecute = false;
            cmdsi.RedirectStandardOutput = true;
            cmdsi.CreateNoWindow = true;
            cmdsi.RedirectStandardError = true;
            Process cmd = Process.Start(cmdsi);

            string err = "";
            string output = "";

            cmd.OutputDataReceived += (s, e) =>
            {
                output += e.Data;
            };
            cmd.ErrorDataReceived += (s, e) =>
            {
                err += e.Data;
            };

            cmd.BeginErrorReadLine();
            cmd.BeginOutputReadLine();
            cmd.WaitForExit();

            // Ffprobe doesn't always output to the correct data side
            string correctOutput;
            if (err.Length > output.Length)
                correctOutput = err;
            else
                correctOutput = output;

            ProbeResult probeResult = new ProbeResult();
            try
            {
                int json_index = correctOutput.IndexOf('{');
                correctOutput = correctOutput.Substring(json_index, correctOutput.Length - json_index);
                probeResult = JsonConvert.DeserializeObject<ProbeResult>(correctOutput);
            }
            catch { }

            return probeResult;
        }

        /// <summary> Find and map necessary video streams </summary>
        /// <param name="probe"> The ProbeResult to map the video string from </param>
        /// <returns> The video mapping string </returns>
        public static string MapVideo(ProbeResult probe)
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
        public static string MapAudio(ProbeResult probe, bool englishOnly, bool twoChannels, bool isMatroska)
        {
            var audioStreams = probe.streams.Where(s => s.codec_type == "audio").ToList();
            if (!audioStreams.Any())
                return "";

            if (englishOnly)
            {
                var englishAudioStreams = audioStreams
                    .Where(s => s.tags?.language == "eng" && (s.tags?.title == null || !s.tags.title.ToLower().Contains("comm")))
                    .ToList();

                if (englishAudioStreams.Any())
                {
                    var selectedStream = englishAudioStreams.FirstOrDefault(s => twoChannels && s.channels == 2 && isMatroska) ?? englishAudioStreams.First();
                    return twoChannels && selectedStream.channels == 2 && isMatroska
                        ? $"-map 0:{selectedStream.index} -c:a copy"
                        : $"-map 0:{selectedStream.index} -c:a aac -ac 2 -vbr 5";
                }
            }

            if (twoChannels)
                return "-map 0:a -q:a aac -ac 2 -vbr 5";

            return isMatroska ? "-map 0:a -c:a copy" : "-map 0:a -c:a aac -vbr 5";
        }

        /// <summary> Returns an audio filter to preserve LFE when downmixing to 2 channels </summary>
        /// <param name="channelLayout"> The channel layout to get a downmix filter for </param>
        /// <returns> The audio downmixing filter string </returns>
        public static string GetDownmixAudioFilter(string channelLayout)
        {
            switch (channelLayout)
            {
                case "5.1":
                    return "-af \"pan=stereo|FL=0.374107*FC+0.529067*FL+0.458186*BL+0.264534*BR+0.374107*LFE" +
                                           "|FR=0.374107*FC+0.529067*FR+0.458186*BR+0.264534*BL+0.374107*LFE\" ";
                case "5.1(side)":
                    return "-af \"pan=stereo|FL=0.374107*FC+0.529067*FL+0.458186*SL+0.264534*SR+0.374107*LFE" +
                                           "|FR=0.374107*FC+0.529067*FR+0.458186*SR+0.264534*SL+0.374107*LFE\" ";
                case "7.1":
                    return "-af \"pan=stereo|FL=0.274804*FC+0.388631*FL+0.336565*SL+0.194316*SR+0.336565*BL+0.194316*BR+0.274804*LFE" +
                                           "|FR=0.274804*FC+0.388631*FR+0.336565*SR+0.194316*SL+0.336565*BR+0.194316*BL+0.274804*LFE\" ";
                case "7.1(wide)":
                    return "-af \"pan=stereo|FL=0.274804*FC+0.388631*FL+0.336565*FLC+0.194316*FRC+0.336565*BL+0.194316*BR+0.274804*LFE" +
                                           "|FR=0.274804*FC+0.388631*FR+0.336565*RFC+0.194316*FLC+0.336565*BR+0.194316*BL+0.274804*LFE\" ";
                case "7.1(wide-side)":
                    return "-af \"pan=stereo|FL=0.274804*FC+0.388631*FL+0.336565*FLC+0.194316*FRC+0.336565*SL+0.194316*SR+0.274804*LFE" +
                                           "|FR=0.274804*FC+0.388631*FR+0.336565*RFC+0.194316*FLC+0.336565*SR+0.194316*SL+0.274804*LFE\" ";
                case "7.1(top)":
                    return "-af \"pan=stereo|FL=0.274804*FC+0.388631*FL+0.336565*TFL+0.194316*TFR+0.336565*BL+0.194316*BR+0.274804*LFE" +
                                           "|FR=0.274804*FC+0.388631*FR+0.336565*TFR+0.194316*TFL+0.336565*BR+0.194316*BL+0.274804*LFE\" ";
                default:
                    return "";
            }
        }

        /// <summary> Find and map necessary subtitles </summary>
        /// <param name="probe"> The ProbeResult to map the subtitles from </param>
        /// <param name="englishOnly"> Whether only English subtitles should be mapped </param>
        /// <param name="isMatroska"> Whether the container is of matroska format </param>
        /// <returns> The subtitle mapping string </returns>
        public static string MapSub(ProbeResult probe, bool englishOnly, bool isMatroska)
        {
            if (!isMatroska)
                return "-sn";

            var subtitleStreams = probe.streams.Where(s => s.codec_type == "subtitle").ToList();

            if (englishOnly)
            {
                var englishSubtitles = subtitleStreams.Where(s => s.tags?.language == "eng").ToList();
                if (englishSubtitles.Any())
                {
                    return string.Join(" ", englishSubtitles.Select(s => $"-map 0:{s.index} -c:s copy"));
                }
            }

            if (subtitleStreams.Any())
            {
                return string.Join(" ", subtitleStreams.Select(s => $"-map 0:{s.index} -c:s copy"));
            }

            return "";
        }

        /// <summary> Compares duration to verify there was no corruption </summary>
        /// <param name="input"> The ProbeResult of the input file </param>
        /// <param name="output"> The ProbeResult of the output file </param>
        /// <returns> Boolean value of the conversion success </returns>
        public static bool ConvertedSuccessfully(ProbeResult input, ProbeResult output)
        {
            try
            {
                double inputDuration = GetVideoDuration(input);
                double outputDuration = GetVideoDuration(output);
                double durationDifference = Math.Abs(inputDuration - outputDuration);

                // Check for 10 seconds of difference for now
                if (durationDifference >= 10)
                    return false;
                else
                    return true;
            }
            catch { return false; }
        }

        /// <summary> Gets the duration of a video stream </summary>
        /// <param name="probe"> The ProbeResult to get the duration from </param>
        /// <returns> The duration of a video in seconds </returns>
        public static double GetVideoDuration(ProbeResult probe)
        {
            try
            {
                for (int i = 0; i < probe.streams.Count(); i++)
                {
                    if (probe.streams[i].codec_type == "video")
                    {
                        double formatDuration = DurationStringToSeconds(probe.format.duration);
                        double streamDuration = DurationStringToSeconds(probe.streams[i].duration);
                        return formatDuration > streamDuration ? formatDuration : streamDuration;
                    }
                }
            }
            catch { }

            return 0;
        }
    }
}