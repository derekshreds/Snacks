using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Snacks
{
    public static class FormValues
    {
        /// <summary>
        /// Thread-safe return of the encoder type
        /// </summary>
        /// <param name="comboBox"></param>
        /// <returns></returns>
        public static string GetEncoder(ComboBox comboBox)
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
        /// Thread-safe return of the target bitrate
        /// </summary>
        /// <param name="comboBox"></param>
        /// <returns></returns>
        public static int GetBitrate(ComboBox comboBox)
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
            }

            return 2000;
        }
    }
}
