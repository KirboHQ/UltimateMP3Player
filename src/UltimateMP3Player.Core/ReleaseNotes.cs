using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// A GitHub release's notes: "v3.9.3 - Phone and computer share their music" → Title "Phone and computer share their music".
public sealed record ReleaseNote(Version Version, string Title, string Body)
{
    public string VersionText => "v" + Version.ToString(3);
    public List<NoteBlock> Blocks => ReleaseNotes.Parse(Body);
}

public enum NoteKind { Heading, Bullet, Quote, Text }

public sealed record NoteRun(string Text, bool Bold, bool Italic);

public sealed record NoteBlock(NoteKind Kind, List<NoteRun> Runs);

// "What's new": at the first start after an update, the notes of every release since the version last started.
public static class ReleaseNotes
{
    // The releases after AppSettings.LastVersion up to this one, newest first; empty when there's nothing to show. A new
    // install only remembers its version; an update from a version that didn't remember it (≤3.9.3) shows this one's
    // notes. Offline, or this version not published yet (a build from the sources): asked again at the next start.
    public static async Task<List<ReleaseNote>> SinceLastAsync(AppSettings settings, string repo, Version current)
    {
        current = Three(current);
        Version? last = Version.TryParse(settings.LastVersion, out var v) ? Three(v) : null;
        if (settings.IsNew || !settings.ShowWhatsNew || last != null && last >= current)
        {
            Remember(settings, current);
            return new();
        }
        if (repo.Length == 0) return new();
        List<ReleaseNote> all;
        try { all = await FetchAsync(repo); }
        catch { return new(); }
        var shown = all.Where(n => n.Version == current || last != null && n.Version > last && n.Version < current)
            .OrderByDescending(n => n.Version).ToList();
        if (shown.Count == 0 || shown[0].Version != current) return new();
        Remember(settings, current);
        return shown;
    }

    // This version's notes (Settings › Updates), empty when they can't be fetched.
    public static async Task<List<ReleaseNote>> ForAsync(string repo, Version current)
    {
        try { return (await FetchAsync(repo)).Where(n => n.Version == Three(current)).ToList(); }
        catch { return new(); }
    }

    private static void Remember(AppSettings settings, Version current)
    {
        if (settings.LastVersion == current.ToString(3)) return;
        settings.LastVersion = current.ToString(3);
        settings.Save();
    }

    // The assembly's 3.9.3.0 and the tag's 3.9.3 are the same version.
    private static Version Three(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    // The latest 100 releases (drafts and pre-releases left out).
    public static async Task<List<ReleaseNote>> FetchAsync(string repo)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases?per_page=100");
        req.Headers.TryAddWithoutValidation("User-Agent", "UltimateMP3Player");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var resp = await Http.Client.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
        var list = new List<ReleaseNote>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean() || r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) continue;
            var name = (r.TryGetProperty("name", out var n) ? n.GetString() : null)?.Trim() ?? "";
            if (name.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) name = name[tag.Length..].TrimStart(' ', '-', '–', '—', ':', '·');
            var body = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            list.Add(new ReleaseNote(Three(version), name, body));
        }
        return list;
    }

    // ------------------------------------------------------------------ the Markdown of the notes

    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"^\d+[.)]\s", RegexOptions.Compiled);

    // What GitHub's release notes use: "#" headings, "-"/"*" bullets, "1." lists, "> " quotes, **bold**, *italic*, links
    // (their text), `code` (plain). A line that doesn't start a block goes on the one before (a bullet wrapped by hand).
    public static List<NoteBlock> Parse(string markdown)
    {
        var blocks = new List<NoteBlock>();
        var text = new StringBuilder();
        NoteKind kind = NoteKind.Text;
        bool open = false;
        void Close()
        {
            if (open && text.Length > 0) blocks.Add(new NoteBlock(kind, Runs(text.ToString())));
            text.Clear();
            open = false;
        }
        void Start(NoteKind k, string s)
        {
            Close();
            kind = k;
            text.Append(s);
            open = true;
        }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.Contains("Full Changelog") || line.Trim('-', '*', '_', ' ').Length == 0 && line.Length >= 3)
            {
                Close();
                continue;
            }
            if (line.StartsWith('#')) Start(NoteKind.Heading, line.TrimStart('#').Trim());
            else if (line.Length > 4 && line.StartsWith("**") && line.EndsWith("**") && line.IndexOf("**", 2, StringComparison.Ordinal) == line.Length - 2)
                Start(NoteKind.Heading, line[2..^2].Trim());
            else if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ') Start(NoteKind.Bullet, line[2..].Trim());
            else if (line.StartsWith('>')) Start(NoteKind.Quote, line.TrimStart('>').Trim());
            else if (Number.IsMatch(line)) Start(NoteKind.Text, line);
            else if (open && kind != NoteKind.Heading) text.Append(' ').Append(line);
            else Start(NoteKind.Text, line);
            if (kind == NoteKind.Heading) Close();
        }
        Close();
        return blocks;
    }

    private static List<NoteRun> Runs(string s)
    {
        s = Link.Replace(Image.Replace(s, ""), "$1").Replace("`", "").Replace("<br>", " ");
        var runs = new List<NoteRun>();
        var part = new StringBuilder();
        bool bold = false, italic = false;
        void Flush()
        {
            if (part.Length > 0) runs.Add(new NoteRun(part.ToString(), bold, italic));
            part.Clear();
        }
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length && "*_`#\\".Contains(s[i + 1])) part.Append(s[++i]);
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*') { Flush(); bold = !bold; i++; }
            // A lone "*" between spaces ("2 * 3") is text.
            else if (s[i] == '*' && !(i > 0 && s[i - 1] == ' ' && i + 1 < s.Length && s[i + 1] == ' ')) { Flush(); italic = !italic; }
            else part.Append(s[i]);
        }
        Flush();
        return runs;
    }
}
