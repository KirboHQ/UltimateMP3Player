using System.Numerics;

namespace UltimateMP3Player.Core;

public enum EqBandKind { LowShelf, Peaking, HighShelf }

public static class Equalizer
{
    public static readonly double[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public const int BandCount = 10;
    public const double MaxGain = 12;
    // One-octave bands, like a hardware graphic EQ.
    public const double Q = 1.41;
    public const double ShelfSlope = 1;

    public static string BandLabel(int i) => Frequencies[i] >= 1000 ? $"{Frequencies[i] / 1000:0.#}k" : $"{Frequencies[i]:0}";

    // Outer bands are shelves, like Spotify.
    public static EqBandKind Kind(int i) => i == 0 ? EqBandKind.LowShelf : i == BandCount - 1 ? EqBandKind.HighShelf : EqBandKind.Peaking;

    public static double FilterFrequency(int i) => Kind(i) switch
    {
        EqBandKind.LowShelf => Frequencies[i] * 1.4,
        EqBandKind.HighShelf => Frequencies[i] / 1.4,
        _ => Frequencies[i],
    };

    // Headroom so boosts rarely hit the limiter.
    public static double PreampDb(IReadOnlyList<double> gains) => -Math.Max(0, gains.Max()) * 0.35;

    // The line drawn over the sliders: through every band's knob, smooth (monotone cubic: never past two neighbouring knobs) and
    // flat past the first and the last band. band: 0 = the first band's place, BandCount - 1 the last's. (The filters' real
    // response overlaps and sums: drawn, it missed the knobs and looked crooked.)
    public static double CurveDb(double band, IReadOnlyList<double> gains)
    {
        int n = Math.Min(BandCount, gains.Count);
        if (n == 0) return 0;
        if (band <= 0 || n == 1) return gains[0];
        if (band >= n - 1) return gains[n - 1];
        int k = (int)band;
        double t = band - k;
        double Tangent(int i)
        {
            if (i <= 0 || i >= n - 1) return 0;
            double a = gains[i] - gains[i - 1], b = gains[i + 1] - gains[i];
            return a * b <= 0 ? 0 : 2 * a * b / (a + b);
        }
        double t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * gains[k] + (t3 - 2 * t2 + t) * Tangent(k) + (-2 * t3 + 3 * t2) * gains[k + 1] + (t3 - t2) * Tangent(k + 1);
    }

    // Total response in dB at f (RBJ cookbook, same filters as the player).
    public static double ResponseDb(double f, IReadOnlyList<double> gains, double rate = 48000)
    {
        double db = 0;
        for (int i = 0; i < BandCount && i < gains.Count; i++)
        {
            if (Math.Abs(gains[i]) < 0.05) continue;
            var (b0, b1, b2, a0, a1, a2) = Coefficients(i, gains[i], rate);
            var z1 = Complex.FromPolarCoordinates(1, -2 * Math.PI * f / rate);
            var z2 = z1 * z1;
            db += 20 * Math.Log10(Complex.Abs((b0 + b1 * z1 + b2 * z2) / (a0 + a1 * z1 + a2 * z2)));
        }
        return db;
    }

    private static (double, double, double, double, double, double) Coefficients(int band, double gain, double rate)
    {
        double a = Math.Pow(10, gain / 40), w0 = 2 * Math.PI * FilterFrequency(band) / rate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        switch (Kind(band))
        {
            case EqBandKind.Peaking:
                {
                    double alpha = sin / (2 * Q);
                    return (1 + alpha * a, -2 * cos, 1 - alpha * a, 1 + alpha / a, -2 * cos, 1 - alpha / a);
                }
            default:
                {
                    double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / ShelfSlope - 1) + 2), s = 2 * Math.Sqrt(a) * alpha;
                    return Kind(band) == EqBandKind.LowShelf
                        ? (a * ((a + 1) - (a - 1) * cos + s), 2 * a * ((a - 1) - (a + 1) * cos), a * ((a + 1) - (a - 1) * cos - s),
                            (a + 1) + (a - 1) * cos + s, -2 * ((a - 1) + (a + 1) * cos), (a + 1) + (a - 1) * cos - s)
                        : (a * ((a + 1) + (a - 1) * cos + s), -2 * a * ((a - 1) + (a + 1) * cos), a * ((a + 1) + (a - 1) * cos - s),
                            (a + 1) - (a - 1) * cos + s, 2 * ((a - 1) - (a + 1) * cos), (a + 1) - (a - 1) * cos - s);
                }
        }
    }

    public static readonly EqPreset[] BuiltIn =
    {
        P("Flat", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        P("Bass boost", 6, 5, 4, 2, 0.5, 0, 0, 0, 0, 0),
        P("Bass reducer", -6, -5, -4, -2, -0.5, 0, 0, 0, 0, 0),
        P("Treble boost", 0, 0, 0, 0, 0, 1, 2, 4, 5, 6),
        P("Treble reducer", 0, 0, 0, 0, 0, -1, -2, -4, -5, -6),
        P("Vocal", -3, -2, -1, 0, 2, 4, 4, 3, 1, 0),
        P("Acoustic", 4, 4, 3, 1, 2, 2, 3, 3, 3, 2),
        P("Electronic", 4, 4, 1, 0, -2, 2, 1, 2, 4, 5),
        P("Dance", 5, 6, 4, 0, 1, 2, 3, 3, 2, 0),
        P("Hip-hop", 5, 4, 2, 3, -1, -1, 1, -1, 2, 3),
        P("Pop", -1, -1, 0, 2, 4, 4, 2, 0, -1, -1),
        P("Rock", 5, 4, 3, 1, -1, -1, 1, 3, 4, 5),
        P("Jazz", 4, 3, 1, 2, -2, -2, 0, 1, 3, 4),
        P("Classical", 5, 4, 3, 2, -1, -1, 0, 2, 3, 4),
        P("Piano", 3, 2, 0, 2, 3, 1, 3, 4, 3, 3),
        P("Loudness", 6, 4, 0, 0, -2, 0, -1, -5, 5, 1),
        P("Late night", 3, 2, 1, 0, 0, -1, -2, -3, -4, -5),
    };

    private static EqPreset P(string name, params double[] g) => new() { Name = name, Gains = g };
}
