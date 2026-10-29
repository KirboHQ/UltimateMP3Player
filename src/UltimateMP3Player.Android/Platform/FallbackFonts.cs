using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Platform;

namespace UltimateMP3Player.Platform;

// Android's fonts for the other writing systems (Cyrillic, Greek, Japanese, Korean, Chinese, Arabic, emoji...) are fallback
// families without a name in fonts.xml: Avalonia found one for a character, then looked it up again by its name, got
// nothing, and the text came out empty. Here they are the app's own fallbacks, opened from their files (each one the
// first time a character needs it): Roboto first, then the Japanese one (all of CJK) and emoji, then the system's order.
public sealed class FallbackFonts : FontCollectionBase
{
    private static readonly Uri CollectionKey = new("fonts:AndroidFallback", UriKind.Absolute);
    // (the platform's loading from a stream, what Avalonia's embedded fonts use: hidden from apps by its reference assemblies)
    private static readonly MethodInfo? FromStream = typeof(IFontManagerImpl).GetMethod("TryCreateGlyphTypeface",
        new[] { typeof(Stream), typeof(FontSimulations), typeof(IGlyphTypeface).MakeByRefType() });

    private readonly Dictionary<string, string> _paths;
    private readonly List<FontFamily> _families;
    private readonly Dictionary<(string, bool), IGlyphTypeface?> _loaded = new();
    private IFontManagerImpl? _fontManager;

    private FallbackFonts(List<string> paths)
    {
        _paths = paths.ToDictionary(p => Path.GetFileName(p), p => p);
        _families = _paths.Keys.Select(name => new FontFamily(CollectionKey + "#" + name)).ToList();
    }

    public static AppBuilder Add(AppBuilder builder)
    {
        List<string> paths;
        try { paths = SystemFallbacks(); }
        catch { return builder; }
        if (paths.Count == 0) return builder;
        var fonts = new FallbackFonts(paths);
        return builder
            .With(new FontManagerOptions { FontFallbacks = fonts._families.Select(f => new FontFallback { FontFamily = f }).ToArray() })
            .AfterSetup(_ => FontManager.Current.AddFontCollection(fonts));
    }

    private static List<string> SystemFallbacks()
    {
        // (Android 15 moved the fallbacks to font_fallback.xml; fonts.xml stays for older apps)
        var xml = File.Exists("/system/etc/font_fallback.xml") ? "/system/etc/font_fallback.xml" : "/system/etc/fonts.xml";
        var families = XDocument.Load(xml).Root!.Elements("family").ToList();
        static string Lang(XElement f) => (string?)f.Attribute("lang") ?? "";
        var fallbacks = families.Where(f => f.Attribute("name") == null && Lang(f) != "" && (string?)f.Attribute("ignore") != "true"
                                            && (string?)f.Attribute("variant") != "compact").ToList();
        var order = families.Where(f => (string?)f.Attribute("name") == "sans-serif")
            .Concat(fallbacks.Where(f => Lang(f).Split(',').Contains("ja")))
            .Concat(fallbacks.Where(f => Lang(f).Contains("Zsye")))
            .Concat(fallbacks);
        return order.Select(FileOf).OfType<string>().Distinct().Where(File.Exists).ToList();
    }

    // The family's regular font: its file name is the element's text, before the <axis> lines.
    private static string? FileOf(XElement family)
    {
        var fonts = family.Elements("font").ToList();
        var font = fonts.FirstOrDefault(f => (string?)f.Attribute("weight") == "400" && (string?)f.Attribute("style") != "italic") ?? fonts.FirstOrDefault();
        var name = font?.Nodes().OfType<XText>().FirstOrDefault()?.Value.Trim();
        return string.IsNullOrEmpty(name) ? null : Path.Combine("/system/fonts", name);
    }

    public override Uri Key => CollectionKey;
    public override int Count => _families.Count;
    public override FontFamily this[int index] => _families[index];
    public override IEnumerator<FontFamily> GetEnumerator() => _families.GetEnumerator();
    public override void Initialize(IFontManagerImpl fontManager) => _fontManager = fontManager;

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out IGlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (_fontManager == null || !_paths.TryGetValue(familyName, out var path)) return false;
        // (only the regular font is opened: titles in bold get it thickened)
        bool bold = weight >= FontWeight.SemiBold;
        lock (_loaded)
        {
            if (!_loaded.TryGetValue((familyName, bold), out glyphTypeface))
            {
                try
                {
                    // (Skia goes on reading the stream: it stays open)
                    var stream = File.OpenRead(path);
                    var args = new object?[] { stream, bold ? FontSimulations.Bold : FontSimulations.None, null };
                    if (FromStream?.Invoke(_fontManager, args) is true) glyphTypeface = (IGlyphTypeface?)args[2];
                    else stream.Dispose();
                }
                catch
                {
                    glyphTypeface = null;
                }
                _loaded[(familyName, bold)] = glyphTypeface;
            }
        }
        return glyphTypeface != null;
    }
}
