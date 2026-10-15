using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The download engines on the iPhone. iOS can't start programs: Python runs yt-dlp inside the app (PythonHost,
// PythonProcess) and the system does ffmpeg's work (MediaTools). yt-dlp is the same file as on Android (a zip run by
// Python): the app's own copy (python/app/yt-dlp) goes into the engines' folder at the first start, then it keeps itself
// up to date (yt-dlp -U, which ump_ios.py does), and an update of the app that brings a newer one takes its place.
// YouTube's JavaScript runs in the system's JavaScriptCore ("jsc", ump_ios.py). No gallery-dl and no videos.
public static class IosEngines
{
    public static string BundledYtDlp => Path.Combine(PythonHost.AppDir, "yt-dlp");

    // Before anything uses the engines (Startup).
    public static void Configure(string dataDir)
    {
        Engines.Host = new EngineHost
        {
            Dir = Path.Combine(dataDir, "engines"),
            Python = PythonHost.Library,
            JsRuntime = "jsc",
            Install = InstallAsync,
            Ffmpeg = false,
        };
        ChildProcess.Starter = Start;
    }

    // Python with yt-dlp (what ProcRunner starts on the phones: Python, then yt-dlp's path and its arguments) runs inside
    // the app; nothing else can be started here.
    private static ChildProcess Start(string exe, IReadOnlyList<string> args, string? workDir, IReadOnlyDictionary<string, string>? env, bool errors)
    {
        if (exe == PythonHost.Library && args.Count > 0 && args[0] == Engines.YtDlp)
            return new PythonProcess(args.Skip(1).ToList(), errors);
        throw new EngineException(L.F("Impossibile avviare {0}.", Path.GetFileName(exe)), "iOS can't start programs");
    }

    private static Task InstallAsync(Action<string, double?> progress, CancellationToken ct) => Task.Run(async () =>
    {
        if (!PythonHost.Bundled || !File.Exists(BundledYtDlp))
            throw new EngineException(L.T("Questa versione dell'app non contiene i motori di download."));
        if (!File.Exists(Engines.YtDlp))
        {
            progress(L.T("Preparazione di yt-dlp"), null);
            Directory.CreateDirectory(Engines.Dir);
            var part = Engines.YtDlp + ".part";
            File.Copy(BundledYtDlp, part, true);
            File.Move(part, Engines.YtDlp, true);
        }
        progress(L.T("Preparazione di Python"), null);
        await PythonHost.StartAsync();
    }, ct);
}
