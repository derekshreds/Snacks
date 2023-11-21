using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Ffmpeg;
using static Snacks.Tools;
using static Snacks.FileHandling;
using static Snacks.FormDelegates;
using Snacks.Forms;
using Newtonsoft.Json;

namespace Snacks
{
    /// <summary> The main form of the application </summary>
    public partial class MainForm : Form
    {
        public WorkQueue hevcQueue = new WorkQueue();
        public EncoderOptions encoderOptions;
        public bool isConverting = false;
        public bool breakConversion = false;

        public MainForm()
        {
            InitializeComponent();

            encoderOptions = Settings.ReadSettings();
            // Apply settings
            convertAudioBox.Checked = encoderOptions.TwoChannelAudio;
            deleteFilesBox.Checked = encoderOptions.DeleteOriginalFile;
            removeAudioBox.Checked = encoderOptions.EnglishOnlyAudio;
            removeSubtitlesBox.Checked = encoderOptions.EnglishOnlySubtitles;
            removeBlackBordersBox.Checked = encoderOptions.RemoveBlackBorders;
            retryOnFailBox.Checked = encoderOptions.RetryOnFail;
            formatBox.SetFormat(encoderOptions.Format);
            codecBox.SetCodec(encoderOptions.Codec);
            encoderBox.SetEncoder(encoderOptions.Encoder);
            targetBitrateBox.SetBitrate(encoderOptions.TargetBitrate > 0 ? encoderOptions.TargetBitrate : 2000);
            strictBitrateBox.Checked = encoderOptions.StrictBitrate;

            #region ToolStrip
            openFileToolStripMenuItem.Click += (s, e) =>
            {
                using (var file = new OpenFileDialog())
                {
                    file.Multiselect = false;
                    file.ShowDialog();

                    if (file.FileName == "")
                        return;

                    string fileLocation = file.FileName.Replace('\\', '/');
                    workLocationLabel.UpdateText("File: " + fileLocation);
                    progressBar.UpdateValue(0);

                    Thread t = new Thread(() =>
                    {
                        hevcQueue.Clear();
                        hevcQueue.Add(fileLocation);
                        GeneratePreview(fileLocation);
                        previewBox.UpdatePicture();
                        filesRemainingLabel.UpdateText("Files Remaining: 1");
                    });
                    t.IsBackground = true;
                    t.Start();
                }
            };

            openFolderToolStripMenuItem.Click += (s, e) =>
            {
                using (var folder = new FolderBrowserDialog())
                {
                    folder.ShowDialog();

                    if (folder.SelectedPath == "")
                        return;

                    string folderLocation = folder.SelectedPath.Replace('\\', '/');
                    workLocationLabel.UpdateText("Folder: " + folderLocation);
                    progressBar.UpdateValue(0);

                    Thread t = new Thread(() =>
                    {
                        hevcQueue.Clear();
                        var folders = RecursivelyFindDirectories(folderLocation);
                        var files = GetAllVideoFiles(folders);
                        var targetBitrate = targetBitrateBox.GetBitrate();

                        for (int i = 0; i < files.Count; i++)
                        {
                            hevcQueue.Add(files[i]);
                        }

                        filesRemainingLabel.UpdateText("Files Remaining: " + hevcQueue.Count.ToString());
                    });
                    t.IsBackground = true;
                    t.Start();
                }
            };

            optionsToolStripMenuItem.Click += (s, e) =>
            {
                var form = new AdvancedOptions(encoderOptions);
                form.ShowDialog();
            };

            aboutToolStripMenuItem.Click += (s, e) =>
            {
                MessageBox.Show("     2023\r\n" +
                                "     Created by Derek Morris     \r\n" +
                                "     github.com/derekshreds     ");
            };
            #endregion

            startButton.Click += (s, e) =>
            {
                encoderOptions.Format = formatBox.GetFormat();
                encoderOptions.Codec = codecBox.GetCodec();
                encoderOptions.Encoder = encoderBox.GetEncoder(codecBox);
                encoderOptions.TargetBitrate = targetBitrateBox.GetBitrate();
                encoderOptions.TwoChannelAudio = convertAudioBox.Checked;
                encoderOptions.DeleteOriginalFile = deleteFilesBox.Checked;
                encoderOptions.EnglishOnlyAudio = removeAudioBox.Checked;
                encoderOptions.EnglishOnlySubtitles = removeSubtitlesBox.Checked;
                encoderOptions.RemoveBlackBorders = removeBlackBordersBox.Checked;
                encoderOptions.RetryOnFail = retryOnFailBox.Checked;
                encoderOptions.StrictBitrate = strictBitrateBox.Checked;

                if (!isConverting && hevcQueue.Count > 0)
                {
                    isConverting = true;
                    startButton.UpdateText("Stop Converting");

                    Thread t = new Thread(() =>
                    {
                        logTextBox.AppendLine(hevcQueue.Count.ToString() + " items need conversion.");

                        while (hevcQueue.GetWorkItem() != null)
                        {
                            filesRemainingLabel.UpdateText("Files Remaining: " + hevcQueue.Count.ToString());

                            var workItem = hevcQueue.GetWorkItem();
                            GeneratePreview(workItem.Path);
                            previewBox.UpdatePicture();

                            workItem.ConvertVideo(encoderOptions, logTextBox, progressBar);
                            hevcQueue.Remove(workItem);

                            if (breakConversion)
                            {
                                breakConversion = false;
                                isConverting = false;
                                break;
                            }
                        }

                        isConverting = false;
                        filesRemainingLabel.UpdateText("Files Remaining: " + hevcQueue.Count.ToString());
                        startButton.UpdateText("Start");
                    });
                    t.IsBackground = true;
                    t.Start();
                }
                else if (!breakConversion && hevcQueue.Count > 0)
                {
                    breakConversion = true;
                    logTextBox.AppendLine("Conversion process stopped. The current file will still finish encoding.");
                    startButton.UpdateText("Start");
                }
            };

            targetBitrateBox.KeyPress += (s, e) =>
            {
                // If isn't a backspace or digit 0-9, prevent input
                if ((!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) || e.KeyChar == '.')
                {
                    e.Handled = true;
                }
            };

            this.FormClosing += (s, e) =>
            {
                try
                {
                    encoderOptions.Format = formatBox.GetFormat();
                    encoderOptions.Codec = codecBox.GetCodec();
                    encoderOptions.Encoder = encoderBox.GetEncoder(codecBox);
                    encoderOptions.TargetBitrate = targetBitrateBox.GetBitrate();
                    encoderOptions.TwoChannelAudio = convertAudioBox.Checked;
                    encoderOptions.DeleteOriginalFile = deleteFilesBox.Checked;
                    encoderOptions.EnglishOnlyAudio = removeAudioBox.Checked;
                    encoderOptions.EnglishOnlySubtitles = removeSubtitlesBox.Checked;
                    encoderOptions.RemoveBlackBorders = removeBlackBordersBox.Checked;
                    encoderOptions.RetryOnFail = retryOnFailBox.Checked;
                    encoderOptions.StrictBitrate = strictBitrateBox.Checked;
                    Settings.WriteSettings(encoderOptions);
                }
                catch { }
            };
        }
    }
}
