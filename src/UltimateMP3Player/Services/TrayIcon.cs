using System.Drawing;
using System.Windows.Forms;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// Notification area icon, used while playing in the background.
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _playPause, _title, _next, _previous, _open, _exit;
    private bool _hintShown, _playing;

    public TrayIcon(Action show, Action playPause, Action next, Action previous, Action exit)
    {
        var menu = new ContextMenuStrip { Renderer = new MenuRenderer(), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _title = new ToolStripMenuItem("Ultimate MP3 Player") { Enabled = false };
        _playPause = new ToolStripMenuItem("", null, (_, _) => playPause());
        _next = new ToolStripMenuItem("", null, (_, _) => next());
        _previous = new ToolStripMenuItem("", null, (_, _) => previous());
        _open = new ToolStripMenuItem("", null, (_, _) => show());
        _exit = new ToolStripMenuItem("", null, (_, _) => exit());
        menu.Items.AddRange(new ToolStripItem[] { _title, new ToolStripSeparator(), _playPause, _next, _previous, new ToolStripSeparator(), _open, _exit });
        Relabel();

        var exe = Environment.ProcessPath ?? "";
        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(exe) ?? SystemIcons.Application,
            Text = "Ultimate MP3 Player",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) show(); };
    }

    public void Relabel()
    {
        _playPause.Text = L.T(_playing ? "Pause" : "Play");
        _next.Text = L.T("Next song");
        _previous.Text = L.T("Previous song");
        _open.Text = L.T("Open Ultimate MP3 Player");
        _exit.Text = L.T("Quit");
    }

    public void Update(string? nowPlaying, bool playing)
    {
        _playing = playing;
        var text = nowPlaying == null ? "Ultimate MP3 Player" : (playing ? "▶ " : "⏸ ") + nowPlaying;
        _icon.Text = text.Length > 120 ? text[..120] : text;
        _title.Text = nowPlaying ?? "Ultimate MP3 Player";
        _playPause.Text = L.T(playing ? "Pause" : "Play");
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
        _icon.Visible = false;
        _icon.Dispose();
    }

    // The look's colours (Themes), read each time the menu is drawn: it follows the light and dark look.
    private static Color C(string name)
    {
        var c = Themes.Get(name);
        return Color.FromArgb(c.R, c.G, c.B);
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = C(e.Item.Enabled ? "Text" : "SubText");
            base.OnRenderItemText(e);
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private static Color Bg => C("Popup");
        private static Color Hover => C("Surface3");
        private static Color Line => C("Border");
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }
}
