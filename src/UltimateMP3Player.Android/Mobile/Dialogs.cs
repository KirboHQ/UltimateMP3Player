using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Views;

namespace UltimateMP3Player;

// The dialogs of the shared view models, as sheets from the bottom. On a phone nothing can wait for a dialog in one line
// (the computer apps' ShowDialog): the view models await these.
public static partial class Dialogs
{
    // Bumped when the terms of use change: they're asked again.
    public const int TermsVersion = 1;

    private static SheetHost? Sheets => App.Host?.View?.Sheets;

    public static async Task<bool> ConfirmAsync(string title, string message, string ok, bool danger = false)
        => await ShowAsync(title, message, Array.Empty<(string, string)>(), ok, danger) != null;

    // ok: the button's word ("Save" when none).
    public static async Task<string?> PromptAsync(string title, string label, string initial, string? ok = null)
        => (await ShowAsync(title, null, new[] { (label, initial) }, ok ?? L.T("Save"), false))?[0];

    // A few text fields under a message (and a button that opens a page, for what goes in them): their values, or null.
    public static Task<string[]?> FormAsync(string title, string? message, (string Text, string Url)? link, params (string Label, string Value)[] fields)
        => ShowAsync(title, message, fields, L.T("Save"), false, link: link);

    // The archive's versions of a song's lyrics: the list (whose they are, their length, whether they have times), the
    // words of the one picked under it. The chosen one, or null.
    public static async Task<LyricsChoice?> ChooseLyricsAsync(string song, double duration, IReadOnlyList<LyricsChoice> choices)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        var body = new DockPanel { Margin = new Thickness(20, 4, 20, 18) };
        var head = new StackPanel { Margin = new Thickness(4, 0, 4, 12) };
        head.Children.Add(new TextBlock { Text = L.F("Lyrics of “{0}”", song), FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap });
        head.Children.Add(new TextBlock
        {
            Text = L.T("The versions LRCLIB has, the likeliest first. Those with times light up line by line as you listen."),
            Classes = { "hint" }, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        var use = new Button
        {
            Content = L.T("Use these lyrics"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
        };
        DockPanel.SetDock(use, Dock.Bottom);
        body.Children.Add(use);

        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 15, LineHeight = 22 };
        var previewScroll = new ScrollViewer { Content = preview };
        var rows = new StackPanel();
        var grid = new Grid { RowDefinitions = new RowDefinitions("*,10,*") };
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Ui.Res("BgBrush"), Padding = new Thickness(6), Child = new ScrollViewer { Content = rows } });
        var previewBox = new Border { CornerRadius = new CornerRadius(14), Background = Ui.Res("BgBrush"), Padding = new Thickness(16, 12, 12, 12), Child = previewScroll };
        Grid.SetRow(previewBox, 2);
        grid.Children.Add(previewBox);
        body.Children.Add(grid);

