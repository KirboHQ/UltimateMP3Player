using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core.Mods;

// A mod's mod.json. Written by people outside the app: every field is checked before anything of the mod is used.
public sealed class ModManifest
{
    public const string FileName = "mod.json";
    // The newest mod API this app understands. Additions keep the number; a change that breaks mods would raise it.
    public const int ApiVersion = 1;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Homepage { get; set; }
    public int Api { get; set; } = 1;
    public string Main { get; set; } = "main.js";
    public List<string> Permissions { get; set; } = new();
    // Folders it may read without asking (with "files"): where another program keeps its things on each system ("~/…",
    // "%APPDATA%/…"), shown before it's installed. ump.files.known(id) gives the one there.
    public List<ModPlace> Places { get; set; } = new();
    // Sites it may download from (permission "internet"), shown before it's installed: "osu.ppy.sh", "catboy.best".
    public List<string> Hosts { get; set; } = new();
    public ModContributes Contributes { get; set; } = new();
    // The mod's own translations: language ("it") → English text → text in that language.
    public Dictionary<string, Dictionary<string, string>> Strings { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
    private static readonly Regex IdShape = new(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant);

    // Reads and checks a mod.json; throws ModException with a sentence for the user when it can't be used.
    public static ModManifest Parse(string json)
    {
        ModManifest? m;
        try { m = JsonSerializer.Deserialize<ModManifest>(json, Options); }
        catch (JsonException ex) { throw new ModException(L.F("Its mod.json can't be read: {0}", ex.Message)); }
        if (m == null) throw new ModException(L.T("Its mod.json is empty."));
        m.Id = m.Id.Trim();
        m.Name = m.Name.Trim();
        m.Version = m.Version.Trim();
        if (m.Id.Length > 64 || !IdShape.IsMatch(m.Id))
            throw new ModException(L.T("Its id must look like author.name (lower case letters, digits, dashes)."));
        if (m.Name.Length is 0 or > 60) throw new ModException(L.T("Its name is missing or too long."));
        if (m.Version.Length is 0 or > 32) throw new ModException(L.T("Its version is missing."));
        if (m.Api > ApiVersion) throw new ModException(L.T("It needs a newer version of the app."));
        if (m.Api < 1) throw new ModException(L.T("Its api number is wrong."));
        if (!ModPackage.IsSafePath(m.Main) || !m.Main.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            throw new ModException(L.T("Its main script must be a .js file inside the mod."));
        if (m.Icon != null && !ModPackage.IsSafePath(m.Icon)) m.Icon = null;
        if (m.Permissions.FirstOrDefault(p => !ModPermissions.Known.Contains(p)) is { } unknown)
            throw new ModException(L.F("It asks for “{0}”, which needs a newer version of the app.", unknown));
        m.Places = m.Permissions.Contains(ModPermissions.Files)
            ? m.Places.Where(p => p.Id.Length is > 0 and <= 40 && p.Title.Length is > 0 and <= 60).Take(8)
                .Select(p => new ModPlace { Id = p.Id, Title = p.Title, Paths = p.Paths.Where(x => x.Length is > 1 and <= 260).Take(12).ToList() }).ToList()
            : new();
        m.Hosts = m.Permissions.Contains(ModPermissions.Internet)
            ? m.Hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns && h.Contains('.')).Distinct().Take(20).ToList()
            : new();
        if (m.Permissions.Contains(ModPermissions.Internet) && m.Hosts.Count == 0) throw new ModException(L.T("It asks for the internet without saying which sites."));
        m.Contributes.Looks.RemoveAll(p => !ModPackage.IsSafePath(p) || !p.EndsWith(LookPreset.Extension, StringComparison.OrdinalIgnoreCase));
        m.Author = Short(m.Author, 60);
        m.Description = Short(m.Description, 600);
        if (m.Homepage != null && !m.Homepage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) m.Homepage = null;
        return m;
    }

    public static ModManifest Read(string folder) =>
        Parse(File.ReadAllText(Path.Combine(folder, FileName)));

    private static string? Short(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    // The mod's text in the app's language: its own translation, else the English it wrote.
    public string Translate(string text) =>
        !L.English && Strings.TryGetValue("it", out var it) && it.TryGetValue(text, out var t) ? t : text;

    // An update that asks for something the user wasn't asked about: a permission, a site or a folder read without asking
    // (a subdomain of a site already granted counts as new too). Without any, the app updates it by itself.
    public bool AsksMoreThan(ModManifest old, IEnumerable<string> granted) =>
        Permissions.Except(granted).Any() || Hosts.Except(old.Hosts).Any()
        || Places.SelectMany(p => p.Paths).Except(old.Places.SelectMany(p => p.Paths), StringComparer.OrdinalIgnoreCase).Any();
}

public sealed class ModPlace
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

public sealed class ModContributes
{
    // Looks (.umplook files inside the mod) the user can add to a profile.
    public List<string> Looks { get; set; } = new();
}

// What a mod may do beyond its own folder, asked for in mod.json and shown before it's installed.
public static class ModPermissions
{
    public const string LibraryRead = "library.read";
    public const string PlayerControl = "player.control";
    public const string Files = "files";
    public const string LibraryWrite = "library.write";
    public const string Internet = "internet";
    public static readonly string[] Known = { LibraryRead, PlayerControl, Files, LibraryWrite, Internet };

    public static string Describe(string p) => p switch
    {
        LibraryRead => L.T("See your songs and their details"),
        PlayerControl => L.T("Play, pause and change song"),
        Files => L.T("Open the files and folders you choose for it"),
        LibraryWrite => L.T("Add songs and playlists to your library"),
        Internet => L.T("Download from the sites it names"),
        _ => p,
    };

    // The list shown before installing (and in its page): each permission, with the folders and sites it names.
    public static IEnumerable<string> Lines(ModManifest m, IEnumerable<string> granted)
    {
        foreach (var p in granted)
        {
            yield return ModPermissions.Describe(p);
            if (p == Files && m.Places.Count > 0) yield return L.F("Read without asking: {0}", string.Join(", ", m.Places.Select(x => m.Translate(x.Title))));
            if (p == Internet) yield return L.F("Sites: {0}", string.Join(", ", m.Hosts));
        }
    }
}

public sealed class ModException(string message) : Exception(message);
