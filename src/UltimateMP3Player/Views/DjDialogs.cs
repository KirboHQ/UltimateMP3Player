using System.IO;
using System.Windows;
using System.Windows.Controls;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public static class DjDialogs
{
    // A recording just stopped: title, artist, cover and playlist of the mix (null = discarded).
    public static MixSave? SaveRecording(DjViewModel dj, IReadOnlyList<DjDeckViewModel> decks)
    {
        var res = Application.Current.Resources;
        var body = new StackPanel();
        TextBox Field(string label, string value)
        {
            body.Children.Add(new TextBlock { Text = label, Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 8, 0, 6) });
            var box = new TextBox { Text = value, Style = (Style)res["BoxTextBox"] };
            body.Children.Add(box);
            return box;
        }
        var title = Field(L.T("Titolo"), string.Join(" x ", decks.Select(d => d.Title)) + " (mix)");
        var artist = Field(L.T("Artista"), string.Join(", ", decks.Select(d => d.Artist).Where(a => a.Length > 0).Distinct()));
        body.Children.Add(new TextBlock { Text = L.T("Copertina"), Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 8, 0, 6) });
        string? cover = null;
        var coverButton = new Button { Content = L.T("Scegli un'immagine…"), Style = (Style)res["GhostButton"], HorizontalAlignment = HorizontalAlignment.Left };
        coverButton.Click += (_, _) =>
        {
            if (Dialogs.PickFile(L.T("Scegli un'immagine"), (L.T("Immagini"), new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })) is not { } file) return;
            cover = file;
            coverButton.Content = Path.GetFileName(cover);
        };
        body.Children.Add(coverButton);
        body.Children.Add(new TextBlock { Text = L.T("Salva nella playlist"), Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 12, 0, 6) });
        const string NewOne = "\0new";
        var choices = new List<Choice> { new(L.T("Nessuna (solo in «Tutti i brani»)"), null), new(L.T("Nuova playlist…"), NewOne) };
        choices.AddRange(dj.Main.Playlists.Select(p => new Choice(p.Name, p.P)));
        var playlist = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
        body.Children.Add(playlist);
        // Name of the new playlist, shown when "New playlist…" is picked.
        var newName = new TextBox { Text = dj.Main.NewPlaylistName(), Style = (Style)res["BoxTextBox"], Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        body.Children.Add(newName);
        bool IsNew() => Equals((playlist.SelectedItem as Choice)?.Value, NewOne);
        playlist.SelectionChanged += (_, _) =>
        {
            newName.Visibility = IsNew() ? Visibility.Visible : Visibility.Collapsed;
            if (IsNew()) { newName.Focus(); newName.SelectAll(); }
        };

        var ok = Dialogs.Button(L.T("Salva"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Salva la registrazione"), body, 420, Dialogs.Button(L.T("Scarta"), "GhostButton", isCancel: true), ok);
        ok.Click += (_, _) => { if (title.Text.Trim().Length > 0 && (!IsNew() || newName.Text.Trim().Length > 0)) win.DialogResult = true; };
        if (win.ShowDialog() != true) return null;
        return new MixSave(title.Text.Trim(), artist.Text.Trim(), cover, IsNew() ? null : (playlist.SelectedItem as Choice)?.Value as Playlist,
            IsNew() ? newName.Text.Trim() : null);
    }
}
