using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// Small dark dialogs: confirmations and text fields.
public static partial class Dialogs
{
    public static bool Confirm(string title, string message, string ok, bool danger = false)
        => Show(title, message, Array.Empty<(string, string)>(), ok, danger) != null;

    // A message with only "OK".
    public static void Alert(string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SubTextBrush"], LineHeight = 20 };
        var ok = Button("OK", "PrimaryButton", isDefault: true, isCancel: true);
        var win = Frame(title, text, 400, ok);
        ok.Click += (_, _) => win.DialogResult = true;
        win.ShowDialog();
    }

    // A question with a few answers, a button each (the first is the main one, on the right): its index, or -1.
    public static int Choose(string title, string message, params string[] answers)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SubTextBrush"], LineHeight = 20 };
        var buttons = new List<Button> { Button(L.T("Cancel"), "GhostButton", isCancel: true) };
        for (int i = answers.Length - 1; i >= 0; i--) buttons.Add(Button(answers[i], i == 0 ? "PrimaryButton" : "GhostButton", isDefault: i == 0));
        var win = Frame(title, text, 520, buttons.ToArray());
        int chosen = -1;
        for (int i = 0; i < answers.Length; i++)
        {
            int n = i;
            buttons[answers.Length - i].Click += (_, _) =>
            {
                chosen = n;
                win.DialogResult = true;
            };
        }
        return win.ShowDialog() == true ? chosen : -1;
    }

    // ok: the button's word ("Save" when none).
    public static string? Prompt(string title, string label, string initial, string? ok = null)
        => Show(title, null, new[] { (label, initial) }, ok ?? L.T("Save"), false)?[0];

    // Bumped when the terms of use change: they're asked again.
    public const int TermsVersion = 1;

    // The terms of use. ask: the first start, they must be accepted (false = the app closes); otherwise only to read them.
    public static bool Terms(bool ask)
    {
        var res = Application.Current.Resources;
        var sub = (Brush)res["SubTextBrush"];
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Ultimate MP3 Player is a tool: it only downloads what you ask it to, through third-party programs and sites, and it doesn't host or distribute any music."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 20,
        });
        stack.Children.Add(new TextBlock { Text = L.T("By using it you declare that:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        foreach (var point in new[]
                 {
                     "you will only download songs, videos and images that you already lawfully own (for example music you bought) or that you otherwise have the right to download, such as free content or content its creators allow;",
                     "you alone are responsible for what you download, how you use it and what you share with others (Listen together included), in compliance with the copyright laws of your country and the terms of the sites you download from;",
                     "the author of the app doesn't control the content you download and is in no way liable for the use you make of it.",
                 })
        {
            var row = new DockPanel { Margin = new Thickness(2, 6, 0, 0) };
            var dot = new TextBlock { Text = "•", Foreground = (Brush)res["AccentTextBrush"], FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = L.T(point), TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 20 });
            stack.Children.Add(row);
        }
        if (!ask)
        {
            var close = Button(L.T("Close"), "PrimaryButton", isDefault: true, isCancel: true);
            var info = Frame(L.T("Terms of use"), stack, 480, close);
            close.Click += (_, _) => info.DialogResult = true;
            info.ShowDialog();
            return true;
        }
        stack.Children.Add(new TextBlock
        {
            Text = L.T("If you don't agree, quit: the app closes and you can uninstall it."), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)res["MutedBrush"], FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0),
        });
        var ok = Button(L.T("I accept"), "PrimaryButton", isDefault: true);
        ok.IsEnabled = false;
        var check = new CheckBox { Content = L.T("I have read and accept these terms"), Margin = new Thickness(0, 16, 0, 0) };
        check.Checked += (_, _) => ok.IsEnabled = true;
        check.Unchecked += (_, _) => ok.IsEnabled = false;
        stack.Children.Add(check);
        var win = Frame(L.T("Before you start"), stack, 480, Button(L.T("Quit"), "GhostButton", isCancel: true), ok);
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => check.Focus();
        return win.ShowDialog() == true;
    }

    // ------------------------------------------------------------------ files and folders

    // filters: (name, extensions like ".jpg"); an "all files" choice is added at the end.
    public static string[]? PickFiles(string title, bool multiple, params (string Name, IEnumerable<string> Extensions)[] filters)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = title,
            Multiselect = multiple,
            Filter = string.Join("|", filters.Select(f => f.Name + "|" + string.Join(";", f.Extensions.Select(e => "*" + e)))
                .Append(L.T("All files") + "|*.*")),
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    public static string? PickFile(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => PickFiles(title, false, filters)?.FirstOrDefault();

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif" };

    public static string? PickImage() => PickFile(L.T("Choose an image"), (L.T("Images"), ImageExtensions));

    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initial != null) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static (string Title, string Artist, string Album, string Bpm)? EditTrack(Track t)
    {
        var bpm = t.Bpm?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "";
        var r = Show(L.T("Edit info"), null,
            new[] { (L.T("Title"), t.Title), (L.T("Artist"), t.Artist ?? ""), (L.T("Album"), t.Album ?? ""), ("BPM", bpm) }, L.T("Save"), false);
        return r == null ? null : (r[0], r[1], r[2], r[3]);
    }

    // The same dialogs for the view models shared with the Android app, which wait for them with await (there a dialog
    // can't hold the code up). Here they're the ones above.
    public static Task<bool> ConfirmAsync(string title, string message, string ok, bool danger = false) => Task.FromResult(Confirm(title, message, ok, danger));
    public static Task<string?> PromptAsync(string title, string label, string initial, string? ok = null) => Task.FromResult(Prompt(title, label, initial, ok));
    public static Task<int> ChooseAsync(string title, string message, params string[] answers) => Task.FromResult(Choose(title, message, answers));
    public static Task<(string Title, string Artist, string Album, string Bpm)?> EditTrackAsync(Track t) => Task.FromResult(EditTrack(t));
    // A few text fields under a message (and a button that opens a page, for what goes in them): their values, or null.
    public static Task<string[]?> FormAsync(string title, string? message, (string Text, string Url)? link, params (string Label, string Value)[] fields)
        => Task.FromResult(Show(title, message, fields, L.T("Save"), false, link));
    public static Task<string?> PickImageAsync() => Task.FromResult(PickImage());
    public static Task<string?> PickFolderAsync(string title, string? initial = null) => Task.FromResult(PickFolder(title, initial));
    public static Task<string[]?> PickFilesAsync(string title, bool multiple, params (string Name, IEnumerable<string> Extensions)[] filters)
        => Task.FromResult(PickFiles(title, multiple, filters));
    public static Task<string?> PickFileAsync(string title, params (string Name, IEnumerable<string> Extensions)[] filters) => Task.FromResult(PickFile(title, filters));

    // Settings › Backup (BackupsViewModel): on a computer a folder like any other (OneDrive, a USB disk...).
    public const string BookmarkPrefix = "bookmark:";
    public static string DefaultBackupFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ultimate MP3 Player", "Backups");
    public static Task<IBackupFolder?> OpenBackupFolderAsync(string? setting) => Task.FromResult<IBackupFolder?>(new LocalBackupFolder(setting ?? DefaultBackupFolder));
    public static Task<(string Setting, string Name)?> PickBackupFolderAsync()
        => Task.FromResult(PickFolder(L.T("Where to keep the backups"), DefaultBackupFolder) is { } dir ? (dir, dir) : ((string, string)?)null);

    private static string[]? Show(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger, (string Text, string Url)? link = null)
    {
        var res = Application.Current.Resources;
        var stack = new StackPanel();
        if (message != null)
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["SubTextBrush"], LineHeight = 20 });
        if (link is { } page)
        {
            var open = Button(page.Text, "GhostButton");
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 12, 0, 2);
            open.Click += (_, _) => ViewModels.MainViewModel.OpenUrl(page.Url);
            stack.Children.Add(open);
        }

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 10, 0, 6), TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = value, Style = (Style)res["BoxTextBox"] };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        string[]? result = null;
        var okButton = Button(ok, danger ? "DangerButton" : "PrimaryButton", isDefault: true);
        var win = Frame(title, stack, 400, Button(L.T("Cancel"), "GhostButton", isCancel: true), okButton);
        okButton.Click += (_, _) =>
        {
            result = boxes.Select(b => b.Text).ToArray();
            win.DialogResult = true;
        };
        win.Loaded += (_, _) =>
        {
            if (boxes.Count > 0)
            {
                boxes[0].Focus();
                boxes[0].SelectAll();
            }
            else okButton.Focus();
        };
        return win.ShowDialog() == true ? result : null;
    }

    // The archive's versions of a song's lyrics: on the left whose they are, their length and whether they have times; on
    // the right the words of the one picked. The chosen one, or null.
    public static Task<LyricsChoice?> ChooseLyricsAsync(string song, double duration, IReadOnlyList<LyricsChoice> choices)
    {
        var res = Application.Current.Resources;
        Brush B(string key) => (Brush)res[key];
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var previewScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = preview };
        var rows = new StackPanel();
        var grid = new Grid { Height = 400, Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = B("BgBrush"), Padding = new Thickness(6),
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = rows },
        });
        var previewBox = new Border { CornerRadius = new CornerRadius(10), Background = B("BgBrush"), Padding = new Thickness(16, 12, 8, 12), Child = previewScroll };
        Grid.SetColumn(previewBox, 2);
        grid.Children.Add(previewBox);

        LyricsChoice? picked = null;
        var borders = new List<Border>();
        void Pick(int i)
        {
            picked = choices[i];
            for (int k = 0; k < borders.Count; k++) borders[k].Background = k == i ? B("AccentSoftBrush") : Brushes.Transparent;
            preview.Text = picked.Words;
            previewScroll.ScrollToTop();
        }
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            int n = i;
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = c.Artist, FontSize = 12.5, Foreground = B("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = c.Details(duration), FontSize = 12, Foreground = B(c.Synced ? "SuccessBrush" : "MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = info };
            row.MouseLeftButtonUp += (_, _) => Pick(n);
            borders.Add(row);
            rows.Children.Add(row);
        }

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.T("The versions LRCLIB has, the likeliest first. Those with times light up line by line as you listen."),
            TextWrapping = TextWrapping.Wrap, Foreground = B("SubTextBrush"), LineHeight = 20,
        });
        body.Children.Add(grid);
        var use = Button(L.T("Use these lyrics"), "PrimaryButton", isDefault: true);
        var win = Frame(L.F("Lyrics of “{0}”", song), body, 760, Button(L.T("Cancel"), "GhostButton", isCancel: true), use);
        use.Click += (_, _) => win.DialogResult = true;
        Pick(0);
        return Task.FromResult(win.ShowDialog() == true ? picked : null);
    }

    public static Button Button(string text, string style, bool isDefault = false, bool isCancel = false)
        => new() { Content = text, Style = (Style)Application.Current.Resources[style], IsDefault = isDefault, IsCancel = isCancel, MinWidth = 96 };

    // The dark card every dialog uses: title, body, buttons on the right. Cancel buttons close it by themselves.
    public static Window Frame(string title, UIElement body, double width, params Button[] buttons)
    {
        var res = Application.Current.Resources;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? App.Host?.Window;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsLoaded: true } ? owner : null,
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 14,
            Foreground = (Brush)res["TextBrush"],
            Title = title,
            UseLayoutRounding = true,
        };

        var stack = new StackPanel { Width = width };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(buttons[i]);
        }
        stack.Children.Add(row);

        var card = new Border
        {
            Background = (Brush)res["SurfaceBrush"],
            BorderBrush = (Brush)res["BorderStrongBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(18),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };
        // Dragging the card moves the dialog, but not from something clickable (its release would be lost).
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || IsClickable(e.OriginalSource as DependencyObject, card)) return;
            try { win.DragMove(); } catch { }
        };
        win.Content = card;
        return win;
    }

    private static bool IsClickable(DependencyObject? d, DependencyObject card)
    {
        for (; d != null && d != card; d = Ui.Parent(d))
            if (d is Control || d is FrameworkElement { Cursor: not null } fe && fe.Cursor == Cursors.Hand) return true;
        return false;
    }
}
