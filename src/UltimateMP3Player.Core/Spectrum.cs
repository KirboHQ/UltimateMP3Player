using System.Diagnostics;

namespace UltimateMP3Player.Core;

// Where the bass goes: Settings › Appearance and each stream overlay.
public enum VisualizerFlow { LeftToRight, RightToLeft, MirrorCenter, MirrorSides }

// Where they stand: rising from the bottom, hanging from the top, both, or opening out from a line through the middle of the cover.
public enum VisualizerPlace { Bottom, Top, Both, Center }

// How the bars of the music are drawn, and their colour.
public enum VisualizerStyle { Bars, Peaks, Blocks, Dots, Line, Wave }
public enum VisualizerTint { Cover, Accent, Text, Rainbow }

// The columns a visualizer draws: where each one stands in the spectrum, how they fall when the music stops, the peaks falling
// slowly above them. The same on every screen; only the drawing differs.
public sealed class SpectrumColumns
{
    public float[] Heights { get; private set; } = Array.Empty<float>();
    public float[] Peaks { get; private set; } = Array.Empty<float>();
    // How visible they are, 0..1: they fade out when the music stops and in when it comes back (a flat line or a row of small
    // bars would pop), in the time of the app's animations.
    public float Presence { get; private set; }

    private const double FadeMs = 320;
    private long _last;

    // As many as fit, each at least a slot wide (thickness: 0 thin, 1 normal, 2 wide); on a wide window they grow instead of
    // multiplying (the spectrum only has BandCount bands, and every column more is more to draw at every frame of the screen).
    public static int CountFor(double width, int thickness, VisualizerStyle style)
    {
        int most = style switch { VisualizerStyle.Line or VisualizerStyle.Wave or VisualizerStyle.Dots => Spectrum.BandCount, VisualizerStyle.Blocks => 72, _ => 160 };
        double each = style switch
        {
            VisualizerStyle.Line or VisualizerStyle.Wave => 8,
            VisualizerStyle.Dots => thickness switch { 0 => 12, 2 => 40, _ => 20 },
            VisualizerStyle.Blocks => thickness switch { 0 => 10, 2 => 30, _ => 16 },
            _ => thickness switch { 0 => 6, 2 => 24, _ => 11 },
        };
        return Math.Clamp((int)(width / each), 4, most);
    }

    // How much of its slot a column takes: on a big window the number of columns is at its cap whatever the thickness, so this
    // is what keeps thin, normal and wide looking different.
    public static double BarOf(double slot, int thickness, VisualizerStyle style) => style switch
    {
        VisualizerStyle.Dots => Math.Min(slot * 0.62, thickness switch { 0 => 5.0, 2 => 24, _ => 10 }),
        VisualizerStyle.Blocks => slot * thickness switch { 0 => 0.45, 2 => 0.86, _ => 0.68 },
        _ => slot * thickness switch { 0 => 0.3, 2 => 0.8, _ => 0.62 },
    };

    public static double LineOf(int thickness) => thickness switch { 0 => 1.5, 2 => 5, _ => 2.5 };

    // The lines they grow from on a space h high, and which way (-1 up, +1 down); middle: where the cover's centre is.
    public static int LanesOf(VisualizerPlace place) => place is VisualizerPlace.Both or VisualizerPlace.Center ? 2 : 1;

    public static (double From, int Way) Lane(VisualizerPlace place, int lane, double h, double middle) => place switch
    {
        VisualizerPlace.Top => (0, 1),
        VisualizerPlace.Both => lane == 0 ? (h, -1) : (0, 1),
        VisualizerPlace.Center => (middle, lane == 0 ? -1 : 1),
        _ => (h, -1),
    };

    // How far a column can go: with two lanes the height is shared between them.
    public static double ReachOf(VisualizerPlace place, double h, double share) => h * share * (LanesOf(place) == 2 ? 0.5 : 1);

