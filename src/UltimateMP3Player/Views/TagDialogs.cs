using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Create/edit a tag, and put a playlist's tags on its songs.
public static class TagDialogs
{
    private static ResourceDictionary Res => Application.Current.Resources;
    private static Brush B(string key) => (Brush)Res[key];

    // For the view models shared with the Android app (Dialogs.ConfirmAsync).
    public static Task<(string Name, string Color)?> EditAsync(string? name, string color) => Task.FromResult(Edit(name, color));
    public static Task<(List<TrackViewModel> Songs, List<TagViewModel> Tags, bool Add)?> ApplyToSongsAsync(PlaylistViewModel p, List<TrackViewModel> songs)
        => Task.FromResult(ApplyToSongs(p, songs));

    // Name and colour (one of the palette or any #RRGGBB), with a live preview of the chip.
    public static (string Name, string Color)? Edit(string? name, string color)
    {
        bool isNew = name == null;
        var chosen = color;
        var body = new StackPanel();

        body.Children.Add(new TextBlock { Text = L.T("Nome"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 4, 0, 6) });
        var nameBox = new TextBox { Text = name ?? "", Style = (Style)Res["BoxTextBox"], Tag = L.T("Es. chill, anni 2000, palestra…") };
        body.Children.Add(nameBox);

        var hexBox = new TextBox { Style = (Style)Res["BoxTextBox"], Width = 130, HorizontalAlignment = HorizontalAlignment.Left, Tag = "#RRGGBB", Text = color };
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };

        body.Children.Add(new TextBlock { Text = L.T("Colore"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 14, 0, 6) });
        var swatches = new WrapPanel();
        var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        foreach (var hex in Core.Tag.Palette)
        {
            var ring = new Ellipse { Stroke = B("TextBrush"), StrokeThickness = 2, Opacity = 0 };
            var swatch = new Grid { Width = 34, Height = 34, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = hex };
            swatch.Children.Add(ring);
            swatch.Children.Add(new Ellipse { Margin = new Thickness(5), Fill = Ui.BrushFrom(hex) });
            swatch.MouseLeftButtonDown += (_, e) => { Pick(hex, true); e.Handled = true; };
            rings[hex] = ring;
            swatches.Children.Add(swatch);
        }
        body.Children.Add(swatches);

        var custom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        custom.Children.Add(new TextBlock { Text = L.T("Personalizzato"), Foreground = B("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 12, 0) });
        custom.Children.Add(hexBox);
        body.Children.Add(custom);

        body.Children.Add(new TextBlock { Text = L.T("Anteprima"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 16, 0, 8) });
        var chipText = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(dot);
        chipRow.Children.Add(chipText);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 12, 4), Background = B("Surface3Brush"), HorizontalAlignment = HorizontalAlignment.Left, Child = chipRow });

        var ok = Dialogs.Button(L.T(isNew ? "Crea" : "Salva"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T(isNew ? "Nuovo tag" : "Modifica tag"), body, 380, Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true), ok);

        void Pick(string hex, bool fromSwatch)
        {
            chosen = hex;
            foreach (var (h, r) in rings) r.Opacity = h.Equals(hex, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            dot.Fill = Ui.BrushFrom(hex);
            if (fromSwatch) hexBox.Text = hex;
        }
        void Refresh()
        {
            chipText.Text = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : L.T("Nome del tag");
            ok.IsEnabled = nameBox.Text.Trim().Length > 0;
        }
        hexBox.TextChanged += (_, _) => { if (ParseHex(hexBox.Text) is { } h && !h.Equals(chosen, StringComparison.OrdinalIgnoreCase)) Pick(h, false); };
        nameBox.TextChanged += (_, _) => Refresh();
        Pick(color, false);
        Refresh();

        bool done = false;
        ok.Click += (_, _) =>
        {
            done = true;
            win.DialogResult = true;
        };
        win.Loaded += (_, _) =>
        {
            nameBox.Focus();
            nameBox.SelectAll();
        };
        win.ShowDialog();
        return done ? (nameBox.Text.Trim(), chosen) : null;
    }

