using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;
using UltimateMP3Player.Views;

// The desktop app (the Avalonia one; the Windows app runs the same view models) in a headless Avalonia: its real window,
// pages and dialogs, a throwaway data folder, the keyboard instead of the mouse. Nothing reaches out: no Discord, no
// updates, no rooms or DJ (network, audio device), no file association (only Settings reads the supporters' list from
// GitHub). Screenshots go to <folder>/shots.

var root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ump-headless"));
if (Directory.Exists(root)) Directory.Delete(root, true);
var shots = Directory.CreateDirectory(Path.Combine(root, "shots")).FullName;
Environment.SetEnvironmentVariable("UMP_DATA", Path.Combine(root, "data"));

var failed = new List<string>();
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
    if (!ok) failed.Add(what);
}

var first = AppSettings.Load();
Check(!first.SaveAudio, "a new user's songs only go into the library (Save the audio off)");
first.Language = "it";
first.TermsAccepted = Dialogs.TermsVersion;
first.DiscordPresence = false;
first.AutoUpdateApp = first.AutoUpdateEngines = first.ShowWhatsNew = false;
first.MusicDir = Path.Combine(root, "music");
first.Save();

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .With(new FontManagerOptions { DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : "avares://UltimateMP3Player/Assets/Fonts#Selawik" })
    .SetupWithoutStarting();
Dispatcher.UIThread.UnhandledException += (_, e) =>
{
    Check(false, "no unhandled exception: " + e.Exception);
    e.Handled = true;
};

// What App.Start does, without the single instance, the terms and the profile picker.
var host = new AppHost();
typeof(App).GetProperty(nameof(App.Host))!.SetValue(null, host);
host.OpenProfile(host.ChooseStartupProfile(null)!);
host.ShowWindow();
var win = host.Window!;
win.Width = 1400;
win.Height = 900;
var main = host.Session!;

foreach (var (name, section, go) in new (string, string, Action)[]
{
    ("home", "home", () => main.IsHome = true),
    ("library", "library", () => main.IsLibrary = true),
    ("unsorted", "unsorted", () => main.IsUnsorted = true),
    ("downloads", "downloads", () => main.IsDownloads = true),
    ("search", "search", () => main.IsSearch = true),
    ("stats", "settings", main.OpenStats),
    ("settings", "settings", main.GoSettings),
})
{
    go();
    Shot(win, name);
    Check(main.Section == section, $"page {name} opens");
}

// ---- settings: the cards' order, the keyboard on a switch and in the search
var view = win.GetVisualDescendants().OfType<SettingsView>().First();
var cards = view.FindControl<Panel>("Cards")!;
var titles = cards.Children.Select(Title).ToList();
Console.WriteLine("        cards: " + string.Join(" · ", titles.Where(t => t != null)));
int download = titles.IndexOf(L.T("Download"));
Check(download >= 0 && titles.IndexOf(L.T("Temporary storage (unsaved songs)")) == download + 1, "settings: Temporary storage right after Download");
Check(titles.IndexOf(L.T("Listening statistics")) > titles.IndexOf(L.T("Export and import")), "settings: statistics with the library's cards");
Check(titles.LastOrDefault(t => t != null) == L.T("Support the project"), "settings: Support the project last, after Help");

var save = view.GetLogicalDescendants().OfType<CheckBox>().First(c => TextOf(c).Contains(L.T("Save the audio on your device")));
Check(save.IsChecked == false, "settings: “Save the audio on your device” is off");
save.Focus();
Press(Key.Space);
Check(host.Settings.SaveAudio && SavedAudio() == true, "settings: Space turns it on, saved in settings.json");
Press(Key.Space);
Check(!host.Settings.SaveAudio && SavedAudio() != true, "settings: Space again turns it off");

var search = view.FindControl<TextBox>("SearchBox")!;
search.Focus();
win.KeyTextInput("statistiche");
Shot(win, "settings-search");
var shown = cards.Children.Where(c => c.IsVisible).Select(Title).ToList();
Check(shown.Contains(L.T("Listening statistics")) && shown.Count < titles.Count, $"settings search “statistiche”: {shown.Count} cards ({string.Join(", ", shown)})");
Press(Key.Escape);
Check(search.Text == "" && cards.Children.All(c => c.IsVisible), "settings search: Esc empties it, every card back");
search.Focus();
win.KeyTextInput("donazione");
Shot(win, "settings-support");
shown = cards.Children.Where(c => c.IsVisible).Select(Title).ToList();
Check(shown is [var only] && only == L.T("Support the project"), $"settings search “donazione”: {string.Join(", ", shown)}");
Press(Key.Escape);

