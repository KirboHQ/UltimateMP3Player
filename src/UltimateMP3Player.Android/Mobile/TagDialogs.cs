using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using Avalonia.Controls.Primitives;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Create/edit a tag, and put a playlist's tags on its songs (the computer apps' TagDialogs, as sheets).
public static class TagDialogs
{
    private static IBrush B(string key) => Ui.Res(key);

    // Name and colour (one of the palette or any #RRGGBB), with a live preview of the chip.
    public static async Task<(string Name, string Color)?> EditAsync(string? name, string color)
    {
        var sheets = App.Host.View?.Sheets;
        if (sheets == null) return null;
        bool isNew = name == null;
        var chosen = color;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = L.T(isNew ? "Nuovo tag" : "Modifica tag"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });

        body.Children.Add(Ui.Text(L.T("Nome"), "field-label", new Thickness(4, 8, 0, 8)));
        var nameBox = new TextBox { Text = name ?? "", Theme = Ui.Theme("TouchBox"), Watermark = L.T("Es. chill, anni 2000, palestra…") };
        body.Children.Add(nameBox);

        body.Children.Add(Ui.Text(L.T("Colore"), "field-label", new Thickness(4, 16, 0, 8)));
        var swatches = new SwatchGrid();
        var rings = new Dictionary<string, Ellipse>(StringComparer.OrdinalIgnoreCase);
        var dot = new Ellipse { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center };
        var hexBox = new TextBox { Theme = Ui.Theme("TouchBox"), Width = 150, HorizontalAlignment = HorizontalAlignment.Left, Watermark = "#RRGGBB", Text = color };
        void Pick(string hex, bool fromSwatch)
        {
            chosen = hex;
            foreach (var (h, r) in rings) r.Opacity = h.Equals(hex, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            dot.Fill = Ui.BrushFrom(hex);
            if (fromSwatch) hexBox.Text = hex;
        }
        foreach (var hex in Tag.Palette)
        {
            var ring = new Ellipse { Stroke = B("TextBrush"), StrokeThickness = 2.5, Opacity = 0 };
            var swatch = new Grid { Width = 46, Height = 46, Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
            swatch.Children.Add(ring);
            swatch.Children.Add(new Ellipse { Margin = new Thickness(6), Fill = Ui.BrushFrom(hex) });
            var h = hex;
            swatch.Tapped += (_, e) => { Pick(h, true); e.Handled = true; };
            rings[hex] = ring;
            swatches.Children.Add(swatch);
        }
        body.Children.Add(swatches);
        var custom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var customLabel = new TextBlock { Text = L.T("Personalizzato"), Foreground = B("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
        DockPanel.SetDock(customLabel, Dock.Left);
        custom.Children.Add(customLabel);
        custom.Children.Add(hexBox);
        body.Children.Add(custom);

        body.Children.Add(Ui.Text(L.T("Anteprima"), "field-label", new Thickness(4, 16, 0, 8)));
        var chipText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
        chipRow.Children.Add(dot);
        chipRow.Children.Add(chipText);
        body.Children.Add(new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 6, 14, 6), Background = B("Surface3Brush"), HorizontalAlignment = HorizontalAlignment.Left, Child = chipRow });

        var ok = new Button { Content = L.T(isNew ? "Crea" : "Salva"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = L.T("Annulla"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 22, 0, 0) };
        row.Children.Add(cancel);
        Grid.SetColumn(ok, 2);
        row.Children.Add(ok);
        body.Children.Add(row);

        void Refresh()
        {
            var n = (nameBox.Text ?? "").Trim();
            chipText.Text = n.Length > 0 ? n : L.T("Nome del tag");
            ok.IsEnabled = n.Length > 0;
        }
        hexBox.TextChanged += (_, _) => { if (ParseHex(hexBox.Text ?? "") is { } h && !h.Equals(chosen, StringComparison.OrdinalIgnoreCase)) Pick(h, false); };
        nameBox.TextChanged += (_, _) => Refresh();
        Pick(color, false);
        Refresh();

        bool done = false;
        ok.Click += (_, _) => { done = true; sheets.Close(body); };
        cancel.Click += (_, _) => sheets.Close(body);
        var shown = sheets.Show(new ScrollViewer { Content = body });
        Dispatcher.UIThread.Post(() => { nameBox.Focus(); nameBox.SelectAll(); }, DispatcherPriority.Background);
        await shown;
        return done ? ((nameBox.Text ?? "").Trim(), chosen) : null;
    }

    private static string? ParseHex(string s)
    {
        s = s.Trim().TrimStart('#');
        return s.Length == 6 && int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ? "#" + s.ToUpperInvariant() : null;
    }

    // Which tags, on which songs; Add false = take them off.
    public static async Task<(List<TrackViewModel> Songs, List<TagViewModel> Tags, bool Add)?> ApplyToSongsAsync(PlaylistViewModel p, List<TrackViewModel> songs)
    {
        var sheets = App.Host.View?.Sheets;
        if (sheets == null) return null;
        var main = songs[0].Main;
        var chosenTags = main.Tags.Where(t => p.P.Tags.Contains(t.Id)).ToHashSet();
        var choices = songs.Select(t => new SongChoice(t)).ToList();
        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 18) };

        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(new TextBlock { Text = L.F("Tag sui brani di «{0}»", p.Name), FontSize = 19, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 6) });
        top.Children.Add(Ui.Text(L.T("Scegli i tag e i brani: puoi aggiungerli o toglierli a tutti insieme."), "hint", new Thickness(4, 0, 4, 12)));
        var tagPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        top.Children.Add(tagPanel);
        var header = new DockPanel { Margin = new Thickness(4, 10, 0, 8) };
        var toggleAll = new Button { Theme = Ui.Theme("SmallGhost") };
        DockPanel.SetDock(toggleAll, Dock.Right);
        var countText = new TextBlock { Foreground = B("SubTextBrush"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(toggleAll);
        header.Children.Add(countText);
        top.Children.Add(header);
        body.Children.Add(top);

        var add = new Button { Content = L.T("Aggiungi ai brani"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var remove = new Button { Content = L.T("Togli dai brani"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Children.Add(remove);
        Grid.SetColumn(add, 2);
        buttons.Children.Add(add);
        body.Children.Add(buttons);

        var list = new ItemsControl { ItemsSource = choices, ItemTemplate = Ui.Find<Avalonia.Controls.Templates.IDataTemplate>("SongChoiceTemplate") };
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 360 });

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
            var chip = new ToggleButton { Theme = Ui.Theme("Chip"), IsChecked = chosenTags.Contains(tag), Margin = new Thickness(0, 0, 8, 8) };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = tag.Brush, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = tag.Name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            chip.Content = content;
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked == true) chosenTags.Add(tag);
                else chosenTags.Remove(tag);
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
        add.Click += (_, _) => { adding = true; sheets.Close(body); };
        remove.Click += (_, _) => { adding = false; sheets.Close(body); };
        await sheets.Show(body, maxHeightShare: 0.92);
        if (adding == null) return null;
        return (choices.Where(c => c.IsSelected).Select(c => c.Track).ToList(), main.Tags.Where(chosenTags.Contains).ToList(), adding.Value);
    }
}
