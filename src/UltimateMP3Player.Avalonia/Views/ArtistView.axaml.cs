using Avalonia.Controls;
using Avalonia.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
        PictureButton.PointerEntered += (_, _) => PictureButton.Opacity = 1;
        PictureButton.PointerExited += (_, _) => PictureButton.Opacity = 0;
        NameText.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left) Vm?.RenameCommand.Execute(null);
        };
        MoreButton.Click += (_, _) =>
        {
            if (Vm != null) Menus.Open(Menus.ForArtist(Vm), MoreButton, true);
        };
    }

    private ArtistPageViewModel? Vm => DataContext as ArtistPageViewModel;
}
