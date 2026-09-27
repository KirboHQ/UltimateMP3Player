namespace UltimateMP3Player.Core;

// What a deck needs to draw and sync a song: a detailed waveform, the tempo and where the beats fall.
public sealed record DjAnalysis(float[] Peaks, double PeaksPerSecond, double? Bpm, double FirstBeat, double Duration);

// Decodes a song once (mono, 11 kHz) for the waveform and the tempo: onset strength (rises in loudness),
// the beat period that repeats the most, and the offset where the beats line up.
public static class DjAnalyzer
{
    private const int Rate = 11025, Hop = 128, PeakWindow = 74;

    public static async Task<DjAnalysis?> AnalyzeAsync(string file, CancellationToken ct = default)
    {
        var raw = Path.Combine(AppPaths.TempDir, Ids.New() + ".f32");
        try
        {
            var r = await ProcRunner.RunAsync(Engines.Ffmpeg, new[]
            {
                "-v", "error", "-y", "-t", "900", "-i", file, "-vn", "-ac", "1", "-ar", Rate.ToString(), "-f", "f32le", raw,
            }, ct: ct, captureOut: false, timeout: TimeSpan.FromMinutes(2));
            if (r.ExitCode != 0 || !File.Exists(raw)) return null;
            var bytes = await File.ReadAllBytesAsync(raw, ct);
            var s = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, s, 0, s.Length * 4);
            return Analyze(s);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
        finally
        {
            try { File.Delete(raw); } catch { }
        }
    }

    public static DjAnalysis Analyze(float[] s)
    {
        // Waveform: peak per window, then scaled so the loudest is 1.
        var peaks = new float[s.Length / PeakWindow];
        float max = 1e-6f;
        for (int p = 0; p < peaks.Length; p++)
        {
            float m = 0;
            for (int i = p * PeakWindow, end = i + PeakWindow; i < end; i++) m = Math.Max(m, Math.Abs(s[i]));
            peaks[p] = m;
            max = Math.Max(max, m);
        }
        for (int p = 0; p < peaks.Length; p++) peaks[p] /= max;
        var (bpm, first) = Tempo(s);
        return new DjAnalysis(peaks, (double)Rate / PeakWindow, bpm, first, (double)s.Length / Rate);
    }

    // The whole song is tried against beat grids: every tempo of the DJ range 88-176 BPM (0.1 steps) with its best
    // offset, then the best few again to the hundredth. A grid scores the average onset it lands on; onsets
    // come from the full band and from the low end (kick, bass) so a busy guitar can't pull it off the beat.
    // A tenth of a BPM off already drifts a beat within a minute or two.
    private static (double? Bpm, double FirstBeat) Tempo(float[] s)
    {
        var full = Onset(s);
        var low = Onset(LowPass(s, 150));
        int frames = Math.Min(full.Length, low.Length);
        if (frames < 800) return (null, 0);
        double mf = full.Average() + 1e-9, ml = low.Average() + 1e-9;
        var onset = new double[frames];
        for (int f = 0; f < frames; f++) onset[f] = full[f] / mf + low[f] / ml;

        double fps = (double)Rate / Hop;
        var coarse = new List<(double Bpm, double Phase, double Score)>();
        for (double bpm = 88; bpm < 176; bpm += 0.1)
        {
            var (ph, score) = BestPhase(onset, 60 * fps / bpm, 1);
            coarse.Add((bpm, ph, score));
        }
        // The strongest few, not counting near-duplicates of a better one.
        var picks = new List<(double Bpm, double Phase, double Score)>();
        foreach (var c in coarse.OrderByDescending(c => c.Score))
        {
            if (picks.Any(p => Math.Abs(p.Bpm - c.Bpm) < 0.35)) continue;
            picks.Add(c);
            if (picks.Count == 6) break;
        }
        double bestBpm = 0, bestPhase = 0, bestScore = double.MinValue;
        foreach (var p in picks)
            for (double bpm = p.Bpm - 0.15; bpm <= p.Bpm + 0.15; bpm += 0.01)
            {
                var (ph, score) = BestPhase(onset, 60 * fps / bpm, 0.5);
                if (score > bestScore) { bestScore = score; bestBpm = bpm; bestPhase = ph; }
            }
        if (bestScore <= 0) return (null, 0);
        double result = bestBpm;
        // DJ range: fold into 88-176 (half/double tempo is ambiguous; electronic music sits high).
        while (result < 88) result *= 2;
        while (result >= 176) result /= 2;
        return (Math.Round(result, 2), bestPhase / fps);
    }

    // The offset (in frames) whose beats land on the strongest onsets, and that average strength.
    private static (double Phase, double Score) BestPhase(double[] onset, double lag, double step)
    {
        double best = double.MinValue, bestPh = 0;
        int frames = onset.Length;
        for (double ph = 0; ph < lag; ph += step)
        {
            double sum = 0;
            int n = 0;
            for (double f = ph; f < frames - 1; f += lag)
            {
                int i = (int)f;
                double w = f - i;
                sum += onset[i] * (1 - w) + onset[i + 1] * w;
                n++;
            }
            if (n > 0 && sum / n > best) { best = sum / n; bestPh = ph; }
        }
        return (bestPh, best);
    }

    // Rises in loudness, frame by frame (log energy, only the increases).
    private static double[] Onset(float[] s)
    {
        int frames = s.Length / Hop;
        var energy = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int i = f * Hop, end = i + Hop; i < end; i++) sum += s[i] * s[i];
            energy[f] = Math.Log(1e-9 + sum);
        }
        var onset = new double[frames];
        for (int f = 1; f < frames; f++) onset[f] = Math.Max(0, energy[f] - energy[f - 1]);
        return onset;
    }

    // Two one-pole low-passes: what's left is kick and bass.
    private static float[] LowPass(float[] s, double hz)
    {
        var o = new float[s.Length];
        double k = 1 - Math.Exp(-2 * Math.PI * hz / Rate), y1 = 0, y2 = 0;
        for (int i = 0; i < s.Length; i++)
        {
            y1 += k * (s[i] - y1);
            y2 += k * (y1 - y2);
            o[i] = (float)y2;
        }
        return o;
    }
}
