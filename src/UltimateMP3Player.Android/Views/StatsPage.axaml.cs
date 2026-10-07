using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class StatsPage : UserControl, IPage
{
    private StatsViewModel? _vm;
    private ListHeader? _header;
    private bool _selectNever;

    public StatsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        List.Picks.ActiveChanged += () =>
        {
            bool on = List.Picks.IsActive;
            SelectBar.IsVisible = on;
            TopBar.IsVisible = !on;
            SelectActions.IsVisible = on;
        };
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as StatsViewModel;
        _header = _vm != null ? new ListHeader(_vm) : null;
        if (_vm != null && this.GetVisualRoot() != null) _vm.PropertyChanged += OnVm;
        Fill();
    }

    // Listening only while it's in the window (a page let go of doesn't stay alive behind it), sorting again only while
    // it's on screen (every listen changes the numbers).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm == null) return;
        _vm.PropertyChanged -= OnVm;
        _vm.PropertyChanged += OnVm;
        if (_stale) Shown();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _stale = true;
    }

    private bool _stale, _pending;

    public void Shown()
    {
        if (!_stale) return;
        _stale = false;
        Fill();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(StatsViewModel.Rows)) return;
        if (!IsEffectivelyVisible)
        {
            _stale = true;
            return;
        }
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Fill();
        }, DispatcherPriority.Background);
    }

    // While a song plays the list is sorted again: the scroll and the ticks stay (Selection.SetItems keeps them).
    private void Fill()
    {
        if (_vm == null || _header == null) return;
        var sv = List.FindDescendantOfType<ScrollViewer>();
        var offset = sv?.Offset ?? default;
        var items = _vm.Rows.Select(r => new SongItem(r)).ToList();
        List.Picks.SetItems(items, _vm);
        var all = new List<object> { _header };
        all.AddRange(items);
        if (items.Count == 0) all.Add(new ListNote(L.T("Ancora niente"), _vm.EmptyText));
        List.ItemsSource = all;
        if (sv != null) Dispatcher.UIThread.Post(() => sv.Offset = offset, DispatcherPriority.Background);
        if (_selectNever)
        {
            _selectNever = false;
            List.Picks.Start(null);
            List.Picks.AllCommand.Execute(null);
        }
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => App.Host.Session?.BackCommand.Execute(null);

    private void Select_Click(object? sender, RoutedEventArgs e)
    {
        if (List.Picks.Items.Count > 0) List.Picks.Start(null);
    }

    private void Sort_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        var menu = new SheetMenu { Title = L.T("Ordina per") };
        foreach (var s in vm.Sorts)
        {
            var choice = s;
            menu.Add(new SheetEntry(s.Label, s == vm.Sort ? "" : "", () => vm.Sort = choice));
        }
        Menus.Open(menu);
    }

    // "Choose the n never played": they're shown by themselves, all ticked.
    private void Never_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        _selectNever = true;
        _vm.ShowNever();
        Fill();
    }

    public bool Back()
    {
        if (!List.Picks.IsActive) return false;
        List.Picks.Stop();
        return true;
    }

    public void ScrollToTop()
    {
        if (List.FindDescendantOfType<ScrollViewer>() is { } sv) sv.Offset = default;
    }
}
