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
    ("search", "search", () => main.Navigate(main.Search)),
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
export.Close();

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
    var physical = key == Key.Space ? PhysicalKey.Space : PhysicalKey.Escape;
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
