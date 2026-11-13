#if AVALONIA
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using BrushKind = Avalonia.Media.IBrush;
using ElementKind = Avalonia.Controls.Control;
using ShapePath = Avalonia.Controls.Shapes.Path;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using BrushKind = System.Windows.Media.Brush;
using ElementKind = System.Windows.FrameworkElement;
using ShapePath = System.Windows.Shapes.Path;
#endif
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// A choice of a few, all in sight, as the stream overlay's editor draws them: a tile each, the chosen one outlined; with Picture, a
// little drawing of what it does above its words; with Numbers, only its value (a row of 1 2 3...). A list that changes (the
// outputs, the profiles) stays a drop-down. Linked into Avalonia and the phones.
public sealed class ChoiceTiles : UniformGrid
{
#if AVALONIA
    public static readonly StyledProperty<IEnumerable<Choice>?> ItemsProperty = AvaloniaProperty.Register<ChoiceTiles, IEnumerable<Choice>?>(nameof(Items));
    public static readonly StyledProperty<Choice?> SelectedProperty = AvaloniaProperty.Register<ChoiceTiles, Choice?>(nameof(Selected), defaultBindingMode: BindingMode.TwoWay);

    public IEnumerable<Choice>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public Choice? Selected { get => GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty) Build();
        else if (change.Property == SelectedProperty) Mark();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        Build();
    }
