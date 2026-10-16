using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.Views;

public sealed class ProfileTile
{
    public ProfileInfo? Info { get; init; }
    public string Name { get; init; } = "";
    public string Initial => Info?.Initial ?? "";
    public Brush Brush { get; init; } = Brushes.Transparent;
    public ImageSource? Avatar { get; init; }
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
    public Brush Brush { get; }

    private bool _selected;
    public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
}

// "Who's listening?": pick, add or edit profiles (like Chrome).
public partial class ProfilePickerWindow : Window
{
    private readonly AppHost _host;
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
        if (owner is { IsLoaded: true })
        {
            w.Owner = owner;
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        w.ShowDialog();
        return w._picked;
    }

    private ProfilePickerWindow(AppHost host, string? currentId)
    {
        _host = host;
        _currentId = currentId;
        InitializeComponent();
        WindowFrame.Apply(this, Root, null, null, CloseButton);
        SmoothScroll.Attach(this);
        Swatches.ItemsSource = _swatches;
        Remember.IsChecked = host.Settings.StartupProfile != null;
        if (currentId != null) SubTitle.Text = L.T("Pick a profile, add one or edit the existing ones.");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            if (EditPanel.Visibility == Visibility.Visible) CloseEditor();
            else Close();
            e.Handled = true;
        };
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
            tiles.Add(new ProfileTile { Name = L.T("Add profile"), Brush = (Brush)FindResource("Surface2Brush") });
        Tiles.ItemsSource = tiles;
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProfileTile tile) return;
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

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is ProfileTile { Info: { } info }) OpenEditor(info, false);
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
        DeleteButton.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
        DeleteButton.IsEnabled = canDelete;
        DeleteHint.Visibility = !isNew && !canDelete ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
        EditPanel.Visibility = Visibility.Visible;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void CloseEditor()
    {
        EditPanel.Visibility = Visibility.Collapsed;
        _editing = null;
    }

    private void UpdatePreview()
    {
        EditColorDisc.Fill = Ui.BrushFrom(_editColor);
        foreach (var s in _swatches) s.IsSelected = s.Hex.Equals(_editColor, StringComparison.OrdinalIgnoreCase);
        var name = NameBox.Text.Trim();
        EditInitial.Text = name.Length > 0 ? char.ToUpper(name[0]).ToString() : "?";
        ImageSource? avatar = null;
        if (_pendingAvatar != null) avatar = Images.Decode(_pendingAvatar, 176);
        else if (!_removeAvatar && _editing is { HasAvatar: true } p) avatar = Images.Decode(p.AvatarPath, 176);
        EditAvatar.Fill = avatar != null ? new ImageBrush(avatar) { Stretch = Stretch.UniformToFill } : null;
        RemoveAvatarButton.Visibility = avatar != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NameBox_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePreview();

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ColorSwatch s) return;
        _editColor = s.Hex;
        UpdatePreview();
    }

    private void PickAvatar_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Choose a profile photo"),
            Filter = L.T("Images") + "|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif|" + L.T("All files") + "|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        _pendingAvatar = dlg.FileName;
        _removeAvatar = false;
        UpdatePreview();
    }

    private void RemoveAvatar_Click(object sender, RoutedEventArgs e)
    {
        _pendingAvatar = null;
        _removeAvatar = true;
        UpdatePreview();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var name = NameBox.Text.Trim();
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

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseEditor();

    private void Delete_Click(object sender, RoutedEventArgs e)
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