    // Strongest where they start, fading towards their tips: (where, from 0 the top of the space to 1 its bottom; how solid).
    public static (double At, double Alpha)[] FadeOf(VisualizerPlace place, double h, double reach, double middle)
    {
        const double Root = 0.82, Half = 0.45, Tip = 0.2;
        double T(double y) => Math.Clamp(y / h, 0, 1);
        return place switch
        {
            VisualizerPlace.Top => new[] { (0, Root), (T(reach * 0.55), Half), (T(reach), Tip) },
            VisualizerPlace.Both => new[] { (0, Root), (T(reach * 0.55), Half), (T(reach), Tip), (T(h - reach), Tip), (T(h - reach * 0.55), Half), (1, Root) },
            VisualizerPlace.Center => new[] { (T(middle - reach), Tip), (T(middle - reach * 0.55), Half), (T(middle), Root), (T(middle + reach * 0.55), Half), (T(middle + reach), Tip) },
            _ => new[] { (T(h - reach), Tip), (T(h - reach * 0.55), Half), (1.0, Root) },
        };
    }

    // True while there's something to draw. Stopped, they fade and fall to nothing and the frames can end. demo: a made-up sound
    // when nothing plays (the preview in the settings). fade off: no animations in the app. Everything goes by the clock, not
    // by the frame: the same on a 60 Hz screen as on a 200 Hz one. (Stopwatch: Windows' tick count moves in steps of 15 ms,
    // which a fast screen shows as jerks.)
    public bool Update(int count, VisualizerFlow flow, bool running, bool demo, bool fade = true)
    {
        if (Heights.Length != count)
        {
            Heights = new float[count];
            Peaks = new float[count];
        }
        long now = Stopwatch.GetTimestamp();
        // (the first frame after a rest counts for nothing: a fade starts from where it was)
        double ms = _last == 0 ? 0 : Math.Min((now - _last) * 1000.0 / Stopwatch.Frequency, 200), step = ms / 33;
        _last = now;
        float move = fade ? (float)(ms * Glide.Speed / FadeMs) : 1;
        Presence = Math.Clamp(Presence + (running ? move : -move), 0, 1);
        if (running)
        {
            var bands = Spectrum.Shared.Read();
            bool silent = demo && Array.TrueForAll(bands, b => b < 0.01f);
            double t = now * 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < count; i++)
            {
                double place = Spectrum.PlaceOf((i + 0.5) / count, flow);
                Heights[i] = silent
                    ? (float)Math.Clamp(0.55 - 0.3 * place + 0.3 * Math.Sin(t / 260 + place * 34) * Math.Sin(t / 430 + place * 11), 0.03, 1)
                    : Spectrum.At(bands, place);
            }
        }
        else
        {
            float fall = (float)Math.Pow(0.8, step);
            for (int i = 0; i < count; i++) Heights[i] *= fall;
        }
        float peakFall = 0.012f * (float)step;
        for (int i = 0; i < count; i++) Peaks[i] = Math.Max(Heights[i], Peaks[i] - peakFall);
        bool any = running || Presence > 0;
        if (!any)
        {
            Array.Clear(Heights);
            Array.Clear(Peaks);
            _last = 0;
        }
        return any;
    }
}

// What the music sounds like right now, as bands from the bass to the treble: the visualizers' data (the song's page, the
// stream overlays). While nobody looks (Wanted false) the audio thread only reads a flag. While somebody does, it copies its
// samples into a ring, without locks; the FFT runs when a viewer asks for it, at most every AnalyzeMs, and the bands move
// towards what it found at every frame of the screen.
public sealed class Spectrum
{
    public static readonly Spectrum Shared = new();

    public const int BandCount = 96;
    private const int Rate = 48000, Window = 2048, Half = Window / 2, RingSize = 32768;
    private const double MinHz = 40, MaxHz = 16000, RangeDb = 62, TiltDbPerOctave = 3;

    private float[]? _ring;
    private volatile int _write;
    private long _fedAt;
    private int _viewers;
    private volatile bool _wanted;

