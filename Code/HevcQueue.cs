using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static Snacks.Ffprobe;
using static Snacks.FileHandling;
using static Snacks.Tools;
using static Snacks.FormValues;

namespace Snacks
{
    public class HevcQueue
    {
        private bool isSorted = false;
        public int Count { get { return Items.Count; } }

        private class Item
        {
            public string Path = "";
            public long Size = 0;
            public long Bitrate = 0;
            public double Length = 0;
        }

        private List<Item> Items = new List<Item>();

        /// <summary>
        /// Gets the current item in need of work (largest file size first)
        /// </summary>
        /// <returns></returns>
        public string GetWorkItem()
        {
            if (Items.Count == 0) return null;

            if (!isSorted)
            {
                Sort();
            }

            return Items[0].Path;
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
        public void Add(string path, int target_bitrate)
        {
            isSorted = false;
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
                int gig = 1073741824;
                bool is_hevc = false;

                for (int i = 0; i < probe.streams.Length; i++)
                {
                    if (probe.streams[i].codec_type == "video")
                    {
                        length = DurationStringToSeconds(probe.streams[i].duration);
                    }

                    if (probe.streams[i].codec_name == "hevc")
                    {
                        is_hevc = true;
                    }
                }

                Item item = new Item()
                {
                    Path = path,
                    Bitrate = length > 0 ? (long)(size / length) : bitrate,
                    Size = size,
                    Length = length
                };

                // Don't bother with stuff below target bitrate + 700 for audio headroom if already x265
                if (item.Bitrate / 1000 < target_bitrate + 700 && is_hevc)
                {
                    return;
                }

                Items.Add(item);
            }
            catch { return; }
        }

        /// <summary>
        /// Removes an item from the HEVC queue
        /// </summary>
        /// <param name="path"></param>
        public void Remove(string path)
        {
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].Path == path)
                {
                    Items.RemoveAt(i);
                    break;
                }
            }
        }
    }
}
