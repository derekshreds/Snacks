using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Snacks
{
    public static class Extensions
    {
        /// <summary>
        /// Remove all forms of line breaks from input string
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string RemoveBreaks(this string input)
        {
            return input.Replace("\r\n", "").Replace("\r", "").Replace("\n", "");
        }

        /// <summary>
        /// Create a copy of the string, instead of a reference.
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public static string Duplicate(this string input)
        {
            string output = "" + input;
            return output;
        }
    }
}