// ---- packs: the export dialog, and the import of a links-only pack (its songs into the cloud unless Settings say otherwise)
var lists = main.Playlists.Where(p => !p.IsOther).Select(p => new PackDialogs.ExportList(p.P, p.Name, p.CountText, () => new ContentControl())).ToList();
var tags = main.Tags.Select(t => new PackDialogs.ExportTag(t.T, t.Brush, t.Count)).ToList();
var export = PackDialogs.BuildExport(main.Library, main.Profile, lists, tags, Array.Empty<Playlist>(), null);
export.Show();
Shot(export, "pack-export");
var linksOnly = export.GetLogicalDescendants().OfType<RadioButton>().FirstOrDefault(r => TextOf(r).Contains(L.T("Links only")));
Check(linksOnly is { IsChecked: true }, "pack export: “Links only” is the default (the audio files only when chosen)");
var behind = export.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => TextOf(b).Contains(L.T("In the background")) || b.Content as string == L.T("In the background"));
Check(behind is { IsVisible: false }, "pack export: “In the background” only while it writes");
export.Close();

// ---- an export left to go on by itself: a group of the downloads panel with its bar, gone when it's done
main.IsHome = true;
var exporting = new BackgroundExport(host, L.F("Exporting “{0}”", "Headless.ump"), L.T("Preparing the pack…"), new CancellationTokenSource());
exporting.Report(0.5, "A song");
Shot(win, "export-background");
var group = host.Downloads.Groups.FirstOrDefault();
Check(main.ShowDownloadPanel && group is { Percent: 50 } && Shown(L.F("Copying “{0}”…", "A song")), "export in the background: its bar in the panel: " + group?.Detail);
exporting.Done(null);
Check(host.Downloads.Groups.Count == 0, "export in the background: done, its line goes");

// ---- internet radios: a saved one on the Radio page and on Home (nothing fetched: the page's lists come when it's opened by hand)
main.Profile.SaveStation(new Station { Id = "headless-fm", Name = "Headless FM", Url = "http://127.0.0.1:9/stream", Tags = "rock,indie", Country = "Italia", Codec = "MP3" });
main.Navigate(main.Stations);
Shot(win, "radio");
Check(main.Section == "radio" && main.IsRadio && Shown("Headless FM") && Shown("Rock · Indie · Italia"), "radio: the page, the saved station and what it plays");
main.IsHome = true;
Shot(win, "home-radios");
Check(Shown(L.T("Your radios")) && Shown("Headless FM"), "radio: Home's shelf of saved radios");
main.Stations.Toggle(main.Stations.Saved[0].S);
Dispatcher.UIThread.RunJobs();
Check(!main.Stations.HasSaved && !main.Profile.HasStation("headless-fm"), "radio: the heart takes it away again");

// ---- the recap of the year: its pages (one play is enough)
main.Profile.Log.Add("headless-song", 240, true, DateTime.Now);
main.OpenRecap(DateTime.Now.Year, 0);
Shot(win, "recap");
Check(main.Page is RecapViewModel { IsEmpty: false }, "recap: the year's opens");
main.BackCommand.Execute(null);

