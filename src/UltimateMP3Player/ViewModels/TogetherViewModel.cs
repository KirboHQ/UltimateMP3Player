using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Together;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

// A room found on the network.
public sealed class RoomAdViewModel
{
    public RoomAdViewModel(RoomAd ad) => Ad = ad;

    public RoomAd Ad { get; }
    public string Name => Ad.Name;
    public string Initial => string.IsNullOrWhiteSpace(Ad.Host) ? "?" : char.ToUpper(Ad.Host.Trim()[0]).ToString();
    public Brush HostBrush => Ui.BrushFrom(Ad.HostColor ?? "#7C5CFF");
    public string Detail => L.F("di {0}", Ad.Host) + " · " + L.F("{0} di {1} persone", Ad.Members, Ad.Max);
    public string? Playing => Ad.Playing != null ? L.F("Sta suonando «{0}»", Ad.Playing) : null;
    public bool Locked => Ad.Locked;
    public bool IsFull => Ad.Members >= Ad.Max;
    public bool CanJoin => !IsFull;
    public string JoinText => IsFull ? L.T("Piena") : L.T("Entra");
    public string Key => $"{Ad.RoomId}|{Ad.Name}|{Ad.Members}|{Ad.Max}|{Ad.Playing}|{Ad.Ip}|{Ad.Port}|{Ad.Locked}";
}

// A person in the room.
public sealed class MemberViewModel : Observable
{
    private readonly TogetherViewModel _owner;
    private byte[]? _avatarBytes;
    private ImageSource? _avatar;

    public MemberViewModel(Member m, TogetherViewModel owner)
    {
        M = m;
        _owner = owner;
    }

    public Member M { get; private set; }
    public string Id => M.Id;
    public string Name => M.Name;
    public string Initial => string.IsNullOrWhiteSpace(M.Name) ? "?" : char.ToUpper(M.Name.Trim()[0]).ToString();
    public Brush Brush => Ui.BrushFrom(M.Color);
    public ImageSource? Avatar
    {
        get
        {
            if (!ReferenceEquals(_avatarBytes, M.Avatar))
            {
                _avatarBytes = M.Avatar;
                _avatar = TogetherViewModel.ImageFrom(M.Avatar, 96);
            }
            return _avatar;
        }
    }
    public bool IsHost => _owner.Session?.HostId == M.Id;
    public bool IsMe => _owner.Session?.Me.Id == M.Id;
    public bool Away => M.Away;
    public bool NoP2P => M.NoP2P;
    public string NameText => IsMe ? L.F("{0} (tu)", M.Name) : M.Name;

    public string RoleText => IsHost ? L.T("Host") : M.Perms == Perm.All ? L.T("Co-host") : (M.Perms & Perm.Add) != 0 ? "DJ" : L.T("Ascoltatore");

    // The permissions, lit or dim.
    public bool PermAdd => IsHost || (M.Perms & Perm.Add) != 0;
    public bool PermRemove => IsHost || (M.Perms & Perm.Remove) != 0;
    public bool PermSkip => IsHost || (M.Perms & Perm.Skip) != 0;
    public bool PermPause => IsHost || (M.Perms & Perm.Pause) != 0;
    public bool PermSpeed => IsHost || (M.Perms & Perm.Speed) != 0;
    public string PermsTip => IsHost ? L.T("L'host può fare tutto.") : TogetherViewModel.PermsText(M.Perms);

    // How far they are with the song playing and the next ones.
    public string StatusText { get; private set; } = "";
    public double StatusPct { get; private set; }
    public bool StatusBusy { get; private set; }
    public bool StatusReady { get; private set; }
    public bool StatusFailed { get; private set; }

    private bool _isSelected;
    // Only the host selects people (to change what they may do).
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value && _owner.IsHost && !IsMe)) _owner.OnMemberSelection(); } }

    public void Update(Member m)
    {
        M = m;
        var s = _owner.Session;
        if (s != null) UpdateStatus(s);
        OnChanged(nameof(Name), nameof(Initial), nameof(Brush), nameof(Avatar), nameof(IsHost), nameof(IsMe), nameof(Away), nameof(NoP2P), nameof(NameText),
            nameof(RoleText), nameof(PermAdd), nameof(PermRemove), nameof(PermSkip), nameof(PermPause), nameof(PermSpeed), nameof(PermsTip),
            nameof(StatusText), nameof(StatusPct), nameof(StatusBusy), nameof(StatusReady), nameof(StatusFailed));
    }

    private void UpdateStatus(TogetherSession s)
    {
        StatusBusy = StatusReady = StatusFailed = false;
        StatusPct = 0;
        if (M.Away)
        {
            StatusText = L.T("Si sta ricollegando…");
            return;
        }
        var window = new[] { s.Current }.Concat(s.Queue.Take(s.AheadOf(M))).OfType<RoomTrack>().ToList();
        if (window.Count == 0)
        {
            StatusText = L.T("Pronto");
            StatusReady = true;
            return;
        }
        foreach (var t in window)
        {
            var st = s.StatusOf(M.Id, t.Id);
            if (st?.State == FileState.Ready) continue;
            bool current = t == s.Current;
            StatusPct = st?.Pct ?? 0;
            switch (st?.State)
            {
                case FileState.Downloading:
                    StatusBusy = true;
                    StatusText = current ? L.F("Scarica il brano attuale · {0:0}%", StatusPct) : L.F("Prepara «{0}» · {1:0}%", t.Title, StatusPct);
                    return;
                case FileState.Transfer:
                    StatusBusy = true;
                    StatusText = current ? L.F("Riceve il brano attuale · {0:0}%", StatusPct) : L.F("Riceve «{0}» · {1:0}%", t.Title, StatusPct);
                    return;
                case FileState.Failed:
                    StatusFailed = true;
                    StatusText = L.F("Non riesce a scaricare «{0}»", t.Title);
                    return;
                default:
                    if (!current) continue;
                    StatusBusy = true;
                    StatusText = L.T("In attesa del brano…");
                    return;
            }
        }
        StatusReady = true;
        StatusText = L.T("Pronto");
    }
}

// A song of the room (playing or in the queue).
public sealed class RoomItemViewModel : Observable
{
    private readonly TogetherViewModel _owner;

    public RoomItemViewModel(RoomTrack item, TogetherViewModel owner)
    {
        Item = item;
        _owner = owner;
        Track = owner.TrackFor(item);
    }

    public RoomTrack Item { get; private set; }
    public string Id => Item.Id;
    public TrackViewModel Track { get; private set; }
    public int Number { get; private set; }
    public bool IsCurrent { get; private set; }

    public string AddedByName => _owner.MemberName(Item.AddedBy);
    public Brush AddedByBrush => _owner.MemberBrush(Item.AddedBy);
    public string AddedByInitial => AddedByName.Length > 0 ? char.ToUpper(AddedByName[0]).ToString() : "?";
    public string AddedByText => L.F("aggiunto da {0}", AddedByName);
    public bool IsMine => _owner.Session?.Me.Id == Item.AddedBy;

    // This computer's copy of the file.
    public FileState State { get; private set; }
    public double Pct { get; private set; }
    public bool IsReady => State == FileState.Ready;
    public bool IsBusy => State is FileState.Downloading or FileState.Transfer;
    public bool IsFailed => State == FileState.Failed;
    public string StateTip => State switch
    {
        FileState.Ready => _owner.InLibrary(Item.Id) ? L.T("Pronto: è nella tua libreria") : L.T("Pronto"),
        FileState.Downloading => L.F("Lo stai scaricando · {0:0}%", Pct),
        FileState.Transfer when _owner.WaitingHost(Item.Id) => _owner.HostWaitText(Item.Id),
        FileState.Transfer => L.F("Lo stai ricevendo da qualcuno nella stanza · {0:0}%", Pct),
        FileState.Failed when !_owner.P2P => L.T("Non si riesce a scaricarlo dal suo link, e senza P2P nessuno può mandartelo"),
        FileState.Failed => L.T("Non si riesce ad averlo: si riprova tra poco"),
        _ => L.T("Verrà preparato quando si avvicina il suo turno"),
    };
    public string PctText => IsBusy ? $"{Pct:0}%" : "";
    public bool InLibrary => _owner.InLibrary(Item.Id);
    public bool CanRemove => _owner.CanRemoveItem(Item);

    public ICommand RemoveCommand => new RelayCommand(() => _owner.Remove(this));
    public ICommand SaveCommand => new RelayCommand(p => _owner.SaveMenu(this, p as UIElement));
    public ICommand PlayNowCommand => new RelayCommand(() => _owner.PlayNow(Id));
    public string RemoveTip => CanRemove ? L.T("Togli dalla coda") : L.T("Solo chi ha il permesso può togliere i brani degli altri");

