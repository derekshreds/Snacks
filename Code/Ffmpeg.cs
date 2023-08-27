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

namespace Snacks
{
    public static class Ffmpeg
    {
        /// <summary>
        /// Generates a preview of the video file
        /// </summary>
        /// <param name="path"></param>
        /// <param name="duration"></param>
        public static void GeneratePreview(string path)
        {
            string previewPath = GetStartupDirectory() + "preview.bmp";

            if (File.Exists(previewPath))
                File.Delete(previewPath);

            string command = "-i \"" + path + "\" -s 145x145 -ss 00:00:30 -frames:v 1 \"" + previewPath + "\"";

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
        public static void ConvertVideo(this HevcQueue.WorkItem workItem, EncoderOptions encoderOptions, RichTextBox logTextBox, ProgressBar progressBar)
        {
            logTextBox.AppendLine("Converting " + workItem.FileName);

            int percentComplete = 0;
            string allData = "";
            string targetBitrate;
            string minBitrate;
            string maxBitrate;

            if (encoderOptions.StrictBitrate)
            {
                targetBitrate = encoderOptions.TargetBitrate + "k";
                minBitrate = targetBitrate;
                maxBitrate = targetBitrate;
            }
            else if (workItem.Bitrate < encoderOptions.TargetBitrate + 700 && !workItem.IsHevc)
            {
                targetBitrate = ((int)(workItem.Bitrate * 0.7)).ToString() + "k";
                minBitrate = ((int)(workItem.Bitrate * 0.6)).ToString() + "k";
                maxBitrate = ((int)(workItem.Bitrate * 0.8)).ToString() + "k";
            }
            else
            {
                targetBitrate = encoderOptions.TargetBitrate.ToString() + "k";
                minBitrate = (encoderOptions.TargetBitrate - 200).ToString() + "k";
                maxBitrate = (encoderOptions.TargetBitrate + 500).ToString() + "k";
            }

            string compressionFlags = "-g 25 -b:v " + targetBitrate + " -minrate " + minBitrate +
                                       " -maxrate " + maxBitrate + " -bufsize " + maxBitrate + " ";
            string initFlags = "-y" + " -hwaccel auto" + " -i ";
            string videoFlags = MapVideo(workItem.Probe) + " -c:v " + encoderOptions.Encoder + " -preset medium ";
            string audioFlags = MapAudio(workItem.Probe, encoderOptions.EnglishOnlyAudio, encoderOptions.TwoChannelAudio) + " ";
            string subtitleFlags = MapSub(workItem.Probe, encoderOptions.EnglishOnlySubtitles) + " ";
            string varFlags = "-movflags +faststart -max_muxing_queue_size 9999 ";
            string fileOutput;

            if (encoderOptions.EncodeDirectory != null)
            {
                fileOutput = encoderOptions.EncodeDirectory + workItem.FileName.RemoveExtension() + ".mkv";
            }
            else if (encoderOptions.OutputDirectory != null)
            {
                fileOutput = encoderOptions.OutputDirectory + workItem.FileName.RemoveExtension() + ".mkv";
            }
            else
            {
                fileOutput = workItem.Path.RemoveExtension() + ".mkv";
            }

            string newFileInput = workItem.Path.RemoveExtension() + "-OG." + workItem.Path.GetExtension();

            FileMove(workItem.Path, newFileInput);

            // Just to make sure the file is moved before we begin
            Thread.Sleep(5000);

            string command = initFlags + "\"" + newFileInput + "\" " + videoFlags + compressionFlags + audioFlags + subtitleFlags +
                             varFlags + "-f matroska \"" + fileOutput + "\"";
            allData += command + "\r\n";
            var startTime = DateTime.Now;
            ProcessStartInfo cmdsi = new ProcessStartInfo(GetStartupDirectory() + "ffmpeg.exe");
            cmdsi.Arguments = command;
            cmdsi.UseShellExecute = false;
            cmdsi.RedirectStandardOutput = true;
            cmdsi.CreateNoWindow = true;
            cmdsi.RedirectStandardError = true;
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

                            logTextBox.AppendLine("Converted successfully in " + DateTime.Now.Subtract(startTime).TotalMinutes.ToString("0.00") + " minutes.");

                            if (savings > 0)
                            {
                                logTextBox.AppendLine(savings.ToString("0,0") + "mb / " + percent.ToString("P") + " saved.");

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
    }
}
