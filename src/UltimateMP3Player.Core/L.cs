using System.Globalization;

namespace UltimateMP3Player.Core;

// UI texts: Italian is the key, English comes from En.
public static partial class L
{
    public static bool English { get; set; }

    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-US") : Text.It;

    public static string T(string it) => English && En.TryGetValue(it, out var en) ? en : it;

    public static string F(string it, params object?[] args) => string.Format(Culture, T(it), args);

    // "1 brano" / "5 brani" in the current language.
    public static string Count(int n, string one, string many) => n == 1 ? T(one) : F(many, n);
}
