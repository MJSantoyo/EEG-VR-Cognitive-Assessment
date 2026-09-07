using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace IkeaEeg.EditorTools
{
    /// <summary>
    /// A very small raster plotter: pixels, lines, rectangles and a 5x7 bitmap font, encoded to
    /// PNG through Unity's own texture encoder.
    ///
    /// WHY THIS IS HERE AND WHY IT IS DELIBERATELY DUMB. The offline analysis needs pictures, and
    /// a Quest-targeted Unity project has no charting library and should not gain one for this.
    /// Everything in this file is PRESENTATION: it draws numbers that were already computed
    /// elsewhere. It performs no filtering, no resampling beyond min/max decimation for display,
    /// no smoothing and no statistics. If a plot looks wrong the fault is in the data or in the
    /// axis range, never in a hidden transform applied here — there are none.
    ///
    /// COORDINATES ARE TOP-LEFT ORIGIN while drawing, because that is how one reasons about a
    /// figure. The buffer is flipped once, at encode time, because Texture2D is bottom-left.
    /// </summary>
    public class OfflinePlot
    {
        readonly int m_Width;
        readonly int m_Height;
        readonly Color32[] m_Pixels;

        public int width => m_Width;
        public int height => m_Height;

        public OfflinePlot(int width, int height, Color32 background)
        {
            m_Width = Math.Max(1, width);
            m_Height = Math.Max(1, height);
            m_Pixels = new Color32[m_Width * m_Height];

            for (var i = 0; i < m_Pixels.Length; i++)
                m_Pixels[i] = background;
        }

        // ---------------------------------------------------------------------------------
        // Primitives
        // ---------------------------------------------------------------------------------

        public void SetPixel(int x, int y, Color32 c)
        {
            if (x < 0 || y < 0 || x >= m_Width || y >= m_Height)
                return;

            m_Pixels[y * m_Width + x] = c;
        }

        /// <summary>Bresenham. No anti-aliasing: a crisp one-pixel trace reads better at these sizes.</summary>
        public void Line(int x0, int y0, int x1, int y1, Color32 c)
        {
            var dx = Math.Abs(x1 - x0);
            var dy = -Math.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx + dy;

            // Bounded so a coordinate derived from a bad value cannot spin here forever.
            var guard = dx + (-dy) + 4;

            while (guard-- > 0)
            {
                SetPixel(x0, y0, c);

                if (x0 == x1 && y0 == y1)
                    return;

                var e2 = 2 * err;

                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        public void HLine(int x0, int x1, int y, Color32 c)
        {
            if (x1 < x0)
                (x0, x1) = (x1, x0);

            for (var x = x0; x <= x1; x++)
                SetPixel(x, y, c);
        }

        public void VLine(int x, int y0, int y1, Color32 c)
        {
            if (y1 < y0)
                (y0, y1) = (y1, y0);

            for (var y = y0; y <= y1; y++)
                SetPixel(x, y, c);
        }

        /// <summary>Dashed vertical rule, for event onsets that must not be mistaken for data.</summary>
        public void VLineDashed(int x, int y0, int y1, Color32 c, int on = 4, int off = 4)
        {
            if (y1 < y0)
                (y0, y1) = (y1, y0);

            var period = Math.Max(1, on + off);

            for (var y = y0; y <= y1; y++)
            {
                if ((y - y0) % period < on)
                    SetPixel(x, y, c);
            }
        }

        public void FillRect(int x, int y, int w, int h, Color32 c)
        {
            for (var yy = y; yy < y + h; yy++)
            for (var xx = x; xx < x + w; xx++)
                SetPixel(xx, yy, c);
        }

        /// <summary>Alpha-blends a block over what is already there. Used for phase shading.</summary>
        public void BlendRect(int x, int y, int w, int h, Color32 c, double alpha)
        {
            var a = Math.Max(0d, Math.Min(1d, alpha));

            for (var yy = y; yy < y + h; yy++)
            {
                if (yy < 0 || yy >= m_Height)
                    continue;

                for (var xx = x; xx < x + w; xx++)
                {
                    if (xx < 0 || xx >= m_Width)
                        continue;

                    var i = yy * m_Width + xx;
                    var d = m_Pixels[i];

                    m_Pixels[i] = new Color32(
                        (byte)(d.r + (c.r - d.r) * a),
                        (byte)(d.g + (c.g - d.g) * a),
                        (byte)(d.b + (c.b - d.b) * a),
                        255);
                }
            }
        }

        public void StrokeRect(int x, int y, int w, int h, Color32 c)
        {
            HLine(x, x + w - 1, y, c);
            HLine(x, x + w - 1, y + h - 1, c);
            VLine(x, y, y + h - 1, c);
            VLine(x + w - 1, y, y + h - 1, c);
        }

        // ---------------------------------------------------------------------------------
        // Text
        // ---------------------------------------------------------------------------------

        public const int GlyphWidth = 5;
        public const int GlyphHeight = 7;

        /// <summary>Width in pixels a string will occupy at the given scale.</summary>
        public static int TextWidth(string text, int scale)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            return (text.Length * (GlyphWidth + 1) - 1) * Math.Max(1, scale);
        }

        public static int TextHeight(int scale)
        {
            return GlyphHeight * Math.Max(1, scale);
        }

        /// <summary>
        /// Draws upper-cased text. Lower case is folded rather than given its own glyphs: labels
        /// here are electrode names, band names and numbers, and a second font would be weight
        /// for nothing.
        /// </summary>
        public void Text(int x, int y, string text, Color32 c, int scale = 1)
        {
            if (string.IsNullOrEmpty(text))
                return;

            scale = Math.Max(1, scale);
            var cursor = x;

            foreach (var raw in text)
            {
                var glyph = Glyph(char.ToUpperInvariant(raw));

                if (glyph != null)
                {
                    for (var row = 0; row < GlyphHeight; row++)
                    {
                        var line = glyph[row];

                        for (var col = 0; col < GlyphWidth; col++)
                        {
                            if (line[col] != '#')
                                continue;

                            for (var sy = 0; sy < scale; sy++)
                            for (var sx = 0; sx < scale; sx++)
                                SetPixel(cursor + col * scale + sx, y + row * scale + sy, c);
                        }
                    }
                }

                cursor += (GlyphWidth + 1) * scale;
            }
        }

        public void TextRight(int rightX, int y, string text, Color32 c, int scale = 1)
        {
            Text(rightX - TextWidth(text, scale), y, text, c, scale);
        }

        public void TextCentred(int centreX, int y, string text, Color32 c, int scale = 1)
        {
            Text(centreX - TextWidth(text, scale) / 2, y, text, c, scale);
        }

        /// <summary>Text rotated 90 degrees counter-clockwise, for y-axis titles.</summary>
        public void TextVertical(int x, int bottomY, string text, Color32 c, int scale = 1)
        {
            if (string.IsNullOrEmpty(text))
                return;

            scale = Math.Max(1, scale);
            var cursor = bottomY;

            foreach (var raw in text)
            {
                var glyph = Glyph(char.ToUpperInvariant(raw));

                if (glyph != null)
                {
                    for (var row = 0; row < GlyphHeight; row++)
                    {
                        var line = glyph[row];

                        for (var col = 0; col < GlyphWidth; col++)
                        {
                            if (line[col] != '#')
                                continue;

                            // (col, row) maps to (row, -col) about the glyph origin.
                            for (var sy = 0; sy < scale; sy++)
                            for (var sx = 0; sx < scale; sx++)
                                SetPixel(x + row * scale + sx, cursor - col * scale - sy, c);
                        }
                    }
                }

                cursor -= (GlyphWidth + 1) * scale;
            }
        }

        // ---------------------------------------------------------------------------------
        // Encoding
        // ---------------------------------------------------------------------------------

        /// <summary>Writes the figure as a PNG. Returns the path, or empty on failure.</summary>
        public string Save(string path)
        {
            Texture2D texture = null;

            try
            {
                texture = new Texture2D(m_Width, m_Height, TextureFormat.RGBA32, false);

                // Texture2D rows run bottom-up; the drawing buffer runs top-down.
                var flipped = new Color32[m_Pixels.Length];

                for (var y = 0; y < m_Height; y++)
                {
                    Array.Copy(m_Pixels, y * m_Width,
                        flipped, (m_Height - 1 - y) * m_Width, m_Width);
                }

                texture.SetPixels32(flipped);
                texture.Apply(false, false);

                var bytes = texture.EncodeToPNG();

                if (bytes == null || bytes.Length == 0)
                    return string.Empty;

                var folder = Path.GetDirectoryName(path);

                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);

                File.WriteAllBytes(path, bytes);

                return path;
            }
            catch (Exception e)
            {
                Debug.LogError("[IKEA_EEG] Could not write plot " + path + ": " + e.Message);
                return string.Empty;
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        // ---------------------------------------------------------------------------------
        // Font: 5x7, one string per row, '#' set.
        // ---------------------------------------------------------------------------------

        static Dictionary<char, string[]> s_Font;

        static string[] Glyph(char c)
        {
            if (s_Font == null)
                s_Font = BuildFont();

            string[] g;
            return s_Font.TryGetValue(c, out g) ? g : null;
        }

        static Dictionary<char, string[]> BuildFont()
        {
            var f = new Dictionary<char, string[]>();

            f[' '] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "....." };
            f['0'] = new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." };
            f['1'] = new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." };
            f['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" };
            f['3'] = new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." };
            f['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." };
            f['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." };
            f['6'] = new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." };
            f['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." };
            f['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." };
            f['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." };
            f['A'] = new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
            f['B'] = new[] { "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####." };
            f['C'] = new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." };
            f['D'] = new[] { "###..", "#..#.", "#...#", "#...#", "#...#", "#..#.", "###.." };
            f['E'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" };
            f['F'] = new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#...." };
            f['G'] = new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####" };
            f['H'] = new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" };
            f['I'] = new[] { ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###." };
            f['J'] = new[] { "....#", "....#", "....#", "....#", "#...#", "#...#", ".###." };
            f['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" };
            f['L'] = new[] { "#....", "#....", "#....", "#....", "#....", "#....", "#####" };
            f['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#" };
            f['N'] = new[] { "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#" };
            f['O'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." };
            f['P'] = new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." };
            f['Q'] = new[] { ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#" };
            f['R'] = new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" };
            f['S'] = new[] { ".####", "#....", "#....", ".###.", "....#", "....#", "####." };
            f['T'] = new[] { "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." };
            f['U'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###." };
            f['V'] = new[] { "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.." };
            f['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#" };
            f['X'] = new[] { "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#" };
            f['Y'] = new[] { "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.." };
            f['Z'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####" };
            f['.'] = new[] { ".....", ".....", ".....", ".....", ".....", ".##..", ".##.." };
            f[','] = new[] { ".....", ".....", ".....", ".....", ".##..", ".##..", ".#..." };
            f['-'] = new[] { ".....", ".....", ".....", "#####", ".....", ".....", "....." };
            f['+'] = new[] { ".....", "..#..", "..#..", "#####", "..#..", "..#..", "....." };
            f[':'] = new[] { ".....", ".##..", ".##..", ".....", ".##..", ".##..", "....." };
            f[';'] = new[] { ".....", ".##..", ".##..", ".....", ".##..", ".##..", ".#..." };
            f['/'] = new[] { "....#", "....#", "...#.", "..#..", ".#...", "#....", "#...." };
            f['('] = new[] { "..##.", ".#...", "#....", "#....", "#....", ".#...", "..##." };
            f[')'] = new[] { ".##..", "...#.", "....#", "....#", "....#", "...#.", ".##.." };
            f['['] = new[] { ".###.", ".#...", ".#...", ".#...", ".#...", ".#...", ".###." };
            f[']'] = new[] { ".###.", "...#.", "...#.", "...#.", "...#.", "...#.", ".###." };
            f['%'] = new[] { "##..#", "##.#.", "...#.", "..#..", ".#...", "#.##.", "#..##" };
            f['_'] = new[] { ".....", ".....", ".....", ".....", ".....", ".....", "#####" };
            f['='] = new[] { ".....", ".....", "#####", ".....", "#####", ".....", "....." };
            f['|'] = new[] { "..#..", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.." };
            f['<'] = new[] { "...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#." };
            f['>'] = new[] { ".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#..." };
            f['#'] = new[] { ".#.#.", "#####", ".#.#.", ".#.#.", "#####", ".#.#.", "....." };
            f['*'] = new[] { ".....", "#.#.#", ".###.", "#####", ".###.", "#.#.#", "....." };
            f['?'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#.." };
            f['!'] = new[] { "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#.." };

            return f;
        }

        // ---------------------------------------------------------------------------------
        // Shared palette
        // ---------------------------------------------------------------------------------

        public static readonly Color32 Background = new Color32(255, 255, 255, 255);
        public static readonly Color32 Ink = new Color32(24, 24, 28, 255);
        public static readonly Color32 Muted = new Color32(128, 132, 140, 255);
        public static readonly Color32 Grid = new Color32(226, 228, 232, 255);
        public static readonly Color32 Warn = new Color32(196, 64, 48, 255);

        /// <summary>
        /// Eight visually separable colours, one per electrode, in montage order.
        /// </summary>
        public static readonly Color32[] ChannelColours =
        {
            new Color32(31, 119, 180, 255),   // ch1
            new Color32(214, 39, 40, 255),    // ch2
            new Color32(44, 160, 44, 255),    // ch3
            new Color32(255, 127, 14, 255),   // ch4
            new Color32(148, 103, 189, 255),  // ch5
            new Color32(23, 190, 207, 255),   // ch6
            new Color32(140, 86, 75, 255),    // ch7
            new Color32(227, 119, 194, 255),  // ch8
        };

        public static Color32 ChannelColour(int index)
        {
            var n = ChannelColours.Length;
            return ChannelColours[((index % n) + n) % n];
        }
    }

    /// <summary>
    /// A rectangular plot area with linear axes, drawn onto an <see cref="OfflinePlot"/>.
    ///
    /// Holds the data-to-pixel mapping and nothing else. Ranges are supplied by the caller and
    /// never inferred silently — an axis that auto-scaled itself would hide exactly the outliers
    /// this analysis exists to find.
    /// </summary>
    public class PlotAxes
    {
        readonly OfflinePlot m_Plot;

        public readonly int left;
        public readonly int top;
        public readonly int plotWidth;
        public readonly int plotHeight;

        public double xMin { get; private set; }
        public double xMax { get; private set; }
        public double yMin { get; private set; }
        public double yMax { get; private set; }

        public int right => left + plotWidth;
        public int bottom => top + plotHeight;

        public PlotAxes(OfflinePlot plot, int left, int top, int plotWidth, int plotHeight,
            double xMin, double xMax, double yMin, double yMax)
        {
            m_Plot = plot;
            this.left = left;
            this.top = top;
            this.plotWidth = Math.Max(1, plotWidth);
            this.plotHeight = Math.Max(1, plotHeight);

            // Degenerate ranges are widened rather than allowed to divide by zero: a flat
            // channel must still draw as a flat line, not vanish.
            if (xMax <= xMin)
                xMax = xMin + 1d;

            if (yMax <= yMin)
            {
                var pad = Math.Abs(yMin) > 0d ? Math.Abs(yMin) * 0.05 : 1d;
                yMin -= pad;
                yMax += pad;
            }

            this.xMin = xMin;
            this.xMax = xMax;
            this.yMin = yMin;
            this.yMax = yMax;
        }

        public int X(double value)
        {
            var t = (value - xMin) / (xMax - xMin);
            return left + (int)Math.Round(t * (plotWidth - 1));
        }

        public int Y(double value)
        {
            var t = (value - yMin) / (yMax - yMin);

            // Larger values sit higher on the page.
            return bottom - 1 - (int)Math.Round(t * (plotHeight - 1));
        }

        public void Frame(Color32 colour)
        {
            m_Plot.StrokeRect(left, top, plotWidth, plotHeight, colour);
        }

        /// <summary>Horizontal grid lines plus left-hand tick labels.</summary>
        public void YTicks(int count, Func<double, string> format, Color32 grid, Color32 ink,
            int scale = 1)
        {
            for (var i = 0; i <= count; i++)
            {
                var v = yMin + (yMax - yMin) * i / count;
                var y = Y(v);

                m_Plot.HLine(left, right - 1, y, grid);
                m_Plot.TextRight(left - 4, y - OfflinePlot.TextHeight(scale) / 2,
                    format(v), ink, scale);
            }
        }

        /// <summary>Vertical grid lines plus bottom tick labels.</summary>
        public void XTicks(int count, Func<double, string> format, Color32 grid, Color32 ink,
            int scale = 1)
        {
            for (var i = 0; i <= count; i++)
            {
                var v = xMin + (xMax - xMin) * i / count;
                var x = X(v);

                m_Plot.VLine(x, top, bottom - 1, grid);
                m_Plot.TextCentred(x, bottom + 6, format(v), ink, scale);
            }
        }

        /// <summary>
        /// Draws a series, decimating to at most one min/max pair per pixel column.
        ///
        /// Min/max rather than sub-sampling: dropping samples would silently erase the spikes an
        /// artefact check is looking for, while a min/max envelope keeps every excursion visible
        /// at any zoom level.
        ///
        /// A MISSING VALUE AND A SPARSE ONE ARE NOT THE SAME THING, and conflating them is the
        /// obvious way to get this wrong. An empty pixel column can mean either "the data has a
        /// hole here" or "there are fewer data points than pixels", and those must draw
        /// differently: the first is a gap, the second is a line. So a break is recorded only
        /// where a NON-FINITE value actually landed. Columns that are merely empty are bridged,
        /// which is what turns a 257-bin spectrum on a 1100-pixel axis into a curve rather than a
        /// dotted trail.
        /// </summary>
        public void Series(IReadOnlyList<double> x, IReadOnlyList<double> y, Color32 colour)
        {
            if (x == null || y == null || x.Count == 0)
                return;

            var n = Math.Min(x.Count, y.Count);

            var columnMin = new double[plotWidth];
            var columnMax = new double[plotWidth];
            var columnHas = new bool[plotWidth];
            var columnBreak = new bool[plotWidth];

            for (var i = 0; i < n; i++)
            {
                if (double.IsNaN(x[i]) || double.IsInfinity(x[i]))
                    continue;

                var px = X(x[i]) - left;

                if (px < 0 || px >= plotWidth)
                    continue;

                var v = y[i];

                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    // The series genuinely has no value here. Whatever else lands in this
                    // column, the line must not be carried across it.
                    columnBreak[px] = true;
                    continue;
                }

                if (!columnHas[px])
                {
                    columnHas[px] = true;
                    columnMin[px] = v;
                    columnMax[px] = v;
                }
                else
                {
                    if (v < columnMin[px]) columnMin[px] = v;
                    if (v > columnMax[px]) columnMax[px] = v;
                }
            }

            var previousX = -1;
            var previousY = 0;
            var brokenSincePrevious = false;

            for (var px = 0; px < plotWidth; px++)
            {
                if (!columnHas[px])
                {
                    if (columnBreak[px])
                    {
                        // A hole in the data: stop the line here and restart after it.
                        previousX = -1;
                        brokenSincePrevious = false;
                    }

                    continue;
                }

                if (columnBreak[px])
                    brokenSincePrevious = true;

                var yTop = Clamp(Y(columnMax[px]));      // larger value, smaller pixel y
                var yBottom = Clamp(Y(columnMin[px]));

                m_Plot.VLine(left + px, yTop, yBottom, colour);

                if (previousX >= 0 && !brokenSincePrevious)
                    m_Plot.Line(left + previousX, previousY, left + px, yTop, colour);

                previousX = px;
                previousY = yBottom;
                brokenSincePrevious = false;
            }
        }

        /// <summary>Draws a series as discrete filled squares, for sparse per-item values.</summary>
        public void Points(IReadOnlyList<double> x, IReadOnlyList<double> y, Color32 colour,
            int size = 3)
        {
            if (x == null || y == null)
                return;

            var n = Math.Min(x.Count, y.Count);
            var half = Math.Max(1, size) / 2;

            for (var i = 0; i < n; i++)
            {
                if (double.IsNaN(y[i]) || double.IsInfinity(y[i]))
                    continue;

                if (x[i] < xMin || x[i] > xMax || y[i] < yMin || y[i] > yMax)
                    continue;

                m_Plot.FillRect(X(x[i]) - half, Y(y[i]) - half,
                    Math.Max(1, size), Math.Max(1, size), colour);
            }
        }

        /// <summary>A horizontal reference line at a data value.</summary>
        public void HRule(double value, Color32 colour)
        {
            if (value < yMin || value > yMax)
                return;

            m_Plot.HLine(left, right - 1, Y(value), colour);
        }

        /// <summary>A vertical marker at a data x, dashed so it cannot be read as signal.</summary>
        public void VMarker(double value, Color32 colour)
        {
            if (value < xMin || value > xMax)
                return;

            m_Plot.VLineDashed(X(value), top, bottom - 1, colour);
        }

        /// <summary>Shades an x interval, for phase annotation.</summary>
        public void Band(double from, double to, Color32 colour, double alpha)
        {
            if (to < from)
                (from, to) = (to, from);

            if (to < xMin || from > xMax)
                return;

            var x0 = X(Math.Max(from, xMin));
            var x1 = X(Math.Min(to, xMax));

            m_Plot.BlendRect(x0, top, Math.Max(1, x1 - x0), plotHeight, colour, alpha);
        }

        /// <summary>A filled bar from the axis baseline to a value.</summary>
        public void Bar(double centreX, double halfWidth, double value, double baseline,
            Color32 colour)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            var x0 = X(centreX - halfWidth);
            var x1 = X(centreX + halfWidth);
            var yValue = Clamp(Y(value));
            var yBase = Clamp(Y(baseline));

            var top = Math.Min(yValue, yBase);
            var height = Math.Max(1, Math.Abs(yValue - yBase));

            m_Plot.FillRect(x0, top, Math.Max(1, x1 - x0), height, colour);
        }

        int Clamp(int y)
        {
            if (y < top) return top;
            if (y > bottom - 1) return bottom - 1;
            return y;
        }
    }

    /// <summary>Formatting helpers shared by the plots and the CSV writers.</summary>
    public static class PlotFormat
    {
        public static string F(double v, int decimals = 2)
        {
            if (double.IsNaN(v))
                return "NAN";

            if (double.IsInfinity(v))
                return v > 0 ? "INF" : "-INF";

            return v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        }

        /// <summary>Round-tripping form, for numbers that go into a CSV rather than onto a plot.</summary>
        public static string R(double v)
        {
            if (double.IsNaN(v))
                return "NaN";

            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Compact scientific notation, for band powers that span many decades.</summary>
        public static string Sci(double v, int decimals = 2)
        {
            if (double.IsNaN(v))
                return "NAN";

            if (double.IsInfinity(v))
                return v > 0 ? "INF" : "-INF";

            if (v == 0d)
                return "0";

            return v.ToString("0." + new string('#', Math.Max(1, decimals)) + "E+0",
                CultureInfo.InvariantCulture);
        }

        /// <summary>Escapes a value for a CSV cell.</summary>
        public static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s))
                return string.Empty;

            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
                return s;

            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
