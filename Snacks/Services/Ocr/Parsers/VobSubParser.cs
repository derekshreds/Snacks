using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Snacks.Services.Ocr.Parsers;

/// <summary>
///     Decoder for VobSub / DVD subtitle streams (<c>dvd_subtitle</c> / <c>dvdsub</c>).
///     Reads the plain-text <c>.idx</c> companion for the palette, dimensions, and
///     per-cue timestamps/offsets, then demuxes the MPEG-2 Program Stream <c>.sub</c>
///     file to recover Sub-Picture Units (SPUs) and RLE-decode them into bitmaps.
/// </summary>
/// <remarks>
///     <para>
///         VobSub quality with Tesseract is markedly worse than PGS because the source is
///         720×480 (or 720×576) with a 4-colour palette. We upscale 3× in <see cref="ImagePreprocessor"/>
///         to compensate, but expect occasional recognition errors on tight or stylised fonts.
///     </para>
///     <para>
///         Written from the public DVD-Video specification and the VobSub IDX grammar used
///         by VSRip / MPlayer.
///     </para>
/// </remarks>
internal static class VobSubParser
{
    public static async IAsyncEnumerable<BitmapEvent> ParseAsync(
        string idxPath, string subPath, [EnumeratorCancellation] CancellationToken ct)
    {
        var idx = await ParseIdxAsync(idxPath, ct);
        if (idx.Entries.Count == 0) yield break;

        // Read the whole .sub file — these are <~50 MB for even long films.
        var subData = await File.ReadAllBytesAsync(subPath, ct);

        for (int i = 0; i < idx.Entries.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = idx.Entries[i];

            // Reassemble the SPU by walking PES packets starting at this filepos.
            var spu = ReadSpu(subData, (int)entry.FilePos);
            if (spu.Length < 4) continue;

            if (!TryDecodeSpu(spu, idx.Width, idx.Height, idx.Palette, out var rgba, out var w, out var h, out var startDelay, out var endDelay))
                continue;

            var start = entry.Timestamp + startDelay;
            var end   = entry.Timestamp + endDelay;
            if (end <= start)
            {
                // Some discs omit the stop command — fall back to the next cue's start,
                // or to a sensible 4-second default on the last cue.
                end = (i + 1 < idx.Entries.Count)
                    ? idx.Entries[i + 1].Timestamp
                    : start + TimeSpan.FromSeconds(4);
            }

            if (rgba is not null) yield return new BitmapEvent(start, end, rgba, w, h);
        }
    }

    /******************************************************************
     *  .idx parsing
     ******************************************************************/

    private sealed class IdxFile
    {
        public int     Width   { get; set; } = 720;
        public int     Height  { get; set; } = 480;
        public uint[]  Palette { get; set; } = new uint[16];  // RGB packed 0x00RRGGBB
        public List<IdxEntry> Entries { get; } = new();
    }

    private readonly record struct IdxEntry(TimeSpan Timestamp, long FilePos);

    private static async Task<IdxFile> ParseIdxAsync(string path, CancellationToken ct)
    {
        var idx = new IdxFile();
        foreach (var raw in await File.ReadAllLinesAsync(path, ct))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            if (line.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                var v = line[5..].Trim().Split('x');
                if (v.Length == 2
                    && int.TryParse(v[0], out int w)
                    && int.TryParse(v[1], out int h))
                {
                    idx.Width  = w;
                    idx.Height = h;
                }
            }
            else if (line.StartsWith("palette:", StringComparison.OrdinalIgnoreCase))
            {
                var entries = line[8..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(16, entries.Length); i++)
                {
                    if (uint.TryParse(entries[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                        idx.Palette[i] = rgb & 0x00FFFFFFu;
                }
            }
            else if (line.StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase))
            {
                // timestamp: HH:MM:SS:mmm, filepos: XXXXXXXXXX
                var parts = line[10..].Split(',', 2);
                if (parts.Length != 2) continue;
                var ts = ParseIdxTimestamp(parts[0].Trim());
                var fp = parts[1].Trim();
                int colon = fp.IndexOf(':');
                if (colon < 0) continue;
                var hex = fp[(colon + 1)..].Trim();
                if (long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long filePos))
                    idx.Entries.Add(new IdxEntry(ts, filePos));
            }
        }
        // IDX files aren't always sorted by time; enforce order so end-time fallback works.
        idx.Entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return idx;
    }