#else
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items), typeof(IEnumerable<Choice>), typeof(ChoiceTiles),
        new PropertyMetadata(null, (d, _) => ((ChoiceTiles)d).Build()));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(Choice), typeof(ChoiceTiles),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ChoiceTiles)d).Mark()));

    public IEnumerable<Choice>? Items { get => (IEnumerable<Choice>?)GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public Choice? Selected { get => (Choice?)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Build();
    }
#endif

    // "shape", "flow"... (TilePictures.For), none: only words.
    public string? Picture { get; set; }
    public bool Numbers { get; set; }

    // How wide a tile wants to be (with its gap): as many in a row as fit, so all of them on a wide card, two rows on a narrow one.
    public double TileWidth { get; set; }

    private readonly List<(RadioButton Tile, Choice Choice)> _tiles = new();
    private bool _marking;

    public ChoiceTiles()
    {
        Margin = new Thickness(-4, 0, -4, 0);
    }

    // All in a row if they fit, otherwise two even rows; only a very narrow place takes more.
    public static int ColumnsFor(double width, double tile, int count)
    {
        int fit = Math.Max(1, (int)(width / tile));
        if (fit >= count) return Math.Max(1, count);
        int half = (count + 1) / 2;
        return half <= fit ? half : fit;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (!double.IsInfinity(available.Width))
        {
            int columns = ColumnsFor(available.Width, TileWidth > 0 ? TileWidth : Numbers ? 38 : 96, Children.Count);
            if (columns != Columns) Columns = columns;
        }
        return base.MeasureOverride(available);
    }

    private void Build()
    {
        // (Picture and Numbers are set in the XAML's order: all of them are there once it's initialized)
        if (!IsInitialized) return;
        Children.Clear();
        _tiles.Clear();
        foreach (var choice in Items ?? Enumerable.Empty<Choice>())
        {
            // (narrow tiles keep their room for the words: a word never breaks in two)
            double side = Numbers ? 0 : TileWidth is > 0 and < 96 ? 2 : 6;
            var tile = new RadioButton { Content = Content(choice), Padding = new Thickness(side, 9, side, 9) };
#if AVALONIA
            tile.Theme = Ui.Theme("ChoiceTile");
            tile.IsCheckedChanged += (_, _) => Picked(tile, choice);
#else
            tile.Style = (Style)Application.Current.Resources["ChoiceTile"];
            tile.Checked += (_, _) => Picked(tile, choice);
#endif
            AutomationProperties.SetName(tile, choice.Label);
#if AVALONIA
            if (Numbers) ToolTip.SetTip(tile, choice.Label);
#else
            if (Numbers) tile.ToolTip = choice.Label;
#endif
            _tiles.Add((tile, choice));
            Children.Add(tile);
        }
        Mark();
    }

    private ElementKind Content(Choice choice)
    {
        var words = new StackPanel();
        words.Children.Add(new TextBlock { Text = Numbers ? choice.Value?.ToString() : choice.Label, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (!Numbers && !string.IsNullOrEmpty(choice.Hint))
        {
            var hint = new TextBlock { Text = choice.Hint, FontSize = 11, FontWeight = TilePictures.Normal, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 0) };
            TilePictures.Paint(hint, "MutedBrush");
            words.Children.Add(hint);
        }
        if (TilePictures.For(Picture, choice.Value) is not { } picture) return words;
        var both = new StackPanel();
        both.Children.Add(picture);
        words.Margin = new Thickness(0, 6, 0, 0);
        both.Children.Add(words);
        return both;
    }

    private void Mark()
    {
        _marking = true;
        foreach (var (tile, choice) in _tiles) tile.IsChecked = Selected != null && Equals(choice.Value, Selected.Value);
        _marking = false;
    }

    private void Picked(RadioButton tile, Choice choice)
    {
        if (_marking || tile.IsChecked != true || (Selected != null && Equals(choice.Value, Selected.Value))) return;
        Selected = choice;
    }
}

// The little drawings on the tiles (64 x 36), the same in Settings and in the overlay editor: what a choice is about in the accent's colour, the rest in a quiet one.
public static class TilePictures
{
#if AVALONIA
    public static readonly FontWeight Normal = FontWeight.Normal;
    public static BrushKind Brush(string key) => Ui.Res(key);
    private static void Fill(ShapePath path, string key) => path.Fill = Ui.Res(key);
    private static void Stroke(ShapePath path, string key) => path.Stroke = Ui.Res(key);
    public static void Paint(TextBlock text, string key) => text.Foreground = Ui.Res(key);
#else
    public static readonly FontWeight Normal = FontWeights.Normal;
    public static BrushKind Brush(string key) => (BrushKind)Application.Current.Resources[key];
    // (by reference: going from light to dark changes the app's brushes, with these on the screen)
    private static void Fill(ShapePath path, string key) => path.SetResourceReference(Shape.FillProperty, key);
    private static void Stroke(ShapePath path, string key) => path.SetResourceReference(Shape.StrokeProperty, key);
    public static void Paint(TextBlock text, string key) => text.SetResourceReference(TextBlock.ForegroundProperty, key);
#endif

    public static ElementKind? For(string? kind, object? value) => kind switch
    {
        "shape" => BarShape((VisualizerStyle)value!),
        "flow" => Flow((VisualizerFlow)value!),
        "place" => Place((VisualizerPlace)value!),
        "tint" => Tint((VisualizerTint)value!),
        "thickness" => BarThickness((int)value!),
        "look" => Look((string)value!),
        "close" => Close((string)value!),
        "searchmode" => SearchMode((bool)value!),
        "bars" => Bars((string)value!),
        "glasspicture" => GlassPicture((string)value!),
        "glasstint" => GlassTintPicture((string)value!),
        _ => null,
    };

    public static string Rect(int x, int y, int w, int h, int r = 0)
    {
        if (r == 0) return $"M{x},{y} h{w} v{h} h{-w} z ";
        return $"M{x + r},{y} h{w - 2 * r} a{r},{r} 0 0 1 {r},{r} v{h - 2 * r} a{r},{r} 0 0 1 {-r},{r} h{-(w - 2 * r)} a{r},{r} 0 0 1 {-r},{-r} v{-(h - 2 * r)} a{r},{r} 0 0 1 {r},{-r} z ";
    }

    // card: the card, in a quiet colour; marks: what the choice is about, in the accent's (or the brushes given).
    public static ElementKind Icon(string? card, string? marks, bool line = false, BrushKind? cardBrush = null, BrushKind? marksBrush = null)
    {
        var both = Frame();
        if (card != null)
        {
            var path = new ShapePath { Data = Geometry.Parse(card) };
            if (cardBrush != null) path.Fill = cardBrush;
            else Fill(path, "BorderStrongBrush");
            both.Children.Add(path);
        }
        if (marks != null)
        {
            var path = new ShapePath { Data = Geometry.Parse(marks) };
            if (line)
            {
                if (marksBrush != null) path.Stroke = marksBrush;
                else Stroke(path, "AccentTextBrush");
                path.StrokeThickness = 2;
            }
            else if (marksBrush != null) path.Fill = marksBrush;
            else Fill(path, "AccentTextBrush");
            both.Children.Add(path);
        }
        return both;
    }

    private static Grid Frame() => new() { Width = 64, Height = 36, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };

    private static SolidColorBrush Solid(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static readonly string Lines = Rect(13, 12, 24, 4, 2) + Rect(13, 19, 16, 3, 1);

    // A card in the colours of a light or a dark look.
    public static ElementKind LightOrDark(bool light) => Icon(Rect(8, 6, 48, 24, 5), Lines, false,
        light ? Solid(0xF4, 0xF4, 0xF8) : Solid(0x24, 0x25, 0x2C), light ? Solid(0x3A, 0x3A, 0x44) : Solid(0xFF, 0xFF, 0xFF));

    // Same as the system: half of each.
    private static ElementKind Look(string id)
    {
        if (id == ThemeColors.Light) return LightOrDark(true);
        if (id == ThemeColors.Dark) return LightOrDark(false);
        var both = Frame();
        both.Children.Add(new ShapePath { Data = Geometry.Parse("M13,6 h19 v24 h-19 a5,5 0 0 1 -5,-5 v-14 a5,5 0 0 1 5,-5 z"), Fill = Solid(0xF4, 0xF4, 0xF8) });
        both.Children.Add(new ShapePath { Data = Geometry.Parse("M32,6 h19 a5,5 0 0 1 5,5 v14 a5,5 0 0 1 -5,5 h-19 z"), Fill = Solid(0x24, 0x25, 0x2C) });
        both.Children.Add(new ShapePath { Data = Geometry.Parse(Rect(13, 12, 19, 4) + Rect(13, 19, 16, 3)), Fill = Solid(0x3A, 0x3A, 0x44) });
        both.Children.Add(new ShapePath { Data = Geometry.Parse(Rect(32, 12, 19, 4) + Rect(32, 19, 10, 3)), Fill = Solid(0xFF, 0xFF, 0xFF) });
        return both;
    }

    // Columns rising (way -1) or hanging (way 1) from a line.
    public static string Columns(int x, int count, int step, int width, int from, int way, IEnumerable<int> heights)
    {
        var path = "";
        int i = 0;
        foreach (int h in heights.Take(count))
        {
            path += Rect(x + i * step, way < 0 ? from - h : from, width, h);
            i++;
        }
        return path;
    }

    public static readonly int[] Sample = { 5, 11, 7, 14, 9, 4, 8, 13, 6, 10, 5, 12 };

    public static ElementKind BarShape(VisualizerStyle style)
    {
        var tall = Sample;
        int X(int i) => 4 + i * 5;
        string marks = "";
        switch (style)
        {
            case VisualizerStyle.Peaks:
                for (int i = 0; i < tall.Length; i++) marks += Rect(X(i), 34 - tall[i] * 2, 3, tall[i] * 2) + Rect(X(i), 34 - tall[i] * 2 - 5, 3, 2);
                return Icon(null, marks);
            case VisualizerStyle.Blocks:
                for (int i = 0; i < tall.Length; i++)
                    for (int k = 0; k < tall[i] / 2; k++) marks += Rect(X(i), 31 - k * 5, 3, 3);
                return Icon(null, marks);
            case VisualizerStyle.Dots:
                for (int i = 0; i < tall.Length; i++) marks += $"M{X(i)},{33 - tall[i] * 2} a2,2 0 1 0 4,0 a2,2 0 1 0 -4,0 z ";
                return Icon(null, marks);
            case VisualizerStyle.Line:
                marks = $"M{X(0)},{33 - tall[0] * 2}";
                for (int i = 1; i < tall.Length; i++) marks += $" L{X(i)},{33 - tall[i] * 2}";
                return Icon(null, marks, true);
            case VisualizerStyle.Wave:
                marks = $"M{X(0)},35 L{X(0)},{33 - tall[0] * 2}";
                for (int i = 1; i < tall.Length; i++) marks += $" L{X(i)},{33 - tall[i] * 2}";
                return Icon(null, marks + $" L{X(tall.Length - 1)},35 z");
            default:
                for (int i = 0; i < tall.Length; i++) marks += Rect(X(i), 35 - tall[i] * 2, 3, tall[i] * 2);
                return Icon(null, marks);
        }
    }

    // Where the bass goes: the tallest columns there.
    public static ElementKind Flow(VisualizerFlow flow)
    {
        const int Count = 13;
        var marks = "";
        for (int i = 0; i < Count; i++)
        {
            int h = (int)Math.Round(5 + 26 * (1 - Spectrum.PlaceOf((i + 0.5) / Count, flow)));
            marks += Rect(6 + i * 4, 34 - h, 3, h);
        }
        return Icon(null, marks);
    }

    // Around the song's cover.
    private static ElementKind Place(VisualizerPlace place)
    {
        string cover = Rect(24, 10, 16, 16, 3);
        int[] small = { 4, 7, 5, 9, 6, 3, 5, 8, 4, 6, 3, 7 };
        string marks = place switch
        {
            VisualizerPlace.Top => Columns(4, 12, 5, 3, 1, 1, small),
            VisualizerPlace.Both => Columns(4, 12, 5, 3, 1, 1, small) + Columns(4, 12, 5, 3, 35, -1, small),
            // (through the middle: only at its sides)
            VisualizerPlace.Center => Columns(4, 4, 4, 2, 18, -1, new[] { 5, 9, 12, 8 }) + Columns(4, 4, 4, 2, 18, 1, new[] { 5, 9, 12, 8 }) + Columns(46, 4, 4, 2, 18, -1, new[] { 8, 12, 9, 5 }) + Columns(46, 4, 4, 2, 18, 1, new[] { 8, 12, 9, 5 }),
            _ => Columns(4, 12, 5, 3, 35, -1, small),
        };
        return Icon(cover, marks);
    }

    private static ElementKind Tint(VisualizerTint tint)
    {
        string bars = Columns(4, 12, 5, 3, 35, -1, Sample.Select(h => h * 2));
        return tint switch
        {
            VisualizerTint.Rainbow => Icon(null, bars, false, null, Rainbow()),
            VisualizerTint.Text => Icon(null, bars, false, null, Brush("TextBrush")),
            VisualizerTint.Accent => Icon(null, bars),
            // (the colours of a cover: the cover, its bars)
            _ => Icon(Rect(4, 8, 20, 20, 3), Columns(29, 7, 5, 3, 35, -1, new[] { 14, 22, 12, 26, 16, 20, 10 }), false, CoverColors(), CoverColors()),
        };
    }

    private static ElementKind BarThickness(int thickness)
    {
        int width = thickness == 0 ? 1 : thickness == 1 ? 3 : 6, step = thickness == 2 ? 8 : thickness == 1 ? 5 : 4;
        int count = 56 / step;
        return Icon(null, Columns(32 - count * step / 2 + (step - width) / 2, count, step, width, 35, -1, Enumerable.Range(0, count).Select(i => Sample[i % Sample.Length] * 2)));
    }

    // A window; what the music does once it's closed.
    private static ElementKind Close(string id)
    {
        string window = Rect(14, 4, 36, 28, 5);
        return id switch
        {
            "pause" => Icon(window, Rect(26, 11, 4, 14, 1) + Rect(34, 11, 4, 14, 1)),
            "quit" => Icon(window, "M25,11 L39,25 M39,11 L25,25", true),
            _ => Icon(window, "M27,10 L27,26 L40,18 z"),
        };
    }

    // The sites asked together, or one after the other.
    private static ElementKind SearchMode(bool together) => Icon(null,
        together ? Rect(8, 7, 48, 5, 2) + Rect(8, 16, 48, 5, 2) + Rect(8, 25, 48, 5, 2)
                 : Rect(8, 7, 18, 5, 2) + Rect(23, 16, 18, 5, 2) + Rect(38, 25, 18, 5, 2));

    private static ShapePath Part(string data, string key)
    {
        var path = new ShapePath { Data = Geometry.Parse(data) };
        Fill(path, key);
        return path;
    }

    private static ShapePath Part(string data, BrushKind brush) => new() { Data = Geometry.Parse(data), Fill = brush };

    // The side bar and the player bar on the window: floating (a gap all round), attached (against its edges), or by the glass
    // look (automatic: a small one of each).
    private static ElementKind Bars(string bars)
    {
        if (bars == LayoutBars.Float) return Icon(Rect(10, 3, 44, 30, 4), Rect(13, 6, 10, 19, 2) + Rect(13, 28, 38, 3, 1));
        if (bars == LayoutBars.Dock)
            return Icon(Rect(10, 3, 44, 30, 4), "M10,7 a4,4 0 0 1 4,-4 h8 v23 h-12 z M10,27 h44 v2 a4,4 0 0 1 -4,4 h-36 a4,4 0 0 1 -4,-4 z");
        return Icon(Rect(4, 7, 26, 22, 3) + Rect(34, 7, 26, 22, 3),
            Rect(6, 9, 6, 13, 1) + Rect(6, 24, 22, 3, 1) + "M34,10 a3,3 0 0 1 3,-3 h4 v17 h-7 z M34,25 h26 v1 a3,3 0 0 1 -3,3 h-20 a3,3 0 0 1 -3,-3 z");
    }

    private static readonly string GlassWindow = Rect(8, 4, 48, 28, 5);
    private static readonly SolidColorBrush ClearPane = new(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF));

    // What the window lies on, under its panes: the song's cover, the theme's two colours, a picture of your own.
    private static ElementKind GlassPicture(string kind)
    {
        var both = Frame();
        switch (kind)
        {
            case "theme":
                both.Children.Add(Part("M13,4 h19 v28 h-19 a5,5 0 0 1 -5,-5 v-18 a5,5 0 0 1 5,-5 z", "AccentBrush"));
                both.Children.Add(Part("M32,4 h19 a5,5 0 0 1 5,5 v18 a5,5 0 0 1 -5,5 h-19 z", "Accent2Brush"));
                break;
            case "mine":
                both.Children.Add(Part(GlassWindow, "BorderStrongBrush"));
                both.Children.Add(Part("M11,29 L21,16 L29,24 L37,17 L53,29 z M42,11 a3,3 0 1 0 6,0 a3,3 0 1 0 -6,0 z", "AccentTextBrush"));
                break;
            default:
                both.Children.Add(Part(GlassWindow, CoverColors()));
                break;
        }
        both.Children.Add(Part(Rect(12, 8, 10, 20, 2) + Rect(25, 8, 27, 20, 2), ClearPane));
        return both;
    }

    // A pane over the cover: dark glass, white glass, the accent's; its lines of text on it.
    private static ElementKind GlassTintPicture(string tint)
    {
        var both = Frame();
        both.Children.Add(Part(GlassWindow, CoverColors()));
        string pane = Rect(14, 9, 36, 18, 4);
        if (tint == GlassTint.Color)
        {
            var colored = Part(pane, "AccentBrush");
            colored.Opacity = 0.75;
            both.Children.Add(colored);
        }
        else both.Children.Add(Part(pane, new SolidColorBrush(tint == GlassTint.Frost ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB8, 0x10, 0x12, 0x17))));
        both.Children.Add(Part(Rect(19, 14, 20, 3, 1) + Rect(19, 20, 13, 3, 1), tint == GlassTint.Frost ? Solid(0x30, 0x30, 0x3A) : Solid(0xFF, 0xFF, 0xFF)));
        return both;
    }

    private static LinearGradientBrush Gradient(params (double At, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
#if AVALONIA
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
#else
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
#endif
        };
        foreach (var (at, color) in stops) brush.GradientStops.Add(new GradientStop(color, at));
        return brush;
    }

    public static Color ColorOf(string hex)
    {
        int n = Convert.ToInt32(hex.TrimStart('#'), 16);
        return Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
    }

    public static LinearGradientBrush Rainbow() => Gradient(Enumerable.Range(0, 13).Select(i => (i / 12.0, ColorOf(ThemeColors.FromHue(i * 30 % 360)))).ToArray());

    private static LinearGradientBrush CoverColors() => Gradient((0, Color.FromRgb(0xF5, 0x9E, 0x0B)), (1, Color.FromRgb(0xEF, 0x44, 0x44)));
}
