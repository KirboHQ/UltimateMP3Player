using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A song of a list on the phone: the shared row (number, song, the list it belongs to) and its tick while choosing songs.
public sealed class SongItem : Observable
{
    public SongItem(TrackRow row) => Row = row;

    public TrackRow Row { get; }
    public TrackViewModel Track => Row.Track;
    public ICommand PlayCommand => Row.PlayCommand;

    private bool _checked;
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
}

// One button of the bar at the bottom while choosing.
public sealed class PickAction
{
    public PickAction(string glyph, string label, Action run, bool danger = false)
    {
        Glyph = glyph;
        Label = label;
        Command = new RelayCommand(run);
        Danger = danger;
    }

    public string Glyph { get; }
    public string Label { get; }
    public ICommand Command { get; }
    public bool Danger { get; }
}

// Choosing several things of a list (the computer's Ctrl/Shift + click), like a phone's gallery: started with "Select"
// from an item's menu, then a tap ticks; the bar on top says how many, the one at the bottom what to do with them. Songs
// (rows, cards), songs of "next up", downloads or playlists: one kind at a time, the kind of the first one. Set on the
// list's page (Selection.Owner, inherited); the rows that show a tick have Selection.Tick (classes "picking"/"picked").
public sealed class Selection : Observable
{
    public static readonly AttachedProperty<Selection?> OwnerProperty =
        AvaloniaProperty.RegisterAttached<Control, Selection?>("Owner", typeof(Selection), inherits: true);

    public static readonly AttachedProperty<bool> TickProperty = AvaloniaProperty.RegisterAttached<Control, bool>("Tick", typeof(Selection));

    public static Selection? GetOwner(Control c) => c.GetValue(OwnerProperty);
    public static void SetOwner(Control c, Selection? v) => c.SetValue(OwnerProperty, v);
    public static bool GetTick(Control c) => c.GetValue(TickProperty);
    public static void SetTick(Control c, bool v) => c.SetValue(TickProperty, v);

    public static Selection? Of(Control c) => c.GetValue(OwnerProperty);

    // The rows on screen with a tick, by their selection (they leave it when they're taken off the screen).
    private static readonly ConditionalWeakTable<Control, Selection> RowOwners = new();
    private readonly HashSet<Control> _rows = new();

