using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class TogetherView : UserControl
{
    private TogetherViewModel? _vm;

    // Narrow window: the queue rows show who added a song with their dot only.
    public static readonly DependencyProperty NarrowProperty = DependencyProperty.Register(nameof(Narrow), typeof(bool), typeof(TogetherView));
    public bool Narrow { get => (bool)GetValue(NarrowProperty); set => SetValue(NarrowProperty, value); }

    // Low window: smaller header, no secondary hints, so the queue and the chat keep some room.
    public static readonly DependencyProperty LowProperty = DependencyProperty.Register(nameof(Low), typeof(bool), typeof(TogetherView),
        new PropertyMetadata(false, (d, _) => ((TogetherView)d).ApplyLow()));
    public bool Low { get => (bool)GetValue(LowProperty); set => SetValue(LowProperty, value); }

    private void ApplyLow()
    {
        bool low = Low;
        RoomIcon.Visibility = QueueHint.Visibility = RoleHint.Visibility = low ? Visibility.Collapsed : Visibility.Visible;
        RoomNameText.FontSize = low ? 20 : 28;
        RoomTitle.Margin = new Thickness(low ? 0 : 18, 0, 16, 0);
        RoomHeader.Margin = new Thickness(0, 0, 8, low ? 10 : 16);
        // (its visibility is bound: hidden by height instead)
        RoomAddress.MaxHeight = low ? 0 : double.PositiveInfinity;
    }

    public TogetherView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook(DataContext as TogetherViewModel);
        Unloaded += (_, _) => Hook(null);
        Loaded += (_, _) =>
        {
            Hook(DataContext as TogetherViewModel);
            ScrollChatToEnd();
        };
        LeftCol.SizeChanged += (_, _) => Adapt();
        RightCol.SizeChanged += (_, _) => Adapt();
        SizeChanged += (_, e) => Low = e.NewSize.Height is > 0 and < 560;
    }

    // Small windows: a smaller cover, the people list gives room to the chat, shorter queue rows.
    private void Adapt()
    {
        bool low = LeftCol.ActualHeight is > 0 and < 470;
        NowCover.Width = NowCover.Height = low ? 76 : 112;
        NowAddedBy.Visibility = low || DataContext is TogetherViewModel { HasCurrent: false } ? Visibility.Collapsed : Visibility.Visible;
        Narrow = LeftCol.ActualWidth is > 0 and < 500;
        if (RightCol.ActualHeight > 0) MemberList.MaxHeight = Math.Clamp(RightCol.ActualHeight * 0.5 - 84, 76, 300);
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
        var sv = FindScroll(ChatList);
        bool atEnd = sv == null || sv.VerticalOffset >= sv.ScrollableHeight - 40;
        if (atEnd) ScrollChatToEnd();
    }

    private void ScrollChatToEnd()
        => Dispatcher.BeginInvoke(() =>
        {
            if (ChatList.Items.Count > 0) ChatList.ScrollIntoView(ChatList.Items[^1]);
        }, DispatcherPriority.Background);

    private static ScrollViewer? FindScroll(DependencyObject d)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(d, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScroll(c) is { } found) return found;
        }
        return null;
    }

    // Enter sends.
    private void Chat_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        if (_vm.SendCommand.CanExecute(null)) _vm.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void Item_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RoomItemViewModel item || _vm == null) return;
        Menus.Open(Menus.ForRoomItem(item, _vm), (UIElement)sender, false);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ reordering the queue (like "Up next")

    private Point? _dragStart;

    private void Row_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Row_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start) return;
        if (e.OriginalSource is DependencyObject d && Ui.FindAncestor<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        var p = e.GetPosition(null);
        if (Math.Abs(p.X - start.X) < 6 && Math.Abs(p.Y - start.Y) < 6) return;
        _dragStart = null;
        // (without the permission the rows stay put: moving needs the same one as removing)
        if (sender is not FrameworkElement fe || fe.DataContext is not RoomItemViewModel item || _vm is not { CanRemove: true } vm) return;
        var session = new DragSession(RoomQueue, item);
        var outcome = DragVisuals.Run(fe, new DataObject(typeof(RoomItemViewModel), item), DragDropEffects.Move, session);
        if (outcome != DragOutcome.Placed || session.Target is not RoomItemViewModel target) return;
        int from = vm.Queue.IndexOf(item), to = vm.Queue.IndexOf(target);
        if (from < 0 || to < 0) return;
        int at = DragVisuals.MoveIndex(from, to, session.After);
        if (at != from) vm.MoveTo(item, at);
    }

    private void Queue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(RoomItemViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void Current_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.CurrentItem is not { } item) return;
        Menus.Open(Menus.ForRoomItem(item, _vm), (UIElement)sender, false);
        e.Handled = true;
    }

    // The host's menu for the person clicked (or the selected ones).
    private void Member_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MemberViewModel m || _vm == null) return;
        Menus.Open(Menus.ForMembers(_vm, m), (UIElement)sender, false);
        e.Handled = true;
    }
}
