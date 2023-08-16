using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Ffprobe;
using static Snacks.Tools;
using static Snacks.FileHandling;
using static Snacks.FormValues;

namespace Snacks
{
    public static class Ffmpeg
    {
        /// <summary>
        /// Generates a preview of the video file
        /// </summary>
        /// <param name="path"></param>
        /// <param name="duration"></param>
        public static void GeneratePreview(string path, string duration)
        {
            //Format: ffmpeg -i input.flv -ss 00:00:14.435 -frames:v 1 out.png
            string command = "-i \"" + path + "\" -ss " + duration + " -frames:v 1 preview.bmp";

            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffmpeg.exe");

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
        }

        /// <summary>
        /// Converts video using dynamic compression based on algorithms and flags
        /// </summary>
        /// <param name="logTextBox"></param>
        /// <param name="progressBar"></param>
        /// <param name="file_input"></param>
        /// <param name="englishOnlyAudio"></param>
        /// <param name="twoChannelAudio"></param>
        /// <param name="englishOnlySubtitles"></param>
        /// <param name="deleteOldFiles"></param>
        /// <param name="hardwareAcceleration"></param>
        public static void FfmpegVideo(RichTextBox logTextBox, ProgressBar progressBar, ComboBox encoderBox, ComboBox targetBitrateBox,
            string file_input, bool englishOnlyAudio, bool twoChannelAudio, bool englishOnlySubtitles, bool deleteOldFiles)
        {
            ProbeResult probe = Probe(file_input);
            logTextBox.Invoke(new Action(() =>
            {
                logTextBox.AppendText("Converting " + GetFileName(file_input) + "\r\n");
            }));

            int original_bitrate = int.Parse(probe.format.bit_rate) / 1000;
            bool is_hevc = false;

            for (int i = 0; i < probe.streams.Length; i++)
            {
                if (probe.streams[i].codec_name == "hevc")
                {
                    is_hevc = true;
                    break;
                }
            }

            string compression_flags = "";

            if (original_bitrate < 3000 && !is_hevc)
            {
                // Aim to save 30-40% on already low bitrate x264 files
                // Quality should be near identical as x265
                string target_bitrate = ((int)(original_bitrate * 0.7)).ToString() + "k";
                string min_bitrate = ((int)(original_bitrate * 0.6)).ToString() + "k";
                compression_flags = "-b:v " + target_bitrate + " -minrate " + min_bitrate +
                                    " -maxrate " + target_bitrate + " -bufsize " + target_bitrate + " ";
            }
            else
            {
                int br = GetBitrate(targetBitrateBox);
                string target_bitrate = br.ToString() + "k";
                string min_bitrate = (br - 200).ToString() + "k";
                string max_bitrate = (br + 500).ToString() + "k";
                compression_flags = "-b:v " + target_bitrate + " -minrate " + min_bitrate +
                                    " -maxrate " + max_bitrate + " -bufsize " + max_bitrate + " ";
            }

            string init_flags = "-y" + " -hwaccel auto" + " -i ";
            string video_flags = MapVideo(probe) + " -c:v " + GetEncoder(encoderBox) + " -preset medium ";
            string audio_flags = MapAudio(probe, englishOnlyAudio, twoChannelAudio) + " ";
            string subtitle_flags = MapSub(probe, englishOnlySubtitles) + " ";
            string var_flags = "-movflags +faststart -max_muxing_queue_size 9999 ";
            string file_output = file_input.Substring(0, file_input.LastIndexOf('.')) + ".mkv";
            string new_file_input = file_input.Substring(0, file_input.LastIndexOf('.')) + " - OG." + GetExtension(file_input);
            File.Move(file_input, new_file_input);

            // Just to make sure the file is moved before we begin
            Thread.Sleep(5000);

            string command = init_flags + "\"" + new_file_input + "\" " + video_flags + compression_flags + audio_flags + subtitle_flags +
                             var_flags + "-f matroska \"" + file_output + "\"";

            var start_time = DateTime.Now;
            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffmpeg.exe");
            cmdsi.Arguments = command;
            cmdsi.UseShellExecute = false;
            cmdsi.RedirectStandardOutput = true;
            cmdsi.CreateNoWindow = true;
            cmdsi.RedirectStandardError = true;
            Process cmd = Process.Start(cmdsi);

            int percent_complete = 0;
            double duration = double.Parse(probe.format.duration);
            
            cmd.OutputDataReceived += (s, e) =>
            {
                // Ffmpeg only outputs to ErrorData
            };

            cmd.ErrorDataReceived += (s, e) =>
            {
                // Parse error data so we can collect progress and update the form
                try
                {
                    if (e.Data.Contains("time="))
                    {
                        var split_output = e.Data.Split('=');

                        for (int i = 0; i < split_output.Length; i++)
                        {
                            if (split_output[i].Contains("time"))
                            {
                                percent_complete = (int)Math.Round(DurationStringToSeconds(split_output[i + 1].Split(' ')[0]) / duration * 100);

                                if (percent_complete < 0)
                                    percent_complete = 0;
                                else if (percent_complete > 100)
                                    percent_complete = 100;

                                progressBar.Invoke(new Action(() =>
                                {
                                    progressBar.Value = percent_complete;
                                    progressBar.Update();
                                }));
                            }
                        }
                    }
                }
                catch { }
            };
            
            cmd.BeginErrorReadLine();
            cmd.BeginOutputReadLine();
            cmd.WaitForExit();
            
            DateTime st = DateTime.Now;
            while (true)
            {
                try
                {
                    if (!File.Exists(file_output) && DateTime.Now.Subtract(st).TotalSeconds > 30)
                    {
                        logTextBox.Invoke(new Action(() =>
                        {
                            logTextBox.AppendText("Couldn't find output file after 30 seconds. Original file may be corrupted.\r\n");
                        }));

                        try
                        {
                            File.Delete(file_output);
                            Thread.Sleep(5000);
                            File.Move(new_file_input, file_input);
                        }
                        catch { }

                        return;
                    }

                    if (File.Exists(file_output))
                    {
                        if (ConvertedSuccessfully(new_file_input, file_output))
                        {
                            var input_size = new FileInfo(new_file_input).Length;
                            var output_size = new FileInfo(file_output).Length;
                            float savings = (input_size - output_size) / 1048576;
                            float percent = 1 - ((float)output_size / (float)input_size);

                            logTextBox.Invoke(new Action(() =>
                            {
                                logTextBox.AppendText("Converted successfully in " + DateTime.Now.Subtract(start_time).TotalMinutes.ToString("0.00") + " minutes.\r\n");

                                if (savings > 0)
                                {
                                    logTextBox.AppendText(savings.ToString("0,0") + "mb / " + percent.ToString("P") + " saved.\r\n");

                                    if (deleteOldFiles)
                                    {
                                        try
                                        {
                                            File.Delete(new_file_input);
                                        }
                                        catch { }
                                    }
                                }
                                else
                                {
                                    logTextBox.AppendText("No savings was realized. Deleting conversion.\r\n");

                                    try
                                    {
                                        File.Delete(file_output);
                                        Thread.Sleep(5000);
                                        File.Move(new_file_input, file_input);
                                        return;
                                    }
                                    catch { }
                                }

                            }));

                            return;
                        }
                        else
                        {
                            logTextBox.Invoke(new Action(() =>
                            {
                                logTextBox.AppendText("File length mismatch. Original file may be corrupted.");

                                try
                                {
                                    File.Delete(file_output);
                                    Thread.Sleep(5000);
                                    File.Move(new_file_input, file_input);
                                    return;
                                }
                                catch { }
                            }));

                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logTextBox.Invoke(new Action(() =>
                    {
                        logTextBox.AppendText("Conversion loop error: " + ex.ToString() + "\r\n");
                    }));

                    return;
                }
                Thread.Sleep(100);
            }
        }
    }
}