// ---- a radio's logo as its cover: a see-through one on the station's colour, a solid one on its edge's colour
byte[] Png(int w, int h, SkiaSharp.SKColor back, SkiaSharp.SKColor dot)
{
    using var bmp = new SkiaSharp.SKBitmap(w, h);
    using (var c = new SkiaSharp.SKCanvas(bmp))
    {
        c.Clear(back);
        using var paint = new SkiaSharp.SKPaint { Color = dot, IsAntialias = true };
        c.DrawCircle(w / 2f, h / 2f, w / 3f, paint);
    }
    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
    return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
}
bool Near(byte[]? jpeg, int r, int g, int b, int y = 2)
{
    if (jpeg == null) return false;
    using var bmp = SkiaSharp.SKBitmap.Decode(jpeg);
    var c = bmp.GetPixel(2, y);
    return bmp.Width == 512 && Math.Abs(c.Red - r) < 12 && Math.Abs(c.Green - g) < 12 && Math.Abs(c.Blue - b) < 12;
}
var clearLogo = Images.TileJpeg(Png(64, 64, SkiaSharp.SKColors.Transparent, SkiaSharp.SKColors.White), 512, (w, h, px) => StationsViewModel.Tile(w, h, px, "#7C5CFF"));
var solidLogo = Images.TileJpeg(Png(300, 300, new SkiaSharp.SKColor(0xE5, 0x48, 0x4D), SkiaSharp.SKColors.White), 512, (w, h, px) => StationsViewModel.Tile(w, h, px, "#7C5CFF"));
Check(Near(clearLogo, 0x7C, 0x5C, 0xFF), "radio cover: a see-through logo on the station's colour");
Check(Near(solidLogo, 0xE5, 0x48, 0x4D), "radio cover: a solid logo on its edge's colour");
// (a small picture, light blue above and dark below: a shade from one to the other, a bit darker)
byte[] Photo()
{
    using var bmp = new SkiaSharp.SKBitmap(64, 64);
    for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            bmp.SetPixel(x, y, y < 32 ? new SkiaSharp.SKColor(100, 160, 240) : new SkiaSharp.SKColor(30, 20, 20));
    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
    return img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
}
var photoLogo = Images.TileJpeg(Photo(), 512, (w, h, px) => StationsViewModel.Tile(w, h, px, "#7C5CFF"));
Check(Near(photoLogo, 80, 128, 192) && Near(photoLogo, 24, 16, 16, 509), "radio cover: a picture on a shade of its top and bottom colours");

// ---- an artist's page: Change image over the picture, Rename and the picture in its menu; a rename reaches the songs where
// it's one of several artists
var duet = new Track { Id = "headless-duet", Title = "Duet", Artist = "Headless Artist feat. Someone", Duration = 200, SourceUrl = "https://example.com/duet" };
main.Library.Add(duet);
main.OpenArtist("Headless Artist", null);
Dispatcher.UIThread.RunJobs();
Shot(win, "artist");
var artistPage = main.Page as ArtistPageViewModel;
// (its menu: Choose the artist…, Open on its site once known, Rename, Change image)
string[] MenuOf(ArtistPageViewModel a) => Menus.ForArtist(a).Items.OfType<MenuItem>().Select(m => m.Header as string ?? "").ToArray();
Check(artistPage is { HasSongs: true } && MenuOf(artistPage) is [var choose, .., var rename, var change] && choose == L.T("Choose the artist…")
    && rename == L.T("Rename…") && change == L.T("Change image…")
    && win.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "PictureButton"), "artist: its page, Change image, the menu (Choose the artist, Rename, Change image)");
File.WriteAllBytes(ArtistPictures.Mine("Headless Artist"), clearLogo!);
Check(artistPage!.HasMyPicture && MenuOf(artistPage).Last() == L.T("Remove image"), "artist: a picture chosen by hand, and Remove image");
main.Library.RenameArtist("Headless Artist", "Headless Band", inLines: true);
Check(duet.Artist == "Headless Band feat. Someone", "artist: renamed where it's one of several: " + duet.Artist);
// (a name written like a handle, spelled as the library or the song found write it; one with capitals stays; no network here)
string? Spell(string name, params string?[] hints) => ArtistNames.SpellAsync(main.Library, name, CancellationToken.None, hints).GetAwaiter().GetResult();
Check(Spell("headlessband") == "Headless Band" && Spell("ne obliviscaris", "Ne Obliviscaris") == "Ne Obliviscaris" && Spell("Avicii") == "Avicii",
    "artist: a handle spelled as the library writes it");
main.BackCommand.Execute(null);

var manifest = new PackManifest { Music = false, Author = "Headless" };
manifest.Tracks.Add(new PackTrack { Id = "t1", Title = "A song", Artist = "An artist", Duration = 200, SourceUrl = "https://www.youtube.com/watch?v=aaaaaaaaaaa" });
var pack = PackFile.Remote("Test pack", manifest, 4096, Array.Empty<string>(), (_, _, _, _) => Task.FromResult(false), null);
var songs = PackImporter.Analyze(pack, main.Library);
Check(songs is [{ State: PackSongState.Download }], "pack: a song with only its link is to be fetched");
Check(manifest.Credits(4096).StartsWith(L.F("by {0}", "Headless")) && manifest.Credits(4096).Contains(L.T("links only")), "pack: credits line");
var import = PackDialogs.BuildImport(pack, songs, main.Library, main.Profile, host.Settings.MusicDir, () => Task.CompletedTask, _ => { }, host.Settings);
import.Show();
Shot(import, "pack-import");
var cloudBox = import.GetLogicalDescendants().OfType<CheckBox>().FirstOrDefault(c => TextOf(c).Contains(L.T("Save the audio on your device")));
Check(cloudBox is { IsChecked: false }, "pack import: “Save the audio on your device” starts off");
import.Close();

