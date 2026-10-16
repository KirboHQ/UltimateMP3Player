using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// A song of a .ump pack in the import dialog: what happens to it (new, already here, downloaded, missing).
public sealed class PackSongRow : Observable
{
    private readonly PackFile _pack;

    public PackSongRow(PackSong song, PackFile pack)
    {
        Song = song;
        _pack = pack;
    }

    public PackSong Song { get; }
    public string Title => Song.T.Title;

    public string Subtitle => string.Join(" · ", new[] { Song.T.Artist, Text.Duration(Song.T.Duration) }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string StateText => L.T(Song.State switch
    {
        PackSongState.InLibrary => "Already in your library",
        PackSongState.New => "New",
        PackSongState.Download => "To download",
        _ => "Not available",
    });

    public Brush StateBrush => (Brush)Application.Current.Resources[Song.State switch
    {
        PackSongState.InLibrary => "SuccessBrush",
        PackSongState.New => "AccentTextBrush",
        PackSongState.Download => "WarningBrush",
        _ => "ErrorBrush",
    }];

    public string StateTip => Song.State switch
    {
        PackSongState.InLibrary => L.F("You already have “{0}”: it isn't copied again.", Song.Existing?.Title ?? Song.T.Title),
        PackSongState.New => L.T("The file is in the pack: it's copied into your music folder."),
        PackSongState.Download => L.F("The file isn't in the pack: it's downloaded from {0}.", Sites.NameFor(Song.T.SourceUrl ?? "")),
        _ => L.T("The file isn't in the pack and there's no link to download it from."),
    };

    // Brought in with the current choices.
    private bool _isIncluded = true;
    public bool IsIncluded { get => _isIncluded; set => Set(ref _isIncluded, value); }

    private ImageSource? _cover;
    private bool _asked;
    public ImageSource? Cover
    {
        get
        {
            if (!_asked)
            {
                _asked = true;
                _ = LoadCover();
            }
            return _cover;
        }
    }

    private async Task LoadCover()
    {
        if (Song.Existing is { HasCover: true } e) _cover = await Images.LoadAsync(AppPaths.TrackCover(e.Id), 72, e.CoverVersion);
        _cover ??= await PackImages.LoadAsync(_pack, Pack.CoverEntry(Song.T.Id), 72);
        if (_cover != null) OnChanged(nameof(Cover));
    }
}

// Pictures inside a pack (song and playlist covers).
public static class PackImages
{
    public static async Task<BitmapSource?> LoadAsync(PackFile pack, string entry, int width)
    {
        if (!pack.Has(entry)) return null;
        return await Task.Run(() =>
        {
            try { return Images.FromBytes(pack.ReadBytes(entry), width); }
            catch { return null; }
        });
    }
}
