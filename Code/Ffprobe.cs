using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
        public static ProbeResult Probe(string file_input)
        {
            string flags = "-v quiet -print_format json -show_streams -show_format \"";
            string command = flags + file_input + "\"";

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
            string correct_output;
            if (err.Length > output.Length)
            {
                correct_output = err;
            }
            else
            {
                correct_output = output;
            }

            ProbeResult probe_result = new ProbeResult();
            try
            {
                int json_index = correct_output.IndexOf('{');
                correct_output = correct_output.Substring(json_index, correct_output.Length - json_index);
                probe_result = JsonConvert.DeserializeObject<ProbeResult>(correct_output);
            }
            catch { }

            return probe_result;
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
            int audio_count = 0;

            for (int i = 0; i < probe.streams.Count(); i++)
            {
                if (probe.streams[i].codec_type == "audio")
                {
                    audio_count++;
                }
            }

            if (englishOnly)
            {
                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "audio")
                    {

                        if (probe.streams[i].tags != null &&
                            probe.streams[i].tags.language != null &&
                            probe.streams[i].tags.language == "eng")
                        {

                            if (probe.streams[i].channels == 2)
                            {
                                return "-map 0:" + i.ToString() + " -c:a copy";
                            }
                            else if (twoChannels)
                            {
                                return "-map 0:" + i.ToString() + " -c:a aac -b:a 320k";
                            }
                            else
                            {
                                return "-map 0:" + i.ToString() + " -c:a copy";
                            }
                        }
                    }
                }
            }

            if (twoChannels && audio_count > 0)
            {
                return "-map 0:a -c:a aac -b:a 320k";
            }

            if (audio_count > 0)
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
            string mapping = "-map 0:s -c:s copy";
            int subtitle_count = 0;

            for (int i = 0; i < probe.streams.Length; i++)
            {
                if (probe.streams[i].codec_type == "subtitle")
                {
                    subtitle_count++;
                }
            }

            if (englishOnly)
            {
                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "subtitle" && probe.streams[i].tags.language == "eng")
                    {
                        // Copy instead of srt, as ffmpeg can't convert all formats to srt
                        mapping = "-map 0:" + i.ToString() + " -c:s copy";
                        return mapping;
                    }
                }
            }

            if (subtitle_count > 0)
                return mapping;
            else
                return "";
        }

        /// <summary>
        /// Compares duration to verify there was no corruption
        /// </summary>
        /// <param name="input"></param>
        /// <param name="output"></param>
        /// <returns></returns>
        public static bool ConvertedSuccessfully(string input, string output)
        {
            try
            {
                ProbeResult input_probe = Probe(input);
                ProbeResult output_probe = Probe(output);
                double input_duration = DurationStringToSeconds(input_probe.format.duration);
                double output_duration = DurationStringToSeconds(output_probe.format.duration);
                double duration_difference = Math.Abs(input_duration - output_duration);

                // Check for 30 seconds of difference for now
                if (duration_difference >= 30)
                {
                    return false;
                }
                else
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                return false;
            }
        }
    }
}