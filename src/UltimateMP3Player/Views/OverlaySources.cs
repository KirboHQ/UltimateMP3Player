#if AVALONIA
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BrushKind = Avalonia.Media.IBrush;
using ElementKind = Avalonia.Controls.Control;
using TimerKind = Avalonia.Threading.DispatcherTimer;
using ShapePath = Avalonia.Controls.Shapes.Path;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BrushKind = System.Windows.Media.Brush;
using ElementKind = System.Windows.FrameworkElement;
using TimerKind = System.Windows.Threading.DispatcherTimer;
using ShapePath = System.Windows.Shapes.Path;
#endif
using Pic = UltimateMP3Player.Views.TilePictures;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player;

// Settings › Stream overlay, the same on Windows and on Linux/macOS (linked into the Avalonia app). Not on the phones: OBS
// reads the overlays from 127.0.0.1, so it has to be on the computer playing the music.
public sealed class OverlaySourcesViewModel : Observable
{
    public static readonly OverlaySourcesViewModel Shared = new();

    public ObservableCollection<OverlaySourceRow> Sources { get; } = new();
    public ICommand AddCommand { get; }

    private static AppSettings S => App.Host.Settings;

    private OverlaySourcesViewModel()
    {
        foreach (var s in S.OverlaySources) Sources.Add(new OverlaySourceRow(s, this));
        AddCommand = new RelayCommand(() => Dialogs.EditOverlaySource(Add()));
    }

    public OverlaySourceRow Add()
    {
        var source = new OverlaySource
        {
            Name = L.F("Overlay {0}", S.OverlaySources.Count + 1),
            NothingTitle = L.T("Nothing playing"),
            NothingArtist = L.T("Get the music started"),
        };
        S.OverlaySources.Add(source);
        Apply();
        var row = new OverlaySourceRow(source, this);
        Sources.Add(row);
        return row;
    }

