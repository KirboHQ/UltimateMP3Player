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
        var menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Font("Segoe UI", 9.5f) };
        _title = new ToolStripMenuItem("Ultimate MP3 Player") { Enabled = false };
        _playPause = new ToolStripMenuItem("", null, (_, _) => playPause());
        _next = new ToolStripMenuItem("", null, (_, _) => next());
        _previous = new ToolStripMenuItem("", null, (_, _) => previous());
        _open = new ToolStripMenuItem("", null, (_, _) => show());
        _exit = new ToolStripMenuItem("", null, (_, _) => exit());
        menu.Items.AddRange(new ToolStripItem[] { _title, new ToolStripSeparator(), _playPause, _next, _previous, new ToolStripSeparator(), _open, _exit });
        foreach (ToolStripItem i in menu.Items) i.ForeColor = Color.FromArgb(0xEC, 0xEE, 0xF3);
        _title.ForeColor = Color.FromArgb(0x9A, 0xA3, 0xB5);
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
        _playPause.Text = L.T(_playing ? "Pausa" : "Riproduci");
        _next.Text = L.T("Brano successivo");
        _previous.Text = L.T("Brano precedente");
        _open.Text = L.T("Apri Ultimate MP3 Player");
        _exit.Text = L.T("Esci");
    }

    public void Update(string? nowPlaying, bool playing)
    {
        _playing = playing;
        var text = nowPlaying == null ? "Ultimate MP3 Player" : (playing ? "▶ " : "⏸ ") + nowPlaying;
        _icon.Text = text.Length > 120 ? text[..120] : text;
        _title.Text = nowPlaying ?? "Ultimate MP3 Player";
        _playPause.Text = L.T(playing ? "Pausa" : "Riproduci");
    }

    // Tells once where the app went after closing the window.
    public void ShowBackgroundHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "Ultimate MP3 Player", L.T("La musica continua in background. Clicca l'icona per riaprire il lettore."), ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(0xEC, 0xEE, 0xF3) : Color.FromArgb(0x9A, 0xA3, 0xB5);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x1D, 0x21, 0x29);
        private static readonly Color Hover = Color.FromArgb(0x2E, 0x33, 0x40);
        private static readonly Color Line = Color.FromArgb(0x3A, 0x41, 0x50);
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