// ---- Discord: the paused bar's dot where the song is
var bar = typeof(DiscordPresence).GetMethod("Bar", BindingFlags.NonPublic | BindingFlags.Static)!;
string Bar(double at) => (string)bar.Invoke(null, new object[] { TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(200) })!;
Check(Bar(0).Contains("●" + new string('─', 11)), "discord: at 0% the dot is at the start: " + Bar(0));
Check(Bar(100).Contains(new string('━', 6) + "●" + new string('─', 5)), "discord: at 50% in the middle: " + Bar(100));
Check(Bar(200).Contains(new string('━', 11) + "●"), "discord: at 100% at the end: " + Bar(200));

// ---- a list of songs: read like a download, in the panel of the other pages; read, "loading complete" until the download
// page is seen. (An Exportify CSV: its Spotify ids need no network.)
main.IsHome = true;
var queue = host.Downloads;
var reading = queue.AddReading("Headless list", () => { });
typeof(DownloadGroupViewModel).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(reading, new object[] { 120, 431, true });
Shot(win, "list-reading");
Check(main.ShowDownloadPanel && Shown(reading.Detail) && reading.Detail.Contains(L.T("the site asks to wait")), "list: its bar in the panel while it's read: " + reading.Detail);
_ = queue.CancelGroupAsync(reading);
Check(queue.Groups.Count == 0, "list: its ✕ stops it and takes it away");
var csvList = "Track URI,Track Name,Artist Name(s),Duration (ms)\nspotify:track:4uLU6hMCjMI75M1A2tKUQC,Never Gonna Give You Up,Rick Astley,213573\n" +
              "spotify:track:7ouMYWpwJ422jRcDASZB7P,Heroes,David Bowie,371000\n";
var importing = main.Downloads.ImportListAsync(csvList, "Headless list");
for (int i = 0; i < 300 && !importing.IsCompleted; i++)
{
    Dispatcher.UIThread.RunJobs();
    Thread.Sleep(10);
}
Shot(win, "list-ready");
var ready = queue.Groups.FirstOrDefault();
Check(ready is { Reading: false } && ready.Detail == L.T("Loading complete") + " · " + L.Count(2, "1 song", "{0} songs") && Shown(ready.Detail),
    "list: read, the panel says so: " + ready?.Detail);
ready?.OpenCommand.Execute(null);
Dispatcher.UIThread.RunJobs();
Shot(win, "list-opened");
Check(main.Section == "downloads" && queue.Groups.Count == 0 && main.Downloads.Link?.Items.Count == 2, "list: a click opens its card, the panel's line goes");

// ---- 4.3.1: Search in the sidebar's menu, its box at the top of its page (online off: nothing reaches out), one list under chips;
// the library's tabs
host.Settings.OnlineSearch = false;
main.Search.OnSettingsChanged();
main.IsHome = true;
main.IsSearch = true;
Dispatcher.UIThread.RunJobs();
Shot(win, "search-empty");
Check(main.Page == main.Search && main.Search.Items is [SearchNote] && Shown(L.T("Search your music and online")), "search: the menu's Search opens its page, saying what it's for");
var box = win.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
box.Focus();
win.KeyTextInput("headless");
Press(Key.Enter);
Shot(win, "search-results");
Check(main.Page == main.Search && main.Search.Items.OfType<SearchSection>().Any(x => x.Title == L.T("Songs"))
    && main.Search.Items.OfType<TrackRow>().Any(r => r.Track.Title == "Duet") && Shown("Duet"), "search: the page's box (Enter), your song under Songs");
