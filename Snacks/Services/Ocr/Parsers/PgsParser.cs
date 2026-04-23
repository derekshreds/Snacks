using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Snacks.Services.Ocr.Parsers;

/// <summary>
///     Decoder for HDMV Presentation Graphic Stream (PGS) subtitles — the bitmap subtitle
///     format used on Blu-ray discs (<c>hdmv_pgs_subtitle</c>). Reads a <c>.sup</c> file
///     (the raw PGS bitstream FFmpeg emits with <c>-c:s copy</c>) and yields one
///     <see cref="BitmapEvent"/> per display set.
/// </summary>
/// <remarks>
///     Written from the PGS spec in the US Patent Office filing and the community-maintained
///     reverse-engineered documentation at <c>blog.thescorpius.com/index.php/2017/07/15/</c>.
///
///     Segment types:
///     <list type="bullet">
///         <item><c>0x14</c> PDS — palette definition</item>
///         <item><c>0x15</c> ODS — object (bitmap) definition</item>
///         <item><c>0x16</c> PCS — presentation composition (start/end cue marker)</item>
///         <item><c>0x17</c> WDS — window definition</item>
///         <item><c>0x80</c> END — end of display set</item>
///     </list>
/// </remarks>
internal static class PgsParser
{
    private const byte SEG_PDS = 0x14;
    private const byte SEG_ODS = 0x15;
    private const byte SEG_PCS = 0x16;
    private const byte SEG_WDS = 0x17;
    private const byte SEG_END = 0x80;

