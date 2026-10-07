using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// Null-tolerant readers for yt-dlp / gallery-dl JSON.
internal static class J
{
    public static JsonElement? Prop(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;

    public static string? Str(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static double? Num(this JsonElement e, string name)
    {
        if (e.Prop(name) is not { } v) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static int? Int(this JsonElement e, string name)
    {
        var d = e.Num(name);
        return d is null || double.IsNaN(d.Value) ? null : (int)Math.Round(d.Value);
    }

    public static long? Long(this JsonElement e, string name)
    {
        var d = e.Num(name);
        return d is null || double.IsNaN(d.Value) ? null : (long)d.Value;
    }

    public static bool Bool(this JsonElement e, string name)
        => e.Prop(name) is { } v && (v.ValueKind == JsonValueKind.True ||
                                     (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    public static IEnumerable<JsonElement> Arr(this JsonElement e, string name)
        => e.Prop(name) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}

public static class Text
{
    public static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Makes a string a valid Windows file name.
    public static string SafeFileName(string? name, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == '"') sb.Append('\'');
            else if (ch is '/' or '\\' or '|') sb.Append('-');
            else if (ch is ':') sb.Append(" -");
            else if (ch is '<' or '>' or '*' or '?') { }
            else if (char.IsControl(ch) || invalid.Contains(ch)) sb.Append(' ');
            else sb.Append(ch);
        }
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().Trim('.', ' ');
        if (s.Length > max)
        {
            var cut = max;
            if (char.IsHighSurrogate(s[cut - 1])) cut--;
            s = s[..cut].TrimEnd('.', ' ');
        }
        if (s.Length == 0) s = "download";
        if (Reserved.Contains(s)) s = "_" + s;
        return s;
    }

    // dir\name.ext, or "name (2).ext" when taken.
    public static string UniquePath(string dir, string name, string ext)
    {
        ext = ext.TrimStart('.');
        var path = Path.Combine(dir, $"{name}.{ext}");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}).{ext}");
        return path;
    }

    public static string Duration(double? seconds)
    {
        if (seconds is not > 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Size(double? bytes)
    {
        if (bytes is not > 0) return "";
        double b = bytes.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return b.ToString(u >= 2 ? "0.0" : "0", It) + " " + units[u];
    }

    public static string Speed(double? bytesPerSec) => bytesPerSec is > 0 ? Size(bytesPerSec) + "/s" : "";

    // Lowercase, no accents, letters and digits only.
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static HashSet<string> Tokens(string? s) => Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // Share of wanted's words found in candidate.
    public static double Coverage(string? wanted, string? candidate)
    {
        var w = Tokens(wanted);
        if (w.Count == 0) return 0;
        var c = Tokens(candidate);
        return w.Count(c.Contains) / (double)w.Count;
    }

    public static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    public static string Hz(int? hz) => hz is > 0 ? (hz.Value % 1000 == 0 ? $"{hz / 1000} kHz" : (hz.Value / 1000.0).ToString("0.#", It) + " kHz") : "";

    public static string Channels(int? ch) => ch switch { 1 => "mono", 2 => "stereo", > 2 => L.F("{0} canali", ch), _ => "" };
}

// Engine error already turned into a user message.
public sealed class EngineException : Exception
{
    public string Details { get; }
    public bool NeedsLogin { get; init; }
    public bool Unsupported { get; init; }
    public bool NoMedia { get; init; }
    public bool FormatUnavailable { get; init; }
    // The site refuses because of too many requests.
    public bool RateLimited { get; init; }
    // "403 Forbidden": one song closed to us, or (several in a row) a site that has had enough (SoundCloud's way of saying it).
    public bool Forbidden { get; init; }

    public EngineException(string message, string? details = null) : base(message) => Details = details ?? "";
}

public static class ErrorText
{
    // Maps yt-dlp / gallery-dl stderr to an EngineException.
    public static EngineException FromEngine(string engine, string stderr, int exitCode)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // Killed by the system without a word (139 = it crashed, 137 = killed, 134 = aborted): not the site's fault.
        if (exitCode is 134 or 137 or 139 or < 0 && !lines.Any(l => l.StartsWith("ERROR:", StringComparison.Ordinal)))
        {
            var text = L.F("{0} si è chiuso per un errore (codice {1}).", engine, exitCode);
            if (Engines.Host?.CrashHint?.Invoke() is { } hint) text += " " + hint;
            return new EngineException(text, stderr);
        }
        string raw =
            lines.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ??
            lines.LastOrDefault(l => l.Contains("[error]", StringComparison.Ordinal)) ??
            lines.LastOrDefault(l => !l.StartsWith("WARNING", StringComparison.Ordinal) && !l.Contains("[warning]")) ??
            L.F("{0} è terminato con codice {1}", engine, exitCode);

        string msg = raw;
        msg = Regex.Replace(msg, @"^ERROR:\s*", "");
        msg = Regex.Replace(msg, @"^\[[^\]]+\]\[error\]\s*", "");
        msg = Regex.Replace(msg, @"^\[[^\]]+\]\s*[^:\s]+:\s*", "");
        var low = raw.ToLowerInvariant();
        var all = stderr.ToLowerInvariant();

        bool Has(params string[] keys) => keys.Any(k => low.Contains(k));

        if (Has("unsupported url", "no suitable extractor"))
            return new EngineException(L.T("Questo link non è supportato."), stderr) { Unsupported = true };
        if (Has("error 429", "(429)", "status code 429", "too many requests", "rate-limited", "rate limited", "rate limit", "try again later",
                "please wait a few minutes") ||
            all.Contains("http error 429") || all.Contains("has been rate-limited"))
            return new EngineException(L.T("Il sito sta limitando i download (troppe richieste in poco tempo)."), stderr) { RateLimited = true };
        if (Has("confirm you're not a bot", "confirm you’re not a bot", "not a bot"))
            return new EngineException(L.T("YouTube chiede di confermare che non sei un bot."), stderr) { NeedsLogin = true };
        if (Has("confirm your age", "age-restricted", "age restricted", "inappropriate for some users"))
            return new EngineException(L.T("Contenuto con limite d'età: serve l'accesso con il tuo account."), stderr) { NeedsLogin = true };
        if (Has("private video", "this video is private", "is private", "private account", "privateaccount"))
            return new EngineException(L.T("Il contenuto è privato."), stderr) { NeedsLogin = true };
        if (Has("login required", "log in", "login", "sign in", "authrequired", "authorizationerror", "401 unauthorized",
                "use --cookies", "cookies", "not available to you", "members-only", "subscriber"))
            return new EngineException(L.T("Il sito richiede l'accesso (login) per questo contenuto."), stderr) { NeedsLogin = true };
        if (Has("drm"))
            return new EngineException(L.T("Il contenuto è protetto da DRM e non può essere scaricato."), stderr);
        if (Has("requested format is not available", "requested format not available"))
            return new EngineException(L.T("Il formato richiesto non è più disponibile."), stderr) { FormatUnavailable = true };
        if (Has("no video formats found", "no video could be found", "there's no video", "no media found", "no video in this", "does not contain any video"))
            return new EngineException(L.T("Nessun video trovato in questo link."), stderr) { NoMedia = true };
        if (Has("video unavailable", "this video is unavailable", "not available", "has been removed", "no longer available", "does not exist",
                "could not be found", "not found", "404"))
            return new EngineException(L.T("Il contenuto non è disponibile (rimosso o inesistente)."), stderr);
        if (Has("403", "forbidden"))
            return new EngineException(L.T("Il sito ha negato l'accesso (errore 403)."), stderr) { Forbidden = true };
        if (Has("getaddrinfo", "failed to resolve", "name or service not known", "timed out", "connection refused", "network is unreachable", "no connection"))
            return new EngineException(L.T("Impossibile contattare il sito: controlla la connessione a Internet."), stderr);
        if (Has("is not a valid url", "invalid url"))
            return new EngineException(L.T("Il link non è valido."), stderr);
        if (Has("live event will begin", "premieres in", "this live event"))
            return new EngineException(L.T("La diretta o la première non è ancora iniziata."), stderr);
        if (Regex.IsMatch(msg.Trim(), @"^(KeyError: )?'[\w.-]+'$|^KeyError|^TypeError|^IndexError|^AttributeError"))
            return new EngineException(L.T("Il sito ha risposto in modo inatteso: il contenuto potrebbe essere stato rimosso, richiedere l'accesso (cookie del browser) o servire un aggiornamento dei motori."), stderr);

        if (msg.Length > 220) msg = msg[..220] + "…";
        return new EngineException(msg, stderr);
    }
}
