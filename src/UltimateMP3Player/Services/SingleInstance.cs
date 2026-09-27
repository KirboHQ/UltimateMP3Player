using System.IO;
using System.IO.Pipes;
using System.Text;

namespace UltimateMP3Player.Services;

// One copy only: a second launch forwards its arguments.
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = "UltimateMP3Player.SingleInstance";

    // A UMP_DATA test copy can run next to the normal one.
    private static readonly string Suffix = Environment.GetEnvironmentVariable("UMP_DATA") is { Length: > 0 } d
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..8]
        : "";
    private static readonly string PipeName = "UltimateMP3Player-" + Environment.UserName + Suffix;

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName + Suffix, out bool created);
        IsFirst = created;
    }

    // Empty arguments just show the window.
    public static bool SendToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var data = Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(data);
            return true;
        }
        catch { return false; }
    }

    public void Listen(Action<string[]> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, _cts.Token);
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    onMessage(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(200); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (IsFirst) _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}
