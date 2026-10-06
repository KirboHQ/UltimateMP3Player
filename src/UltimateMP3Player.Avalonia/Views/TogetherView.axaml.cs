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
