#if AVALONIA
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BrushKind = Avalonia.Media.IBrush;
using ElementKind = Avalonia.Controls.Control;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrushKind = System.Windows.Media.Brush;
using ElementKind = System.Windows.FrameworkElement;
#endif
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// Any colour there is (the Custom theme, a stream overlay's): a square of how strong (across) and how bright (up) in the hue chosen on
// the bar under it, then the colour and its code to type. Nothing runs but on a press, a drag, a key or a letter: the knobs move by a
// transform (no layout), the square's hue is one brush. Linked into Avalonia and the phones ("handle": the finger drags it, not the page).
public sealed class ColorPicker : StackPanel
{
#if AVALONIA
    public static readonly StyledProperty<string?> HexProperty = AvaloniaProperty.Register<ColorPicker, string?>(nameof(Hex), defaultBindingMode: BindingMode.TwoWay);
    public string? Hex { get => GetValue(HexProperty); set => SetValue(HexProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HexProperty) Show(Hex, true);
    }
#else
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(nameof(Hex), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorPicker)d).Show((string?)e.NewValue, true)));
    public string? Hex { get => (string?)GetValue(HexProperty); set => SetValue(HexProperty, value); }
#endif

    // Chosen here (a drag, a key, a whole code typed); not when Hex is set from outside.
    public event Action<string>? Picked;

    private const double Knob = 22;
    private readonly SolidColorBrush _pure = new(Colors.Red), _chosen = new(Colors.Red);
    private readonly TranslateTransform _squareAt = new(), _hueAt = new();
    private readonly Grid _square = Area(150, L.T("Saturation and brightness"));
    private readonly Grid _bar = Area(Knob + 4, L.T("Hue"));
    private readonly TextBox _code = new() { Width = 112, MaxLength = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    // (kept apart from the colour: a grey has no hue, black no strength either, and their knobs stay where they were put)
    private double _h, _s = 1, _v = 1;
    private string _shown = "#FF0000";
    private bool _writing;

    public ColorPicker()
    {
        // (the knobs reach half out of the square and the bar: room for them inside, nothing clips them)
        _square.Margin = new Thickness(Knob / 2, Knob / 2, Knob / 2, 0);
        _square.Children.Add(Layer(_pure));
        _square.Children.Add(Layer(Linear(true, (0, Color.FromArgb(255, 255, 255, 255)), (1, Color.FromArgb(0, 255, 255, 255)))));
        _square.Children.Add(Layer(Linear(false, (0, Color.FromArgb(0, 0, 0, 0)), (1, Color.FromArgb(255, 0, 0, 0)))));
        _square.Children.Add(KnobOf(_chosen, _squareAt, true));
        Children.Add(_square);

        _bar.Margin = new Thickness(Knob / 2, 12, Knob / 2, 0);
        _bar.Children.Add(new Border
        {
            Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            Background = Linear(true, Enumerable.Range(0, 7).Select(i => (i / 6.0, TilePictures.ColorOf(ThemeColors.FromHsv(i * 60, 1, 1)))).ToArray()),
        });
        _bar.Children.Add(KnobOf(_pure, _hueAt, false));
        Children.Add(_bar);

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        line.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = _chosen, VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(_code, L.T("Colour code"));
#if !AVALONIA
        _code.Style = (Style)Application.Current.Resources["BoxTextBox"];
#endif
        line.Children.Add(_code);
        Children.Add(line);

        Drag(_square, p =>
        {
            _s = Fraction(p.X, Wide(_square));
            _v = 1 - Fraction(p.Y, High(_square));
            Pick();
        });
        Drag(_bar, p =>
        {
            _h = Fraction(p.X, Wide(_bar)) * 360;
            Pick();
        });
        _square.KeyDown += (_, e) =>
        {
            double across = e.Key == Key.Right ? 0.02 : e.Key == Key.Left ? -0.02 : 0, up = e.Key == Key.Up ? 0.02 : e.Key == Key.Down ? -0.02 : 0;
            if (across == 0 && up == 0) return;
            _s = Math.Clamp(_s + across, 0, 1);
            _v = Math.Clamp(_v + up, 0, 1);
            Pick();
            e.Handled = true;
        };
        _bar.KeyDown += (_, e) =>
        {
            double by = e.Key == Key.Right ? 2 : e.Key == Key.Left ? -2 : 0;
            if (by == 0) return;
            _h = (_h + by + 360) % 360;
            Pick();
            e.Handled = true;
        };
        _square.SizeChanged += (_, _) => Place();
        _bar.SizeChanged += (_, _) => Place();
        // A code counts once it's whole, and what's typed is never written over while typing (only on leaving the box, or Enter).
        _code.TextChanged += (_, _) =>
        {
            if (_writing || ThemeColors.ColorFrom(_code.Text) is not { } typed || typed == _shown) return;
            Show(typed, false);
            Tell();
        };
        _code.LostFocus += (_, _) => Write();
        _code.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Write();
        };
        Write();
    }

    private void Pick()
    {
        _shown = ThemeColors.FromHsv(_h, _s, _v);
        Paint(true);
        Tell();
    }

    private void Tell()
    {
        _writing = true;
        Hex = _shown;
        _writing = false;
        Picked?.Invoke(_shown);
    }

    private void Show(string? hex, bool text)
    {
        if (_writing || ThemeColors.ColorFrom(hex) is not { } c || c == _shown) return;
        var (h, s, v) = ThemeColors.ToHsv(c);
        if (s > 0 && v > 0) _h = h;
        if (v > 0) _s = s;
        _v = v;
        _shown = c;
        Paint(text);
    }

    private void Paint(bool text)
    {
        _pure.Color = TilePictures.ColorOf(ThemeColors.FromHsv(_h, 1, 1));
        _chosen.Color = TilePictures.ColorOf(_shown);
        Place();
        if (text) Write();
    }

    private void Place()
    {
        _squareAt.X = _s * Wide(_square) - Knob / 2;
        _squareAt.Y = (1 - _v) * High(_square) - Knob / 2;
        _hueAt.X = _h / 360 * Wide(_bar) - Knob / 2;
    }

    private void Write()
    {
        if (_code.Text == _shown) return;
        _writing = true;
        _code.Text = _shown;
        _writing = false;
    }

    private static Grid Area(double height, string name)
    {
        var area = new Grid { Height = height, Background = Brushes.Transparent, Focusable = true };
#if AVALONIA
        area.Classes.Add("handle");
#if !ANDROID_APP && !IOS_APP
        area.Cursor = new Cursor(StandardCursorType.Hand);
#endif
#else
        area.Cursor = Cursors.Hand;
#endif
        AutomationProperties.SetName(area, name);
        return area;
    }

    // A white ring with a dark edge (it shows on white too), the colour inside.
    private static Border KnobOf(BrushKind fill, TranslateTransform at, bool top) => new()
    {
        Width = Knob, Height = Knob, CornerRadius = new CornerRadius(Knob / 2), Padding = new Thickness(1),
        Background = new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)), IsHitTestVisible = false, RenderTransform = at,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
        Child = new Border { CornerRadius = new CornerRadius(Knob / 2 - 1), BorderThickness = new Thickness(3), BorderBrush = Brushes.White, Background = fill },
    };

    private static Border Layer(BrushKind brush) => new() { Background = brush, CornerRadius = new CornerRadius(12), IsHitTestVisible = false };

    private static LinearGradientBrush Linear(bool across, params (double At, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush
        {
#if AVALONIA
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(across ? 1 : 0, across ? 0 : 1, RelativeUnit.Relative),
#else
            StartPoint = new Point(0, 0), EndPoint = new Point(across ? 1 : 0, across ? 0 : 1),
#endif
        };
        foreach (var (at, color) in stops) brush.GradientStops.Add(new GradientStop(color, at));
        return brush;
    }

    private static double Fraction(double at, double of) => of > 0 ? Math.Clamp(at / of, 0, 1) : 0;

    // A press on the square or the bar, and the drag after it (the mouse held, or the finger: the page doesn't scroll under it).
    private static void Drag(ElementKind area, Action<Point> at)
    {
#if AVALONIA
        area.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(area).Properties.IsLeftButtonPressed) return;
            e.Pointer.Capture(area);
            e.PreventGestureRecognition();
            area.Focus();
            at(e.GetPosition(area));
            e.Handled = true;
        };
        area.PointerMoved += (_, e) =>
        {
            if (e.Pointer.Captured == area) at(e.GetPosition(area));
        };
        area.PointerReleased += (_, e) =>
        {
            if (e.Pointer.Captured == area) e.Pointer.Capture(null);
        };
#else
        area.MouseLeftButtonDown += (_, e) =>
        {
            area.CaptureMouse();
            area.Focus();
            at(e.GetPosition(area));
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (area.IsMouseCaptured) at(e.GetPosition(area));
        };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
#endif
    }

#if AVALONIA
    private static double Wide(ElementKind e) => e.Bounds.Width;
    private static double High(ElementKind e) => e.Bounds.Height;
#else
    private static double Wide(ElementKind e) => e.ActualWidth;
    private static double High(ElementKind e) => e.ActualHeight;
#endif
}
