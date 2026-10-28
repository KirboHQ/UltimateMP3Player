using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UltimateMP3Player.Core;

// A play to send: what was heard and when it started.
public sealed class Scrobble
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Album { get; set; }
    public int Duration { get; set; }
    // When it started (Unix seconds).
    public long At { get; set; }
    // The services still waiting for it (Scrobbler.LastFmName, Scrobbler.ListenBrainzName).
    public List<string> Left { get; set; } = new();

    public static Scrobble Of(Track t, long at) => new()
    {
        Artist = t.Artist?.Trim() ?? "", Title = t.Title.Trim(),
        Album = string.IsNullOrWhiteSpace(t.Album) ? null : t.Album.Trim(), Duration = (int)Math.Round(t.Duration), At = at,
    };
}

// The service said no (a session or token no longer valid, a key refused): the plays wait, the account has to be connected again.
public sealed class ScrobbleRefused(string message) : Exception(message);

// Last.fm's API: signed calls (md5 of the sorted parameters and the secret), the user's own key; the account is connected
// on Last.fm's page (auth.getToken → the page → auth.getSession), no password goes through the app.
public static class LastFm
{
    private const string Api = "https://ws.audioscrobbler.com/2.0/";
    public const string KeysPage = "https://www.last.fm/api/account/create";

    public static string AuthPage(string key, string token) => $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(key)}&token={Uri.EscapeDataString(token)}";

    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string secret)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal)) sb.Append(k).Append(v);
        sb.Append(secret);
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    private static async Task<JsonNode> CallAsync(string key, string secret, Dictionary<string, string> p, bool post, CancellationToken ct)
    {
        p["api_key"] = key;
        p["api_sig"] = Sign(p, secret);
        // (not signed)
        p["format"] = "json";
        using var req = post
            ? new HttpRequestMessage(HttpMethod.Post, Api) { Content = new FormUrlEncodedContent(p) }
            : new HttpRequestMessage(HttpMethod.Get, Api + "?" + string.Join("&", p.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value))));
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); }
        catch { }
        // 4, 9, 10, 14, 15, 26: the token, the session or the key isn't valid (any more); 11, 16, 29: try again later.
        if (json?["error"] is { } e)
        {
            int code = e.GetValue<int>();
            var message = "Last.fm: " + (json["message"]?.GetValue<string>() ?? code.ToString());
            if (code is 4 or 9 or 10 or 14 or 15 or 26) throw new ScrobbleRefused(message);
            throw new IOException(message);
        }
        resp.EnsureSuccessStatusCode();
        return json ?? throw new IOException("Last.fm: no answer");
    }

    public static async Task<string> TokenAsync(string key, string secret, CancellationToken ct)
        => (await CallAsync(key, secret, new() { ["method"] = "auth.getToken" }, false, ct))["token"]!.GetValue<string>();

    // The user allowed the app on the page: their name and the session (it doesn't expire).
    public static async Task<(string Name, string Session)> SessionAsync(string key, string secret, string token, CancellationToken ct)
    {
        var s = (await CallAsync(key, secret, new() { ["method"] = "auth.getSession", ["token"] = token }, false, ct))["session"]!;
        return (s["name"]!.GetValue<string>(), s["key"]!.GetValue<string>());
    }

    public static Task NowPlayingAsync(string key, string secret, string session, Scrobble s, CancellationToken ct)
    {
        var p = new Dictionary<string, string> { ["method"] = "track.updateNowPlaying", ["sk"] = session, ["artist"] = s.Artist, ["track"] = s.Title };
        if (s.Album != null) p["album"] = s.Album;
        if (s.Duration > 0) p["duration"] = s.Duration.ToString();
        return CallAsync(key, secret, p, true, ct);
    }

    // The user's loved songs (title, artist), the latest first. Read-only: no session needed.
    public static async Task<List<(string Title, string Artist)>> LovedAsync(string key, string secret, string user, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        for (int page = 1, pages = 1; page <= pages && page <= 100; page++)
        {
            var j = (await CallAsync(key, secret, new() { ["method"] = "user.getLovedTracks", ["user"] = user, ["limit"] = "1000", ["page"] = page.ToString() }, false, ct))["lovedtracks"];
            pages = TotalPages(j);
            foreach (var t in Items(j?["track"]))
                if (Str(t["name"]) is { Length: > 0 } title && Str(t["artist"]?["name"]) is { Length: > 0 } artist) list.Add((title, artist));
        }
        return list;
    }

    // The user's scrobbles until to (when, title, artist, album), the oldest first: 200 a page, progress(page, pages).
    public static async Task<List<(DateTime At, string Title, string Artist, string? Album)>> ScrobblesAsync(string key, string secret, string user, DateTime to,
        Action<int, int> progress, CancellationToken ct)
    {
        var list = new List<(DateTime, string, string, string?)>();
        var until = new DateTimeOffset(to).ToUnixTimeSeconds().ToString();
        for (int page = 1, pages = 1; page <= pages; page++)
        {
            var j = (await CallAsync(key, secret, new() { ["method"] = "user.getRecentTracks", ["user"] = user, ["limit"] = "200", ["page"] = page.ToString(), ["to"] = until },
                false, ct))["recenttracks"];
            pages = TotalPages(j);
            progress(page, pages);
            foreach (var t in Items(j?["track"]))
            {
                // (the song playing now has no date)
                if (!long.TryParse(Str(t["date"]?["uts"]), out var uts) || Str(t["name"]) is not { Length: > 0 } title
                    || Str(t["artist"]?["#text"]) is not { Length: > 0 } artist) continue;
                list.Add((DateTimeOffset.FromUnixTimeSeconds(uts).LocalDateTime, title, artist, Str(t["album"]?["#text"]) is { Length: > 0 } album ? album : null));
            }
        }
        list.Reverse();
        return list;
    }

    private static int TotalPages(JsonNode? j) => int.TryParse(Str(j?["@attr"]?["totalPages"]), out var n) ? n : 1;

    // (a single result comes as an object, not in an array)
    private static IEnumerable<JsonNode> Items(JsonNode? n) => n is JsonArray a ? a.OfType<JsonNode>() : n is JsonObject o ? new[] { o } : Enumerable.Empty<JsonNode>();

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;

    // At most 50 at a time.
    public static Task ScrobbleAsync(string key, string secret, string session, IReadOnlyList<Scrobble> batch, CancellationToken ct)
    {
        var p = new Dictionary<string, string> { ["method"] = "track.scrobble", ["sk"] = session };
        for (int i = 0; i < batch.Count; i++)
        {
            var s = batch[i];
            p[$"artist[{i}]"] = s.Artist;
            p[$"track[{i}]"] = s.Title;
            p[$"timestamp[{i}]"] = s.At.ToString();
            if (s.Album != null) p[$"album[{i}]"] = s.Album;
            if (s.Duration > 0) p[$"duration[{i}]"] = s.Duration.ToString();
        }
        return CallAsync(key, secret, p, true, ct);
    }
}

