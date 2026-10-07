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
public static class Dialogs
{
    // Bumped when the terms of use change: they're asked again.
    public const int TermsVersion = 1;

    private static SheetHost? Sheets => App.Host?.View?.Sheets;

    public static async Task<bool> ConfirmAsync(string title, string message, string ok, bool danger = false)
        => await ShowAsync(title, message, Array.Empty<(string, string)>(), ok, danger) != null;

    public static async Task<string?> PromptAsync(string title, string label, string initial)
        => (await ShowAsync(title, null, new[] { (label, initial) }, L.T("Salva"), false))?[0];

    public static async Task<(string Title, string Artist, string Album, string Bpm)?> EditTrackAsync(Track t)
    {
        var bpm = t.Bpm?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "";
        var r = await ShowAsync(L.T("Modifica informazioni"), null,
            new[] { (L.T("Titolo"), t.Title), (L.T("Artista"), t.Artist ?? ""), (L.T("Album"), t.Album ?? ""), ("BPM", bpm) }, L.T("Salva"), false);
        return r == null ? null : (r[0], r[1], r[2], r[3]);
    }

    // A message with only "OK".
    public static void Alert(string title, string message) => _ = ShowAsync(title, message, Array.Empty<(string, string)>(), "OK", false, cancel: false);

    // Title, text, fields; the OK button's answer (the fields' texts), or null.
    private static async Task<string[]?> ShowAsync(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger, bool cancel = true)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        var body = new StackPanel { Margin = new Thickness(24, 4, 24, 18) };
        body.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        if (message != null) body.Children.Add(new TextBlock { Text = message, Classes = { "hint" }, FontSize = 14.5, LineHeight = 21 });
        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            body.Children.Add(new TextBlock { Text = label, Classes = { "field-label" }, Margin = new Thickness(4, 12, 0, 8) });
            var box = new TextBox { Text = value, Theme = Ui.Theme("TouchBox") };
            boxes.Add(box);
            body.Children.Add(box);
        }
        string[]? result = null;
        var okButton = new Button { Content = ok, Theme = Ui.Theme(danger ? "TouchDanger" : "TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var row = new Grid { ColumnDefinitions = cancel ? new ColumnDefinitions("*,12,*") : new ColumnDefinitions("*"), Margin = new Thickness(0, 22, 0, 0) };
        if (cancel)
        {
            var no = new Button { Content = L.T("Annulla"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            no.Click += (_, _) => sheets.Close(body);
            row.Children.Add(no);
            Grid.SetColumn(okButton, 2);
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
        if (boxes.Count > 0)
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
            Text = L.T("Ultimate MP3 Player è uno strumento: scarica soltanto quello che gli chiedi tu, attraverso programmi e siti di terze parti, e non ospita né distribuisce musica."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 21, FontSize = 14.5,
        });
        stack.Children.Add(new TextBlock { Text = L.T("Usandolo dichiari che:"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4), FontSize = 14.5 });
        foreach (var point in new[]
                 {
                     "scaricherai solo brani, video e immagini che possiedi già legittimamente (per esempio musica che hai acquistato) o che hai comunque il diritto di scaricare, come contenuti liberi o con il permesso di chi li ha creati;",
                     // The phone has no "Listen together": what goes to others here is the .ump packs.
                     "sei l'unico responsabile di cosa scarichi, di come lo usi e di cosa condividi con gli altri (anche con i pacchetti .ump), nel rispetto delle leggi sul diritto d'autore del tuo paese e delle condizioni dei siti da cui scarichi;",
                     "l'autore dell'app non controlla i contenuti che scarichi e non risponde in alcun modo dell'uso che ne fai.",
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
        body.Children.Add(new TextBlock { Text = L.T(ask ? "Prima di iniziare" : "Termini d'uso"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 12) });
        body.Children.Add(TermsText());
        bool accepted = false;
        if (!ask)
        {
            var close = new Button { Content = L.T("Chiudi"), Theme = Ui.Theme("TouchPrimary"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            close.Click += (_, _) => sheets.Close(body);
            body.Children.Add(close);
            await sheets.Show(new ScrollViewer { Content = body });
            return true;
        }
        body.Children.Add(new TextBlock { Text = L.T("Se non sei d'accordo, esci: l'app si chiude e puoi disinstallarla."), Classes = { "hint" }, Foreground = Ui.Res("MutedBrush"), Margin = new Thickness(0, 14, 0, 0) });
        var check = new CheckBox { Content = new TextBlock { Text = L.T("Ho letto e accetto queste condizioni"), TextWrapping = TextWrapping.Wrap, FontSize = 15 }, Margin = new Thickness(0, 16, 0, 0) };
        body.Children.Add(check);
        var ok = new Button { Content = L.T("Accetto"), Theme = Ui.Theme("TouchPrimary"), IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var exit = new Button { Content = L.T("Esci"), Theme = Ui.Theme("TouchGhost"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
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
        var types = filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*" + e).ToList(), MimeTypes = Mimes(f.Extensions) }).ToList();
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types });
        if (files.Count == 0) return null;
        bool songs = files.All(f => IsAudio(f.Name));
        var paths = await CopyIn(files, songs ? ImportDir : null);
        return paths.Count > 0 ? paths.ToArray() : null;
    }

    public static async Task<string?> PickFileAsync(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => (await PickFilesAsync(title, false, filters))?.FirstOrDefault();

    public static Task<string?> PickImageAsync() => PickFileAsync(L.T("Scegli un'immagine"), (L.T("Immagini"), ImageExtensions));

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
        App.Host.Session?.Toast(L.T("Copia dei brani della cartella…"));
        await Walk(folder, 0);
        if (files.Count == 0) return null;
        var dir = Path.Combine(ImportDir, string.Join("_", folder.Name.Split(Path.GetInvalidFileNameChars())));
        await CopyIn(files, dir);
        return dir;
    }

    private static List<string>? Mimes(IEnumerable<string> extensions)
    {
        var list = new HashSet<string>();
        foreach (var e in extensions)
        {
            var mime = Android.Webkit.MimeTypeMap.Singleton?.GetMimeTypeFromExtension(e.TrimStart('.'));
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
