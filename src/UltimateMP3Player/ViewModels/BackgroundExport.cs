using System.Windows;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// An export (a pack, MP3s into a folder) that its dialog can leave going on by itself ("In the background"): meanwhile it's a
// group of the downloads panel, with its bar and its Cancel (on the phones also the downloads' notification, which keeps the
// app alive), and what came of it is told by a toast.
public sealed class BackgroundExport
{
    private readonly AppHost? _host;
    private readonly DownloadGroupViewModel? _group;
    private double _last = -1;
    private string? _lastSong;

    public BackgroundExport(AppHost? host, string title, string detail, CancellationTokenSource cts)
    {
        _host = host;
        _group = host?.Downloads.AddReading(title, cts.Cancel, detail);
    }

    // The dialog went: the result is a toast.
    public bool Hidden { get; set; }

    // From the worker thread, a few times per percent at most.
    public void Report(double fraction, string? song)
    {
        if (_group == null || Math.Abs(fraction - _last) < 0.004 && song == _lastSong) return;
        _last = fraction;
        _lastSong = song;
        Application.Current.Dispatcher.BeginInvoke(() => _group.Working(fraction, song != null ? L.F("Copying “{0}”…", song) : null));
    }

    // Done (what came of it), canceled or failed (null: nothing to tell).
    public void Done(string? message)
    {
        if (_group != null) _host!.Downloads.RemoveGroup(_group);
        if (Hidden && message != null) _host?.Session?.Toast(message);
    }
}
