using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Ffprobe;
using static Snacks.Tools;

namespace Snacks
{
    public class HevcQueue
    {
        private bool isSorted = false;
        public int Count { get { return Items.Count; } }

        public class WorkItem
        {
            public string FileName = "";
            public string Path = "";
            public long Size = 0;
            public long Bitrate = 0;
            public double Length = 0;
            public bool IsHevc = false;
            public ProbeResult Probe;
        }

        private List<WorkItem> Items = new List<WorkItem>();

        /// <summary>
        /// Gets the current item in need of work (largest file size first)
        /// </summary>
        /// <returns></returns>
        public WorkItem GetWorkItem()
        {
            if (Items.Count == 0) return null;

            if (!isSorted)
            {
                Sort();
            }

            return Items[0];
        }

        /// <summary>
        /// Sorts the items in the HEVC queue by descending bitrate
        /// </summary>
        public void Sort()
        {
            var items = Items.OrderByDescending(x => x.Bitrate).ToList();
            Items = items;
            isSorted = true;
        }

        /// <summary>
        /// Clears the queue
        /// </summary>
        public void Clear()
        {
            Items.Clear();
            isSorted = false;
        }

        /// <summary>
        /// Adds an item to the HEVC queue
        /// </summary>
        /// <param name="path"></param>
        public void Add(string path, int targetBitrate)
        {
            long size;
            long bitrate;
            double length = 0;

            // If ffprobe fails, assume file is corrupted
            try
            {
                FileInfo f = new FileInfo(path);
                ProbeResult probe = Probe(path);
                bitrate = long.Parse(probe.format.bit_rate);
                size = f.Length;
                bool isHevc = false;

                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "video")
                    {
                        if (probe.streams[i].codec_name == "hevc")
                        {
                            isHevc = true;
                        }

                        double formatDuration = DurationStringToSeconds(probe.format.duration);
                        double streamDuration = DurationStringToSeconds(probe.streams[i].duration);
                        var duration = formatDuration > streamDuration ? formatDuration : streamDuration;
                        length = duration;
                    }
                }

                WorkItem item = new WorkItem()
                {
                    FileName = path.GetFileName(),
                    Path = path,
                    // Convert bytes to bits before calculating
                    Bitrate = length > 0 ? (long)(size * 8 / length / 1000) : bitrate,
                    Size = size,
                    Length = length,
                    IsHevc = isHevc,
                    Probe = probe
                };

                // Don't bother with stuff below target bitrate + 700 for audio headroom if already x265
                if (item.Bitrate < targetBitrate + 700 && isHevc)
                {
                    return;
                }

                Items.Add(item);
                isSorted = false;
            }
            catch { return; }
        }

        /// <summary>
        /// Removes an item from the HEVC queue
        /// </summary>
        /// <param name="path"></param>
        public void Remove(WorkItem item)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Path == item.Path)
                {
                    Items.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