    private readonly object _lock = new();
    private readonly float[] _bands = new float[BandCount], _shown = new float[BandCount];
    private readonly float[] _re = new float[Half], _im = new float[Half], _power = new float[Half + 1];
    private readonly float[] _hann = new float[Window], _cos = new float[Half + 1], _sin = new float[Half + 1];
    private readonly int[] _reverse = new int[Half];
    private readonly (float From, float To, float Boost)[] _ranges = new (float, float, float)[BandCount];
    private long _lastAt, _analyzedAt;

    public Spectrum()
    {
        for (int i = 0; i < Window; i++) _hann[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Window - 1)));
        // (half a turn in Half steps: the post-pass uses each step, the FFT every second one)
        for (int i = 0; i <= Half; i++)
        {
            _cos[i] = (float)Math.Cos(Math.PI * i / Half);
            _sin[i] = (float)-Math.Sin(Math.PI * i / Half);
        }
        int bits = (int)Math.Log2(Half);
        for (int i = 0; i < Half; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i >> b & 1) != 0) r |= 1 << (bits - 1 - b);
            _reverse[i] = r;
        }
        double binHz = (double)Rate / Window, octaves = Math.Log2(MaxHz / MinHz);
        for (int b = 0; b < BandCount; b++)
        {
            double from = MinHz * Math.Pow(2, octaves * b / BandCount), to = MinHz * Math.Pow(2, octaves * (b + 1) / BandCount);
            // Music has less energy the higher it goes: without this the treble would never lift.
            _ranges[b] = ((float)(from / binHz), (float)(to / binHz), (float)(TiltDbPerOctave * Math.Log2(Math.Sqrt(from * to) / 1000)));
        }
    }

    public bool Wanted => _wanted;

    // How often the sound is looked at while the bars run: often enough for a 200 Hz screen; phones say less (their battery, and
    // the bands glide between looks anyway).
    public double AnalyzeMs { get; set; } = 7.5;

    // How long what leaves the mixer takes to be heard (AudioEngine, on the UI thread: the bars stay in time with the ears).
    public int LatencyMs { get; set; }

    // Who's about to read the bands says so, and says when it stops: at zero the audio thread goes back to doing nothing.
    public void Acquire()
    {
        if (Interlocked.Increment(ref _viewers) != 1) return;
        _ring ??= new float[RingSize];
        _wanted = true;
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _viewers) == 0) _wanted = false;
    }

    // The audio thread: stereo samples leaving the mixer. Element by element: NAudio's buffer is a byte[] seen as float[].
    public void Feed(float[] buffer, int offset, int count)
    {
        if (_ring is not { } ring) return;
        int w = _write;
        for (int i = offset, end = offset + count - 1; i < end; i += 2)
        {
            ring[w] = (buffer[i] + buffer[i + 1]) * 0.5f;
            w = (w + 1) & (RingSize - 1);
        }
        _write = w;
        Volatile.Write(ref _fedAt, Stopwatch.GetTimestamp());
    }

    // The bands as they should look now (0..1); nothing fed for a while (a pause) = silence, they fall. The array is shared:
    // read it, don't keep it.
    public float[] Read()
    {
        lock (_lock)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _lastAt) * 1000.0 / Stopwatch.Frequency;
            // (two viewers in one frame: one step)
            if (elapsed < 2 || _ring is not { } ring) return _shown;
            _lastAt = now;
            if ((now - _analyzedAt) * 1000.0 / Stopwatch.Frequency >= AnalyzeMs)
            {
                _analyzedAt = now;
                double sinceFed = (double)(now - Volatile.Read(ref _fedAt)) / Stopwatch.Frequency;
                if (sinceFed < 0.3 + LatencyMs / 1000.0) Analyze(ring, sinceFed);
                else Array.Clear(_bands);
            }
            // Up at once, down quickly (slower, on a fast screen they look late), whatever the time since the last look.
            double step = Math.Min(elapsed, 200) / 33;
            float rise = 1 - (float)Math.Pow(0.1, step), fall = (float)Math.Pow(0.75, step);
            for (int b = 0; b < BandCount; b++)
                _shown[b] = _bands[b] > _shown[b] ? _shown[b] + (_bands[b] - _shown[b]) * rise : Math.Max(_bands[b], _shown[b] * fall);
            return _shown;
        }
    }

    // The bands' value at a place from 0 (bass) to 1 (treble).
    public static float At(float[] bands, double place)
    {
        double p = Math.Clamp(place, 0, 1) * (bands.Length - 1);
        int i = (int)p;
        if (i >= bands.Length - 1) return bands[^1];
        return bands[i] + (bands[i + 1] - bands[i]) * (float)(p - i);
    }

    // The place (0 bass, 1 treble) shown by a bar at x (0 the left edge, 1 the right one).
    public static double PlaceOf(double x, VisualizerFlow flow) => flow switch
    {
        VisualizerFlow.RightToLeft => 1 - x,
        VisualizerFlow.MirrorCenter => Math.Abs(2 * x - 1),
        VisualizerFlow.MirrorSides => 1 - Math.Abs(2 * x - 1),
        _ => x,
    };

    private void Analyze(float[] ring, double sinceFed)
    {
        // The audio arrives in blocks: the window moves on with the clock between them, so the bars don't step with the blocks.
        int back = Math.Max(0, (int)((LatencyMs / 1000.0 - sinceFed) * Rate));
        back = Math.Min(back, RingSize - Window - 1);
        int start = (_write - back - Window) & (RingSize - 1);
        // Real samples packed into a complex FFT of half the size: half the work for the same bins.
        for (int i = 0; i < Half; i++)
        {
            int at = _reverse[i];
            _re[at] = ring[(start + 2 * i) & (RingSize - 1)] * _hann[2 * i];
            _im[at] = ring[(start + 2 * i + 1) & (RingSize - 1)] * _hann[2 * i + 1];
        }
        for (int size = 2; size <= Half; size <<= 1)
        {
            int half = size / 2, step = 2 * Half / size;
            for (int i = 0; i < Half; i += size)
                for (int j = 0, k = 0; j < half; j++, k += step)
                {
                    int a = i + j, c = a + half;
                    float tr = _re[c] * _cos[k] - _im[c] * _sin[k], ti = _re[c] * _sin[k] + _im[c] * _cos[k];
                    _re[c] = _re[a] - tr;
                    _im[c] = _im[a] - ti;
                    _re[a] += tr;
                    _im[a] += ti;
                }
        }
        // Back to the bins of the real signal.
        for (int k = 0; k <= Half; k++)
        {
            int a = k % Half, c = (Half - k) % Half;
            float er = (_re[a] + _re[c]) * 0.5f, ei = (_im[a] - _im[c]) * 0.5f, or = (_im[a] + _im[c]) * 0.5f, oi = (_re[c] - _re[a]) * 0.5f;
            float xr = er + or * _cos[k] - oi * _sin[k], xi = ei + or * _sin[k] + oi * _cos[k];
            _power[k] = xr * xr + xi * xi;
        }
        // Scaled so that a full-scale sine reads about 0 dB (the transform adds Window/2, the Hann window keeps half of that).
        float scale = 16f / ((float)Window * Window);
        for (int b = 0; b < BandCount; b++)
        {
            var (from, to, boost) = _ranges[b];
            int f = (int)from, t = Math.Min(Half, (int)Math.Ceiling(to));
            float power = 0;
            // Narrower than a bin (the bass): between the two nearest.
            if (t - f <= 1) power = _power[f] + (_power[Math.Min(Half, f + 1)] - _power[f]) * (from - f);
            else for (int i = f; i < t; i++) if (_power[i] > power) power = _power[i];
            double db = 10 * Math.Log10(power * scale + 1e-12) + boost;
            _bands[b] = (float)Math.Clamp(1 + db / RangeDb, 0, 1);
        }
        // A band alone far above its neighbours reads as a glitch: each one shares a little with the two beside it.
        float before = _bands[0];
        for (int b = 1; b < BandCount - 1; b++)
        {
            float here = _bands[b];
            _bands[b] = (before + 2 * here + _bands[b + 1]) * 0.25f;
            before = here;
        }
    }
}
