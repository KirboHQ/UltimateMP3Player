namespace UltimateMP3Player.Core;

// How the window is laid out, per profile (ProfileData.Layout, Settings › Appearance › Layout): the side bar and the player bar
// floating on the window or attached to its edges, the side bar's width, which pages the menus have and in what order, Home's rows.
public sealed class LayoutLook
{
    // LayoutBars.Auto (floating with the glass look), Float or Dock.
    public string Bars { get; set; } = LayoutBars.Auto;
    // The side bar of the computers (Layout.MinSide-MaxSide).
    public int SideWidth { get; set; } = 280;
    // The order chosen, by "place.key" ("menu.dj", "home.recent"...), and the ones hidden.
    public List<string> Order { get; set; } = new();
    public List<string> Hidden { get; set; } = new();
}

public static class LayoutBars
{
    public const string Auto = "auto", Float = "float", Dock = "dock";
}

public static class Layout
{
    // The profile's choices (AppHost.ApplyTheme); Changed: the views arrange their menus and rows again.
    public static LayoutLook Look { get; set; } = new();
    public static event Action? Changed;
    public static void Notify() => Changed?.Invoke();

    public static bool Floating => Look.Bars == LayoutBars.Float || Look.Bars != LayoutBars.Dock && Glass.On;
    public const int MinSide = 220, MaxSide = 360;
    public static int SideWidth => Math.Clamp(Look.SideWidth, MinSide, MaxSide);

    // The places and their keys, in the app's own order: the computers' side bar and its sections, the phones' bottom bar, Home's
    // rows (the phones' have the playlists first and the statistics).
    public const string Menu = "menu", Sections = "sections", Tabs = "tabs", Home = "home", PhoneHome = "phonehome";
    public static string[] Keys(string place) => place switch
    {
        Menu => new[] { "home", "search", "library", "together", "dj" },
        Sections => new[] { "playlists", "artists", "radios", "tags" },
        Tabs => new[] { "home", "search", "library", "together", "settings" },
        PhoneHome => new[] { "playlists", "recent", "foryou", "releases", "radios", "stats", "added" },
        _ => new[] { "recent", "foryou", "releases", "radios", "playlists", "added" },
    };

    // Never hidden: the way home and to the settings.
    public static bool Fixed(string place, string key) => place is Menu or Tabs && key is "home" or "settings";

    public static bool IsHidden(string place, string key) => !Fixed(place, key) && Look.Hidden.Contains(place + "." + key);

    // A place's keys as chosen: in the order chosen (one added by a later version where the app has it), the hidden ones left out
    // unless all.
    public static List<string> Arrange(string place, bool all = false)
    {
        var keys = Keys(place);
        var list = Look.Order.Where(o => o.StartsWith(place + ".", StringComparison.Ordinal)).Select(o => o[(place.Length + 1)..])
            .Where(keys.Contains).Distinct().ToList();
        for (int i = 0; i < keys.Length; i++)
            if (!list.Contains(keys[i])) list.Insert(Math.Min(i, list.Count), keys[i]);
        return all ? list : list.Where(k => !IsHidden(place, k)).ToList();
    }

    public static void SetOrder(string place, IEnumerable<string> keys)
    {
        Look.Order.RemoveAll(o => o.StartsWith(place + ".", StringComparison.Ordinal));
        Look.Order.AddRange(keys.Select(k => place + "." + k));
    }

    public static void SetHidden(string place, string key, bool hidden)
    {
        Look.Hidden.Remove(place + "." + key);
        if (hidden && !Fixed(place, key)) Look.Hidden.Add(place + "." + key);
    }
}
