using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A mod's page on the phones (Settings › Mods › a mod), in a sheet: what it is and may do, on or off, its visualizers, the
// settings it asked for drawn with the app's own controls, its log; in cards like Settings' own. As the desktops'
// (Views/ModDialogs.cs). Linked into iOS.
public static class ModDialogs
{
    private static SheetHost? Sheets => App.Host.View?.Sheets;

    // One of a list for a mod (ump.ui.list: a beatmap's difficulties): a sheet of rows, the one chosen ticked; -1 if it's closed.
    public static async Task<int> ListAsync(string title, IReadOnlyList<(string Title, string? Hint)> items, int selected)
    {
        var sheets = Sheets;
        if (sheets == null) return -1;
        int chosen = -1;
        var list = new StackPanel { Margin = new Thickness(12, 0, 12, 16) };
        list.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 4, 12, 10) });
        var content = new ScrollViewer { Content = list };
        for (int i = 0; i < items.Count; i++)
        {
            int n = i;
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = items[i].Title, Classes = { "setting" }, TextWrapping = TextWrapping.Wrap });
            if (items[i].Hint is { } hint) words.Children.Add(new TextBlock { Text = hint, Classes = { "setting-hint" }, TextWrapping = TextWrapping.Wrap });
            line.Children.Add(words);
            if (i == selected)
            {
                var tick = new TextBlock { Text = Icons.Map("\uE73E"), Classes = { "glyph" }, Foreground = Ui.Res("AccentTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(tick, 1);
                line.Children.Add(tick);
            }
            var row = new Button { Theme = Ui.Theme("SettingRow"), Content = line, HorizontalAlignment = HorizontalAlignment.Stretch };
            if (i == selected) row.Background = Ui.Res("AccentSoftBrush");
            row.Click += (_, _) =>
            {
                chosen = n;
                sheets.Close(content);
            };
            list.Children.Add(row);
        }
        await sheets.Show(content);
        return chosen;
    }

    // One of a few choices, then the button (ump.ui.pick), as "Delete…" asks: cards with a dot, the one chosen in the accent.
    public static async Task<int> PickAsync(string title, string message, IReadOnlyList<(string Title, string? Hint)> items, int selected, string okText)
    {
        var sheets = Sheets;
        if (sheets == null) return -1;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 4) });
        if (message.Length > 0)
            body.Children.Add(new TextBlock { Text = message, Foreground = Ui.Res("SubTextBrush"), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 14) });
        var cards = new List<(Border Card, Border Dot)>();
        int chosen = selected;
        void Update()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var (card, dot) = cards[i];
                bool on = i == chosen;
                card.BorderBrush = Ui.Res(on ? "AccentBrush" : "BorderBrush");
                card.Background = Ui.Res(on ? "AccentSoftBrush" : "Surface2Brush");
                dot.BorderBrush = Ui.Res(on ? "AccentBrush" : "MutedBrush");
                dot.Child = on ? new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Ui.Res("AccentBrush") } : null;
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            int n = i;
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 14, 0),
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = items[i].Title, FontWeight = FontWeight.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextBrush") });
            if (items[i].Hint is { } hint)
                text.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("SubTextBrush"), FontSize = 13.5, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0) });
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            var card = new Border
            {
                CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 10), Child = row,
            };
            card.Tapped += (_, e) =>
            {
                if (FlingScroll.Caught) return;
                chosen = n;
                Haptics.Tick();
                Update();
                e.Handled = true;
            };
            cards.Add((card, dot));
            body.Children.Add(card);
        }
        Update();
        int result = -1;
        Button Wide(string text, string theme) => new()
        {
            Content = text, Theme = Ui.Theme(theme), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var no = Wide(L.T("Cancel"), "TouchGhost");
        var ok = Wide(okText, "TouchPrimary");
        no.Click += (_, _) => sheets.Close(body);
        ok.Click += (_, _) =>
        {
            result = chosen;
            sheets.Close(body);
        };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(no);
        Grid.SetColumn(ok, 2);
        buttons.Children.Add(ok);
        body.Children.Add(buttons);
        await sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.92);
        return result;
    }

    public static async Task ShowAsync(ModItemViewModel item)
    {
        var sheets = Sheets;
        if (sheets == null) return;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        var title = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 4) };
        var head = new StackPanel();
        var settings = new StackPanel();
        var log = new TextBlock { Classes = { "hint" }, FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), Margin = new Thickness(0, 12) };
        body.Children.Add(title);
        body.Children.Add(head);
        body.Children.Add(settings);
        // The log is for who makes the mod: folded, open by itself for a mod in trouble (as the computers').
        var logButton = Wide("", "TouchGhost", 20);
        var logBox = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        logBox.Children.Add(Words(L.T("What the mod writes, the newest at the bottom."), "setting-hint", new Thickness(4, 0, 0, 8)));
        logBox.Children.Add(Card(log));
        body.Children.Add(logButton);
        body.Children.Add(logBox);

        static TextBlock Label(string text) => new() { Text = text, Classes = { "field-label" }, Margin = new Thickness(4, 18, 0, 8), TextWrapping = TextWrapping.Wrap };
        static TextBlock Words(string text, string cls, Thickness margin = default) => new() { Text = text, Classes = { cls }, TextWrapping = TextWrapping.Wrap, Margin = margin };
        static Button Wide(string text, string theme, double top) => new()
        {
            Content = text, Theme = Ui.Theme(theme), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, top, 0, 0),
        };
        // Rows on a card, a hairline between two (as Settings' cards).
        static Border Card(params Control[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Classes = { "line" } });
                stack.Children.Add(rows[i]);
            }
            // (the sheet is Settings' cards' colour: the wells' one)
            return new Border { Classes = { "card" }, Child = stack, Margin = new Thickness(0), Background = Ui.Res("BgBrush") };
        }
        // A switch at the right of its words, as Settings' rows.
        static Grid Switch(string text, string? hint, bool on, Action<bool> changed, bool enabled = true)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12) };
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(Words(text, "setting"));
            if (hint != null) words.Children.Add(Words(hint, "setting-hint"));
            var sw = new CheckBox { Theme = Ui.Theme("Switch"), Classes = { "switch" }, IsChecked = on, IsEnabled = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            sw.IsCheckedChanged += (_, _) => changed(sw.IsChecked == true);
            Grid.SetColumn(sw, 1);
            row.Children.Add(words);
            row.Children.Add(sw);
            return row;
        }

        void FillHead()
        {
            title.Text = item.Name;
            head.Children.Clear();
            var line = Words(item.Line, "hint", new Thickness(4, 0, 0, 0));
            if (item.Icon is { } icon)
            {
                var top = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4, 4, 0, 0) };
                top.Children.Add(new Border
                {
                    Width = 48, Height = 48, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0),
                    Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill },
                });
                line.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(line, 1);
                top.Children.Add(line);
                head.Children.Add(top);
            }
            else head.Children.Add(line);
            if (item.Description != null) head.Children.Add(Words(item.Description, "setting", new Thickness(4, 6, 0, 0)));
            // Why it isn't running, in sight (an error in its script, a mod.json this app can't use).
            if (item.Mod.Broken != null || item.HasProblem)
                head.Children.Add(new Border
                {
                    Background = Ui.Res("ErrorSoftBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Margin = new Thickness(0, 14, 0, 0),
                    Child = Words(item.StatusText, "setting"),
                });
            var rows = new List<Control>
            {
                Switch(L.T("On"), null, item.Enabled, v => item.Enabled = v, item.Mod.Broken == null),
                Words(item.PermissionsText, "setting-hint", new Thickness(0, 12)),
            };
            if (item.HasLooks)
            {
                var looks = Wide(item.LooksText, "TouchGhost", 0);
                looks.Margin = new Thickness(0, 12);
                looks.Click += (_, _) => _ = item.AddLooksAsync();
                rows.Add(looks);
            }
            var card = Card(rows.ToArray());
            card.Margin = new Thickness(0, 16, 0, 0);
            head.Children.Add(card);
        }

        void FillSettings()
        {
            settings.Children.Clear();
            // Its visualizers as the bars: which one shows (one, whatever mod it comes from). The song page's own are turned on and
            // off by their button there (NowPlayingViewModel.ShowScene).
            var visuals = item.Visualizers.Where(v => v.V.Place == "bars").ToList();
            if (visuals.Count > 0)
            {
                settings.Children.Add(Label(L.T("Visualizers")));
                settings.Children.Add(Card(visuals.Select(v => (Control)Switch(v.Title, v.Hint, v.On, on => v.On = on)).ToArray()));
            }
            // Its settings, in a card under each of its headings (the ones before the first under "Settings").
            var groups = new List<(string Title, List<ModSettingViewModel> Items)>();
            foreach (var s in item.Settings)
            {
                if (s.Type == "heading") { groups.Add((s.Title, new())); continue; }
                if (groups.Count == 0) groups.Add((L.T("Settings"), new()));
                groups[^1].Items.Add(s);
            }
            foreach (var (title, items) in groups)
            {
                settings.Children.Add(Label(title));
                if (items.Count > 0) settings.Children.Add(Card(items.Select(Make).ToArray()));
            }
        }

        Control Make(ModSettingViewModel s)
        {
            if (s.Type == "switch") return Switch(s.Title, s.Hint, s.On, v => s.On = v);
            var box = new StackPanel { Margin = new Thickness(0, 12) };
            switch (s.Type)
            {
                case "slider":
                    var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    var value = new TextBlock { Text = s.NumberText, Classes = { "value" } };
                    Grid.SetColumn(value, 1);
                    top.Children.Add(Words(s.Title, "setting"));
                    top.Children.Add(value);
                    box.Children.Add(top);
                    var slider = new Slider { Minimum = s.S.Min, Maximum = s.S.Max, Value = s.Number };
                    if (s.S.Step > 0) { slider.TickFrequency = s.S.Step; slider.IsSnapToTickEnabled = true; }
                    slider.ValueChanged += (_, _) => { s.Number = slider.Value; value.Text = s.NumberText; };
                    box.Children.Add(slider);
                    break;
                case "choice" when s.Choices.Count > 6:
                    // A long list that changes (the skins found): a row with what's chosen, the choices in a sheet, as Settings' such rows.
                    var pick = new Button { Theme = Ui.Theme("SettingRow"), HorizontalAlignment = HorizontalAlignment.Stretch };
                    var words = new StackPanel();
                    words.Children.Add(Words(s.Title, "setting"));
                    var current = Words(s.Selected?.Label ?? "", "value");
                    words.Children.Add(current);
                    pick.Content = words;
                    pick.Click += (_, _) =>
                    {
                        var menu = new SheetMenu { Title = s.Title };
                        foreach (var c in s.Choices)
                            menu.Add(new(c.Label + (c == s.Selected ? "  ✓" : ""), "", () => { s.Selected = c; current.Text = c.Label; }));
                        Menus.Open(menu);
                    };
                    box.Children.Add(pick);
                    break;
                case "choice":
                    box.Children.Add(Words(s.Title, "setting", new Thickness(0, 0, 0, 8)));
                    var tiles = new ChoiceTiles { Items = s.Choices, Selected = s.Selected };
                    tiles.Bind(ChoiceTiles.SelectedProperty, new Binding(nameof(s.Selected)) { Source = s, Mode = BindingMode.TwoWay });
                    box.Children.Add(tiles);
                    break;
                case "text":
                    box.Children.Add(Words(s.Title, "setting", new Thickness(0, 0, 0, 8)));
                    var text = new TextBox { Text = s.Text, Theme = Ui.Theme("TouchBox") };
                    text.LostFocus += (_, _) => s.Text = text.Text ?? "";
                    box.Children.Add(text);
                    break;
                case "button":
                    var press = Wide(s.Title, "TouchGhost", 0);
                    press.Command = s.PressCommand;
                    box.Children.Add(press);
                    break;
                default: // info
                    if (s.Title.Length > 0) box.Children.Add(Words(s.Title, "setting"));
                    break;
            }
            if (s.Hint != null) box.Children.Add(Words(s.Hint, "setting-hint", new Thickness(0, 6, 0, 0)));
            return box;
        }

        void FillLog() => log.Text = item.LogText.Length > 0 ? string.Join("\n", item.LogText.Split('\n').TakeLast(60)) : L.T("Nothing written yet.");

        // Its toasts, here too: the app's own shows under the sheets.
        var toastText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        var toast = new Border
        {
            Child = toastText, Background = Ui.Res("PopupBrush"), BoxShadow = BoxShadows.Parse("0 4 16 0 #59000000"), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 12), Margin = new Thickness(20, 0, 20, 16), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 520, IsHitTestVisible = false, IsVisible = false,
        };
        var toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); toast.IsVisible = false; };
        void Toasted(string text)
        {
            toastText.Text = text;
            toast.IsVisible = true;
            toastTimer.Stop();
            toastTimer.Start();
        }

        var reload = Wide(L.T("Reload"), "TouchGhost", 20);
        reload.Click += (_, _) => item.Reload();
        bool logOpen = false;
        void ShowLog(bool open)
        {
            logOpen = open;
            logBox.IsVisible = open;
            logButton.Content = open ? L.T("Hide the log") : L.T("Show the log");
        }
        logButton.Click += (_, _) => ShowLog(!logOpen);
        // What a mod in trouble needs (its log, Reload) in sight; a mod that works keeps them out of the way.
        bool Troubled() => item.Mod.IsDev || item.HasProblem || item.Mod.Broken != null;
        void ShowTools()
        {
            reload.IsVisible = item.Mod.IsDev || item.HasProblem;
            if (Troubled() && !logOpen) ShowLog(true);
        }
        FillHead();
        FillSettings();
        FillLog();
        ShowLog(Troubled());
        ShowTools();
        var remove = Wide(L.T("Remove…"), "TouchDanger", 10);
        var page = new Panel();
        page.Children.Add(new ScrollViewer { Content = body });
        page.Children.Add(toast);
        remove.Click += async (_, _) =>
        {
            if (await item.RemoveAsync()) sheets.Close(page);
        };
        body.Children.Add(reload);
        body.Children.Add(remove);
        void Changed() { FillHead(); FillSettings(); ShowTools(); }
        item.Changed += Changed;
        item.LogUpdated += FillLog;
        item.Toasted += Toasted;
        try { await sheets.Show(page); }
        finally
        {
            toastTimer.Stop();
            item.Changed -= Changed;
            item.LogUpdated -= FillLog;
            item.Toasted -= Toasted;
        }
    }
}