    private static string? ParseHex(string s)
    {
        s = s.Trim().TrimStart('#');
        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + s.ToUpperInvariant() : null;
    }

    // Which tags, on which songs; Add false = take them off.
    public static (List<TrackViewModel> Songs, List<TagViewModel> Tags, bool Add)? ApplyToSongs(PlaylistViewModel p, List<TrackViewModel> songs)
    {
        var main = songs[0].Main;
        var chosenTags = main.Tags.Where(t => p.P.Tags.Contains(t.Id)).ToHashSet();
        var choices = songs.Select(t => new SongChoice(t)).ToList();
        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = L.T("Scegli i tag e i brani: puoi aggiungerli o toglierli a tutti insieme."),
            Style = (Style)Res["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        body.Children.Add(new TextBlock { Text = L.T("Tag"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 0, 0, 8) });
        var tagPanel = new WrapPanel();
        body.Children.Add(tagPanel);

        var header = new DockPanel { Margin = new Thickness(0, 14, 0, 8), LastChildFill = false };
        var toggleAll = new Button { Style = (Style)Res["GhostButton"], Padding = new Thickness(12, 5, 12, 5) };
        var countText = new TextBlock { Foreground = B("SubTextBrush"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        header.Children.Add(new TextBlock { Text = L.T("Brani"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(toggleAll);
        header.Children.Add(countText);
        body.Children.Add(header);

        var list = new ListBox { Style = (Style)Res["PlainList"], ItemsSource = choices, ItemTemplate = (DataTemplate)Res["SongChoiceTemplate"] };
        body.Children.Add(new Border
        {
            Height = 320, CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(6), Child = list,
        });

        var add = Dialogs.Button(L.T("Aggiungi ai brani"), "PrimaryButton", isDefault: true);
        var remove = Dialogs.Button(L.T("Togli dai brani"), "GhostButton");
        var win = Dialogs.Frame(L.F("Tag sui brani di «{0}»", p.Name), body, 560, Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true), remove, add);

        void Update()
        {
            int n = choices.Count(c => c.IsSelected);
            countText.Text = L.F("{0} di {1} selezionati", n, choices.Count);
            toggleAll.Content = n == choices.Count ? L.T("Deseleziona tutti") : L.T("Seleziona tutti");
            add.IsEnabled = remove.IsEnabled = n > 0 && chosenTags.Count > 0;
        }
        foreach (var t in main.Tags)
        {
            var tag = t;
            var chip = TagToggle(tag, chosenTags.Contains(tag));
            chip.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (!chosenTags.Remove(tag)) chosenTags.Add(tag);
                SetToggle(chip, tag, chosenTags.Contains(tag));
                Update();
            };
            tagPanel.Children.Add(chip);
        }
        foreach (var c in choices) c.PropertyChanged += (_, _) => Update();
        toggleAll.Click += (_, _) =>
        {
            bool all = choices.All(c => c.IsSelected);
            foreach (var c in choices) c.IsSelected = !all;
        };
        Update();

        bool? adding = null;
        add.Click += (_, _) => { adding = true; win.DialogResult = true; };
        remove.Click += (_, _) => { adding = false; win.DialogResult = true; };
        win.ShowDialog();
        if (adding == null) return null;
        return (choices.Where(c => c.IsSelected).Select(c => c.Track).ToList(), main.Tags.Where(chosenTags.Contains).ToList(), adding.Value);
    }

    // A clickable chip: dot + name, highlighted when chosen.
    private static Border TagToggle(TagViewModel t, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = t.Brush, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = t.Name, FontSize = 13, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(11, 5, 13, 5), Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = row,
        };
        SetToggle(chip, t, on);
        return chip;
    }

    private static void SetToggle(Border chip, TagViewModel t, bool on)
    {
        chip.Background = on ? B("AccentSoftBrush") : B("Surface3Brush");
        chip.BorderBrush = on ? t.Brush : Brushes.Transparent;
    }
}
