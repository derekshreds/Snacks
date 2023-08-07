using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Snacks
{
    public partial class NoDesigner { } // Hide VS designer

    public partial class MainForm : Form
    {
        public delegate void LabelDelegate(Label label, string content);
        /// <summary>
        /// Thread-safe way of updating a label component
        /// </summary>
        /// <param name="label"></param>
        /// <param name="text"></param>
        public void LabelText(Label label, string text)
        {
            if (this.InvokeRequired)
            {
                var d = new LabelDelegate(LabelText);
                Invoke(d, new object[] { label, text });
            }
            else
            {
                label.Text = text;
            }
        }
    }
}
