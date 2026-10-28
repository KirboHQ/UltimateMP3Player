using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Forms;
using System.Windows.Interop;
using UltimateMP3Player.Core;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace UltimateMP3Player.Services;

// Notification area icon, used while playing in the background. Its menu is one of the app's (a WPF menu: the look of the
// others, light or dark with the app, its icons), opened by hand on a right click: the system's can't be drawn like them.
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Action _show, _playPause, _next, _previous, _exit;
    private ContextMenu? _menu;
    private string? _nowPlaying;
    private bool _hintShown, _playing;

    public TrayIcon(Action show, Action playPause, Action next, Action previous, Action exit)
    {
        (_show, _playPause, _next, _previous, _exit) = (show, playPause, next, previous, exit);
        var exe = Environment.ProcessPath ?? "";
        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application,
            Text = "Ultimate MP3 Player",
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
        _icon.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(); };
    }

    // Built on each opening: the song playing and the labels in the language of now.
    private void ShowMenu()
    {
        if (_menu != null) _menu.IsOpen = false;
        var menu = _menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        var title = Item(_nowPlaying ?? "Ultimate MP3 Player", "", _show);
        title.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T(_playing ? "Pause" : "Play"), _playing ? "" : "", _playPause));
        menu.Items.Add(Item(L.T("Next song"), "", _next));
        menu.Items.Add(Item(L.T("Previous song"), "", _previous));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Open Ultimate MP3 Player"), "", _show));
        menu.Items.Add(Item(L.T("Quit"), "", _exit));
        menu.Closed += (_, _) => { if (_menu == menu) _menu = null; };
        menu.IsOpen = true;
        // (in front: otherwise, with the window hidden, a click elsewhere wouldn't close it)
        if (PresentationSource.FromVisual(menu) is HwndSource source) SetForegroundWindow(source.Handle);
    }

    private static MenuItem Item(string header, string glyph, Action click)
    {
        var m = new MenuItem { Header = header };
        Ui.SetGlyph(m, glyph);
        m.Click += (_, _) => click();
        return m;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // (nothing to do: the menu is made in the language of when it opens)
    public void Relabel() { }

    public void Update(string? nowPlaying, bool playing)
    {
        _playing = playing;
        _nowPlaying = nowPlaying;
        var text = nowPlaying == null ? "Ultimate MP3 Player" : (playing ? "▶ " : "⏸ ") + nowPlaying;
        _icon.Text = text.Length > 120 ? text[..120] : text;
    }

    // Tells once where the app went after closing the window.
    public void ShowBackgroundHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "Ultimate MP3 Player", L.T("Music keeps playing in the background. Click the icon to reopen the player."), ToolTipIcon.None);
    }

    // A toast of the system (a followed artist's new release); a click on it runs clicked.
    private Action? _clicked;

    public void Notify(string title, string text, Action? clicked)
    {
        if (_clicked == null) _icon.BalloonTipClicked += (_, _) => _clicked?.Invoke();
        _clicked = clicked ?? (() => { });
        _icon.ShowBalloonTip(6000, title, text, ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_menu != null) _menu.IsOpen = false;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
