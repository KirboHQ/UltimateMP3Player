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

    public static string? Prompt(string title, string label, string initial)
        => Show(title, null, new[] { (label, initial) }, L.T("Salva"), false)?[0];

    private static IBrush Res(string key) => Application.Current!.FindResource(key) as IBrush ?? Brushes.Gray;
    private static T Res<T>(string key) where T : class => (Application.Current!.FindResource(key) as T)!;

    // Bumped when the terms of use change: they're asked again.
    public const int TermsVersion = 1;

    // The terms of use. ask: the first start, they must be accepted (false = the app closes); otherwise only to read them.
    public static bool Terms(bool ask)
    {
        var sub = Res("SubTextBrush");
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Ultimate MP3 Player è uno strumento: scarica soltanto quello che gli chiedi tu, attraverso programmi e siti di terze parti, e non ospita né distribuisce musica."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 20,
        });
        stack.Children.Add(new TextBlock { Text = L.T("Usandolo dichiari che:"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        foreach (var point in new[]
                 {
                     "scaricherai solo brani, video e immagini che possiedi già legittimamente (per esempio musica che hai acquistato) o che hai comunque il diritto di scaricare, come contenuti liberi o con il permesso di chi li ha creati;",
                     "sei l'unico responsabile di cosa scarichi, di come lo usi e di cosa condividi con gli altri (anche con Ascolta insieme), nel rispetto delle leggi sul diritto d'autore del tuo paese e delle condizioni dei siti da cui scarichi;",
                     "l'autore dell'app non controlla i contenuti che scarichi e non risponde in alcun modo dell'uso che ne fai.",
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
            var close = Button(L.T("Chiudi"), "PrimaryButton", isDefault: true, isCancel: true);
            var info = Frame(L.T("Termini d'uso"), stack, 480, close);
            close.Click += (_, _) => info.DialogResult = true;
            info.ShowDialog();
            return true;
        }
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Se non sei d'accordo, esci: l'app si chiude e puoi disinstallarla."), TextWrapping = TextWrapping.Wrap,
            Foreground = Res("MutedBrush"), FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0),
        });
        var ok = Button(L.T("Accetto"), "PrimaryButton", isDefault: true);
        ok.IsEnabled = false;
        var check = new CheckBox { Content = L.T("Ho letto e accetto queste condizioni"), Margin = new Thickness(0, 16, 0, 0) };
        check.IsCheckedChanged += (_, _) => ok.IsEnabled = check.IsChecked == true;
        stack.Children.Add(check);
        var win = Frame(L.T("Prima di iniziare"), stack, 480, Button(L.T("Esci"), "GhostButton", isCancel: true), ok);
        ok.Click += (_, _) => win.DialogResult = true;
        win.Opened += (_, _) => check.Focus();
        return win.ShowDialog() == true;
    }

    public static (string Title, string Artist, string Album, string Bpm)? EditTrack(Track t)
    {
        var bpm = t.Bpm?.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) ?? "";
        var r = Show(L.T("Modifica informazioni"), null,
            new[] { (L.T("Titolo"), t.Title), (L.T("Artista"), t.Artist ?? ""), (L.T("Album"), t.Album ?? ""), ("BPM", bpm) }, L.T("Salva"), false);
        return r == null ? null : (r[0], r[1], r[2], r[3]);
    }

    // The same dialogs for the view models shared with the Android app, which wait for them with await (there a dialog
    // can't hold the code up). Here they're the ones above.
    public static Task<bool> ConfirmAsync(string title, string message, string ok, bool danger = false) => Task.FromResult(Confirm(title, message, ok, danger));
    public static Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult(Prompt(title, label, initial));
    public static Task<(string Title, string Artist, string Album, string Bpm)?> EditTrackAsync(Track t) => Task.FromResult(EditTrack(t));
    public static Task<string?> PickImageAsync() => Task.FromResult(PickImage());
    public static Task<string?> PickFolderAsync(string title, string? initial = null) => Task.FromResult(PickFolder(title, initial));
    public static Task<string[]?> PickFilesAsync(string title, bool multiple, params (string Name, IEnumerable<string> Extensions)[] filters)
        => Task.FromResult(PickFiles(title, multiple, filters));
    public static Task<string?> PickFileAsync(string title, params (string Name, IEnumerable<string> Extensions)[] filters) => Task.FromResult(PickFile(title, filters));

    private static string[]? Show(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger)
    {
        var stack = new StackPanel();
        if (message != null)
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Res("SubTextBrush"), LineHeight = 20 });

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            var l = new TextBlock { Text = label, Margin = new Thickness(2, 10, 0, 6) };
            l.Classes.Add("field-label");
            stack.Children.Add(l);
            var box = new TextBox { Text = value, Theme = Res<Avalonia.Styling.ControlTheme>("BoxTextBox") };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        string[]? result = null;
        var okButton = Button(ok, danger ? "DangerButton" : "PrimaryButton", isDefault: true);
        var win = Frame(title, stack, 400, Button(L.T("Annulla"), "GhostButton", isCancel: true), okButton);
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
        types.Add(new FilePickerFileType(L.T("Tutti i file")) { Patterns = new[] { "*" } });
        var files = Wait(() => storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = multiple, FileTypeFilter = types }));
        var paths = files?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        return paths is { Length: > 0 } ? paths : null;
    }

    public static string? PickFile(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => PickFiles(title, false, filters)?.FirstOrDefault();

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif" };

    public static string? PickImage() => PickFile(L.T("Scegli un'immagine"), (L.T("Immagini"), ImageExtensions));

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
