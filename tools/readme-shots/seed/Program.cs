using System.Text.RegularExpressions;
using UltimateMP3Player.Core;

// Demo data for the README's screenshots: profile "Alex", playlists, tags, favourites, plays, history, over the demo library
// (tools/readme-shots/demo.ps1). Once, on a new folder: a second run adds the plays again.
var root = Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("UMP_DATA", Path.Combine(root, "data"));
var lib = Library.Load();
var songs = lib.Snapshot();
Track S(string title) => songs.First(t => t.Title.StartsWith(title, StringComparison.OrdinalIgnoreCase));
IEnumerable<string> Ids(params string[] titles) => titles.Select(t => S(t).Id);

foreach (var t in songs.Where(t => t.Album != null && Regex.IsMatch(t.Album, "sped|slowed", RegexOptions.IgnoreCase)))
{
    t.Album = t.Title;
    lib.Changed(t);
}
// Deezer names the main artist only: the guests as on the songs' covers.
var guests = new Dictionary<string, string>
{
    ["On & On"] = "Cartoon, Jéja, Daniel Levi", ["Why We Lose"] = "Cartoon, Jéja, Coleman Trapp", ["Savannah"] = "Diviners, Philly K",
    ["Mortals"] = "Warriyo, Laura Brehm", ["Dreams pt. II"] = "Lost Sky, Sara Skinner", ["Superhero"] = "Unknown Brain, Chris Linton",
    ["Fearless"] = "Lost Sky, Chris Linton", ["Make Me Move"] = "Culture Code, Karra", ["Royalty"] = "Egzod, Maestro Chives, Neoni",
    ["Light It Up"] = "Robin Hustin, TobiMorrow, Jex", ["Where We Started"] = "Lost Sky, Jex",
};
foreach (var (title, artist) in guests)
{
    var t = S(title);
    t.Artist = artist;
    lib.Changed(t);
}
lib.SaveNow();

var list = ProfileList.Load();
var info = list.Profiles.FirstOrDefault(p => p.Name == "Alex");
if (info == null)
{
    info = new ProfileInfo { Name = "Alex", Color = "#7C5CFF" };
    list.Profiles.Add(info);
    list.Save();
}

var s = AppSettings.Load();
s.Language = "en";
s.TermsAccepted = 1;
s.DiscordPresence = false;
s.AutoUpdateApp = s.AutoUpdateEngines = false;
s.ShowWhatsNew = false;
s.BackgroundHintSeen = true;
s.StartupProfile = info.Id;
s.MusicDir = Path.Combine(root, "music");
// The songs are saved MP3s: with the library in the cloud (the default) each would carry the "saved on the device" mark.
s.SaveAudio = true;
// 1280 × 800 of layout in 1600 × 1000 pixels: sharper pictures on a screen at 100%.
s.UiZoom = 1.25;
s.WindowWidth = 1600;
s.WindowHeight = 1000;
s.WindowMaximized = false;
s.Save();

var p = Profile.Get(info);
p.Data.Appearance = ThemeColors.Dark;
foreach (var pl in p.PlaylistsSnapshot().Where(pl => !pl.IsFavorites)) p.DeletePlaylist(pl);
foreach (var tag in p.TagsSnapshot()) p.DeleteTag(tag);

var road = p.CreatePlaylist("Road Trip");
p.AddTracks(road, Ids("On & On", "Savannah", "Superhero", "Sky High", "Why We Lose", "Feel Good", "Light It Up", "Make Me Move", "Summertime"));
var gym = p.CreatePlaylist("Gym Mix");
p.AddTracks(gym, Ids("Invincible", "Mortals", "Blank", "Link", "Energy", "Limitless", "Royalty", "Firefly", "Arrow", "Nekozilla"));
var chill = p.CreatePlaylist("Chill Evening");
p.AddTracks(chill, Ids("Dreams pt. II", "Fearless", "Vision pt. II", "Where We Started", "My Heart", "Clear My Head", "Candyland", "Symbolism"));
p.AddTracks(p.Favorites, Ids("On & On", "Superhero", "Sky High", "Mortals", "Dreams pt. II", "Royalty", "Fearless"));

var energetic = p.CreateTag("Energetic", "#E5484D");
p.SetTag(Ids("Invincible", "Mortals", "Blank", "Link", "Energy", "Limitless", "Royalty", "Firefly", "Arrow", "Nekozilla"), energetic.Id, true);
var vocals = p.CreateTag("Vocals", "#1E9BFF");
p.SetTag(Ids("On & On", "Why We Lose", "Mortals", "Dreams pt. II", "Fearless", "Superhero", "Make Me Move", "Royalty", "My Heart", "Where We Started", "Light It Up", "Vision pt. II"), vocals.Id, true);
var chillTag = p.CreateTag("Chill", "#12B5A5");
p.SetTag(Ids("Dreams pt. II", "Vision pt. II", "Where We Started", "My Heart", "Clear My Head", "Symbolism"), chillTag.Id, true);
var summer = p.CreateTag("Summer", "#F5B400");
p.SetTag(Ids("Savannah", "Summertime", "Candyland", "Feel Good", "Sky High"), summer.Id, true);

// Plays: the favourites and the road trip most, the rest a few times each (Home's "For you" comes from them).
var random = new Random(7);
foreach (var t in songs)
{
    int plays = p.Favorites.Tracks.Contains(t.Id) ? 18 + random.Next(12) : road.Tracks.Contains(t.Id) ? 8 + random.Next(8) : 2 + random.Next(6);
    for (int i = 0; i < plays; i++) p.AddListening(t.Id, t.Duration, true, save: false);
}
// History: the last one played first on Home.
foreach (var title in new[] { "Clear My Head", "Link", "Sky High", "Superhero", "Fearless", "Mortals", "Feel Good", "Blank", "Savannah", "On & On" })
    p.AddHistory(S(title).Id);
p.Data.LastTrack = S("On & On").Id;
// Opened paused on "On & On" at 1:03, playing from Road Trip: nothing heard while the pictures are taken.
p.Data.Queue = new QueueState { Context = road.Tracks.ToList(), ContextId = "playlist:" + road.Id, ContextName = "Road Trip", Current = S("On & On").Id, Position = 63 };
p.Save();
p.Flush();
Console.WriteLine($"seed: {songs.Count} songs, profile {info.Id}");
