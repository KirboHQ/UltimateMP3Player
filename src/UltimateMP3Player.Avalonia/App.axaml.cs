using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;

namespace UltimateMP3Player;

public partial class App : Application
{
    private SingleInstance? _single;

    public static AppHost Host { get; private set; } = null!;
    public static string[] Args { get; set; } = Array.Empty<string>();

    // The app icon for every window (Linux uses it on the taskbar, macOS in the Dock is the bundle's).
    public static WindowIcon? WindowIcon { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        try { WindowIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/app-256.png"))); }
        catch { }
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _single?.Dispose();
            // After the main loop has started: the terms and the profile picker wait like the Windows app's dialogs.
            Dispatcher.UIThread.Post(() => Start(desktop), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // The Windows app's App.OnStartup.
    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = Args.ToList();
        WaitForPrevious(args);
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            // The running copy has another working folder: files go with their full path.
            SingleInstance.SendToFirst(args.Select(a => File.Exists(a) || Directory.Exists(a) ? Path.GetFullPath(a) : a).ToArray());
            desktop.Shutdown();
            return;
        }
        Dispatcher.UIThread.UnhandledException += OnUnhandled;
        CleanTemp();
        Updater.CleanUp();
        Views.SliderDrag.Register();
        Views.Spinners.Register();
        Views.MenuAim.Register();
        SmoothScroll.Register();
        RequeryAfterInput();
        WatchActivation();
        string? profileArg = Take(args, "--profile");
        bool background = args.Remove("--background");

        try
        {
            Host = new AppHost();
            if (!Host.AcceptTerms())
            {
                Host.Exit(true);
                return;
            }
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
            try { Dialogs.Alert("Ultimate MP3 Player", L.T("Ultimate MP3 Player non è riuscito ad avviarsi:") + "\n\n" + ex.Message); }
            catch { }
            desktop.Shutdown();
            return;
        }
        _single.Listen(a => Dispatcher.UIThread.Post(() => Host.HandleArgs(a)));
        if (args.Count > 0) Host.HandleArgs(args.ToArray());
        if (Engines.Missing().Count > 0) _ = Host.EnsureEnginesAsync();
        // .ump packs open with this copy of the app, with their icon.
        Dispatcher.UIThread.Post(FileAssociation.Register, DispatcherPriority.ApplicationIdle);
    }

    // WPF looks at every command's CanExecute again after any input: so does this app (the buttons grey out the same way).
    private static void RequeryAfterInput()
    {
        static void Requery() => Dispatcher.UIThread.Post(System.Windows.Input.CommandManager.InvalidateRequerySuggested, DispatcherPriority.Background);
        InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>((_, _) => Requery(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        InputElement.KeyUpEvent.AddClassHandler<TopLevel>((_, _) => Requery(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        InputElement.GotFocusEvent.AddClassHandler<TopLevel>((_, _) => Requery(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    // macOS: a click on the Dock icon brings the window back, and the files opened with the app (a .ump pack, songs)
    // arrive as an event instead of on the command line.
    private void WatchActivation()
    {
        if (this.TryGetFeature<IActivatableLifetime>() is not { } lifetime) return;
        lifetime.Activated += (_, e) =>
        {
            if (Host == null) return;
            switch (e)
            {
                case FileActivatedEventArgs files:
                    var paths = files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
                    if (paths.Length > 0) Host.HandleArgs(paths);
                    break;
                case ProtocolActivatedEventArgs link:
                    Host.HandleArgs(new[] { link.Uri.ToString() });
                    break;
                default:
                    if (e.Kind == ActivationKind.Reopen) Host.ShowWindow();
                    break;
            }
        };
    }

    // After a self-update the new copy waits for the old one to quit.
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

    public static void Log(Exception ex)
    {
        try
        {
            var log = Path.Combine(AppPaths.DataDir, "errori.log");
            File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    private static void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        try { Host?.Session?.Toast(L.T("Si è verificato un errore imprevisto:") + " " + e.Exception.Message); }
        catch { }
    }
}
