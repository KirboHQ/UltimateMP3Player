using System.Globalization;

namespace UltimateMP3Player.Core;

// UI texts: English is the key, Italian comes from It. "Select all|f": the same English with another Italian (a
// feminine, a verb...), the part after "|" never shown.
public static partial class L
{
    public static bool English { get; set; }

    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-US") : Text.It;

    public static string T(string en)
    {
        if (!English && It.TryGetValue(en, out var it)) return it;
        int bar = en.IndexOf('|');
        return bar < 0 ? en : en[..bar];
    }

    public static string F(string en, params object?[] args) => string.Format(Culture, T(en), args);

    // "1 song" / "5 songs" in the current language.
    public static string Count(int n, string one, string many) => n == 1 ? T(one) : F(many, n);

    // A text saved or sent by a version with Italian keys (3.7 and before: an equalizer preset, a room's message): its
    // English key; any other text stays as it is.
    public static string KeyOf(string text) => Keys.TryGetValue(text, out var en) ? en : text;

    private static Dictionary<string, string>? _keys;
    private static Dictionary<string, string> Keys
    {
        get
        {
            if (_keys != null) return _keys;
            var keys = new Dictionary<string, string>();
            foreach (var (en, it) in It) keys.TryAdd(it, en);
            return _keys = keys;
        }
    }
}
