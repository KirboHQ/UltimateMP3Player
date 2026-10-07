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

// Choosing several songs of a list (the computer's Ctrl/Shift + click): started from a song's menu, then a tap ticks; the
// bar on top says how many and what to do with them. Set on the list's page (Selection.Owner), the rows find it there.
public sealed class Selection : Observable
{
    public static readonly AttachedProperty<Selection?> OwnerProperty =
        AvaloniaProperty.RegisterAttached<Control, Selection?>("Owner", typeof(Selection), inherits: true);

    public static Selection? GetOwner(Control c) => c.GetValue(OwnerProperty);
    public static void SetOwner(Control c, Selection? v) => c.SetValue(OwnerProperty, v);

    public static Selection? Of(Control c) => c.GetValue(OwnerProperty);

    public Selection()
    {
        CancelCommand = new RelayCommand(Stop);
        AllCommand = new RelayCommand(ToggleAll);
        MoreCommand = new RelayCommand(() => { if (Menus.ForSelection(this) is { } m) Menus.Open(m); }, () => Count > 0);
        PlayCommand = new RelayCommand(() => { if (Tracks is { Count: > 0 } t) { t[0].Main.PlaySelection(t); Stop(); } }, () => Count > 0);
        QueueCommand = new RelayCommand(() => { if (Tracks is { Count: > 0 } t) { t[0].Main.Enqueue(t); Stop(); } }, () => Count > 0);
        PlaylistCommand = new RelayCommand(() => { if (Tracks is { Count: > 0 } t) Menus.Open(Menus.AddManyToPlaylist(t, this)); }, () => Count > 0);
        DeleteCommand = new RelayCommand(async () =>
        {
            if (Tracks is not { Count: > 0 } t) return;
            await t[0].Main.DeleteTracks(t);
            if (t.All(x => x.Main.Library.Get(x.Id) == null)) Stop();
        }, () => Count > 0);
    }

    // The list's songs and the list itself (a playlist: "remove from it").
    public IReadOnlyList<SongItem> Items { get; private set; } = Array.Empty<SongItem>();
    public ITrackList? Owner { get; private set; }

    public void SetItems(IReadOnlyList<SongItem> items, ITrackList? owner)
    {
        var ticked = Items.Where(i => i.IsChecked).Select(i => i.Track.Id).ToHashSet();
        Items = items;
        Owner = owner;
        foreach (var i in items) i.IsChecked = ticked.Contains(i.Track.Id);
        Update();
    }

    private bool _active;
    public bool IsActive { get => _active; private set => Set(ref _active, value); }

    public int Count => Items.Count(i => i.IsChecked);
    public string CountText => L.Count(Count, "1 brano selezionato", "{0} brani selezionati");
    public bool AllChecked => Items.Count > 0 && Items.All(i => i.IsChecked);
    public List<TrackViewModel> Tracks => Items.Where(i => i.IsChecked).Select(i => i.Track).ToList();

    public ICommand CancelCommand { get; }
    public ICommand AllCommand { get; }
    public ICommand MoreCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand QueueCommand { get; }
    public ICommand PlaylistCommand { get; }
    public ICommand DeleteCommand { get; }

    public event Action? ActiveChanged;

    public SongItem? Find(TrackViewModel t) => Items.FirstOrDefault(i => i.Track == t);

    public void Start(SongItem? first)
    {
        foreach (var i in Items) i.IsChecked = false;
        if (first != null) first.IsChecked = true;
        IsActive = true;
        Update();
        ActiveChanged?.Invoke();
    }

    public void Stop()
    {
        if (!IsActive) return;
        foreach (var i in Items) i.IsChecked = false;
        IsActive = false;
        Update();
        ActiveChanged?.Invoke();
    }

    public void Toggle(SongItem item)
    {
        item.IsChecked = !item.IsChecked;
        Haptics.Tick();
        Update();
    }

    private void ToggleAll()
    {
        bool on = !AllChecked;
        foreach (var i in Items) i.IsChecked = on;
        Update();
    }

    private void Update()
    {
        OnChanged(nameof(Count), nameof(CountText), nameof(AllChecked));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
