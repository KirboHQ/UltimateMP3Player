using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        CoverButton.MouseEnter += (_, _) => CoverButton.Opacity = Vm?.Vm.IsFavorites == false ? 1 : 0;
        CoverButton.MouseLeave += (_, _) => CoverButton.Opacity = 0;
    }

    private PlaylistPageViewModel? Vm => DataContext as PlaylistPageViewModel;

    private void Filter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm == null) return;
        Vm.Filter = "";
        e.Handled = true;
    }

    private void Name_Click(object sender, MouseButtonEventArgs e)
    {
        if (Vm?.RenameCommand.CanExecute(null) == true) Vm.RenameCommand.Execute(null);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Menus.Open(Menus.ForPlaylist(Vm.Vm), (UIElement)sender, true);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(typeof(TrackRow));
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok) DragVisuals.Hit(List, e);
        e.Handled = true;
    }

    private void List_DragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(List);
        if (p.X < 0 || p.Y < 0 || p.X > List.ActualWidth || p.Y > List.ActualHeight) DragVisuals.HideLine();
    }

    // Moves a song inside the playlist, or inserts one from another list.
    private void List_Drop(object sender, DragEventArgs e)
    {
        DragVisuals.HideLine();
        if (Vm == null || e.Data.GetData(typeof(TrackRow)) is not TrackRow row) return;
        e.Handled = true;
        var p = Vm.Vm.P;
        var profile = row.Track.Main.Profile;
        var (target, after) = DragVisuals.Hit(List, e);
        DragVisuals.HideLine();
        int t = target is TrackRow tr ? p.Tracks.IndexOf(tr.Track.Id) : -1;
        int from = p.Tracks.IndexOf(row.Track.Id);
        if (from >= 0)
        {
            int to = t < 0 ? p.Tracks.Count - 1 : DragVisuals.MoveIndex(from, t, after);
            profile.MoveTrack(p, from, Math.Clamp(to, 0, p.Tracks.Count - 1));
        }
        else
        {
            profile.AddTrack(p, row.Track.Id, t < 0 ? null : after ? t + 1 : t);
            row.Track.Main.Toast(L.F("Aggiunto a «{0}»", PlaylistViewModel.DisplayName(p)));
        }
    }
}
