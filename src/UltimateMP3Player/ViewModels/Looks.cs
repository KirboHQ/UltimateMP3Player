using System.Windows.Input;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Settings › Appearance › Looks: the profile's looks with a name (Core LookPreset). The one chosen is the look as it is now, so the
// settings change it freely; it's taken back into its preset only when another one is chosen, copied or exported. The first time,
// the look in use becomes "My look".
public sealed class LooksViewModel : Observable
{
    private readonly MainViewModel _main;

    public LooksViewModel(MainViewModel main)
    {
        _main = main;
        CopyCommand = new RelayCommand(() => _ = Copy());
        RenameCommand = new RelayCommand(() => _ = Rename());
        DeleteCommand = new RelayCommand(() => _ = Delete(), () => Data.Looks.Count > 1);
        ExportCommand = new RelayCommand(() => _ = Export());
        ImportCommand = new RelayCommand(() => _ = Import());
    }

    public ICommand CopyCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }

    private ProfileData Data => _main.Profile.Data;
    private AppSettings S => _main.Host.Settings;
    private string ProfileId => _main.Profile.Info.Id;
    private string Backdrop => _main.Profile.BackdropPicture;

    // (the same list until it changes: the drop-down finds the one chosen among its own items)
    public List<Choice> Items
    {
        get
        {
            if (_items != null) return _items;
            Ensure();
            return _items = Data.Looks.Select(l => new Choice(l.Name, l.Id)).ToList();
        }
    }
    private List<Choice>? _items;

    public Choice? Current
    {
        get => _swapping ? null : Items.FirstOrDefault(c => (string?)c.Value == Data.LookId);
        set
        {
            if (value?.Value is string id && id != Data.LookId) Choose(id);
        }
    }

    public string CurrentName
    {
        get
        {
            Ensure();
            return Chosen?.Name ?? "";
        }
    }
    private LookPreset? Chosen => Data.Looks.FirstOrDefault(l => l.Id == Data.LookId);

    // (the look in use, a preset of its own the first time)
    private void Ensure()
    {
        if (Data.Looks.Count > 0 && Chosen != null) return;
        if (Data.Looks.Count == 0)
        {
            var mine = new LookPreset { Name = L.T("My look") };
            mine.Take(Data, S, ProfileId, Backdrop);
            Data.Looks.Add(mine);
        }
        Data.LookId ??= Data.Looks[0].Id;
        if (Chosen == null) Data.LookId = Data.Looks[0].Id;
        _main.Profile.Save();
    }

    // The look in use, into its preset (before another one takes its place, a copy, an export).
    public void Keep() => Chosen?.Take(Data, S, ProfileId, Backdrop);

    private void Choose(string id)
    {
        if (Data.Looks.FirstOrDefault(l => l.Id == id) is not { } next) return;
        Keep();
        next.Give(Data, S, ProfileId, Backdrop);
        Data.LookId = id;
        Applied();
    }

    // A whole look in place: saved, painted, every choice of Appearance shown again.
    private void Applied()
    {
        _main.Profile.Save();
        S.Save();
        _main.Host.ApplyTheme();
        _main.Backdrop.Update();
        _main.Settings.LookChanged();
        Changed();
    }

    // (like the equalizer's presets: none chosen while the list changes, or WPF's drop-down keeps the new choice but shows it blank)
    private bool _swapping;

    private void Changed()
    {
        _swapping = true;
        OnChanged(nameof(Current));
        _items = null;
        OnChanged(nameof(Items));
        _swapping = false;
        OnChanged(nameof(Current), nameof(CurrentName));
        CommandManager.InvalidateRequerySuggested();
    }

    private string Free(string name)
    {
        var free = name;
        for (int n = 2; Data.Looks.Any(l => l.Name.Equals(free, StringComparison.CurrentCultureIgnoreCase)); n++) free = $"{name} {n}";
        return free;
    }

    private async Task Copy()
    {
        Ensure();
        Keep();
        // (a copy of a copy: "X (copy) 2", not "X (copy) (copy)")
        var suffix = L.F("{0} (copy)", "");
        var stem = CurrentName.EndsWith(suffix) ? CurrentName[..^suffix.Length] : CurrentName;
        var name = await Dialogs.PromptAsync(L.T("Copy of the look"), L.T("Name"), Free(L.F("{0} (copy)", stem)));
        if (string.IsNullOrWhiteSpace(name) || Chosen is not { } from) return;
        var copy = from.Clone(ProfileId, Free(name.Trim()));
        Data.Looks.Add(copy);
        Data.LookId = copy.Id;
        _main.Profile.Save();
        Changed();
        _main.Toast(L.F("Look “{0}” made: what you change now goes into it", copy.Name));
    }

    private async Task Rename()
    {
        Ensure();
        if (Chosen is not { } look) return;
        var name = await Dialogs.PromptAsync(L.T("Rename the look"), L.T("Name"), look.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == look.Name) return;
        look.Name = "";
        look.Name = Free(name.Trim());
        _main.Profile.Save();
        Changed();
    }

    private async Task Delete()
    {
        Ensure();
        if (Chosen is not { } look || Data.Looks.Count < 2) return;
        // (the one after it, or before it when it's the last)
        int at = Data.Looks.IndexOf(look);
        var next = Data.Looks[at + 1 < Data.Looks.Count ? at + 1 : at - 1];
        if (!await Dialogs.ConfirmAsync(L.T("Delete this look?"), L.F("The look “{0}” will be deleted; “{1}” takes its place.", look.Name, next.Name), L.T("Delete"), true)) return;
        Data.Looks.Remove(look);
        look.DeletePicture(ProfileId);
        next.Give(Data, S, ProfileId, Backdrop);
        Data.LookId = next.Id;
        Applied();
    }

    private async Task Export()
    {
        Ensure();
        Keep();
        if (Chosen is not { } look) return;
        if (await Dialogs.SaveTextAsync(Text.SafeFileName(look.Name) + LookPreset.Extension, LookPreset.Extension, look.ToFile(ProfileId)))
            _main.Toast(L.F("Look “{0}” exported", look.Name));
    }

    // A look's file, or a file of settings (its look): a new preset of this profile, chosen.
    private async Task Import()
    {
        var path = await Dialogs.PickFileAsync(L.T("Import a look"), (L.T("Looks and settings"), new[] { LookPreset.Extension, SettingsFile.Extension }));
        if (path == null) return;
        Ensure();
        LookPreset? look;
        try { look = await Task.Run(() => LookPreset.Read(path, ProfileId)); }
        catch (Exception ex)
        {
            App.Log(ex);
            look = null;
        }
        if (look == null)
        {
            _main.Toast(L.T("This file has no look of Ultimate MP3 Player."));
            return;
        }
        look.Name = Free(look.Name);
        Keep();
        Data.Looks.Add(look);
        look.Give(Data, S, ProfileId, Backdrop);
        Data.LookId = look.Id;
        Applied();
        _main.Toast(L.F("Look “{0}” imported", look.Name));
    }

    // Another profile (or settings imported) changed the list: shown again.
    public void Refresh() => Changed();
}
