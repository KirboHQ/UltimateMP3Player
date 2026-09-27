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
        P("Piatto", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        P("Bassi potenziati", 6, 5, 4, 2, 0.5, 0, 0, 0, 0, 0),
        P("Bassi ridotti", -6, -5, -4, -2, -0.5, 0, 0, 0, 0, 0),
        P("Alti potenziati", 0, 0, 0, 0, 0, 1, 2, 4, 5, 6),
        P("Alti ridotti", 0, 0, 0, 0, 0, -1, -2, -4, -5, -6),
        P("Voce", -3, -2, -1, 0, 2, 4, 4, 3, 1, 0),
        P("Acustica", 4, 4, 3, 1, 2, 2, 3, 3, 3, 2),
        P("Elettronica", 4, 4, 1, 0, -2, 2, 1, 2, 4, 5),
        P("Dance", 5, 6, 4, 0, 1, 2, 3, 3, 2, 0),
        P("Hip-hop", 5, 4, 2, 3, -1, -1, 1, -1, 2, 3),
        P("Pop", -1, -1, 0, 2, 4, 4, 2, 0, -1, -1),
        P("Rock", 5, 4, 3, 1, -1, -1, 1, 3, 4, 5),
        P("Jazz", 4, 3, 1, 2, -2, -2, 0, 1, 3, 4),
        P("Classica", 5, 4, 3, 2, -1, -1, 0, 2, 3, 4),
        P("Piano", 3, 2, 0, 2, 3, 1, 3, 4, 3, 3),
        P("Loudness", 6, 4, 0, 0, -2, 0, -1, -5, 5, 1),
        P("Serale", 3, 2, 1, 0, 0, -1, -2, -3, -4, -5),
    };

    private static EqPreset P(string name, params double[] g) => new() { Name = name, Gains = g };
}
