using System;
using System.Windows.Forms;

namespace Snacks
{
    public static class Tools
    {
        /// <summary> Returns the startup directory of this application </summary>
        /// <returns> The startup directory of the application </returns>
        public static string GetStartupDirectory()
        {
            return Application.StartupPath.Replace('\\', '/') + '/';
        }
        /// <summary> Convert "00:00:00" duration format to total seconds </summary>
        /// <param name="input"> The input string to convert </param>
        /// <returns> The total duration in seconds </returns>
        public static double DurationStringToSeconds(string input)
        {
            double duration = 0;
            try
            {
                string[] split = input.Split(new char[] { ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (split.Length >=3)
                {
                    // Hour, Minute, Second
                    duration += double.Parse(split[0]) * 3600;
                    duration += double.Parse(split[1]) * 60;
                    duration += double.Parse(split[2]);
                }
                else
                {
                    duration = double.Parse(input);
                }
            }
            catch { }

            return duration;
        }

        /// <summary> Convert total seconds to "00:00:00" duration format </summary>
        /// <param name="input"> The seconds to convert </param>
        /// <returns> The duration format string </returns>
        public static string SecondsToDurationString(double input)
        {
            int hours = (int)(input / 3600);
            int minutes = (int)((input % 3600) / 60);
            int seconds = (int)(input % 60);

            return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
        }
    }
}
