using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;
using Path = System.IO.Path;

namespace UltimateMP3Player.Views;

// .ump packs on the phone (the computer apps' PackDialogs, as sheets): export playlists, tags or the whole library into
// one file, then share it (Telegram, Discord, Drive...) or save it; import one (what's new, what's already here).
public static class PackDialogs
{
    private static IBrush B(string key) => Ui.Res(key);

    private const string MusicGlyph = "";
    private const string LinkGlyph = "";

    private static Bitmap? _packImage;
    private static Bitmap PackImage => _packImage ??= new Bitmap(AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/pack-256.png")));

    private static SheetHost? Sheets => App.Host.View?.Sheets;

    // ================================================================== export

    // many: several playlists chosen together (selected on Home or in the library); done: once the pack is made.
    public static void Export(MainViewModel main, PlaylistViewModel? playlist = null, TagViewModel? tag = null, IReadOnlyCollection<PlaylistViewModel>? many = null,
        Action? done = null) => _ = ExportAsync(main, playlist, tag, many, done);

    private static async Task ExportAsync(MainViewModel main, PlaylistViewModel? playlist, TagViewModel? tag, IReadOnlyCollection<PlaylistViewModel>? many,
        Action? done)
    {
        var sheets = Sheets;
        if (sheets == null) return;
        var lib = main.Library;
        var profile = main.Profile;
        var lists = main.Playlists.ToList();
        var tags = main.Tags.ToList();
        var pickedLists = lists.Where(l => l == playlist || many?.Contains(l) == true).ToHashSet();
        var pickedTags = tags.Where(t => t == tag).ToHashSet();
        bool whole = false;
        Action changed = () => { };

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long SizeOf(string? path)
        {
            if (path == null) return 0;
            if (!sizes.TryGetValue(path, out var s)) sizes[path] = s = PackWriter.SizeOf(path);
            return s;
        }

        var body = new StackPanel { Margin = new Thickness(22, 4, 22, 18) };
        body.Children.Add(Title(L.T("Esporta in un pacchetto")));
        var inputs = new StackPanel();
        body.Children.Add(inputs);
        inputs.Children.Add(Hint(L.T("Scegli cosa mettere nel pacchetto: un file .ump da tenere come copia o da mandare a un amico, che lo apre con Ultimate MP3 Player e ritrova tutto, senza doppioni.")));

        // ---- playlists
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        inputs.Children.Add(Header(L.T("Playlist"), lists.Count > 1 ? allLists : null, 16));
        var listPanel = new StackPanel();
        foreach (var p in lists)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            row.Children.Add(new ContentControl { Content = p, ContentTemplate = Ui.Find<IDataTemplate>("PlaylistCover"), Width = 40, Height = 40 });
            row.Children.Add(At(new TextBlock { Text = p.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) }, 1));
            row.Children.Add(At(new TextBlock { Text = p.CountText, FontSize = 12.5, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center }, 2));
            var cb = new CheckBox { IsChecked = pickedLists.Contains(p), Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 4) };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) pickedLists.Add(p);
                else pickedLists.Remove(p);
                changed();
            };
            listBoxes.Add(cb);
            listPanel.Children.Add(cb);
        }
        inputs.Children.Add(listPanel);
        allLists.Click += (_, _) =>
        {
            bool all = listBoxes.All(b => b.IsChecked == true);
            foreach (var b in listBoxes) b.IsChecked = !all;
        };

        // ---- tags: each brings all its songs
        if (tags.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Tag"), null));
            var wrap = new WrapPanel();
            foreach (var t in tags)
            {
                var chip = Chip(t.Name, t.Brush, t.Count.ToString(), pickedTags.Contains(t));
                chip.IsCheckedChanged += (_, _) =>
                {
                    if (chip.IsChecked == true) pickedTags.Add(t);
                    else pickedTags.Remove(t);
                    changed();
                };
                wrap.Children.Add(chip);
            }
            inputs.Children.Add(wrap);
        }

        // ---- the whole library
        inputs.Children.Add(Label(L.T("Altro"), 12));
        var wholeBox = new CheckBox
        {
            Content = TwoLines(L.T("Tutta la libreria"), L.F("Anche i brani che non sono in nessuna playlist o tag ({0} in tutto): comodo per spostare tutto su un altro telefono o computer.", lib.Count)),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        wholeBox.IsCheckedChanged += (_, _) => { whole = wholeBox.IsChecked == true; changed(); };
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
        withFiles.IsCheckedChanged += (_, _) => changed();
        linksOnly.IsCheckedChanged += (_, _) => changed();
        videoBox.IsCheckedChanged += (_, _) => changed();

        var (summary, line1, line2) = Summary();
        body.Children.Add(summary);
        var (progress, bar, status) = Progress();
        body.Children.Add(progress);

        var cancel = Button(L.T("Annulla"), "TouchGhost");
        var ok = Button(L.T("Crea il pacchetto"), "TouchPrimary");
        var buttons = Buttons(cancel, ok);
        body.Children.Add(buttons);
        var finished = new StackPanel { IsVisible = false, Margin = new Thickness(0, 16, 0, 0), Spacing = 10 };
        var share = Button(L.T("Condividi…"), "TouchPrimary");
        var saveAs = Button(L.T("Salva in…"), "TouchGhost");
        var close = Button(L.T("Chiudi"), "TouchGhost");
        finished.Children.Add(share);
        finished.Children.Add(saveAs);
        finished.Children.Add(close);
        body.Children.Add(finished);

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
            modeHint.Text = e.Music
                ? L.T("La musica è dentro il pacchetto: si importa anche senza internet e con i brani spariti dai siti. Pesa quanto i brani.")
                : L.T("Pacchetto leggerissimo, perfetto da mandare su Discord: chi lo apre riscarica i brani dai loro link. Quelli senza link (file tuoi, mix del DJ) viaggiano comunque come file.");
            videoBox.IsVisible = e.Music && songs.Any(t => t.HasVideo);
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

        bool busy = false;
        string? saved = null;
        CancellationTokenSource? cts = null;
        ok.Click += async (_, _) =>
        {
            if (busy) return;
            var e = Current();
            if (e.IsEmpty) return;
            Directory.CreateDirectory(Platform.Files.ShareDir);
            var path = Path.Combine(Platform.Files.ShareDir, DefaultName(profile, e));
            busy = true;
            inputs.IsEnabled = false;
            ok.IsEnabled = false;
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparo il pacchetto…");
            cts = new CancellationTokenSource();
            var report = Reporter(bar, status);
            try
            {
                var m = await Task.Run(() => PackWriter.WriteAsync(path, lib, profile, e, AppInfo.VersionText, report, cts.Token));
                saved = path;
                bar.Value = 1;
                status.Foreground = B("SuccessBrush");
                status.Text = "✓ " + L.F("Pacchetto creato: {0} ({1})", Path.GetFileName(path), Text.Size(PackWriter.SizeOf(path)));
                line1.Text = L.Count(m.Tracks.Count, "1 brano", "{0} brani") + " · " + L.Count(m.Playlists.Count, "1 playlist", "{0} playlist") +
                             " · " + L.Count(m.Tags.Count, "1 tag", "{0} tag");
                buttons.IsVisible = false;
                finished.IsVisible = true;
                done?.Invoke();
            }
            catch (OperationCanceledException)
            {
                sheets.Close(body);
            }
            catch (Exception ex)
            {
                inputs.IsEnabled = true;
                ok.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Esportazione non riuscita:") + " " + ex.Message;
            }
            finally { busy = false; }
        };
        cancel.Click += (_, _) =>
        {
            if (busy)
            {
                cts?.Cancel();
                status.Text = L.T("Annullo…");
            }
            else sheets.Close(body);
        };
        share.Click += (_, _) => { if (saved != null) Platform.Files.Share(saved, "application/octet-stream", L.T("Condividi il pacchetto")); };
        saveAs.Click += async (_, _) =>
        {
            if (saved == null) return;
            try
            {
                if (await Dialogs.SaveAsAsync(saved, Path.GetFileName(saved), "application/octet-stream")) main.Toast(L.T("Pacchetto salvato"));
            }
            catch (Exception ex) { main.Toast(L.T("Esportazione non riuscita:") + " " + ex.Message); }
        };
        close.Click += (_, _) => sheets.Close(body);
        changed();
        await sheets.Show(new ScrollViewer { Content = body }, dismissable: true, onDismiss: () => cts?.Cancel(), maxHeightShare: 0.94);
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
            result = await ImportSheet(pack, songs, main.Library, main.Profile, main.Host.Settings.MusicDir, main.Host.EnsureEnginesAsync, main.Host.Settings);
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

        var ids = result.PlaylistIds.Distinct().ToList();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (ids.Count == 1 && main.Playlists.FirstOrDefault(p => p.Id == ids[0]) is { } pl) main.OpenPlaylist(pl);
    }

    private static async Task<PackImportResult?> ImportSheet(PackFile pack, List<PackSong> songs, Library lib, Profile profile, string musicDir, Func<Task> ensureEngines,
        AppSettings settings)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        // The songs that travelled as links: downloaded, or into the library in the cloud (the last choice, like the links).
        bool cloud = !settings.SaveLinkAudio;
        var m = pack.Manifest;
        var rows = songs.Select(s => new PackSongRow(s, pack)).ToList();
        var pickedLists = m.Playlists.Select(p => p.Id).ToHashSet();
        var merge = m.Playlists.ToDictionary(p => p.Id, _ => true);
        var pickedTags = m.Tags.Select(t => t.Id).ToHashSet();
        var loose = PackImporter.Loose(m);
        bool others = loose.Count > 0;
        Action changed = () => { };
        PackImportResult? outcome = null;

        var body = new StackPanel { Margin = new Thickness(22, 4, 22, 18) };
        body.Children.Add(Title(L.T("Importa un pacchetto")));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(new Image { Source = PackImage, Width = 58, Height = 58 });
        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = pack.Name, FontSize = 16.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = Credits(m, pack.FileSize), FontSize = 13, Foreground = B("SubTextBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
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

        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        if (m.Playlists.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Playlist"), m.Playlists.Count > 1 ? allLists : null, 12));
            foreach (var pp in m.Playlists)
            {
                var existing = PackImporter.SameName(profile, pp);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                row.Children.Add(PlaylistCover(pack, pp));
                var names = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = pp.Favorites ? L.T("Preferiti") : pp.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(new TextBlock
                {
                    Text = L.Count(pp.Tracks.Count, "1 brano", "{0} brani") + (existing != null && !pp.Favorites ? " · " + L.T("ne hai già una con questo nome") : ""),
                    FontSize = 12.5, Foreground = B("MutedBrush"), TextWrapping = TextWrapping.Wrap,
                });
                row.Children.Add(At(names, 1));
                var cb = new CheckBox { IsChecked = true, Content = row, Margin = new Thickness(0, 4) };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) pickedLists.Add(pp.Id);
                    else pickedLists.Remove(pp.Id);
                    changed();
                };
                listBoxes.Add(cb);
                inputs.Children.Add(cb);
                if (existing != null)
                {
                    var choices = pp.Favorites
                        ? new List<Choice> { new(L.T("Nei tuoi Preferiti"), true), new(L.T("Come playlist a parte"), false) }
                        : new List<Choice> { new(L.T("Unisci alla tua"), true), new(L.T("Crea una copia"), false) };
                    var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 42, Margin = new Thickness(32, 2, 0, 6) };
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice c) merge[pp.Id] = (bool)c.Value!; };
                    inputs.Children.Add(combo);
                }
            }
            allLists.Click += (_, _) =>
            {
                bool all = listBoxes.All(b => b.IsChecked == true);
                foreach (var b in listBoxes) b.IsChecked = !all;
            };
        }

        if (m.Tags.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Tag"), null));
            var wrap = new WrapPanel();
            foreach (var pt in m.Tags)
            {
                var mine = PackImporter.SameName(profile, pt);
                var dot = Ui.BrushFrom(mine?.Color ?? pt.Color);
                int count = m.Tracks.Count(t => t.Tags.Contains(pt.Id));
                var chip = Chip(pt.Name, dot, count + (mine != null ? " · " + L.T("già tuo") : ""), true);
                chip.IsCheckedChanged += (_, _) =>
                {
                    if (chip.IsChecked == true) pickedTags.Add(pt.Id);
                    else pickedTags.Remove(pt.Id);
                    changed();
                };
                wrap.Children.Add(chip);
            }
            inputs.Children.Add(wrap);
        }

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
            othersBox.IsCheckedChanged += (_, _) => { others = othersBox.IsChecked == true; changed(); };
            inputs.Children.Add(othersBox);
        }

        // ---- songs that are only links: their audio saved, or in the cloud
        if (CountOf(PackSongState.Download) > 0)
        {
            var saveBox = new CheckBox
            {
                Theme = Ui.Theme("Switch"),
                IsChecked = !cloud,
                Margin = new Thickness(0, 14, 0, 0),
                Content = TwoLines(L.T("Salva l'audio sul dispositivo"),
                    L.T("Spento: i brani che nel pacchetto sono solo link vanno nella libreria nel cloud, senza scaricarli. Li ascolti dal loro link e li salvi quando vuoi.")),
            };
            saveBox.IsCheckedChanged += (_, _) =>
            {
                cloud = saveBox.IsChecked != true;
                settings.SaveLinkAudio = !cloud;
                settings.Save();
                changed();
            };
            inputs.Children.Add(saveBox);
        }

        if (rows.Count > 0)
        {
            var toggle = Link();
            toggle.Margin = new Thickness(2, 16, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            var list = new ItemsControl { ItemsSource = rows, ItemTemplate = Ui.Find<IDataTemplate>("PackSongTemplate") };
            var listBox = new Border
            {
                CornerRadius = new CornerRadius(12), Background = B("BgBrush"), Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0),
                Child = new ScrollViewer { Content = list, MaxHeight = 300 }, IsVisible = false,
            };
            void ToggleText() => toggle.Content = listBox.IsVisible ? "▴  " + L.T("Nascondi i brani") : "▾  " + L.T("Mostra i brani");
            toggle.Click += (_, _) =>
            {
                listBox.IsVisible = !listBox.IsVisible;
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

        var cancel = Button(L.T("Annulla"), "TouchGhost");
        var ok = Button(L.T("Importa"), "TouchPrimary");
        body.Children.Add(Buttons(cancel, ok));

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
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparo l'importazione…");
            cts = new CancellationTokenSource();
            var plan = Plan();
            var report = Reporter(bar, status);
            try
            {
                if (Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await ensureEngines();
                outcome = await Task.Run(() => PackImporter.RunAsync(pack, plan, lib, profile, musicDir, report, cts.Token));
                busy = false;
                sheets.Close(body);
            }
            catch (OperationCanceledException)
            {
                busy = false;
                sheets.Close(body);
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
        cancel.Click += (_, _) =>
        {
            if (busy)
            {
                cts?.Cancel();
                status.Text = L.T("Annullo…");
            }
            else sheets.Close(body);
        };
        changed();
        await sheets.Show(new ScrollViewer { Content = body }, dismissable: true, onDismiss: () => cts?.Cancel(), maxHeightShare: 0.94);
        return outcome;
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

    private static Control PlaylistCover(PackFile pack, PackPlaylist p)
    {
        var grid = new Panel { Width = 40, Height = 40 };
        var glyph = new TextBlock
        {
            Text = Icons.Map(p.Favorites ? "" : MusicGlyph), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = p.Favorites ? Brushes.White : B("MutedBrush"),
        };
        glyph.Classes.Add("glyph");
        if (p.Favorites) glyph.Classes.Add("filled");
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = p.Favorites ? Ui.Res("FavoritesGradient") : B("PlaceholderGradient"), Child = glyph });
        if (p.Favorites) return grid;
        var image = new Border { CornerRadius = new CornerRadius(6), IsVisible = false };
        grid.Children.Add(image);
        _ = Load();
        return grid;

        async Task Load()
        {
            var entry = p.HasCover ? Pack.PlaylistCoverEntry(p.Id) : p.Tracks.Select(Pack.CoverEntry).FirstOrDefault(pack.Has);
            if (entry == null || await PackImages.LoadAsync(pack, entry, 80) is not Bitmap img) return;
            image.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            image.IsVisible = true;
        }
    }

    // ================================================================== pieces

    private static Action<double, string?> Reporter(ProgressBar bar, TextBlock status)
    {
        double last = -1;
        string? lastTitle = null;
        return (f, title) =>
        {
            if (Math.Abs(f - last) < 0.004 && title == lastTitle) return;
            last = f;
            lastTitle = title;
            Dispatcher.UIThread.Post(() =>
            {
                bar.Value = f;
                if (title != null) status.Text = L.F("Copio «{0}»…", title);
            });
        };
    }

    private static T At<T>(T e, int column) where T : Control
    {
        Grid.SetColumn(e, column);
        return e;
    }

    private static TextBlock Title(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 10) };
    private static TextBlock Label(string text, double top = 18) => Ui.Text(text, "field-label", new Thickness(4, top, 0, 8));
    private static TextBlock Hint(string text, double top = 0) => Ui.Text(text, "hint", new Thickness(0, top, 0, 0));

    private static StackPanel TwoLines(string title, string hint)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = 15 });
        s.Children.Add(Ui.Text(hint, "hint"));
        return s;
    }

    private static DockPanel Header(string text, Button? link, double top = 18)
    {
        var dock = new DockPanel { Margin = new Thickness(0, top, 0, 6) };
        if (link != null)
        {
            DockPanel.SetDock(link, Dock.Right);
            dock.Children.Add(link);
        }
        var label = Ui.Text(text, "field-label", new Thickness(4, 0, 0, 0));
        label.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(label);
        return dock;
    }

    private static Button Link() => new() { Theme = Ui.Theme("LinkButton"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6), Margin = new Thickness(0, 0, 4, 0) };

    private static Button Button(string text, string theme) => new()
    {
        Content = text, Theme = Ui.Theme(theme), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
    };

    private static Grid Buttons(Button left, Button right)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*"), Margin = new Thickness(0, 18, 0, 0) };
        g.Children.Add(left);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return g;
    }

    private static ToggleButton Chip(string name, IBrush dot, string? extra, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(extra))
            row.Children.Add(new TextBlock { Text = extra, FontSize = 12, Foreground = B("MutedBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return new ToggleButton { Theme = Ui.Theme("Chip"), Content = row, IsChecked = on, Margin = new Thickness(0, 0, 8, 8) };
    }

    private static Border Pill(string text, IBrush? dot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (dot != null) row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = B("SubTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        return new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 11, 5), Margin = new Thickness(0, 0, 6, 6), Background = B("Surface3Brush"), Child = row };
    }

    private static (Border Bar, RadioButton A, RadioButton B) Segmented(string a, string aGlyph, string b, string bGlyph)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var ra = new RadioButton { Theme = Ui.Theme("Segment"), Content = a, GroupName = group };
        var rb = new RadioButton { Theme = Ui.Theme("Segment"), Content = b, GroupName = group, Margin = new Thickness(4, 0, 0, 0) };
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

    private static (Border Box, TextBlock Line1, TextBlock Line2) Summary()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new Image { Source = PackImage, Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var line1 = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var line2 = new TextBlock { FontSize = 13, Foreground = B("SubTextBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        text.Children.Add(line1);
        text.Children.Add(line2);
        grid.Children.Add(At(text, 1));
        var box = new Border
        {
            CornerRadius = new CornerRadius(14), Background = B("Surface2Brush"), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 20, 0, 0), Child = grid,
        };
        return (box, line1, line2);
    }

    private static (StackPanel Panel, ProgressBar Bar, TextBlock Status) Progress()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), IsVisible = false };
        var status = new TextBlock { FontSize = 13, Foreground = B("SubTextBrush"), TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(status);
        panel.Children.Add(bar);
        return (panel, bar, status);
    }
}
