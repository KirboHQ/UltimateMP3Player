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
