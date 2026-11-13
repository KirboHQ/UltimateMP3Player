using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace UltimateMP3Player.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // A plain click on a card plays it (or opens the playlist) without selecting it; Ctrl or Shift + click selects.
        foreach (var list in new[] { RecentList, PlaylistList })
            list.AddHandler(PointerPressedEvent, OnCardPressed, RoutingStrategies.Tunnel);
        // Settings › Layout: the rows in the order chosen, the hidden ones out.
        Loaded += (_, _) =>
        {
            Core.Layout.Changed += ArrangeRows;
            ArrangeRows();
        };
        Unloaded += (_, _) => Core.Layout.Changed -= ArrangeRows;
    }

    private void ArrangeRows() => Rearrange.Apply(HomeRows, Core.Layout.Home);

    internal static void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0) return;
        // (the buttons on a card still get their click)
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) != null) return;
        list.UnselectAll();
        e.Handled = true;
    }
}
