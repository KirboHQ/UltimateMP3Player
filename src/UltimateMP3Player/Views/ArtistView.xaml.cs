using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class ArtistView : UserControl
{
    public ArtistView()
    {
        InitializeComponent();
        PictureButton.MouseEnter += (_, _) => PictureButton.Opacity = 1;
        PictureButton.MouseLeave += (_, _) => PictureButton.Opacity = 0;
    }

    private ArtistPageViewModel? Vm => DataContext as ArtistPageViewModel;

    private void Name_Click(object sender, MouseButtonEventArgs e) => Vm?.RenameCommand.Execute(null);

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Menus.Open(Menus.ForArtist(Vm), (UIElement)sender, true);
    }
}
