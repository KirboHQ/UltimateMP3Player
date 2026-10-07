using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;
using UltimateMP3Player.Services;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    private static readonly List<Incoming> Pending = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            try
            {
                if (Host == null)
                {
                    Dispatcher.UIThread.UnhandledException += (_, e) =>
                    {
                        Log(e.Exception);
                        e.Handled = true;
                        try { Host?.Session?.Toast(L.T("Si è verificato un errore imprevisto:") + " " + e.Exception.Message); }
                        catch { }
                    };
                    CleanTemp();
                    Files.CleanUp();
                    Updater.CleanUp();
                    Spinners.Register();
                    RequeryAfterInput();
                    Host = new AppHost();
                }
                // The screen (again, if the app was closed while the music played on).
                var view = Host.View ?? new MainView(Host);
                Host.View = view;
                single.MainView = view;
            }
            catch (Exception ex)
            {
                Log(ex);
                single.MainView = new TextBlock { Text = L.T("Ultimate MP3 Player non è riuscito ad avviarsi:") + "\n\n" + ex.Message, Margin = new Thickness(24, 80) };
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    // A link, a pack, a tap on a notification: now, or once the profile is open.
    public static void Deliver(Incoming what)
    {
        if (Host?.Session != null && Host.View?.IsReady == true) Host.HandleIncoming(what);
        else Pending.Add(what);
    }

    public static void DeliverPending()
    {
        if (Host?.Session == null) return;
        var list = Pending.ToList();
        Pending.Clear();
        foreach (var what in list) Host.HandleIncoming(what);
    }

    // WPF looks at every command's CanExecute again after any input: so does this app (the buttons grey out the same way).
    private static void RequeryAfterInput()
    {
        static void Requery() => Dispatcher.UIThread.Post(System.Windows.Input.CommandManager.InvalidateRequerySuggested, DispatcherPriority.Background);
        Avalonia.Input.InputElement.PointerReleasedEvent.AddClassHandler<TopLevel>((_, _) => Requery(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        Avalonia.Input.InputElement.KeyUpEvent.AddClassHandler<TopLevel>((_, _) => Requery(), Avalonia.Interactivity.RoutingStrategies.Bubble, true);
    }

    // Leftovers of downloads interrupted (the app closed by Android halfway).
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
            Android.Util.Log.Error("UltimateMP3Player", ex.ToString());
        }
        catch { }
    }
}
