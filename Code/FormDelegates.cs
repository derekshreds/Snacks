using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Tools;

namespace Snacks
{
    public static class FormDelegates
    {
        /// <summary>
        /// Thread-safe return of the encoder type
        /// </summary>
        /// <param name="comboBox"></param>
        /// <returns></returns>
        public static string GetEncoder(this ComboBox comboBox)
        {
            if (comboBox.InvokeRequired)
            {
                string encoder = "";

                comboBox.Invoke(new Action(() =>
                {
                    encoder = GetEncoder(comboBox);
                }));

                return encoder;
            }

            switch (comboBox.SelectedIndex)
            {
                case 0:
                    return "libx265";
                case 1:
                    return "hevc_qsv";
                case 2:
                    return "hevc_nvenc";
                case 3:
                    return "hevc_amf";
            }

            return "libx265";
        }

        /// <summary>
        /// Thread-safe setting the encoder
        /// </summary>
        /// <param name="comboBox"></param>
        /// <param name="input"></param>
        public static void SetEncoder(this ComboBox comboBox, string input)
        {
            if (comboBox.InvokeRequired)
            {
                comboBox.Invoke(new Action(() =>
                {
                    comboBox.SetEncoder(input);
                }));
            }

            switch (input)
            {
                case "libx265":
                    comboBox.SelectedIndex = 0;
                    break;
                case "hevc_qsv":
                    comboBox.SelectedIndex = 1;
                    break;
                case "hevc_nvenc":
                    comboBox.SelectedIndex = 2;
                    break;
                case "hevc_amf":
                    comboBox.SelectedIndex = 3;
                    break;
                default:
                    comboBox.SelectedIndex = 0;
                    break;
            }
        }

        /// <summary>
        /// Thread-safe return of the target bitrate
        /// </summary>
        /// <param name="comboBox"></param>
        /// <returns></returns>
        public static int GetBitrate(this ComboBox comboBox)
        {
            if (comboBox.InvokeRequired)
            {
                int bitrate = 0;

                comboBox.Invoke(new Action(() =>
                {
                    bitrate = GetBitrate(comboBox);
                }));

                return bitrate;
            }
            
            switch (comboBox.SelectedIndex)
            {
                case 0:
                    return 1000;
                case 1:
                    return 1500;
                case 2:
                    return 2000;
                case 3:
                    return 2500;
                case 4:
                    return 3000;
                case 5:
                    return 3500;
                case 6:
                    return 4000;
                case 7:
                    return 4500;
                case 8:
                    return 5000;
                default:
                    break;
            }
            
            try
            {
                int value = int.Parse(comboBox.Text);
                return value;
            }
            catch { }

            return 2000;
        }

        /// <summary>
        /// Thread-safe way to set target bitrate
        /// </summary>
        /// <param name="comboBox"></param>
        /// <param name="bitrate"></param>
        public static void SetBitrate(this ComboBox comboBox, int bitrate)
        {
            if (comboBox.InvokeRequired)
            {
                comboBox.Invoke(new Action(() =>
                {
                    comboBox.SetBitrate(bitrate);
                }));
            }

            switch (bitrate)
            {
                case 1000:
                    comboBox.SelectedIndex = 0;
                    return;
                case 1500:
                    comboBox.SelectedIndex = 1;
                    return;
                case 2000:
                    comboBox.SelectedIndex = 2;
                    return;
                case 2500:
                    comboBox.SelectedIndex = 3;
                    return;
                case 3000:
                    comboBox.SelectedIndex = 4;
                    return;
                case 3500:
                    comboBox.SelectedIndex = 5;
                    return;
                case 4000:
                    comboBox.SelectedIndex = 6;
                    return;
                case 4500:
                    comboBox.SelectedIndex = 7;
                    return;
                case 5000:
                    comboBox.SelectedIndex = 8;
                    return;
                default:
                    comboBox.SelectedIndex = -1;
                    break;
            }

            comboBox.Text = bitrate.ToString();
        }

        /// <summary>
        /// Thread-safe way to append a line to a RichTextBox
        /// </summary>
        /// <param name="richTextBox"></param>
        /// <param name="input"></param>
        public static void AppendLine<T>(this RichTextBox richTextBox, T input)
        {
            if (richTextBox.InvokeRequired)
            {
                richTextBox.Invoke(new Action(() =>
                {
                    richTextBox.AppendLine(input);
                }));
            }
            else
            {
                richTextBox.AppendText(input.ToString() + "\r\n");
            }
        }

        /// <summary>
        /// Thread-safe way to update the ProgressBar value
        /// </summary>
        /// <param name="progressBar"></param>
        /// <param name="value"></param>
        public static void UpdateValue(this ProgressBar progressBar, int value)
        {
            if (progressBar.InvokeRequired)
            {
                progressBar.Invoke(new Action(() =>
                {
                    progressBar.UpdateValue(value);
                }));
            }
            else
            {
                if (value < 0)
                {
                    progressBar.Value = 0;
                }
                else if (value > 100)
                {
                    progressBar.Value = 100;
                }
                else
                {
                    progressBar.Value = value;
                }

                progressBar.Update();
            }
        }

        /// <summary>
        /// Thread-safe way of updating label text
        /// </summary>
        /// <param name="label"></param>
        /// <param name="text"></param>
        public static void UpdateText(this Label label, string text)
        {
            if (label.InvokeRequired)
            {
                label.Invoke(new Action(() =>
                {
                    label.UpdateText(text);
                }));
            }
            else
            {
                label.Text = text;
            }
        }

        /// <summary>
        /// Thread-safe way of updating button text
        /// </summary>
        /// <param name="button"></param>
        /// <param name="text"></param>
        public static void UpdateText(this Button button, string text)
        {
            if (button.InvokeRequired)
            {
                button.Invoke(new Action(() =>
                {
                    button.UpdateText(text);
                }));
            }
            else
            {
                button.Text = text;
            }
        }

        /// <summary>
        /// Thread-safe way of updating a picturebox image
        /// </summary>
        /// <param name="pictureBox"></param>
        /// <param name="path"></param>
        public static void UpdatePicture(this PictureBox pictureBox)
        {
            if (pictureBox.InvokeRequired)
            {
                pictureBox.Invoke(new Action(() =>
                {
                    pictureBox.UpdatePicture();
                }));
            }
            else
            {
                pictureBox.ImageLocation = GetStartupDirectory() + "preview.bmp";
                pictureBox.SizeMode = PictureBoxSizeMode.StretchImage;
                pictureBox.Update();
            }
        }
    }
}
