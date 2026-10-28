using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player;

// A dialog that waits like WPF's ShowDialog: the code that opens it goes on once it's closed (a nested loop of the
// dispatcher), so the shared view models keep asking "are you sure?" in one line.
public class DialogWindow : Window
{
    private DispatcherFrame? _frame;
    private Window? _owner;

    public DialogWindow()
    {
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = App.WindowIcon;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                DialogResult = false;
                e.Handled = true;
            }
        };
    }

    private bool? _result;
    // Setting it closes the dialog (like WPF).
    public bool? DialogResult
    {
        get => _result;
        set
        {
            _result = value;
            Close();
        }
    }

    // The window it belongs to: the active one, otherwise the main window (none before the app has a window).
    public static Window? ActiveOwner()
    {
        var windows = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows ?? Array.Empty<Window>();
        return windows.LastOrDefault(w => w.IsActive && w.IsVisible) ?? (App.Host?.Window is { IsVisible: true } m ? m : windows.LastOrDefault(w => w.IsVisible));
    }

    public bool? ShowDialog()
    {
        _owner = ActiveOwner();
        if (_owner == null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }
        Closed += (_, _) =>
        {
            if (_owner != null)
            {
                _owner.IsEnabled = true;
                _owner.Activate();
            }
            if (_frame != null) _frame.Continue = false;
        };
        if (_owner != null)
        {
            _owner.IsEnabled = false;
            Show(_owner);
        }
        else Show();
        // In front with the keyboard: otherwise the first click only brings it forward.
        Activate();
        _frame = new DispatcherFrame();
        Dispatcher.UIThread.PushFrame(_frame);
        return _result;
    }
}

// Small dark dialogs: confirmations and text fields.
public static partial class Dialogs
{
    public static bool Confirm(string title, string message, string ok, bool danger = false)
        => Show(title, message, Array.Empty<(string, string)>(), ok, danger) != null;

