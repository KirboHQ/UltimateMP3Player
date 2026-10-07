using System.Collections;
using System.Text.RegularExpressions;
using Android.OS;
using Android.Runtime;
using Microsoft.Win32.SafeHandles;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The programs the app starts (Python with yt-dlp, ffmpeg, ffprobe), started by Android's own Java ProcessBuilder like
// youtubedl-android does (ChildProcess.Starter, set at the app's start). .NET's Process isn't used here: the child it makes
// with vfork shares the app's memory until it runs the program, and in that time it puts the signal handlers back to
// default through Android's signal chain, which lives in that shared memory: now and then the runtime aborted
// ("Cannot transition thread ... DONE_BLOCKING", dotnet/android#12994, during long downloads).
// The output is read from the pipes' own file descriptors (a copy of each), not through Java's streams one call at a time:
// a video's frames are tens of megabytes a second.
public sealed class JavaProcess : ChildProcess
{
    private readonly Java.Lang.Process _p;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _pid;
    private int _disposed;

    public static void Install() => Starter = (exe, args, dir, env, errors) => new JavaProcess(exe, args, dir, env, errors);

    private JavaProcess(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, bool errors)
    {
        var command = new List<string>(args.Count + 1) { exe };
        command.AddRange(args);
        using var pb = new Java.Lang.ProcessBuilder(command.ToArray());
        if (workDir != null) pb.Directory(new Java.IO.File(workDir));
        // Java's environment is the process's real one: the variables the app set in .NET (LD_LIBRARY_PATH, PYTHONHOME,
        // HOME, TMPDIR...) only live in .NET's copy, given here.
        var penv = pb.Environment()!;
        foreach (DictionaryEntry kv in System.Environment.GetEnvironmentVariables())
            if (kv.Key is string k && kv.Value is string v) penv[k] = v;
        if (env != null)
            foreach (var kv in env) penv[kv.Key] = kv.Value;
        if (!errors) pb.RedirectError(Java.Lang.ProcessBuilder.Redirect.To(new Java.IO.File("/dev/null"))!);
        try { _p = pb.Start()!; }
        catch (Java.Lang.Throwable ex) { throw new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(exe)), ex.Message ?? ""); }
        _pid = PidOf(_p);
        try { _p.OutputStream?.Close(); } catch { }
        StandardOutput = Pipe(_p.InputStream);
        StandardError = errors ? Pipe(_p.ErrorStream) : Stream.Null;
        var p = _p;
        new Thread(() =>
        {
            try { _exit.TrySetResult(p.WaitFor()); }
            catch { _exit.TrySetResult(-1); }
        }) { IsBackground = true, Name = "Program exit" }.Start();
    }

    public override Stream StandardOutput { get; }
    public override Stream StandardError { get; }
    public override bool HasExited => _exit.Task.IsCompleted;
    public override int ExitCode => _exit.Task.IsCompleted ? _exit.Task.Result : throw new InvalidOperationException("The program is still running.");
    public override Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    // "Process[pid=1234, hasExited=false]" (Android's own UNIXProcess.toString).
    private static int PidOf(Java.Lang.Process p)
    {
        try
        {
            var m = Regex.Match(p.ToString() ?? "", @"pid=(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        catch { return 0; }
    }

    // A copy of the pipe's file descriptor, read by .NET directly; Java's stream is closed at once (once the program ends,
    // Java would otherwise drain what's left in the pipe into a buffer of its own: the last lines would get lost).
    private static Stream Pipe(Stream? javaStream)
    {
        if (javaStream == null) return Stream.Null;
        try
        {
            if (javaStream is InputStreamInvoker { BaseInputStream: { } inner } && FileStreamOf(inner) is { FD: { } fd })
            {
                using var dup = ParcelFileDescriptor.Dup(fd);
                int raw = dup!.DetachFd();
                javaStream.Dispose();
                return new FileStream(new SafeFileHandle(raw, true), FileAccess.Read, 0);
            }
        }
        catch { }
        // (another Android: Java's stream, slower but the same bytes)
        return javaStream;
    }

    private static Java.Lang.Class? _fileClass, _filterClass;
    private static Java.Lang.Reflect.Field? _inField;

    // The file stream under the BufferedInputStream (ProcessPipeInputStream) Java gives.
    private static Java.IO.FileInputStream? FileStreamOf(Java.Lang.Object? s)
    {
        _fileClass ??= Java.Lang.Class.FromType(typeof(Java.IO.FileInputStream));
        _filterClass ??= Java.Lang.Class.FromType(typeof(Java.IO.FilterInputStream));
        for (int depth = 0; depth < 3 && s != null; depth++)
        {
            if (_fileClass.IsInstance(s)) return s.JavaCast<Java.IO.FileInputStream>();
            if (!_filterClass.IsInstance(s)) return null;
            if (_inField == null)
            {
                var f = _filterClass.GetDeclaredField("in");
                f.Accessible = true;
                _inField = f;
            }
            s = _inField.Get(s);
        }
        return null;
    }

    public override void Kill()
    {
        if (HasExited) return;
        // Its own programs first (yt-dlp's ffmpeg), then it.
        if (_pid > 0)
            foreach (var child in Descendants(_pid))
                try { Android.OS.Process.KillProcess(child); } catch { }
        try { _p.DestroyForcibly(); } catch { }
    }

    public override void LowerPriority()
    {
        if (_pid <= 0) return;
        try { Android.OS.Process.SetThreadPriority(_pid, Android.OS.ThreadPriority.Background); } catch { }
    }

    // The processes started by pid, and theirs (/proc/<n>/stat: "n (name) state ppid ...").
    private static List<int> Descendants(int pid)
    {
        var parents = new Dictionary<int, int>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var n)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                    var rest = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    if (rest.Length > 1 && int.TryParse(rest[1], out var ppid)) parents[n] = ppid;
                }
                catch { }
            }
        }
        catch { }
        var found = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(pid);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var (child, ppid) in parents)
                if (ppid == parent && !found.Contains(child))
                {
                    found.Add(child);
                    queue.Enqueue(child);
                }
        }
        found.Reverse();
        return found;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { StandardOutput.Dispose(); } catch { }
        try { StandardError.Dispose(); } catch { }
        // (once it has ended: the waiting thread still holds it until then)
        if (HasExited) _p.Dispose();
        else _ = _exit.Task.ContinueWith(_ => _p.Dispose(), TaskScheduler.Default);
    }
}
