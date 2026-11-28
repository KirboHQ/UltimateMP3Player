namespace UltimateMP3Player.Core;

// How late an MP3's music comes out of our decoders compared with where the song's time really starts (osu!'s BASS and ffmpeg
// drop the encoder's delay written in the LAME tag, and never play the Xing/Info header frame): the players' clock is moved
// back by it, so what's timed to the song (osu! hitsounds and circles, lyrics) lands on the music. Measured on many files
// (cross-correlated with ffmpeg): Media Foundation plays the "Info" frame (constant bitrate) as a frame of silence, skips the
// "Xing" one, keeps the encoder's delay and drops its own decoder's; NAudio + NLayer skip either frame and drop neither delay.
public static class Mp3Gapless
{
    private const int DecoderDelay = 529;

    public readonly record struct Header(string? Tag, bool Lame, int EncoderDelay, int FrameSamples, int Rate);

    // Windows' Media Foundation (MfTrackSource).
    public static TimeSpan MediaFoundationLead(string path) => Lead(path, h => (h.Tag == "Info" ? h.FrameSamples : 0) + (h.Lame ? h.EncoderDelay : 0));

    // NAudio's MP3 reader with NLayer (the other systems' Mp3TrackSource).
    public static TimeSpan NLayerLead(string path) => Lead(path, h => h.Lame ? h.EncoderDelay + DecoderDelay : 0);

    // Where Media Foundation's music really is after a jump to `at` (its own time, the lead in it): at the start of the frame
    // that time falls in (1152 samples, 26 ms), though it says it's at `at`, so the music came up to a frame early (osu!'s
    // circles and hitsounds ahead of it). A VBR file ("Xing") is jumped in through its table, not by frames: left as it is.
    public static TimeSpan MediaFoundationSeek(Header? h, TimeSpan at)
    {
        if (h is not { FrameSamples: > 0, Rate: > 0 } x || x.Tag == "Xing" || at <= TimeSpan.Zero) return at;
        long frame = (long)(at.TotalSeconds * x.Rate) / x.FrameSamples;
        return TimeSpan.FromSeconds(frame * x.FrameSamples / (double)x.Rate);
    }

    private static TimeSpan Lead(string path, Func<Header, int> samples)
    {
        try
        {
            if (!path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || Read(path) is not { Rate: > 0 } h) return TimeSpan.Zero;
            return TimeSpan.FromSeconds(Math.Clamp(samples(h), 0, 8192) / (double)h.Rate);
        }
        catch (Exception) { return TimeSpan.Zero; }
    }

    public static Header? Read(string path)
    {
        using var s = File.OpenRead(path);
        var head = new byte[10];
        long start = 0;
        if (s.Read(head, 0, 10) == 10 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
            start = 10 + (head[6] << 21 | head[7] << 14 | head[8] << 7 | head[9]) + ((head[5] & 0x10) != 0 ? 10 : 0);
        s.Position = start;
        var b = new byte[8192];
        int n = s.Read(b, 0, b.Length);
        return Parse(b, n);
    }

    // The first frame of what follows the ID3 tag: its kind, and the Xing/Info header with the LAME tag if it has them.
    public static Header? Parse(byte[] b, int n)
    {
        for (int at = 0; at + 4 < n; at++)
        {
            if (b[at] != 0xFF || (b[at + 1] & 0xE0) != 0xE0) continue;
            int version = (b[at + 1] >> 3) & 3, layer = (b[at + 1] >> 1) & 3, bitrate = b[at + 2] >> 4, rateIndex = (b[at + 2] >> 2) & 3;
            if (version == 1 || layer != 1 || bitrate is 0 or 15 || rateIndex == 3) continue;
            int rate = new[] { 44100, 48000, 32000 }[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            bool mono = b[at + 3] >> 6 == 3;
            int frameSamples = version == 3 ? 1152 : 576;
            int x = at + 4 + (version == 3 ? (mono ? 17 : 32) : (mono ? 9 : 17));
            if (x + 8 > n) return new Header(null, false, 0, frameSamples, rate);
            string tag = System.Text.Encoding.ASCII.GetString(b, x, 4);
            if (tag is not ("Xing" or "Info")) return new Header(null, false, 0, frameSamples, rate);
            int flags = b[x + 4] << 24 | b[x + 5] << 16 | b[x + 6] << 8 | b[x + 7];
            int lame = x + 8 + ((flags & 1) != 0 ? 4 : 0) + ((flags & 2) != 0 ? 4 : 0) + ((flags & 4) != 0 ? 100 : 0) + ((flags & 8) != 0 ? 4 : 0);
            // (the encoder's name, "LAME3.100", "Lavc61.3"..., then its fields: the delay in 12 bits at 21)
            bool named = lame + 24 <= n && Enumerable.Range(lame, 4).All(i => b[i] is >= (byte)'0' and <= (byte)'z');
            int delay = named ? b[lame + 21] << 4 | b[lame + 22] >> 4 : 0;
            return new Header(tag, named && delay <= 4096, delay, frameSamples, rate);
        }
        return null;
    }
}
