using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core.Mods;

// A mod anyone can install from inside the app (Settings › Mods › Get mods): the app's author's own ("official") and the ones they
// checked ("certified"), listed in catalogue.json of KirboHQ/UltimateMP3Player-Mods. Its package's address and SHA-256: what's
// downloaded is installed only if it matches, after the same question as a file (what it is, what it asks for).
public sealed class ModCatalogueEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Author { get; set; }
    public string? Description { get; set; }
    // Its description in other languages: "it" → text.
    public Dictionary<string, string> Descriptions { get; set; } = new();
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public bool Official { get; set; }
    // "windows", "macos", "linux", "android", "ios"; none: all.
    public List<string> Platforms { get; set; } = new();
    // The app's version it needs at least ("6.0.0").
    public string? MinApp { get; set; }

    public string Text => !L.English && Descriptions.TryGetValue("it", out var it) ? it : Description ?? "";
}

public static class ModCatalogue
{
    public const string Address = "https://raw.githubusercontent.com/KirboHQ/UltimateMP3Player-Mods/main/catalogue.json";
    private static readonly Regex IdShape = new(@"^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private sealed class Root
    {
        public List<ModCatalogueEntry> Mods { get; set; } = new();
    }

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "mods-catalogue.json");

    public static string Platform => OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsIOS() ? "ios" : OperatingSystem.IsMacOS() ? "macos"
        : OperatingSystem.IsWindows() ? "windows" : "linux";

    // The list from GitHub (kept for when there's no connection: the last one read), the ones for this device and this version.
    public static async Task<List<ModCatalogueEntry>> LoadAsync(Version app, CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(Address, ct: ct);
            try { File.WriteAllText(CacheFile, json); } catch (Exception) { }
        }
        catch (Exception) when (!ct.IsCancellationRequested && File.Exists(CacheFile))
        {
            json = File.ReadAllText(CacheFile);
        }
        return Parse(json, app);
    }

    public static List<ModCatalogueEntry> Parse(string json, Version app)
    {
        var root = JsonSerializer.Deserialize<Root>(json, Options) ?? new Root();
        return root.Mods.Where(m =>
                IdShape.IsMatch(m.Id) && m.Name.Trim().Length is > 0 and <= 60 && m.Version.Length is > 0 and <= 32 &&
                Uri.TryCreate(m.Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
                m.Sha256.Length == 64 && m.Sha256.All(Uri.IsHexDigit) &&
                (m.Platforms.Count == 0 || m.Platforms.Contains(Platform, StringComparer.OrdinalIgnoreCase)) &&
                (m.MinApp == null || !Version.TryParse(m.MinApp, out var need) || app >= need))
            .GroupBy(m => m.Id).Select(g => g.First())
            .OrderByDescending(m => m.Official).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Its package into dir, checked against the list's SHA-256 (a different file is deleted, never installed).
    public static async Task<string> DownloadAsync(ModCatalogueEntry m, string dir, Action<long, long?>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, m.Id + ModPackage.Extension);
        await Http.DownloadFileAsync(m.Url, file, null, progress, ct);
        string hash;
        await using (var s = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
        if (!hash.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(file); } catch (Exception) { }
            throw new ModException(L.T("The mod downloaded isn't the one in the list: it wasn't installed."));
        }
        return file;
    }

    // Newer than what's installed ("1.2.0" > "1.1.9"; versions that aren't numbers: different is newer).
    public static bool IsNewer(string listed, string installed) =>
        Version.TryParse(listed, out var a) && Version.TryParse(installed, out var b) ? a > b : !string.Equals(listed, installed, StringComparison.Ordinal);
}
