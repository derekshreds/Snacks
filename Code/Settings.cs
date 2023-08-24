using Newtonsoft.Json;
using System.IO;
using static Snacks.Tools;

namespace Snacks
{
    public static class Settings
    {
        private static string SettingsFile = GetStartupDirectory() + "settings.ini";

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