    public void Update(RoomTrack item, int number, bool current)
    {
        Item = item;
        Number = number;
        IsCurrent = current;
        Refresh();
    }

    public void Refresh()
    {
        var st = _owner.MyStatus(Item.Id);
        State = st?.State ?? FileState.None;
        Pct = st?.Pct ?? 0;
        var track = _owner.TrackFor(Item);
        if (track != Track) Track = track;
        OnChanged(nameof(Item), nameof(Track), nameof(Number), nameof(IsCurrent), nameof(AddedByName), nameof(AddedByBrush), nameof(AddedByInitial),
            nameof(AddedByText), nameof(IsMine), nameof(State), nameof(Pct), nameof(IsReady), nameof(IsBusy), nameof(IsFailed), nameof(StateTip),
            nameof(PctText), nameof(InLibrary), nameof(CanRemove), nameof(RemoveTip));
    }
}

public sealed class ChatLineViewModel
{
    public ChatLineViewModel(ChatLine line, bool mine, bool showName)
    {
        Line = line;
        IsMine = mine;
        ShowName = showName && !line.System;
    }

    public ChatLine Line { get; }
    public bool IsSystem => Line.System;
    public bool IsMine { get; }
    public bool ShowName { get; }
    public string Name => Line.Name;
    public Brush Brush => Ui.BrushFrom(Line.Color ?? "#9AA3B5");
    public string Text => Line.Display;
    public string Time => DateTimeOffset.FromUnixTimeMilliseconds(Line.Time).ToLocalTime().ToString("HH:mm");
}

// The "Listen together" page and everything the room does to the rest of the app.
public sealed class TogetherViewModel : Observable
{
    // Output buffer of the player: what you hear is this much behind what it has read (AudioEngine.Position).
    private static readonly double Lead = AudioEngine.LatencyMs / 1000.0;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _follow = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
    private TogetherSession? _s;
    private TogetherFetcher? _fetcher;
    private readonly Dictionary<string, Track> _ephemeral = new();
    private readonly Dictionary<string, RoomItemViewModel> _items = new();
    private bool _shown, _scanning, _loading, _justSeeked;
    private long _lastSeek, _failedUntil;
    private string? _failedItem;
    // Extra delay measured after a jump on this song (slow decoders): seeks aim this much ahead.
    private double _lag;
    private string? _joinedAddress;
    private DateTime _lastDeny = DateTime.MinValue;

    public TogetherViewModel(MainViewModel main)
    {
        _main = main;
        var s = main.Host.Settings;
        _newRoomName = L.F("Stanza di {0}", main.ProfileName);
        MaxChoices = new[] { 2, 3, 4, 5, 6, 8, 10, 12, 16 }.Select(n => new Choice(L.F("{0} persone", n), n)).ToList();
        _maxChoice = MaxChoices.FirstOrDefault(c => (int)c.Value! == s.TogetherMax) ?? MaxChoices[5];
        _defaultPerms = (Perm)s.TogetherRoomPerms & Perm.All;
        _scanTimer.Tick += (_, _) => _ = Scan();
        _follow.Tick += (_, _) => Follow();

        CreateCommand = new RelayCommand(Create, () => !Busy);
        JoinCommand = new RelayCommand(p => { if (p is RoomAdViewModel r && r.Ad.Ip != null) _ = Join(r.Ad.Ip, r.Ad.Port, r.Locked, r.Name); }, _ => !Busy);
        JoinByAddressCommand = new RelayCommand(JoinByAddress, () => !Busy);
        RefreshCommand = new RelayCommand(() => _ = Scan());
        LeaveCommand = new RelayCommand(() => ConfirmLeave(null, L.T("Esci dalla stanza")));
        SendCommand = new RelayCommand(Send, () => ChatInput.Trim().Length > 0);
        CopyAddressCommand = new RelayCommand(CopyAddress);
        FirewallCommand = new RelayCommand(() => _ = AllowFirewall());
        DismissMessageCommand = new RelayCommand(() => LobbyMessage = null);
        MemberMenuCommand = new RelayCommand(p => { if (p is UIElement e) Views.Menus.Open(Views.Menus.ForMembers(this), e, true); });
        TogglePermCommand = new RelayCommand(p => { if (p is string name && Enum.TryParse<Perm>(name, out var perm)) TogglePerm(perm); });
        KickSelectedCommand = new RelayCommand(KickSelected);
        ClearSelectionCommand = new RelayCommand(() => { foreach (var m in Members) m.IsSelected = false; });
        ToggleRandomCommand = new RelayCommand(() => ShowRandom = !ShowRandom);
        AddRandomCommand = new RelayCommand(AddRandom, () => !RandomBusy);
    }

    public ICommand ToggleRandomCommand { get; }
    public ICommand AddRandomCommand { get; }

    public MainViewModel Main => _main;
    public TogetherSession? Session => _s;
    public bool InRoom => _s != null;
    public bool IsHost => _s?.IsHost == true;

    public ICommand CreateCommand { get; }
    public ICommand JoinCommand { get; }
    public ICommand JoinByAddressCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand CopyAddressCommand { get; }
    public ICommand FirewallCommand { get; }
    public ICommand DismissMessageCommand { get; }
    public ICommand MemberMenuCommand { get; }
    public ICommand TogglePermCommand { get; }
    public ICommand KickSelectedCommand { get; }
    public ICommand ClearSelectionCommand { get; }

    private TogetherCache Cache => _main.Host.SongCache;

    // ------------------------------------------------------------------ page

    public void Shown()
    {
        _shown = true;
        if (!InRoom)
        {
            _ = Scan();
            _scanTimer.Start();
        }
    }

