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
        public static string SecondsToDurationString(double seconds)
        {
            TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
            return string.Format("{0}:{1}:{2}", timeSpan.Hours, timeSpan.Minutes, timeSpan.Seconds);
        }
    }
}
