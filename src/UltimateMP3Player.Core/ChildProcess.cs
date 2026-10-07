using System.Diagnostics;

namespace UltimateMP3Player.Core;

// A program the app starts (ffmpeg, ffprobe, yt-dlp...) with its output read as streams. On the computers it's .NET's
// Process; the Android app starts programs its own way (Starter, Java's ProcessBuilder): .NET's way there (vfork, then the
// child puts the signal handlers back to default through Android's signal chain, whose memory it still shares with the app)
// crashed the app now and then during long downloads ("Cannot transition thread ... DONE_BLOCKING").
public abstract class ChildProcess : IDisposable
{
    public abstract Stream StandardOutput { get; }
    // Empty when not asked for (Start's errors: false).
    public abstract Stream StandardError { get; }
    public abstract bool HasExited { get; }
    public abstract int ExitCode { get; }
    public abstract Task WaitForExitAsync(CancellationToken ct = default);
    // Stops it and the programs it started.
    public abstract void Kill();
    // Work in the background (an analysis, a download): the app's screen and sound come first.
    public abstract void LowerPriority();
    public abstract void Dispose();

    // (program, arguments, working folder or null, extra environment or null, errors wanted)
    public static Func<string, IReadOnlyList<string>, string?, IReadOnlyDictionary<string, string>?, bool, ChildProcess>? Starter { get; set; }

    // Starts it with standard input closed, standard output and (if errors) standard error to read.
    public static ChildProcess Start(string exe, IEnumerable<string> args, string? workDir = null, IReadOnlyDictionary<string, string>? env = null, bool errors = true)
    {
        var list = args as IReadOnlyList<string> ?? args.ToList();
        return Starter != null ? Starter(exe, list, workDir, env, errors) : new DotNetChild(exe, list, workDir, env, errors);
    }

    private sealed class DotNetChild : ChildProcess
    {
        private readonly Process _p;

        public DotNetChild(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, bool errors)
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = errors,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            if (workDir != null) psi.WorkingDirectory = workDir;
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null)
                foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            _p = Process.Start(psi) ?? throw new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(exe)));
            try { _p.StandardInput.Close(); } catch { }
            StandardError = errors ? _p.StandardError.BaseStream : Stream.Null;
        }

        public override Stream StandardOutput => _p.StandardOutput.BaseStream;
        public override Stream StandardError { get; }
        public override bool HasExited
        {
            get
            {
                try { return _p.HasExited; }
                catch { return true; }
            }
        }
        public override int ExitCode => _p.ExitCode;
        public override Task WaitForExitAsync(CancellationToken ct = default) => _p.WaitForExitAsync(ct);

        public override void Kill()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
            catch { }
        }

        public override void LowerPriority()
        {
            try { _p.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { }
        }

        public override void Dispose() => _p.Dispose();
    }
}