    public void Hidden()
    {
        _shown = false;
        _scanTimer.Stop();
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? _busyText;
    public string? BusyText { get => _busyText; private set => Set(ref _busyText, value); }

    private string? _lobbyMessage;
    // Why the last room ended or a join failed: shown on top of the lobby.
    public string? LobbyMessage { get => _lobbyMessage; set => Set(ref _lobbyMessage, value); }

    // ------------------------------------------------------------------ lobby: rooms around

    public ObservableCollection<RoomAdViewModel> Rooms { get; } = new();
    public bool HasRooms => Rooms.Count > 0;
    public bool Scanning { get => _scanning; private set => Set(ref _scanning, value); }

    private async Task Scan()
    {
        if (Scanning || InRoom) return;
        Scanning = true;
        List<RoomAd> found;
        try { found = await Discovery.ScanAsync(900); }
        catch { found = new List<RoomAd>(); }
        Scanning = false;
        if (InRoom) return;
        var fresh = found.Where(a => a.Proto == TogetherSession.Proto).Select(a => new RoomAdViewModel(a)).ToList();
        // Updated in place: no flicker while the list stays the same.
        if (fresh.Select(r => r.Key).SequenceEqual(Rooms.Select(r => r.Key))) return;
        Rooms.Clear();
        foreach (var r in fresh) Rooms.Add(r);
        OnChanged(nameof(HasRooms));
    }

    // ------------------------------------------------------------------ lobby: create

    private string _newRoomName;
    public string NewRoomName { get => _newRoomName; set => Set(ref _newRoomName, value); }

    public List<Choice> MaxChoices { get; }
    private Choice _maxChoice;
    public Choice MaxChoice { get => _maxChoice; set { if (value != null) Set(ref _maxChoice, value); } }

    private string _newPassword = "";
    public string NewPassword { get => _newPassword; set => Set(ref _newPassword, value); }

    private Perm _defaultPerms;
    public bool DefAdd { get => Has(Perm.Add); set => SetDefault(Perm.Add, value); }
    public bool DefRemove { get => Has(Perm.Remove); set => SetDefault(Perm.Remove, value); }
    public bool DefSkip { get => Has(Perm.Skip); set => SetDefault(Perm.Skip, value); }
    public bool DefPause { get => Has(Perm.Pause); set => SetDefault(Perm.Pause, value); }
    public bool DefSpeed { get => Has(Perm.Speed); set => SetDefault(Perm.Speed, value); }
    private bool Has(Perm p) => (_defaultPerms & p) != 0;

    private void SetDefault(Perm p, bool on)
    {
        _defaultPerms = on ? _defaultPerms | p : _defaultPerms & ~p;
        OnChanged(nameof(DefAdd), nameof(DefRemove), nameof(DefSkip), nameof(DefPause), nameof(DefSpeed));
    }

    public static string PermsText(Perm p) => p == Perm.None ? L.T("Può solo ascoltare.") : L.T("Può") + " " + PermsList(p) + ".";

    private static string PermsList(Perm p)
    {
        var parts = new List<string>();
        if ((p & Perm.Add) != 0) parts.Add(L.T("aggiungere brani"));
        if ((p & Perm.Remove) != 0) parts.Add(L.T("togliere e spostare brani"));
        if ((p & Perm.Skip) != 0) parts.Add(L.T("saltare"));
        if ((p & Perm.Pause) != 0) parts.Add(L.T("mettere in pausa, andare avanti e indietro e ripetere il brano"));
        if ((p & Perm.Speed) != 0) parts.Add(L.T("cambiare la velocità"));
        return string.Join(", ", parts);
    }

    private void Create()
    {
        if (InRoom) return;
        var settings = _main.Host.Settings;
        settings.TogetherMax = (int)MaxChoice.Value!;
        settings.TogetherRoomPerms = (int)_defaultPerms;
        settings.Save();
        var s = NewSession();
        try
        {
            s.Create(new RoomOptions { Name = NewRoomName, Max = (int)MaxChoice.Value!, Password = NewPassword, DefaultPerms = _defaultPerms });
        }
        catch (Exception ex)
        {
            s.Dispose();
            LobbyMessage = L.T("Impossibile creare la stanza:") + " " + ex.Message;
            return;
        }
        LobbyMessage = null;
        Attach(s, null);
    }

    // ------------------------------------------------------------------ lobby: join

    private async Task Join(string ip, int port, bool locked, string? name)
    {
        if (InRoom || Busy) return;
        string? password = null;
        if (locked)
        {
            password = Dialogs.Prompt(L.T("Stanza con password"), L.F("Password di «{0}»", name ?? ip), "");
            if (password == null) return;
        }
        Busy = true;
        BusyText = L.F("Entro in «{0}»…", name ?? ip);
        LobbyMessage = null;
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var s = NewSession();
                var r = await s.JoinAsync(ip, port, password);
                if (r.Ok)
                {
                    Attach(s, $"{ip}:{port}");
                    return;
                }
                s.Dispose();
                // The room turned out to have a password: ask once.
                if (r.NeedsPassword && attempt == 0)
                {
                    password = Dialogs.Prompt(L.T("Stanza con password"), r.Error ?? L.T("Password"), "");
                    if (password == null) return;
                    continue;
                }
                LobbyMessage = r.Error ?? L.T("Non è stato possibile entrare nella stanza.");
                return;
            }
        }
        finally
        {
            Busy = false;
            BusyText = null;
        }
    }

    // Rooms not found by the search (another network, broadcasts blocked): by address.
    private void JoinByAddress()
    {
        var settings = _main.Host.Settings;
        var text = Dialogs.Prompt(L.T("Entra con l'indirizzo"),
            L.T("Indirizzo del computer che ospita la stanza (per esempio 192.168.1.20, oppure quello di Radmin VPN)"),
            settings.TogetherLastAddress ?? "");
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        int port = TogetherSession.FirstPort;
        var host = text;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            host = text[..colon];
            port = p;
        }
        settings.TogetherLastAddress = text;
        settings.Save();
        _ = Join(host, port, false, host);
    }

    private TogetherSession NewSession()
    {
        var settings = _main.Host.Settings;
        if (string.IsNullOrEmpty(settings.TogetherId))
        {
            settings.TogetherId = Guid.NewGuid().ToString("N");
            settings.Save();
        }
        var info = _main.Profile.Info;
        var me = new Member
        {
            Id = settings.TogetherId, Name = info.Name, Color = info.Color, Version = AppInfo.VersionText,
            Avatar = info.HasAvatar ? JpegOf(info.AvatarPath, 96) : null,
            Ahead = AheadSetting(settings.TogetherAhead), HostAhead = AheadSetting(settings.TogetherHostAhead),
            SendsFiles = settings.TogetherSendAsHost && settings.TogetherP2P, NoP2P = !settings.TogetherP2P,
        };
        var ui = Application.Current.Dispatcher;
        return new TogetherSession(me, a => ui.BeginInvoke(a));
    }

    // ------------------------------------------------------------------ in the room

    private void Attach(TogetherSession s, string? address)
    {
        _s = s;
        _joinedAddress = address;
        var settings = _main.Host.Settings;
        _fetcher = new TogetherFetcher(s, _main.Library, Cache,
            () => new FetchOptions(settings.AudioFormat, settings.CookiesBrowserOrNull, Math.Clamp(settings.TogetherCacheSize, 1, 200),
                AheadSetting(settings.TogetherAhead), AheadSetting(settings.TogetherHostAhead), settings.TogetherTakeFromHost, settings.TogetherP2P));
        s.RoomChanged += OnRoom;
        s.QueueChanged += OnQueue;
        s.PlaybackChanged += OnPlayback;
        s.FilesChanged += OnFiles;
        s.ChatAdded += OnChat;
        s.Ended += OnEnded;
        s.Notice += OnNotice;
        s.CoverArrived += OnCover;
        _fetcher.Changed += OnMyFiles;
        foreach (var (id, bytes) in s.Covers) OnCover(id, bytes);
        Chat.Clear();
        foreach (var line in s.Chat) OnChat(line);
        _scanTimer.Stop();
        _main.Player.EnterRoom(this);
        _follow.Start();
        OnRoom();
        OnQueue();
        OnPlayback();
        _fetcher.Refresh();
        OnChanged(nameof(InRoom), nameof(IsHost));
        _main.OnRoomChanged();
        if (_main.Page != this) _main.Navigate(this);
    }

    private void Detach()
    {
        var s = _s;
        if (s == null) return;
        s.RoomChanged -= OnRoom;
        s.QueueChanged -= OnQueue;
        s.PlaybackChanged -= OnPlayback;
        s.FilesChanged -= OnFiles;
        s.ChatAdded -= OnChat;
        s.Ended -= OnEnded;
        s.Notice -= OnNotice;
        s.CoverArrived -= OnCover;
        if (_fetcher != null)
        {
            _fetcher.Changed -= OnMyFiles;
            _fetcher.Dispose();
        }
        _fetcher = null;
        _s = null;
        _follow.Stop();
        _loading = false;
        _main.Player.LeaveRoom();
        foreach (var id in _ephemeral.Keys)
        {
            _main.ForgetVm("lt-" + id);
            try { File.Delete(AppPaths.TrackCover("lt-" + id)); } catch { } LyricsStore.Delete("lt-" + id);
        }
        _ephemeral.Clear();
        _items.Clear();
        Members.Clear();
        Queue = new List<RoomItemViewModel>();
        CurrentItem = null;
        Chat.Clear();
        ShowRandom = false;
        _suggestedUsed.Clear();
        OnChanged(nameof(InRoom), nameof(IsHost), nameof(Queue), nameof(HasQueue), nameof(QueueCountText), nameof(MemberCount), nameof(RoomName),
            nameof(RoomSubtitle), nameof(Reconnecting), nameof(CanAdd), nameof(CanRemove), nameof(CanSkip), nameof(CanPause), nameof(CanSpeed));
        _main.OnRoomChanged();
        if (_shown)
        {
            _ = Scan();
            _scanTimer.Start();
        }
    }

    // Asks first (with why), then leaves; false = stays. Not in a room: true.
    public bool ConfirmLeave(string? why, string action)
    {
        if (!InRoom) return true;
        var text = why ?? L.F("Uscirai da «{0}».", RoomName);
        if (IsHost && _s!.Members.Count(m => !m.Away) > 1) text += "\n\n" + L.T("Sei l'host: la stanza passerà a chi ha più permessi (a parità, a chi è entrato per primo).");
        if (!Dialogs.Confirm(L.T("Uscire dalla stanza?"), text, action, true)) return false;
        Leave();
        return true;
    }

    // Leaves now; the goodbye to the others (or the handover of the room) finishes in the background.
    public void Leave()
    {
        var s = _s;
        if (s == null) return;
        Detach();
        _ = Goodbye(s);
    }

    private static async Task Goodbye(TogetherSession s)
    {
        try { await s.LeaveAsync(); } catch { }
        s.Dispose();
    }

    // Closing the app: the goodbye has to be out before the process ends.
    public void LeaveForExit()
    {
        var s = _s;
        if (s == null) return;
        Detach();
        try { Task.Run(() => s.LeaveAsync()).Wait(1500); } catch { }
        s.Dispose();
    }

    private void OnEnded(string reason)
    {
        var name = RoomName;
        Detach();
        LobbyMessage = L.T(reason);
        _main.Toast(L.F("Sei uscito da «{0}»: {1}", name, L.T(reason)));
    }

    private void OnNotice(string key)
    {
        // A burst of refusals (e.g. arrow keys held) shows once.
        if ((DateTime.Now - _lastDeny).TotalSeconds < 2) return;
        _lastDeny = DateTime.Now;
        _main.Toast(L.T(key));
    }

    // ------------------------------------------------------------------ room: header and people

    public string RoomName => _s?.RoomName ?? "";
    public int MemberCount => _s?.Members.Count(m => !m.Away) ?? 0;
    public bool Reconnecting => _s?.Status == RoomStatus.Reconnecting;
    public string? HostName => _s?.Host?.Name;

    public string RoomSubtitle
    {
        get
        {
            if (_s == null) return "";
            var parts = new List<string> { L.F("{0} di {1} persone", MemberCount, _s.Max) };
            if (IsHost) parts.Add(L.T("sei l'host"));
            else if (HostName != null) parts.Add(L.F("ospitata da {0}", HostName));
            if (_s.Locked) parts.Add(L.T("con password"));
            if (!IsHost && _s.Latency > 0) parts.Add(L.F("ritardo {0} ms", _s.Latency));
            return string.Join(" · ", parts);
        }
    }

    // What others type to join by address.
    public string AddressText
    {
        get
        {
            if (_s == null) return "";
            if (!IsHost) return _joinedAddress != null ? L.F("Collegato a {0}", _joinedAddress) : "";
            var ips = Discovery.LocalAddresses();
            if (ips.Count == 0) return "";
            string port = _s.ListenPort == TogetherSession.FirstPort ? "" : ":" + _s.ListenPort;
            return L.T("Indirizzo per entrare:") + " " + string.Join("  ·  ", ips.Take(3).Select(ip => ip + port));
        }
    }

    public ObservableCollection<MemberViewModel> Members { get; } = new();

    private void OnRoom()
    {
        var s = _s;
        if (s == null) return;
        var byId = Members.ToDictionary(m => m.Id);
        var ordered = s.Members.OrderByDescending(m => m.Id == s.HostId).ThenBy(m => m.Seq).ToList();
        foreach (var gone in Members.Where(m => ordered.All(x => x.Id != m.Id)).ToList()) Members.Remove(gone);
        for (int i = 0; i < ordered.Count; i++)
        {
            var m = ordered[i];
            if (!byId.TryGetValue(m.Id, out var vm))
            {
                vm = new MemberViewModel(m, this);
                Members.Insert(Math.Min(i, Members.Count), vm);
            }
            else if (Members.IndexOf(vm) != i) Members.Move(Members.IndexOf(vm), Math.Min(i, Members.Count - 1));
            vm.Update(m);
        }
        foreach (var item in _items.Values) item.Refresh();
        OnChanged(nameof(RoomName), nameof(MemberCount), nameof(Reconnecting), nameof(HostName), nameof(RoomSubtitle), nameof(AddressText), nameof(IsHost),
            nameof(CanAdd), nameof(CanRemove), nameof(CanSkip), nameof(CanPause), nameof(CanSpeed), nameof(MyRoleText), nameof(AddHint));
        OnMemberSelection();
        _main.OnRoomChanged();
        _main.Player.OnRoomPermissions();
    }

    private void OnFiles()
    {
        var s = _s;
        if (s == null) return;
        foreach (var vm in Members)
            if (s.Members.FirstOrDefault(m => m.Id == vm.Id) is { } m) vm.Update(m);
        // Songs coming from the host show how far the host is.
        foreach (var item in _items.Values)
            if (WaitingHost(item.Id)) item.Refresh();
        UpdateNowPlaying();
    }

    public string MemberName(string id) => _s?.Members.FirstOrDefault(m => m.Id == id)?.Name ?? L.T("qualcuno che è uscito");
    public Brush MemberBrush(string id) => Ui.BrushFrom(_s?.Members.FirstOrDefault(m => m.Id == id)?.Color ?? "#687084");

    // ------------------------------------------------------------------ permissions

    public bool CanAdd => _s?.Can(Perm.Add) == true;
    public bool CanRemove => _s?.Can(Perm.Remove) == true;
    public bool CanSkip => _s?.Can(Perm.Skip) == true;
    // Pause, seek and loop are one permission.
    public bool CanPause => _s?.Can(Perm.Pause) == true;
    public bool CanSpeed => _s?.Can(Perm.Speed) == true;
    public bool CanRemoveItem(RoomTrack t) => CanRemove || _s?.Me.Id == t.AddedBy;

    public string MyRoleText => _s == null ? "" : IsHost ? L.T("Sei l'host: puoi fare tutto e decidi cosa possono fare gli altri.") : PermsText(_s.MyPerms);
    public string AddHint => CanAdd
        ? L.T("Nelle tue playlist e nelle librerie il tasto + (o il doppio clic) mette i brani qui.")
        : L.T("Solo chi ha il permesso può aggiungere brani: lo decide l'host.");

    private void Deny(string key)
    {
        OnNotice(key);
    }

    public string Denied(Perm p) => L.T(p switch
    {
        Perm.Add => "Non hai il permesso di aggiungere brani alla stanza (lo decide l'host).",
        Perm.Remove => "Non hai il permesso di togliere o spostare brani (lo decide l'host).",
        Perm.Skip => "Non hai il permesso di saltare i brani (lo decide l'host).",
        Perm.Pause => "Non hai il permesso di mettere in pausa o andare avanti e indietro (lo decide l'host).",
        _ => "Non hai il permesso di cambiare la velocità (lo decide l'host).",
    });

    // Host: the people selected in the list.
    public List<MemberViewModel> SelectedMembers => Members.Where(m => m.IsSelected && !m.IsMe).ToList();
    public bool HasSelection => IsHost && SelectedMembers.Count > 0;
    public string SelectionText => L.Count(SelectedMembers.Count, "1 persona selezionata", "{0} persone selezionate");

    // Tick = all the selected have it, dash = only some.
    public bool? SelAdd => SelState(Perm.Add);
    public bool? SelRemove => SelState(Perm.Remove);
    public bool? SelSkip => SelState(Perm.Skip);
    public bool? SelPause => SelState(Perm.Pause);
    public bool? SelSpeed => SelState(Perm.Speed);

    private bool? SelState(Perm p)
    {
        var sel = SelectedMembers;
        if (sel.Count == 0) return false;
        int n = sel.Count(m => (m.M.Perms & p) != 0);
        return n == 0 ? false : n == sel.Count ? true : null;
    }

    public void OnMemberSelection()
        => OnChanged(nameof(HasSelection), nameof(SelectionText), nameof(SelAdd), nameof(SelRemove), nameof(SelSkip), nameof(SelPause), nameof(SelSpeed));

    public void TogglePerm(Perm p) => TogglePerm(SelectedMembers, p);

    public void TogglePerm(IReadOnlyList<MemberViewModel> who, Perm p)
    {
        if (_s == null || !IsHost || who.Count == 0) return;
        bool on = who.Any(m => (m.M.Perms & p) == 0);
        _s.SetPerm(who.Select(m => m.Id), p, on);
    }

    public void SetAllPerms(IReadOnlyList<MemberViewModel> who, bool on)
    {
        if (_s == null || !IsHost || who.Count == 0) return;
        _s.SetPerm(who.Select(m => m.Id), Perm.All, on);
    }

    private void KickSelected() => Kick(SelectedMembers);

    public void Kick(IReadOnlyList<MemberViewModel> who)
    {
        if (_s == null || !IsHost || who.Count == 0) return;
        var names = string.Join(", ", who.Select(m => m.Name));
        if (!Dialogs.Confirm(who.Count == 1 ? L.T("Espellere dalla stanza?") : L.F("Espellere {0} persone?", who.Count),
                L.F("{0} verrà tolto dalla stanza e non potrà rientrarci finché resta aperta.", names), L.T("Espelli dalla stanza"), true)) return;
        foreach (var m in who) _s.KickMember(m.Id);
    }

    public void MakeHost(MemberViewModel m)
    {
        if (_s == null || !IsHost || m.IsMe) return;
        if (!Dialogs.Confirm(L.T("Passare l'host?"), L.F("{0} diventerà l'host della stanza e deciderà i permessi. Tu resti nella stanza.", m.Name), L.T("Passa l'host"))) return;
        _s.Promote(m.Id);
    }

    // ------------------------------------------------------------------ queue

    public List<RoomItemViewModel> Queue { get; private set; } = new();
    public bool HasQueue => Queue.Count > 0;
    public string QueueCountText => Queue.Count == 0 ? "" : L.Count(Queue.Count, "1 brano", "{0} brani");

    private RoomItemViewModel? _currentItem;
    public RoomItemViewModel? CurrentItem { get => _currentItem; private set { if (Set(ref _currentItem, value)) OnChanged(nameof(HasCurrent)); } }
    public bool HasCurrent => CurrentItem != null;

    public IReadOnlyList<RoomTrack> RoomQueue => _s?.Queue ?? (IReadOnlyList<RoomTrack>)Array.Empty<RoomTrack>();

    private RoomItemViewModel ItemVm(RoomTrack t, int number, bool current)
    {
        if (!_items.TryGetValue(t.Id, out var vm)) _items[t.Id] = vm = new RoomItemViewModel(t, this);
        vm.Update(t, number, current);
        return vm;
    }

    private void OnQueue()
    {
        var s = _s;
        if (s == null) return;
        var keep = new[] { s.Current }.Concat(s.Queue).OfType<RoomTrack>().Select(t => t.Id).ToHashSet();
        foreach (var id in _items.Keys.Where(k => !keep.Contains(k)).ToList()) _items.Remove(id);
        foreach (var id in _ephemeral.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _ephemeral.Remove(id);
            _main.ForgetVm("lt-" + id);
            try { File.Delete(AppPaths.TrackCover("lt-" + id)); } catch { } LyricsStore.Delete("lt-" + id);
        }
        CurrentItem = s.Current != null ? ItemVm(s.Current, 0, true) : null;
        Queue = s.Queue.Select((t, i) => ItemVm(t, i + 1, false)).ToList();
        OnChanged(nameof(Queue), nameof(HasQueue), nameof(QueueCountText));
        foreach (var m in Members) if (_s?.Members.FirstOrDefault(x => x.Id == m.Id) is { } mm) m.Update(mm);
        UpdateNowPlaying();
        _main.Player.OnRoomQueue();
        Follow();
    }

    private void OnMyFiles()
    {
        foreach (var vm in _items.Values) vm.Refresh();
        UpdateNowPlaying();
        Follow();
    }

    public FileStatus? MyStatus(string itemId) => _fetcher?.StatusOf(itemId);
    public bool WaitingHost(string itemId) => _fetcher?.WaitingHost(itemId) == true;

    // A song that will come from the host (P2P): how far the host is with it.
    public string HostWaitText(string itemId)
    {
        var st = _s?.StatusOf(_s.HostId, itemId);
        return st?.State is FileState.Downloading or FileState.Transfer
            ? L.F("Arriverà dall'host: lo sta preparando · {0:0}%", st.Pct)
            : L.T("Arriverà dall'host appena l'ha preparato");
    }
    public bool InLibrary(string itemId) => _fetcher?.LibraryTrack(itemId) is { } t && _main.Library.Get(t.Id) != null;

    // The song as the rest of the app sees it: the library one if you have it, otherwise a stand-in with the room's data.
    public TrackViewModel TrackFor(RoomTrack item)
    {
        if (_fetcher?.LibraryTrack(item.Id) is { } lt && _main.Library.Get(lt.Id) != null) return _main.Vm(lt);
        if (!_ephemeral.TryGetValue(item.Id, out var t))
        {
            t = new Track
            {
                Id = "lt-" + item.Id, Title = item.Title, Artist = item.Artist, Album = item.Album, Duration = item.Duration, SourceUrl = item.SourceUrl,
                Site = item.Site, Keys = item.Keys.ToList(), ArtUrl = item.ArtUrl, Wave = item.Wave, Loudness = item.Loudness, Peak = item.Peak, Bpm = item.Bpm,
                HasCover = File.Exists(AppPaths.TrackCover("lt-" + item.Id)),
            };
            _ephemeral[item.Id] = t;
        }
        if (t.Duration <= 0 && item.Duration > 0) t.Duration = item.Duration;
        t.Path = _fetcher?.PathFor(item.Id) ?? "";
        return _main.Vm(t);
    }

    // A room song that isn't in the library (a stand-in made above).
    public RoomTrack? RoomTrackOf(TrackViewModel vm)
    {
        if (_s == null) return null;
        if (vm.Id.StartsWith("lt-") && _s.Find(vm.Id[3..]) is { } t) return t;
        return null;
    }

    public bool IsStandIn(TrackViewModel vm) => vm.Id.StartsWith("lt-") && _main.Library.Get(vm.Id) == null;

    private void OnCover(string id, byte[] bytes)
    {
        var path = AppPaths.TrackCover("lt-" + id);
        try { File.WriteAllBytes(path, bytes); }
        catch { return; }
        if (_ephemeral.TryGetValue(id, out var t))
        {
            t.HasCover = true;
            t.CoverVersion++;
            _main.Vm(t).Refresh();
        }
    }

    // ------------------------------------------------------------------ adding songs

    public void Add(IReadOnlyList<TrackViewModel> tracks, bool next = false)
    {
        if (_s == null || tracks.Count == 0) return;
        if (!CanAdd)
        {
            Deny(Denied(Perm.Add));
            return;
        }
        if (next && !CanRemove) next = false;
        _ = AddAsync(tracks.ToList(), next);
    }

    // A whole list (play buttons of playlists and pages): asks first when it's a lot.
    public void AddAll(IReadOnlyList<TrackViewModel> tracks, string listName, bool shuffle)
    {
        if (_s == null || tracks.Count == 0) return;
        if (!CanAdd)
        {
            Deny(Denied(Perm.Add));
            return;
        }
        var list = tracks.ToList();
        if (shuffle) list = list.OrderBy(_ => Random.Shared.Next()).ToList();
        int room = TogetherSession.MaxQueue - _s.Queue.Count;
        if (list.Count > room) list = list.Take(Math.Max(0, room)).ToList();
        if (list.Count == 0)
        {
            _main.Toast(L.T("La coda della stanza è piena."));
            return;
        }
        if (list.Count > 12 && !Dialogs.Confirm(L.T("Aggiungere alla stanza?"),
                L.F("{0} brani di «{1}» andranno nella coda della stanza{2}.", list.Count, listName, shuffle ? " " + L.T("in ordine casuale") : ""),
                L.T("Aggiungi"))) return;
        _ = AddAsync(list, false);
    }

    private async Task AddAsync(List<TrackViewModel> tracks, bool next)
    {
        var items = tracks.Select(RoomTrackFor).ToList();
        var covers = await Task.Run(() => tracks.Select(t => t.T.HasCover ? JpegOf(AppPaths.TrackCover(t.Id), 300) : null).ToList());
        if (_s == null) return;
        var order = Enumerable.Range(0, items.Count).ToList();
        if (next) order.Reverse();
        foreach (var i in order) _s.Add(items[i], covers[i], next);
        _main.Toast(items.Count == 1
            ? L.F(next ? "«{0}» sarà il prossimo nella stanza" : "«{0}» aggiunto alla coda della stanza", items[0].Title)
            : L.F("{0} brani aggiunti alla coda della stanza", items.Count));
    }

    // ------------------------------------------------------------------ random songs

    // A few songs picked at random from all your songs, a playlist or a tag, or suggested online like the room's song:
    // a queue for the room in two clicks.
    private bool _showRandom;
    public bool ShowRandom
    {
        get => _showRandom;
        set
        {
            if (value) RefreshRandomSources();
            Set(ref _showRandom, value);
        }
    }

    public List<Choice> RandomSources { get; private set; } = new();

    private Choice? _randomSource;
    public Choice? RandomSource
    {
        get => _randomSource;
        set
        {
            if (value == null || !Set(ref _randomSource, value)) return;
            _lastRandomSource = value.Value as string;
            OnRandomChanged();
        }
    }
    private string? _lastRandomSource;

    private const string Suggested = "radio";
    private bool SuggestedSource => RandomSource?.Value as string == Suggested;

    private bool _randomBusy;
    // Looking for suggested songs online.
    public bool RandomBusy
    {
        get => _randomBusy;
        private set
        {
            if (!Set(ref _randomBusy, value)) return;
            OnRandomChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string RandomTitle => SuggestedSource ? L.T("Aggiungi brani consigliati") : L.T("Aggiungi brani a caso");
    public string RandomHint => SuggestedSource
        ? L.T("Simili a quello in riproduzione nella stanza, trovati online come in «Riproduci brani simili»: premi ancora per averne altri.")
        : L.T("Scelti a caso, senza quelli già nella stanza: premi ancora per aggiungerne altri.");
    public string RandomGlyph => SuggestedSource ? "" : "";
    public string RandomAddText => RandomBusy ? L.T("Cerco brani simili…") : L.T("Aggiungi alla coda");

    private void OnRandomChanged() => OnChanged(nameof(RandomTitle), nameof(RandomHint), nameof(RandomGlyph), nameof(RandomAddText));

    public List<Choice> RandomCounts { get; } = new[] { 1, 5, 10, 25, 50 }.Select(n => new Choice(n.ToString(), n)).ToList();

    private Choice? _randomCount;
    public Choice RandomCount { get => _randomCount ??= RandomCounts[1]; set { if (value != null) Set(ref _randomCount, value); } }

    // Songs the button already put in the room: not again until the list has been used up.
    private readonly HashSet<string> _randomUsed = new();

    private void RefreshRandomSources()
    {
        var list = new List<Choice> { new(L.T("Tutti i brani"), "library", L.Count(_main.Library.Count, "1 brano", "{0} brani")) };
        var seed = SuggestedSeed();
        list.Add(new Choice("✦ " + L.T("Consigliati online"), Suggested,
            seed != null ? L.F("simili a «{0}»", seed.Title.Length > 30 ? seed.Title[..30] + "…" : seed.Title) : L.T("simili al brano della stanza")));
        foreach (var p in _main.Playlists.Where(p => p.P.Tracks.Count > 0))
            list.Add(new Choice(p.IsFavorites ? "♥ " + p.Name : p.Name, "playlist:" + p.Id, L.Count(p.P.Tracks.Count, "1 brano", "{0} brani")));
        foreach (var t in _main.Tags.Where(t => t.Count > 0))
            list.Add(new Choice("# " + t.Name, "tag:" + t.Id, L.Count(t.Count, "1 brano", "{0} brani")));
        RandomSources = list;
        _randomSource = list.FirstOrDefault(c => (string)c.Value! == _lastRandomSource) ?? list[0];
        OnChanged(nameof(RandomSources), nameof(RandomSource));
        OnRandomChanged();
    }

    private IEnumerable<Track> RandomPool(string source)
    {
        var lib = _main.Library;
        if (source.StartsWith("playlist:"))
            return _main.Profile.GetPlaylist(source[9..]) is { } p ? p.Tracks.ToList().Select(lib.Get).OfType<Track>() : Enumerable.Empty<Track>();
        if (source.StartsWith("tag:"))
        {
            var tag = source[4..];
            return lib.Snapshot().Where(t => _main.Profile.TagsOf(t.Id).Contains(tag));
        }
        return lib.Snapshot();
    }

    private void AddRandom()
    {
        if (_s == null) return;
        if (!CanAdd)
        {
            Deny(Denied(Perm.Add));
            return;
        }
        int want = Math.Min((int)RandomCount.Value!, TogetherSession.MaxQueue - _s.Queue.Count);
        if (want <= 0)
        {
            _main.Toast(L.T("La coda della stanza è piena."));
            return;
        }
        if (SuggestedSource)
        {
            _ = AddSuggested(want);
            return;
        }
        var pool = RandomPool(RandomSource?.Value as string ?? "library").DistinctBy(t => t.Id).ToList();
        // Not what the room already has (playing or waiting).
        var inRoom = new[] { _s.Current }.Concat(_s.Queue).OfType<RoomTrack>()
            .Select(t => _fetcher?.LibraryTrack(t.Id)?.Id).OfType<string>().ToHashSet();
        var fresh = pool.Where(t => !inRoom.Contains(t.Id) && !_randomUsed.Contains(t.Id)).ToList();
        if (fresh.Count < want)
        {
            foreach (var t in pool) _randomUsed.Remove(t.Id);
            fresh = pool.Where(t => !inRoom.Contains(t.Id)).ToList();
        }
        var pick = fresh.OrderBy(_ => Random.Shared.Next()).Where(t => File.Exists(t.Path)).Take(want).Select(_main.Vm).ToList();
        if (pick.Count == 0)
        {
            _main.Toast(L.T("In questa lista non ci sono altri brani da aggiungere."));
            return;
        }
        foreach (var t in pick) _randomUsed.Add(t.Id);
        _ = AddAsync(pick, false);
    }

    // Songs the button already suggested: not again (the next ones start from the newest).
    private readonly HashSet<string> _suggestedUsed = new();

    // What the suggestions are like: the song playing, or the last one waiting.
    private RoomTrack? SuggestedSeed() => _s?.Current ?? _s?.Queue.LastOrDefault();

    // Songs like the room's found online (the sites of Settings → Suggested songs), without the ones it already has.
    // A song you have plays from your library for you, the others are downloaded by everyone from their link.
    private async Task AddSuggested(int want)
    {
        if (_s == null || RandomBusy) return;
        if (SuggestedSeed() is not { } from)
        {
            _main.Toast(L.T("Nella stanza non c'è ancora un brano: i consigliati sono simili a quello in riproduzione."));
            return;
        }
        RandomBusy = true;
        try
        {
            var inRoom = new[] { _s.Current }.Concat(_s.Queue).OfType<RoomTrack>().Select(t => SongMeta.Key(t.Title, t.Artist)).ToHashSet();
            var seed = new RadioSeed(from.Title, from.Artist, from.Keys.ToList());
            var source = _main.Host.Settings.RadioSource;
            var lib = _main.Library;
            var picks = await Task.Run(async () =>
            {
                var list = new List<(SearchHit Hit, Track? Have)>();
                for (int round = 0; round < 3 && list.Count < want; round++)
                {
                    var hits = await OnlineSearchServices.SimilarAsync(seed, source, 50, CancellationToken.None);
                    foreach (var h in hits)
                    {
                        var key = SongMeta.Key(h.Title, h.Artist);
                        if (list.Count >= want || inRoom.Contains(key) || _suggestedUsed.Contains(key)) continue;
                        var keys = SourceKeys.ForItem(new MediaItem { Url = h.Url, PageUrl = h.Url });
                        var have = lib.FindByKeys(keys) ?? lib.FindSimilar(h.Title, h.Artist, h.Duration);
                        list.Add((h, have != null && File.Exists(have.Path) ? have : null));
                        inRoom.Add(key);
                    }
                    if (hits.Count == 0) break;
                    seed = new RadioSeed(hits[^1].Title, hits[^1].Artist, SourceKeys.ForItem(new MediaItem { Url = hits[^1].Url, PageUrl = hits[^1].Url }));
                }
                return list;
            });
            if (_s == null) return;
            if (picks.Count == 0)
            {
                _main.Toast(L.T("Nessun brano simile nuovo trovato online."));
                return;
            }
            foreach (var p in picks) _suggestedUsed.Add(SongMeta.Key(p.Hit.Title, p.Hit.Artist));
            var items = picks.Select(p => p.Have != null ? RoomTrackFor(_main.Vm(p.Have)) : SuggestedTrack(p.Hit)).ToList();
            var covers = await Task.Run(async () =>
            {
                var list = new List<byte[]?>();
                foreach (var p in picks)
                {
                    if (p.Have != null) list.Add(p.Have.HasCover ? JpegOf(AppPaths.TrackCover(p.Have.Id), 300) : null);
                    else
                    {
                        try { list.Add(p.Hit.Thumb != null ? SquareJpeg(await Http.GetBytesAsync(p.Hit.Thumb), 300) : null); }
                        catch { list.Add(null); }
                    }
                }
                return list;
            });
            if (_s == null) return;
            for (int i = 0; i < items.Count; i++) _s.Add(items[i], covers[i], false);
            _main.Toast(L.Count(items.Count, "1 brano consigliato aggiunto alla coda della stanza", "{0} brani consigliati aggiunti alla coda della stanza"));
        }
        catch
        {
            _main.Toast(L.T("I siti non hanno risposto: controlla la connessione e riprova."));
        }
        finally { RandomBusy = false; }
    }

    private static RoomTrack SuggestedTrack(SearchHit h) => new()
    {
        Title = h.Title, Artist = h.Artist, Album = h.Album, Duration = h.Duration ?? 0, SourceUrl = h.Url, Site = h.Service,
        Keys = SourceKeys.ForItem(new MediaItem { Url = h.Url, PageUrl = h.Url }),
        ArtUrl = h.Thumb is { Length: <= 250 } th && th.StartsWith("https://") && !th.Contains("webp") ? th : null,
    };

    private RoomTrack RoomTrackFor(TrackViewModel vm)
    {
        if (RoomTrackOf(vm) is { } known)
            return new RoomTrack
            {
                Title = known.Title, Artist = known.Artist, Album = known.Album, Duration = known.Duration, SourceUrl = known.SourceUrl, Site = known.Site,
                Keys = known.Keys.ToList(), ArtUrl = known.ArtUrl, Wave = known.Wave, Loudness = known.Loudness, Peak = known.Peak, Bpm = known.Bpm, Ext = known.Ext,
            };
        var t = vm.T;
        bool web = t.SourceUrl is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !t.IsLocal && t.Site != "DJ";
        return new RoomTrack
        {
            Title = t.Title, Artist = t.Artist, Album = t.Album, Duration = t.Duration, SourceUrl = web ? t.SourceUrl : null, Site = t.Site,
            Keys = t.Keys.ToList(), ArtUrl = t.ArtUrl, Wave = t.Wave, Loudness = t.Loudness, Peak = t.Peak, Bpm = t.Bpm, Ext = Path.GetExtension(t.Path),
        };
    }

    // ------------------------------------------------------------------ queue actions

    public void Remove(RoomItemViewModel item)
    {
        if (_s == null) return;
        if (item.IsCurrent)
        {
            Skip();
            return;
        }
        if (!CanRemoveItem(item.Item))
        {
            Deny(Denied(Perm.Remove));
            return;
        }
        _s.Remove(item.Id);
    }

    public void RemoveAt(int index)
    {
        if (_s != null && index >= 0 && index < _s.Queue.Count && _items.TryGetValue(_s.Queue[index].Id, out var vm)) Remove(vm);
    }

    public void MoveAt(int from, int to)
    {
        if (_s == null || from < 0 || from >= _s.Queue.Count) return;
        if (!CanRemove)
        {
            Deny(Denied(Perm.Remove));
            return;
        }
        _s.Move(_s.Queue[from].Id, to);
    }

    public void MoveTo(RoomItemViewModel item, int index)
    {
        if (_s == null) return;
        if (!CanRemove)
        {
            Deny(Denied(Perm.Remove));
            return;
        }
        _s.Move(item.Id, index);
    }

    public void PlayNowAt(int index)
    {
        if (_s == null || index < 0 || index >= _s.Queue.Count) return;
        PlayNow(_s.Queue[index].Id);
    }

    public void PlayNow(string itemId)
    {
        if (_s == null) return;
        if (!CanRemove || !CanSkip)
        {
            Deny(!CanSkip ? Denied(Perm.Skip) : Denied(Perm.Remove));
            return;
        }
        _s.PlayNow(itemId);
    }

    // ------------------------------------------------------------------ transport (from the player bar, keys, tray)

    public void TogglePause()
    {
        if (_s == null) return;
        if (!CanPause)
        {
            Deny(Denied(Perm.Pause));
            return;
        }
        if (_s.Current == null)
        {
            _main.Toast(L.T("La coda della stanza è vuota."));
            return;
        }
        _s.SetPaused(_s.Play.State is PlayState.Playing or PlayState.Waiting);
    }

    public void SetPaused(bool paused)
    {
        if (_s?.Current == null) return;
        bool isPaused = _s.Play.State == PlayState.Paused;
        if (isPaused == paused) return;
        TogglePause();
    }

    public void Skip()
    {
        if (_s == null) return;
        if (!CanSkip)
        {
            Deny(Denied(Perm.Skip));
            return;
        }
        if (_s.Current != null) _s.Skip();
    }

    public void Restart() => Seek(0);

    public void Seek(double seconds)
    {
        if (_s?.Current == null) return;
        if (!CanPause)
        {
            Deny(Denied(Perm.Pause));
            return;
        }
        _s.Seek(Math.Max(0, seconds));
    }

    // Loop of the song playing (the room has no "repeat the queue"): same permission as pause.
    public bool RoomLoop => _s?.Play.Loop == true;

    public void ToggleLoop()
    {
        if (_s == null) return;
        if (!CanPause)
        {
            Deny(Denied(Perm.Pause));
            return;
        }
        _s.SetLoop(!RoomLoop);
    }

    // Speed for everyone in the room.
    public double RoomSpeed => _s?.Play.Speed is > 0 and var v ? v : 1;
    public bool RoomPitch => _s?.Play.Pitch == true;

    public void SetSpeed(double speed, bool pitch)
    {
        if (_s == null) return;
        if (!CanSpeed)
        {
            Deny(Denied(Perm.Speed));
            return;
        }
        _s.SetSpeed(speed, pitch);
    }

    // Small signs on the song card: loop on, not at the normal speed.
    public string SpeedBadge => Math.Abs(RoomSpeed - 1) < 0.001 ? "" : PlayerViewModel.SpeedLabel(RoomSpeed);
    public bool HasSpeedBadge => SpeedBadge.Length > 0;

    public bool IsPlaying => _s?.Play.State == PlayState.Playing;
    public double TargetPosition => _s?.Position ?? 0;

    // ------------------------------------------------------------------ now playing on this computer

    private string _nowStatus = "";
    // Under the song: waiting for the others, downloading it here, paused by the host...
    public string NowStatus { get => _nowStatus; private set => Set(ref _nowStatus, value); }
    private bool _nowBusy;
    public bool NowBusy { get => _nowBusy; private set => Set(ref _nowBusy, value); }
    public string NowKicker => _s?.Play.State switch
    {
        PlayState.Playing => L.T("IN RIPRODUZIONE"),
        PlayState.Paused => L.T("IN PAUSA"),
        PlayState.Waiting => L.T("STA PER INIZIARE"),
        _ => L.T("NIENTE IN RIPRODUZIONE"),
    };

    private void OnPlayback()
    {
        UpdateNowPlaying();
        OnChanged(nameof(RoomLoop), nameof(RoomSpeed), nameof(RoomPitch), nameof(SpeedBadge), nameof(HasSpeedBadge));
        _main.Player.OnRoomPlayback();
        Follow();
        _main.Host.OnSeek();
    }

    private void UpdateNowPlaying()
    {
        var s = _s;
        if (s == null) return;
        OnChanged(nameof(NowKicker), nameof(IsPlaying));
        if (s.Current is not { } cur)
        {
            NowStatus = CanAdd ? L.T("La coda è vuota: aggiungi un brano con il tasto + dalle tue playlist.") : L.T("La coda è vuota: aspetta che qualcuno aggiunga un brano.");
            NowBusy = false;
            return;
        }
        var mine = _fetcher?.StatusOf(cur.Id);
        int ready = s.Members.Count(m => !m.Away && s.StatusOf(m.Id, cur.Id)?.State == FileState.Ready);
        int total = s.Members.Count(m => !m.Away);
        NowBusy = mine?.State is FileState.Downloading or FileState.Transfer || s.Play.State == PlayState.Waiting;
        NowStatus = mine?.State switch
        {
            FileState.Downloading => L.F("Lo stai scaricando · {0:0}%: appena finisce entri al punto giusto", mine.Pct),
            FileState.Transfer when WaitingHost(cur.Id) => HostWaitText(cur.Id),
            FileState.Transfer => L.F("Lo stai ricevendo da qualcuno nella stanza · {0:0}%", mine.Pct),
            FileState.Failed when !P2P => L.T("Non si riesce a scaricare il brano dal suo link, e senza P2P nessuno può mandartelo"),
            FileState.Failed => L.T("Non si riesce ad avere il brano: si riprova tra poco"),
            _ when s.Play.State == PlayState.Waiting => L.F("Aspettiamo che tutti abbiano il brano · {0} di {1} pronti", ready, total),
            _ when s.Play.State == PlayState.Paused => L.T("In pausa per tutti"),
            _ => ready < total ? L.F("{0} di {1} lo stanno già ascoltando", ready, total) : L.T("Tutti lo stanno ascoltando"),
        };
    }

    // Every 200 ms: the player plays the room's song at the room's position (a new song, a seek, a drift).
    private void Follow()
    {
        var s = _s;
        var p = _main.Player;
        if (s == null || !p.InRoom) return;
        var item = s.Current;
        if (item == null || s.Play.State == PlayState.Idle)
        {
            p.RoomShow(null, 0);
            p.RoomState(false, 0);
            if (p.RoomLoadedId != null) p.RoomStop();
            return;
        }
        var vm = TrackFor(item);
        p.RoomShow(vm, item.Duration);
        // "queue" (the new song) and "play" (its state) come one after the other: until the second one arrives
        // the state still belongs to the song before, so the new one waits at the start.
        bool mine = s.Play.ItemId == item.Id;
        double target = mine ? s.Position : 0;
        bool playing = mine && s.Play.State == PlayState.Playing;
        double speed = RoomSpeed;
        p.RoomState(playing, target);
        p.RoomSpeed(speed, RoomPitch);
        var path = _fetcher?.PathFor(item.Id);
        if (path == null)
        {
            // Not here yet: silence (the song you were hearing doesn't go on).
            if (p.RoomLoadedId != null) p.RoomStop();
            return;
        }
        // While the file opens the engine still holds the song before: nothing is played, paused or moved until then.
        if (_loading) return;
        long now = Environment.TickCount64;
        if (p.RoomLoadedId != item.Id)
        {
            // A file that didn't open is tried again after a while, not every 200 ms.
            if (_failedItem == item.Id && now < _failedUntil) return;
            _loading = true;
            // The song before stops here (it doesn't go on under the new one); the new one starts at the right point once open.
            p.RoomPlay(false);
            _ = Load(item.Id, vm, path);
            return;
        }
        var audio = p.Audio;
        double lead = Lead * speed;
        if (playing)
        {
            if (!audio.IsPlaying)
            {
                audio.Seek(TimeSpan.FromSeconds(target + lead + _lag));
                p.RoomPlay(true);
                _lastSeek = now;
                _justSeeked = true;
            }
            else if (now - _lastSeek > 1500)
            {
                double behind = target - audio.Position.TotalSeconds;
                if (Math.Abs(behind) > 0.2 * Math.Max(1, speed))
                {
                    // Drifted (a hiccup, a seek by someone): back on the room's time. If the last jump itself landed
                    // late (files decoded through ffmpeg take a moment to restart), the next ones aim that much further.
                    if (_justSeeked) _lag = Math.Clamp(_lag + behind, 0, 1.5);
                    audio.Seek(TimeSpan.FromSeconds(target + lead + _lag));
                    _lastSeek = now;
                    _justSeeked = true;
                }
                else _justSeeked = false;
            }
        }
        else
        {
            if (audio.IsPlaying) p.RoomPlay(false);
            if (now - _lastSeek > 400 && Math.Abs(audio.Position.TotalSeconds - target) > 0.25)
            {
                audio.Seek(TimeSpan.FromSeconds(target));
                _lastSeek = now;
            }
        }
    }

    private async Task Load(string itemId, TrackViewModel vm, string path)
    {
        bool ok = false;
        try { ok = await _main.Player.RoomLoad(itemId, vm, path); }
        finally { _loading = false; }
        if (!ok)
        {
            _failedItem = itemId;
            _failedUntil = Environment.TickCount64 + 5000;
        }
        _lastSeek = 0;
        _lag = 0;
        _justSeeked = false;
        Follow();
    }

    // ------------------------------------------------------------------ saving a room song

    public void SaveMenu(RoomItemViewModel item, UIElement? anchor)
    {
        if (anchor != null) Views.Menus.Open(Views.Menus.ForRoomItem(item, this), anchor, true);
    }

    public RoomItemViewModel? ItemFor(TrackViewModel vm)
    {
        if (_s == null) return null;
        foreach (var t in new[] { _s.Current }.Concat(_s.Queue).OfType<RoomTrack>())
            if (_items.TryGetValue(t.Id, out var item) && item.Track == vm) return item;
        return null;
    }

    // Into the library (and a playlist): the file leaves the room's cache.
    public async Task<Track?> Save(RoomItemViewModel item, Playlist? playlist)
    {
        if (_s == null) return null;
        if (_fetcher?.LibraryTrack(item.Id) is { } have && _main.Library.Get(have.Id) != null)
        {
            if (playlist != null) _main.AddToPlaylist(_main.Vm(have), playlist);
            else _main.Toast(L.T("È già nella tua libreria"));
            return have;
        }
        var from = _fetcher?.PathFor(item.Id);
        if (from == null)
        {
            _main.Toast(L.T("Il brano non è ancora arrivato su questo computer: aspetta che finisca di scaricarsi."));
            return null;
        }
        var r = item.Item;
        try
        {
            var dir = _main.Host.Settings.MusicDir;
            Directory.CreateDirectory(dir);
            var name = Text.SafeFileName(string.IsNullOrWhiteSpace(r.Artist) ? r.Title : $"{r.Artist.Split(',')[0].Trim()} - {r.Title}");
            var target = Text.UniquePath(dir, name, Path.GetExtension(from));
            await Task.Run(() => File.Copy(from, target));
            var t = new Track
            {
                Title = r.Title, Artist = r.Artist, Album = r.Album, Duration = r.Duration, Path = target, SourceUrl = r.SourceUrl,
                Site = r.Site ?? (r.SourceUrl != null ? Sites.NameFor(r.SourceUrl) : "Ascolta insieme"), Keys = r.Keys.ToList(), ArtUrl = r.ArtUrl,
                Wave = r.Wave, Loudness = r.Loudness, Peak = r.Peak, Bpm = r.Bpm,
            };
            if (t.Site == "File locale") t.Site = "Ascolta insieme";
            var standInCover = AppPaths.TrackCover("lt-" + r.Id);
            if (File.Exists(standInCover))
            {
                File.Copy(standInCover, AppPaths.TrackCover(t.Id), true);
                t.HasCover = true;
            }
            else t.HasCover = await AudioAnalysis.ExtractCoverAsync(target, AppPaths.TrackCover(t.Id), false, CancellationToken.None);
            // The lyrics found while it played in the room come along.
            if (_ephemeral.TryGetValue(r.Id, out var standIn)) LyricsStore.Copy(standIn.Id, t, standIn.Lyrics);
            if (t.Wave == null || t.Loudness == null || t.Duration <= 0)
            {
                try
                {
                    var a = await AudioAnalysis.AnalyzeAsync(target, CancellationToken.None);
                    t.Wave ??= a.Wave;
                    t.Loudness ??= a.Loudness;
                    t.Peak ??= a.Peak;
                    if (a.Duration > 0) t.Duration = a.Duration;
                }
                catch { }
            }
            _main.Library.Add(t);
            if (playlist != null) _main.Profile.AddTrack(playlist, t.Id);
            // It plays on from where it is; the cache copy goes when it's no longer open.
            _fetcher?.UseLibrary(r.Id, t);
            if (Cache.Contains(from)) Cache.Forget(from);
            item.Refresh();
            _main.Toast(playlist != null ? L.F("«{0}» salvato in «{1}»", t.Title, PlaylistViewModel.DisplayName(playlist)) : L.F("«{0}» salvato nella libreria", t.Title));
            return t;
        }
        catch (Exception ex)
        {
            _main.Toast(L.T("Salvataggio non riuscito:") + " " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ chat

    public ObservableCollection<ChatLineViewModel> Chat { get; } = new();

    private string _chatInput = "";
    public string ChatInput { get => _chatInput; set { if (Set(ref _chatInput, value)) CommandManager.InvalidateRequerySuggested(); } }

    private void Send()
    {
        var text = ChatInput.Trim();
        if (text.Length == 0 || _s == null) return;
        _s.Say(text);
        ChatInput = "";
    }

    private void OnChat(ChatLine line)
    {
        var last = Chat.LastOrDefault();
        bool showName = last == null || last.IsSystem || last.Line.From != line.From || line.Time - last.Line.Time > 5 * 60_000;
        Chat.Add(new ChatLineViewModel(line, line.From == _s?.Me.Id, showName));
        while (Chat.Count > 200) Chat.RemoveAt(0);
    }

    // ------------------------------------------------------------------ misc

    private void CopyAddress()
    {
        var text = IsHost ? string.Join(", ", Discovery.LocalAddresses().Take(3).Select(ip => _s!.ListenPort == TogetherSession.FirstPort ? ip : $"{ip}:{_s.ListenPort}")) : _joinedAddress;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            _main.Toast(L.T("Indirizzo copiato"));
        }
        catch { }
    }

    private async Task AllowFirewall()
    {
        bool ok = await Task.Run(Firewall.Allow);
        _main.Toast(ok ? L.T("Fatto: Windows ora lascia entrare le connessioni degli altri su ogni rete.") : L.T("Il firewall non è stato cambiato."));
    }

    // Cache in Settings (the files in use, by a room or by the suggested songs, stay).
    public void ClearCache()
    {
        var keep = _fetcher?.InUse ?? new HashSet<string>();
        Cache.Clear(keep);
    }

    public static int AheadSetting(int n) => Math.Clamp(n, 1, TogetherSession.MaxAhead);

    // Settings changed while in a room (songs to get ready, P2P): the others are told, the fetcher follows.
    public void OnPrefsChanged()
    {
        if (_s == null) return;
        var settings = _main.Host.Settings;
        _s.SetPrefs(AheadSetting(settings.TogetherAhead), AheadSetting(settings.TogetherHostAhead), settings.TogetherSendAsHost && settings.TogetherP2P,
            !settings.TogetherP2P);
        _fetcher?.Refresh();
        OnFiles();
        foreach (var item in _items.Values) item.Refresh();
    }

    // Songs can go from one computer to another in this room (P2P on here, and on the host).
    public bool P2P => _s?.P2PAllowed ?? _main.Host.Settings.TogetherP2P;

    // App closing or profile going away: out of the room, no questions.
    public void Shutdown()
    {
        _scanTimer.Stop();
        LeaveForExit();
    }

    public static byte[]? JpegOf(string path, int size)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.UriSource = new Uri(path);
            bi.DecodePixelWidth = size;
            bi.EndInit();
            bi.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bi));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    // A site's picture as a square cover (the middle of a 16:9 video thumbnail).
    public static byte[]? SquareJpeg(byte[] data, int size)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = new MemoryStream(data);
            bi.EndInit();
            int side = Math.Min(bi.PixelWidth, bi.PixelHeight);
            BitmapSource square = new CroppedBitmap(bi, new Int32Rect((bi.PixelWidth - side) / 2, (bi.PixelHeight - side) / 2, side, side));
            if (side > size) square = new TransformedBitmap(square, new ScaleTransform(size / (double)side, size / (double)side));
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(square));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public static ImageSource? ImageFrom(byte[]? bytes, int size)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = new MemoryStream(bytes);
            bi.DecodePixelWidth = size;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }
}
