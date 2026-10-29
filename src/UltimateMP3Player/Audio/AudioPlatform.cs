using System.IO;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UltimateMP3Player.Audio;

// What the audio engine needs from Windows: the output device (WASAPI: the one chosen in Settings, or the default one)
// and Media Foundation's decoders.
public static class AudioPlatform
{
    private static readonly MMDeviceEnumerator Devices = new();

    // The output chosen in Settings (an endpoint id), null = the system's default.
    public static string? Device { get; set; }

    // The outputs plugged in now, to choose from in Settings (read off the UI thread: with an enumerator of its own there).
    public static List<(string Id, string Name)> OutputDevices()
    {
        var list = new List<(string Id, string Name)>();
        try
        {
            using var devices = new MMDeviceEnumerator();
            foreach (var d in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d)
                    try { list.Add((d.ID, d.FriendlyName)); } catch { }
        }
        catch { }
        return list;
    }

    // Where the sound goes: the chosen output while it's plugged in, otherwise the default one.
    private static MMDevice Endpoint()
    {
        if (Device is { } id)
            try
            {
                var d = Devices.GetDevice(id);
                if (d.State == DeviceState.Active) return d;
                d.Dispose();
            }
            catch { }
        return Devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    // The device a new output would play on: when it's another one, the engine moves there.
    public static string? OutputId()
    {
        try
        {
            using var d = Endpoint();
            return d.ID;
        }
        catch { return null; }
    }

    // The rate the device mixes at (the engine resamples to it).
    public static int OutputRate()
    {
        try
        {
            using var d = Endpoint();
            return d.AudioClient.MixFormat.SampleRate;
        }
        catch { return 48000; }
    }

    public static IWavePlayer CreateOutput(int latencyMs) => new WasapiOut(Endpoint(), AudioClientShareMode.Shared, true, latencyMs);

    // Media Foundation first (MP3, AAC, MP4, WAV, FLAC, WMA), ffmpeg for the rest.
    public static ITrackSource Open(string path, double durationHint)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found", path);
        try { return new MfTrackSource(path); }
        catch { return new FfmpegTrackSource(path, TimeSpan.FromSeconds(durationHint)); }
    }

    // Headphones plugged in, Bluetooth, the chosen output unplugged or back: changed is called (on any thread) when the
    // default device changes or a device comes or goes; the engine sees whether its output must move (OutputId).
    public static IDisposable? WatchDevices(Action changed)
    {
        try { return new DeviceWatcher(changed); }
        catch { return null; }
    }

    private sealed class DeviceWatcher : IMMNotificationClient, IDisposable
    {
        private readonly MMDeviceEnumerator _devices = new();
        private readonly Action _changed;

        public DeviceWatcher(Action changed)
        {
            _changed = changed;
            _devices.RegisterEndpointNotificationCallback(this);
        }

        void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) _changed();
        }

        void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => _changed();
        void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => _changed();
        void IMMNotificationClient.OnDeviceRemoved(string deviceId) => _changed();
        void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { _devices.UnregisterEndpointNotificationCallback(this); } catch { }
            _devices.Dispose();
        }
    }
}

// Media Foundation: MP3, AAC, MP4, WAV, FLAC, WMA.
public sealed class MfTrackSource : ITrackSource
{
    private readonly MediaFoundationReader _reader;

    public MfTrackSource(string path)
    {
        _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        ISampleProvider s = _reader.ToSampleProvider();
        if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
        else if (s.WaveFormat.Channels > 2) s = new FirstTwoChannels(s);
        Samples = s;
    }

    public ISampleProvider Samples { get; }
    public TimeSpan Position => _reader.CurrentTime;
    public TimeSpan Duration => _reader.TotalTime;
    public void Seek(TimeSpan t) => _reader.CurrentTime = t < TimeSpan.Zero ? TimeSpan.Zero : t;
    public void Dispose() => _reader.Dispose();
}

// The first two channels of a multichannel source.
public sealed class FirstTwoChannels : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _ch;
    private float[] _buf = Array.Empty<float>();

    public FirstTwoChannels(ISampleProvider src)
    {
        _src = src;
        _ch = src.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        if (_buf.Length < frames * _ch) _buf = new float[frames * _ch];
        int read = _src.Read(_buf, 0, frames * _ch) / _ch;
        for (int f = 0; f < read; f++)
        {
            buffer[offset + f * 2] = _buf[f * _ch];
            buffer[offset + f * 2 + 1] = _buf[f * _ch + 1];
        }
        return read * 2;
    }
}