    private static TimeSpan ParseIdxTimestamp(string s)
    {
        var p = s.Split(':');
        if (p.Length != 4) return TimeSpan.Zero;
        return new TimeSpan(
            0,
            int.TryParse(p[0], out int hh) ? hh : 0,
            int.TryParse(p[1], out int mm) ? mm : 0,
            int.TryParse(p[2], out int ss) ? ss : 0,
            int.TryParse(p[3], out int ms) ? ms : 0);
    }

    /******************************************************************
     *  MPEG-2 PS demux 
     *    — extract contiguous private_stream_1 (0xBD) payload for one SPU
     ******************************************************************/

    private static byte[] ReadSpu(byte[] data, int startPos)
    {
        // The first pack_start_code at or after startPos is the SPU's first PES pack.
        int pos = startPos;
        using var buf = new MemoryStream();
        int spuSize  = -1;
        int gathered = 0;

        while (pos + 14 < data.Length)
        {
            // pack_start_code 0x000001BA
            if (data[pos] != 0x00 || data[pos + 1] != 0x00 || data[pos + 2] != 0x01 || data[pos + 3] != 0xBA) break;
            // MPEG-2 pack header is at least 14 bytes; bits 0-1 of byte 13 give the stuffing length.
            int stuff = data[pos + 13] & 0x07;
            pos += 14 + stuff;

            // Optional system header (0x000001BB) may follow — skip it.
            if (pos + 6 <= data.Length
                && data[pos] == 0x00 && data[pos + 1] == 0x00 && data[pos + 2] == 0x01 && data[pos + 3] == 0xBB)
            {
                int shLen = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 4, 2));
                pos += 6 + shLen;
            }

