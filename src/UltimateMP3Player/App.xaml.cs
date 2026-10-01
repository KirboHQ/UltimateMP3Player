using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

public partial class App : Application
{
    private SingleInstance? _single;

    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args.ToList();
        WaitForPrevious(args);
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            // The running copy has another working folder: files go with their full path.
            SingleInstance.SendToFirst(args.Select(a => File.Exists(a) || Directory.Exists(a) ? Path.GetFullPath(a) : a).ToArray());
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnUnhandled;
        base.OnStartup(e);
        CleanTemp();
        Updater.CleanUp();
        SliderDrag.Register();
        MenuAim.Register();
        SmoothScroll.Register();
        string? profileArg = Take(args, "--profile");
        bool background = args.Remove("--background");

        try
        {
            Host = new AppHost();
            var profile = Host.ChooseStartupProfile(profileArg);
            if (profile == null)
            {
                Host.Exit();
                return;
            }
            Host.OpenProfile(profile);
            if (!background) Host.ShowWindow();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show(L.T("Ultimate MP3 Player non è riuscito ad avviarsi:") + "\n\n" + ex.Message, "Ultimate MP3 Player",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        _single.Listen(a => Dispatcher.BeginInvoke(() => Host.HandleArgs(a)));
        if (args.Count > 0) Host.HandleArgs(args.ToArray());
        if (Engines.Missing().Count > 0) _ = Host.EnsureEnginesAsync();
        // .ump packs open with this copy of the app, with their icon.
        Dispatcher.BeginInvoke(FileAssociation.Register, DispatcherPriority.ApplicationIdle);
    }

    // After a self-update the new exe waits for the old one to quit.
    private static void WaitForPrevious(List<string> args)
    {
        if (Take(args, "--wait") is not { } pid || !int.TryParse(pid, out var id)) return;
        try { Process.GetProcessById(id).WaitForExit(15000); } catch { }
    }

    private static string? Take(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        if (i < 0 || i + 1 >= args.Count) return null;
        var value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }

    // Leftovers of downloads interrupted by a crash.
    private static void CleanTemp()
    {
        try
        {
            foreach (var d in Directory.EnumerateFileSystemEntries(AppPaths.TempDir))
            {
                try
                {
                    if (File.GetLastWriteTime(d) > DateTime.Now.AddHours(-6)) continue;
                    if (Directory.Exists(d)) Directory.Delete(d, true);
                    else File.Delete(d);
                }
                catch { }
            }
        }
        catch { }
    }

    private static void Log(Exception ex)
    {
        try
        {
            var log = Path.Combine(AppPaths.DataDir, "errori.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        try { Host?.Session?.Toast(L.T("Si è verificato un errore imprevisto:") + " " + e.Exception.Message); }
        catch { }
    }
}
