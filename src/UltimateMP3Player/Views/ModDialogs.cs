#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Element = Avalonia.Controls.Control;
using DialogKind = UltimateMP3Player.DialogWindow;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Element = System.Windows.FrameworkElement;
using DialogKind = System.Windows.Window;
#endif
using System.Windows.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// A mod's page (Settings › Mods › a mod): what it is and may do, on or off, its visualizers, the settings it asked for drawn
// with the app's own controls, its log; in cards like Settings' own. Linked into the Avalonia app; the phones have theirs
// (Mobile/ModDialogs.cs).
public static class ModDialogs
{
    public static Task ShowAsync(ModItemViewModel item)
    {
        Build(item).ShowDialog();
        return Task.CompletedTask;
    }

    internal static List<(string Title, List<ModSettingViewModel> Items)> Groups(IEnumerable<ModSettingViewModel> list)
    {
        var groups = new List<(string, List<ModSettingViewModel>)>();
        foreach (var s in list)
        {
            if (s.Type == "heading") { groups.Add((s.Title, new())); continue; }
            if (groups.Count == 0) groups.Add((L.T("Settings"), new()));
            groups[^1].Item2.Add(s);
        }
        return groups;
    }

    // One of a list for a mod (ump.ui.list: a beatmap's difficulties): rows like a menu's, the one chosen ticked, scrolling when long.
    public static Task<int> ListAsync(string title, IReadOnlyList<(string Title, string? Hint)> items, int selected)
    {
        var rows = new StackPanel();
        int chosen = -1;
        var cancel = Dialogs.Button(L.T("Cancel"), "GhostButton", isCancel: true);
        var scroll = new ScrollViewer { Content = rows, MaxHeight = Math.Max(240, Both.Room().Height * 0.6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var win = Dialogs.Frame(title, scroll, 440, cancel);
        Element? current = null;
        for (int i = 0; i < items.Count; i++)
        {
            int n = i;
            var line = Both.Columns(true, false);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = items[i].Title, TextWrapping = TextWrapping.Wrap });
            if (items[i].Hint is { } hint)
            {
                var h = Both.Text(hint, "Hint", new Thickness(0, 2, 0, 0));
                h.TextWrapping = TextWrapping.Wrap;
                words.Children.Add(h);
            }
            line.Children.Add(words);
            if (i == selected)
            {
                var tick = Both.At(Both.Glyph("\uE73E", 14, Both.Brush("AccentTextBrush")), 1);
                tick.VerticalAlignment = VerticalAlignment.Center;
                tick.Margin = new Thickness(12, 0, 0, 0);
                line.Children.Add(tick);
            }
            var row = Both.Look(new Button
            {
                Content = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 2),
            }, "SubtleButton");
            if (i == selected) row.Background = Both.Brush("AccentSoftBrush");
            row.Click += (_, _) =>
            {
                chosen = n;
                win.Close();
            };
            rows.Children.Add(row);
            if (i == selected) current = row;
        }
        // (the one chosen in sight, in a long list)
        if (current != null) Both.OnOpened(win, () => current.BringIntoView());
        win.ShowDialog();
        return Task.FromResult(chosen);
    }

    // One of a few choices, then the button (ump.ui.pick: an osu! song you have already), as the app's own "Delete…" asks:
    // cards with a dot, the one chosen in the accent.
    public static Task<int> PickAsync(string title, string message, IReadOnlyList<(string Title, string? Hint)> items, int selected, string okText)
    {
        var body = new StackPanel();
        if (message.Length > 0)
        {
            var m = Both.Text(message, "Hint", new Thickness(0, -4, 0, 12));
            m.TextWrapping = TextWrapping.Wrap;
            body.Children.Add(m);
        }
        var ok = Dialogs.Button(okText, "PrimaryButton", isDefault: true);
        var cards = new List<(Border Card, Border Dot)>();
        int chosen = selected;
        void Update()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var (card, dot) = cards[i];
                bool on = i == chosen;
                card.BorderBrush = Both.Brush(on ? "AccentBrush" : "BorderBrush");
                card.Background = Both.Brush(on ? "AccentSoftBrush" : "Surface2Brush");
                dot.BorderBrush = Both.Brush(on ? "AccentBrush" : "MutedBrush");
                dot.Child = on ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Both.Brush("AccentBrush") } : null;
            }
        }
        for (int i = 0; i < items.Count; i++)
        {
            int n = i;
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(2), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 12, 0),
            };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = items[i].Title, FontWeight = Both.SemiBold, FontSize = 14.5, TextWrapping = TextWrapping.Wrap, Foreground = Both.Brush("TextBrush") });
            if (items[i].Hint is { } hint)
                text.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Foreground = Both.Brush("SubTextBrush"), FontSize = 12.5, LineHeight = 18, Margin = new Thickness(0, 3, 0, 0) });
            var row = new DockPanel();
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(text);
            var card = new Border
            {
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), Padding = new Thickness(14, 11, 14, 12), Margin = new Thickness(0, 0, 0, 8),
                Child = row, Cursor = Both.Hand,
            };
            Both.OnPress(card, _ => { chosen = n; Update(); });
            Both.OnHover(card, () => { if (chosen != n) card.Background = Both.Brush("Surface3Brush"); }, Update);
            cards.Add((card, dot));
            body.Children.Add(card);
        }
        Update();
        var win = Dialogs.Frame(title, body, 460, Dialogs.Button(L.T("Cancel"), "GhostButton", isCancel: true), ok);
        int result = -1;
        ok.Click += (_, _) =>
        {
            result = chosen;
            win.Close();
        };
        Both.OnOpened(win, () => ok.Focus());
        win.ShowDialog();
        return Task.FromResult(result);
    }

    // (the tests show it without waiting)
    public static DialogKind Build(ModItemViewModel item)
    {
        var body = new StackPanel();
        var head = new StackPanel();
        var settings = new StackPanel();
        var log = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 18, Foreground = Both.Brush("SubTextBrush"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono"),
        };
        var logScroll = new ScrollViewer { Content = log, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 10) };
        body.Children.Add(head);
        body.Children.Add(settings);
        // The log is for who makes the mod: folded, open by itself for a folder being worked on or a mod in trouble.
        var logButton = Both.Look(new Button { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 20, 0, 0) }, "GhostButton");
        var logBox = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        logBox.Children.Add(Wrapped(L.T("What the mod writes, the newest at the bottom."), "Hint", new Thickness(4, 0, 0, 8)));
        logBox.Children.Add(Card(logScroll));
        body.Children.Add(logButton);
        body.Children.Add(logBox);

        static TextBlock Wrapped(string text, string style, Thickness margin = default)
        {
            var t = Both.Text(text, style, margin);
            t.TextWrapping = TextWrapping.Wrap;
            return t;
        }
        static TextBlock Plain(string text, Thickness margin = default) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        static TextBlock Section(string text) => Both.Text(text, "FieldLabel", new Thickness(4, 20, 0, 8));
        // Rows on a card, a hairline between two (as Settings' cards).
        static Border Card(params Element[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(new Border { Height = 1, Background = Both.Brush("BorderBrush") });
                stack.Children.Add(rows[i]);
            }
            return new Border { Child = stack, Background = Both.Brush("BgBrush"), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 2, 16, 2) };
        }
        static CheckBox Switch(string title, string? hint, bool on, Action<bool> changed, bool enabled = true)
        {
            var words = new StackPanel();
            words.Children.Add(Plain(title));
            if (hint != null) words.Children.Add(Wrapped(hint, "Hint", new Thickness(0, 2, 0, 0)));
            var sw = Both.Look(new CheckBox { Content = words, IsChecked = on, IsEnabled = enabled, Margin = new Thickness(0, 12, 0, 12) }, "Switch");
            Both.OnTick(sw, changed);
            return sw;
        }

        void FillHead()
        {
            head.Children.Clear();
            var line = Wrapped(item.Line, "Hint");
            if (item.Icon is { } icon)
            {
                var top = Both.Columns(false, true);
                top.Children.Add(new Border
                {
                    Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0),
                    Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill },
                });
                line.VerticalAlignment = VerticalAlignment.Center;
                top.Children.Add(Both.At(line, 1));
                head.Children.Add(top);
            }
            else head.Children.Add(line);
            if (item.Description != null) head.Children.Add(Plain(item.Description, new Thickness(0, 6, 0, 0)));
            // Why it isn't running, in sight (an error in its script, a mod.json this app can't use).
            if (item.Mod.Broken != null || item.HasProblem)
                head.Children.Add(new Border
                {
                    Background = Both.Brush("ErrorSoftBrush"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 14, 0, 0),
                    Child = Plain(item.StatusText),
                });
            var rows = new List<Element>
            {
                Switch(L.T("On"), null, item.Enabled, v => item.Enabled = v, item.Mod.Broken == null),
                Wrapped(item.PermissionsText, "Hint", new Thickness(0, 12, 0, 12)),
            };
            if (item.HasLooks)
            {
                var looks = Both.Look(new Button { Content = item.LooksText, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 10) }, "GhostButton");
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
                settings.Children.Add(Section(L.T("Visualizers")));
                settings.Children.Add(Card(visuals.Select(v => (Element)Switch(v.Title, v.Hint, v.On, on => v.On = on)).ToArray()));
            }
            // Its settings, in a card under each of its headings (the ones before the first under "Settings").
            foreach (var (title, items) in Groups(item.Settings))
            {
                settings.Children.Add(Section(title));
                if (items.Count > 0) settings.Children.Add(Card(items.Select(Make).ToArray()));
            }
        }

        Element Make(ModSettingViewModel s)
        {
            if (s.Type == "switch") return Switch(s.Title, s.Hint, s.On, v => s.On = v);
            var box = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
            switch (s.Type)
            {
                case "slider":
                    var top = Both.Columns(true, false);
                    var value = Both.At(Plain(s.NumberText), 1);
                    value.Foreground = Both.Brush("SubTextBrush");
                    top.Children.Add(Plain(s.Title));
                    top.Children.Add(value);
                    box.Children.Add(top);
                    var slider = new Slider { Minimum = s.S.Min, Maximum = s.S.Max, Value = s.Number, Margin = new Thickness(0, 6, 0, 0) };
                    if (s.S.Step > 0) { slider.TickFrequency = s.S.Step; slider.IsSnapToTickEnabled = true; }
                    slider.ValueChanged += (_, _) => { s.Number = slider.Value; value.Text = s.NumberText; };
                    box.Children.Add(slider);
                    break;
                case "choice" when s.Choices.Count > 6:
                    // A long list that changes (the skins found on the computer): a drop-down, as the app's own such lists.
                    box.Children.Add(Plain(s.Title, new Thickness(0, 0, 0, 8)));
                    var combo = new ComboBox
                    {
                        ItemsSource = s.Choices.Select(c => c.Label).ToList(), Height = 34, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Stretch,
                        SelectedIndex = s.Selected is { } chosen ? s.Choices.ToList().IndexOf(chosen) : -1,
                    };
#if !AVALONIA
                    // (the theme's template is for Choice items; these are plain words)
                    combo.ItemTemplate = null;
#endif
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) s.Selected = s.Choices[combo.SelectedIndex]; };
                    box.Children.Add(combo);
                    break;
                case "choice":
                    box.Children.Add(Plain(s.Title, new Thickness(0, 0, 0, 8)));
                    var tiles = new ChoiceTiles { Items = s.Choices, Selected = s.Selected };
