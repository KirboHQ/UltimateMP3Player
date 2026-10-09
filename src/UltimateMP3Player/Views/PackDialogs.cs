using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// .ump packs: export playlists, tags or the whole library into one file, and import one (what's new, what's already here).
public static class PackDialogs
{
    private static ResourceDictionary Res => Application.Current.Resources;
    private static Brush B(string key) => (Brush)Res[key];

    private const string MusicGlyph = "";
    private const string LinkGlyph = "";

    // ================================================================== export

    // A playlist or a tag to choose, as the dialog shows it.
    public sealed record ExportList(Playlist P, string Name, string CountText, Func<FrameworkElement> Cover);
    public sealed record ExportTag(Tag T, Brush Brush, int Count);

    // many: several playlists chosen together (selected on Home).
    public static void Export(MainViewModel main, PlaylistViewModel? playlist = null, TagViewModel? tag = null, IReadOnlyCollection<PlaylistViewModel>? many = null)
    {
        var lists = main.Playlists.Select(p => new ExportList(p.P, p.Name, p.CountText,
            () => new ContentPresenter { Content = p, ContentTemplate = (DataTemplate)Res["PlaylistCover"] })).ToList();
        var tags = main.Tags.Select(t => new ExportTag(t.T, t.Brush, t.Count)).ToList();
        var chosen = (many ?? Array.Empty<PlaylistViewModel>()).Select(p => p.P).ToList();
        if (playlist != null) chosen.Add(playlist.P);
        BuildExport(main.Library, main.Profile, lists, tags, chosen, tag?.T).ShowDialog();
    }

