using System.IO;
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
        PackSongState.InLibrary => "Già nella libreria",
        PackSongState.New => "Nuovo",
        PackSongState.Download => "Da scaricare",
        _ => "Non disponibile",
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
        PackSongState.InLibrary => L.F("Hai già «{0}»: non viene copiato di nuovo.", Song.Existing?.Title ?? Song.T.Title),
        PackSongState.New => L.T("Il file è nel pacchetto: viene copiato nella tua cartella della musica."),
        PackSongState.Download => L.F("Il file non è nel pacchetto: viene scaricato da {0}.", Sites.NameFor(Song.T.SourceUrl ?? "")),
        _ => L.T("Il file non è nel pacchetto e non c'è un link da cui scaricarlo."),
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
            try
            {
                var bytes = pack.ReadBytes(entry);
                if (bytes == null) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.StreamSource = new MemoryStream(bytes);
                bi.DecodePixelWidth = width;
                bi.EndInit();
                bi.Freeze();
                return (BitmapSource?)bi;
            }
            catch { return null; }
        });
    }
}