main.Search.Filter = SearchViewModel.ArtistsKey;
Shot(win, "search-artists");
Check(main.Search.Items.OfType<CardShelf>().Any(x => x.Cards.OfType<ArtistCardViewModel>().Any(c => c.Name == "Headless Band")), "search: the Artists chip, your artist's card");
main.Search.Filter = SearchViewModel.AlbumsKey;
Dispatcher.UIThread.RunJobs();
Check(main.Search.Items is [SearchNote], "search: Albums and EPs without the online search says so");
box.Text = "";
Dispatcher.UIThread.RunJobs();
Check(main.Page == main.Search && main.Search.Items is [SearchNote] && !main.Search.HasQuery, "search: the box emptied, the page stays");
main.IsUnsorted = true;
Shot(win, "library-unsorted");
Check(main.Page is LibraryViewModel { ShowTabs: true, IsAll: false }, "library: “Not in a playlist” is a tab of the library");
main.IsHome = true;

// ---- 4.3.1: Spotify's library in its zip (liked songs, a playlist) as lists for ListImport (their Spotify ids: nothing looked for)
var zipPath = Path.Combine(root, "spotify.zip");
using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
{
    void Entry(string name, string json)
    {
        using var w = new StreamWriter(zip.CreateEntry("Spotify Account Data/" + name).Open());
        w.Write(json);
    }
    Entry("YourLibrary.json", """{"tracks":[{"artist":"Rick Astley","album":"Whenever You Need Somebody","track":"Never Gonna Give You Up","uri":"spotify:track:4uLU6hMCjMI75M1A2tKUQC"},{"artist":"","track":"No artist"}],"albums":[]}""");
    Entry("Playlist1.json", """{"playlists":[{"name":"Road trip","items":[{"track":{"trackName":"Heroes","artistName":"David Bowie","albumName":"Heroes","trackUri":"spotify:track:7ouMYWpwJ422jRcDASZB7P"},"episode":null},{"track":null,"episode":{"episodeName":"A podcast"}},{"track":{"trackName":"My file","artistName":"Me","albumName":"","trackUri":"spotify:local:Me::My+file:200"}}]}]}""");
    Entry("StreamingHistory_music_0.json", """[{"endTime":"2025-01-02 10:00","artistName":"David Bowie","trackName":"Heroes","msPlayed":200000}]""");
}
var (liked, playlists) = SpotifyHistory.ReadLibrary(new[] { zipPath });
Check(liked is [{ Title: "Never Gonna Give You Up", SpotifyId: "4uLU6hMCjMI75M1A2tKUQC" }], "spotify: the liked songs (one without an artist left out)");
Check(playlists is [{ Name: "Road trip", Songs: [{ SpotifyId: "7ouMYWpwJ422jRcDASZB7P" }, { Title: "My file", SpotifyId: null }] }], "spotify: a playlist (the podcast left out)");
var likedList = ListImport.Parse(SpotifyHistory.AsList(liked));
Check(likedList is [{ SpotifyId: "4uLU6hMCjMI75M1A2tKUQC", Album: "Whenever You Need Somebody" }], "spotify: the liked songs as a list, with their ids");
Check(SpotifyHistory.Read(new[] { zipPath }) is [{ Title: "Heroes" }], "spotify: the plays in the same zip");

host.Exit(true);
Check(!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length == 0, "nothing in errors.log");
Console.WriteLine(failed.Count == 0 ? "headless: ok" : $"headless: {failed.Count} FAILED");
return failed.Count == 0 ? 0 : 1;

// Lets the queued work, the layout and a frame happen, then saves what's drawn.
void Shot(TopLevel top, string name)
{
    for (int i = 0; i < 10; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(20);
    }
    top.CaptureRenderedFrame()?.Save(Path.Combine(shots, name + ".png"));
}

void Press(Key key)
{
    var physical = key switch { Key.Space => PhysicalKey.Space, Key.Enter => PhysicalKey.Enter, _ => PhysicalKey.Escape };
    win.KeyPress(key, RawInputModifiers.None, physical, null);
    win.KeyRelease(key, RawInputModifiers.None, physical, null);
    Dispatcher.UIThread.RunJobs();
}

bool? SavedAudio()
{
    var m = Regex.Match(File.ReadAllText(AppPaths.SettingsFile), "\"SaveAudio\"\\s*:\\s*(true|false)");
    return m.Success ? m.Groups[1].Value == "true" : null;
}

bool Shown(string text) => win.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);

static string TextOf(ILogical c) => string.Join(" ", c.GetSelfAndLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

static string? Title(Control card) => card.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("card-title"))?.Text;
