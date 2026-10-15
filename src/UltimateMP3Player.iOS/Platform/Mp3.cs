using System.Globalization;
using System.Text;
using Foundation;
using ObjCRuntime;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// MP3 files on the iPhone, without ffmpeg: iOS decodes MP3 but can't make them, so LAME (Frameworks/mp3lame.framework,
// built by tools/ios-deps.sh, reached through its symbols like Python) encodes what AudioToolbox decodes. The tags are
// written here: ID3v2.3, the version car stereos read, as ffmpeg writes them on the other systems.
public static class Mp3
{
    // input (anything AudioToolbox decodes) → output, constant bitrate, with the tag and the LAME header (the length, the
    // encoder's delay and padding: exact seeking and gapless playback). progress: 0..1.
    public static void Encode(string input, string output, int kbps, Tags? tags, byte[]? cover, Action<double>? progress, CancellationToken ct)
    {
        Lame.Load();
        using var source = new AppleTrackSource(input);
        int rate = source.WaveFormat.SampleRate;
        var gf = Lame.Init();
        if (gf == IntPtr.Zero) throw new EngineException(L.T("Conversione non riuscita") + ".", "lame_init");
        try
        {
            // Always two channels in (a mono source comes doubled, which costs nothing in joint stereo).
            Lame.Configure(gf, rate, kbps);
            using var file = new FileStream(output, FileMode.Create, FileAccess.ReadWrite);
            var tag = Id3.Tag(tags, cover);
            file.Write(tag);
            long audioStart = file.Position;
            const int Frames = 8192;
            var pcm = new float[Frames * 2];
            var mp3 = new byte[Frames * 5 / 4 + 7200];
            double total = Math.Max(1, source.Duration.TotalSeconds * rate);
            long done = 0;
            int got;
            while ((got = source.Read(pcm, 0, pcm.Length) / 2) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int n = Lame.Encode(gf, pcm, got, mp3);
                if (n < 0) throw new EngineException(L.T("Conversione non riuscita") + ".", "lame_encode " + n);
                file.Write(mp3, 0, n);
                done += got;
                progress?.Invoke(Math.Min(1, done / total));
            }
            int last = Lame.Flush(gf, mp3);
            if (last > 0) file.Write(mp3, 0, last);
            // The first frame was left empty for the LAME header: now that the whole song is known.
            var header = Lame.TagFrame(gf);
            if (header.Length > 0)
            {
                file.Position = audioStart;
                file.Write(header);
            }
        }
        finally { Lame.Close(gf); }
    }

    // An MP3 with its tag written again (the one it had left out); the audio stays as it is.
    public static void Retag(string input, string output, Tags? tags, byte[]? cover)
    {
        using var src = File.OpenRead(input);
        long skip = Id3.Length(src);
        src.Position = skip;
        using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
        file.Write(Id3.Tag(tags, cover));
        src.CopyTo(file);
    }
}

// The encoder's C API (lame.h, 3.100).
internal static unsafe class Lame
{
    private const int RtldNow = 0x2;
    private static readonly object Gate = new();
    private static IntPtr _lib;

    private static delegate* unmanaged<IntPtr> _init;
    private static delegate* unmanaged<IntPtr, int, int> _setNumChannels, _setInSamplerate, _setBrate, _setVbr, _setWriteVbrTag;
    private static delegate* unmanaged<IntPtr, int, void> _setWriteId3TagAutomatic;
    private static delegate* unmanaged<IntPtr, int> _initParams, _close;
    private static delegate* unmanaged<IntPtr, float*, int, byte*, int, int> _encodeInterleavedFloat;
    private static delegate* unmanaged<IntPtr, byte*, int, int> _flush;
    private static delegate* unmanaged<IntPtr, byte*, nuint, nuint> _getLametagFrame;

