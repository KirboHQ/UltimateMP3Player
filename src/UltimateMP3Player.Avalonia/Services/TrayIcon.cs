using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// The icon in the notification area (Linux: the system tray; macOS: the menu bar), used while playing in the background.
// Same menu as the Windows app.
public sealed class TrayIcon : IDisposable
{
    private readonly Avalonia.Controls.TrayIcon? _icon;
    private readonly NativeMenuItem _playPause, _title, _next, _previous, _open, _exit;
    private bool _playing;

    public TrayIcon(Action show, Action playPause, Action next, Action previous, Action exit)
    {
        _title = new NativeMenuItem("Ultimate MP3 Player") { IsEnabled = false };
        _playPause = new NativeMenuItem("");
        _playPause.Click += (_, _) => playPause();
        _next = new NativeMenuItem("");
        _next.Click += (_, _) => next();
        _previous = new NativeMenuItem("");
        _previous.Click += (_, _) => previous();
        _open = new NativeMenuItem("");
        _open.Click += (_, _) => show();
        _exit = new NativeMenuItem("");
        _exit.Click += (_, _) => exit();
        Relabel();
        Available = Detect();
        if (!Available) return;
        try
        {
            var menu = new NativeMenu();
            foreach (var i in new NativeMenuItemBase[] { _title, new NativeMenuItemSeparator(), _playPause, _next, _previous, new NativeMenuItemSeparator(), _open, _exit })
                menu.Items.Add(i);
            _icon = new Avalonia.Controls.TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/app-256.png"))),
                ToolTipText = "Ultimate MP3 Player",
                Menu = menu,
                IsVisible = true,
            };
            _icon.Clicked += (_, _) => show();
            if (Application.Current != null) Avalonia.Controls.TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
        }
        catch
        {
            _icon = null;
            Available = false;
        }
    }

    // Whether there is somewhere to show it: closing the window keeps the app in the background only then.
    public bool Available { get; }

    // macOS always has the menu bar; on Linux the desktop must show "status notifier" icons (KDE, Xfce, Cinnamon, Ubuntu's
    // GNOME: yes; plain GNOME without the AppIndicator extension: no).
    private static bool Detect()
    {
        if (!OperatingSystem.IsLinux()) return true;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("dbus-send", new[]
            {
                "--session", "--print-reply", "--dest=org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus.NameHasOwner",
                "string:org.kde.StatusNotifierWatcher",
            }) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output.Contains("boolean true");
        }
        catch { return false; }
    }

    public void Relabel()
    {
        _playPause.Header = L.T(_playing ? "Pausa" : "Riproduci");
        _next.Header = L.T("Brano successivo");
        _previous.Header = L.T("Brano precedente");
        _open.Header = L.T("Apri Ultimate MP3 Player");
        _exit.Header = L.T("Esci");
    }

    public void Update(string? nowPlaying, bool playing)
    {
        _playing = playing;
        var text = nowPlaying == null ? "Ultimate MP3 Player" : (playing ? "▶ " : "⏸ ") + nowPlaying;
        if (_icon != null) _icon.ToolTipText = text.Length > 120 ? text[..120] : text;
        _title.Header = nowPlaying ?? "Ultimate MP3 Player";
        _playPause.Header = L.T(playing ? "Pausa" : "Riproduci");
    }

    // Tells once where the app went after closing the window (as a toast of the system where there is one).
    private bool _hintShown;

    public void ShowBackgroundHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        var text = L.T("La musica continua in background. Clicca l'icona per riaprire il lettore.");
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("notify-send", new[] { "-a", "Ultimate MP3 Player", "Ultimate MP3 Player", text }) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("osascript", new[] { "-e", $"display notification \"{text.Replace("\"", "'")}\" with title \"Ultimate MP3 Player\"" }) { UseShellExecute = false });
        }
        catch { }
    }

    public void Dispose()
    {
        if (_icon == null) return;
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