// ListenBrainz's API: the user's token (listenbrainz.org/settings) in the Authorization header, the listens as JSON.
public static class ListenBrainz
{
    private const string Api = "https://api.listenbrainz.org/1/";
    public const string TokenPage = "https://listenbrainz.org/settings/";

    private static async Task<JsonNode?> CallAsync(string token, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new ScrobbleRefused("ListenBrainz: " + resp.StatusCode);
        if (!resp.IsSuccessStatusCode) throw new IOException("ListenBrainz: " + (int)resp.StatusCode + " " + text);
        try { return JsonNode.Parse(text); }
        catch { return null; }
    }

    // The token's user (null: not a valid token).
    public static async Task<string?> UserAsync(string token, CancellationToken ct)
    {
        var json = await CallAsync(token, HttpMethod.Get, "validate-token", null, ct);
        return json?["valid"]?.GetValue<bool>() == true ? json["user_name"]?.GetValue<string>() : null;
    }

    private static JsonObject Listen(Scrobble s, string version, bool now)
    {
        var info = new JsonObject
        {
            ["media_player"] = "Ultimate MP3 Player", ["submission_client"] = "Ultimate MP3 Player", ["submission_client_version"] = version,
        };
        if (s.Duration > 0) info["duration_ms"] = s.Duration * 1000;
        var meta = new JsonObject { ["artist_name"] = s.Artist, ["track_name"] = s.Title, ["additional_info"] = info };
        if (s.Album != null) meta["release_name"] = s.Album;
        var listen = new JsonObject { ["track_metadata"] = meta };
        if (!now) listen["listened_at"] = s.At;
        return listen;
    }

    public static Task NowPlayingAsync(string token, Scrobble s, string version, CancellationToken ct)
        => CallAsync(token, HttpMethod.Post, "submit-listens",
            new JsonObject { ["listen_type"] = "playing_now", ["payload"] = new JsonArray(Listen(s, version, true)) }, ct);

    public static Task SubmitAsync(string token, IReadOnlyList<Scrobble> batch, string version, CancellationToken ct)
        => CallAsync(token, HttpMethod.Post, "submit-listens", new JsonObject
        {
            ["listen_type"] = batch.Count == 1 ? "single" : "import",
            ["payload"] = new JsonArray(batch.Select(s => (JsonNode)Listen(s, version, false)).ToArray()),
        }, ct);
}

// Sends what the profile listens to, to Last.fm and ListenBrainz (Settings › Integrations): "now playing" when a song starts,
// the play once half of it has been heard (at most 4 minutes; songs under 30 s never, Last.fm's rule). Offline, or with a
// service not answering, the plays wait in the profile's scrobbles.json and go with the next ones.
public sealed class Scrobbler
{
    public const string LastFmName = "lastfm", ListenBrainzName = "listenbrainz";
    private const int MinLength = 30, EnoughSeconds = 240, MaxWaiting = 5000;

    private readonly Profile _profile;
    private readonly AppSettings _settings;
    private readonly string _version;
    private readonly string? _file;
    private readonly object _lock = new();
    private readonly List<Scrobble> _waiting;
    private string? _id;
    private long _startedAt;
    private bool _done;
    private int _sending;

