using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// yt-dlp "started" on the iPhone (ChildProcess.Starter, IosEngines): run inside the app by Python (ump_ios.py) on a thread
// of its own, with two pipes for what it writes, read by the app as a program's output and errors, so YtDlp, Analyzer and
// the downloads work as everywhere else. Kill stops it from inside (ump_ios.cancel) and lets it go at once: its thread
// ends by itself a moment later, and closes the pipes.
public sealed class PythonProcess : ChildProcess
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [DllImport(LibSystem, SetLastError = true)]
    private static extern unsafe int pipe(int* fds);

    [DllImport(LibSystem, SetLastError = true)]
    private static extern int close(int fd);

    [DllImport(LibSystem, SetLastError = true)]
    private static extern unsafe nint write(int fd, byte* buffer, nint count);

    private static int _ids;

    private readonly int _id = Interlocked.Increment(ref _ids);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    // argv: yt-dlp's arguments (without the program).
    public PythonProcess(IReadOnlyList<string> argv, bool errors)
    {
        var (outRead, outWrite) = Pipe();
        int errRead = -1, errWrite = -1;
        if (errors)
        {
            try { (errRead, errWrite) = Pipe(); }
            catch
            {
                close(outRead);
                close(outWrite);
                throw;
            }
        }
        StandardOutput = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read, 0);
        StandardError = errors ? new FileStream(new SafeFileHandle(errRead, true), FileAccess.Read, 0) : Stream.Null;
        // (a big stack: Python's own threads have 16 MB on Apple's systems, and yt-dlp's extractors go deep)
        new Thread(() => Run(argv, outWrite, errWrite), 16 * 1024 * 1024) { IsBackground = true, Name = "yt-dlp" }.Start();
    }

    private static unsafe (int Read, int Write) Pipe()
    {
        int* fds = stackalloc int[2];
        if (pipe(fds) != 0) throw new EngineException(L.F("Couldn't start {0}.", "yt-dlp"), "pipe: " + Marshal.GetLastPInvokeError());
        return (fds[0], fds[1]);
    }

    private void Run(IReadOnlyList<string> argv, int outWrite, int errWrite)
    {
        int code = 1;
        try
        {
            PythonHost.StartAsync().GetAwaiter().GetResult();
            if (_exit.Task.IsCompleted) return;
            var answer = PythonHost.Call(PythonHost.Json(w =>
            {
                w.WriteString("op", "run");
                w.WriteNumber("id", _id);
                w.WriteNumber("out", outWrite);
                w.WriteNumber("err", errWrite);
                w.WriteStartArray("argv");
                foreach (var a in argv) w.WriteStringValue(a);
                w.WriteEndArray();
            }));
            using var doc = JsonDocument.Parse(answer);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var c) && c.TryGetInt32(out var n)) code = n;
            else Write(errWrite, "ERROR: " + (root.TryGetProperty("error", out var e) ? e.GetString() : answer) + "\n");
        }
        catch (Exception ex)
        {
            // Python itself didn't start: said as yt-dlp's error, which the app shows.
            Write(errWrite, "ERROR: " + ex.Message + (ex is EngineException { Details.Length: > 0 } e ? "\n" + e.Details : "") + "\n");
        }
        finally
        {
            // (Python has let go of them: ump_ios closes its side of a run before answering)
            close(outWrite);
            if (errWrite >= 0) close(errWrite);
            _exit.TrySetResult(code);
        }
    }

    private static unsafe void Write(int fd, string text)
    {
        if (fd < 0) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            for (nint done = 0; done < bytes.Length;)
            {
                var n = write(fd, p + done, bytes.Length - done);
                if (n <= 0) break;
                done += n;
            }
    }

    public override Stream StandardOutput { get; }
    public override Stream StandardError { get; }
    public override bool HasExited => _exit.Task.IsCompleted;
    public override int ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : throw new InvalidOperationException("yt-dlp is still running.");
    public override Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    public override void Kill()
    {
        if (!_exit.TrySetResult(-1)) return;
        var request = PythonHost.Json(w =>
        {
            w.WriteString("op", "cancel");
            w.WriteNumber("id", _id);
        });
        // (Python's lock may be busy for a moment: not on the caller's thread. A run that hasn't begun yet won't.)
        Task.Run(async () =>
        {
            try
            {
                await PythonHost.StartAsync();
                PythonHost.Call(request);
            }
            catch { }
        });
    }

    // Downloads already run behind the app's screen and sound (Python lets the lock go often).
    public override void LowerPriority() { }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // (a read still waiting keeps its pipe open until it ends: .NET closes it after)
        try { StandardOutput.Dispose(); } catch { }
        try { StandardError.Dispose(); } catch { }
    }
}