            // PES packet. For DVD subs we expect 0x000001BD (private_stream_1).
            if (pos + 9 > data.Length) break;
            if (data[pos] != 0x00 || data[pos + 1] != 0x00 || data[pos + 2] != 0x01) break;
            byte streamId = data[pos + 3];
            int  pesLen   = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 4, 2));
            int  pesHdr   = 6; // start code + stream id + length
            if (pos + pesHdr + pesLen > data.Length) break;

            if (streamId != 0xBD) { pos += pesHdr + pesLen; continue; }

            // PES header: 3 byte header flags then PES_header_data_length bytes.
            byte pesHdrDataLen = data[pos + 8];
            int payloadOff = pos + 9 + pesHdrDataLen;
            int payloadEnd = pos + pesHdr + pesLen;
            // First byte of payload is the DVD substream ID (0x20-0x3F).
            // We tolerate any substream ID here because the caller already selected one stream.
            payloadOff += 1;
            if (payloadOff < payloadEnd)
                buf.Write(data, payloadOff, payloadEnd - payloadOff);

            gathered = (int)buf.Length;
            if (spuSize < 0 && gathered >= 2)
            {
                // SPU's first two bytes are the total size (big-endian).
                var bytes = buf.ToArray();
                spuSize = (bytes[0] << 8) | bytes[1];
                buf.SetLength(0);
                buf.Write(bytes, 0, bytes.Length);
            }
            if (spuSize > 0 && gathered >= spuSize) break;

            pos = payloadEnd;
        }

        return buf.ToArray();
    }

    /******************************************************************
     *  SPU decode 
     *    — palette remap, control sequence, RLE to 4-color indexed bitmap
     ******************************************************************/

    private static bool TryDecodeSpu(
        byte[] spu, int frameW, int frameH, uint[] palette,
        out byte[]? rgbaOut, out int widthOut, out int heightOut,
        out TimeSpan startDelay, out TimeSpan endDelay)
    {
        rgbaOut    = null;
        widthOut   = 0;
        heightOut  = 0;
        startDelay = TimeSpan.Zero;
        endDelay   = TimeSpan.Zero;
        if (spu.Length < 4) return false;

        // int totalSize = (spu[0] << 8) | spu[1];
        int ctrlOff = (spu[2] << 8) | spu[3];
        if (ctrlOff >= spu.Length) return false;

        // Control sequence — one or more linked "display control sub-units" (DCSQ), each
        // starting with a 16-bit date and 16-bit link to the next DCSQ (or itself = end).
        int[] colourMap = { 0, 1, 2, 3 };  // pal-index remap, 4 entries (4 bits each in SET_COLOR)
        int[] alphaMap  = { 0, 0, 0, 0 };  // 0-15 per entry (15 = opaque)
        int x1 = 0, y1 = 0, x2 = frameW - 1, y2 = frameH - 1;
        int rleTopOff = 4, rleBotOff = 4;
        bool haveTop = false, haveBot = false;

        int cur = ctrlOff;
        while (cur + 4 <= spu.Length)
        {
            int date = (spu[cur] << 8) | spu[cur + 1];
            int next = (spu[cur + 2] << 8) | spu[cur + 3];
            var delay = TimeSpan.FromMilliseconds(date * 1024 / 90.0);  // DVD subtitle delay unit = 1024/90000 sec
            int o = cur + 4;

            bool sawStart = false, sawStop = false;
            while (o < spu.Length)
            {
                byte cmd = spu[o++];
                if (cmd == 0xFF) break;
                switch (cmd)
                {
                    case 0x00: /* FSTA_DSP */
                    case 0x01: sawStart = true; break;        // STA_DSP
                    case 0x02: sawStop  = true; break;        // STP_DSP
                    case 0x03:                                 // SET_COLOR (4-bit indices into .idx palette)
                        if (o + 2 > spu.Length) goto end_ctrl;
                        colourMap[3] = (spu[o]     >> 4) & 0x0F;
                        colourMap[2] =  spu[o]           & 0x0F;
                        colourMap[1] = (spu[o + 1] >> 4) & 0x0F;
                        colourMap[0] =  spu[o + 1]       & 0x0F;
                        o += 2;
                        break;
                    case 0x04:                                 // SET_CONTR (4-bit alpha per index, 0=transparent 15=opaque)
                        if (o + 2 > spu.Length) goto end_ctrl;
                        alphaMap[3] = (spu[o]     >> 4) & 0x0F;
                        alphaMap[2] =  spu[o]           & 0x0F;
                        alphaMap[1] = (spu[o + 1] >> 4) & 0x0F;
                        alphaMap[0] =  spu[o + 1]       & 0x0F;
                        o += 2;
                        break;
                    case 0x05:                                 // SET_DAREA (3 bytes x1/x2, 3 bytes y1/y2)
                        if (o + 6 > spu.Length) goto end_ctrl;
                        x1 = (spu[o] << 4)       | (spu[o + 1] >> 4);
                        x2 = ((spu[o + 1] & 0x0F) << 8) | spu[o + 2];
                        y1 = (spu[o + 3] << 4)   | (spu[o + 4] >> 4);
                        y2 = ((spu[o + 4] & 0x0F) << 8) | spu[o + 5];
                        o += 6;
                        break;
                    case 0x06:                                 // SET_DSPXA (top/bottom field RLE offsets)
                        if (o + 4 > spu.Length) goto end_ctrl;
                        rleTopOff = (spu[o]     << 8) | spu[o + 1];
                        rleBotOff = (spu[o + 2] << 8) | spu[o + 3];
                        haveTop = haveBot = true;
                        o += 4;
                        break;
                    case 0x07:                                 // CHG_COLCON — length-prefixed, skip.
                        if (o + 2 > spu.Length) goto end_ctrl;
                        int chgLen = (spu[o] << 8) | spu[o + 1];
                        o += chgLen; // includes the 2 length bytes
                        break;
                    default: goto end_ctrl;                    // unknown command — bail on this DCSQ
                }
            }
            end_ctrl:
            if (sawStart) startDelay = delay;
            if (sawStop)  endDelay   = delay;

            if (next == cur) break;
            cur = next;
        }

        if (!haveTop || !haveBot) return false;

        int w = x2 - x1 + 1;
        int h = y2 - y1 + 1;
        if (w <= 0 || h <= 0 || w > frameW * 2 || h > frameH * 2) return false;

        // Decode the two interlaced fields into a single progressive indexed bitmap.
        var indexed = DecodeRleInterlaced(spu, rleTopOff, rleBotOff, w, h);

        // Compose to RGBA using the idx palette via the 4-entry remap.
        var rgba = new byte[w * h * 4];
        for (int i = 0; i < indexed.Length; i++)
        {
            byte ix = indexed[i];
            uint rgb  = palette[colourMap[ix & 0x3] & 0x0F];
            int   aNibble = alphaMap[ix & 0x3] & 0x0F;
            byte  a    = (byte)(aNibble * 17); // 0..15 → 0..255
            int   o    = i * 4;
            rgba[o]     = (byte)((rgb >> 16) & 0xFF);
            rgba[o + 1] = (byte)((rgb >> 8)  & 0xFF);
            rgba[o + 2] = (byte)( rgb        & 0xFF);
            rgba[o + 3] = a;
        }

        // Emit raw RGBA; preprocessing happens in NativeOcrService
        rgbaOut   = rgba;
        widthOut  = w;
        heightOut = h;
        return true;
    }

    /******************************************************************
     *  VobSub RLE
     *    - nibble stream, interlaced (top field then bottom field)
     ******************************************************************/

    private static byte[] DecodeRleInterlaced(byte[] spu, int topOff, int botOff, int w, int h)
    {
        var pix = new byte[w * h];
        DecodeRleField(spu, topOff, pix, w, h, startRow: 0, stepRow: 2);
        DecodeRleField(spu, botOff, pix, w, h, startRow: 1, stepRow: 2);
        return pix;
    }

    private static void DecodeRleField(byte[] spu, int offset, byte[] pix, int w, int h, int startRow, int stepRow)
    {
        var nib = new NibbleReader(spu, offset);
        int y = startRow;
        int x = 0;
        while (y < h && !nib.Eof)
        {
            int val = nib.Read();
            if (val >= 0x4) { Emit(pix, w, y, ref x, val >> 2, val & 0x3); }
            else
            {
                val = (val << 4) | nib.Read();
                if (val >= 0x10) Emit(pix, w, y, ref x, val >> 2, val & 0x3);
                else
                {
                    val = (val << 4) | nib.Read();
                    if (val >= 0x40) Emit(pix, w, y, ref x, val >> 2, val & 0x3);
                    else
                    {
                        val = (val << 4) | nib.Read();
                        if (val == 0)
                        {
                            // Fill to end of line with last value's colour ≡ pad with 0.
                            while (x < w) { pix[y * w + x] = 0; x++; }
                        }
                        else
                        {
                            Emit(pix, w, y, ref x, val >> 2, val & 0x3);
                        }
                    }
                }
            }

            if (x >= w)
            {
                x = 0;
                y += stepRow;
                nib.AlignToByte();  // each scan line pads to a byte boundary
            }
        }
    }

    private static void Emit(byte[] pix, int w, int y, ref int x, int count, int colour)
    {
        int end = Math.Min(w, x + count);
        int off = y * w;
        for (int xx = x; xx < end; xx++) pix[off + xx] = (byte)colour;
        x = end;
    }

    private struct NibbleReader
    {
        private readonly byte[] _buf;
        private int             _pos;
        private bool            _high;
        public NibbleReader(byte[] buf, int startByte) { _buf = buf; _pos = startByte; _high = true; }
        public bool Eof => _pos >= _buf.Length;
        public int Read()
        {
            if (Eof) return 0;
            int val;
            if (_high) { val = (_buf[_pos] >> 4) & 0x0F; _high = false; }
            else       { val = _buf[_pos] & 0x0F; _high = true; _pos++; }
            return val;
        }
        public void AlignToByte() { if (!_high) { _high = true; _pos++; } }
    }
}