    public static async IAsyncEnumerable<BitmapEvent> ParseAsync(string supPath, [EnumeratorCancellation] CancellationToken ct)
    {
        // PGS files are modest (typically <30 MB for a full feature) — read up front to keep
        // the parse loop synchronous and simple. Stream if this ever becomes a memory concern.
        var data = await File.ReadAllBytesAsync(supPath, ct);
        int pos = 0;

        // State carried between display sets.
        var palettes = new Dictionary<byte, Rgba[]>();
        var objects  = new Dictionary<ushort, PgsObject>();

        // Current in-progress cue.
        TimeSpan?       cueStart = null;
        byte            cuePaletteId = 0;
        CompositionObject[] cueObjects = Array.Empty<CompositionObject>();

        while (pos + 13 <= data.Length)
        {
            ct.ThrowIfCancellationRequested();

            // 13-byte segment header: "PG" magic, PTS, DTS, type, size.
            if (data[pos] != 0x50 || data[pos + 1] != 0x47) break; // lost sync — bail
            uint pts90 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 2, 4));
            // uint dts90 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos + 6, 4));  // unused
            byte type  = data[pos + 10];
            ushort size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 11, 2));
            pos += 13;
            if (pos + size > data.Length) break;

            var seg = data.AsSpan(pos, size);
            pos += size;

            switch (type)
            {
                case SEG_PCS:
                {
                    // ushort width      = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(0, 2));
                    // ushort height     = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(2, 2));
                    // byte   frameRate  = seg[4];
                    // ushort compNumber = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(5, 2));
                    byte compState = seg[7];
                    // byte   palUpdate  = seg[8];
                    byte palId     = seg[9];
                    byte nComp     = seg[10];

                    if (compState == 0x80 /* epoch start */ || compState == 0x40 /* acquisition */)
                    {
                        // Start of a new state — the catalog of previously decoded objects is
                        // only guaranteed valid within an epoch, so clear it.
                        objects.Clear();
                        palettes.Clear();
                    }

                    var comps = new CompositionObject[nComp];
                    int o = 11;
                    for (int i = 0; i < nComp; i++)
                    {
                        ushort objId = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(o, 2));
                        o += 2;
                        // byte winId = seg[o];
                        o += 1;
                        byte flags = seg[o];
                        o += 1;
                        ushort x = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(o, 2)); o += 2;
                        ushort y = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(o, 2)); o += 2;
                        if ((flags & 0x40) != 0)
                        {
                            // Crop rect: skip cx, cy, cw, ch.
                            o += 8;
                        }
                        comps[i] = new CompositionObject(objId, x, y);
                    }

                    if (nComp == 0)
                    {
                        // "Hide everything" — marks the end of the currently displayed cue.
                        if (cueStart is not null && cueObjects.Length > 0 && palettes.ContainsKey(cuePaletteId))
                        {
                            var end = Pts(pts90);
                            var ev = Render(cueObjects, objects, palettes[cuePaletteId], cueStart.Value, end);
                            if (ev is not null)
                                yield return ev;
                        }
                        cueStart   = null;
                        cueObjects = Array.Empty<CompositionObject>();
                    }
                    else
                    {
                        // New cue beginning — remember the start time and composition list.
                        cueStart     = Pts(pts90);
                        cuePaletteId = palId;
                        cueObjects   = comps;
                    }
                    break;
                }

                case SEG_PDS:
                {
                    byte palId = seg[0];
                    // byte palVer = seg[1];
                    // Start from a fully-transparent palette so unreferenced entries stay invisible.
                    if (!palettes.TryGetValue(palId, out var pal))
                    {
                        pal = new Rgba[256];
                        palettes[palId] = pal;
                    }
                    for (int o = 2; o + 5 <= seg.Length; o += 5)
                    {
                        byte idx = seg[o];
                        byte Y = seg[o + 1], Cr = seg[o + 2], Cb = seg[o + 3], A = seg[o + 4];
                        pal[idx] = YCbCrToRgba(Y, Cb, Cr, A);
                    }
                    break;
                }

                case SEG_ODS:
                {
                    if (seg.Length < 7) break;
                    ushort objId = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(0, 2));
                    // byte version = seg[2];
                    byte seqFlag = seg[3];

                    if ((seqFlag & 0x80) != 0)
                    {
                        // First (possibly only) fragment: header carries the total data length + dimensions.
                        if (seg.Length < 11) break;
                        // 24-bit data length follows at offset 4 (includes width+height = 4 bytes).
                        // int dataLen = (seg[4] << 16) | (seg[5] << 8) | seg[6];
                        ushort w = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(7, 2));
                        ushort h = BinaryPrimitives.ReadUInt16BigEndian(seg.Slice(9, 2));
                        var obj = new PgsObject(w, h);
                        obj.AppendRle(seg.Slice(11).ToArray());
                        if ((seqFlag & 0x40) != 0) obj.MarkLast();
                        objects[objId] = obj;
                    }
                    else
                    {
                        if (!objects.TryGetValue(objId, out var obj)) break;
                        obj.AppendRle(seg.Slice(4).ToArray());
                        if ((seqFlag & 0x40) != 0) obj.MarkLast();
                    }
                    break;
                }

                case SEG_WDS:
                case SEG_END:
                default:
                    // WDS carries window rects — useful for strict rendering but not needed
                    // for OCR, since Tesseract doesn't care about absolute screen placement.
                    // END marks the end of a display set; we use PCS timing directly.
                    break;
            }
        }
    }

    private static TimeSpan Pts(uint pts90) => TimeSpan.FromMilliseconds(pts90 / 90.0);

    /// <summary>
    ///     Composes all objects referenced by the current cue into a single PNG, cropped to
    ///     the tight bounding box. Returns <c>null</c> if nothing renders (no decoded objects,
    ///     all transparent, etc.).
    /// </summary>
    private static BitmapEvent? Render(CompositionObject[] comps, Dictionary<ushort, PgsObject> objects, Rgba[] palette, TimeSpan start, TimeSpan end)
    {
        // First pass: determine the bounding box of all placed objects.
        int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0;
        var placed = new List<(PgsObject obj, int x, int y)>(comps.Length);
        foreach (var c in comps)
        {
            if (!objects.TryGetValue(c.ObjectId, out var obj)) continue;
            if (!obj.IsComplete) continue;
            placed.Add((obj, c.X, c.Y));
            if (c.X < minX) minX = c.X;
            if (c.Y < minY) minY = c.Y;
            if (c.X + obj.Width  > maxX) maxX = c.X + obj.Width;
            if (c.Y + obj.Height > maxY) maxY = c.Y + obj.Height;
        }
        if (placed.Count == 0) return null;

        int w = maxX - minX;
        int h = maxY - minY;
        if (w <= 0 || h <= 0) return null;

        // Allocate an RGBA canvas, composite each object on top of a transparent background.
        var canvas = new byte[w * h * 4];
        foreach (var (obj, px, py) in placed)
        {
            var pix = obj.DecodeIndexed();
            for (int row = 0; row < obj.Height; row++)
            {
                int srcOff = row * obj.Width;
                int dstRow = (py - minY + row);
                int dstOff = (dstRow * w + (px - minX)) * 4;
                for (int col = 0; col < obj.Width; col++)
                {
                    byte idx = pix[srcOff + col];
                    var   rg = palette[idx];
                    if (rg.A == 0) continue;  // transparent — leave canvas pixel alone
                    int o = dstOff + col * 4;
                    canvas[o + 0] = rg.R;
                    canvas[o + 1] = rg.G;
                    canvas[o + 2] = rg.B;
                    canvas[o + 3] = rg.A;
                }
            }
        }

        // Emit raw RGBA — the OCR service runs preprocessing
        return new BitmapEvent(start, end, canvas, w, h);
    }

    private readonly record struct CompositionObject(ushort ObjectId, int X, int Y);
    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private static Rgba YCbCrToRgba(byte Y, byte Cb, byte Cr, byte A)
    {
        // BT.709 limited-range inverse — PGS is an HD format, so 709 is the right matrix.
        // Coefficients matter less for OCR (which binarizes anyway) than alpha correctness.
        double y = Y;
        double cb = Cb - 128.0;
        double cr = Cr - 128.0;
        double r = y + 1.5748 * cr;
        double g = y - 0.1873 * cb - 0.4681 * cr;
        double b = y + 1.8556 * cb;
        return new Rgba(Clamp(r), Clamp(g), Clamp(b), A);
    }

    private static byte Clamp(double v) =>
        v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    /// <summary>
    ///     Accumulates RLE fragments for a single PGS object and decodes them lazily into an
    ///     8-bit palette-indexed bitmap on demand.
    /// </summary>
    private sealed class PgsObject
    {
        private readonly List<byte[]> _fragments = new();
        public int  Width  { get; }
        public int  Height { get; }
        public bool IsComplete { get; private set; }

        public PgsObject(int w, int h) { Width = w; Height = h; }
        public void AppendRle(byte[] bytes) => _fragments.Add(bytes);
        public void MarkLast() => IsComplete = true;

        public byte[] DecodeIndexed()
        {
            // Flatten fragments into a single span for the decoder.
            int total = 0;
            foreach (var f in _fragments) total += f.Length;
            var buf = new byte[total];
            int p = 0;
            foreach (var f in _fragments) { Buffer.BlockCopy(f, 0, buf, p, f.Length); p += f.Length; }

            var outPix = new byte[Width * Height];
            int x = 0, y = 0;
            int i = 0;
            while (i < buf.Length && y < Height)
            {
                byte b1 = buf[i++];
                int runLen;
                byte colour;
                if (b1 != 0)
                {
                    runLen = 1;
                    colour = b1;
                }
                else
                {
                    if (i >= buf.Length) break;
                    byte b2 = buf[i++];
                    if (b2 == 0)
                    {
                        // End of line — pad remainder with transparent (index 0).
                        x = 0; y++;
                        continue;
                    }
                    switch (b2 & 0xC0)
                    {
                        case 0x00:
                            runLen = b2 & 0x3F;
                            colour = 0;
                            break;
                        case 0x40:
                            if (i >= buf.Length) goto done;
                            runLen = ((b2 & 0x3F) << 8) | buf[i++];
                            colour = 0;
                            break;
                        case 0x80:
                            if (i >= buf.Length) goto done;
                            runLen = b2 & 0x3F;
                            colour = buf[i++];
                            break;
                        default: // 0xC0
                            if (i + 1 >= buf.Length) goto done;
                            runLen = ((b2 & 0x3F) << 8) | buf[i++];
                            colour = buf[i++];
                            break;
                    }
                }

                int lineOff = y * Width;
                int endX    = Math.Min(Width, x + runLen);
                for (int xx = x; xx < endX; xx++) outPix[lineOff + xx] = colour;
                x = endX;
                // PGS RLE always wraps on explicit end-of-line markers, so if a run advances
                // past the right edge, drop the overflow rather than wrapping to next row.
            }
            done:
            return outPix;
        }
    }
}
