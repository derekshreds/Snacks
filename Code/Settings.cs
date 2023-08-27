using Newtonsoft.Json;
using System.IO;
using static Snacks.Tools;

namespace Snacks
{
    public static class Settings
    {
        private static string SettingsFile = GetStartupDirectory() + "settings.ini";

        /// <summary>
        /// Reads the Snacks settings file
        /// </summary>
        /// <returns></returns>
        public static EncoderOptions ReadSettings()
        {
            EncoderOptions encoderOptions = new EncoderOptions();
            string settings;

            if (File.Exists(SettingsFile))
            {
                try
                {
                    settings = File.ReadAllText(SettingsFile);
                    encoderOptions = JsonConvert.DeserializeObject<EncoderOptions>(settings);
                }
                catch { }
            }

            return encoderOptions;
        }

        /// <summary>
        /// Writes current Snacks settings to file
        /// </summary>
        /// <param name="encoderOptions"></param>
        public static void WriteSettings(EncoderOptions encoderOptions)
        {
            try
            {
                string settings = JsonConvert.SerializeObject(encoderOptions);
                File.WriteAllText(SettingsFile, settings);
            }
            catch { }
        }
    }
}
