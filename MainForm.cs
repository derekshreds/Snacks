using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Ffmpeg;
using static Snacks.Tools;
using static Snacks.FileHandling;
using Newtonsoft.Json;

namespace Snacks
{
    public partial class MainForm : Form
    {
        public string file_location = "";
        public string folder_location = "";
        public HevcQueue hevcQueue = new HevcQueue();

        public MainForm()
        {
            InitializeComponent();
            // Default to showing software encoding and 2000 bitrate
            encoderBox.SelectedIndex = 0;
            targetBitrateBox.SelectedIndex = 2;

            openFileToolStripMenuItem.Click += (s, e) =>
            {
                using (var file = new OpenFileDialog())
                {
                    file.Multiselect = false;
                    file.ShowDialog();
                    string path = file.SafeFileName;
                    string full_path = file.FileName;
                    file_location = "" + file.FileName;
                    folder_location = "";
                    LabelText(workLocationLabel, "File: " + path);
                    progressBar.Value = 0;

                    if (full_path == null || full_path == "")
                    {
                        previewBox.Image = null;
                        previewBox.Update();
                    }

                    Thread t = new Thread(() =>
                    {
                        string preview = GetStartupDirectory() + "preview.bmp";

                        if (File.Exists(preview))
                            File.Delete(preview);

                        GeneratePreview(full_path, "00:00:30");
                        previewBox.Invoke(new Action(() =>
                        {
                            previewBox.ImageLocation = preview;
                            previewBox.SizeMode = PictureBoxSizeMode.StretchImage;
                            previewBox.Update();
                        }));
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
                    string path = folder.SelectedPath;
                    file_location = "";
                    folder_location = "" + path;
                    LabelText(workLocationLabel, "Folder: " + path);

                    // Don't bother processing if no folder selected
                    if (folder_location == null || folder_location == "")
                        return;

                    var dirs = RecursivelyFindDirectories(path);
                    dirs.Add(path);
                    var files = GetAllVideoFiles(dirs);

                    for (int i = 0; i < files.Count; i++)
                    {
                        hevcQueue.Add(files[i]);
                    }
                }

                progressBar.Value = 0;
            };

            aboutToolStripMenuItem.Click += (s, e) =>
            {
                MessageBox.Show("     2023\r\n" +
                                "     Created by Derek Morris     \r\n" +
                                "     github.com/derekshreds     ");
            };

            startButton.Click += (s, e) =>
            {
                startButton.Text = "Converting";
                startButton.Enabled = false;

                Thread t = new Thread(() =>
                {
                    // Single file work
                    if (file_location != "")
                    {
                        FfmpegVideo(logTextBox, progressBar, encoderBox, targetBitrateBox, file_location, removeAudioBox.Checked,
                            convertAudioBox.Checked, removeSubtitlesBox.Checked, deleteFilesBox.Checked);

                        startButton.Invoke(new Action(() =>
                        {
                            startButton.Text = "Start";
                            startButton.Enabled = true;
                        }));
                    }
                    // Library/folder work
                    else if (folder_location != "")
                    {
                        logTextBox.Invoke(new Action(() =>
                        {
                            logTextBox.AppendText(hevcQueue.Count.ToString() + " items need conversion.\r\n");
                        }));

                        while (hevcQueue.GetWorkItem() != null)
                        {
                            var file = hevcQueue.GetWorkItem();
                            string preview = GetStartupDirectory() + "preview.bmp";

                            if (File.Exists(preview))
                                File.Delete(preview);

                            GeneratePreview(file, "00:00:30");
                            previewBox.Invoke(new Action(() =>
                            {
                                previewBox.ImageLocation = preview;
                                previewBox.SizeMode = PictureBoxSizeMode.StretchImage;
                                previewBox.Update();
                            }));

                            FfmpegVideo(logTextBox, progressBar, encoderBox, targetBitrateBox, file, removeAudioBox.Checked,
                                convertAudioBox.Checked, removeSubtitlesBox.Checked, deleteFilesBox.Checked);
                            hevcQueue.Remove(file);
                        }

                        startButton.Invoke(new Action(() =>
                        {
                            startButton.Text = "Start";
                            startButton.Enabled = true;
                        }));
                    }

                });
                t.IsBackground = true;
                t.Start();
            };
        }
    }
}
