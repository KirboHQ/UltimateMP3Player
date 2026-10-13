using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class TogetherView : UserControl
{
    private TogetherViewModel? _vm;
    private bool _narrow, _low;

    // Narrow window: the queue rows show who added a song with their dot only.
    public bool Narrow
    {
        get => _narrow;
        set
        {
            _narrow = value;
            Classes.Set("narrow", value);
        }
    }

    // Low window: smaller header, no secondary hints, so the queue and the chat keep some room.
    public bool Low
    {
        get => _low;
        set
        {
            if (_low == value) return;
            _low = value;
            RoomIcon.IsVisible = QueueHint.IsVisible = RoleHint.IsVisible = !value;
            RoomNameText.FontSize = value ? 20 : 28;
            RoomTitle.Margin = new Thickness(value ? 0 : 18, 0, 16, 0);
            RoomHeader.Margin = new Thickness(0, 0, 8, value ? 10 : 16);
            // (its visibility is bound: hidden by height instead)
            RoomAddress.MaxHeight = value ? 0 : double.PositiveInfinity;
        }
    }

    public TogetherView()
    {
        InitializeComponent();
        // Radmin VPN and Windows' own firewall question exist only on Windows.
        if (!OperatingSystem.IsWindows())
        {
            LobbyHint.Text = L.T("La stessa musica, nello stesso momento, per tutti nella stanza, con la chat. Funziona sulla stessa rete (Wi-Fi o cavo); da lontano entrate tutti nella stessa rete virtuale (per esempio ZeroTier o Tailscale).");
            FirewallHint.Text = L.T("Se il sistema chiede se l'app può usare la rete, consenti l'accesso. Se gli altri non riescono a entrare, usa il pulsante qui accanto.");
        }
        DataContextChanged += (_, _) => Hook(DataContext as TogetherViewModel);
        DetachedFromVisualTree += (_, _) => Hook(null);
        AttachedToVisualTree += (_, _) =>
        {
            Hook(DataContext as TogetherViewModel);
            ScrollChatToEnd();
        };
        LeftCol.SizeChanged += (_, _) => Adapt();
        RightCol.SizeChanged += (_, _) => Adapt();
        SizeChanged += (_, e) => Low = e.NewSize.Height is > 0 and < 560;
        ChatBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || _vm == null) return;
            if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Right click: the song's menu (a queued one, or the one playing), the host's menu for a person.
        QueueList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || _vm == null || Find<RoomItemViewModel>(e.Source, "qrow") is not { } hit) return;
            Menus.Open(Menus.ForRoomItem(hit.Item, _vm), hit.Row, false);
            e.Handled = true;
        });
        CurrentCard.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || e.Handled || _vm?.CurrentItem is not { } item) return;
            Menus.Open(Menus.ForRoomItem(item, _vm), CurrentCard, false);
            e.Handled = true;
        });
        MemberList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right || _vm == null || Find<MemberViewModel>(e.Source, "mrow") is not { } hit) return;
            Menus.Open(Menus.ForMembers(_vm, hit.Item), hit.Row, false);
            e.Handled = true;
        });
        // Dragging a song of the queue moves it, like "Up next".
        QueueList.AddHandler(PointerPressedEvent, Row_Pressed, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, Row_Moved, RoutingStrategies.Tunnel);
        QueueList.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(RoomItemFormat) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        });
        // (the move itself happens when the drag ends: Row_Moved)
        QueueList.AddHandler(DragDrop.DropEvent, (_, e) => e.Handled = e.Data.Contains(RoomItemFormat));
    }

    // ------------------------------------------------------------------ reordering the queue

    private const string RoomItemFormat = "UltimateMP3Player.RoomItem";
    private Point? _dragStart;

    private void Row_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && Find<RoomItemViewModel>(e.Source, "qrow") != null) _dragStart = e.GetPosition(null);
    }

    private async void Row_Moved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _dragStart is not Point start) return;
        if (Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        // (without the permission the rows stay put: moving needs the same one as removing)
        if (Find<RoomItemViewModel>(e.Source, "qrow") is not { } hit || _vm is not { CanRemove: true } vm) return;
        var item = hit.Item;
        var session = new DragSession(QueueList, item);
        var data = new DataObject();
        data.Set(RoomItemFormat, item.Id);
        var outcome = await DragVisuals.RunAsync(hit.Row, e, data, DragDropEffects.Move, session);
        if (outcome != DragOutcome.Placed || session.Target is not RoomItemViewModel target) return;
        int from = vm.Queue.IndexOf(item), to = vm.Queue.IndexOf(target);
        if (from < 0 || to < 0) return;
        int at = DragVisuals.MoveIndex(from, to, session.After);
        if (at != from) vm.MoveTo(item, at);
    }

    private static (T Item, Control Row)? Find<T>(object? source, string cls) where T : class
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is Border { Classes: var c } b && c.Contains(cls) && b.DataContext is T item) return (item, b);
        return null;
    }

    // Small windows: a smaller cover, the people list gives room to the chat, shorter queue rows.
    private void Adapt()
    {
        bool low = LeftCol.Bounds.Height is > 0 and < 470;
        NowCover.Width = NowCover.Height = low ? 76 : 112;
        NowAddedBy.MaxHeight = low ? 0 : double.PositiveInfinity;
        Narrow = LeftCol.Bounds.Width is > 0 and < 500;
        if (RightCol.Bounds.Height > 0) MemberList.MaxHeight = Math.Clamp(RightCol.Bounds.Height * 0.5 - 84, 76, 300);
    }

    private void Hook(TogetherViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm != null) _vm.Chat.CollectionChanged -= OnChat;
        _vm = vm;
        if (_vm != null) _vm.Chat.CollectionChanged += OnChat;
    }

    // New messages scroll into view (unless you're reading further up).
    private void OnChat(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) return;
        var sv = ChatList.FindDescendantOfType<ScrollViewer>();
        bool atEnd = sv == null || sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 40;
        if (atEnd) ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
        => Dispatcher.UIThread.Post(() =>
        {
            if (ChatList.ItemCount > 0) ChatList.ScrollIntoView(ChatList.ItemCount - 1);
        }, DispatcherPriority.Background);
}
