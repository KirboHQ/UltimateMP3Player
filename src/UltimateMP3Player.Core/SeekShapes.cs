namespace UltimateMP3Player.Core;

// The song's bar in the shapes of the bars of the music, from the song's own loudness: worked out once per song and size, then only cut
// where the played part ends, so nothing moves with the sound. Only little rectangles (or dots, for Dots): no long path to rasterize.
public static class SeekShapes
{
    public readonly record struct Mark(float X, float Y, float W, float H);

    // A song not read yet: a low, even bar.
    private const double Empty = 0.08;
    // The thin columns of the shapes that look like one line or surface (a maximized 4K window would otherwise need thousands).
    private const int MaxColumns = 800;

    // Shapes grow up from a line, each mark so far above it: the bottom, or mirrored the middle, the same again below it.
    public static Mark[] Build(VisualizerStyle shape, byte[]? peaks, double width, double height, double barWidth, bool mirrored = true)
    {
        var marks = new List<Mark>();
        double room = mirrored ? height / 2 : height, floor = mirrored ? 1 : 2;
        void Add(double x, double up, double w, double h)
        {
            h = Math.Min(h, room - up);
            if (h <= 0) return;
            if (!mirrored) marks.Add(new Mark((float)x, (float)(height - up - h), (float)w, (float)h));
            // (touching the middle: one mark, not two that meet)
            else if (up <= 0) marks.Add(new Mark((float)x, (float)(room - h), (float)w, (float)(h * 2)));
            else
            {
                marks.Add(new Mark((float)x, (float)(room - up - h), (float)w, (float)h));
                marks.Add(new Mark((float)x, (float)(room + up), (float)w, (float)h));
            }
        }
        double gap = Math.Max(1, barWidth * 0.6);

        void Columns(double w, Action<double, double> column)
        {
            double step = w + gap;
            int count = Math.Max(1, (int)((width + gap) / step));
            for (int i = 0; i < count; i++) column(i * step, Level(peaks, i, count));
        }

        switch (shape)
        {
            case VisualizerStyle.Peaks:
                const double capHeight = 2, capSpace = 1.5;
                double body = Math.Max(1, room - capHeight - capSpace);
                Columns(barWidth, (x, v) =>
                {
                    double h = Math.Max(floor * 0.75, v * body);
                    Add(x, 0, barWidth, h);
                    Add(x, h + capSpace, barWidth, capHeight);
                });
                break;
            case VisualizerStyle.Blocks:
                const double cell = 3, cellSpace = 1.5;
                double wide = barWidth * 1.6;
                // (mirrored, the middle's gap is shared: the two first cells don't touch)
                double start = mirrored ? cellSpace / 2 : 0;
                int most = Math.Max(1, (int)((room - start + cellSpace) / (cell + cellSpace)));
                Columns(wide, (x, v) =>
                {
                    int n = Math.Clamp((int)Math.Round(v * most), 1, most);
                    for (int k = 0; k < n; k++) Add(x, start + k * (cell + cellSpace), wide, cell);
                });
                break;
            case VisualizerStyle.Dots:
                double dot = Math.Max(3, barWidth * 1.2);
                Columns(dot, (x, v) => Add(x, Math.Max(0.01, (room - dot) * v), dot, dot));
                break;
            case VisualizerStyle.Line:
            case VisualizerStyle.Wave:
                Thin(shape, peaks, width, room, floor, Add);
                break;
            default:
                Columns(barWidth, (x, v) => Add(x, 0, barWidth, Math.Max(floor, v * room)));
                break;
        }
        return marks.ToArray();
    }

    // One line or a filled wave: columns side by side, a touch wider than their step or the screen's pixels would leave seams between them.
    private static void Thin(VisualizerStyle shape, byte[]? peaks, double width, double room, double floor, Action<double, double, double, double> add)
    {
        const double thickness = 2, overlap = 0.35;
        int count = Math.Clamp((int)width, 1, MaxColumns);
        double column = width / count;
        var levels = Smooth(peaks, count);
        double Up(int i) => Math.Max(0, room - thickness) * levels[Math.Min(i, count - 1)];
        for (int i = 0; i < count; i++)
        {
            double x = i * column, w = Math.Min(column + overlap, width - x);
            if (shape == VisualizerStyle.Wave) add(x, 0, w, Math.Max(floor, levels[i] * room));
            else
            {
                double a = Up(i), b = Up(i + 1);
                add(x, Math.Max(0.01, Math.Min(a, b)), w, Math.Abs(a - b) + thickness);
            }
        }
    }

    // The loudest moment in a column.
    private static double Level(byte[]? peaks, int i, int count)
    {
        if (peaks is not { Length: > 0 }) return Empty;
        int a = i * peaks.Length / count, b = Math.Max(a + 1, (i + 1) * peaks.Length / count), max = 0;
        for (int k = a; k < b && k < peaks.Length; k++) max = Math.Max(max, peaks[k]);
        return max / 255.0;
    }

    // Column by column between the song's values, softened: a line, not a staircase.
    private static double[] Smooth(byte[]? peaks, int count)
    {
        var levels = new double[count];
        if (peaks is not { Length: > 0 })
        {
            Array.Fill(levels, Empty);
            return levels;
        }
        for (int i = 0; i < count; i++)
        {
            double at = count == 1 ? 0 : (double)i / (count - 1) * (peaks.Length - 1);
            int a = (int)at, b = Math.Min(a + 1, peaks.Length - 1);
            levels[i] = (peaks[a] + (peaks[b] - peaks[a]) * (at - a)) / 255;
        }
        var soft = new double[count];
        const int reach = 2;
        for (int i = 0; i < count; i++)
        {
            int from = Math.Max(0, i - reach), to = Math.Min(count - 1, i + reach);
            double sum = 0;
            for (int k = from; k <= to; k++) sum += levels[k];
            soft[i] = sum / (to - from + 1);
        }
        return soft;
    }
}
