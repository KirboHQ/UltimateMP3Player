using System.Runtime.CompilerServices;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace UltimateMP3Player.Core.Mods;

// A short sound a mod gave the app (ump.sounds.load / make): stereo samples at their rate, decoded in the background (Data null
// until then, or for a file that isn't a sound: a cue on it stays silent, as osu! skins' empty files mean).
public sealed class CueSound
{
    public volatile float[]? Data;
    public int Rate = 44100;
}

// Sounds to play with a song at its times (ump.sounds.cue): its file (the engine knows songs by it), each sound's time (seconds of
// the song, in order), the sound, its loudness left and right. The mixer plays them inside the song (Audio/Mixer.cs): in step with
// it at any speed, after a seek, through a crossfade.
public sealed class CueSheet
{
    public string Path = "";
    public double[] Times = Array.Empty<double>();
    public CueSound[] Sounds = Array.Empty<CueSound>();
    public float[] Left = Array.Empty<float>(), Right = Array.Empty<float>();
}

public sealed partial class ModRuntime
{
    private static readonly ConditionalWeakTable<ObjectInstance, CueSound> SoundOf = new();
    private long _soundSamples;
    // (64 MB of samples a mod, 20 s a sound, a song's cues)
    private const long MaxSoundSamples = 16L * 1024 * 1024;
    private const int MaxSoundSeconds = 20;
    private const int MaxCues = 100_000;

    private void AddSounds(Engine e, JsObject ump)
    {
        var sounds = new JsObject(e);
        JsObject Handle(CueSound s)
        {
            var o = new JsObject(e);
            SoundOf.AddOrUpdate(o, s);
            return o;
        }
        bool Take(int samples)
        {
            if (Interlocked.Add(ref _soundSamples, samples) <= MaxSoundSamples) return true;
            Interlocked.Add(ref _soundSamples, -samples);
            return false;
        }
        // load(path): a sound file of the mod's or of what the user gave it (WAV read here; Ogg, MP3 by the app), in the background.
        Fn(e, sounds, "load", a =>
        {
            var file = Readable(Str(a, 0));
            if (!File.Exists(file)) return JsValue.Null;
            if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new JavaScriptException("sounds.load: too big for a sound (16 MB).");
            var s = new CueSound();
            _ = Task.Run(() =>
            {
                try
                {
                    var bytes = File.ReadAllBytes(file);
                    // (an empty file: a skin silencing that sound)
                    if (bytes.Length == 0 || (Wav.Read(bytes) ?? _host.DecodeSound(file)) is not { } d || d.Samples.Length < 2) return;
                    var samples = d.Samples.Length > d.Rate * 2 * MaxSoundSeconds ? d.Samples[..(d.Rate * 2 * MaxSoundSeconds)] : d.Samples;
                    if (!Take(samples.Length)) { Write("sounds: too many sounds (64 MB)."); return; }
                    s.Rate = d.Rate;
                    s.Data = samples;
                }
                catch (Exception ex) { Write("sounds.load " + Path.GetFileName(file) + ": " + ex.Message); }
            });
            return Handle(s);
        });
        // make(samples, rate): a sound of the mod's own making (numbers -1..1, one channel; an array or a Float32Array), up to 20 s.
        Fn(e, sounds, "make", a =>
        {
            if (Arg(a, 0) is not ObjectInstance src) throw new JavaScriptException("sounds.make needs a list of samples.");
            int rate = Arg(a, 1).IsNumber() ? (int)Math.Clamp(TypeConverter.ToNumber(Arg(a, 1)), 8000, 192000) : 44100;
            int n = (int)Math.Min(TypeConverter.ToNumber(src.Get("length")), rate * MaxSoundSeconds);
            if (n <= 0) throw new JavaScriptException("sounds.make needs a list of samples.");
            if (!Take(n * 2)) throw new JavaScriptException("Too many sounds (64 MB).");
            var data = new float[n * 2];
            for (int i = 0; i < n; i++)
            {
                var x = (float)TypeConverter.ToNumber(src.Get(JsNumber.Create(i)));
                data[2 * i] = data[2 * i + 1] = float.IsFinite(x) ? Math.Clamp(x, -1, 1) : 0;
            }
            return Handle(new CueSound { Rate = rate, Data = data });
        });
        Fn(e, sounds, "free", a =>
        {
            if (Arg(a, 0) is ObjectInstance o && SoundOf.TryGetValue(o, out var s))
            {
                if (s.Data is { } d) Interlocked.Add(ref _soundSamples, -d.Length);
                s.Data = null;
                SoundOf.Remove(o);
            }
            return JsValue.Undefined;
        });
        // cue(songId, [[ms, sound, volume, pan], …]): played with that song at those times while it plays (volume 0..2, pan -1..1).
        // One list at a time: a new one takes the place of the last. False if the song isn't a file on this device.
        Fn(e, sounds, "cue", a =>
        {
            var id = Str(a, 0);
            if (Arg(a, 1) is not JsArray list) throw new JavaScriptException("sounds.cue needs a song's id and a list of [ms, sound, volume, pan].");
            var song = _host.Song is { } now && now.Id == id ? now : _host.Songs().FirstOrDefault(s => s.Id == id);
            if (song?.Path is not { Length: > 0 } path || !File.Exists(path)) return false;
            var cues = new List<(double T, CueSound S, float L, float R)>();
            foreach (var item in list)
            {
                if (cues.Count >= MaxCues) break;
                if (item is not JsArray c || c.Length < 2 || c[1] is not ObjectInstance h || !SoundOf.TryGetValue(h, out var sound)) continue;
                double ms = TypeConverter.ToNumber(c[0]);
                if (!double.IsFinite(ms)) continue;
                float volume = c.Length > 2 && c[2].IsNumber() ? (float)Math.Clamp(TypeConverter.ToNumber(c[2]), 0, 2) : 1;
                float pan = c.Length > 3 && c[3].IsNumber() ? (float)Math.Clamp(TypeConverter.ToNumber(c[3]), -1, 1) : 0;
                // (equal power: in the middle as loud as on one side)
                float angle = (pan + 1) * MathF.PI / 4;
                cues.Add((ms / 1000, sound, volume * MathF.Cos(angle) * MathF.Sqrt(2), volume * MathF.Sin(angle) * MathF.Sqrt(2)));
            }
            cues.Sort((x, y) => x.T.CompareTo(y.T));
            _host.Cue(this, new CueSheet
            {
                Path = path,
                Times = cues.Select(c => c.T).ToArray(),
                Sounds = cues.Select(c => c.S).ToArray(),
                Left = cues.Select(c => c.L).ToArray(),
                Right = cues.Select(c => c.R).ToArray(),
            });
            return true;
        });
        Fn(e, sounds, "clear", a => { _host.Cue(this, null); return JsValue.Undefined; });
        ump.Set("sounds", sounds);
    }

