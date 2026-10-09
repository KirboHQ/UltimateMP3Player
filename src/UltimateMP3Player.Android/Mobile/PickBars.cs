using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace UltimateMP3Player.Views;

// The bars of a page while things are being chosen (Selection), like a phone's gallery: on top ✕, how many, all, "⋮";
// at the bottom the main things to do with them. Shown only while choosing; a page puts them over its own bars.
public sealed class PickTop : Grid
{
    public static readonly StyledProperty<Selection?> PicksProperty = AvaloniaProperty.Register<PickTop, Selection?>(nameof(Picks));

    public Selection? Picks { get => GetValue(PicksProperty); set => SetValue(PicksProperty, value); }

    private readonly TextBlock _count;
    private readonly Button _cancel, _all, _more;

    public PickTop()
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto");
        Height = 56;
        VerticalAlignment = VerticalAlignment.Top;
        IsVisible = false;
        Background = Ui.Res("BgBrush");
        _cancel = Icon("");
        _count = new TextBlock { FontSize = 16.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
        _all = Icon("");
        _more = Icon("");
        Children.Add(_cancel);
        Children.Add(_count);
        Children.Add(_all);
        Children.Add(_more);
        SetColumn(_count, 1);
        SetColumn(_all, 2);
        SetColumn(_more, 3);
        Margin = new Thickness(0);
    }

    private static Button Icon(string glyph) => new() { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(glyph) };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PicksProperty) return;
        if (change.OldValue is Selection old) old.PropertyChanged -= OnPicks;
        if (change.NewValue is Selection sel)
        {
            sel.PropertyChanged += OnPicks;
            _cancel.Command = sel.CancelCommand;
            _all.Command = sel.AllCommand;
            _more.Command = sel.MoreCommand;
        }
        Refresh();
    }

    private void OnPicks(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        IsVisible = Picks?.IsActive == true;
        _count.Text = Picks?.CountText ?? "";
    }
}

public sealed class PickActions : Border
{
    public static readonly StyledProperty<Selection?> PicksProperty = AvaloniaProperty.Register<PickActions, Selection?>(nameof(Picks));

    public Selection? Picks { get => GetValue(PicksProperty); set => SetValue(PicksProperty, value); }

    private readonly UniformGrid _row = new() { Rows = 1 };

    // A rounded card floating over the song playing, like the mini player under it and the computer's selection bar.
    public PickActions()
    {
        VerticalAlignment = VerticalAlignment.Bottom;
        IsVisible = false;
        Margin = new Thickness(8, 4, 8, 8);
        CornerRadius = new CornerRadius(14);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(4);
        Background = Ui.Res("Surface3Brush");
        BorderBrush = Ui.Res("BorderStrongBrush");
        BoxShadow = BoxShadows.Parse("0 2 12 0 #59000000");
        Child = _row;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PicksProperty) return;
        if (change.OldValue is Selection old) old.PropertyChanged -= OnPicks;
        if (change.NewValue is Selection sel) sel.PropertyChanged += OnPicks;
        Refresh();
    }

    private void OnPicks(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Selection.Actions) or nameof(Selection.IsActive)) Refresh();
    }

    private void Refresh()
    {
        IsVisible = Picks?.IsActive == true;
        _row.Children.Clear();
        foreach (var a in Picks?.Actions ?? new List<PickAction>())
        {
            var b = new Button
            {
                Theme = Ui.Theme("PickButton"), Command = a.Command,
                Content = new TextBlock { Text = a.Label, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center },
            };
            Ui.SetGlyph(b, a.Glyph);
            if (a.Danger) b.Classes.Add("danger");
            _row.Children.Add(b);
        }
    }
}
