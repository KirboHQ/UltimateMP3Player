using System.IO.Compression;
using Android.Content;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The download engines inside the app (youtubedl-android's native libraries, tools\android-deps.ps1): Python with yt-dlp's
// libraries and ffmpeg's libraries come zipped as "native libraries" (lib*.zip.so) and are unpacked at the first start
// (again after an update that changes them); the programs (libpython.so, libffmpeg.so, libffprobe.so, libqjs.so) run from
// the app's folder of native libraries, the only place Android lets an app start programs from. yt-dlp (a Python
// program) is copied from the app's assets the first time and then keeps itself up to date (yt-dlp -U).
public static class AndroidEngines
{
    private static Context Ctx => Android.App.Application.Context;

    public static string NativeDir => Ctx.ApplicationInfo!.NativeLibraryDir!;
    private static string Base => Path.Combine(Ctx.NoBackupFilesDir!.AbsolutePath, "engines");
    // ffmpeg and ffprobe (links to the native libraries) and yt-dlp: Engines.Dir.
    public static string Bin => Path.Combine(Base, "bin");
    private static string PythonHome => Path.Combine(Base, "python", "usr");

    // Before anything uses the engines: what every program started from the app finds in its environment.
    public static void Configure()
    {
        Set("LD_LIBRARY_PATH", Path.Combine(PythonHome, "lib") + ":" + Path.Combine(Base, "ffmpeg", "usr", "lib"));
        Set("SSL_CERT_FILE", Path.Combine(PythonHome, "etc", "tls", "cert.pem"));
        Set("PYTHONHOME", PythonHome);
        Set("PATH", (Environment.GetEnvironmentVariable("PATH") ?? "/system/bin") + ":" + NativeDir);
        Engines.Host = new EngineHost
        {
            Dir = Bin,
            Python = Path.Combine(NativeDir, "libpython.so"),
            JsRuntime = "quickjs:" + Path.Combine(NativeDir, "libqjs.so"),
            Install = InstallAsync,
        };
        // The folder of the native libraries changes with every update of the app: the links follow it. An update that
        // brings other packages takes the links away, so Engines.Missing() asks for them to be unpacked again.
        try
        {
            if (!Directory.Exists(Bin)) return;
            if (Stale("libpython.zip.so", "python") || Stale("libffmpeg.zip.so", "ffmpeg")) File.Delete(Path.Combine(Bin, "ffmpeg"));
            else Links();
        }
        catch { }
    }

    private static string Stamp(string zipName)
    {
        var src = new FileInfo(Path.Combine(NativeDir, zipName));
        return src.Exists ? $"{src.Length}|{src.LastWriteTimeUtc.Ticks}" : "";
    }

    private static bool Stale(string zipName, string dirName)
    {
        var stampFile = Path.Combine(Base, dirName) + ".stamp";
        return !File.Exists(stampFile) || File.ReadAllText(stampFile) != Stamp(zipName);
    }

    private static void Set(string name, string value) => Environment.SetEnvironmentVariable(name, value);

    private static Task InstallAsync(Action<string, double?> progress, CancellationToken ct) => Task.Run(() =>
    {
        Unpack("libpython.zip.so", "python", L.T("Preparazione di Python"), progress, ct);
        Unpack("libffmpeg.zip.so", "ffmpeg", L.T("Preparazione di ffmpeg"), progress, ct);
        Directory.CreateDirectory(Bin);
        Links();
        var ytdlp = Path.Combine(Bin, "yt-dlp");
        if (!File.Exists(ytdlp))
        {
            progress(L.T("Preparazione di yt-dlp"), null);
            var part = ytdlp + ".part";
            using (var asset = Ctx.Assets!.Open("yt-dlp"))
            using (var file = File.Create(part))
                asset.CopyTo(file);
            File.Move(part, ytdlp, true);
        }
    }, ct);

    private static void Links()
    {
        Link("ffmpeg", "libffmpeg.so");
        Link("ffprobe", "libffprobe.so");
    }

    private static void Link(string name, string target)
    {
        var path = Path.Combine(Bin, name);
        var to = Path.Combine(NativeDir, target);
        var info = new FileInfo(path);
        if (info.LinkTarget == to && File.Exists(path)) return;
        if (info.Exists || info.LinkTarget != null) File.Delete(path);
        File.CreateSymbolicLink(path, to);
    }

    // A zipped package into its folder, unless it's already there (the zip's size and date say which one it is).
    private static void Unpack(string zipName, string dirName, string text, Action<string, double?> progress, CancellationToken ct)
    {
        var zip = Path.Combine(NativeDir, zipName);
        var dir = Path.Combine(Base, dirName);
        var stampFile = dir + ".stamp";
        if (!File.Exists(zip)) throw new EngineException(L.F("Impossibile avviare {0}.", dirName));
        var stamp = Stamp(zipName);
        if (Directory.Exists(dir) && !Stale(zipName, dirName)) return;
        try { File.Delete(stampFile); } catch { }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        using var za = ZipFile.OpenRead(zip);
        int done = 0, total = za.Entries.Count;
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var e in za.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 40 == 0) progress(text, done * 100.0 / total);
            var dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) continue;
            if (e.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(dest);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            // Unix links inside the zip (libz.so → libz.so.1.3): links here too.
            if (((e.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            {
                using var r = new StreamReader(e.Open());
                var target = r.ReadToEnd();
                if (File.Exists(dest) || new FileInfo(dest).LinkTarget != null) File.Delete(dest);
                File.CreateSymbolicLink(dest, target);
            }
            else e.ExtractToFile(dest, true);
        }
        File.WriteAllText(stampFile, stamp);
    }
}