    // A message with only "OK".
    public static void Alert(string title, string message)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20 };
        var ok = Button("OK", "PrimaryButton", isDefault: true, isCancel: true);
        var win = Frame(title, text, 400, ok);
        ok.Click += (_, _) => win.DialogResult = true;
        win.ShowDialog();
    }

    // A question with a few answers, a button each (the first is the main one, on the right): its index, or -1.
    public static int Choose(string title, string message, params string[] answers)
    {
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20 };
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

    // Ticks to choose what to do (the parts of an import): which ones are ticked, or null. ok is enabled with one ticked.
    public static Task<bool[]?> ChecksAsync(string title, string message, (string Text, bool On)[] options, string ok)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20 });
        var checks = options.Select(o => new CheckBox
        {
            Content = new TextBlock { Text = o.Text, TextWrapping = TextWrapping.Wrap }, IsChecked = o.On, Margin = new Thickness(0, 14, 0, 0),
        }).ToList();
        var go = Button(ok, "PrimaryButton", isDefault: true);
        foreach (var c in checks)
        {
            stack.Children.Add(c);
            c.IsCheckedChanged += (_, _) => go.IsEnabled = checks.Any(x => x.IsChecked == true);
        }
        go.IsEnabled = checks.Any(x => x.IsChecked == true);
        var win = Frame(title, stack, 520, Button(L.T("Cancel"), "GhostButton", isCancel: true), go);
        go.Click += (_, _) => win.DialogResult = true;
        return Task.FromResult(win.ShowDialog() == true ? checks.Select(c => c.IsChecked == true).ToArray() : null);
    }

    // ok: the button's word ("Save" when none).
    public static string? Prompt(string title, string label, string initial, string? ok = null)
        => Show(title, null, new[] { (label, initial) }, ok ?? L.T("Save"), false)?[0];

    private static IBrush Res(string key) => Ui.Find<IBrush>(key) ?? Brushes.Gray;
    private static T Res<T>(string key) where T : class => Ui.Find<T>(key);

    // Bumped when the terms of use change: they're asked again.
    public const int TermsVersion = 1;

    // The terms of use. ask: the first start, they must be accepted (false = the app closes); otherwise only to read them.
    public static bool Terms(bool ask)
    {
        var sub = Res("SubTextBrush");
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Ultimate MP3 Player is a tool: it only downloads what you ask it to, through third-party programs and sites, and it doesn't host or distribute any music."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 20,
        });
        stack.Children.Add(new TextBlock { Text = L.T("By using it you declare that:"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        foreach (var point in new[]
                 {
                     "you will only download songs, videos and images that you already lawfully own (for example music you bought) or that you otherwise have the right to download, such as free content or content its creators allow;",
                     "you alone are responsible for what you download, how you use it and what you share with others (Listen together included), in compliance with the copyright laws of your country and the terms of the sites you download from;",
                     "the author of the app doesn't control the content you download and is in no way liable for the use you make of it.",
                 })
        {
            var row = new DockPanel { Margin = new Thickness(2, 6, 0, 0) };
            var dot = new TextBlock { Text = "•", Foreground = Res("AccentTextBrush"), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 10, 0) };
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
            Foreground = Res("MutedBrush"), FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0),
        });
        var ok = Button(L.T("I accept"), "PrimaryButton", isDefault: true);
        ok.IsEnabled = false;
        var check = new CheckBox { Content = L.T("I have read and accept these terms"), Margin = new Thickness(0, 16, 0, 0) };
        check.IsCheckedChanged += (_, _) => ok.IsEnabled = check.IsChecked == true;
        stack.Children.Add(check);
        var win = Frame(L.T("Before you start"), stack, 480, Button(L.T("Quit"), "GhostButton", isCancel: true), ok);
        ok.Click += (_, _) => win.DialogResult = true;
        win.Opened += (_, _) => check.Focus();
        return win.ShowDialog() == true;
    }

    // "What's new" at the first start after an update (Views/WhatsNew.cs).
    public static Task WhatsNewAsync(IReadOnlyList<ReleaseNote> notes, AppSettings settings)
    {
        WhatsNew(notes, settings);
        return Task.CompletedTask;
    }

    // "Don't show again" under the notes turns AppSettings.ShowWhatsNew off.
    public static void WhatsNew(IReadOnlyList<ReleaseNote> notes, AppSettings settings)
    {
        var scroll = new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(0, 0, 12, 0), Child = Views.WhatsNew.Build(notes, false) },
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 560,
        };
        var hide = new CheckBox { Content = L.T("Don't show again"), IsChecked = !settings.ShowWhatsNew, Margin = new Thickness(0, 16, 0, 0) };
        var all = new StackPanel { Children = { scroll, hide } };
        var close = Button(L.T("Close"), "PrimaryButton", isDefault: true, isCancel: true);
        var win = Frame(L.T("What's new"), all, 540, close);
        win.ShowDialog();
        settings.ShowWhatsNew = hide.IsChecked != true;
        settings.Save();
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

    // Settings › Backup (BackupsViewModel): on a computer a folder like any other (a cloud drive's, a USB disk...).
    public const string BookmarkPrefix = "bookmark:";
    public static string DefaultBackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ultimate MP3 Player", "Backups");
    public static Task<IBackupFolder?> OpenBackupFolderAsync(string? setting) => Task.FromResult<IBackupFolder?>(new LocalBackupFolder(setting ?? DefaultBackupFolder));
    public static Task<(string Setting, string Name)?> PickBackupFolderAsync()
        => Task.FromResult(PickFolder(L.T("Where to keep the backups"), DefaultBackupFolder) is { } dir ? (dir, dir) : ((string, string)?)null);

    private static string[]? Show(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger, (string Text, string Url)? link = null)
    {
        var stack = new StackPanel();
        if (message != null)
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20 });
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
            var l = new TextBlock { Text = label, Margin = new Thickness(2, 10, 0, 6), TextWrapping = TextWrapping.Wrap };
            l.Classes.Add("field-label");
            stack.Children.Add(l);
            var box = new TextBox { Text = value, Theme = Res<Avalonia.Styling.ControlTheme>("BoxTextBox") };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        string[]? result = null;
        var okButton = Button(ok, danger ? "DangerButton" : "PrimaryButton", isDefault: true);
        var win = Frame(title, stack, 400, Button(L.T("Cancel"), "GhostButton", isCancel: true), okButton);
        okButton.Click += (_, _) =>
        {
            result = boxes.Select(b => b.Text ?? "").ToArray();
            win.DialogResult = true;
        };
        win.Opened += (_, _) =>
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
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        var previewScroll = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = preview };
        var rows = new StackPanel();
        var grid = new Grid { Height = 400, Margin = new Thickness(0, 12, 0, 0), ColumnDefinitions = new ColumnDefinitions("300,14,*") };
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = Res("BgBrush"), Padding = new Thickness(6),
            Child = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = rows },
        });
        var previewBox = new Border { CornerRadius = new CornerRadius(10), Background = Res("BgBrush"), Padding = new Thickness(16, 12, 8, 12), Child = previewScroll };
        Grid.SetColumn(previewBox, 2);
        grid.Children.Add(previewBox);

        LyricsChoice? picked = null;
        var borders = new List<Border>();
        void Pick(int i)
        {
            picked = choices[i];
            for (int k = 0; k < borders.Count; k++) borders[k].Background = k == i ? Res("AccentSoftBrush") : Brushes.Transparent;
            preview.Text = picked.Words;
            previewScroll.ScrollToHome();
        }
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            int n = i;
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = c.Title, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = c.Artist, FontSize = 12.5, Foreground = Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = c.Details(duration), FontSize = 12, Foreground = Res(c.Synced ? "SuccessBrush" : "MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            var row = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = info,
            };
            row.PointerPressed += (_, _) => Pick(n);
            borders.Add(row);
            rows.Children.Add(row);
        }

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.T("The versions LRCLIB has, the likeliest first. Those with times light up line by line as you listen."),
            TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20,
        });
        body.Children.Add(grid);
        var use = Button(L.T("Use these lyrics"), "PrimaryButton", isDefault: true);
        var win = Frame(L.F("Lyrics of “{0}”", song), body, 760, Button(L.T("Cancel"), "GhostButton", isCancel: true), use);
        use.Click += (_, _) => win.DialogResult = true;
        Pick(0);
        return Task.FromResult(win.ShowDialog() == true ? picked : null);
    }

    // "Choose the artist…": the artists the name may be on the music sites (picture, site, fans, how many of your songs), the
    // likeliest first. The chosen one, or null.
    public static Task<ArtistChoice?> ChooseArtistAsync(string name, IReadOnlyList<ArtistChoice> choices)
    {
        var rows = new StackPanel();
        ArtistChoice? picked = null;
        var borders = new List<Border>();
        DialogWindow? win = null;
        void Pick(int i)
        {
            picked = choices[i];
            for (int k = 0; k < borders.Count; k++) borders[k].Background = k == i ? Res("AccentSoftBrush") : Brushes.Transparent;
        }
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            int n = i;
            var line = new DockPanel();
            line.Children.Add(ArtistCircle(c, 48));
            if (c.IsCurrent)
            {
                var now = new TextBlock { Text = L.T("Shown now"), FontSize = 12, Foreground = Res("AccentTextBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
                DockPanel.SetDock(now, Dock.Right);
                line.Children.Add(now);
            }
            var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = c.Details, FontSize = 12.5, Foreground = Res(c.Site.Songs > 0 ? "SuccessBrush" : "SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
            line.Children.Add(info);
            var row = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Child = line,
            };
            row.PointerPressed += (_, e) =>
            {
                Pick(n);
                if (e.ClickCount == 2 && win != null) win.DialogResult = true;
            };
            borders.Add(row);
            rows.Children.Add(row);
        }

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = L.T("The artists with this name on Deezer, YouTube Music and SoundCloud, the likeliest first: the ones that have your songs. The one you choose gives the picture, the popular songs and the new releases."),
            TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20,
        });
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = Res("BgBrush"), Padding = new Thickness(6), Margin = new Thickness(0, 12, 0, 0), MaxHeight = 420,
            Child = new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = rows },
        });
        var use = Button(L.T("Use this artist"), "PrimaryButton", isDefault: true);
        win = Frame(L.F("Who is “{0}”?", name), body, 560, Button(L.T("Cancel"), "GhostButton", isCancel: true), use);
        use.Click += (_, _) => win.DialogResult = true;
        Pick(0);
        return Task.FromResult(win.ShowDialog() == true ? picked : null);
    }

    // An artist's picture in a circle, its initial on its colour until (or when no) picture comes. Linked into the phones.
    public static Control ArtistCircle(ArtistChoice c, double size)
    {
        var circle = new Panel { Width = size, Height = size };
        circle.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Fill = c.Brush });
        circle.Children.Add(new TextBlock
        {
            Text = c.Initial, Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = size * 0.42, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var photo = new Views.CoverImage { IsCircle = true };
        photo.Bind(Views.CoverImage.SourceProperty, new Avalonia.Data.Binding(nameof(ArtistChoice.Picture)) { Source = c });
        circle.Children.Add(photo);
        return circle;
    }

    public static Button Button(string text, string style, bool isDefault = false, bool isCancel = false)
        => new() { Content = text, Theme = Res<Avalonia.Styling.ControlTheme>(style), IsDefault = isDefault, IsCancel = isCancel, MinWidth = 96 };

    // The dark card every dialog uses: title, body, buttons on the right. Cancel buttons close it by themselves.
    public static DialogWindow Frame(string title, Control body, double width, params Button[] buttons)
    {
        var win = new DialogWindow
        {
            FontFamily = Res<FontFamily>("UiFont"),
            FontSize = 14,
            Foreground = Res("TextBrush"),
            Title = title,
        };

        var stack = new StackPanel { Width = width };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 10), TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(body);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            if (i > 0) buttons[i].Margin = new Thickness(10, 0, 0, 0);
            if (buttons[i].IsCancel) buttons[i].Click += (_, _) => win.DialogResult = false;
            row.Children.Add(buttons[i]);
        }
        stack.Children.Add(row);

        var card = new Border
        {
            Background = Res("SurfaceBrush"),
            BorderBrush = Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(18),
            Child = stack,
            BoxShadow = BoxShadows.Parse("0 6 24 0 #8C000000"),
        };
        // Dragging the card moves the dialog, but not from something clickable.
        card.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
            if (e.Source is Visual v && (v.FindAncestorOfType<Button>(true) != null || v.FindAncestorOfType<TextBox>(true) != null ||
                                        v.FindAncestorOfType<CheckBox>(true) != null || v.FindAncestorOfType<ComboBox>(true) != null ||
                                        v.FindAncestorOfType<ListBox>(true) != null || v.FindAncestorOfType<Slider>(true) != null)) return;
            try { win.BeginMoveDrag(e); } catch { }
        };
        win.Content = card;
        return win;
    }

    // ------------------------------------------------------------------ files and folders

    private static IStorageProvider? Storage => DialogWindow.ActiveOwner()?.StorageProvider ?? App.Host?.Window?.StorageProvider;

    // Waits for an async system dialog like WPF's (the shared code asks and goes on).
    private static T? Wait<T>(Func<Task<T>> run)
    {
        var frame = new DispatcherFrame();
        T? result = default;
        Dispatcher.UIThread.Post(async () =>
        {
            try { result = await run(); }
            catch { }
            finally { frame.Continue = false; }
        });
        Dispatcher.UIThread.PushFrame(frame);
        return result;
    }

    // filters: (name, extensions like ".jpg"); an "all files" choice is added at the end.
    public static string[]? PickFiles(string title, bool multiple, params (string Name, IEnumerable<string> Extensions)[] filters)
    {
        if (Storage is not { } storage) return null;
        var types = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*" + e).ToList() }).ToList();
        types.Add(new FilePickerFileType(L.T("All files")) { Patterns = new[] { "*" } });
        var files = Wait(() => storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types }));
        var paths = files?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        return paths is { Length: > 0 } ? paths : null;
    }

    public static string? PickFile(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => PickFiles(title, false, filters)?.FirstOrDefault();

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif" };

    public static string? PickImage() => PickFile(L.T("Choose an image"), (L.T("Images"), ImageExtensions));

    public static string? PickFolder(string title, string? initial = null)
    {
        if (Storage is not { } storage) return null;
        var start = initial != null && Directory.Exists(initial) ? Wait(() => storage.TryGetFolderFromPathAsync(initial)) : null;
        var folders = Wait(() => storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = start }));
        return folders?.FirstOrDefault()?.TryGetLocalPath();
    }

    // A file to save (the export of a pack).
    public static string? PickSave(string title, string fileName, string filterName, string extension)
    {
        if (Storage is not { } storage) return null;
        var file = Wait(() => storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title, SuggestedFileName = fileName, DefaultExtension = extension.TrimStart('.'), ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(filterName) { Patterns = new[] { "*" + extension } } },
        }));
        return file?.TryGetLocalPath();
    }
}