    public Scrobbler(Profile profile, AppSettings settings, string version)
    {
        _profile = profile;
        _settings = settings;
        _version = version;
        _file = profile.IsMirror ? null : Path.Combine(AppPaths.ProfileDir(profile.Info.Id), "scrobbles.json");
        _waiting = _file != null && File.Exists(_file) ? JsonStore.Load<List<Scrobble>>(_file) : new();
    }

    // The plays waiting to be sent changed, or an account was refused.
    public event Action? Changed;
    // A service refused the account (its name): it has to be connected again.
    public event Action<string>? Refused;

    public bool LastFmOn => _settings.LastFmKey is { Length: > 0 } && _settings.LastFmSecret is { Length: > 0 } && _profile.Data.LastFmSession != null;
    public bool ListenBrainzOn => _profile.Data.ListenBrainzToken != null;
    public bool On => LastFmOn || ListenBrainzOn;

    public int Waiting
    {
        get { lock (_lock) return _waiting.Count; }
    }

    // A song starts being heard (a new play of it): "now playing" on the services.
    public void Started(string id, Track t)
    {
        _id = id;
        _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _done = false;
        if (!On || string.IsNullOrWhiteSpace(t.Artist) || string.IsNullOrWhiteSpace(t.Title)) return;
        var s = Scrobble.Of(t, _startedAt);
        _ = Task.Run(async () =>
        {
            try
            {
                if (LastFmOn) await LastFm.NowPlayingAsync(_settings.LastFmKey!, _settings.LastFmSecret!, _profile.Data.LastFmSession!, s, CancellationToken.None);
            }
            catch (ScrobbleRefused) { OnRefused(LastFmName); }
            catch { }
            try
            {
                if (ListenBrainzOn) await ListenBrainz.NowPlayingAsync(_profile.Data.ListenBrainzToken!, s, _version, CancellationToken.None);
            }
            catch (ScrobbleRefused) { OnRefused(ListenBrainzName); }
            catch { }
        });
    }

    // How much of the play has been heard so far (song time): enough of it, it's sent.
    public void Heard(string id, Track t, double heard, double duration)
    {
        if (_done || id != _id || !On || duration < MinLength || heard < Math.Min(duration / 2, EnoughSeconds)) return;
        if (string.IsNullOrWhiteSpace(t.Artist) || string.IsNullOrWhiteSpace(t.Title)) return;
        _done = true;
        var s = Scrobble.Of(t, _startedAt);
        if (LastFmOn) s.Left.Add(LastFmName);
        if (ListenBrainzOn) s.Left.Add(ListenBrainzName);
        lock (_lock)
        {
            _waiting.Add(s);
            // (months offline: the oldest go)
            if (_waiting.Count > MaxWaiting) _waiting.RemoveRange(0, _waiting.Count - MaxWaiting);
        }
        Save();
        _ = SendAsync();
    }

    // The plays waiting, to every service still waiting for them, 50 at a time. One at a time.
    public async Task SendAsync()
    {
        if (Interlocked.Exchange(ref _sending, 1) == 1) return;
        try
        {
            foreach (var service in new[] { LastFmName, ListenBrainzName })
            {
                while (true)
                {
                    List<Scrobble> batch;
                    lock (_lock) batch = _waiting.Where(s => s.Left.Contains(service)).Take(50).ToList();
                    if (batch.Count == 0) break;
                    bool on = service == LastFmName ? LastFmOn : ListenBrainzOn;
                    try
                    {
                        // (an account disconnected meanwhile: its plays aren't waited for)
                        if (on && service == LastFmName)
                            await LastFm.ScrobbleAsync(_settings.LastFmKey!, _settings.LastFmSecret!, _profile.Data.LastFmSession!, batch, CancellationToken.None);
                        else if (on) await ListenBrainz.SubmitAsync(_profile.Data.ListenBrainzToken!, batch, _version, CancellationToken.None);
                    }
                    catch (ScrobbleRefused)
                    {
                        OnRefused(service);
                        break;
                    }
                    catch { break; }
                    lock (_lock)
                    {
                        foreach (var s in batch) s.Left.Remove(service);
                        _waiting.RemoveAll(s => s.Left.Count == 0);
                    }
                    Save();
                }
            }
        }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }

    // An account no longer connected (or just connected): what waits for it is dropped, so it doesn't stay forever.
    public void Forget(string service)
    {
        lock (_lock)
        {
            foreach (var s in _waiting) s.Left.Remove(service);
            _waiting.RemoveAll(s => s.Left.Count == 0);
        }
        Save();
    }

    private void OnRefused(string service)
    {
        Refused?.Invoke(service);
        Changed?.Invoke();
    }

    private void Save()
    {
        if (_file != null)
        {
            List<Scrobble> copy;
            lock (_lock) copy = _waiting.Select(s => new Scrobble { Artist = s.Artist, Title = s.Title, Album = s.Album, Duration = s.Duration, At = s.At, Left = s.Left.ToList() }).ToList();
            try { JsonStore.Save(_file, copy); }
            catch { }
        }
        Changed?.Invoke();
    }
}