    public void Delete(OverlaySourceRow row)
    {
        if (!Dialogs.Confirm(L.T("Delete this overlay?"), L.F("The link to “{0}” in OBS will stop working.", row.Name), L.T("Delete"), true)) return;
        S.OverlaySources.Remove(row.Source);
        try { File.Delete(row.PictureFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Sources.Remove(row);
        Apply();
    }

    // (the port is kept: OBS goes on using the link it was given)
    public static void Apply()
    {
        S.OverlayPort = OverlayServer.Shared.Apply(S.OverlaySources, S.OverlayPort);
        S.Save();
    }
}

public sealed class OverlaySourceRow : Observable
{
    public static readonly (string Id, string Name)[] Looks =
    {
        ("compact", "Compact"), ("boxy", "Boxy"), ("gallery", "Gallery"), ("minimal", "Minimal"), ("macos", "macOS"), ("shell", "Shell"), ("discord", "Discord"),
        ("pill", "Pill"), ("neon", "Neon"), ("cassette", "Cassette"), ("polaroid", "Polaroid"), ("broadcast", "Broadcast"),
    };
    public static readonly (string Id, string Name)[] Covers = { ("square", "Square"), ("canvas", "Canvas"), ("vinyl", "Vinyl"), ("none", "None|f") };

    public OverlaySource Source { get; }
    public ICommand CopyCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }

    public OverlaySourceRow(OverlaySource source, OverlaySourcesViewModel list)
    {
        Source = source;
        CopyCommand = new RelayCommand(() =>
        {
            if (!Ready()) return;
            Ui.CopyText(OverlayServer.Shared.LinkOf(Source));
            App.Host.Session?.Toast(L.T("Link copied: in OBS add a Browser source and paste it."));
        });
        // (a page in the browser, over a dotted background: it follows the settings while they change)
        PreviewCommand = new RelayCommand(() =>
        {
            if (Ready()) MainViewModel.OpenUrl(OverlayServer.Shared.LinkOf(Source) + "?preview");
        });
        EditCommand = new RelayCommand(() => Dialogs.EditOverlaySource(this));
        DeleteCommand = new RelayCommand(() => list.Delete(this));
    }

    public string Name => Source.Name;

    // In the settings' list: what its editor last showed (a picture kept from there, nothing runs for it), or the picture of its look.
    public string PictureFile => System.IO.Path.Combine(AppPaths.DataDir, "overlays", Source.Id + ".jpg");

    public object Picture
    {
        get
        {
            try
            {
                if (File.Exists(PictureFile))
                {
#if AVALONIA
                    using var file = File.OpenRead(PictureFile);
                    return Bitmap.DecodeToWidth(file, 200);
#else
                    var saved = new BitmapImage();
                    saved.BeginInit();
                    saved.CacheOption = BitmapCacheOption.OnLoad;
                    // (the same file with a new picture in it)
                    saved.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    saved.DecodePixelWidth = 200;
                    saved.UriSource = new Uri(PictureFile);
                    saved.EndInit();
                    saved.Freeze();
                    return saved;
#endif
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }
            return Dialogs.OverlayPicture(Source.Skin);
        }
    }

    // (it stands where the source puts it: the part of the picture that shows it)
    public AlignmentY PictureY => Source.AnchorY < 0.25 ? AlignmentY.Top : Source.AnchorY > 0.75 ? AlignmentY.Bottom : AlignmentY.Center;

    public void PictureChanged() => OnChanged(nameof(Picture), nameof(PictureY));

    // What a look has room for, so that the editor only shows what would change something: a light and a needle need a big cover
    // standing free; a look without cards has no opacity (the big bars and the canvas do), and without any frame no corners; the polaroid is always white.
    public bool HasCoverLight => Source.Skin is "compact" or "boxy" or "gallery" or "macos" or "shell" or "neon";
    public bool HasOpacity => Source.BarsBehind || (Source.Skin != "polaroid" && (Source.Skin != "neon" || Source.Cover == "canvas"));
    // (neon has only its cover to round, the TV's plates are cut sideways: a record has no corners)
    public bool HasCorners => Source.Skin switch
    {
        "neon" => Source.Cover is "square" or "canvas",
        "broadcast" => Source.Cover == "square",
        _ => true,
    };
    public bool HasTheme => Source.Skin != "polaroid";

    // The page as the editor shows it (null: the server can't start).
    public string? PreviewLink => Ready() ? OverlayServer.Shared.LinkOf(Source) + "?preview" : null;

    // The three tiles of the corners. As drawn is 1, and a capsule as drawn is as round as anything gets: square, half, all the way.
    public double CornersOf(int tile) => Source.Skin == "pill" ? tile / 2.0 : tile;
    public int CornersTile => (int)Math.Round(Source.Skin == "pill" ? Source.Corners * 2 : Source.Corners);

    public string Summary
    {
        get
        {
            string Named((string Id, string Name)[] all, string id) => L.T(all.FirstOrDefault(a => a.Id == id).Name ?? id);
            return Named(Looks, Source.Skin) + " · " + Named(Covers, Source.Cover);
        }
    }

    private static TimerKind? _saving;

    // Every page of this source (OBS's too) changes with it at once. Saved a moment later, once, not at each step of a slider or
    // letter typed: replacing the file and its .bak holds up the window, more with an antivirus looking at them.
    public void Changed()
    {
        if (_saving == null)
        {
            _saving = new TimerKind { Interval = TimeSpan.FromMilliseconds(600) };
            _saving.Tick += (_, _) =>
            {
                _saving.Stop();
                App.Host.Settings.Save();
            };
        }
        _saving.Stop();
        _saving.Start();
        OverlayServer.Shared.Changed(Source);
        OnChanged(nameof(Name), nameof(Summary));
    }

    private static bool Ready()
    {
        if (!OverlayServer.Shared.Running) OverlaySourcesViewModel.Apply();
        if (OverlayServer.Shared.Running) return true;
        App.Host.Session?.Toast(L.T("The overlays can't start: the ports they use are taken."));
        return false;
    }
}

public static partial class Dialogs
{
    private static readonly (string Id, string Name)[] OverlayMoves =
    {
        ("original", "Original"), ("fade", "Fade"), ("slide-left", "Slide: left"), ("slide-right", "Slide: right"),
        ("slide-up", "Slide: top"), ("slide-down", "Slide: bottom"), ("grow", "Grow"), ("shrink", "Shrink"),
        ("swing-left", "Swing: left"), ("swing-right", "Swing: right"), ("tilt-left", "Tilt: left"), ("tilt-right", "Tilt: right"),
    };
    private static readonly (string Id, string Name)[] OverlayColorWays = { ("cover", "From the cover"), ("rgb", "RGB"), ("one", "One color") };
    private static readonly string[] OverlayTints = { "#7C5CFF", "#FF4F8B", "#FF6B3D", "#F5B83D", "#1DB954", "#19C5C5", "#3D9BFF", "#E8E8F0" };
    private static readonly (string Id, string Name)[] OverlayWhen = { ("playing", "With the music"), ("change", "At every new song"), ("always", "Always") };
    private static readonly (string Id, string Name)[] OverlayTextPlaces = { ("left", "Left|place"), ("center", "Center|place"), ("right", "Right|place") };
    private static readonly (string Id, string Name)[] OverlayShapes = { ("0", "Bars"), ("1", "Bars with falling peaks"), ("2", "Blocks"), ("3", "Dots"), ("4", "One line"), ("5", "Filled wave") };
    private static readonly (string Id, string Name)[] OverlayFlows = { ("0", "Left to right"), ("1", "Right to left"), ("2", "Mirrored, bass in the center"), ("3", "Mirrored, bass at the sides") };
    private static readonly (string Id, string Name)[] OverlayCorners = { ("0", "Square|corners"), ("1", "Normal|corners"), ("2", "Round|corners") };
    private static readonly (string Id, string Name)[] OverlayInside = { ("bars", "Small bars"), ("line", "Progress line"), ("both", "Both|f"), ("none", "Neither|f") };
    private static readonly (string Id, string Name)[] OverlayBehind = { ("0", "Above the card"), ("1", "Below the card"), ("2", "Above and below"), ("3", "Through the middle") };
    private static readonly string[] OverlayPlaces = { "Top left", "Top|place", "Top right", "Left|place", "Center|place", "Right|place", "Bottom left", "Bottom|place", "Bottom right" };

    // Fonts whose letters are pictures: their names would come out as pictures, and so would the song's.
    private static readonly Regex OverlaySymbolFont = new("Wingdings|Webdings|^Symbol$|MDL2|Fluent Icons|Marlett|MT Extra|Bookshelf Symbol", RegexOptions.IgnoreCase);

    public static void EditOverlaySource(OverlaySourceRow row)
    {
        var win = Frame(L.T("Stream overlay"), OverlayEditor(row), OverlayEditorWidth(), Button(L.T("Done"), "PrimaryButton", isCancel: true));
        win.ShowDialog();
        // (what was still waiting to be saved)
        App.Host.Settings.Save();
    }

    // Everything takes effect as it's touched: with the preview open in the browser you watch it change. Four pages, like the library's
    // tabs, in place of one long list: what it looks like, its words, its bars, where and when it's on the screen.
    public static ElementKind OverlayEditor(OverlaySourceRow row)
    {
        var s = row.Source;
        // (from here on the small bars and the big ones each have their own shape)
        s.InsideStyle ??= s.Style;
        // (each look, cover or choice can change what makes sense further down)
        Action? fit = null;

        var copy = Button(L.T("Copy link"), "PrimaryButton");
        copy.Click += (_, _) => row.CopyCommand.Execute(null);
        var preview = Button(L.T("Preview"), "GhostButton");
        preview.Click += (_, _) => row.PreviewCommand.Execute(null);
        copy.Margin = preview.Margin = new Thickness(8, 0, 0, 0);
        var top = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        DockPanel.SetDock(preview, Dock.Right);
        top.Children.Add(copy);
        top.Children.Add(preview);
        top.Children.Add(OverlayBox(s.Name, v => { s.Name = v; row.Changed(); }));

        // ---- what it looks like
        var covers = OverlayTiles(4, "cover", OverlaySourceRow.Covers, s.Cover, OverlayThumb, v => { s.Cover = v; row.Changed(); fit?.Invoke(); });
        var coverHint = OverlayHint("Square: the cover beside the card. Canvas: the picture fills the whole card and moves (the song's video, muted and in time with it, when it has one). Vinyl: the cover on a spinning record.");
        coverHint.Margin = new Thickness(2, 8, 0, 12);
        var glow = OverlaySwitch("Light around the cover", null, s.Glow, v => { s.Glow = v; row.Changed(); });
        var pulse = OverlaySwitch("The cover beats with the bass", null, s.Pulse, v => { s.Pulse = v; row.Changed(); });
        var arm = OverlaySwitch("The needle on the record", null, s.Arm, v => { s.Arm = v; row.Changed(); });
        var colors = OverlayTiles(3, "colors", OverlayColorWays, s.Rgb ? "rgb" : s.MagicColors ? "cover" : "one", OverlayColorIcon, v =>
        {
            s.Rgb = v == "rgb";
            s.MagicColors = v == "cover";
            row.Changed();
            fit?.Invoke();
        });
        var noCover = OverlayHeading("For songs without a cover");
        var tint = OverlayTint(row);
        var rgbSpeed = OverlaySlider("RGB speed", ThemeColors.RgbMinSpeed, ThemeColors.RgbMaxSpeed, s.RgbSpeed, v => { s.RgbSpeed = v; row.Changed(); }, 0.5, v => $"{v:0.#}x");
        rgbSpeed.Margin = new Thickness(0, 12, 0, 0);
        var themeHeading = OverlayHeading("Background");
        var theme = OverlayTiles(2, "theme", new[] { ("dark", "Dark"), ("light", "Light") }, s.Light ? "light" : "dark", OverlayThemeIcon, v => { s.Light = v == "light"; row.Changed(); });
        var corners = OverlayTiles(3, "corners", OverlayCorners, row.CornersTile.ToString(), id => Pic.Icon(Pic.Rect(12, 6, 40, 24, id == "0" ? 0 : id == "1" ? 5 : 12), null), v => { s.Corners = row.CornersOf(int.Parse(v)); row.Changed(); });
        var opacityLine = OverlaySlider("Card opacity", 0.15, 1, s.Solid, v => { s.Solid = v; row.Changed(); });
        opacityLine.Margin = new Thickness(0, 12, 0, 0);
        var cardSection = OverlaySection("Card|overlay", null, corners, opacityLine);
        var look = OverlayPage(
            OverlaySection("Style", "The shape of the whole overlay.", OverlayTiles(4, "skin", OverlaySourceRow.Looks, s.Skin, OverlayThumb, v => { s.Skin = v; row.Changed(); fit?.Invoke(); })),
            OverlaySection("Cover", null, covers, coverHint, glow, pulse, arm),
            OverlaySection("Colors", "From the cover: they change with each song. RGB: all the colors, one after the other. One color: always the one you pick.", colors, noCover, tint, rgbSpeed, themeHeading, theme),
            cardSection);

        // ---- its words
        var nothingArtist = OverlayBox(s.NothingArtist, v => { s.NothingArtist = v; row.Changed(); });
        var font = OverlayHint("Any font on this computer. The Shell look keeps a terminal's font.");
        var words = OverlayPage(
            OverlaySection("What it shows", null,
                OverlaySwitch("Artist", null, s.ShowArtist, v => { s.ShowArtist = v; row.Changed(); }),
                OverlaySwitch("Time", null, s.ShowTime, v => { s.ShowTime = v; row.Changed(); })),
            OverlaySection("Title and artist", null, OverlayTiles(3, "text", OverlayTextPlaces, s.TextAlign, id => Pic.Icon(Pic.Rect(8, 6, 48, 24, 5), id switch
            {
                "center" => Pic.Rect(20, 12, 24, 4, 2) + Pic.Rect(24, 19, 16, 3, 1),
                "right" => Pic.Rect(27, 12, 24, 4, 2) + Pic.Rect(35, 19, 16, 3, 1),
                _ => Pic.Rect(13, 12, 24, 4, 2) + Pic.Rect(13, 19, 16, 3, 1),
            }), v => { s.TextAlign = v; row.Changed(); })),
            OverlaySection("Font", null, OverlayFontCombo(row), font),
            OverlaySection("When nothing is playing", "Shown only with “Always”. Both empty: it stays hidden.",
                OverlayLine("Title", OverlayWide(OverlayBox(s.NothingTitle, v => { s.NothingTitle = v; row.Changed(); }))),
                OverlayLine("Artist", OverlayWide(nothingArtist))));

        // ---- its bars: each kind has its shape
        string inside = s.Bars ? (s.ShowBar ? "both" : "bars") : s.ShowBar ? "line" : "none";
        var insideShapeHeading = OverlayHeading("Shape of the small bars");
        var insideShape = OverlayTiles(3, "shape-inside", OverlayShapes, ((int)s.InsideStyle.Value).ToString(), OverlayShapeIcon, v => { s.InsideStyle = (VisualizerStyle)int.Parse(v); row.Changed(); });
        var behindPlace = OverlayTiles(4, "behind", OverlayBehind, ((int)s.BarsPlace).ToString(), OverlayBehindIcon, v => { s.BarsPlace = (VisualizerPlace)int.Parse(v); row.Changed(); });
        var shapeHeading = OverlayHeading("Shape of the big bars");
        var shape = OverlayTiles(3, "shape", OverlayShapes, ((int)s.Style).ToString(), OverlayShapeIcon, v => { s.Style = (VisualizerStyle)int.Parse(v); row.Changed(); });
        var flowSection = OverlaySection("Direction of the bars", null, OverlayTiles(2, "flow", OverlayFlows, ((int)s.Flow).ToString(), OverlayFlowIcon, v => { s.Flow = (VisualizerFlow)int.Parse(v); row.Changed(); }));
        var bars = OverlayPage(
            OverlaySection("Inside the card", null,
                OverlayTiles(4, "inside", OverlayInside, inside, OverlayInsideIcon, v => { s.Bars = v is "bars" or "both"; s.ShowBar = v is "line" or "both"; row.Changed(); fit?.Invoke(); }),
                insideShapeHeading, insideShape),
            OverlaySection("Behind the card", null,
                OverlaySwitch("Big bars behind the card", null, s.BarsBehind, v => { s.BarsBehind = v; row.Changed(); fit?.Invoke(); }),
                behindPlace, shapeHeading, shape),
            flowSection);

        // ---- where and when it's on the screen
        static string Seconds(double v) => $"{v:0.#} s";
        var hideLine = OverlaySlider("With the music paused, hide it", 0, 30, s.HideAfter, v => { s.HideAfter = v; row.Changed(); }, 0.5, Seconds);
        var stayLine = OverlaySlider("It stays for", 1, 30, s.ShowFor, v => { s.ShowFor = v; row.Changed(); }, 0.5, Seconds);
        hideLine.Margin = stayLine.Margin = new Thickness(0, 12, 0, 0);
        void WhenChanged()
        {
            OverlayVisible(hideLine, s.Show == "playing");
            OverlayVisible(stayLine, s.Show == "change");
        }
        WhenChanged();
        var screen = OverlayPage(
            OverlaySection("Size and position", "They work inside the browser source: make the source as big as your stream and place the overlay from here.",
                OverlaySlider("Size", 0.3, 1, s.Size, v => { s.Size = v; row.Changed(); }),
                OverlayLine("Position", OverlayPlace(row))),
            OverlaySection("When it shows", null, OverlayTiles(3, "when", OverlayWhen, s.Show, null, v => { s.Show = v; row.Changed(); WhenChanged(); }), hideLine, stayLine),
            OverlaySection("When it appears", "Point at a choice to see it move.", OverlayMoveTiles("reveal", s.Reveal, true, v => { s.Reveal = v; row.Changed(); })),
            OverlaySection("When it goes away", null, OverlayMoveTiles("exit", s.Exit, false, v => { s.Exit = v; row.Changed(); })));

        fit = () =>
        {
            bool free = s.Cover is "square" or "vinyl";
            OverlayVisible(glow, free && row.HasCoverLight);
            OverlayVisible(pulse, free);
            OverlayVisible(arm, s.Cover == "vinyl" && row.HasCoverLight);
            // (the same corners going from a capsule to any other look, and back)
            s.Corners = s.Skin == "pill" ? Math.Min(s.Corners, 1) : s.Corners == 0.5 ? 1 : s.Corners;
            ((ToggleButton)corners.Children[row.CornersTile]).IsChecked = true;
            OverlayVisible(noCover, s.MagicColors && !s.Rgb);
            OverlayVisible(tint, !s.Rgb);
            OverlayVisible(rgbSpeed, s.Rgb);
            OverlayVisible(themeHeading, row.HasTheme);
            OverlayVisible(theme, row.HasTheme);
            OverlayVisible(cardSection, row.HasCorners || row.HasOpacity);
            OverlayVisible(corners, row.HasCorners);
            OverlayVisible(opacityLine, row.HasOpacity);
            OverlayVisible(insideShapeHeading, s.Bars);
            OverlayVisible(insideShape, s.Bars);
            OverlayVisible(behindPlace, s.BarsBehind);
            OverlayVisible(shapeHeading, s.BarsBehind);
            OverlayVisible(shape, s.BarsBehind);
            OverlayVisible(flowSection, s.Bars || s.BarsBehind);
        };
        fit();

        // The tabs over the pages, the same as the library's, and sliding the same way.
        var pages = new Views.AnimatedContent { Horizontal = true };
        var places = new List<object>();
        pages.PlaceOf = page => places.IndexOf(page);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, page) in new[] { ("Appearance", look), ("Text", words), ("Bars", bars), ("On screen", screen) })
        {
            bool first = tabs.Children.Count == 0;
            var tab = OverlayLook(new RadioButton { GroupName = "overlay-tabs", Content = L.T(key), IsChecked = first, Margin = new Thickness(first ? 0 : 4, 0, 0, 0) }, "TabSegment");
            var pageBox = new Grid { Margin = new Thickness(0, 0, 12, 0) };
            pageBox.Children.Add(page);
            var scroller = new ScrollViewer { Content = pageBox, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            places.Add(scroller);
            if (first) pages.Content = scroller;
            OverlayOnTick(tab, on =>
            {
                if (!on) return;
#if AVALONIA
                scroller.Offset = default;
#else
                scroller.ScrollToVerticalOffset(0);
#endif
                pages.Content = scroller;
            });
            tabs.Children.Add(tab);
        }
        var tabsPanel = new Grid();
        tabsPanel.Children.Add(new Views.SlidingPill { Target = tabs, Fill = OverlayBrush("AccentSoftBrush") });
        tabsPanel.Children.Add(tabs);
        var tabsBox = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(20), Padding = new Thickness(4), Margin = new Thickness(0, 14, 0, 14), Background = OverlayBrush("BgBrush"), Child = tabsPanel };
        var options = new Grid();
        options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        options.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        options.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(tabsBox, 1);
        Grid.SetRow(pages, 2);
        top.Margin = new Thickness(0, 0, 12, 0);
        options.Children.Add(top);
        options.Children.Add(tabsBox);
        options.Children.Add(pages);

        // The preview on the left, as big as there's room for; the options on the right.
        double width = OverlayEditorWidth(), height = OverlayEditorHeight(), optionsWidth = Math.Min(620, width * 0.55), gap = 16;
        var all = new Grid { Height = height };
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(optionsWidth) });
        var live = OverlayLive(row, width - optionsWidth - gap, height);
        live.Margin = new Thickness(0, 0, gap, 0);
        all.Children.Add(live);
        Grid.SetColumn(options, 1);
        all.Children.Add(options);
        return all;
    }

    // The overlay itself, moving, beside the options, whatever they scroll to; it changes as they're touched. From a browser of this
    // computer (OverlayPreview), only while the editor is open; the picture of the look until the first frame arrives. Without a browser
    // that picture stays, and a note says why: it's not the overlay that's broken.
    private static ElementKind OverlayLive(OverlaySourceRow row, double width, double height)
    {
        // (the small picture of the look at its own size; the frames fill the box)
        var picture = new Image { IsHitTestVisible = false, Stretch = Stretch.None, Source = OverlayPicture(row.Source.Skin) };
#if AVALONIA
        RenderOptions.SetBitmapInterpolationMode(picture, Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality);
#else
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
#endif
        var stage = new Grid();
        stage.Children.Add(picture);
        var box = new Border
        {
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x16)), Child = stage,
        };
        AutomationProperties.SetName(box, L.T("Preview"));
        void SayWhy() => stage.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(12),
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = L.T("The preview can't move: it needs Firefox, Chrome, Edge or Brave on this computer. The overlay itself works anyway, in OBS too."), FontSize = 12.5, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
        });
        OverlayPreview? live = null;
        int drawing = 0;
