#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BrushKind = Avalonia.Media.IBrush;
using DialogKind = UltimateMP3Player.DialogWindow;
using ElementKind = Avalonia.Controls.Control;
using TemplateKind = Avalonia.Controls.Templates.IDataTemplate;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrushKind = System.Windows.Media.Brush;
using DialogKind = System.Windows.Window;
using ElementKind = System.Windows.FrameworkElement;
using TemplateKind = System.Windows.DataTemplate;
#endif

namespace UltimateMP3Player;

// What the windows built in code say the same way on Windows and on Linux/macOS (linked into the Avalonia app).
public static class Both
{
#if AVALONIA
    public static Cursor Hand => Ui.Hand;
    public static FontWeight SemiBold => FontWeight.SemiBold;
    public static BrushKind Brush(string key) => Ui.Res(key);
    public static TemplateKind Template(string key) => Ui.Find<TemplateKind>(key);
#else
    public static Cursor Hand => Cursors.Hand;
    public static FontWeight SemiBold => FontWeights.SemiBold;
    public static BrushKind Brush(string key) => (BrushKind)Application.Current.Resources[key];
    public static TemplateKind Template(string key) => (TemplateKind)Application.Current.Resources[key];
#endif

    public static T Look<T>(T control, string key) where T : Control
    {
#if AVALONIA
        control.Theme = Ui.Theme(key);
#else
        control.Style = (Style)Application.Current.Resources[key];
#endif
        return control;
    }

    // Style: the WPF one's name ("FieldLabel"), a class of App.axaml there ("field-label").
    public static TextBlock Text(string text, string style, Thickness margin = default)
    {
#if AVALONIA
        return Ui.Text(text, style == "FieldLabel" ? "field-label" : style.ToLowerInvariant(), margin);
#else
        return new() { Text = text, Style = (Style)Application.Current.Resources[style], Margin = margin };
#endif
    }

    public static TextBlock Glyph(string glyph, double size, BrushKind brush)
    {
#if AVALONIA
        glyph = Icons.Map(glyph);
#endif
        var t = Text(glyph, "Glyph");
        t.FontSize = size;
        t.Foreground = brush;
        return t;
    }

    public static Ellipse Spinner(double size, Thickness margin = default)
    {
        var e = new Ellipse { Width = size, Height = size, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center, Margin = margin };
#if AVALONIA
        e.Classes.Add("spinner");
#else
        e.Style = (Style)Application.Current.Resources["Spinner"];
#endif
        return e;
    }

    public static TextBox Box(string text, string? placeholder = null)
    {
        var box = Look(new TextBox { Text = text }, "BoxTextBox");
#if AVALONIA
        box.Watermark = placeholder;
#else
        box.Tag = placeholder;
#endif
        return box;
    }

    public static T Tip<T>(T e, string? text) where T : ElementKind
    {
#if AVALONIA
        ToolTip.SetTip(e, text);
#else
        e.ToolTip = text;
#endif
        return e;
    }

    public static void Show(ElementKind e, bool on)
    {
#if AVALONIA
        e.IsVisible = on;
#else
        e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
#endif
    }

    // (its own, not whether its window shows)
    public static bool IsShown(ElementKind e)
    {
#if AVALONIA
        return e.IsVisible;
#else
        return e.Visibility == Visibility.Visible;
#endif
    }

    public static T At<T>(T e, int column) where T : ElementKind
    {
        Grid.SetColumn(e, column);
        return e;
    }

    // Columns as wide as what's in them (false) or sharing the rest (true).
    public static Grid Columns(params bool[] stars)
    {
        var grid = new Grid();
        foreach (var star in stars) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        return grid;
    }

    public static void OnTick(ToggleButton button, Action<bool> changed)
    {
#if AVALONIA
        button.IsCheckedChanged += (_, _) => changed(button.IsChecked == true);
#else
        button.Checked += (_, _) => changed(true);
        button.Unchecked += (_, _) => changed(false);
#endif
    }

    // Pressed with the left button, how many times in a row. Handled: the dialog isn't dragged from there.
    public static void OnPress(ElementKind e, Action<int> pressed)
    {
#if AVALONIA
        e.PointerPressed += (_, a) =>
        {
            if (!a.GetCurrentPoint(e).Properties.IsLeftButtonPressed) return;
#else
        e.MouseLeftButtonDown += (_, a) =>
        {
#endif
            a.Handled = true;
            pressed(a.ClickCount);
        };
    }

    public static void OnHover(ElementKind e, Action over, Action away)
    {
#if AVALONIA
        e.PointerEntered += (_, _) => over();
        e.PointerExited += (_, _) => away();
#else
        e.MouseEnter += (_, _) => over();
        e.MouseLeave += (_, _) => away();
#endif
    }

    public static void OnOpened(DialogKind win, Action opened)
    {
#if AVALONIA
        win.Opened += (_, _) => opened();
#else
        win.Loaded += (_, _) => opened();
#endif
    }

    // What the screen has room for, in the window's own units.
    public static Size Room()
    {
#if AVALONIA
        var top = App.Host?.Window;
        var screen = top?.Screens.ScreenFromWindow(top);
        return screen != null ? new Size(screen.WorkingArea.Width / screen.Scaling / Ui.Zoom, screen.WorkingArea.Height / screen.Scaling / Ui.Zoom) : new Size(1280, 800);
#else
        return new Size(SystemParameters.WorkArea.Width / Ui.Zoom, SystemParameters.WorkArea.Height / Ui.Zoom);
#endif
    }
}