    public static Window BuildExport(Library lib, Profile profile, List<ExportList> lists, List<ExportTag> tags, IReadOnlyCollection<Playlist> chosen, Tag? tag)
    {
        var pickedLists = lists.Where(l => chosen.Contains(l.P)).ToHashSet();
        var pickedTags = tags.Where(t => t.T == tag).ToHashSet();
        bool whole = false;
        // Every choice refreshes the summary (set once all the parts exist).
        Action changed = () => { };

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long SizeOf(string? path)
        {
            if (path == null) return 0;
            if (!sizes.TryGetValue(path, out var s)) sizes[path] = s = PackWriter.SizeOf(path);
            return s;
        }

        var body = new StackPanel();
        var inputs = new StackPanel();
        body.Children.Add(inputs);
        inputs.Children.Add(Hint(L.T("Scegli cosa mettere nel pacchetto: un file .ump da tenere come copia o da mandare a un amico. Si apre con un doppio clic (o trascinandolo nella finestra) e l'app aggiunge tutto, senza doppioni.")));

        // ---- playlists
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        inputs.Children.Add(Header(L.T("Playlist"), lists.Count > 1 ? allLists : null, 16));
        var listPanel = new StackPanel();
        foreach (var p in lists)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = p.Cover();
            cover.Width = cover.Height = 34;
            row.Children.Add(cover);
            row.Children.Add(At(new TextBlock { Text = p.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) }, 1));
            row.Children.Add(At(new TextBlock { Text = p.CountText, FontSize = 12, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center }, 2));
            var cb = new CheckBox { IsChecked = pickedLists.Contains(p), Content = row };
            cb.Checked += (_, _) => { pickedLists.Add(p); changed(); };
            cb.Unchecked += (_, _) => { pickedLists.Remove(p); changed(); };
            listBoxes.Add(cb);
            listPanel.Children.Add(HoverRow(cb));
        }
        inputs.Children.Add(Box(listPanel, 200));
        allLists.Click += (_, _) =>
        {
            bool all = listBoxes.All(b => b.IsChecked == true);
            foreach (var b in listBoxes) b.IsChecked = !all;
        };

        // ---- tags: each brings all its songs
        var tagChips = new List<(ExportTag T, Border Chip)>();
        var allTags = Link();
        if (tags.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Tag"), tags.Count > 1 ? allTags : null));
            var wrap = new WrapPanel();
            foreach (var t in tags)
            {
                var chip = Chip(t.T.Name, t.Brush, t.Count.ToString(), pickedTags.Contains(t));
                chip.ToolTip = L.F("Il tag e i suoi {0}", L.Count(t.Count, "1 brano", "{0} brani"));
                chip.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if (!pickedTags.Remove(t)) pickedTags.Add(t);
                    SetChip(chip, t.Brush, pickedTags.Contains(t));
                    changed();
                };
                tagChips.Add((t, chip));
                wrap.Children.Add(chip);
            }
            inputs.Children.Add(wrap);
            allTags.Click += (_, _) =>
            {
                bool all = pickedTags.Count == tags.Count;
                pickedTags.Clear();
                if (!all) pickedTags.UnionWith(tags);
                foreach (var (t, chip) in tagChips) SetChip(chip, t.Brush, pickedTags.Contains(t));
                changed();
            };
        }

        // ---- the whole library
        inputs.Children.Add(Label(L.T("Altro"), tags.Count > 0 ? 10 : 18));
        var wholeBox = new CheckBox
        {
            Content = TwoLines(L.T("Tutta la libreria"),
                L.F("Anche i brani che non sono in nessuna playlist o tag ({0} in tutto): comodo per spostare tutto su un altro computer.", lib.Count)),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        wholeBox.Checked += (_, _) => { whole = true; changed(); };
        wholeBox.Unchecked += (_, _) => { whole = false; changed(); };
        inputs.Children.Add(wholeBox);

        // ---- with the music or links only
        inputs.Children.Add(Label(L.T("Brani")));
        var (modeBar, withFiles, linksOnly) = Segmented(L.T("Con i file audio"), MusicGlyph, L.T("Solo i link"), LinkGlyph);
        withFiles.IsChecked = true;
        inputs.Children.Add(modeBar);
        var modeHint = Hint("", 8);
        inputs.Children.Add(modeHint);
        var videoBox = new CheckBox { Content = L.T("Includi anche i video dei brani che li hanno"), Margin = new Thickness(0, 12, 0, 0) };
        inputs.Children.Add(videoBox);
        withFiles.Checked += (_, _) => changed();
        linksOnly.Checked += (_, _) => changed();
        videoBox.Checked += (_, _) => changed();
        videoBox.Unchecked += (_, _) => changed();

        // ---- what it makes
        var (summary, line1, line2) = Summary();
        body.Children.Add(summary);
        var (progress, bar, status) = Progress();
        body.Children.Add(progress);

        var cancel = Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true);
        var ok = Dialogs.Button(L.T("Esporta…"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Esporta in un pacchetto"), Fit(body), 560, cancel, ok);

        PackExport Current() => new()
        {
            Playlists = lists.Where(pickedLists.Contains).Select(p => p.P).ToList(),
            Tags = tags.Where(pickedTags.Contains).Select(t => t.T).ToList(),
            WholeLibrary = whole,
            Music = withFiles.IsChecked == true,
            Videos = withFiles.IsChecked == true && videoBox.IsChecked == true,
        };

        void Update()
        {
            var e = Current();
            var songs = PackWriter.Songs(lib, profile, e);
            allLists.Content = listBoxes.All(b => b.IsChecked == true) ? L.T("Deseleziona tutte") : L.T("Seleziona tutte");
            allTags.Content = pickedTags.Count == tags.Count ? L.T("Deseleziona tutti") : L.T("Seleziona tutti");
            modeHint.Text = e.Music
                ? L.T("La musica è dentro il pacchetto: si importa anche senza internet e con i brani spariti dai siti. Pesa quanto i brani.")
                : L.T("Pacchetto leggerissimo, perfetto da mandare su Discord: chi lo apre riscarica i brani dai loro link. Quelli senza link (file tuoi, mix del DJ) viaggiano comunque come file.");
            videoBox.Visibility = e.Music && songs.Any(t => t.HasVideo) ? Visibility.Visible : Visibility.Collapsed;
            ok.IsEnabled = !e.IsEmpty;
            if (e.IsEmpty)
            {
                line1.Text = L.T("Scegli almeno una playlist, un tag o tutta la libreria.");
                line2.Text = "";
                return;
            }
            var parts = new List<string> { L.Count(songs.Count, "1 brano", "{0} brani") };
            if (e.Playlists.Count > 0) parts.Add(L.Count(e.Playlists.Count, "1 playlist", "{0} playlist"));
            if (e.Tags.Count > 0) parts.Add(L.Count(e.Tags.Count, "1 tag", "{0} tag"));
            line1.Text = string.Join(" · ", parts);
            var size = PackWriter.EstimateSize(songs, e, SizeOf);
            var more = new List<string> { L.F("circa {0}", Text.Size(Math.Max(size, 1024))) };
            int files = songs.Count(t => !Pack.HasLink(t));
            if (!e.Music && files > 0) more.Add(L.Count(files, "1 brano senza link viaggia come file", "{0} brani senza link viaggiano come file"));
            line2.Text = string.Join(" · ", more);
        }
        changed = Update;

        // 0 = choosing, 1 = writing, 2 = done.
        int stage = 0;
        string? saved = null;
        CancellationTokenSource? cts = null;
        ok.Click += async (_, _) =>
        {
            if (stage == 2)
            {
                ShowInFolder(saved!);
                win.DialogResult = true;
                return;
            }
            if (stage != 0) return;
            var e = Current();
            if (e.IsEmpty) return;
            var dlg = new SaveFileDialog
            {
                Title = L.T("Salva il pacchetto"),
                FileName = DefaultName(profile, e),
                DefaultExt = Pack.Extension,
                AddExtension = true,
                OverwritePrompt = true,
                Filter = L.T("Pacchetto di Ultimate MP3 Player") + " (*" + Pack.Extension + ")|*" + Pack.Extension,
            };
            if (dlg.ShowDialog(win) != true) return;
            var path = Pack.IsPack(dlg.FileName) ? dlg.FileName : dlg.FileName + Pack.Extension;

            stage = 1;
            inputs.IsEnabled = false;
            ok.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparo il pacchetto…");
            cts = new CancellationTokenSource();
            var report = Reporter(win, bar, status);
            try
            {
                var m = await Task.Run(() => PackWriter.WriteAsync(path, lib, profile, e, AppInfo.VersionText, report, cts.Token));
                stage = 2;
                saved = path;
                bar.Value = 1;
                status.Foreground = B("SuccessBrush");
                status.Text = "✓ " + L.F("Pacchetto creato: {0} ({1})", System.IO.Path.GetFileName(path), Text.Size(PackWriter.SizeOf(path)));
                line1.Text = L.Count(m.Tracks.Count, "1 brano", "{0} brani") + " · " + L.Count(m.Playlists.Count, "1 playlist", "{0} playlist") +
                             " · " + L.Count(m.Tags.Count, "1 tag", "{0} tag");
                ok.Content = L.T("Mostra nella cartella");
                ok.IsEnabled = true;
                cancel.Content = L.T("Chiudi");
            }
            catch (OperationCanceledException)
            {
                stage = 0;
                win.DialogResult = false;
            }
            catch (Exception ex)
            {
                stage = 0;
                inputs.IsEnabled = true;
                ok.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Esportazione non riuscita:") + " " + ex.Message;
            }
        };
        // Closing while it writes: it stops (the half-written file is deleted), then the dialog goes.
        win.Closing += (_, e) =>
        {
            if (stage != 1) return;
            e.Cancel = true;
            cts?.Cancel();
            status.Text = L.T("Annullo…");
        };
        changed();
        return win;
    }

    private static string DefaultName(Profile profile, PackExport e)
    {
        string name;
        if (e.WholeLibrary) name = L.F("Libreria di {0}", profile.Info.Name) + " " + DateTime.Now.ToString("yyyy-MM-dd");
        else if (e.Playlists.Count == 1 && e.Tags.Count == 0) name = PlaylistViewModel.DisplayName(e.Playlists[0]);
        else if (e.Tags.Count == 1 && e.Playlists.Count == 0) name = e.Tags[0].Name;
        else name = L.F("Musica di {0}", profile.Info.Name) + " " + DateTime.Now.ToString("yyyy-MM-dd");
        return Text.SafeFileName(name) + Pack.Extension;
    }

    private static void ShowInFolder(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    // ================================================================== import

    public static async Task Import(MainViewModel main, string path)
    {
        PackFile pack;
        try
        {
            pack = await Task.Run(() => PackFile.Open(path));
        }
        catch (PackException ex)
        {
            Dialogs.Alert(L.T("Impossibile aprire il pacchetto"), ex.Message);
            return;
        }
        PackImportResult? result = null;
        using (pack)
        {
            var m = pack.Manifest;
            if (m.Tracks.Count == 0 && m.Playlists.Count == 0 && m.Tags.Count == 0)
            {
                Dialogs.Alert(L.T("Il pacchetto è vuoto"), L.F("«{0}» non contiene brani, playlist o tag.", pack.Name));
                return;
            }
            var songs = await Task.Run(() => PackImporter.Analyze(pack, main.Library));
            BuildImport(pack, songs, main.Library, main.Profile, main.Host.Settings.MusicDir, main.Host.EnsureEnginesAsync, r => result = r, main.Host.Settings).ShowDialog();
        }
        if (result == null) return;

        main.QueuePackDownloads(result);
        var parts = new List<string>();
        if (result.Added > 0) parts.Add(L.Count(result.Added, "1 brano aggiunto", "{0} brani aggiunti"));
        if (result.Cloud > 0) parts.Add(L.F("{0} nel cloud", result.Cloud));
        if (result.Present > 0) parts.Add(L.F("{0} già nella libreria", result.Present));
        if (result.Downloads.Count > 0) parts.Add(L.F("{0} in download", result.Downloads.Count));
        if (result.Missing + result.Failed > 0) parts.Add(L.Count(result.Missing + result.Failed, "1 non disponibile", "{0} non disponibili"));
        if (result.NewPlaylists > 0) parts.Add(L.Count(result.NewPlaylists, "1 playlist nuova", "{0} playlist nuove"));
        if (result.NewTags > 0) parts.Add(L.Count(result.NewTags, "1 tag nuovo", "{0} tag nuovi"));
        main.Toast(L.T("Pacchetto importato") + (parts.Count > 0 ? ": " + string.Join(" · ", parts) : ""));

        // One playlist (or only one tag): straight to it, once the lists have caught up.
        var ids = result.PlaylistIds.Distinct().ToList();
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        if (ids.Count == 1 && main.Playlists.FirstOrDefault(p => p.Id == ids[0]) is { } pl) main.OpenPlaylist(pl);
    }

    // done: what was imported (the dialog closes by itself right after).
    public static Window BuildImport(PackFile pack, List<PackSong> songs, Library lib, Profile profile, string musicDir, Func<Task> ensureEngines,
        Action<PackImportResult> done, AppSettings? settings = null)
    {
        var m = pack.Manifest;
        var rows = songs.Select(s => new PackSongRow(s, pack)).ToList();
        var pickedLists = m.Playlists.Select(p => p.Id).ToHashSet();
        var merge = m.Playlists.ToDictionary(p => p.Id, _ => true);
        var pickedTags = m.Tags.Select(t => t.Id).ToHashSet();
        var loose = PackImporter.Loose(m);
        bool others = loose.Count > 0;
        // The songs that travelled as links: downloaded, or into the library in the cloud (the last choice, like the links).
        bool cloud = settings != null && !settings.SaveLinkAudio;
        // Every choice refreshes the songs and the summary (set once all the parts exist).
        Action changed = () => { };

        var body = new StackPanel();

        // ---- the pack: icon, name, who made it
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.Children.Add(new Image { Source = (ImageSource)Res["PackImage"], Width = 58, Height = 58 });
        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = pack.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = Credits(m, pack.FileSize), FontSize = 12.5, Foreground = B("SubTextBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(At(info, 1));
        body.Children.Add(head);

        int CountOf(PackSongState s) => songs.Count(x => x.State == s);
        var stats = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        stats.Children.Add(Pill(L.Count(songs.Count, "1 brano", "{0} brani"), null));
        if (CountOf(PackSongState.New) is > 0 and var n1) stats.Children.Add(Pill(L.Count(n1, "1 nuovo", "{0} nuovi"), B("AccentTextBrush")));
        if (CountOf(PackSongState.InLibrary) is > 0 and var n2) stats.Children.Add(Pill(L.F("{0} già nella libreria", n2), B("SuccessBrush")));
        if (CountOf(PackSongState.Download) is > 0 and var n3) stats.Children.Add(Pill(L.F("{0} da scaricare", n3), B("WarningBrush")));
        if (CountOf(PackSongState.Missing) is > 0 and var n4) stats.Children.Add(Pill(L.Count(n4, "1 non disponibile", "{0} non disponibili"), B("ErrorBrush")));
        body.Children.Add(stats);

        var inputs = new StackPanel();
        body.Children.Add(inputs);

        // ---- playlists: new, or merged into the one with the same name
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        if (m.Playlists.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Playlist"), m.Playlists.Count > 1 ? allLists : null, 12));
            var listPanel = new StackPanel();
            foreach (var pp in m.Playlists)
            {
                var existing = PackImporter.SameName(profile, pp);
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(PlaylistCover(pack, pp));
                var names = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = pp.Favorites ? L.T("Preferiti") : pp.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(new TextBlock
                {
                    Text = L.Count(pp.Tracks.Count, "1 brano", "{0} brani") + (existing != null && !pp.Favorites ? " · " + L.T("ne hai già una con questo nome") : ""),
                    FontSize = 12, Foreground = B("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(At(names, 1));
                var cb = new CheckBox { IsChecked = true, Content = row };
                cb.Checked += (_, _) => { pickedLists.Add(pp.Id); changed(); };
                cb.Unchecked += (_, _) => { pickedLists.Remove(pp.Id); changed(); };
                listBoxes.Add(cb);

                // What happens to it, on the right (outside the tick box).
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(cb);
                if (existing != null)
                {
                    var choices = pp.Favorites
                        ? new List<Choice> { new(L.T("Nei tuoi Preferiti"), true), new(L.T("Come playlist a parte"), false) }
                        : new List<Choice> { new(L.T("Unisci alla tua"), true), new(L.T("Crea una copia"), false) };
                    var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0, Width = 190, Height = 32, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    combo.ToolTip = pp.Favorites
                        ? L.T("I brani si aggiungono ai tuoi Preferiti, oppure diventano una playlist nuova.")
                        : L.T("I brani mancanti si aggiungono alla tua playlist (senza doppioni), oppure ne viene creata un'altra.");
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice c) merge[pp.Id] = (bool)c.Value!; };
                    line.Children.Add(At(combo, 1));
                }
                else line.Children.Add(At(new TextBlock { Text = L.T("Nuova"), FontSize = 12, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 1));
                listPanel.Children.Add(HoverRow(line));
            }
            inputs.Children.Add(Box(listPanel, 200));
            allLists.Click += (_, _) =>
            {
                bool all = listBoxes.All(b => b.IsChecked == true);
                foreach (var b in listBoxes) b.IsChecked = !all;
            };
        }

        // ---- tags: same name = yours
        var tagChips = new List<(PackTag T, Brush Dot, Border Chip)>();
        var allTags = Link();
        if (m.Tags.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Tag"), m.Tags.Count > 1 ? allTags : null));
            var wrap = new WrapPanel();
            foreach (var pt in m.Tags)
            {
                var mine = PackImporter.SameName(profile, pt);
                var dot = Ui.BrushFrom(mine?.Color ?? pt.Color);
                int count = m.Tracks.Count(t => t.Tags.Contains(pt.Id));
                var chip = Chip(pt.Name, dot, count + (mine != null ? " · " + L.T("già tuo") : ""), true);
                chip.ToolTip = mine != null
                    ? L.T("Hai già un tag con questo nome: i brani riceveranno il tuo.")
                    : L.F("Un tag nuovo, sui suoi {0}", L.Count(count, "1 brano", "{0} brani"));
                chip.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    if (!pickedTags.Remove(pt.Id)) pickedTags.Add(pt.Id);
                    SetChip(chip, dot, pickedTags.Contains(pt.Id));
                    changed();
                };
                tagChips.Add((pt, dot, chip));
                wrap.Children.Add(chip);
            }
            inputs.Children.Add(wrap);
            allTags.Click += (_, _) =>
            {
                bool all = pickedTags.Count == m.Tags.Count;
                pickedTags.Clear();
                if (!all) pickedTags.UnionWith(m.Tags.Select(t => t.Id));
                foreach (var (t, dot, chip) in tagChips) SetChip(chip, dot, pickedTags.Contains(t.Id));
                changed();
            };
        }

        // ---- songs in no playlist and without tags (a whole library)
        if (loose.Count > 0)
        {
            var othersBox = new CheckBox
            {
                IsChecked = true,
                Margin = new Thickness(0, m.Playlists.Count + m.Tags.Count > 0 ? 10 : 16, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = TwoLines(m.Playlists.Count + m.Tags.Count > 0 ? L.Count(loose.Count, "Un altro brano", "Gli altri {0} brani") : L.Count(loose.Count, "1 brano", "{0} brani"),
                    L.T("Brani del pacchetto che non sono in nessuna playlist o tag.")),
            };
            othersBox.Checked += (_, _) => { others = true; changed(); };
            othersBox.Unchecked += (_, _) => { others = false; changed(); };
            inputs.Children.Add(othersBox);
        }

        // ---- songs that are only links: their audio saved, or in the cloud
        if (CountOf(PackSongState.Download) > 0)
        {
            var saveBox = new CheckBox
            {
                IsChecked = !cloud,
                Margin = new Thickness(0, 12, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
                Content = TwoLines(L.T("Salva l'audio sul dispositivo"),
                    L.T("Spento: i brani che nel pacchetto sono solo link vanno nella libreria nel cloud, senza scaricarli. Li ascolti dal loro link e li salvi quando vuoi.")),
            };
            void SetCloud(bool on)
            {
                cloud = on;
                if (settings != null)
                {
                    settings.SaveLinkAudio = !on;
                    settings.Save();
                }
                changed();
            }
            saveBox.Checked += (_, _) => SetCloud(false);
            saveBox.Unchecked += (_, _) => SetCloud(true);
            inputs.Children.Add(saveBox);
        }

        // ---- every song, with what happens to it
        if (rows.Count > 0)
        {
            var toggle = Link();
            toggle.Margin = new Thickness(2, 16, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            var list = new ListBox { Style = (Style)Res["PlainList"], ItemsSource = rows, ItemTemplate = (DataTemplate)Res["PackSongTemplate"] };
            var listBox = new Border
            {
                Height = 236, CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"),
                BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0), Child = list, Visibility = Visibility.Collapsed,
            };
            void ToggleText() => toggle.Content = listBox.Visibility == Visibility.Visible ? "▴  " + L.T("Nascondi i brani") : "▾  " + L.T("Mostra i brani");
            toggle.Click += (_, _) =>
            {
                listBox.Visibility = listBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                ToggleText();
            };
            ToggleText();
            inputs.Children.Add(toggle);
            inputs.Children.Add(listBox);
        }

        var (summary, line1, line2) = Summary();
        body.Children.Add(summary);
        var (progress, bar, status) = Progress();
        body.Children.Add(progress);

        var cancel = Dialogs.Button(L.T("Annulla"), "GhostButton", isCancel: true);
        var ok = Dialogs.Button(L.T("Importa"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Importa un pacchetto"), Fit(body), 580, cancel, ok);

        PackImportPlan Plan() => new()
        {
            Playlists = pickedLists.ToHashSet(),
            Merge = new Dictionary<string, bool>(merge),
            Tags = pickedTags.ToHashSet(),
            OtherSongs = others,
            Cloud = cloud,
        };

        void Update()
        {
            var plan = Plan();
            var chosen = PackImporter.Chosen(m, plan).Select(t => t.Id).ToHashSet();
            foreach (var r in rows) r.IsIncluded = chosen.Contains(r.Song.T.Id);
            allLists.Content = listBoxes.All(b => b.IsChecked == true) ? L.T("Deseleziona tutte") : L.T("Seleziona tutte");
            allTags.Content = pickedTags.Count == m.Tags.Count ? L.T("Deseleziona tutti") : L.T("Seleziona tutti");
            int Count(PackSongState s) => rows.Count(r => r.IsIncluded && r.Song.State == s);
            int add = Count(PackSongState.New), get = Count(PackSongState.Download), have = Count(PackSongState.InLibrary), miss = Count(PackSongState.Missing);
            ok.IsEnabled = chosen.Count > 0 || plan.Playlists.Count > 0 || plan.Tags.Count > 0;
            if (!ok.IsEnabled)
            {
                line1.Text = L.T("Scegli cosa importare.");
                line2.Text = "";
                return;
            }
            line1.Text = add + get == 0
                ? (chosen.Count > 0 ? L.T("Nessun brano da copiare: li hai già tutti") : L.T("Nessun brano da aggiungere"))
                : L.Count(add + get, "Verrà aggiunto 1 brano", "Verranno aggiunti {0} brani");
            var more = new List<string>();
            if (get > 0) more.Add(cloud ? L.F("{0} nel cloud, senza scaricarli", get) : L.F("{0} da scaricare", get));
            if (have > 0) more.Add(L.F("{0} già nella libreria", have));
            if (miss > 0) more.Add(L.Count(miss, "1 non disponibile", "{0} non disponibili"));
            if (add > 0) more.Add(L.F("nella cartella {0}", musicDir));
            line2.Text = string.Join(" · ", more);
        }
        changed = Update;

        bool busy = false;
        CancellationTokenSource? cts = null;
        ok.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            inputs.IsEnabled = false;
            ok.IsEnabled = false;
            progress.Visibility = Visibility.Visible;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparo l'importazione…");
            cts = new CancellationTokenSource();
            var plan = Plan();
            var report = Reporter(win, bar, status);
            try
            {
                if (Engines.Missing().Contains("ffmpeg.exe")) await ensureEngines();
                var result = await Task.Run(() => PackImporter.RunAsync(pack, plan, lib, profile, musicDir, report, cts.Token));
                busy = false;
                done(result);
                win.DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                busy = false;
                win.DialogResult = false;
            }
            catch (Exception ex)
            {
                busy = false;
                inputs.IsEnabled = true;
                ok.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Importazione non riuscita:") + " " + ex.Message;
            }
        };
        // Closing while it copies: it stops (the songs already copied stay), then the dialog goes.
        win.Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            cts?.Cancel();
            status.Text = L.T("Annullo…");
        };
        changed();
        return win;
    }

    private static string Credits(PackManifest m, long size)
    {
        var parts = new List<string?>
        {
            string.IsNullOrWhiteSpace(m.Author) ? null : L.F("di {0}", m.Author),
            m.Created.ToString("d MMM yyyy", L.Culture),
            Text.Size(size),
            m.Music ? null : L.T("solo link"),
        };
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    // The playlist's own picture, the cover of its first song in the pack, the Favorites heart or a note.
    private static FrameworkElement PlaylistCover(PackFile pack, PackPlaylist p)
    {
        var grid = new Grid { Width = 34, Height = 34 };
        var back = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = p.Favorites ? (Brush)Res["FavoritesGradient"] : B("PlaceholderGradient"),
            Child = new TextBlock
            {
                Text = p.Favorites ? "" : MusicGlyph, Style = (Style)Res["Glyph"], FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = p.Favorites ? Brushes.White : B("MutedBrush"),
            },
        };
        grid.Children.Add(back);
        if (p.Favorites) return grid;
        var image = new Border { CornerRadius = new CornerRadius(6), Visibility = Visibility.Collapsed };
        grid.Children.Add(image);
        _ = Load();
        return grid;

        async Task Load()
        {
            var entry = p.HasCover ? Pack.PlaylistCoverEntry(p.Id) : p.Tracks.Select(Pack.CoverEntry).FirstOrDefault(pack.Has);
            if (entry == null || await PackImages.LoadAsync(pack, entry, 72) is not { } img) return;
            image.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            image.Visibility = Visibility.Visible;
        }
    }

    // ================================================================== pieces

    // On a short screen the dialog scrolls instead of going past the bottom (title and buttons stay).
    private static ScrollViewer Fit(UIElement body) => new()
    {
        Content = body, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 200),
    };

    // Progress from a worker thread, a few times per percent at most.
    private static Action<double, string?> Reporter(Window win, ProgressBar bar, TextBlock status)
    {
        double last = -1;
        string? lastTitle = null;
        return (f, title) =>
        {
            if (Math.Abs(f - last) < 0.004 && title == lastTitle) return;
            last = f;
            lastTitle = title;
            win.Dispatcher.BeginInvoke(() =>
            {
                bar.Value = f;
                if (title != null) status.Text = L.F("Copio «{0}»…", title);
            });
        };
    }

    private static T At<T>(T e, int column) where T : UIElement
    {
        Grid.SetColumn(e, column);
        return e;
    }

    private static TextBlock Label(string text, double top = 18)
        => new() { Text = text, Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, top, 0, 8) };

    private static TextBlock Hint(string text, double top = 0)
        => new() { Text = text, Style = (Style)Res["Hint"], Margin = new Thickness(0, top, 0, 0) };

    private static StackPanel TwoLines(string title, string hint)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        s.Children.Add(new TextBlock { Text = hint, Style = (Style)Res["Hint"] });
        return s;
    }

    // A section title with a "select all" link on the right.
    private static DockPanel Header(string text, Button? link, double top = 18)
    {
        var dock = new DockPanel { Margin = new Thickness(0, top, 0, 8) };
        if (link != null)
        {
            DockPanel.SetDock(link, Dock.Right);
            dock.Children.Add(link);
        }
        dock.Children.Add(new TextBlock { Text = text, Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }

    private static Button Link() => new() { Style = (Style)Res["LinkButton"], FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    // Rows in a dark box that scrolls when long.
    private static Border Box(UIElement content, double maxHeight) => new()
    {
        CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1),
        Padding = new Thickness(4),
        Child = new ScrollViewer { Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
    };

    private static Border HoverRow(UIElement child)
    {
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Child = child };
        row.MouseEnter += (_, _) => row.Background = B("SurfaceBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    // A tag that can be ticked: dot, name, how many songs.
    private static Border Chip(string name, Brush dot, string? extra, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, FontSize = 13, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(extra))
            row.Children.Add(new TextBlock { Text = extra, FontSize = 12, Foreground = B("MutedBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(11, 5, 13, 5), Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Child = row,
        };
        SetChip(chip, dot, on);
        return chip;
    }

    private static void SetChip(Border chip, Brush dot, bool on)
    {
        chip.Background = on ? B("AccentSoftBrush") : B("Surface3Brush");
        chip.BorderBrush = on ? dot : Brushes.Transparent;
        chip.Opacity = on ? 1 : 0.75;
    }

    // A small rounded label with a coloured dot.
    private static Border Pill(string text, Brush? dot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot != null) row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = B("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 10, 4), Margin = new Thickness(0, 0, 6, 6), Background = B("Surface3Brush"), Child = row };
    }

    // Two tabs in a pill (files / links).
    private static (Border Bar, RadioButton A, RadioButton B) Segmented(string a, string aGlyph, string b, string bGlyph)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var ra = new RadioButton { Style = (Style)Res["Segment"], Content = a, GroupName = group };
        var rb = new RadioButton { Style = (Style)Res["Segment"], Content = b, GroupName = group, Margin = new Thickness(4, 0, 0, 0) };
        Ui.SetGlyph(ra, aGlyph);
        Ui.SetGlyph(rb, bGlyph);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(ra);
        panel.Children.Add(rb);
        var bar = new Border
        {
            CornerRadius = new CornerRadius(20), Padding = new Thickness(4), Background = B("BgBrush"), BorderBrush = B("BorderBrush"),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = panel,
        };
        return (bar, ra, rb);
    }

    // The box at the bottom: the pack icon and what will happen.
    private static (Border Box, TextBlock Line1, TextBlock Line2) Summary()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new Image { Source = (ImageSource)Res["PackImage"], Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var line1 = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var line2 = new TextBlock { FontSize = 12.5, Foreground = B("SubTextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(line1);
        text.Children.Add(line2);
        grid.Children.Add(At(text, 1));
        var box = new Border
        {
            CornerRadius = new CornerRadius(12), Background = B("Surface2Brush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 20, 0, 0), Child = grid,
        };
        return (box, line1, line2);
    }

    private static (StackPanel Panel, ProgressBar Bar, TextBlock Status) Progress()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        var status = new TextBlock { FontSize = 12.5, Foreground = B("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(status);
        panel.Children.Add(bar);
        return (panel, bar, status);
    }
}
