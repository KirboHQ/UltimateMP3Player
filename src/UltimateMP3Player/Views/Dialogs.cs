using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// Small dark dialogs: confirmations and text fields.
public static class Dialogs
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

    public static string? Prompt(string title, string label, string initial)
        => Show(title, null, new[] { (label, initial) }, L.T("Salva"), false)?[0];

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
            Text = L.T("Ultimate MP3 Player è uno strumento: scarica soltanto quello che gli chiedi tu, attraverso programmi e siti di terze parti, e non ospita né distribuisce musica."),
            TextWrapping = TextWrapping.Wrap, Foreground = sub, LineHeight = 20,
        });
        stack.Children.Add(new TextBlock { Text = L.T("Usandolo dichiari che:"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });
        foreach (var point in new[]
                 {
                     "scaricherai solo brani, video e immagini che possiedi già legittimamente (per esempio musica che hai acquistato) o che hai comunque il diritto di scaricare, come contenuti liberi o con il permesso di chi li ha creati;",
                     "sei l'unico responsabile di cosa scarichi, di come lo usi e di cosa condividi con gli altri (anche con Ascolta insieme), nel rispetto delle leggi sul diritto d'autore del tuo paese e delle condizioni dei siti da cui scarichi;",
                     "l'autore dell'app non controlla i contenuti che scarichi e non risponde in alcun modo dell'uso che ne fai.",
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
            var close = Button(L.T("Chiudi"), "PrimaryButton", isDefault: true, isCancel: true);
            var info = Frame(L.T("Termini d'uso"), stack, 480, close);
            close.Click += (_, _) => info.DialogResult = true;
            info.ShowDialog();
            return true;
        }
        stack.Children.Add(new TextBlock
        {
            Text = L.T("Se non sei d'accordo, esci: l'app si chiude e puoi disinstallarla."), TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)res["MutedBrush"], FontSize = 12.5, Margin = new Thickness(0, 14, 0, 0),
        });
        var ok = Button(L.T("Accetto"), "PrimaryButton", isDefault: true);
        ok.IsEnabled = false;
        var check = new CheckBox { Content = L.T("Ho letto e accetto queste condizioni"), Margin = new Thickness(0, 16, 0, 0) };
        check.Checked += (_, _) => ok.IsEnabled = true;
        check.Unchecked += (_, _) => ok.IsEnabled = false;
        stack.Children.Add(check);
        var win = Frame(L.T("Prima di iniziare"), stack, 480, Button(L.T("Esci"), "GhostButton", isCancel: true), ok);
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
                .Append(L.T("Tutti i file") + "|*.*")),
        };
        return dlg.ShowDialog() == true ? dlg.FileNames : null;
    }

    public static string? PickFile(string title, params (string Name, IEnumerable<string> Extensions)[] filters)
        => PickFiles(title, false, filters)?.FirstOrDefault();

    public static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".jfif", ".avif" };

    public static string? PickImage() => PickFile(L.T("Scegli un'immagine"), (L.T("Immagini"), ImageExtensions));

    public static string? PickFolder(string title, string? initial = null)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initial != null) dlg.InitialDirectory = initial;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
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
        var res = Application.Current.Resources;
        var stack = new StackPanel();
        if (message != null)
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["SubTextBrush"], LineHeight = 20 });

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 10, 0, 6) });
            var box = new TextBox { Text = value, Style = (Style)res["BoxTextBox"] };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        string[]? result = null;
        var okButton = Button(ok, danger ? "DangerButton" : "PrimaryButton", isDefault: true);
        var win = Frame(title, stack, 400, Button(L.T("Annulla"), "GhostButton", isCancel: true), okButton);
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
