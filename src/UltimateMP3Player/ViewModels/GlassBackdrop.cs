using System.ComponentModel;
using System.IO;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// The glass look's picture behind the window (Core Glass): the cover of the song playing, made once per song off the UI thread, again
// when the look turns light or dark; no song or no cover: the accent's colours. Each UI shows it behind everything (Themes.ShowBackdrop).
public sealed class GlassBackdrop
{
    private readonly MainViewModel _main;
    private TrackViewModel? _watched;
    private (string? Id, int Version, bool Cover, bool Light, uint Accent, double Blur, double Bright, double Vivid)? _made;

    public GlassBackdrop(MainViewModel main)
    {
        _main = main;
        main.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Current)) Update();
        };
        // (glass chosen or left, light or dark: Themes.Apply)
        Themes.Changed += () =>
        {
            if (ReferenceEquals(_main.Host.Session, _main)) Update();
        };
        Update();
    }

    public void Update()
    {
        if (!Glass.On)
        {
            _made = null;
            Themes.ShowBackdrop(null);
            return;
        }
        var song = _main.Player.Current;
        if (!ReferenceEquals(song, _watched))
        {
            if (_watched != null) _watched.PropertyChanged -= OnSong;
            _watched = song;
            if (song != null) song.PropertyChanged += OnSong;
        }
        var look = Glass.Look;
        // (a picture of your own: the same whatever plays, made again only when it's another)
        var mine = look.Picture && File.Exists(_main.Profile.BackdropPicture) ? _main.Profile.BackdropPicture : null;
        bool light = Themes.IsLight, cover = mine == null && song?.T.HasCover == true && !look.FromTheme;
        uint a = ColorOf("Accent"), b = ColorOf("Accent2");
        // (without a cover the accent's colours make it: they count only then)
        var what = (mine != null ? "mine" : cover ? song!.Id : null, mine != null ? (int)(File.GetLastWriteTimeUtc(mine).Ticks % int.MaxValue) : cover ? song!.T.CoverVersion : 0,
            cover || mine != null, light, cover || mine != null ? 0u : a, look.Blur, look.Brightness, look.Vivid);
        if (what == _made) return;
        _made = what;
        _ = Make(song, mine != null, mine ?? (cover ? AppPaths.TrackCover(song!.Id) : null), light, a, b);
    }

    private static uint ColorOf(string key)
    {
        var c = Themes.Get(key);
        return 0xFF000000 | (uint)c.R << 16 | (uint)c.G << 8 | c.B;
    }

    // (a cover that arrives while the song plays)
    private void OnSong(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.Cover300)) Update();
    }

    private async Task Make(TrackViewModel? song, bool anySong, string? file, bool light, uint a, uint b)
    {
        var px = await Task.Run(() =>
        {
            if (file != null)
                try
                {
                    if (Themes.PixelsOf(file, 160) is { } p) return Glass.FromCover(p.Bgra, p.Width, p.Height, light);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { }
            return Glass.FromColors(a, b, light);
        });
        // (another song, look or choice meanwhile: its own picture is on its way)
        // (a picture of your own: whatever plays)
        if (!Glass.On || !anySong && !ReferenceEquals(_main.Player.Current, song) || light != Themes.IsLight || _made?.Blur != Glass.Look.Blur
            || _made?.Bright != Glass.Look.Brightness || _made?.Vivid != Glass.Look.Vivid) return;
        Themes.ShowBackdrop(px);
    }
}
