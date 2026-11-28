using System.Net;
using System.Text.Json;

namespace UltimateMP3Player.Core;

// An internet radio (Library › Radio), as radio-browser.info lists it; the saved ones are in the profile (ProfileData.Stations).
public sealed class Station
{
    // radio-browser's stationuuid.
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    // The stream itself (its playlist already followed by radio-browser).
    public string Url { get; set; } = "";
    public string? Favicon { get; set; }
    public string? Homepage { get; set; }
    // "rock,indie,90s"
    public string? Tags { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? Codec { get; set; }
    public int Bitrate { get; set; }

    // Its first tags and its country: what it plays and from where.
    public string Subtitle
    {
        get
        {
            var tags = (Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length is > 1 and < 24).Take(2).Select(t => char.ToUpperInvariant(t[0]) + t[1..]);
            return string.Join(" · ", tags.Append(Country).Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public string Quality => (Codec ?? "") + (Bitrate > 0 ? $" {Bitrate} kbps" : "");

    // As the player knows it: a song without an end ("st-" + its id), its cover the station's picture once fetched.
    public const string Prefix = "st-";
    public const string Site = "Radio";
    public static bool Is(string? trackId) => trackId != null && trackId.StartsWith(Prefix, StringComparison.Ordinal);

    public Track ToTrack() => new() { Id = Prefix + Id, Title = Name, Artist = Subtitle, Duration = 0, SourceUrl = Url, Site = Site, ArtUrl = Favicon };
}

// radio-browser.info: the open, free directory of internet radios (no key). Its servers by name, the first that answers kept.
public static class RadioBrowser
{
    // Its servers as radio-browser asks: all.api.radio-browser.info looked up, each address named by its reverse lookup (HTTPS
    // needs the name), in a random order, then these known ones (2026-11: the lookup gave de1 only, de2 answered, fi1/at1/nl1 were
    // gone); the known ones alone while the lookup is slow or finds nothing (offline: asked again next time).
    private static readonly string[] Known = { "de1.api.radio-browser.info", "de2.api.radio-browser.info" };
    private static Task<string[]>? _lookup;
    private static string[]? _servers;
    private static int _server;
    private const string Agent = "UltimateMP3Player/4.3";
    private const int Limit = 60;

    // The most listened to: everywhere, in a country (its two letters), of a genre (a tag), or by name; mp3Only for the iPhone
    // (it decodes MP3 streams only). Stations that radio-browser found broken stay out.
    public static Task<List<Station>> SearchAsync(string? name, string? countryCode, string? tag, bool mp3Only, CancellationToken ct)
    {
        var q = $"stations/search?limit={Limit}&hidebroken=true&order=clickcount&reverse=true";
        if (!string.IsNullOrWhiteSpace(name)) q += "&name=" + Uri.EscapeDataString(name.Trim());
        if (!string.IsNullOrWhiteSpace(countryCode)) q += "&countrycode=" + Uri.EscapeDataString(countryCode);
        if (!string.IsNullOrWhiteSpace(tag)) q += "&tag=" + Uri.EscapeDataString(tag) + "&tagExact=true";
        if (mp3Only) q += "&codec=MP3";
        return GetAsync(q, ct);
    }

    // A station played: radio-browser counts it (its "most listened to" are these counts). Nothing waits for it.
    public static void Clicked(string id) => _ = Task.Run(async () =>
    {
        try { await CallAsync("url/" + Uri.EscapeDataString(id), CancellationToken.None); } catch { }
    });

    private static async Task<List<Station>> GetAsync(string query, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await CallAsync(query, ct));
        var list = new List<Station>();
        var seen = new HashSet<string>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var s = Parse(e);
            // (the same stream listed twice under two names: once)
            if (s != null && seen.Add(s.Url)) list.Add(s);
        }
        return list;
    }

    public static Station? Parse(JsonElement e)
    {
        string? S(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
        var url = S("url_resolved") ?? S("url");
        var id = S("stationuuid");
        var name = S("name");
        if (url == null || id == null || name == null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;
        return new Station
        {
            Id = id, Name = name, Url = url, Favicon = S("favicon") is { } f && f.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? f : null,
            Homepage = S("homepage"), Tags = S("tags"), Country = S("country"), CountryCode = S("countrycode"), Codec = S("codec"),
            Bitrate = e.TryGetProperty("bitrate", out var b) && b.TryGetInt32(out var kbps) ? kbps : 0,
        };
    }

    public static async Task<string[]> ServersAsync(CancellationToken ct)
    {
        if (_servers != null) return _servers;
        var lookup = _lookup ??= FindServersAsync();
        // (a slow DNS never holds the radio page up)
        await Task.WhenAny(lookup, Task.Delay(3000, ct));
        if (!lookup.IsCompleted) return Known;
        _lookup = null;
        if (lookup.Result.Length == 0) return Known;
        return _servers = lookup.Result.Concat(Known).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static async Task<string[]> FindServersAsync()
    {
        try
        {
            var ips = await Dns.GetHostAddressesAsync("all.api.radio-browser.info");
            var names = await Task.WhenAll(ips.Select(async ip =>
            {
                try { return (await Dns.GetHostEntryAsync(ip)).HostName.TrimEnd('.'); } catch { return ""; }
            }));
            var found = names.Where(n => n.EndsWith(".radio-browser.info", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            Random.Shared.Shuffle(found);
            return found;
        }
        catch { return Array.Empty<string>(); }
    }

    private static async Task<string> CallAsync(string query, CancellationToken ct)
    {
        Exception? last = null;
        var servers = await ServersAsync(ct);
        for (int i = 0; i < servers.Length; i++)
        {
            int n = (_server + i) % servers.Length;
            try
            {
                var text = await Http.GetStringAsync($"https://{servers[n]}/json/{query}", Agent, ct: ct);
                _server = n;
                return text;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("radio-browser.info");
    }
}