#if !AVALONIA
        WriteableBitmap? shown = null;
        var pixels = Array.Empty<byte>();
#endif
        void Start()
        {
            if (live != null || row.PreviewLink is not { } link) return;
            // (a page as big as the box: the look fits it by itself, and comes out as big as it'll go)
#if AVALONIA
            double scale = TopLevel.GetTopLevel(box)?.RenderScaling ?? 1;
#else
            double scale = VisualTreeHelper.GetDpi(box).DpiScaleX;
#endif
            live = new OverlayPreview(link, (int)width, (int)height, scale * Ui.Zoom, (bytes, size) =>
            {
#if AVALONIA
                // (still drawing the last one: this one is skipped, the next is newer anyway)
                if (Interlocked.CompareExchange(ref drawing, 1, 0) != 0) return;
                var frame = new Bitmap(new MemoryStream(bytes, 0, size));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var old = picture.Source as IDisposable;
                    picture.Stretch = Stretch.Uniform;
                    picture.Source = frame;
                    old?.Dispose();
                    drawing = 0;
                });
#else
                // One bitmap filled again and one array to carry the pixels over. A BitmapImage per picture tells the collector of its
                // megabytes each time, 15 to 20 full collections a second while the music plays: the lag of editing an overlay.
                // (still drawing the last one: this one is skipped, the next is newer anyway)
                if (Interlocked.CompareExchange(ref drawing, 1, 0) != 0) return;
                int w, h, stride;
                try
                {
                    using var jpeg = new MemoryStream(bytes, 0, size, false);
                    using var decoded = new System.Drawing.Bitmap(jpeg);
                    (w, h) = (decoded.Width, decoded.Height);
                    var locked = decoded.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                    stride = locked.Stride;
                    if (pixels.Length < stride * h) pixels = new byte[stride * h];
                    System.Runtime.InteropServices.Marshal.Copy(locked.Scan0, pixels, 0, stride * h);
                    decoded.UnlockBits(locked);
                }
                catch (ArgumentException)
                {
                    drawing = 0;
                    return;
                }
                picture.Dispatcher.BeginInvoke(() =>
                {
                    if (shown == null || shown.PixelWidth != w || shown.PixelHeight != h)
                    {
                        shown = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                        picture.Stretch = Stretch.Uniform;
                        picture.Source = shown;
                        // (the look's little picture is scaled once; these are new at each frame)
                        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.Linear);
                    }
                    shown.WritePixels(new Int32Rect(0, 0, w, h), pixels, stride, 0);
                    drawing = 0;
                });
