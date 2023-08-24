using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Tools;

namespace Snacks
{
    public static class Ffprobe
    {
        public class Tags
        {
            public string language;
            public string title;
            public string handler_name;
            public string vendor_id;
        }

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

        public class ProbeResult
        {
            public Stream[] streams;
            public Format format;
        }

        /// <summary>
        /// Probe a file and return all relevant stream information
        /// </summary>
        /// <param name="file_input"></param>
        /// <returns></returns>
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
            {
                correctOutput = err;
            }
            else
            {
                correctOutput = output;
            }

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

        /// <summary>
        /// Find and map neccessary video streams
        /// </summary>
        /// <param name="probe"></param>
        /// <returns></returns>
        public static string MapVideo(ProbeResult probe)
        {
            string mapping = "";

            for (int i = 0; i < probe.streams.Length; i++)
            {
                if (probe.streams[i].codec_type == "video")
                {
                    mapping = "-map 0:" + i.ToString();
                    break;
                }
            }

            return mapping;
        }

        /// <summary>
        /// Find and map neccessary audio streams
        /// </summary>
        /// <param name="probe"></param>
        /// <param name="englishOnly"></param>
        /// <param name="twoChannels"></param>
        /// <returns></returns>
        public static string MapAudio(ProbeResult probe, bool englishOnly, bool twoChannels)
        {
            int audioCount = 0;

            for (int i = 0; i < probe.streams.Count(); i++)
            {
                if (probe.streams[i].codec_type == "audio")
                {
                    audioCount++;
                }
            }

            string flags = "";
            if (englishOnly)
            {
                // tuple (channels, location in collection)
                List<(int, int)> englishChannelLocation = new List<(int, int)>();

                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "audio" &&
                        probe.streams[i].tags != null &&
                        probe.streams[i].tags.language != null &&
                        probe.streams[i].tags.language == "eng")
                    {
                        englishChannelLocation.Add((probe.streams[i].channels, 0 + i));

                        if (twoChannels && probe.streams[i].channels == 2)
                        {
                            return "-map 0:" + i.ToString() + " -c:a copy";
                        }
                    }
                }

                for (int i = 0; i < englishChannelLocation.Count(); i++)
                {
                    if (i > 0)
                    {
                        flags += " ";
                    }

                    if (twoChannels)
                    {
                        flags += ("-map 0:" + englishChannelLocation[i].Item2.ToString() + " -c:a aac -b:a 320k");
                    }
                    else
                    {
                        flags += ("-map 0:" + englishChannelLocation[i].Item2.ToString() + " -c:a copy");
                    }
                }

                if (englishChannelLocation.Count() > 0)
                    return flags;
            }

            if (twoChannels && audioCount > 0)
            {
                return "-map 0:a -c:a aac -b:a 320k";
            }

            if (audioCount > 0)
                return "-map 0:a -c:a copy";
            else
                return "";
        }

        /// <summary>
        /// Find and map neccessary subtitles
        /// </summary>
        /// <param name="probe"></param>
        /// <param name="englishOnly"></param>
        /// <returns></returns>
        public static string MapSub(ProbeResult probe, bool englishOnly)
        {
            int subtitleCount = 0;

            for (int i = 0; i < probe.streams.Length; i++)
            {
                if (probe.streams[i].codec_type == "subtitle")
                {
                    subtitleCount++;
                }
            }

            if (englishOnly)
            {
                string mapping = "";
                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "subtitle" && probe.streams[i].tags.language == "eng")
                    {
                        if (i > 0)
                        {
                            mapping += " ";
                        }

                        // Copy instead of srt, as ffmpeg can't convert all formats to srt
                        mapping += "-map 0:" + i.ToString() + " -c:s copy";
                    }
                }

                if (mapping != "")
                    return mapping;
            }

            if (subtitleCount > 0)
                return "-map 0:s -c:s copy";
            else
                return "";
        }

        /// <summary>
        /// Compares duration to verify there was no corruption
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <returns></returns>
        public static bool ConvertedSuccessfully(ProbeResult input, ProbeResult output)
        {
            try
            {
                double inputDuration = GetVideoDuration(input);
                double outputDuration = GetVideoDuration(output);
                double durationDifference = Math.Abs(inputDuration - outputDuration);

                // Check for 10 seconds of difference for now
                if (durationDifference >= 10)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Gets the duration of a video stream
        /// </summary>
        /// <param name="probe"></param>
        /// <returns></returns>
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