#if AVALONIA
                    tiles.Bind(ChoiceTiles.SelectedProperty, new Binding(nameof(s.Selected)) { Source = s, Mode = BindingMode.TwoWay });
#else
                    tiles.SetBinding(ChoiceTiles.SelectedProperty, new Binding(nameof(s.Selected)) { Source = s, Mode = BindingMode.TwoWay });
#endif
                    box.Children.Add(tiles);
                    break;
                case "text":
                    box.Children.Add(Plain(s.Title, new Thickness(0, 0, 0, 8)));
                    var text = Both.Box(s.Text);
                    text.LostFocus += (_, _) => s.Text = text.Text ?? "";
                    box.Children.Add(text);
                    break;
                case "button":
                    box.Children.Add(Both.Look(new Button { Content = s.Title, Command = s.PressCommand, HorizontalAlignment = HorizontalAlignment.Left }, "GhostButton"));
                    break;
                default: // info
                    if (s.Title.Length > 0) box.Children.Add(Plain(s.Title));
                    break;
            }
            if (s.Hint != null) box.Children.Add(Wrapped(s.Hint, "Hint", new Thickness(0, 6, 0, 0)));
            return box;
        }

        void FillLog()
        {
            log.Text = item.LogText.Length > 0 ? item.LogText : L.T("Nothing written yet.");
            logScroll.ScrollToEnd();
        }

        // Its toasts, here too: the window's own shows under this page.
        var toastText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var toast = new Border
        {
            Child = toastText, Background = Both.Brush("PopupBrush"), BorderBrush = Both.Brush("BorderStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, MaxWidth = 420, IsHitTestVisible = false,
        };
        Both.Show(toast, false);
        var toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Both.Show(toast, false); };
        void Toasted(string text)
        {
            toastText.Text = text;
            Both.Show(toast, true);
            toastTimer.Stop();
            toastTimer.Start();
        }

        var remove = Dialogs.Button(L.T("Remove…"), "GhostButton");
        var reload = Both.Tip(Dialogs.Button(L.T("Reload"), "GhostButton"), L.T("Stop the mod and start it again from its files"));
        bool logOpen = false;
        void ShowLog(bool open)
        {
            logOpen = open;
            Both.Show(logBox, open);
            logButton.Content = open ? L.T("Hide the log") : L.T("Show the log");
            if (open) logScroll.ScrollToEnd();
        }
        logButton.Click += (_, _) => ShowLog(!logOpen);
        // What a developer or a mod in trouble needs (its log, Reload) in sight; a mod that works keeps them out of the way.
        bool Troubled() => item.Mod.IsDev || item.HasProblem || item.Mod.Broken != null;
        void ShowTools()
        {
            Both.Show(reload, item.Mod.IsDev || item.HasProblem);
            if (Troubled() && !logOpen) ShowLog(true);
        }

        FillHead();
        FillSettings();
        FillLog();
        ShowLog(Troubled());
        ShowTools();
        void Changed() { FillHead(); FillSettings(); ShowTools(); }
        item.Changed += Changed;
        item.LogUpdated += FillLog;
        item.Toasted += Toasted;

        var close = Dialogs.Button(L.T("Close"), "PrimaryButton", isDefault: true, isCancel: true);
        var scroll = new ScrollViewer { Content = body, MaxHeight = Math.Max(300, Both.Room().Height * 0.66), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var page = new Grid();
        page.Children.Add(scroll);
        page.Children.Add(toast);
        var win = Dialogs.Frame(item.Name, page, 520, remove, reload, close);
        reload.Click += (_, _) => item.Reload();
        remove.Click += async (_, _) =>
        {
            if (await item.RemoveAsync()) win.Close();
        };
        win.Closed += (_, _) =>
        {
            toastTimer.Stop();
            item.Changed -= Changed;
            item.LogUpdated -= FillLog;
            item.Toasted -= Toasted;
        };
        return win;
    }
}
