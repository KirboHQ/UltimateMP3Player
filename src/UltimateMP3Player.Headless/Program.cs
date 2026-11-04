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

// ("perf": the real window on the screen with its real GPU, on top for half a minute, frames counted while a silent song plays)
bool perf = args.Contains("perf");
var folder = args.FirstOrDefault(a => a != "perf");
var root = Path.GetFullPath(folder ?? Path.Combine(Path.GetTempPath(), "ump-headless"));
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

if (perf) AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
else AppBuilder.Configure<App>()
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
if (perf)
{
    Perf();
    host.Exit(true);
    return 0;
}

foreach (var (name, section, go) in new (string, string, Action)[]
{
    ("home", "home", () => main.IsHome = true),
    ("library", "library", () => main.IsLibrary = true),
    ("unsorted", "unsorted", () => main.IsUnsorted = true),
    ("playlists", "playlists", () => main.LibraryHub.PlaylistsCommand.Execute(null)),
    ("artists", "artists", main.OpenArtists),
    ("together", "together", () => main.IsTogether = true),
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
// (4.4: several small cards are parts of one: Download with the temporary storage, Search and suggestions, Library, Backup with
// export and import, About with updates, help and support)
int download = titles.IndexOf(L.T("Download"));
Check(download >= 0 && TextOf(cards.Children[download]).Contains(L.T("Temporary storage (unsaved songs)")) && titles.IndexOf(L.T("Search and suggestions")) == download + 1,
    "settings: Temporary storage inside Download, then Search and suggestions");
Check(titles.IndexOf(L.T("Listening statistics")) == titles.IndexOf(L.T("Backup, export and import")) + 1
    && titles.IndexOf(L.T("Backup, export and import")) == titles.IndexOf(L.T("Library")) + 1, "settings: Library, Backup, then statistics");
Check(titles.LastOrDefault(t => t != null) == L.T("About") && TextOf(cards.Children[^1]).Contains(L.T("Support the project")), "settings: About last, with Support the project");

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
var parts = cards.Children[^1] is Border { Child: Panel about } ? about.Children.Where(c => c.IsVisible).Select(TextOf).ToList() : new();
Check(shown is [var only] && only == L.T("About") && parts.Count == 2 && parts[1].Contains(L.T("Support the project")),
    $"settings search “donazione”: only Support the project, in About: {string.Join(", ", shown)}");
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
Check(main.Page == main.Search && main.Search.ShowDownloads && Shown(L.T("Headless list")), "search: the menu's Search opens its page, the downloads (the list's card) under its box");
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
Check(main.Shown == main.LibraryHub && main.LibraryHub.IsUnsorted && Shown(L.T("Playlists")), "library: “Not in a playlist” is a tab of the library, with Playlists");

// ---- 4.4: the library's parts are tabs of one page; back and forward; the keyboard's pages; up next popped out
main.OpenArtists();
Shot(win, "library-artists");
Check(main.Shown == main.LibraryHub && main.LibraryHub.IsArtists && main.IsLibraryNav, "library: Artists is a tab of the library");
main.IsHome = true;
main.BackCommand.Execute(null);
Check(main.Page is ArtistsViewModel && main.ForwardCommand.CanExecute(null), "back: the page before, forward possible");
main.ForwardCommand.Execute(null);
Check(main.Page is HomeViewModel && !main.ForwardCommand.CanExecute(null), "forward: the page left with back");
win.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
win.KeyRelease(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
Dispatcher.UIThread.RunJobs();
Check(main.IsSearch, "Ctrl+down: from Home to Search");
win.KeyPress(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, null);
win.KeyRelease(Key.D3, RawInputModifiers.Control, PhysicalKey.Digit3, null);
win.KeyPress(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
win.KeyRelease(Key.Right, RawInputModifiers.Control, PhysicalKey.ArrowRight, null);
Dispatcher.UIThread.RunJobs();
Check(main.IsLibraryNav && main.LibraryHub.IsRadio, "Ctrl+3 then Ctrl+right: the library's next tab (the search box left behind lets go of the keys)");
win.KeyPress(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
win.KeyRelease(Key.Left, RawInputModifiers.Alt, PhysicalKey.ArrowLeft, null);
Dispatcher.UIThread.RunJobs();
Check(main.LibraryHub.IsArtists, "Alt+left: back");
// ---- Settings opened again: its audio output box still shows the one chosen (the outputs are read again)
main.GoSettings();
Shot(win, "settings-again");
var output = win.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => Avalonia.Automation.AutomationProperties.GetName(c) == L.T("Audio output"));
Check(output is { SelectedIndex: >= 0 }, "settings opened again: the audio output still shown");
// ---- a page left and opened again keeps its view (not built again); Ctrl+↓ goes on from the search's box
main.IsTogether = true;
Shot(win, "together-again");
var together = win.GetVisualDescendants().OfType<TogetherView>().FirstOrDefault();
main.IsHome = true;
Shot(win, "home-again");
main.IsTogether = true;
Shot(win, "together-again");
Check(together != null && win.GetVisualDescendants().OfType<TogetherView>().SingleOrDefault() == together, "a page opened again keeps its view");
main.IsSearch = true;
Shot(win, "search-focus");
win.KeyPress(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
win.KeyRelease(Key.Down, RawInputModifiers.Control, PhysicalKey.ArrowDown, null);
Dispatcher.UIThread.RunJobs();
Check(main.IsLibraryNav, "Ctrl+down: from the search's box to the library");
main.IsHome = true;
main.QueueOpen = true;
Shot(win, "queue");
Check(win.GetVisualDescendants().OfType<QueuePopup>().Any(q => q.IsEffectivelyVisible) && Shown(L.T("The queue is empty")), "up next pops out over the page");
Press(Key.Escape);
Check(!main.QueueOpen, "Esc folds up next");
// ---- 4.4.1: the player bar goes down out of the window for the DJ, and comes back up
var playerBar = win.FindControl<Border>("PlayerBar")!;
main.IsDj = true;
Shot(win, "dj");
Check(!playerBar.IsVisible, "dj: the player bar went away");
main.IsHome = true;
Shot(win, "bar-back");
Check(playerBar.IsVisible && playerBar.Margin.Bottom == 0 && playerBar.IsHitTestVisible, "dj left: the player bar back in its place");
// ---- 4.4.2: the song's page over the whole screen, Esc and another page end it
var titleBar = win.FindControl<Grid>("TitleBar")!;
main.GoNowPlayingCommand.Execute(null);
// The song above the play button, halfway up the stage, with as much on its left as on its right.
void SongInTheMiddle(string when)
{
    Dispatcher.UIThread.RunJobs();
    var songPage = win.GetVisualDescendants().OfType<NowPlayingView>().First(v => v.IsEffectivelyVisible);
    var songHit = songPage.GetVisualDescendants().OfType<Control>().First(c => c.Name == "CoverHit");
    var songBars = songPage.GetVisualDescendants().OfType<WaveVisualizer>().First();
    var barsStage = songPage.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Stage");
    double barsFrom = songBars.TranslatePoint(default, barsStage)?.X ?? 1;
    Check(barsFrom <= 0 && barsFrom + songBars.Bounds.Width >= barsStage.Bounds.Width, $"song's page, {when}: the bars reach both sides of the stage ({barsFrom:0}, {barsFrom + songBars.Bounds.Width:0} of {barsStage.Bounds.Width:0})");
    var songAt = songHit.TranslatePoint(new Point(songHit.Bounds.Width / 2, songHit.Bounds.Height / 2), win) ?? default;
    var playerGrid = win.FindControl<Control>("PlayerGrid")!;
    var playAt = playerGrid.TranslatePoint(new Point(playerGrid.Bounds.Width / 2, 0), win) ?? default;
    var songStage = songPage.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Stage");
    var stageAt = songStage.TranslatePoint(new Point(0, songStage.Bounds.Height / 2), win) ?? default;
    Check(Math.Abs(songAt.X - playAt.X) < 1 && Math.Abs(songAt.Y - stageAt.Y) < 1, $"song's page, {when}: the cover above play, halfway up the stage ({songAt.X:0},{songAt.Y:0} of {win.Bounds.Width:0}x{win.Bounds.Height:0})");
}
// Every icon of the map is in the fonts the apps carry (a new code: python tools/make-icon-fonts.py).
{
    var mapped = (Dictionary<char, char>)typeof(Icons).GetField("Map_", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    FontManager.Current.TryGetGlyphTypeface(new Typeface(Icons.Regular), out var regularIcons);
    FontManager.Current.TryGetGlyphTypeface(new Typeface(Icons.Filled), out var filledIcons);
    var gone = mapped.Values.Where(c => (regularIcons?.GetGlyph(c) ?? 0) == 0 && (filledIcons?.GetGlyph(c) ?? 0) == 0).ToList();
    Check(mapped.Count > 0 && gone.Count == 0, $"icons: all {mapped.Count} of the map are in the fonts{(gone.Count > 0 ? ", missing " + string.Join(" ", gone.Select(c => ((int)c).ToString("X4"))) : "")}");
}
Shot(win, "song-page");
SongInTheMiddle("with up next");
{
    var songView = win.GetVisualDescendants().OfType<NowPlayingView>().First(v => v.IsEffectivelyVisible);
    var entries = ((Border)win.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Sidebar")).Child!;
    var stageCard = songView.GetVisualDescendants().OfType<Control>().First(c => c.Name == "Stage");
    var upNext = songView.GetVisualDescendants().OfType<Control>().First(c => c.Name == "QueueCard");
    double FromLeft(Control c) => c.TranslatePoint(default, win)?.X ?? -1;
    double[] sideGaps = { FromLeft(entries), FromLeft(stageCard) - FromLeft(entries) - entries.Bounds.Width, FromLeft(upNext) - FromLeft(stageCard) - stageCard.Bounds.Width, win.Bounds.Width - FromLeft(upNext) - upNext.Bounds.Width };
    Check(sideGaps.All(g => Math.Abs(g - 14) < 1) && Math.Abs(entries.Bounds.Width - upNext.Bounds.Width) < 1, $"song's page: the same gaps, the sidebar's entries as wide as up next ({string.Join(", ", sideGaps.Select(g => g.ToString("0")))}; {entries.Bounds.Width:0} and {upNext.Bounds.Width:0})");
}
main.FullScreen = true;
Shot(win, "fullscreen");
SongInTheMiddle("over the whole screen");
Check(main.NowPlaying.FullScreen && win.WindowState == WindowState.FullScreen && !titleBar.IsVisible && playerBar.Background is LinearGradientBrush,
      "full screen: the window over the screen, no title bar, the player bar see-through");
Press(Key.Escape);
Check(!main.FullScreen && win.WindowState != WindowState.FullScreen && titleBar.IsVisible && playerBar.Background is not LinearGradientBrush, "full screen: Esc ends it");
main.FullScreen = true;
main.IsHome = true;
Dispatcher.UIThread.RunJobs();
Check(!main.FullScreen && titleBar.IsVisible, "full screen: another page ends it");

// ---- 4.5: the speed of the animations; the interface's size; the sidebar's sections; the bars of the music; the stream overlays
main.GoSettings();
main.Settings.AnimationSpeed = 2;
Check(Glide.Speed == 2 && main.Settings.AnimationSpeedText == "2x", "animations: 2x reaches the glide");
main.Settings.AnimationSpeed = 1;
void Ctrl(Key key, PhysicalKey physical)
{
    win.KeyPress(key, RawInputModifiers.Control, physical, null);
    win.KeyRelease(key, RawInputModifiers.Control, physical, null);
    Dispatcher.UIThread.RunJobs();
}
main.IsHome = true;
Ctrl(Key.OemMinus, PhysicalKey.Minus);
Check(Math.Abs(Ui.Zoom - 0.9) < 0.01 && Math.Abs(host.Settings.UiZoom - 0.9) < 0.01, "zoom: Ctrl - makes it smaller, saved");
Shot(win, "zoom-90");
Ctrl(Key.D0, PhysicalKey.Digit0);
Check(Ui.Zoom == 1, "zoom: Ctrl 0 back to 100%");
main.SetZoom(1.3, false);
Shot(win, "zoom-130");
var zoomed = Dialogs.Frame("Zoom", new TextBlock { Text = "A dialog at this size" }, 400, Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
zoomed.Show();
Shot(zoomed, "zoom-130-dialog");
zoomed.Close();
main.SetZoom(1, false);
foreach (var side in new[] { main.PlaylistsSection, main.ArtistsSection, main.RadiosSection }) side.ToggleCommand.Execute(null);
Shot(win, "sidebar-folded");
Check(host.Settings.PlaylistsFolded && host.Settings.ArtistsFolded && host.Settings.RadiosFolded && !main.PlaylistsSection.Open, "sidebar: playlists, artists and radios fold, and stay so");
foreach (var side in new[] { main.PlaylistsSection, main.ArtistsSection, main.RadiosSection }) side.ToggleCommand.Execute(null);
Shot(win, "sidebar-open");

main.GoNowPlayingCommand.Execute(null);
Shot(win, "song-page");
var bars = win.GetVisualDescendants().OfType<WaveVisualizer>().FirstOrDefault();
Check(bars is { IsEffectivelyVisible: true } && main.NowPlaying.ShowVisualizer, "bars of the music: behind the song's title");
void Sound()
{
    // (a made-up sound for them: the output is the mixer's, and nothing plays here)
    var block = new float[9600];
    for (int t = 0; t < 40; t++)
    {
        for (int i = 0; i < block.Length; i += 2)
        {
            double at = (t * 4800 + i / 2) / 48000.0;
            block[i] = block[i + 1] = (float)(0.3 * Math.Sin(2 * Math.PI * 70 * at) + 0.15 * Math.Sin(2 * Math.PI * 440 * at) + 0.1 * Math.Sin(2 * Math.PI * 3000 * at));
        }
        Spectrum.Shared.Feed(block, 0, block.Length);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(25);
    }
}
if (bars != null)
{
    bars.IsPlaying = true;
    int baked = TextShadow.Baked;
    Sound();
    win.CaptureRenderedFrame()?.Save(Path.Combine(shots, "bars-center.png"));
    Check(baked > 0 && TextShadow.Baked == baked, "song's title: its shadow is ready, and not made again while the bars move");
    // (a song only shown, nothing plays: the output stays closed)
    var showSong = typeof(PlayerViewModel).GetProperty(nameof(PlayerViewModel.Current))!.GetSetMethod(true)!;
    var look = main.Settings.Appearance;
    showSong.Invoke(main.Player, new object?[] { main.AllVms().First(t => t.Id == "headless-duet") });
    main.Settings.Appearance = main.Settings.Appearances[2];
    Sound();
    win.CaptureRenderedFrame()?.Save(Path.Combine(shots, "title-shadow.png"));
    var title = win.GetVisualDescendants().OfType<TextShadow>().First(s => s.Target?.Name == "TitleBlock").Target!;
    using (var phone = TextShadow.Picture(title, 3, false))
    {
        phone.Save(Path.Combine(shots, "title-shadow-x3.png"));
        // (as it's drawn on such a screen, here at 1:3)
        using var small = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(phone.PixelSize.Width / 3, phone.PixelSize.Height / 3));
        using (var dc = small.CreateDrawingContext()) dc.DrawImage(phone, new Rect(phone.Size), new Rect(phone.Size / 3));
        small.Save(Path.Combine(shots, "title-shadow-x3-small.png"));
        Check(Math.Abs(phone.PixelSize.Width / 3.0 - (title.Bounds.Right + 48)) < 1, "song's title: its shadow at the screen's own pixels, the same size");
    }
    var buttons = win.GetVisualDescendants().OfType<TextShadow>().First(s => s.Target?.Name == "StageButtons").Target!;
    foreach (var scale in new[] { 1.0, 2.0, 3.0 })
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        TextShadow.Picture(title, scale, false).Dispose();
        long letters = clock.ElapsedMilliseconds;
        clock.Restart();
        TextShadow.Picture(buttons, scale, true).Dispose();
        Console.WriteLine($"  shadow pictures x{scale:0}: title {letters} ms, buttons {clock.ElapsedMilliseconds} ms");
    }
    main.Settings.Appearance = look;
    showSong.Invoke(main.Player, new object?[] { null });
    main.Settings.VisualizerFlowChoice = main.Settings.VisualizerFlows[0];
    foreach (var shape in main.Settings.VisualizerStyles)
    {
        main.Settings.VisualizerStyleChoice = shape;
        Sound();
        win.CaptureRenderedFrame()?.Save(Path.Combine(shots, "bars-" + shape.Value + ".png"));
    }
    Check(bars.BarStyle == VisualizerStyle.Wave && bars.Flow == VisualizerFlow.LeftToRight, "bars of the music: their direction and shape follow the settings");
    Check(!main.Settings.VisualizerHasThickness, "bars of the music: no thickness to choose for a filled wave");
    main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
    Check(main.Settings.VisualizerHasThickness, "bars of the music: a thickness for the bars");
    main.Settings.VisualizerFlowChoice = main.Settings.VisualizerFlows[3];
    foreach (var place in main.Settings.VisualizerPlaces)
    {
        main.Settings.VisualizerPlaceChoice = place;
        foreach (var shape in new[] { 0, 4, 5 })
        {
            main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[shape];
            Sound();
            win.CaptureRenderedFrame()?.Save(Path.Combine(shots, $"place-{place.Value}-{main.Settings.VisualizerStyleChoice.Value}.png"));
        }
    }
    Check(bars.Place == VisualizerPlace.Center && bars.Middle > 0 && bars.Middle < bars.Bounds.Height * 0.6, $"bars of the music: in the center, on the middle of the cover ({bars.Middle:0})");
    main.Settings.VisualizerPlaceChoice = main.Settings.VisualizerPlaces[0];
    main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
    foreach (var thickness in main.Settings.VisualizerThicknesses)
    {
        main.Settings.VisualizerThicknessChoice = thickness;
        Sound();
        win.CaptureRenderedFrame()?.Save(Path.Combine(shots, "thickness-" + thickness.Value + ".png"));
    }
    main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[1];
    main.Settings.VisualizerHeight = 1;
    Check(bars.HeightShare == 1, "bars of the music: as high as the whole page");
    // The tallest bars behind everything: what the title and the buttons have to be read over.
    main.FullScreen = true;
    Sound();
    win.CaptureRenderedFrame()?.Save(Path.Combine(shots, "fullscreen-bars.png"));
    main.FullScreen = false;
    Dispatcher.UIThread.RunJobs();
    main.Settings.VisualizerHeight = 0.38;
    bars.IsPlaying = false;
}
main.Settings.Visualizer = false;
Dispatcher.UIThread.RunJobs();
Check(bars is { IsEffectivelyVisible: false }, "bars of the music: switched off in the settings");
main.Settings.Visualizer = true;

var overlay = OverlaySourcesViewModel.Shared.Add();
Check(OverlayServer.Shared.Running && host.Settings.OverlayPort == OverlayServer.Shared.Port, "overlay: the first source starts its server");
using (var http = new HttpClient())
    Check(http.GetAsync(OverlayServer.Shared.LinkOf(overlay.Source)).GetAwaiter().GetResult().IsSuccessStatusCode, "overlay: its link answers");
main.GoSettings();
Shot(win, "settings-page");
var settingsNow = win.GetVisualDescendants().OfType<SettingsView>().First();
settingsNow.FindControl<TextBox>("SearchBox")!.Focus();
win.KeyTextInput("obs");
Shot(win, "settings-overlay");
Check(Shown(L.T("Stream overlay")) && Shown(overlay.Name) && Shown(overlay.Summary), "overlay: Settings finds it by “obs”, its row");
Press(Key.Escape);
settingsNow.FindControl<TextBox>("SearchBox")!.Focus();
win.KeyTextInput(L.T("Bars of the music"));
Shot(win, "settings-bars");
Press(Key.Escape);
var editor = Dialogs.Frame(L.T("Stream overlay"), Dialogs.OverlayEditor(overlay), Dialogs.OverlayEditorWidth(), Dialogs.Button(L.T("Done"), "PrimaryButton", isCancel: true));
editor.Show();
Shot(editor, "overlay-editor");
var pictures = editor.GetVisualDescendants().OfType<Image>().ToList();
Check(pictures.Count(i => i.Source is Avalonia.Media.Imaging.Bitmap { PixelSize.Width: 240 }) >= 16, "overlay: the looks and covers are real pictures");
// The overlay itself, moving, over the options.
if (OverlayPreview.FindBrowser() != null)
{
    for (int i = 0; i < 60 && OverlayPreview.Frames < 8; i++)
    {
        Thread.Sleep(250);
        Dispatcher.UIThread.RunJobs();
    }
    long liveFrames = OverlayPreview.Frames;
    Thread.Sleep(1500);
    Dispatcher.UIThread.RunJobs();
    Check(liveFrames >= 8 && OverlayPreview.Frames > liveFrames && OverlayPreview.Running == 1, $"overlay: the preview moves inside the editor ({(OverlayPreview.Frames - liveFrames) / 1.5:0} pictures a second)");
    Shot(editor, "overlay-live");
}
// Only what makes sense for the look and the cover.
// (a tile hidden from the start has no template yet: its words aren't on screen, whatever they say of themselves)
bool InEditor(string text) => editor.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible && t.GetVisualRoot() != null);
RadioButton Tile(string text) => editor.GetLogicalDescendants().OfType<RadioButton>().First(r => r.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == text));
Tile(L.T("Vinyl")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(InEditor(L.T("The needle on the record")) && InEditor(L.T("Light around the cover")), "overlay: the needle with the vinyl");
Tile(L.T("Square")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(!InEditor(L.T("The needle on the record")) && InEditor(L.T("Light around the cover")), "overlay: no needle without it");
Tile(L.T("Neon")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(!InEditor(L.T("Card opacity")) && !InEditor(L.T("Corners")), "overlay: a look without cards has no opacity or corners");
Tile(L.T("Cassette")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(!InEditor(L.T("Canvas")) && !InEditor(L.T("Light around the cover")), "overlay: the cassette has its cover on its label: no canvas, no light");
Tile(L.T("Small bars")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(overlay.Source is { Bars: true, ShowBar: false } && InEditor(L.T("Shape of the bars")), "overlay: small bars alone, in place of the line");
Tile(L.T("Progress line")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(overlay.Source is { Bars: false, ShowBar: true } && !InEditor(L.T("Shape of the bars")), "overlay: the line alone, and no shape of bars to choose");
Tile(L.T("Small bars")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(!InEditor(L.T("Above and below")), "overlay: no place for the big bars while they're off");
var behindSwitch = editor.GetLogicalDescendants().OfType<CheckBox>().First(c => c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("Big bars behind the card")));
behindSwitch.IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(InEditor(L.T("Above and below")), "overlay: their places when they're on");
Tile(L.T("Through the middle")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(overlay.Source.BarsPlace == VisualizerPlace.Center, "overlay: the big bars through the middle");
behindSwitch.IsChecked = false;
Tile(L.T("Compact")).IsChecked = true;
Dispatcher.UIThread.RunJobs();
// Each font in its own face, and only the ones in view made.
var fontCombo = editor.GetLogicalDescendants().OfType<ComboBox>().Last();
var opening = System.Diagnostics.Stopwatch.StartNew();
fontCombo.IsDropDownOpen = true;
Dispatcher.UIThread.RunJobs();
long openMs = opening.ElapsedMilliseconds;
var fontPopup = fontCombo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First();
Shot(editor, "overlay-fonts");
var madeFonts = ((Visual)fontPopup.Host!).GetVisualDescendants().OfType<ComboBoxItem>().ToList();
var inOwnFace = madeFonts.Skip(1).Select(i => i.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()).Where(t => t != null).ToList();
Check(fontCombo.ItemCount > 2 && madeFonts.Count < 40 && inOwnFace.Count > 0 && inOwnFace.All(t => t!.FontFamily.Name == t.Text), $"overlay: {fontCombo.ItemCount} fonts, {madeFonts.Count} made, each in its face (opened in {openMs} ms)");
fontCombo.IsDropDownOpen = false;
// Size, place and when: they reach the source, and only the line that counts stays.
editor.GetLogicalDescendants().OfType<Slider>().First().Value = 0.6;
editor.GetLogicalDescendants().OfType<RadioButton>().Where(r => r.GroupName == "overlay-place").ElementAt(6).IsChecked = true;
editor.GetLogicalDescendants().OfType<RadioButton>().First(r => r.GroupName == "overlay-when" && TextOf(r).Contains(L.T("At every new song"))).IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(overlay.Source is { Size: 0.6, AnchorX: 0, AnchorY: 1, Show: "change" } && File.ReadAllText(AppPaths.SettingsFile).Contains("\"change\""), "overlay: size, place and when are saved");
Check(editor.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("It stays for") && t.IsEffectivelyVisible) && !editor.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == L.T("With the music paused, hide it") && t.IsEffectivelyVisible), "overlay: only the line of the choice shows");
overlay.Source.Show = "playing";
overlay.Source.Size = 1;
var minimalTile = editor.GetLogicalDescendants().OfType<RadioButton>().First(r => TextOf(r).Contains(L.T("Minimal")));
minimalTile.IsChecked = true;
Dispatcher.UIThread.RunJobs();
Check(overlay.Source.Skin == "minimal" && overlay.Summary.StartsWith(L.T("Minimal")) && File.ReadAllText(AppPaths.SettingsFile).Contains("\"minimal\""), "overlay: a look picked is saved at once");
var editorScroll = editor.GetVisualDescendants().OfType<ScrollViewer>().First();
foreach (var (at, part) in new[] { (620, 2), (1240, 3), (1860, 4), (2480, 5) })
{
    editorScroll.Offset = new Vector(0, at);
    Shot(editor, "overlay-editor-" + part);
}
editor.Close();
for (int i = 0; i < 20 && OverlayPreview.Running > 0; i++)
{
    Thread.Sleep(150);
    Dispatcher.UIThread.RunJobs();
}
Check(OverlayPreview.Running == 0, "overlay: its browser goes with the editor");

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

// Frames of the real window, with the bars and without.
void Perf()
{
    var wav = Path.Combine(root, "music", "perf.wav");
    Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
    WriteMusic(wav, 300);
    var song = new Track { Id = "perf", Title = "A song with a title", Artist = "An artist", Duration = 300, SourceUrl = "https://example.com/perf", Path = wav, HasCover = true };
    WriteCover(AppPaths.TrackCover(song.Id));
    main.Library.Add(song);
    Wait(300);
    main.Player.Volume = 0;
    win.Topmost = true;
    main.Player.PlayFrom(main.LibraryPage, main.AllVms().First(t => t.Id == song.Id));
    main.GoNowPlayingCommand.Execute(null);
    Wait(1500);
    Console.WriteLine($"  window {win.Bounds.Size}, render scale {win.RenderScaling}, playing {main.Player.IsPlaying}, animate {main.Player.Animate}");
    Measure("bars on, window");
    main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[0];
    Wait(600);
    Measure("thin, window", 4000);
    main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[1];
    Wait(600);
    main.Settings.Visualizer = false;
    Wait(800);
    Measure("bars off, window");
    main.Settings.Visualizer = true;
    win.WindowState = WindowState.Maximized;
    Wait(1500);
    Console.WriteLine($"  maximized {win.Bounds.Size}");
    Measure("bars on, maximized");
    main.Settings.Visualizer = false;
    Wait(800);
    Measure("bars off, maximized");
    main.Settings.Visualizer = true;
    Wait(600);
    // The tallest bars behind everything: what the buttons have to be read over.
    var usual = main.Settings.VisualizerHeight;
    main.Settings.VisualizerHeight = 1;
    main.FullScreen = true;
    Wait(1500);
    Measure("full screen, the tallest bars");
    main.FullScreen = false;
    main.Settings.VisualizerHeight = usual;
    Wait(800);
    foreach (var place in main.Settings.VisualizerPlaces)
    {
        main.Settings.VisualizerPlaceChoice = place;
        Wait(600);
        Measure(place.Label + ", maximized", 4000);
    }
    main.Settings.VisualizerPlaceChoice = main.Settings.VisualizerPlaces[0];
    foreach (var thickness in main.Settings.VisualizerThicknesses)
    {
        main.Settings.VisualizerThicknessChoice = thickness;
        Wait(600);
        Measure(thickness.Label + ", maximized", 4000);
    }
    foreach (var style in main.Settings.VisualizerStyles)
    {
        main.Settings.VisualizerStyleChoice = style;
        Wait(600);
        Measure(style.Label + ", maximized", 4000);
    }
    main.Settings.VisualizerStyleChoice = main.Settings.VisualizerStyles[0];
    // Thin and tall: the most there is to draw.
    main.Settings.VisualizerThicknessChoice = main.Settings.VisualizerThicknesses[0];
    main.Settings.VisualizerHeight = 0.8;
    Wait(600);
    Measure("thin and tall, maximized");
    main.GoSettings();
    Wait(900);
    var box = win.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox" && t.IsEffectivelyVisible);
    box.Text = L.T("Bars of the music");
    Wait(900);
    Measure("settings, the preview in sight");
    box.Text = "";
    Wait(500);
    foreach (var scroller in win.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible && s.Extent.Height - s.Viewport.Height > 500)) scroller.ScrollToEnd();
    Wait(900);
    Measure("settings, the preview scrolled away");
    win.Topmost = false;
    main.GoNowPlayingCommand.Execute(null);
    win.WindowState = WindowState.Minimized;
    Wait(1500);
    Measure("minimized, playing", 6000, false);
    main.Player.PlayPauseCommand.Execute(null);
    Wait(1500);
    Measure("minimized, paused", 6000, false);
    win.WindowState = WindowState.Maximized;
    Wait(1500);
    Measure("on screen, paused", 6000, false);
}

void Wait(int ms)
{
    using var done = new CancellationTokenSource(ms);
    Dispatcher.UIThread.MainLoop(done.Token);
}

void Measure(string what, int ms = 6000, bool ask = true)
{
    long drawn = WaveVisualizer.Drawn;
    int ticks = 0;
    bool on = true;
    void Tick(TimeSpan _)
    {
        ticks++;
        if (on) win.RequestAnimationFrame(Tick);
    }
    if (ask) win.RequestAnimationFrame(Tick);
    var me = System.Diagnostics.Process.GetCurrentProcess();
    var cpu = me.TotalProcessorTime;
    var clock = System.Diagnostics.Stopwatch.StartNew();
    Wait(ms);
    on = false;
    me.Refresh();
    double seconds = clock.Elapsed.TotalSeconds;
    Console.WriteLine($"  {what}: {ticks / seconds:0.0} fps, bars drawn {(WaveVisualizer.Drawn - drawn) / seconds:0.0} a second, CPU {(me.TotalProcessorTime - cpu).TotalSeconds / seconds * 100:0}% of a core");
}

// Something for the bars to follow: chords, a beat and a hiss that come and go.
static void WriteMusic(string file, int seconds)
{
    const int rate = 44100;
    int frames = rate * seconds;
    using var w = new BinaryWriter(new BufferedStream(File.Create(file), 1 << 20));
    w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
    w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(frames * 4);
    var random = new Random(7);
    for (int i = 0; i < frames; i++)
    {
        double t = (double)i / rate, beat = t * 2 % 1, chord = Math.Floor(t / 2) % 4;
        double kick = Math.Exp(-beat * 9) * Math.Sin(2 * Math.PI * 62 * t);
        double pad = 0;
        foreach (var note in new[] { 220 + chord * 30, 330 + chord * 45, 660 + chord * 90, 1760 + chord * 120 }) pad += 0.1 * Math.Sin(2 * Math.PI * note * t);
        double hiss = Math.Exp(-((beat + 0.5) % 1) * 14) * (random.NextDouble() - 0.5);
        short s = (short)(Math.Clamp(0.5 * kick + 0.5 * pad + 0.3 * hiss, -1, 1) * 30000);
        w.Write(s); w.Write(s);
    }
}

static void WriteCover(string file)
{
    using var bmp = new SkiaSharp.SKBitmap(1000, 1000);
    using (var c = new SkiaSharp.SKCanvas(bmp))
    {
        using var paint = new SkiaSharp.SKPaint { Shader = SkiaSharp.SKShader.CreateLinearGradient(new SkiaSharp.SKPoint(0, 0), new SkiaSharp.SKPoint(1000, 1000), new[] { new SkiaSharp.SKColor(0x2B, 0x10, 0x70), new SkiaSharp.SKColor(0xF0, 0x40, 0x80) }, SkiaSharp.SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, 1000, 1000, paint);
    }
    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
    using var img = SkiaSharp.SKImage.FromBitmap(bmp);
    File.WriteAllBytes(file, img.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray());
}

// Lets the queued work, the layout and a frame happen, then saves what's drawn.
void Shot(TopLevel top, string name)
{
    // (past the pages' slide)
    for (int i = 0; i < 30; i++)
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
