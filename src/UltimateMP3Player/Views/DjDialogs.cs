#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
#else
using System.Windows;
using System.Windows.Controls;
#endif
using System.IO;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Linked into the Avalonia app.
public static class DjDialogs
{
    // A recording just stopped: title, artist, cover and playlist of the mix (null = discarded).
    public static MixSave? SaveRecording(DjViewModel dj, IReadOnlyList<DjDeckViewModel> decks)
    {
        var body = new StackPanel();
        TextBox Field(string label, string value)
        {
            body.Children.Add(Both.Text(label, "FieldLabel", new Thickness(2, 8, 0, 6)));
            var box = Both.Box(value);
            body.Children.Add(box);
            return box;
        }
        var title = Field(L.T("Title"), string.Join(" x ", decks.Select(d => d.Title)) + " (mix)");
        var artist = Field(L.T("Artist"), string.Join(", ", decks.Select(d => d.Artist).Where(a => a.Length > 0).Distinct()));
        body.Children.Add(Both.Text(L.T("Cover"), "FieldLabel", new Thickness(2, 8, 0, 6)));
        string? cover = null;
        var coverButton = Both.Look(new Button { Content = L.T("Choose an image…"), HorizontalAlignment = HorizontalAlignment.Left }, "GhostButton");
        coverButton.Click += (_, _) =>
        {
            if (Dialogs.PickFile(L.T("Choose an image"), (L.T("Images"), new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp" })) is not { } file) return;
            cover = file;
            coverButton.Content = Path.GetFileName(cover);
        };
        body.Children.Add(coverButton);
        body.Children.Add(Both.Text(L.T("Save to playlist"), "FieldLabel", new Thickness(2, 12, 0, 6)));
        const string NewOne = "\0new";
        var choices = new List<Choice> { new(L.T("None (only in “All songs”)"), null), new(L.T("New playlist…"), NewOne) };
        choices.AddRange(dj.Main.Playlists.Select(p => new Choice(p.Name, p.P)));
        var playlist = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(playlist);
        // Name of the new playlist, shown when "New playlist…" is picked.
        var newName = Both.Box(dj.Main.NewPlaylistName());
        newName.Margin = new Thickness(0, 8, 0, 0);
        Both.Show(newName, false);
        body.Children.Add(newName);
        bool IsNew() => Equals((playlist.SelectedItem as Choice)?.Value, NewOne);
        playlist.SelectionChanged += (_, _) =>
        {
            Both.Show(newName, IsNew());
            if (IsNew()) { newName.Focus(); newName.SelectAll(); }
        };

        var ok = Dialogs.Button(L.T("Save"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Save the recording"), body, 420, Dialogs.Button(L.T("Discard"), "GhostButton", isCancel: true), ok);
        string T(TextBox b) => (b.Text ?? "").Trim();
        ok.Click += (_, _) => { if (T(title).Length > 0 && (!IsNew() || T(newName).Length > 0)) win.DialogResult = true; };
        if (win.ShowDialog() != true) return null;
        return new MixSave(T(title), T(artist), cover, IsNew() ? null : (playlist.SelectedItem as Choice)?.Value as Playlist,
            IsNew() ? T(newName) : null);
    }
}
