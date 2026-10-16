using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

public sealed class ProfileTile
{
    public ProfileInfo? Info { get; init; }
    public string Name { get; init; } = "";
    public string Initial => Info?.Initial ?? "";
    public IBrush Brush { get; init; } = Brushes.Transparent;
    public IImage? Avatar { get; init; }
    public bool IsAdd => Info == null;
    public bool IsCurrent { get; init; }
}

public sealed class ColorSwatch : Observable
{
    public ColorSwatch(string hex)
    {
        Hex = hex;
        Brush = Ui.BrushFrom(hex);
    }

    public string Hex { get; }
    public IBrush Brush { get; }

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

// "Who's listening?": pick, add or edit profiles (like Chrome).
public partial class ProfilePickerWindow : DialogWindow
{
    private readonly AppHost _host = null!;
    private string? _currentId;
    private bool _currentDeleted;
    private ProfileInfo? _picked;
    private ProfileInfo? _editing;
    private bool _editingIsNew;
    private string _editColor = ProfileInfo.Palette[0];
    private string? _pendingAvatar;
    private bool _removeAvatar;
    private readonly List<ColorSwatch> _swatches = ProfileInfo.Palette.Select(c => new ColorSwatch(c)).ToList();

    public static ProfileInfo? Pick(AppHost host, Window? owner, bool startup)
    {
        var w = new ProfilePickerWindow(host, startup ? null : host.Session?.Profile.Info.Id);
        w.ShowDialog();
        return w._picked;
    }

    public ProfilePickerWindow() => InitializeComponent();

    private ProfilePickerWindow(AppHost host, string? currentId) : this()
    {
        _host = host;
        _currentId = currentId;
        // A window of its own (not one of the small dialogs): framed like the main one, resizable, on the taskbar.
        SystemDecorations = SystemDecorations.Full;
        TransparencyLevelHint = Array.Empty<WindowTransparencyLevel>();
        SizeToContent = SizeToContent.Manual;
        CanResize = true;
        ShowInTaskbar = true;
        WindowFrame.Apply(this, Root, TitleBar, TitleText, CloseButton, null, null, CloseButton, ResizeEdges);
        Swatches.ItemsSource = _swatches;
        Remember.IsChecked = host.Settings.StartupProfile != null;
        if (currentId != null) SubTitle.Text = L.T("Pick a profile, add one or edit the existing ones.");
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (EditPanel.IsVisible) CloseEditor();
            else Close();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Profile in use deleted, none chosen: take the first.
        Closing += (_, _) => { if (_currentDeleted && _picked == null) _picked = _host.Profiles.Profiles.FirstOrDefault(); };
        Refresh();
    }

    private void Refresh()
    {
        var tiles = _host.Profiles.Profiles.Select(p => new ProfileTile
        {
            Info = p,
            Name = p.Name,
            Brush = Ui.BrushFrom(p.Color),
            Avatar = p.HasAvatar ? Images.Decode(p.AvatarPath, 208) : null,
            IsCurrent = p.Id == _currentId,
        }).ToList();
        if (tiles.Count < 12)
            tiles.Add(new ProfileTile { Name = L.T("Add profile"), Brush = Ui.Res("Surface2Brush") });
        Tiles.ItemsSource = tiles;
    }

