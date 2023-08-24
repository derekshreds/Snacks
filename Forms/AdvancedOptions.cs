using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.FormDelegates;
using static Snacks.Tools;

namespace Snacks.Forms
{
    public partial class AdvancedOptions : Form
    {
        public AdvancedOptions(EncoderOptions encoderOptions)
        {
            InitializeComponent();

            encodeDirectoryLabel.UpdateText("Encode Directory: " + (encoderOptions.EncodeDirectory != null ? encoderOptions.EncodeDirectory : ""));
            outputDirectoryLabel.UpdateText("Output Directory: " + (encoderOptions.OutputDirectory != null ? encoderOptions.OutputDirectory : ""));

            encodeDirectoryButton.Click += (s, e) =>
            {
                using (var folder = new FolderBrowserDialog())
                {
                    folder.ShowDialog();
                    string path = folder.SelectedPath.Replace('\\', '/');
                    path = path.LastIndexOf('/') != path.Length - 1 ? path + "/" : path;

                    if (folder.SelectedPath != "")
                    {
                        encodeDirectoryLabel.UpdateText("Encode Directory: " + path);
                        encoderOptions.EncodeDirectory = path;
                    }
                    else
                    {
                        encodeDirectoryLabel.UpdateText("Encode Directory: ");
                        encoderOptions.EncodeDirectory = null;
                    }
                }
            };

            outputDirectoryButton.Click += (s, e) =>
            {
                using (var folder = new FolderBrowserDialog())
                {
                    folder.ShowDialog();
                    string path = folder.SelectedPath.Replace('\\', '/');
                    path = path.LastIndexOf('/') != path.Length - 1 ? path + "/" : path;

                    if (folder.SelectedPath != "")
                    {
                        outputDirectoryLabel.UpdateText("Output Directory: " + path);
                        encoderOptions.OutputDirectory = path;
                    }
                    else
                    {
                        outputDirectoryLabel.UpdateText("Output Directory: ");
                        encoderOptions.OutputDirectory = null;
                    }
                }
            };
        }
    }
}