#endif
            },
#if AVALONIA
            () => Avalonia.Threading.Dispatcher.UIThread.Post(SayWhy));
#else
            () => picture.Dispatcher.BeginInvoke(SayWhy));
#endif
        }
        void Stop()
        {
            // (the settings' list shows this source as it was left; without a browser, its look)
            try
            {
                if (live != null && !live.SaveLast(row.PictureFile)) File.Delete(row.PictureFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            row.PictureChanged();
            live?.Dispose();
            live = null;
        }
#if AVALONIA
        box.AttachedToVisualTree += (_, _) => Start();
        box.DetachedFromVisualTree += (_, _) => Stop();
#else
        box.Loaded += (_, _) => Start();
        box.Unloaded += (_, _) => Stop();
#endif
        return box;
    }

    // (it has to fit the screen with the frame, the title and the button around it)
    private static Size OverlayRoom()
    {
#if AVALONIA
        var top = App.Host?.Window;
        var screen = top?.Screens.ScreenFromWindow(top);
        return screen != null ? new Size(screen.WorkingArea.Width / screen.Scaling / Ui.Zoom, screen.WorkingArea.Height / screen.Scaling / Ui.Zoom) : new Size(1280, 800);
#else
        return new Size(SystemParameters.WorkArea.Width / Ui.Zoom, SystemParameters.WorkArea.Height / Ui.Zoom);
#endif
    }

    public static double OverlayEditorWidth() => Math.Clamp(OverlayRoom().Width - 100, 640, 1320);
    private static double OverlayEditorHeight() => Math.Clamp(OverlayRoom().Height - 190, 360, 760);

    private static IEnumerable<string> OverlayFonts()
    {
#if AVALONIA
        var all = FontManager.Current.SystemFonts.Select(f => f.Name);
#else
        var all = Fonts.SystemFontFamilies.Select(f => f.Source);
#endif
        return all.Where(n => !OverlaySymbolFont.IsMatch(n)).Distinct().OrderBy(n => n);
    }

    // Each font written in itself. Hundreds of them: only the ones in view are made, or opening the list would load them all.
    private static ComboBox OverlayFontCombo(OverlaySourceRow row)
    {
        var s = row.Source;
        var fonts = new[] { L.T("Default|m") }.Concat(OverlayFonts()).ToArray();
        var combo = OverlayCombo(fonts, Math.Max(0, Array.IndexOf(fonts, s.Font)), i => { s.Font = i == 0 ? "" : fonts[i]; row.Changed(); }, double.NaN);
#if AVALONIA
        combo.ItemTemplate = new FuncDataTemplate<string>((name, _) => new TextBlock { Text = name, FontSize = 15, FontFamily = name == fonts[0] ? FontFamily.Default : new FontFamily(name ?? "") });
#else
        var word = new FrameworkElementFactory(typeof(TextBlock));
        word.SetBinding(TextBlock.TextProperty, new Binding());
        word.SetBinding(TextBlock.FontFamilyProperty, new Binding());
        word.SetValue(TextBlock.FontSizeProperty, 15.0);
        combo.ItemTemplate = new DataTemplate { VisualTree = word };
        combo.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
        ScrollViewer.SetCanContentScroll(combo, true);
#endif
        return combo;
    }

    // (what a choice brings out or takes away unfolds; a line just built takes its place at once)
    private static void OverlayVisible(ElementKind element, bool on) => Views.Fold.SetOpen(element, on);

    private static BrushKind OverlayBrush(string key)
    {
#if AVALONIA
        return Res(key);
#else
        return (Brush)Application.Current.Resources[key];
#endif
    }

    private static T OverlayLook<T>(T control, string key) where T : Control
    {
#if AVALONIA
        control.Theme = Res<Avalonia.Styling.ControlTheme>(key);
#else
        control.Style = (Style)Application.Current.Resources[key];
#endif
        return control;
    }

    private static void OverlayOnTick(ToggleButton button, Action<bool> changed)
    {
#if AVALONIA
        button.IsCheckedChanged += (_, _) => changed(button.IsChecked == true);
#else
        button.Checked += (_, _) => changed(true);
        button.Unchecked += (_, _) => changed(false);
#endif
    }

    // A tab of the editor: what there is to decide, one thing at a time.
    private static StackPanel OverlayPage(params Border[] sections)
    {
        var page = new StackPanel();
        foreach (var section in sections) page.Children.Add(section);
        return page;
    }

    // One thing to decide, in a card of its own so that you can tell where it ends: its name, in a line what it's about when that isn't obvious, its choices.
    private static Border OverlaySection(string key, string? hintKey, params ElementKind[] inside)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T(key), FontSize = 15, FontWeight = FontWeights_SemiBold, Foreground = OverlayBrush("TextBrush"), Margin = new Thickness(2, 0, 0, hintKey == null ? 10 : 0) });
        if (hintKey != null)
        {
            var hint = OverlayHint(hintKey);
            hint.Margin = new Thickness(2, 2, 0, 10);
            panel.Children.Add(hint);
        }
        foreach (var part in inside) panel.Children.Add(part);
        return new Border { Background = OverlayBrush("Surface2Brush"), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 16), Margin = new Thickness(0, 0, 0, 12), Child = panel };
    }

    // (a box beside its name, as wide as a slider)
    private static ElementKind OverlayWide(ElementKind box)
    {
        box.Width = 270;
        return box;
    }

    private static TextBlock OverlayHeading(string key) => new()
    {
        Text = L.T(key), FontSize = 13, FontWeight = FontWeights_SemiBold, Foreground = OverlayBrush("SubTextBrush"), Margin = new Thickness(2, 20, 0, 8),
    };

    private static TextBlock OverlayHint(string key) => new()
    {
        Text = L.T(key), FontSize = 12, Foreground = OverlayBrush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0),
    };

