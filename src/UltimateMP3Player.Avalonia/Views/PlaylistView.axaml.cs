using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        CoverButton.PointerEntered += (_, _) => CoverButton.Opacity = Vm?.Vm.IsFavorites == false ? 1 : 0;
        CoverButton.PointerExited += (_, _) => CoverButton.Opacity = 0;
        FilterBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || Vm == null) return;
            Vm.Filter = "";
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        NameText.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && Vm?.RenameCommand.CanExecute(null) == true) Vm.RenameCommand.Execute(null);
        };
        MoreButton.Click += (_, _) =>
        {
            if (Vm != null) Menus.Open(Menus.ForPlaylist(Vm.Vm), MoreButton, true);
        };
        TagFilterButton.Click += (_, _) =>
        {
            if (Vm != null && App.Host.Session is { } main) Menus.Open(Menus.TagFilterMenu(Vm.TagFilter, main), TagFilterButton, true);
        };
        StorageFilterButton.Click += (_, _) =>
        {
            if (Vm != null) Menus.Open(Menus.StorageFilterMenu(Vm.Storage), StorageFilterButton, true);
        };
        TagsButton.Click += (_, _) =>
        {
            if (Vm != null) Menus.Open(Menus.PlaylistTagMenu(Vm.Vm), TagsButton, true);
        };
        List.AddHandler(DragDrop.DragOverEvent, List_DragOver);
        List.AddHandler(DragDrop.DragLeaveEvent, List_DragLeave);
        List.AddHandler(DragDrop.DropEvent, List_Drop);
    }

    private PlaylistPageViewModel? Vm => DataContext as PlaylistPageViewModel;

    private void List_DragOver(object? sender, DragEventArgs e)
    {
        bool ok = e.Data.Contains(Rows.TrackRowFormat);
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        if (ok) DragVisuals.Hit(List, e);
        e.Handled = true;
    }

    private void List_DragLeave(object? sender, DragEventArgs e)
    {
        var p = e.GetPosition(List);
        if (p.X < 0 || p.Y < 0 || p.X > List.Bounds.Width || p.Y > List.Bounds.Height) DragVisuals.HideLine();
    }

    // Moves a song inside the playlist, or inserts one from another list.
    private void List_Drop(object? sender, DragEventArgs e)
    {
        DragVisuals.HideLine();
        if (Vm == null || e.Data.Get(Rows.TrackRowFormat) is not TrackRow row) return;
        e.Handled = true;
        var p = Vm.Vm.P;
        var profile = row.Track.Main.Profile;
        var (target, after) = DragVisuals.Hit(List, e);
        DragVisuals.HideLine();
        int t = target is TrackRow tr ? p.Tracks.IndexOf(tr.Track.Id) : -1;
        int from = p.Tracks.IndexOf(row.Track.Id);
        if (from >= 0)
        {
            // (sorted by title, artist…: moving a song is arranging its own order, so that's shown again, the song next to
            // the one it was dropped on)
            if (!Vm.IsCustomOrder)
            {
                Vm.UseCustomOrder();
                row.Track.Main.Toast(L.T("Ordine personalizzato della playlist"));
            }
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
