using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UltimateMP3Player.Views;

public partial class PlaylistsView : UserControl
{
    public PlaylistsView()
    {
        InitializeComponent();
        // (like Home's cards: a plain click opens, Ctrl or Shift + click selects)
        PlaylistList.AddHandler(PointerPressedEvent, HomeView.OnCardPressed, RoutingStrategies.Tunnel);
    }
}
