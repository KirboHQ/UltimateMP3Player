using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class TogetherPage : UserControl, IPage
{
    private TogetherViewModel? _vm;
    private int _tab;
    private bool _syncing, _attached;

    public TogetherPage()
    {
        InitializeComponent();
        MemberTap = new RelayCommand(p =>
        {
            if (p is MemberViewModel { IsMe: false } m && _vm is { IsHost: true } vm && Menus.ForMembers(vm, m) is { } menu) Menus.Open(menu);
        });
        DataContextChanged += (_, _) =>
        {
            Listen(false);
            _vm = DataContext as TogetherViewModel;
            Listen(_attached);
        };
        TabQueue.IsCheckedChanged += (_, _) => Pick(0, TabQueue);
        TabPeople.IsCheckedChanged += (_, _) => Pick(1, TabPeople);
        TabChat.IsCheckedChanged += (_, _) => Pick(2, TabChat);
        ChatBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (_vm?.SendCommand.CanExecute(null) == true) _vm.SendCommand.Execute(null);
        };
        Show();
    }

    // The host taps a person: what they may do, handing over the room, a kick.
    public ICommand MemberTap { get; }

    // Listening only while it's in the window (a page let go of doesn't stay alive behind it).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Listen(true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Listen(false);
    }

    private TopLevel? _top;

    private void Listen(bool on)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVm;
            _vm.Chat.CollectionChanged -= OnChat;
            if (on)
            {
                _vm.PropertyChanged += OnVm;
                _vm.Chat.CollectionChanged += OnChat;
            }
        }
        if (_top?.InputPane is { } old) old.StateChanged -= OnKeyboard;
        _top = on ? TopLevel.GetTopLevel(this) : null;
        if (_top?.InputPane is { } pane) pane.StateChanged += OnKeyboard;
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        // A new room starts on its queue.
        if (e.PropertyName != nameof(TogetherViewModel.InRoom) || _vm?.InRoom != true) return;
        _tab = 0;
        Show();
    }

    private void OnChat(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ChatPane.IsEffectivelyVisible) Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    // The app is drawn under the keyboard: the chat's box goes up above it (by what the keyboard covers of this page).
    private void OnKeyboard(object? sender, Avalonia.Controls.Platform.InputPaneStateEventArgs e)
    {
        double h = e.NewState == Avalonia.Controls.Platform.InputPaneState.Open ? e.EndRect.Height : 0, below = 0;
        if (h > 0 && _top != null && this.TranslatePoint(new Point(0, Bounds.Height), _top) is { } end) below = _top.Bounds.Height - end.Y;
        ChatBar.Margin = new Thickness(12, 6, 12, 10 + Math.Max(0, h - below));
        if (h > 0 && ChatPane.IsEffectivelyVisible) Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void Pick(int tab, RadioButton b)
    {
        if (_syncing || b.IsChecked != true) return;
        _tab = tab;
        Show();
    }

    private void Show()
    {
        _syncing = true;
        TabQueue.IsChecked = _tab == 0;
        TabPeople.IsChecked = _tab == 1;
        TabChat.IsChecked = _tab == 2;
        _syncing = false;
        QueuePane.IsVisible = _tab == 0;
        PeoplePane.IsVisible = _tab == 1;
        ChatPane.IsVisible = _tab == 2;
        if (_tab == 2) Dispatcher.UIThread.Post(ChatScroll.ScrollToEnd, DispatcherPriority.Background);
        else _top?.FocusManager?.ClearFocus();
    }

    private void Max_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) Choose(L.T("Max people"), vm.MaxChoices, vm.MaxChoice, x => vm.MaxChoice = x);
    }

    private void Vote_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) Choose(L.T("Vote to skip"), vm.VoteChoices, vm.VoteChoice, x => vm.VoteChoice = x);
    }

    private void RandomSource_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) Choose(L.T("From"), vm.RandomSources, vm.RandomSource, x => vm.RandomSource = x);
    }

    private void RandomCount_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is { } vm) Choose(L.T("How many songs"), vm.RandomCounts, vm.RandomCount, x => vm.RandomCount = x);
    }

    // A row with an arrow: its choices in a sheet (the computer's drop-down).
    private static void Choose(string title, List<Choice> items, Choice? current, Action<Choice> set)
    {
        var menu = new SheetMenu { Title = title };
        foreach (var item in items)
        {
            var it = item;
            menu.Add(new SheetEntry(item.Label, Equals(item.Value, current?.Value) ? "\uE73E" : "", () => set(it)));
        }
        Menus.Open(menu);
    }

    public bool Back() => false;

    public void ScrollToTop()
    {
        Lobby.Offset = default;
        QueuePane.Offset = default;
        PeoplePane.Offset = default;
    }
}