    private static string Library => Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath ?? Path.Combine(NSBundle.MainBundle.BundlePath, "Frameworks"),
        "mp3lame.framework", "mp3lame");

    public static void Load()
    {
        lock (Gate)
        {
            if (_lib != IntPtr.Zero) return;
            if (!File.Exists(Library)) throw new EngineException(L.T("Questa versione dell'app non sa creare MP3: scegli «Originale» come qualità dell'audio."), Library);
            var lib = Dlfcn.dlopen(Library, RtldNow);
            if (lib == IntPtr.Zero) throw new EngineException(L.T("Conversione non riuscita") + ".", Dlfcn.dlerror() ?? Library);
            IntPtr Symbol(string name)
            {
                var p = Dlfcn.dlsym(lib, name);
                return p != IntPtr.Zero ? p : throw new EngineException(L.T("Conversione non riuscita") + ".", "symbol missing: " + name);
            }
            _init = (delegate* unmanaged<IntPtr>)Symbol("lame_init");
            _setNumChannels = (delegate* unmanaged<IntPtr, int, int>)Symbol("lame_set_num_channels");
            _setInSamplerate = (delegate* unmanaged<IntPtr, int, int>)Symbol("lame_set_in_samplerate");
            _setBrate = (delegate* unmanaged<IntPtr, int, int>)Symbol("lame_set_brate");
            _setVbr = (delegate* unmanaged<IntPtr, int, int>)Symbol("lame_set_VBR");
            _setWriteVbrTag = (delegate* unmanaged<IntPtr, int, int>)Symbol("lame_set_bWriteVbrTag");
            _setWriteId3TagAutomatic = (delegate* unmanaged<IntPtr, int, void>)Symbol("lame_set_write_id3tag_automatic");
            _initParams = (delegate* unmanaged<IntPtr, int>)Symbol("lame_init_params");
            _close = (delegate* unmanaged<IntPtr, int>)Symbol("lame_close");
            _encodeInterleavedFloat = (delegate* unmanaged<IntPtr, float*, int, byte*, int, int>)Symbol("lame_encode_buffer_interleaved_ieee_float");
            _flush = (delegate* unmanaged<IntPtr, byte*, int, int>)Symbol("lame_encode_flush");
            _getLametagFrame = (delegate* unmanaged<IntPtr, byte*, nuint, nuint>)Symbol("lame_get_lametag_frame");
            _lib = lib;
        }
    }

    public static IntPtr Init() => _init();

    // Stereo float in at this rate, constant bitrate out, LAME's own quality (as ffmpeg leaves it), no tags of its own.
    public static void Configure(IntPtr gf, int rate, int kbps)
    {
        _setNumChannels(gf, 2);
        _setInSamplerate(gf, rate);
        _setVbr(gf, 0);
        _setBrate(gf, kbps);
        _setWriteVbrTag(gf, 1);
        _setWriteId3TagAutomatic(gf, 0);
        int r = _initParams(gf);
        if (r < 0) throw new EngineException(L.T("Conversione non riuscita") + ".", "lame_init_params " + r);
    }

    // frames: stereo frames in pcm (interleaved, -1..1); the bytes of MP3 written into mp3.
    public static int Encode(IntPtr gf, float[] pcm, int frames, byte[] mp3)
    {
        fixed (float* p = pcm)
        fixed (byte* m = mp3)
            return _encodeInterleavedFloat(gf, p, frames, m, mp3.Length);
    }

    public static int Flush(IntPtr gf, byte[] mp3)
    {
        fixed (byte* m = mp3) return _flush(gf, m, mp3.Length);
    }

    public static byte[] TagFrame(IntPtr gf)
    {
        var buffer = new byte[4096];
        nuint n;
        fixed (byte* b = buffer) n = _getLametagFrame(gf, b, (nuint)buffer.Length);
        return n is > 0 and <= 4096 ? buffer[..(int)n] : Array.Empty<byte>();
    }

    public static void Close(IntPtr gf)
    {
        if (gf != IntPtr.Zero) _close(gf);
    }
}

// ID3v2.3 tags: the text in UTF-16 (any language), the cover as an attached picture.
internal static class Id3
{
    public static byte[] Tag(Tags? tags, byte[]? cover)
    {
        using var frames = new MemoryStream();
        void Frame(string id, byte[] body)
        {
            frames.Write(Encoding.ASCII.GetBytes(id));
            frames.Write(BigEndian(body.Length));
            frames.WriteByte(0);
            frames.WriteByte(0);
            frames.Write(body);
        }
        // (1: UTF-16 with its byte order mark)
        static byte[] Utf16(string s) => new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(s)).ToArray();
        void Text(string id, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            Frame(id, new byte[] { 1 }.Concat(Utf16(value.Trim())).ToArray());
        }
        Text("TIT2", tags?.Title);
        Text("TPE1", tags?.Artist);
        if (!string.IsNullOrWhiteSpace(tags?.Album)) Text("TPE2", tags.Artist?.Split(',')[0].Trim());
        Text("TALB", tags?.Album);
        Text("TYER", tags?.Year);
        if (tags?.Track is int track) Text("TRCK", track.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(tags?.Comment))
            // the language, an empty description, the text
            Frame("COMM", new byte[] { 1 }.Concat(Encoding.ASCII.GetBytes("eng")).Concat(Utf16("")).Concat(new byte[] { 0, 0 })
                .Concat(Utf16(tags.Comment.Trim())).ToArray());
        if (cover is { Length: > 0 })
            // ISO-8859-1, the type, 3 = the front cover, an empty description, the picture
            Frame("APIC", new byte[] { 0 }.Concat(Encoding.ASCII.GetBytes("image/jpeg")).Concat(new byte[] { 0, 3, 0 }).Concat(cover).ToArray());
        if (frames.Length == 0) return Array.Empty<byte>();
        var tag = new MemoryStream();
        tag.Write("ID3"u8);
        tag.Write(new byte[] { 3, 0, 0 });
        tag.Write(SyncSafe((int)frames.Length));
        frames.Position = 0;
        frames.CopyTo(tag);
        return tag.ToArray();
    }

    // The bytes an ID3v2 tag takes at the start of a file (0 when it has none).
    public static long Length(Stream s)
    {
        var header = new byte[10];
        s.Position = 0;
        if (s.Read(header, 0, 10) < 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3') return 0;
        long size = (header[6] & 0x7F) << 21 | (header[7] & 0x7F) << 14 | (header[8] & 0x7F) << 7 | (header[9] & 0x7F);
        // (a footer, in version 4)
        return 10 + size + ((header[5] & 0x10) != 0 ? 10 : 0);
    }

    private static byte[] BigEndian(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    private static byte[] SyncSafe(int v) => new[] { (byte)((v >> 21) & 0x7F), (byte)((v >> 14) & 0x7F), (byte)((v >> 7) & 0x7F), (byte)(v & 0x7F) };
}
