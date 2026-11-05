using System.ComponentModel;
using System.IO;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// The theme "From the song": the colours of the cover playing, read once per song, off the UI thread, and not at all while nobody
// looks (the screen off, the window away): then when it's shown again. From one song to the next Themes.FadeTo moves them in place.
public sealed class SongColors
{
    private readonly MainViewModel _main;
    private TrackViewModel? _watched;
    private (string? Id, int Version, bool Cover) _read;
    private bool _pending;

    public SongColors(MainViewModel main)
    {
        _main = main;
        main.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Current)) Update();
        };
        Ui.HiddenChanged += () =>
        {
            if (_pending && !Ui.Hidden) Update();
        };
        Update();
    }

    // Chosen just now, or something else about the song.
    public void Restart()
    {
        _read = default;
        Update();
    }

    private void Update()
    {
        if (_main.Profile.Data.Theme != ThemeColors.DynamicId || _main.Player.Current is not { } song) return;
        if (!ReferenceEquals(song, _watched))
        {
            if (_watched != null) _watched.PropertyChanged -= OnSong;
            _watched = song;
            song.PropertyChanged += OnSong;
        }
        _pending = Ui.Hidden;
        if (_pending) return;
        var what = (song.Id, song.T.CoverVersion, song.T.HasCover);
        if (what == _read) return;
        _read = what;
        _ = Read(song, song.T.HasCover ? AppPaths.TrackCover(song.Id) : null);
    }

    // (a cover that arrives while the song plays)
    private void OnSong(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackViewModel.Cover300)) Update();
    }

    private async Task Read(TrackViewModel song, string? file)
    {
        string? colors = null;
        if (file != null)
            colors = await Task.Run(() =>
            {
                try { return Themes.PixelsOf(file) is { } p ? ThemeColors.Palette(p.Bgra) : null; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
            });
        if (!ReferenceEquals(_main.Player.Current, song) || _main.Profile.Data.Theme != ThemeColors.DynamicId || colors == _main.Profile.Data.SongColors) return;
        _main.Profile.Data.SongColors = colors;
        _main.Profile.Save();
        Themes.FadeTo(ThemeColors.FromSong(colors));
    }
}