        LyricsChoice? picked = null, result = null;
        var borders = new List<Border>();
        void Pick(int i)
        {
            picked = choices[i];
            for (int k = 0; k < borders.Count; k++) borders[k].Background = k == i ? Ui.Res("AccentSoftBrush") : Brushes.Transparent;
            preview.Text = picked.Words;
            previewScroll.ScrollToHome();
        }
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            int n = i;
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = c.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = c.Artist, FontSize = 13, Foreground = Ui.Res("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock
            {
                Text = c.Details(duration), FontSize = 12.5, Foreground = Ui.Res(c.Synced ? "SuccessBrush" : "MutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
            });
            var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Background = Brushes.Transparent, Child = info };
            row.Tapped += (_, e) => { Pick(n); e.Handled = true; };
            borders.Add(row);
            rows.Children.Add(row);
        }
        use.Click += (_, _) =>
        {
            result = picked;
            sheets.Close(body);
        };
        Pick(0);
        await sheets.Show(body);
        return result;
    }

    public static async Task<(string Title, string Artist, string Album, string Bpm)?> EditTrackAsync(Track t)
    {
        var bpm = t.Bpm?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "";
        var r = await ShowAsync(L.T("Edit info"), null,
            new[] { (L.T("Title"), t.Title), (L.T("Artist"), t.Artist ?? ""), (L.T("Album"), t.Album ?? ""), ("BPM", bpm) }, L.T("Save"), false);
        return r == null ? null : (r[0], r[1], r[2], r[3]);
    }

    // A message with only "OK".
    public static void Alert(string title, string message) => _ = ShowAsync(title, message, Array.Empty<(string, string)>(), "OK", false, cancel: false);

    // A question with a few answers, a button each, one under the other (the first is the main one): its index, or -1.
    public static async Task<int> ChooseAsync(string title, string message, params string[] answers)
    {
        var sheets = Sheets;
        if (sheets == null) return -1;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(new TextBlock { Text = message, Classes = { "hint" }, FontSize = 14.5, LineHeight = 21, Margin = new Thickness(0, 0, 0, 12) });
        int chosen = -1;
        for (int i = 0; i < answers.Length; i++)
        {
            int n = i;
            var b = new Button
            {
                Content = answers[i], Theme = Ui.Theme(i == 0 ? "TouchPrimary" : "TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0),
            };
            b.Click += (_, _) =>
            {
                chosen = n;
                sheets.Close(body);
            };
            body.Children.Add(b);
        }
        await sheets.Show(new ScrollViewer { Content = body });
        return chosen;
    }

    // Title, text, fields; the OK button's answer (the fields' texts), or null.
    private static async Task<string[]?> ShowAsync(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger, bool cancel = true,
        (string Text, string Url)? link = null)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (message != null) body.Children.Add(new TextBlock { Text = message, Classes = { "hint" }, FontSize = 14.5, LineHeight = 21 });
        if (link is { } page)
        {
            var open = new Button
            {
                Content = page.Text, Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0),
            };
            open.Click += (_, _) => ViewModels.MainViewModel.OpenUrl(page.Url);
            body.Children.Add(open);
        }
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            body.Children.Add(new TextBlock { Text = label, Classes = { "field-label" }, Margin = new Thickness(4, 12, 0, 8), TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = value, Theme = Ui.Theme("TouchBox") };
            boxes.Add(box);
            body.Children.Add(box);
        }
        string[]? result = null;
        var okButton = new Button { Content = ok, Theme = Ui.Theme(danger ? "TouchDanger" : "TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        // A long answer ("Leave and change language") gets the whole width: the two buttons one above the other.
        bool stack = cancel && ok.Length > 18;
        var row = new Grid
        {
            ColumnDefinitions = cancel && !stack ? new ColumnDefinitions("*,12,*") : new ColumnDefinitions("*"),
            RowDefinitions = stack ? new RowDefinitions("Auto,10,Auto") : new RowDefinitions("Auto"), Margin = new Thickness(0, 22, 0, 0),
        };
        if (cancel)
        {
            var no = new Button { Content = L.T("Cancel"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            no.Click += (_, _) => sheets.Close(body);
            row.Children.Add(no);
            if (stack) Grid.SetRow(no, 2);
            else Grid.SetColumn(okButton, 2);
        }
        row.Children.Add(okButton);
        body.Children.Add(row);
        okButton.Click += (_, _) =>
        {
            result = boxes.Select(b => b.Text ?? "").ToArray();
            sheets.Close(body);
        };
        foreach (var b in boxes)
            b.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                e.Handled = true;
                result = boxes.Select(x => x.Text ?? "").ToArray();
                sheets.Close(body);
            };
        var shown = sheets.Show(new ScrollViewer { Content = body });
        // (not with a page to open first: the keyboard would hide the steps)
        if (boxes.Count > 0 && link == null)
            Dispatcher.UIThread.Post(() =>
            {
                boxes[0].Focus();
                boxes[0].SelectAll();
            }, DispatcherPriority.Background);
        await shown;
        return result;
    }

    // ------------------------------------------------------------------ terms of use

    // Opened from the settings, only to read them.
    public static bool Terms(bool ask)
    {
        _ = TermsAsync(false);
        return true;
    }

    public static Control TermsText()
    {
        var sub = Ui.Res("SubTextBrush");
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Ultimate MP3 Player is a tool: it only downloads what you ask it to, through third-party programs and sites, and it doesn't host or distribute any music."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 21, FontSize = 14.5,
        });
        stack.Children.Add(new TextBlock { Text = L.T("By using it you declare that:"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4), FontSize = 14.5 });
        foreach (var point in new[]
                 {
                     "you will only download songs, videos and images that you already lawfully own (for example music you bought) or that you otherwise have the right to download, such as free content or content its creators allow;",
                     // The phone has no "Listen together": what goes to others here is the .ump packs.
                     "you alone are responsible for what you download, how you use it and what you share with others (.ump packs included), in compliance with the copyright laws of your country and the terms of the sites you download from;",
                     "the author of the app doesn't control the content you download and is in no way liable for the use you make of it.",
                 })
        {
            var row = new DockPanel { Margin = new Thickness(2, 8, 0, 0) };
            var dot = new TextBlock { Text = "•", Foreground = Ui.Res("AccentTextBrush"), FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 10, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(new TextBlock { Text = L.T(point), TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 21, FontSize = 14.5 });
            stack.Children.Add(row);
        }
        return stack;
    }

    public static async Task<bool> TermsAsync(bool ask)
    {
        var sheets = Sheets;
        if (sheets == null) return false;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = L.T(ask ? "Before you start" : "Terms of use"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(TermsText());
        bool accepted = false;
        if (!ask)
        {
            var close = new Button { Content = L.T("Close"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            close.Click += (_, _) => sheets.Close(body);
            body.Children.Add(close);
            await sheets.Show(new ScrollViewer { Content = body });
            return true;
        }
        body.Children.Add(new TextBlock { Text = L.T("If you don't agree, quit: the app closes and you can uninstall it."), Classes = { "hint" }, Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(0, 14, 0, 0) });
        var check = new CheckBox { Content = new TextBlock { Text = L.T("I have read and accept these terms"), TextWrapping = TextWrapping.Wrap, FontSize = 15 }, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(check);
        var ok = new Button { Content = L.T("I accept"), Theme = Ui.Theme("TouchPrimary"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var exit = new Button { Content = L.T("Quit"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        check.IsCheckedChanged += (_, _) => ok.IsEnabled = check.IsChecked == true;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 20, 0, 0) };
        row.Children.Add(exit);
        Grid.SetColumn(ok, 2);
        row.Children.Add(ok);
        body.Children.Add(row);
        ok.Click += (_, _) => { accepted = true; sheets.Close(body); };
        exit.Click += (_, _) => sheets.Close(body);
        await sheets.Show(new ScrollViewer { Content = body }, dismissable: false, maxHeightShare: 0.95);
        return accepted;
    }

    // "What's new" at the first start after an update (Avalonia's Views/WhatsNew.cs).
    // "Don't show again" under the notes turns AppSettings.ShowWhatsNew off.
    public static async Task WhatsNewAsync(IReadOnlyList<ReleaseNote> notes, AppSettings settings)
    {
        var sheets = Sheets;
        if (sheets == null) return;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = L.T("What's new"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(Views.WhatsNew.Build(notes, true));
        var hide = new CheckBox { Content = new TextBlock { Text = L.T("Don't show again"), TextWrapping = TextWrapping.Wrap, FontSize = 15 }, IsChecked = !settings.ShowWhatsNew, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(hide);
        var close = new Button { Content = L.T("Close"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
        close.Click += (_, _) => sheets.Close(body);
        body.Children.Add(close);
        await sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.9);
        settings.ShowWhatsNew = hide.IsChecked != true;
        settings.Save();
    }

    // ------------------------------------------------------------------ files

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif" };

    private static IStorageProvider? Storage => App.Host?.Window?.StorageProvider;

    // Files Android hands over by address: copied into dir (null: the cache's incoming folder), their paths.
    private static async Task<List<string>> CopyIn(IEnumerable<IStorageItem> items, string? dir)
    {
        var paths = new List<string>();
        dir ??= Path.Combine(Platform.Files.IncomingDir, Ids.New());
        Directory.CreateDirectory(dir);
        foreach (var item in items)
        {
            if (item is not IStorageFile f) continue;
            try
            {
                var name = string.Join("_", f.Name.Split(Path.GetInvalidFileNameChars()));
                var path = Path.Combine(dir, name);
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                await using (var input = await f.OpenReadAsync())
                await using (var output = File.Create(path))
                    await input.CopyToAsync(output);
                paths.Add(path);
            }
            catch (Exception ex) { App.Log(ex); }
        }
        return paths;
    }

    // Songs picked on the phone go into the app's music folder: from then on they're the app's (deleting the song deletes them).
    private static string ImportDir => Path.Combine(App.Host.Settings.MusicDir, "Importati");

    private static bool IsAudio(string name) => Importer.Extensions.Contains(Path.GetExtension(name));

    public static async Task<string[]?> PickFilesAsync(string title, bool multiple, params (string Name, IEnumerable<string> Extensions)[] filters)
    {
        if (Storage is not { } storage) return null;
        var types = filters.Select(f => new FilePickerFileType(f.Name)
        {
            Patterns = f.Extensions.Select(e => "*" + e).ToList(),
            MimeTypes = Mimes(f.Extensions),
#if IOS_APP
            // The iPhone's picker goes by type (public.mp3...), not by extension.
            AppleUniformTypeIdentifiers = Platform.Files.TypesOf(f.Extensions),
#endif
        }).ToList();
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types });
        if (files.Count == 0) return null;
        bool songs = files.All(f => IsAudio(f.Name));
        var paths = await CopyIn(files, songs ? ImportDir : null);
        return paths.Count > 0 ? paths.ToArray() : null;
    }

    public static async Task<string?> PickFileAsync(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => (await PickFilesAsync(title, false, filters))?.FirstOrDefault();

    public static Task<string?> PickImageAsync() => PickFileAsync(L.T("Choose an image"), (L.T("Images"), ImageExtensions));

    // A folder of music: its songs (also in the folders inside it) copied into the app's music folder.
    public static async Task<string?> PickFolderAsync(string title, string? initial = null)
    {
        if (Storage is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        if (folders.FirstOrDefault() is not { } folder) return null;
        var files = new List<IStorageItem>();
        async Task Walk(IStorageFolder f, int depth)
        {
            await foreach (var item in f.GetItemsAsync())
            {
                if (item is IStorageFile file && IsAudio(file.Name)) files.Add(file);
                else if (item is IStorageFolder sub && depth < 6) await Walk(sub, depth + 1);
            }
        }
        App.Host.Session?.Toast(L.T("Copying the folder's songs…"));
        await Walk(folder, 0);
        if (files.Count == 0) return null;
        var dir = Path.Combine(ImportDir, string.Join("_", folder.Name.Split(Path.GetInvalidFileNameChars())));
        await CopyIn(files, dir);
        return dir;
    }

    // A folder to write into (the export of MP3s for a USB stick or an SD card): the system's picker, which also reaches
    // the memory cards and the sticks plugged in. Null: none chosen.
    public static async Task<IStorageFolder?> PickExportFolderAsync(string title)
    {
        if (Storage is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
        return folders.FirstOrDefault();
    }

    // ------------------------------------------------------------------ Settings › Backup (BackupsViewModel)

    // A folder chosen through the system is kept as its bookmark (Android: a permission that lasts; iPhone: a bookmark of
    // the folder), so the automatic backups reach it later.
    public const string BookmarkPrefix = "bookmark:";
#if IOS_APP
    // Documents: in the Files app (On My iPhone › Ultimate MP3 Player), and in the iPhone's own backups.
    public static string DefaultBackupFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Backups");
#else
    // Next to the music (Android/data/…/files): reachable with the cable, but removed with the app.
    public static string DefaultBackupFolder => Path.Combine(Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath ?? AppPaths.DataDir, "Backups");
#endif

    public static async Task<IBackupFolder?> OpenBackupFolderAsync(string? setting)
    {
        if (setting == null || !setting.StartsWith(BookmarkPrefix, StringComparison.Ordinal)) return new LocalBackupFolder(setting ?? DefaultBackupFolder);
        if (Storage is not { } storage) return null;
        try { return await storage.OpenFolderBookmarkAsync(setting[BookmarkPrefix.Length..]) is { } folder ? new StorageBackupFolder(folder) : null; }
        catch { return null; }
    }

    public static async Task<(string Setting, string Name)?> PickBackupFolderAsync()
    {
        if (await PickExportFolderAsync(L.T("Where to keep the backups")) is not { } folder) return null;
        try
        {
            if (await folder.SaveBookmarkAsync() is { Length: > 0 } bookmark) return (BookmarkPrefix + bookmark, folder.Name);
        }
        catch { }
        if (folder.TryGetLocalPath() is { } path) return (path, folder.Name);
        App.Host.Session?.Toast(L.T("The app can't keep access to this folder: choose another one."));
        return null;
    }

    private sealed class StorageBackupFolder : IBackupFolder
    {
        private readonly IStorageFolder _folder;

        public StorageBackupFolder(IStorageFolder folder) => _folder = folder;

        public string Name => _folder.Name;

        public async Task<Stream> CreateAsync(string fileName)
            => await (await _folder.CreateFileAsync(fileName) ?? throw new IOException(L.T("The chosen folder doesn't accept files."))).OpenWriteAsync();

        public async Task<List<string>> NamesAsync()
        {
            var names = new List<string>();
            await foreach (var item in _folder.GetItemsAsync())
                if (item is IStorageFile) names.Add(item.Name);
            return names;
        }

        public async Task DeleteAsync(string fileName)
        {
            await foreach (var item in _folder.GetItemsAsync())
                if (item is IStorageFile && item.Name == fileName)
                {
                    await item.DeleteAsync();
                    return;
                }
        }
    }

    private static List<string>? Mimes(IEnumerable<string> extensions)
    {
        var list = new HashSet<string>();
        foreach (var e in extensions)
        {
#if ANDROID_APP
            var mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(e.TrimStart('.'));
#else
            string? mime = null;
#endif
            if (mime != null) list.Add(mime);
            else if (e == Pack.Extension) list.Add("application/octet-stream");
        }
        if (extensions.Contains(Pack.Extension)) { list.Add("application/zip"); list.Add("application/octet-stream"); list.Add("*/*"); }
        return list.Count > 0 ? list.ToList() : null;
    }

    // A file to save (the export of a pack): written to the cache, then handed to the system's share sheet or saved by
    // the picker (here: Downloads, Drive...). Null: nowhere.
    public static async Task<bool> SaveAsAsync(string path, string fileName, string mime)
    {
        if (Storage is not { } storage) return false;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = fileName, SuggestedFileName = fileName, DefaultExtension = Path.GetExtension(fileName).TrimStart('.'),
            FileTypeChoices = new[] { new FilePickerFileType(fileName) { Patterns = new[] { "*" + Path.GetExtension(fileName) }, MimeTypes = new[] { mime } } },
        });
        if (file == null) return false;
        await using (var input = File.OpenRead(path))
        await using (var output = await file.OpenWriteAsync())
            await input.CopyToAsync(output);
        return true;
    }

    // ------------------------------------------------------------------ what only the computer apps ask in one line (the DJ,
    // Listen together): never used on the phone, here so the shared view models compile.

    public static bool Confirm(string title, string message, string ok, bool danger = false)
    {
        _ = ConfirmAsync(title, message, ok, danger);
        return false;
    }

    public static string? Prompt(string title, string label, string initial) => null;
    public static string? PickFile(string title, params (string Name, IEnumerable<string> Extensions)[] filters) => null;
}