#if AVALONIA
    private static FontWeight FontWeights_SemiBold => FontWeight.SemiBold;
#else
    private static FontWeight FontWeights_SemiBold => FontWeights.SemiBold;
#endif

    private static TextBox OverlayBox(string text, Action<string> changed)
    {
        var box = OverlayLook(new TextBox { Text = text }, "BoxTextBox");
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        return box;
    }

    private static CheckBox OverlaySwitch(string key, string? hintKey, bool on, Action<bool> changed)
    {
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = L.T(key) });
        if (hintKey != null) words.Children.Add(new TextBlock { Text = L.T(hintKey), FontSize = 12, Foreground = OverlayBrush("MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        var check = OverlayLook(new CheckBox { Content = words, IsChecked = on, Margin = new Thickness(0, 0, 0, 12) }, "Switch");
        OverlayOnTick(check, changed);
        return check;
    }

    private static ComboBox OverlayCombo(string[] items, int chosen, Action<int> changed, double width = 270)
    {
        var combo = new ComboBox { ItemsSource = items, SelectedIndex = chosen, Width = width, Height = 34, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
#if !AVALONIA
        // (the theme's template is for Choice items; these are plain words)
        combo.ItemTemplate = null;
#endif
        if (double.IsNaN(width)) combo.HorizontalAlignment = HorizontalAlignment.Stretch;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(combo.SelectedIndex); };
        return combo;
    }

    // In steps (of 5%, unless it says), its value beside it: a change at each step, not at each pixel.
    private static DockPanel OverlaySlider(string key, double min, double max, double value, Action<double> changed, double every = 0.05, Func<double, string>? format = null)
    {
        Func<double, string> Percent = format ?? (v => $"{Math.Round(v * 100)}%");
        value = Math.Clamp(value, min, max);
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = every, LargeChange = every, Value = value, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, L.T(key));
        var shown = new TextBlock { Text = Percent(value), MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        slider.ValueChanged += (_, _) =>
        {
            double step = Math.Round(Math.Round(slider.Value / every) * every, 3);
            if (step == value) return;
            value = step;
            shown.Text = Percent(step);
            changed(step);
        };
        var both = new DockPanel { Width = 270 };
        DockPanel.SetDock(shown, Dock.Right);
        both.Children.Add(shown);
        both.Children.Add(slider);
        return OverlayLine(key, both);
    }

    // Where it sits: nine little tiles, like the screen.
    private static UniformGrid OverlayPlace(OverlaySourceRow row)
    {
        var s = row.Source;
        var grid = new UniformGrid { Columns = 3, Width = 126, Margin = new Thickness(0, 0, -4, 0) };
        for (int i = 0; i < OverlayPlaces.Length; i++)
        {
            double x = i % 3 / 2.0, y = i / 3 / 2.0;
            var mark = new Border { Width = 18, Height = 10, CornerRadius = new CornerRadius(3), Background = OverlayBrush("BorderStrongBrush") };
            var pick = OverlayLook(new RadioButton { GroupName = "overlay-place", Content = mark, Padding = new Thickness(4), IsChecked = Math.Abs(x - s.AnchorX) < 0.1 && Math.Abs(y - s.AnchorY) < 0.1 }, "ChoiceTile");
            AutomationProperties.SetName(pick, L.T(OverlayPlaces[i]));
            OverlayOnTick(pick, on => { if (on) { s.AnchorX = x; s.AnchorY = y; row.Changed(); } });
            grid.Children.Add(pick);
        }
        return grid;
    }

    private static DockPanel OverlayLine(string key, ElementKind right)
    {
        var line = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(right, Dock.Right);
        line.Children.Add(right);
        line.Children.Add(new TextBlock { Text = L.T(key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        return line;
    }

    // Each button shows what it does (picture: a little drawing of it above its words).
    private static UniformGrid OverlayTiles(int columns, string group, (string Id, string Name)[] choices, string chosen, Func<string, ElementKind>? picture, Action<string> picked)
    {
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var (id, name) in choices)
        {
            ElementKind content = new TextBlock { Text = L.T(name), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
            if (picture != null)
            {
                var both = new StackPanel();
                both.Children.Add(picture(id));
                content.Margin = new Thickness(0, 6, 0, 0);
                both.Children.Add(content);
                content = both;
            }
            var tile = OverlayLook(new RadioButton { GroupName = "overlay-" + group, Content = content, IsChecked = id == chosen }, "ChoiceTile");
            OverlayOnTick(tile, on => { if (on) picked(id); });
            grid.Children.Add(tile);
        }
        return grid;
    }

    // ------------------------------------------------------------------ the little drawings on the buttons (64 x 36)

    private static ElementKind OverlayThemeIcon(string id) => Pic.LightOrDark(id == "light");

    private static ElementKind OverlayShapeIcon(string id) => Pic.BarShape((VisualizerStyle)int.Parse(id));

    private static ElementKind OverlayFlowIcon(string id) => Pic.Flow((VisualizerFlow)int.Parse(id));

    private static ElementKind OverlayInsideIcon(string id)
    {
        var marks = Pic.Rect(13, 11, 20, 4, 2) + Pic.Rect(13, 18, 14, 3, 1);
        if (id is "bars" or "both") marks += Pic.Columns(37, 5, 3, 2, 21, -1, new[] { 5, 9, 6, 10, 4 });
        if (id is "line" or "both") marks += Pic.Rect(13, 25, 38, 2, 1);
        return Pic.Icon(Pic.Rect(8, 5, 48, 26, 5), marks);
    }

    private static ElementKind OverlayBehindIcon(string id)
    {
        string card = Pic.Rect(12, id == "0" ? 18 : id == "1" ? 4 : 12, 40, 14, 3), marks = id switch
        {
            "0" => Pic.Columns(14, 12, 3, 2, 18, -1, Pic.Sample),
            "1" => Pic.Columns(14, 12, 3, 2, 18, 1, Pic.Sample),
            "2" => Pic.Columns(14, 12, 3, 2, 12, -1, Pic.Sample.Select(h => h * 3 / 4)) + Pic.Columns(14, 12, 3, 2, 26, 1, Pic.Sample.Select(h => h * 3 / 4)),
            // (through the middle: only at its sides)
            _ => Pic.Columns(2, 3, 3, 2, 18, -1, new[] { 8, 13, 10 }) + Pic.Columns(2, 3, 3, 2, 18, 1, new[] { 8, 13, 10 }) + Pic.Columns(55, 3, 3, 2, 18, -1, new[] { 10, 13, 8 }) + Pic.Columns(55, 3, 3, 2, 18, 1, new[] { 10, 13, 8 }),
        };
        return Pic.Icon(card, marks);
    }

    // The ways in and out, each button moving a little card the way the real one moves (Overlay.html ANIM): when the mouse comes over
    // it and when it's picked; at rest nothing runs. By the frames of the screen.
    private static UniformGrid OverlayMoveTiles(string group, string chosen, bool entering, Action<string> picked)
    {
        var plays = new List<Action>();
        var grid = OverlayTiles(4, group, OverlayMoves, chosen, id =>
        {
            // (going away "grow" grows and "shrink" shrinks: the motion seen, as on the page)
            string kind = entering ? id : id switch { "grow" => "shrink", "shrink" => "grow", _ => id };
            var card = new Border { Width = 28, Height = 16, CornerRadius = new CornerRadius(4), Background = OverlayBrush("AccentTextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var scale = new ScaleTransform();
            var turn = new RotateTransform();
            var move = new TranslateTransform();
            var all = new TransformGroup();
            all.Children.Add(scale);
            all.Children.Add(turn);
            all.Children.Add(move);
            card.RenderTransform = all;
            double originX = kind == "swing-left" ? 0 : kind == "swing-right" ? 1 : 0.5;
#if AVALONIA
            card.RenderTransformOrigin = new RelativePoint(originX, 0.5, RelativeUnit.Relative);
#else
            card.RenderTransformOrigin = new Point(originX, 0.5);
#endif
            // in: 1 in its place, 0 where it comes from
            void Place(double inPlace)
            {
                double away = 1 - inPlace;
                card.Opacity = inPlace;
                move.X = kind == "slide-left" ? -26 * away : kind == "slide-right" ? 26 * away : 0;
                move.Y = kind switch { "slide-up" => -16 * away, "slide-down" => 16 * away, "tilt-left" or "tilt-right" => 8 * away, "original" => 7 * away, _ => 0 };
                double size = kind switch { "grow" => 0.4 + 0.6 * inPlace, "shrink" => 1.5 - 0.5 * inPlace, "original" => 0.95 + 0.05 * inPlace, _ => 1 };
                scale.ScaleX = kind is "swing-left" or "swing-right" ? Math.Max(0.02, inPlace) : size;
                scale.ScaleY = size;
                turn.Angle = kind == "tilt-left" ? -14 * away : kind == "tilt-right" ? 14 * away : 0;
            }
            int running = 0;
            plays.Add(() =>
            {
                // (the mouse comes over it, then it's picked: only the last one moves it)
                int mine = ++running;
                OverlayRun(card, entering ? 560 : 420, t =>
                {
                    if (mine != running) return;
                    // (out: gone by the end, then back in its place, the button at rest)
                    double eased = 1 - Math.Pow(1 - t, 3);
                    Place(t >= 1 ? 1 : entering ? eased : 1 - eased);
                });
            });
            return new Grid { Width = 64, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false, Children = { card } };
        }, picked);
        for (int i = 0; i < grid.Children.Count; i++)
        {
            var tile = (RadioButton)grid.Children[i];
            var play = plays[i];
#if AVALONIA
            tile.PointerEntered += (_, _) => play();
#else
            tile.MouseEnter += (_, _) => play();
#endif
            OverlayOnTick(tile, on => { if (on) play(); });
        }
        return grid;
    }

    // step(0..1) at every frame of the screen for ms (at the speed of the app's animations; without animations: at once).
    private static void OverlayRun(ElementKind on, double ms, Action<double> step)
    {
        if (!Ui.Animations)
        {
            step(1);
            return;
        }
#if AVALONIA
        Ui.Tween(on, ms, t => t, step);
#else
        double total = Ui.Time(ms).TotalMilliseconds;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / total);
            step(t);
            if (t >= 1) CompositionTarget.Rendering -= frame;
        };
        CompositionTarget.Rendering += frame;
#endif
    }

    // A real picture of each look and cover (Assets/overlay, made from the real pages; a song and a cover of ours).
    private static Image OverlayThumb(string id)
    {
        var picture = new Image { Width = 120, Height = 74, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, Source = OverlayPicture(id) };
#if AVALONIA
        RenderOptions.SetBitmapInterpolationMode(picture, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
#else
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
#endif
        return picture;
    }

#if AVALONIA
    public static Bitmap OverlayPicture(string id) => new(AssetLoader.Open(new Uri($"avares://UltimateMP3Player/Assets/overlay/{id}.png")));
#else
    public static BitmapImage OverlayPicture(string id) => new(new Uri($"pack://application:,,,/UltimateMP3Player;component/Assets/overlay/{id}.png"));
#endif

    private static ElementKind OverlayColorIcon(string id)
    {
        string card = Pic.Rect(8, 6, 48, 24, 5);
        return id switch
        {
            "rgb" => Pic.Icon(card, null, false, Pic.Rainbow()),
            "one" => Pic.Icon(card, null, false, OverlayBrush("AccentTextBrush")),
            _ => Pic.Icon(card, Pic.Rect(13, 10, 16, 16, 3) + Pic.Rect(33, 13, 18, 3, 1) + Pic.Rect(33, 19, 12, 3, 1)),
        };
    }

    // The color: a bar of all of them to press or drag, a few ready, or any by its code.
    private static ElementKind OverlayTint(OverlaySourceRow row)
    {
        var s = row.Source;
        var all = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        // An invisible slider over the rainbow: the mouse and the keys work as on every other one, a ring shows where it is.
        double start = ThemeColors.HueOf(s.Tint);
        var before = new ColumnDefinition { Width = new GridLength(start, GridUnitType.Star) };
        var after = new ColumnDefinition { Width = new GridLength(359 - start, GridUnitType.Star) };
        var ring = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(3), BorderBrush = Brushes.White, IsHitTestVisible = false };
        var bar = new Grid { Height = 26, Margin = new Thickness(0, 0, 0, 10) };
        bar.ColumnDefinitions.Add(before);
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(after);
        var rainbow = new Border { Height = 14, CornerRadius = new CornerRadius(7), Background = Pic.Rainbow(), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var hue = new Slider { Minimum = 0, Maximum = 359, SmallChange = 2, LargeChange = 10, Value = start, Height = 26, Opacity = 0, Focusable = true };
        AutomationProperties.SetName(hue, L.T("Hue"));
        Grid.SetColumnSpan(rainbow, 3);
        Grid.SetColumnSpan(hue, 3);
        Grid.SetColumn(ring, 1);
        bar.Children.Add(rainbow);
        bar.Children.Add(ring);
        bar.Children.Add(hue);
        all.Children.Add(bar);
        void Place(double h)
        {
            before.Width = new GridLength(h, GridUnitType.Star);
            after.Width = new GridLength(359 - h, GridUnitType.Star);
            ring.Background = new SolidColorBrush(Pic.ColorOf(ThemeColors.FromHue(h)));
        }
        Place(start);

        var line = new DockPanel();
        var code = OverlayLook(new TextBox { Text = s.Tint, Width = 104, VerticalAlignment = VerticalAlignment.Center }, "BoxTextBox");
        DockPanel.SetDock(code, Dock.Right);
        line.Children.Add(code);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 0, 0, 0) };
        line.Children.Add(swatches);
        all.Children.Add(line);

        // (each of the three shows what the others chose, without choosing it again)
        bool showing = false;
        var picks = new List<(RadioButton Pick, string Tint)>();
        void Choose(string tint, object from)
        {
            showing = true;
            s.Tint = tint;
            row.Changed();
            if (from != code) code.Text = tint;
            if (from != hue)
            {
                hue.Value = ThemeColors.HueOf(tint);
                Place(hue.Value);
            }
            foreach (var (pick, value) in picks) pick.IsChecked = string.Equals(value, tint, StringComparison.OrdinalIgnoreCase);
            showing = false;
        }
        hue.ValueChanged += (_, _) =>
        {
            if (showing) return;
            double h = Math.Round(hue.Value);
            Place(h);
            Choose(ThemeColors.FromHue(h), hue);
        };
        code.TextChanged += (_, _) =>
        {
            if (!showing && ThemeColors.ColorFrom(code.Text) is { } typed) Choose(typed, code);
        };
        foreach (var tint in OverlayTints)
        {
            var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Pic.ColorOf(tint)) };
            var pick = OverlayLook(new RadioButton { GroupName = "overlay-tint", Content = dot, Padding = new Thickness(5), IsChecked = string.Equals(tint, s.Tint, StringComparison.OrdinalIgnoreCase) }, "ChoiceTile");
            OverlayOnTick(pick, on => { if (on && !showing) Choose(tint, pick); });
            picks.Add((pick, tint));
            swatches.Children.Add(pick);
        }
        return all;
    }
}
