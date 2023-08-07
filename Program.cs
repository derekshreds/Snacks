using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Snacks
{
    class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();

            // Prevent multiple instances of the app
            Process[] preventMultiInstance = Process.GetProcessesByName("Snacks");

            if (preventMultiInstance.Length > 1)
            {
                Environment.Exit(0);
            }

            Application.Run(new MainForm());
        }
    }
}