    static Selection()
    {
        TickProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.AttachedToVisualTree -= OnRowAttached;
            c.DetachedFromVisualTree -= OnRowDetached;
            c.DataContextChanged -= OnRowContext;
            if (e.NewValue is not true) return;
            c.AttachedToVisualTree += OnRowAttached;
            c.DetachedFromVisualTree += OnRowDetached;
            c.DataContextChanged += OnRowContext;
            if (c.GetVisualRoot() != null) Hook(c);
        });
    }

    private static void Hook(Control c)
    {
        if (Of(c) is not { } sel) return;
        RowOwners.AddOrUpdate(c, sel);
        sel._rows.Add(c);
        sel.Paint(c);
    }

    private static void OnRowAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c) Hook(c);
    }

    private static void OnRowDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c && RowOwners.TryGetValue(c, out var sel)) sel._rows.Remove(c);
    }

    private static void OnRowContext(object? sender, EventArgs e)
    {
        if (sender is Control c && RowOwners.TryGetValue(c, out var sel)) sel.Paint(c);
    }

    private void Paint(Control c)
    {
        bool picking = IsActive && SameKind(c.DataContext) && Contains(c.DataContext);
        c.Classes.Set("picking", picking);
        c.Classes.Set("picked", picking && IsChecked(c.DataContext));
    }

    private void PaintAll()
    {
        foreach (var r in _rows.ToList()) Paint(r);
    }

    public Selection()
    {
        CancelCommand = new RelayCommand(Stop);
        AllCommand = new RelayCommand(ToggleAll);
        MoreCommand = new RelayCommand(() => { if (Menus.ForSelection(this) is { } m) Menus.Open(m); }, () => Count > 0);
    }

    // ------------------------------------------------------------------ what's in the list

    // What can be chosen, in the list's order, and the list itself (a playlist: "remove from it").
    public IReadOnlyList<object> Items { get; private set; } = Array.Empty<object>();
    public ITrackList? Owner { get; private set; }
    private readonly HashSet<object> _checked = new(ReferenceEqualityComparer.Instance);
    private string? _kind;

    public static string? KindOf(object? x) => x switch
    {
        SongItem or TrackRow or TrackViewModel => "song",
        QueueRow => "queue",
        DownloadJobViewModel => "download",
        PlaylistViewModel => "playlist",
        _ => null,
    };

    public string? Kind => _kind;
    private bool SameKind(object? x) => x != null && KindOf(x) == _kind;
    public bool Contains(object? x) => x != null && Items.Contains(x);

    // What a choice is (it stays ticked when the list is filled again with new rows of the same things).
    private static object Key(object x) => x switch
    {
        SongItem s => s.Track.Id,
        TrackRow r => r.Track.Id,
        TrackViewModel t => t.Id,
        QueueRow q => "q:" + q.Track.Id,
        _ => x,
    };

    public void SetItems(IEnumerable<object> items, ITrackList? owner = null)
    {
        var keep = Picked.Select(Key).ToHashSet();
        foreach (var i in Items.OfType<SongItem>()) i.IsChecked = false;
        _checked.Clear();
        Items = items.Where(i => KindOf(i) != null).ToList();
        Owner = owner;
        foreach (var i in Items)
            if (keep.Contains(Key(i))) Mark(i, true);
        Update();
    }

    public bool IsChecked(object? x) => x is SongItem s ? s.IsChecked : x != null && _checked.Contains(x);

    private void Mark(object x, bool on)
    {
        if (x is SongItem s) s.IsChecked = on;
        else if (on) _checked.Add(x);
        else _checked.Remove(x);
    }

    // ------------------------------------------------------------------ choosing

    private bool _active;
    public bool IsActive { get => _active; private set => Set(ref _active, value); }

    public List<object> Picked => Items.Where(IsChecked).ToList();
    public int Count => Items.Count(IsChecked);
    public string CountText => _kind switch
    {
        "playlist" => L.Count(Count, "1 playlist selezionata", "{0} playlist selezionate"),
        "download" => L.Count(Count, "1 download selezionato", "{0} download selezionati"),
        _ => L.Count(Count, "1 brano selezionato", "{0} brani selezionati"),
    };
    public bool AllChecked => Items.Where(SameKind).All(IsChecked) && Items.Any(SameKind);

    // The songs among the chosen ones (rows, cards, songs of "next up").
    public List<TrackViewModel> Tracks => Picked.Select(x => x switch
    {
        SongItem s => s.Track,
        TrackRow r => r.Track,
        TrackViewModel t => t,
        QueueRow q => q.Track,
        _ => null,
    }).OfType<TrackViewModel>().Distinct().ToList();

    public List<QueueRow> QueueRows => Picked.OfType<QueueRow>().ToList();
    public List<DownloadJobViewModel> Jobs => Picked.OfType<DownloadJobViewModel>().ToList();
    public List<PlaylistViewModel> Playlists => Picked.OfType<PlaylistViewModel>().ToList();

    public ICommand CancelCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand MoreCommand { get; }

    // The buttons of the bar at the bottom, for what's chosen now.
    public List<PickAction> Actions { get; private set; } = new();

    public event Action? ActiveChanged;

    public SongItem? Find(TrackViewModel t) => Items.OfType<SongItem>().FirstOrDefault(i => i.Track == t);

    // first: the item whose "Select" started it (ticked), or null (nothing ticked yet, songs).
    public void Start(object? first)
    {
        foreach (var i in Items) Mark(i, false);
        _kind = KindOf(first) ?? KindOf(Items.FirstOrDefault());
        if (first != null && Contains(first)) Mark(first, true);
        IsActive = true;
        Haptics.Tick();
        Update();
        ActiveChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive) return;
        foreach (var i in Items) Mark(i, false);
        IsActive = false;
        _kind = null;
        Update();
        ActiveChanged?.Invoke();
    }

    // A tap on an item while choosing: ticked or not (only things of the kind being chosen). The last one unticked ends it.
    public void Toggle(object item)
    {
        if (!SameKind(item) || !Contains(item)) return;
        bool on = !IsChecked(item);
        Mark(item, on);
        Haptics.Tick();
        if (!on && Count == 0)
        {
            Stop();
            return;
        }
        Update();
    }

    private void ToggleAll()
    {
        bool on = !AllChecked;
        foreach (var i in Items.Where(SameKind)) Mark(i, on);
        Update();
    }

    private void Update()
    {
        Actions = IsActive ? Menus.SelectionActions(this) : new List<PickAction>();
        OnChanged(nameof(Count), nameof(CountText), nameof(AllChecked), nameof(Actions), nameof(Kind));
        PaintAll();
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
