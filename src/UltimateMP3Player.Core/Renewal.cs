using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// What stands between the iPhone's app and renewing itself: each has its own words and its own button.
public enum RenewalProblem { None, Vpn, Cable, SignIn, Internet, Install, Other }

// One line of the library that renews it (the installer's umprenew): which event, and what that event carries.
public sealed record RenewalEvent(string Name, string Stage = "", double Progress = 0, string What = "", string Message = "", bool? Renewed = null, DateTime? Expires = null);

// The iPhone's app renewing itself (Platform/Renewal.cs in its project), the part that doesn't need an iPhone: mp3cli renewal checks it.
public static class RenewalRules
{
    // Apple's 7 days: renewed from the third on, worth a box on the screen in the last two.
    private static readonly TimeSpan Early = TimeSpan.FromDays(4), Late = TimeSpan.FromDays(2);

    // (not known: worth renewing, which tells)
    public static bool Due(DateTime? expires, DateTime now) => expires is not { } e || e - now < Early;

    // From when it's due.
    public static DateTime? From(DateTime? expires) => expires - Early;

    // Past its date and yet running, it was renewed some other way (the cable): nothing to be alarmed about.
    public static bool Urgent(DateTime? expires, DateTime now) => expires is { } e && e > now && e - now <= Late;

    // After a failure: an hour, so Apple isn't asked again and again; half a day when only installing it again would help.
    public static bool CanTry(DateTime? failed, RenewalProblem problem, DateTime now)
        => failed is not { } f || f > now || now - f >= (problem == RenewalProblem.Install ? TimeSpan.FromHours(12) : TimeSpan.FromHours(1));

    // The fifth and the sixth day, the ones still to come.
    public static IEnumerable<DateTime> Reminders(DateTime? expires, DateTime now)
        => expires is { } e ? new[] { e.AddDays(-2), e.AddDays(-1) }.Where(at => at > now) : Enumerable.Empty<DateTime>();

    public static DateTime? Latest(params DateTime?[] dates) => dates.Max();

    // How far an update is, the download before it included: each stage its share. A renewal has one stage.
    public static double Overall(string stage, double progress) => stage switch
    {
        "Downloading" => 0.3 * progress,
        "Preparing" => 0.3 + 0.05 * progress,
        "Signing" => 0.35 + 0.25 * progress,
        "Installing" => 0.6 + 0.4 * progress,
        _ => progress,
    };

    private static readonly Regex Expiration = new(@"<key>ExpirationDate</key>\s*<date>([^<]+)</date>");

    // What the installer left beside the keys: until when the app was signed as it put them there, in seconds. Anything else counts as
    // nothing, and so does what no signature lasts (Apple's longest is a year), or the app would never renew itself again.
    public static DateTime? PlacedExpires(string text, DateTime now)
        => long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is > 0 and < 253402300800
            && DateTime.UnixEpoch.AddSeconds(s) is var at && at <= now.AddDays(366) ? at : null;

    // When a provisioning profile ends (a signed envelope with a plist inside, as it is).
    public static DateTime? ProfileExpires(byte[] profile)
    {
        var m = Expiration.Match(Encoding.Latin1.GetString(profile));
        return m.Success && DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? at : null;
    }

    private static readonly (RenewalProblem Problem, string[] Marks)[] Known =
    {
        (RenewalProblem.Vpn, new[] { "can't be reached at" }),
        (RenewalProblem.Cable, new[] { "Not paired", "another Apple ID" }),
        (RenewalProblem.SignIn, new[] { "Not signed in", "session has expired", "expired token", "Please log in" }),
        (RenewalProblem.Internet, new[] { "error sending request", "tcp connect error", "dns error", "anisette", "URL Bag" }),
    };

    public static RenewalProblem Problem(string message)
    {
        foreach (var (problem, marks) in Known)
            if (marks.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))) return problem;
        return RenewalProblem.Other;
    }

    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+"), Secret = new(@"[A-Za-z0-9+/=_-]{24,}");

    // The library draws a tree of what led to it, each with where in its code.
    private static readonly Regex Branch = new(@"^[^\w(“""']+"), Place = new(@"\.rs:\d+\s*$");

    // An error's text as it may be shown: no address, no phone's id, nothing long enough to be a token or a key.
    public static string Shown(string message)
    {
        var text = string.Join(" · ", message.Split('\n').Where(l => !Place.IsMatch(l)).Select(l => Branch.Replace(l, "").Trim()).Where(l => l.Length > 0));
        text = Secret.Replace(Email.Replace(text, "…"), "…");
        return text.Length > 240 ? text[..240] + "…" : text;
    }

    // Null: a line that isn't an event.
    public static RenewalEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var name = Text("event");
            if (name.Length == 0) return null;
            return new RenewalEvent(name, Text("stage"),
                root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetDouble(), 0, 1) : 0,
                Text("what"), Text("message"),
                root.TryGetProperty("renewed", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.ValueKind == JsonValueKind.True : null,
                root.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var s) && s is > 0 and < 253402300800
                    ? DateTime.UnixEpoch.AddSeconds(s) : null);
        }
        catch (JsonException) { return null; }
    }
}
