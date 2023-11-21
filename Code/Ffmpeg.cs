using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using static Snacks.Ffprobe;
using static Snacks.Tools;
using static Snacks.FileHandling;
using static Snacks.FormDelegates;
using System.Runtime.InteropServices;
using System.Linq;

namespace Snacks
{
    /// <summary> The class that handles all ffmpeg related logic </summary>
    public static class Ffmpeg
    {
        /// <summary> Generates a preview of the video file </summary>
        /// <param name="path"> The path of the file </param>
        public static void GeneratePreview(string path)
        {
            string previewPath = GetStartupDirectory() + "preview.bmp";

            if (File.Exists(previewPath))
                File.Delete(previewPath);

            string command = $"-i \"{path}\" -s 145x145 -ss 00:00:30 -frames:v 1 \"{previewPath}\"";

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

        /// <summary> Converts video using dynamic compression based on algorithms and flags </summary>
        /// <param name="workItem"> The WorkItem to convert </param>
        /// <param name="encoderOptions"> The encoder options to use </param>
        /// <param name="logTextBox"> The textbox to log to </param>
        /// <param name="progressBar"> The progress bar to update status with </param>
        public static void ConvertVideo(this WorkQueue.WorkItem workItem, EncoderOptions encoderOptions, RichTextBox logTextBox, ProgressBar progressBar)
        {
            int percentComplete = 0;
            string allData = "";
            string targetBitrate;
            string minBitrate;
            string maxBitrate;
            bool video_copy = false;

            if (encoderOptions.StrictBitrate)
            {
                targetBitrate = $"{encoderOptions.TargetBitrate}k";
                minBitrate = targetBitrate;
                maxBitrate = targetBitrate;
            }
            else if (workItem.Bitrate < encoderOptions.TargetBitrate + 700 && !workItem.IsHevc)
            {
                targetBitrate = $"{(int)(workItem.Bitrate * 0.7)}k";
                minBitrate = $"{(int)(workItem.Bitrate * 0.6)}k";
                maxBitrate = $"{(int)(workItem.Bitrate * 0.8)}k";
            }
            else
            {
                targetBitrate = $"{encoderOptions.TargetBitrate}k";
                minBitrate = $"{encoderOptions.TargetBitrate - 200}k";
                maxBitrate = $"{encoderOptions.TargetBitrate + 500}k";
            }

            string compressionFlags = "-g 25 -b:v " + targetBitrate + " -minrate " + minBitrate +
                                       " -maxrate " + maxBitrate + " -bufsize " + maxBitrate + " ";
            string initFlags = "-y -hwaccel auto -i ";
            string videoFlags = $"{MapVideo(workItem.Probe)} -c:v {encoderOptions.Encoder} -preset medium ";
            string audioFlags = MapAudio(workItem.Probe, encoderOptions.EnglishOnlyAudio, encoderOptions.TwoChannelAudio, encoderOptions.Format == "mkv") + " ";
            string subtitleFlags = MapSub(workItem.Probe, encoderOptions.EnglishOnlySubtitles, encoderOptions.Format == "mkv") + " ";
            string varFlags = "-movflags +faststart -max_muxing_queue_size 9999 ";
            string fileOutput;

            if (workItem.Bitrate > encoderOptions.TargetBitrate + 700 && workItem.IsHevc && !encoderOptions.RemoveBlackBorders)
            {
                compressionFlags = "";
                videoFlags = $"{MapVideo(workItem.Probe)} -c:v copy ";
                video_copy = true;
            }

            if (encoderOptions.EncodeDirectory != null)
                fileOutput = encoderOptions.EncodeDirectory + workItem.FileName.RemoveExtension();
            else if (encoderOptions.OutputDirectory != null)
                fileOutput = encoderOptions.OutputDirectory + workItem.FileName.RemoveExtension();
            else
                fileOutput = workItem.Path.RemoveExtension();

            if (encoderOptions.RemoveBlackBorders)
                videoFlags += $" {GetCropParameters(workItem, encoderOptions, logTextBox)} ";

            // Will have to rewrite this code to add more formats, but this will work for now
            fileOutput += encoderOptions.Format == "mkv" ? ".mkv" : ".mp4";
            string newFileInput = workItem.Path.RemoveExtension() + "-OG." + workItem.Path.GetExtension();
            FileMove(workItem.Path, newFileInput);

            // Just to make sure the file is moved before we begin
            Thread.Sleep(5000);

            string command = initFlags + $"\"{newFileInput}\" " + videoFlags + compressionFlags + audioFlags + subtitleFlags +
                             varFlags + $"-f {(encoderOptions.Format == "mkv" ? "matroska" : "mp4")} \"{fileOutput}\"";
            allData += command + "\r\n";
            var startTime = DateTime.Now;
            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffmpeg.exe");
            cmdsi.Arguments = command;
            cmdsi.UseShellExecute = false;
            cmdsi.RedirectStandardOutput = true;
            cmdsi.CreateNoWindow = true;
            cmdsi.RedirectStandardError = true;

            logTextBox.AppendLine("Converting " + workItem.FileName);
            Process cmd = Process.Start(cmdsi);

            cmd.OutputDataReceived += (s, e) =>
            {
                // Ffmpeg only outputs to ErrorData
            };

            cmd.ErrorDataReceived += (s, e) =>
            {
                allData += e.Data;
                // Parse error data so we can collect progress and update the form
                try
                {
                    if (e.Data.Contains("time="))
                    {
                        var splitOutput = e.Data.Split('=');

                        for (int i = 0; i < splitOutput.Length; i++)
                        {
                            if (splitOutput[i].Contains("time"))
                            {
                                var seconds = DurationStringToSeconds(splitOutput[i + 1]);
                                percentComplete = (int)Math.Round(seconds / workItem.Length * 100);
                                progressBar.UpdateValue(percentComplete);
                            }
                        }
                    }
                }
                catch { }
            };
            
            cmd.BeginErrorReadLine();
            cmd.BeginOutputReadLine();
            cmd.WaitForExit();

            try
            {
                string logDirectory = GetStartupDirectory() + "Logs/";
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);
                string logName = logDirectory + fileOutput.GetFileName() + "-log.txt";
                File.WriteAllText(logName, allData);
            }
            catch { }
            
            DateTime st = DateTime.Now;
            while (true)
            {
                try
                {
                    if (!File.Exists(fileOutput) && DateTime.Now.Subtract(st).TotalSeconds > 10)
                    {
                        logTextBox.AppendLine("Couldn't find output file after 10 seconds.");

                        if (encoderOptions.RetryOnFail && encoderOptions.Encoder != "libx265")
                        {
                            logTextBox.AppendLine("Retrying with software encoding.");
                            FileMove(newFileInput, workItem.Path);
                            encoderOptions.Encoder = "libx265";
                            workItem.ConvertVideo(encoderOptions, logTextBox, progressBar);
                        }
                        else if (encoderOptions.RetryOnFail && encoderOptions.Encoder == "libx265")
                        {
                            logTextBox.AppendLine("Software encoding failed. Not retrying.");
                        }

                        return;
                    }

                    if (File.Exists(fileOutput))
                    {
                        ProbeResult outputProbe = Probe(fileOutput);
                        if (ConvertedSuccessfully(workItem.Probe, outputProbe))
                        {
                            var outputSize = new FileInfo(fileOutput).Length;
                            float savings = (workItem.Size - outputSize) / 1048576;
                            float percent = 1 - ((float)outputSize / (float)workItem.Size);

                            logTextBox.AppendLine($"Converted successfully in {DateTime.Now.Subtract(startTime).TotalMinutes:0.00} minutes.");

                            if (savings > 0 || video_copy)
                            {
                                logTextBox.AppendLine($"{savings:0,0}mb / {percent:P} saved.");

                                if (encoderOptions.DeleteOriginalFile)
                                {
                                    try
                                    {
                                        File.Delete(newFileInput);
                                    }
                                    catch { }
                                }

                                if (encoderOptions.EncodeDirectory != null && encoderOptions.OutputDirectory != null)
                                {
                                    string newOutputLocation = encoderOptions.OutputDirectory + fileOutput.GetFileName();
                                    FileMove(fileOutput, newOutputLocation);
                                }
                                else if (encoderOptions.EncodeDirectory != null && encoderOptions.OutputDirectory == null)
                                {
                                    string newOutputLocation = workItem.Path.GetDirectory() + fileOutput.GetFileName();
                                    FileMove(fileOutput, newOutputLocation);
                                }
                            }
                            else
                            {
                                logTextBox.AppendLine("No savings was realized. Deleting conversion.");

                                try
                                {
                                    File.Delete(fileOutput);
                                    Thread.Sleep(5000);
                                    FileMove(newFileInput, workItem.Path);
                                }
                                catch { }
                            }

                            return;
                        }
                        else
                        {
                            logTextBox.AppendLine("File length mismatch. Original file may be corrupted.");
                            try
                            {
                                File.Delete(fileOutput);
                                Thread.Sleep(5000);
                                FileMove(newFileInput, workItem.Path);
                            }
                            catch { }
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logTextBox.AppendLine("Conversion loop error: " + ex.ToString());

                    return;
                }
                Thread.Sleep(100);
            }
        }

        /// <summary> Parses a video to find the aspect ratio for cropping out black bars </summary>
        /// <param name="workItem"> The work item to parse </param>
        /// <param name="encoderOptions"> The encoder options to use </param>
        /// <param name="logTextBox"> The textbox to update </param>
        /// <returns> A video filter string for cropping the video </returns>
        public static string GetCropParameters(this WorkQueue.WorkItem workItem, EncoderOptions encoderOptions, RichTextBox logTextBox)
        {
            logTextBox.AppendLine("Getting crop values.");

            int length_in_mins = (int)workItem.Length / 60;
            string command = $"-y -hwaccel auto -i \"{workItem.Path}\" -ss {(length_in_mins > 20 ? "00:10:00" : "00:00:00")} -t {(length_in_mins > 20 ? "00:10:00" : $"00:{length_in_mins:D2}:00")} -vf cropdetect=24:2:8 -c:v {encoderOptions.Encoder} -f null -";

            var startTime = DateTime.Now;
            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffmpeg.exe");
            cmdsi.Arguments = command;
            cmdsi.UseShellExecute = false;
            cmdsi.RedirectStandardOutput = true;
            cmdsi.CreateNoWindow = true;
            cmdsi.RedirectStandardError = true;
            Process cmd = Process.Start(cmdsi);

            int width = 0;
            int height = 0;
            int x_offset = 0;
            int y_offset = 0;
            int w = 0;
            int h = 0;
            int x = 0;
            int y = 0;
            Dictionary<int, int> _w = new Dictionary<int, int>();
            Dictionary<int, int> _h = new Dictionary<int, int>();
            Dictionary<int, int> _x = new Dictionary<int, int>();
            Dictionary<int, int> _y = new Dictionary<int, int>();

            cmd.OutputDataReceived += (s, e) =>
            {
                // Ffmpeg only outputs to ErrorData
            };

            cmd.ErrorDataReceived += (s, e) =>
            {
                // Parse error data so we can collect progress and update the form
                try
                {
                    if (e.Data.Contains("crop="))
                    {
                        var splitOutput = e.Data.Split(new string[] { "crop=" }, StringSplitOptions.None);

                        if (splitOutput.Length > 1 && splitOutput[1].Contains(":"))
                        {
                            var secondSplit = splitOutput[1].Split(new char[] { ':' }, StringSplitOptions.RemoveEmptyEntries);

                            if (secondSplit.Length > 3)
                            {
                                if (int.TryParse(secondSplit[0], out w) && int.TryParse(secondSplit[1], out h) &&
                                    int.TryParse(secondSplit[2], out x) && int.TryParse(secondSplit[3], out y))
                                {
                                    if (!_w.TryGetValue(w, out _))
                                        _w.Add(w, 1);
                                    else
                                        _w[w]++;

                                    if (!_h.TryGetValue(h, out _))
                                        _h.Add(h, 1);
                                    else
                                        _h[h]++;

                                    if (!_x.TryGetValue(x, out _))
                                        _x.Add(x, 1);
                                    else
                                        _x[x]++;

                                    if (!_y.TryGetValue(y, out _))
                                        _y.Add(y, 1);
                                    else
                                        _y[y]++;
                                }
                            }
                        }
                    }
                }
                catch { }
            };

            cmd.BeginErrorReadLine();
            cmd.BeginOutputReadLine();
            cmd.WaitForExit();
            Thread.Sleep(5000);
            width = GetHighestValue(_w);
            height = GetHighestValue(_h);
            x_offset = GetHighestValue(_x);
            y_offset = GetHighestValue(_y);

            // Don't mix with a filter, if it isn't filtering
            if (x_offset == 0 && y_offset == 0)
                return "";

            return $"-vf \"crop={width}:{height}:{x_offset}:{y_offset}\"";
        }

        /// <summary> Gets the key with the highest value </summary>
        /// <param name="dictionary"> The dictionary to search </param>
        /// <returns> The key of the highest value in the dictionary </returns>
        public static int GetHighestValue(Dictionary<int, int> dictionary)
        {
            return dictionary.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;
        }
    }
}