    private void StopSounds()
    {
        _host.Cue(this, null);
        Interlocked.Exchange(ref _soundSamples, 0);
    }
}

// A WAV file's samples (PCM 8, 16, 24, 32 bits or float) as stereo floats: what osu!'s hitsounds are. Null when it isn't one.
public static class Wav
{
    public static (float[] Samples, int Rate)? Read(byte[] b)
    {
        if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
        int at = 12, format = 0, channels = 0, rate = 0, bits = 0, dataAt = -1, dataLength = 0;
        while (at + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, at, 4);
            int length = BitConverter.ToInt32(b, at + 4), body = at + 8;
            if (id == "fmt " && body + 16 <= b.Length)
            {
                format = BitConverter.ToUInt16(b, body);
                channels = BitConverter.ToUInt16(b, body + 2);
                rate = BitConverter.ToInt32(b, body + 4);
                bits = BitConverter.ToUInt16(b, body + 14);
                if (format == 0xFFFE && length >= 26 && body + 26 <= b.Length) format = BitConverter.ToUInt16(b, body + 24);
            }
            else if (id == "data")
            {
                // (some writers leave its length wrong or 0: what's there counts)
                dataAt = body;
                dataLength = length > 0 ? Math.Min(length, b.Length - body) : b.Length - body;
                break;
            }
            if (length < 0) return null;
            at = body + length + (length & 1);
        }
        if (dataAt < 0 || channels is < 1 or > 8 || rate is < 4000 or > 384000) return null;
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(format == 3 && bits is 32 or 64)) return null;
        int bytes = bits / 8, frame = bytes * channels, frames = dataLength / frame;
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            int p = dataAt + i * frame;
            float l = Sample(b, p, format, bits), r = channels > 1 ? Sample(b, p + bytes, format, bits) : l;
            samples[2 * i] = l;
            samples[2 * i + 1] = r;
        }
        return (samples, rate);
    }

    private static float Sample(byte[] b, int p, int format, int bits) => (format, bits) switch
    {
        (1, 8) => (b[p] - 128) / 128f,
        (1, 16) => BitConverter.ToInt16(b, p) / 32768f,
        (1, 24) => (b[p] | b[p + 1] << 8 | (sbyte)b[p + 2] << 16) / 8388608f,
        (1, 32) => BitConverter.ToInt32(b, p) / 2147483648f,
        (3, 32) => BitConverter.ToSingle(b, p),
        _ => (float)BitConverter.ToDouble(b, p),
    };
}
