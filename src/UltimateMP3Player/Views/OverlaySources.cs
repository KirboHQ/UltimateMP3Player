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
#endif
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

    // What a look has room for, so that the editor only shows what would change something: a light and a needle need a big cover
    // standing free; a look without cards has no opacity, and without any frame no corners; the polaroid is always white.
    public bool HasCoverLight => Source.Skin is "compact" or "boxy" or "gallery" or "macos" or "shell" or "neon";
    public bool HasOpacity => Source.Skin is not ("neon" or "polaroid");
    public bool HasCorners => Source.Skin != "neon";
    public bool HasTheme => Source.Skin != "polaroid";
    public bool HasCanvas => Source.Skin is not ("pill" or "cassette" or "polaroid" or "broadcast");

    // The page as the editor shows it (null: the server can't start).
    public string? PreviewLink => Ready() ? OverlayServer.Shared.LinkOf(Source) + "?preview" : null;

    public string Summary
    {
        get
        {
            string Named((string Id, string Name)[] all, string id) => L.T(all.FirstOrDefault(a => a.Id == id).Name ?? id);
            return Named(Looks, Source.Skin) + " · " + Named(Covers, Source.Cover);
        }
    }

    // Saved at once, and every page of this source (OBS's too) changes with it.
    public void Changed()
    {
        App.Host.Settings.Save();
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
    private static readonly string[] OverlayTints = { "#7C5CFF", "#FF4F8B", "#FF6B3D", "#F5B83D", "#1DB954", "#19C5C5", "#3D9BFF", "#E8E8F0" };
    private static readonly (string Id, string Name)[] OverlayWhen = { ("playing", "With the music"), ("change", "At every new song"), ("always", "Always") };
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
    }

    // Everything takes effect as it's touched: with the preview open in the browser you watch it change.
    public static ElementKind OverlayEditor(OverlaySourceRow row)
    {
        var s = row.Source;
        var body = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
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
        body.Children.Add(top);

        body.Children.Add(OverlayHeading("Appearance"));
        body.Children.Add(OverlayTiles(4, "skin", OverlaySourceRow.Looks, s.Skin, true, v => { s.Skin = v; row.Changed(); fit?.Invoke(); }));

        body.Children.Add(OverlayHeading("Cover"));
        var glow = OverlaySwitch("Light around the cover", null, s.Glow, v => { s.Glow = v; row.Changed(); });
        var pulse = OverlaySwitch("The cover beats with the bass", null, s.Pulse, v => { s.Pulse = v; row.Changed(); });
        var arm = OverlaySwitch("The needle on the record", null, s.Arm, v => { s.Arm = v; row.Changed(); });
        var covers = OverlayTiles(4, "cover", OverlaySourceRow.Covers, s.Cover, true, v => { s.Cover = v; row.Changed(); fit?.Invoke(); });
        body.Children.Add(covers);
        var coverHint = OverlayHint("Square: the cover beside the card. Canvas: the picture fills the whole card and moves (the song's video, muted and in time with it, when it has one). Vinyl: the cover on a spinning record.");
        coverHint.Margin = new Thickness(2, 8, 0, 12);
        body.Children.Add(coverHint);
        body.Children.Add(glow);
        body.Children.Add(pulse);
        body.Children.Add(arm);

        body.Children.Add(OverlayHeading("What it shows"));
        body.Children.Add(OverlaySwitch("Artist", null, s.ShowArtist, v => { s.ShowArtist = v; row.Changed(); }));
        body.Children.Add(OverlaySwitch("Time", null, s.ShowTime, v => { s.ShowTime = v; row.Changed(); }));

        body.Children.Add(OverlayHeading("Size and position"));
        body.Children.Add(OverlaySlider("Size", 0.3, 1, s.Size, v => { s.Size = v; row.Changed(); }));
        body.Children.Add(OverlayLine("Position", OverlayPlace(row)));
        var corners = OverlayTiles(3, "corners", OverlayCorners, s.Corners.ToString("0"), false, v => { s.Corners = int.Parse(v); row.Changed(); });
        corners.Width = 278;
        var cornersLine = OverlayLine("Corners", corners);
        body.Children.Add(cornersLine);
        var opacityLine = OverlaySlider("Card opacity", 0.15, 1, s.Solid, v => { s.Solid = v; row.Changed(); });
        body.Children.Add(opacityLine);
        body.Children.Add(OverlayHint("Size and position work inside the browser source: make the source as big as your stream and place the overlay from here."));

        body.Children.Add(OverlayHeading("Colors"));
        body.Children.Add(OverlaySwitch("Colors from the cover", "Otherwise, and for songs without a cover, the tint below.", s.MagicColors, v => { s.MagicColors = v; row.Changed(); }));
        var theme = OverlayTiles(2, "theme", new[] { ("dark", "Dark"), ("light", "Light") }, s.Light ? "light" : "dark", false, v => { s.Light = v == "light"; row.Changed(); });
        body.Children.Add(theme);
        body.Children.Add(OverlayTint(row));

        body.Children.Add(OverlayHeading("Inside the card"));
        string inside = s.Bars ? (s.ShowBar ? "both" : "bars") : s.ShowBar ? "line" : "none";
        body.Children.Add(OverlayTiles(4, "inside", OverlayInside, inside, false, v => { s.Bars = v is "bars" or "both"; s.ShowBar = v is "line" or "both"; row.Changed(); fit?.Invoke(); }));

        body.Children.Add(OverlayHeading("Behind the card"));
        var behindPlace = OverlayTiles(4, "behind", OverlayBehind, ((int)s.BarsPlace).ToString(), false, v => { s.BarsPlace = (VisualizerPlace)int.Parse(v); row.Changed(); });
        behindPlace.Margin = new Thickness(-4, 0, -4, 12);
        body.Children.Add(OverlaySwitch("Big bars behind the card", null, s.BarsBehind, v => { s.BarsBehind = v; row.Changed(); fit?.Invoke(); }));
        body.Children.Add(behindPlace);
        var shape = OverlayLine("Shape of the bars", OverlayCombo(new[] { L.T("Bars"), L.T("Bars with falling peaks"), L.T("Blocks"), L.T("Dots"), L.T("One line"), L.T("Filled wave") }, (int)s.Style, i => { s.Style = (VisualizerStyle)i; row.Changed(); }));
        var flow = OverlayLine("Direction of the bars", OverlayCombo(new[] { L.T("Left to right"), L.T("Right to left"), L.T("Mirrored, bass in the center"), L.T("Mirrored, bass at the sides") },
            (int)s.Flow, i => { s.Flow = (VisualizerFlow)i; row.Changed(); }));
        body.Children.Add(shape);
        body.Children.Add(flow);

        body.Children.Add(OverlayHeading("When it shows"));
        var waits = new[] { 3, 5, 10, 30 };
        var hideLine = OverlayLine("With the music paused, hide it", OverlayCombo(waits.Select(w => L.F("After {0} seconds", w)).ToArray(),
            Math.Max(0, Array.IndexOf(waits, s.HideAfter)), i => { s.HideAfter = waits[i]; row.Changed(); }));
        var stays = new[] { 5, 8, 15, 30 };
        var stayLine = OverlayLine("It stays for", OverlayCombo(stays.Select(w => L.F("{0} seconds", w)).ToArray(),
            Math.Max(0, Array.IndexOf(stays, s.ShowFor)), i => { s.ShowFor = stays[i]; row.Changed(); }));
        hideLine.Margin = stayLine.Margin = new Thickness(0, 10, 0, 0);
        void WhenChanged()
        {
            OverlayVisible(hideLine, s.Show == "playing");
            OverlayVisible(stayLine, s.Show == "change");
        }
        WhenChanged();
        body.Children.Add(OverlayTiles(3, "when", OverlayWhen, s.Show, false, v => { s.Show = v; row.Changed(); WhenChanged(); }));
        body.Children.Add(hideLine);
        body.Children.Add(stayLine);

        body.Children.Add(OverlayHeading("When it appears"));
        body.Children.Add(OverlayTiles(4, "reveal", OverlayMoves, s.Reveal, false, v => { s.Reveal = v; row.Changed(); }));
        body.Children.Add(OverlayHeading("When it goes away"));
        body.Children.Add(OverlayTiles(4, "exit", OverlayMoves, s.Exit, false, v => { s.Exit = v; row.Changed(); }));

        body.Children.Add(OverlayHeading("Font"));
        body.Children.Add(OverlayFontCombo(row));
        body.Children.Add(OverlayHint("Any font on this computer. The Shell look keeps a terminal's font."));

        body.Children.Add(OverlayHeading("When nothing is playing"));
        body.Children.Add(OverlayBox(s.NothingTitle, v => { s.NothingTitle = v; row.Changed(); }));
        var artist = OverlayBox(s.NothingArtist, v => { s.NothingArtist = v; row.Changed(); });
        artist.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(artist);
        body.Children.Add(OverlayHint("Shown only with “Always”. Both empty: it stays hidden."));

        fit = () =>
        {
            // (a canvas where a look can't have it counts as the square it's shown as)
            var cover = s.Cover == "canvas" && !row.HasCanvas ? "square" : s.Cover;
            OverlayVisible((ElementKind)covers.Children[1], row.HasCanvas);
            bool free = cover is "square" or "vinyl";
            OverlayVisible(glow, free && row.HasCoverLight);
            OverlayVisible(pulse, free);
            OverlayVisible(arm, cover == "vinyl" && row.HasCoverLight);
            OverlayVisible(cornersLine, row.HasCorners);
            OverlayVisible(opacityLine, row.HasOpacity);
            OverlayVisible(theme, row.HasTheme);
            OverlayVisible(behindPlace, s.BarsBehind);
            OverlayVisible(shape, s.Bars || s.BarsBehind);
            OverlayVisible(flow, s.Bars || s.BarsBehind);
        };
        fit();

        // The preview on the left, as big as there's room for; the options on the right.
        double width = OverlayEditorWidth(), height = OverlayEditorHeight(), options = Math.Min(620, width * 0.55), gap = 16;
        var all = new Grid { Height = height };
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        all.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(options) });
        var live = OverlayLive(row, width - options - gap, height);
        live.Margin = new Thickness(0, 0, gap, 0);
        all.Children.Add(live);
        var scroller = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroller, 1);
        all.Children.Add(scroller);
        return all;
    }

    // The overlay itself, moving, beside the options, whatever they scroll to; it changes as they're touched. From Edge or Chrome
    // (OverlayPreview), only while the editor is open; the picture of the look until the first frame arrives, or when there's no such browser.
    private static ElementKind OverlayLive(OverlaySourceRow row, double width, double height)
    {
        // (the small picture of the look at its own size; the frames fill the box)
        var picture = new Image { IsHitTestVisible = false, Stretch = Stretch.None, Source = OverlayPicture(row.Source.Skin) };
#if AVALONIA
        RenderOptions.SetBitmapInterpolationMode(picture, Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality);
#else
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
#endif
        var box = new Border
        {
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x16)), Child = picture,
        };
        AutomationProperties.SetName(box, L.T("Preview"));
        OverlayPreview? live = null;
        void Start()
        {
            if (live != null || row.PreviewLink is not { } link) return;
            // (a page as big as the box: the look fits it by itself, and comes out as big as it'll go)
            live = new OverlayPreview(link, (int)width, (int)height, bytes =>
            {
#if AVALONIA
                var frame = new Bitmap(new MemoryStream(bytes));
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    var old = picture.Source as IDisposable;
                    picture.Stretch = Stretch.Uniform;
                    picture.Source = frame;
                    old?.Dispose();
                });
#else
                var frame = new BitmapImage();
                frame.BeginInit();
                frame.CacheOption = BitmapCacheOption.OnLoad;
                frame.StreamSource = new MemoryStream(bytes);
                frame.EndInit();
                frame.Freeze();
                picture.Dispatcher.BeginInvoke(() =>
                {
                    picture.Stretch = Stretch.Uniform;
                    picture.Source = frame;
                });
#endif
            });
        }
        void Stop()
        {
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

    private static void OverlayVisible(ElementKind element, bool on)
    {
#if AVALONIA
        element.IsVisible = on;
#else
        element.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
#endif
    }

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

    // In steps of 5%, its value beside it: saved at each step, not at each pixel.
    private static DockPanel OverlaySlider(string key, double min, double max, double value, Action<double> changed)
    {
        static string Percent(double v) => $"{Math.Round(v * 100)}%";
        value = Math.Clamp(value, min, max);
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = 0.05, LargeChange = 0.05, Value = value, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, L.T(key));
        var shown = new TextBlock { Text = Percent(value), MinWidth = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        slider.ValueChanged += (_, _) =>
        {
            double step = Math.Round(slider.Value * 20) / 20;
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

    private static UniformGrid OverlayTiles(int columns, string group, (string Id, string Name)[] choices, string chosen, bool pictures, Action<string> picked)
    {
        var grid = new UniformGrid { Columns = columns, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (var (id, name) in choices)
        {
            ElementKind content = new TextBlock { Text = L.T(name), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (pictures)
            {
                var both = new StackPanel();
                both.Children.Add(OverlayThumb(id));
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
    private static Bitmap OverlayPicture(string id) => new(AssetLoader.Open(new Uri($"avares://UltimateMP3Player/Assets/overlay/{id}.png")));
#else
    private static BitmapImage OverlayPicture(string id) => new(new Uri($"pack://application:,,,/UltimateMP3Player;component/Assets/overlay/{id}.png"));
#endif

    // The tint: a few to pick, or any colour by its code.
    private static ElementKind OverlayTint(OverlaySourceRow row)
    {
        var s = row.Source;
        var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var code = OverlayLook(new TextBox { Text = s.Tint, Width = 104, VerticalAlignment = VerticalAlignment.Center }, "BoxTextBox");
        code.TextChanged += (_, _) =>
        {
            var typed = (code.Text ?? "").Trim();
            if (!Regex.IsMatch(typed, "^#?[0-9a-fA-F]{6}$")) return;
            s.Tint = typed.StartsWith('#') ? typed : "#" + typed;
            row.Changed();
        };
        DockPanel.SetDock(code, Dock.Right);
        line.Children.Add(code);
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 0, 0, 0) };
        foreach (var tint in OverlayTints)
        {
            int n = Convert.ToInt32(tint[1..], 16);
            var dot = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n)) };
            var pick = OverlayLook(new RadioButton { GroupName = "overlay-tint", Content = dot, Padding = new Thickness(5), IsChecked = string.Equals(tint, s.Tint, StringComparison.OrdinalIgnoreCase) }, "ChoiceTile");
            OverlayOnTick(pick, on => { if (on) code.Text = tint; });
            swatches.Children.Add(pick);
        }
        line.Children.Add(swatches);
        return line;
    }
}
