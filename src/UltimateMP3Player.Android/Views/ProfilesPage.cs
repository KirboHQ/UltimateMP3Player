using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

// "Who's listening?" (the computer apps' ProfilePickerWindow): the profiles as big round tiles, a tap opens one, the
// pencil edits it (name, colour, photo, delete), "+" adds one. At start with several profiles, or from the avatar.
public sealed class ProfilesPage : UserControl
{
    private readonly AppHost _host;
    private readonly bool _startup;
    private string? _currentId;
    private bool _currentDeleted;
    private readonly WrapPanel _tiles = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _subtitle;
    private readonly CheckBox _remember;
    private bool _editing;

    public ProfilesPage(AppHost host, bool startup)
    {
        _host = host;
        _startup = startup;
        _currentId = startup ? null : host.Session?.Profile.Info.Id;
        var stack = new StackPanel { Margin = new Thickness(20, 40, 20, 30) };
        if (!startup)
        {
            var back = new Button { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(""), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, -24, 0, 8) };
            back.Click += (_, _) => Done?.Invoke(null);
            stack.Children.Add(back);
        }
        stack.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/app-256.png"))), Width = 64, Height = 64 });
        stack.Children.Add(new TextBlock { Text = L.T("Chi sta ascoltando?"), FontSize = 26, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 6) });
        _subtitle = new TextBlock
        {
            Text = startup ? L.T("Ognuno ha le sue playlist, i preferiti, la cronologia e l'equalizzatore; i brani sono di tutti.")
                : L.T("Scegli un profilo, aggiungine uno o modifica quelli esistenti."),
            Classes = { "hint" }, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 420, Margin = new Thickness(0, 0, 0, 28),
        };
        stack.Children.Add(_subtitle);
        stack.Children.Add(_tiles);
        _remember = new CheckBox
        {
            Content = new TextBlock { Text = L.T("Apri sempre questo profilo all'avvio"), TextWrapping = TextWrapping.Wrap },
            IsChecked = host.Settings.StartupProfile != null, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0),
        };
        stack.Children.Add(_remember);
        Content = new ScrollViewer { Content = stack };
        Background = Ui.Res("BgBrush");
        Refresh();
    }

    // The profile chosen (null: none, back where you were).
    public event Action<ProfileInfo?>? Done;

    // Back: the editor first; at start there's nothing behind.
    public bool Back()
    {
        if (_editing) return false;
        if (_startup) return false;
        Done?.Invoke(_currentDeleted ? _host.Profiles.Profiles.FirstOrDefault() : null);
        return true;
    }

    private void Refresh()
    {
        _tiles.Children.Clear();
        foreach (var p in _host.Profiles.Profiles) _tiles.Children.Add(Tile(p));
        if (_host.Profiles.Profiles.Count < 12) _tiles.Children.Add(Tile(null));
    }

    private Control Tile(ProfileInfo? p)
    {
        var disc = new Panel { Width = 96, Height = 96 };
        if (p == null)
        {
            disc.Children.Add(new Ellipse { Fill = Ui.Res("Surface2Brush") });
            var plus = new TextBlock { Text = Icons.Map(""), FontSize = 34, Foreground = Ui.Res("SubTextBrush"), HorizontalAlignment = HorizontalAlignment.Center };
            plus.Classes.Add("glyph");
            disc.Children.Add(plus);
        }
        else
        {
            disc.Children.Add(new Ellipse { Fill = Ui.BrushFrom(p.Color) });
            disc.Children.Add(new TextBlock { Text = p.Initial, FontSize = 38, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            if (p.HasAvatar && Images.Decode(p.AvatarPath, 208) is { } img)
                disc.Children.Add(new Ellipse { Margin = new Thickness(3), Fill = new ImageBrush(img) { Stretch = Stretch.UniformToFill } });
            if (p.Id == _currentId) disc.Children.Add(new Ellipse { Stroke = Ui.Res("AccentBrush"), StrokeThickness = 3, Margin = new Thickness(-5) });
        }
        var name = new TextBlock
        {
            Text = p?.Name ?? L.T("Aggiungi profilo"), FontSize = 15, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120, Margin = new Thickness(0, 10, 0, 0),
            Foreground = p == null ? Ui.Res("SubTextBrush") : Ui.Res("TextBrush"),
        };
        var col = new StackPanel { Width = 130, Margin = new Thickness(4, 0, 4, 18), Background = Brushes.Transparent };
        col.Children.Add(disc);
        col.Children.Add(name);
        if (p != null)
        {
            var edit = new Button { Theme = Ui.Theme("SmallGhost"), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var pen = new TextBlock { Text = Icons.Map(""), FontSize = 13 };
            pen.Classes.Add("glyph");
            row.Children.Add(pen);
            row.Children.Add(new TextBlock { Text = L.T("Modifica"), Margin = new Thickness(6, 0, 0, 0) });
            edit.Content = row;
            edit.Click += (_, _) => _ = Edit(p, false);
            col.Children.Add(edit);
        }
        col.Tapped += (_, e) =>
        {
            if (Ui.FindAncestor<Button>(e.Source as Visual) != null) return;
            if (p == null)
            {
                _ = Edit(new ProfileInfo { Name = "", Color = ProfileInfo.Palette[_host.Profiles.Profiles.Count % ProfileInfo.Palette.Length] }, true);
                return;
            }
            _host.Settings.StartupProfile = _remember.IsChecked == true ? p.Id : null;
            _host.Settings.Save();
            Done?.Invoke(p);
        };
        return col;
    }

    // ------------------------------------------------------------------ editor (a sheet)

    private async Task Edit(ProfileInfo p, bool isNew)
    {
        var sheets = App.Host.View?.Sheets;
        if (sheets == null) return;
        _editing = true;
        string color = p.Color;
        string? pendingAvatar = null;
        bool removeAvatar = false;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = L.T(isNew ? "Nuovo profilo" : "Modifica profilo"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 14) });

        var disc = new Ellipse { Width = 84, Height = 84 };
        var initial = new TextBlock { FontSize = 32, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var avatar = new Ellipse { Margin = new Thickness(3) };
        var preview = new Panel { Width = 84, Height = 84, HorizontalAlignment = HorizontalAlignment.Center };
        preview.Children.Add(disc);
        preview.Children.Add(initial);
        preview.Children.Add(avatar);
        body.Children.Add(preview);
        var photoRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var pick = new Button { Theme = Ui.Theme("SmallGhost"), Content = L.T("Scegli una foto…") };
        var remove = new Button { Theme = Ui.Theme("SmallGhost"), Content = L.T("Togli la foto"), Margin = new Thickness(8, 0, 0, 0) };
        photoRow.Children.Add(pick);
        photoRow.Children.Add(remove);
        body.Children.Add(photoRow);

        body.Children.Add(Ui.Text(L.T("Nome"), "field-label", new Thickness(4, 16, 0, 8)));
        var nameBox = new TextBox { Theme = Ui.Theme("TouchBox"), Text = p.Name, Watermark = L.T("Il tuo nome") };
        body.Children.Add(nameBox);
        body.Children.Add(Ui.Text(L.T("Colore"), "field-label", new Thickness(4, 16, 0, 8)));
        var swatches = new SwatchGrid();
        var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        void Update()
        {
            disc.Fill = Ui.BrushFrom(color);
            foreach (var (h, r) in rings) r.Opacity = h.Equals(color, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var n = (nameBox.Text ?? "").Trim();
            initial.Text = n.Length > 0 ? char.ToUpper(n[0]).ToString() : "?";
            IImage? img = pendingAvatar != null ? Images.Decode(pendingAvatar, 176) : !removeAvatar && p.HasAvatar ? Images.Decode(p.AvatarPath, 176) : null;
            avatar.Fill = img is Avalonia.Media.Imaging.Bitmap bmp ? new ImageBrush(bmp) { Stretch = Stretch.UniformToFill } : null;
            remove.IsVisible = img != null;
        }
        foreach (var hex in ProfileInfo.Palette)
        {
            var ring = new Ellipse { Stroke = Ui.Res("TextBrush"), StrokeThickness = 2.5, Opacity = 0 };
            var swatch = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
            swatch.Children.Add(ring);
            swatch.Children.Add(new Ellipse { Margin = new Thickness(6), Fill = Ui.BrushFrom(hex) });
            var h = hex;
            swatch.Tapped += (_, _) => { color = h; Update(); };
            rings[hex] = ring;
            swatches.Children.Add(swatch);
        }
        body.Children.Add(swatches);
        nameBox.TextChanged += (_, _) => Update();
        pick.Click += async (_, _) =>
        {
            if (await Dialogs.PickImageAsync() is not { } file) return;
            pendingAvatar = file;
            removeAvatar = false;
            Update();
        };
        remove.Click += (_, _) =>
        {
            pendingAvatar = null;
            removeAvatar = true;
            Update();
        };

        var save = new Button { Theme = Ui.Theme("TouchPrimary"), Content = L.T("Salva"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Theme = Ui.Theme("TouchGhost"), Content = L.T("Annulla"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 20, 0, 0) };
        row.Children.Add(cancel);
        Grid.SetColumn(save, 2);
        row.Children.Add(save);
        body.Children.Add(row);
        if (!isNew)
        {
            var delete = new Button
            {
                Theme = Ui.Theme("TouchDanger"), Content = L.T("Elimina il profilo"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), IsEnabled = _host.Profiles.Profiles.Count > 1,
            };
            delete.Click += async (_, _) =>
            {
                sheets.Close(body);
                await Delete(p);
            };
            body.Children.Add(delete);
            if (_host.Profiles.Profiles.Count < 2)
                body.Children.Add(Ui.Text(L.T("È l'unico profilo: per eliminarlo creane prima un altro."), "hint", new Thickness(4, 8, 0, 0)));
        }
        Update();
        bool saved = false;
        save.Click += async (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                nameBox.Focus();
                return;
            }
            p.Name = name;
            p.Color = color;
            if (isNew) _host.Profiles.Profiles.Add(p);
            if (pendingAvatar != null)
            {
                if (await AudioAnalysis.MakeCoverFromImageAsync(pendingAvatar, p.AvatarPath))
                {
                    p.HasAvatar = true;
                    p.AvatarVersion++;
                }
            }
            else if (removeAvatar)
            {
                try { File.Delete(p.AvatarPath); } catch { }
                p.HasAvatar = false;
            }
            _host.Profiles.Save();
            saved = true;
            sheets.Close(body);
        };
        cancel.Click += (_, _) => sheets.Close(body);
        var shown = sheets.Show(new ScrollViewer { Content = body });
        if (isNew) Dispatcher.UIThread.Post(() => nameBox.Focus(), DispatcherPriority.Background);
        await shown;
        _editing = false;
        if (saved) Refresh();
    }

    private async Task Delete(ProfileInfo p)
    {
        if (_host.Profiles.Profiles.Count < 2) return;
        bool inUse = p.Id == _currentId;
        if (!await Dialogs.ConfirmAsync(L.T("Eliminare il profilo?"),
                L.F("Il profilo «{0}» verrà eliminato con le sue playlist, i preferiti, la cronologia e l'equalizzatore. I brani scaricati restano nella libreria degli altri profili.", p.Name) +
                (inUse ? "\n\n" + L.T("È il profilo in uso: dopo scegli con quale continuare.") : ""),
                L.T("Elimina"), true)) return;
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
            _subtitle.Text = L.T("Il profilo è stato eliminato: scegli con quale profilo continuare.");
        }
        Refresh();
    }
}
