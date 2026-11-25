using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UltimateMP3Player.Core;

// What stands between the iPhone's app and renewing itself: each has its own words and its own button.
public enum RenewalProblem { None, Vpn, Cable, SignIn, Internet, Install, Other }

// Why signing in to Apple again didn't work: each has its own words.
public enum SignInProblem { None, Password, Code, Refused, Internet, Other }

// A phone number Apple can text a code to, as Apple writes it (mostly dots).
public sealed record RenewalNumber(uint Id, string Number);

// One line of the library that renews it (the installer's umprenew): which event, and what that event carries. Of an "ask" for a code: Sms,
// it went by text message; Error, why the last answer wasn't good; Failed, the last way of sending it didn't work, and only another way is
// an answer (a code, or the same way again, would end the sign-in).
public sealed record RenewalEvent(string Name, string Stage = "", double Progress = 0, string What = "", string Message = "", bool? Renewed = null, DateTime? Expires = null,
    string Email = "", bool Sms = false, bool Failed = false, string Error = "", IReadOnlyList<RenewalNumber>? Numbers = null);

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

    // (before Known, which starts with it)
    private static readonly string[] Offline = { "error sending request", "tcp connect error", "dns error", "anisette", "URL Bag" };

    private static readonly (RenewalProblem Problem, string[] Marks)[] Known =
    {
        (RenewalProblem.Vpn, new[] { "can't be reached at" }),
        (RenewalProblem.Cable, new[] { "Not paired", "another Apple ID" }),
        (RenewalProblem.SignIn, new[] { "Not signed in", "session has expired", "expired token", "Please log in" }),
        (RenewalProblem.Internet, Offline),
    };

    // Apple's own numbers, as the installer knows them (its Errors.Known), in the same order.
    private static readonly (SignInProblem Problem, string[] Marks)[] KnownSignIn =
    {
        (SignInProblem.Password, new[] { "-20101", "-22406", "-20970", "entered incorrectly", "incorrect password", "invalid credentials" }),
        (SignInProblem.Code, new[] { "-21669", "-22951", "incorrect verification" }),
        (SignInProblem.Refused, new[] { "-80045", "-80046", "-36607", "HTTP 503", "503 Service", "cannot be completed at this time" }),
        (SignInProblem.Internet, Offline),
    };

    // Why a sign-in ended, or why a code wasn't taken.
    public static SignInProblem SignIn(string message)
    {
        foreach (var (problem, marks) in KnownSignIn)
            if (marks.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))) return problem;
        return SignInProblem.Other;
    }

    public static RenewalProblem Problem(string message)
    {
        foreach (var (problem, marks) in Known)
            if (marks.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase))) return problem;
        return RenewalProblem.Other;
    }

    // The same for the person, with what there is to do; text only for what has no words of its own.
    public static string Why(SignInProblem problem, string text = "") => problem switch
    {
        SignInProblem.None => "",
        SignInProblem.Password => L.T("Wrong password: check it and try again. If the email of this Apple ID has changed, sign in from the computer."),
        SignInProblem.Code => L.T("That code didn't work. Have a new one sent and type it right away."),
        SignInProblem.Refused => L.T("Apple is refusing the sign-in right now. It happens: try again in a while, or use the computer's sign-in."),
        SignInProblem.Internet => L.T("Apple, or the anisette service, couldn't be reached: check the connection and try again."),
        _ => L.T("Something didn't work: try again, or use the computer's sign-in.") + (text.Length > 0 ? "\n" + text : ""),
    };

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
            bool Flag(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
            var numbers = new List<RenewalNumber>();
            if (root.TryGetProperty("numbers", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var n in list.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetUInt32(out var i)
                        && n.TryGetProperty("number", out var said) && said.ValueKind == JsonValueKind.String)
                        numbers.Add(new RenewalNumber(i, said.GetString()!));
            var name = Text("event");
            if (name.Length == 0) return null;
            return new RenewalEvent(name, Text("stage"),
                root.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetDouble(), 0, 1) : 0,
                Text("what"), Text("message"),
                root.TryGetProperty("renewed", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.ValueKind == JsonValueKind.True : null,
                root.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var s) && s is > 0 and < 253402300800
                    ? DateTime.UnixEpoch.AddSeconds(s) : null,
                Text("email"), Flag("sms"), Flag("failed"), Text("error"), numbers);
        }
        catch (JsonException) { return null; }
    }
}