    private void Tile_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ProfileTile tile) return;
        if (tile.IsAdd)
        {
            OpenEditor(new ProfileInfo
            {
                Name = "",
                Color = ProfileInfo.Palette[_host.Profiles.Profiles.Count % ProfileInfo.Palette.Length],
            }, true);
            return;
        }
        _picked = tile.Info;
        _host.Settings.StartupProfile = Remember.IsChecked == true ? tile.Info!.Id : null;
        _host.Settings.Save();
        Close();
    }

    private void Edit_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Control)?.DataContext is ProfileTile { Info: { } info }) OpenEditor(info, false);
    }

    // ------------------------------------------------------------------ editor

    private void OpenEditor(ProfileInfo info, bool isNew)
    {
        _editing = info;
        _editingIsNew = isNew;
        _editColor = info.Color;
        _pendingAvatar = null;
        _removeAvatar = false;
        EditTitle.Text = L.T(isNew ? "New profile" : "Edit profile");
        NameBox.Text = info.Name;
        bool canDelete = _host.Profiles.Profiles.Count > 1;
        DeleteButton.IsVisible = !isNew;
        DeleteButton.IsEnabled = canDelete;
        DeleteHint.IsVisible = !isNew && !canDelete;
        UpdatePreview();
        EditPanel.IsVisible = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void CloseEditor()
    {
        EditPanel.IsVisible = false;
        _editing = null;
    }

    private void UpdatePreview()
    {
        EditColorDisc.Fill = Ui.BrushFrom(_editColor);
        foreach (var s in _swatches) s.IsSelected = s.Hex.Equals(_editColor, StringComparison.OrdinalIgnoreCase);
        var name = (NameBox.Text ?? "").Trim();
        EditInitial.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
        IImage? avatar = null;
        if (_pendingAvatar != null) avatar = Images.Decode(_pendingAvatar, 176);
        else if (!_removeAvatar && _editing is { HasAvatar: true } p) avatar = Images.Decode(p.AvatarPath, 176);
        EditAvatar.Fill = avatar != null ? new ImageBrush((Avalonia.Media.Imaging.Bitmap)avatar) { Stretch = Stretch.UniformToFill } : null;
        RemoveAvatarButton.IsVisible = avatar != null;
    }

    private void NameBox_Changed(object? sender, TextChangedEventArgs e) => UpdatePreview();

    private void Swatch_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || (sender as Control)?.DataContext is not ColorSwatch s) return;
        _editColor = s.Hex;
        UpdatePreview();
    }

    private void PickAvatar_Click(object? sender, RoutedEventArgs e)
    {
        if (Dialogs.PickFile(L.T("Choose a profile photo"), (L.T("Images"), new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" })) is not { } file) return;
        _pendingAvatar = file;
        _removeAvatar = false;
        UpdatePreview();
    }

    private void RemoveAvatar_Click(object? sender, RoutedEventArgs e)
    {
        _pendingAvatar = null;
        _removeAvatar = true;
        UpdatePreview();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        var p = _editing;
        p.Name = name;
        p.Color = _editColor;
        if (_editingIsNew) _host.Profiles.Profiles.Add(p);
        if (_pendingAvatar != null)
        {
            if (await AudioAnalysis.MakeCoverFromImageAsync(_pendingAvatar, p.AvatarPath))
            {
                p.HasAvatar = true;
                p.AvatarVersion++;
            }
        }
        else if (_removeAvatar)
        {
            try { File.Delete(p.AvatarPath); } catch { }
            p.HasAvatar = false;
        }
        _host.Profiles.Save();
        CloseEditor();
        Refresh();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CloseEditor();

    private void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_editing is not { } p || _editingIsNew || _host.Profiles.Profiles.Count < 2) return;
        bool inUse = p.Id == _currentId;
        if (!Dialogs.Confirm(L.T("Delete this profile?"),
                L.F("The profile “{0}” will be deleted along with its playlists, favorites, history and equalizer. Downloaded songs stay in the other profiles' libraries.", p.Name) +
                (inUse ? "\n\n" + L.T("It's the profile in use: afterwards you'll choose which one to continue with.") : ""),
                L.T("Delete"), true)) return;
        _host.Profiles.Profiles.Remove(p);
        _host.Profiles.Save();
        if (_host.Settings.StartupProfile == p.Id)
        {
            _host.Settings.StartupProfile = null;
            _host.Settings.Save();
        }
        Profile.Delete(p.Id);
        if (inUse)
        {
            _currentDeleted = true;
            _currentId = null;
            SubTitle.Text = L.T("The profile was deleted: choose which profile to continue with.");
        }
        CloseEditor();
        Refresh();
    }
}
