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
        // (this device's: the other one's are exported there)
        var lists = main.Playlists.Where(p => !p.IsOther).ToList();
        var tags = main.Tags.Where(t => !main.IsTheirs(t.Id)).ToList();
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
        body.Children.Add(Title(L.T("Export to a pack")));
        var inputs = new StackPanel();
        body.Children.Add(inputs);
        inputs.Children.Add(Hint(L.T("Choose what to put in the pack: a .ump file to keep as a backup or to send to a friend, who opens it with Ultimate MP3 Player and finds everything, without duplicates.")));

        // ---- playlists
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        inputs.Children.Add(Header(L.T("Playlists"), lists.Count > 1 ? allLists : null, 16));
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
            inputs.Children.Add(Header(L.T("Tags"), null));
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
        inputs.Children.Add(Label(L.T("More"), 12));
        var wholeBox = new CheckBox
        {
            Content = TwoLines(L.T("The whole library"), L.F("Also the songs that aren't in any playlist or tag ({0} in all): handy to move everything to another phone or computer.", lib.Count)),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        wholeBox.IsCheckedChanged += (_, _) => { whole = wholeBox.IsChecked == true; changed(); };
        inputs.Children.Add(wholeBox);

        // ---- with the music or links only
        inputs.Children.Add(Label(L.T("Songs")));
        var (modeBar, withFiles, linksOnly) = Segmented(L.T("With the audio files"), MusicGlyph, L.T("Links only"), LinkGlyph);
        withFiles.IsChecked = true;
        inputs.Children.Add(modeBar);
        var modeHint = Hint("", 8);
        inputs.Children.Add(modeHint);
        var videoBox = new CheckBox { Content = L.T("Include the videos of the songs that have one"), Margin = new Thickness(0, 12, 0, 0) };
        inputs.Children.Add(videoBox);
        withFiles.IsCheckedChanged += (_, _) => changed();
        linksOnly.IsCheckedChanged += (_, _) => changed();
        videoBox.IsCheckedChanged += (_, _) => changed();

        var (summary, line1, line2) = Summary();
        body.Children.Add(summary);
        var (progress, bar, status) = Progress();
        body.Children.Add(progress);

        var cancel = Button(L.T("Cancel"), "TouchGhost");
        var ok = Button(L.T("Create the pack"), "TouchPrimary");
        var buttons = Buttons(cancel, ok);
        body.Children.Add(buttons);
        // The same songs as plain MP3s in a folder (a USB stick, an SD card, for the car): FolderExport.
        var folder = Button(L.T("To a folder (MP3)…"), "TouchGhost");
        folder.Margin = new Thickness(0, 10, 0, 0);
        body.Children.Add(folder);
        var folderHint = Hint(L.T("For a USB stick, an SD card or the car stereo: one MP3 per song, “Artist - Title”, with its tags and cover."), 6);
        body.Children.Add(folderHint);
        var finished = new StackPanel { IsVisible = false, Margin = new Thickness(0, 16, 0, 0), Spacing = 10 };
        var share = Button(L.T("Share…"), "TouchPrimary");
        var saveAs = Button(L.T("Save to…"), "TouchGhost");
        var close = Button(L.T("Close"), "TouchGhost");
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
            allLists.Content = listBoxes.All(b => b.IsChecked == true) ? L.T("Deselect all|f") : L.T("Select all|f");
            modeHint.Text = e.Music
                ? L.T("The music is inside the pack: it imports even without internet and with songs gone from the sites. It's as big as the songs.")
                : L.T("A tiny pack, perfect to send on Discord: whoever opens it downloads the songs again from their links. Those without a link (your own files, DJ mixes) still travel as files.");
            videoBox.IsVisible = e.Music && songs.Any(t => t.HasVideo);
            ok.IsEnabled = folder.IsEnabled = !e.IsEmpty;
            (line1.Text, line2.Text) = PackWriter.Summary(songs, e, SizeOf);
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
            var path = Path.Combine(Platform.Files.ShareDir, PackWriter.FileName(profile, e));
            busy = true;
            inputs.IsEnabled = false;
            ok.IsEnabled = false;
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparing the pack…");
            cts = new CancellationTokenSource();
            var report = Reporter(bar, status);
            try
            {
                var m = await Task.Run(() => PackWriter.WriteAsync(path, lib, profile, e, AppInfo.VersionText, report, cts.Token));
                saved = path;
                bar.Value = 1;
                status.Foreground = B("SuccessBrush");
                status.Text = "✓ " + L.F("Pack created: {0} ({1})", Path.GetFileName(path), Text.Size(PackWriter.SizeOf(path)));
                line1.Text = L.Count(m.Tracks.Count, "1 song", "{0} songs") + " · " + L.Count(m.Playlists.Count, "1 playlist", "{0} playlists") +
                             " · " + L.Count(m.Tags.Count, "1 tag", "{0} tags");
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
                status.Text = L.T("Export failed:") + " " + ex.Message;
            }
            finally { busy = false; }
        };
        cancel.Click += (_, _) =>
        {
            if (busy)
            {
                cts?.Cancel();
                status.Text = L.T("Canceling…");
            }
            else sheets.Close(body);
        };
        folder.Click += async (_, _) =>
        {
            var e = Current();
            if (busy || e.IsEmpty) return;
            if (await Dialogs.PickExportFolderAsync(L.T("Where to put the MP3s")) is not { } target) return;
            var songs = PackWriter.Songs(lib, profile, e);
            busy = true;
            inputs.IsEnabled = false;
            ok.IsEnabled = folder.IsEnabled = false;
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparing the MP3s…");
            cts = new CancellationTokenSource();
            var report = Reporter(bar, status);
            var cookies = App.Host.Settings.CookiesBrowserOrNull;
            try
            {
                var r = await Task.Run(() => FolderExport.WriteAsync(songs, cookies, async (file, name, ct) =>
                {
                    var dest = await target.CreateFileAsync(name) ?? throw new IOException(L.T("The chosen folder doesn't accept files."));
                    await using var input = File.OpenRead(file);
                    await using var output = await dest.OpenWriteAsync();
                    await input.CopyToAsync(output, ct);
                }, report, cts.Token));
                bar.Value = 1;
                status.Foreground = B(r.Written > 0 ? "SuccessBrush" : "WarningBrush");
                status.Text = "✓ " + L.F("{0} in “{1}”", L.Count(r.Written, "1 MP3", "{0} MP3s"), target.Name) +
                              (r.Skipped > 0 ? " · " + L.Count(r.Skipped, "1 skipped (no audio and no working link)", "{0} skipped (no audio and no working link)") : "");
                buttons.IsVisible = folder.IsVisible = folderHint.IsVisible = false;
                finished.IsVisible = true;
                share.IsVisible = saveAs.IsVisible = false;
            }
            catch (OperationCanceledException)
            {
                sheets.Close(body);
            }
            catch (Exception ex)
            {
                inputs.IsEnabled = true;
                ok.IsEnabled = folder.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Export failed:") + " " + ex.Message;
            }
            finally { busy = false; }
        };
        share.Click += (_, _) => { if (saved != null) Platform.Files.Share(saved, "application/octet-stream", L.T("Share the pack")); };
        saveAs.Click += async (_, _) =>
        {
            if (saved == null) return;
            try
            {
                if (await Dialogs.SaveAsAsync(saved, Path.GetFileName(saved), "application/octet-stream")) main.Toast(L.T("Pack saved"));
            }
            catch (Exception ex) { main.Toast(L.T("Export failed:") + " " + ex.Message); }
        };
        close.Click += (_, _) => sheets.Close(body);
        changed();
        await sheets.Show(new ScrollViewer { Content = body }, dismissable: true, onDismiss: () => cts?.Cancel(), maxHeightShare: 0.94);
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
            Dialogs.Alert(L.T("Can't open the pack"), ex.Message);
            return;
        }
        await Import(main, pack);
    }

    // An opened pack: a file, or the music the connected computer sends (Connect phone).
    public static async Task Import(MainViewModel main, PackFile pack)
    {
        PackImportResult? result = null;
        using (pack)
        {
            var m = pack.Manifest;
            if (m.Tracks.Count == 0 && m.Playlists.Count == 0 && m.Tags.Count == 0)
            {
                Dialogs.Alert(L.T("The pack is empty"), L.F("“{0}” contains no songs, playlists or tags.", pack.Name));
                return;
            }
            var songs = await Task.Run(() => PackImporter.Analyze(pack, main.Library));
            result = await ImportSheet(pack, songs, main.Library, main.Profile, main.Host.Settings.MusicDir, main.Host.EnsureEnginesAsync, main.Host.Settings);
        }
        if (result == null) return;

        main.QueuePackDownloads(result);
        main.Toast(result.Report());

        var ids = result.PlaylistIds.Distinct().ToList();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (ids.Count == 1 && main.Playlists.FirstOrDefault(p => p.Id == ids[0]) is { } pl) main.OpenPlaylist(pl);
    }

    private static async Task<PackImportResult?> ImportSheet(PackFile pack, List<PackSong> songs, Library lib, Profile profile, string musicDir, Func<Task> ensureEngines,
        AppSettings settings)
    {
        var sheets = Sheets;
        if (sheets == null) return null;
        // The songs that travelled as links: downloaded, or into the library in the cloud (as Settings › Downloads say).
        bool cloud = !settings.SaveAudio;
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
        body.Children.Add(Title(L.T("Import a pack")));

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(new Image { Source = PackImage, Width = 58, Height = 58 });
        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = pack.Name, FontSize = 16.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = m.Credits(pack.FileSize), FontSize = 13, Foreground = B("SubTextBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        head.Children.Add(At(info, 1));
        body.Children.Add(head);

        int CountOf(PackSongState s) => songs.Count(x => x.State == s);
        var stats = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        stats.Children.Add(Pill(L.Count(songs.Count, "1 song", "{0} songs"), null));
        if (CountOf(PackSongState.New) is > 0 and var n1) stats.Children.Add(Pill(L.Count(n1, "1 new", "{0} new"), B("AccentTextBrush")));
        if (CountOf(PackSongState.InLibrary) is > 0 and var n2) stats.Children.Add(Pill(L.F("{0} already in your library", n2), B("SuccessBrush")));
        if (CountOf(PackSongState.Download) is > 0 and var n3) stats.Children.Add(Pill(L.Count(n3, "1 as a link", "{0} as links"), B("WarningBrush")));
        if (CountOf(PackSongState.Missing) is > 0 and var n4) stats.Children.Add(Pill(L.Count(n4, "1 not available", "{0} not available"), B("ErrorBrush")));
        body.Children.Add(stats);

        var inputs = new StackPanel();
        body.Children.Add(inputs);

        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        if (m.Playlists.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Playlists"), m.Playlists.Count > 1 ? allLists : null, 12));
            foreach (var pp in m.Playlists)
            {
                var existing = PackImporter.SameName(profile, pp);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                row.Children.Add(PlaylistCover(pack, pp));
                var names = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = pp.Favorites ? L.T("Favorites") : pp.Name, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(new TextBlock
                {
                    Text = L.Count(pp.Tracks.Count, "1 song", "{0} songs") + (existing != null && !pp.Favorites ? " · " + L.T("you already have one with this name") : ""),
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
                        ? new List<Choice> { new(L.T("Into your Favorites"), true), new(L.T("As a separate playlist"), false) }
                        : new List<Choice> { new(L.T("Merge into yours"), true), new(L.T("Make a copy"), false) };
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
            inputs.Children.Add(Header(L.T("Tags"), null));
            var wrap = new WrapPanel();
            foreach (var pt in m.Tags)
            {
                var mine = PackImporter.SameName(profile, pt);
                var dot = Ui.BrushFrom(mine?.Color ?? pt.Color);
                int count = m.Tracks.Count(t => t.Tags.Contains(pt.Id));
                var chip = Chip(pt.Name, dot, count + (mine != null ? " · " + L.T("already yours") : ""), true);
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
                Content = TwoLines(m.Playlists.Count + m.Tags.Count > 0 ? L.Count(loose.Count, "One more song", "The other {0} songs") : L.Count(loose.Count, "1 song", "{0} songs"),
                    L.T("Songs of the pack that aren't in any playlist or tag.")),
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
                Content = TwoLines(L.T("Save the audio on your device"),
                    L.T("Off: the songs that are only links in the pack go into your library in the cloud, without downloading them. You listen from their link and save them whenever you like.")),
            };
            saveBox.IsCheckedChanged += (_, _) =>
            {
                cloud = saveBox.IsChecked != true;
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
            void ToggleText() => toggle.Content = listBox.IsVisible ? "▴  " + L.T("Hide the songs") : "▾  " + L.T("Show the songs");
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

        var cancel = Button(L.T("Cancel"), "TouchGhost");
        var ok = Button(L.T("Import"), "TouchPrimary");
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
            allLists.Content = listBoxes.All(b => b.IsChecked == true) ? L.T("Deselect all|f") : L.T("Select all|f");
            int Count(PackSongState s) => rows.Count(r => r.IsIncluded && r.Song.State == s);
            int add = Count(PackSongState.New), get = Count(PackSongState.Download), have = Count(PackSongState.InLibrary), miss = Count(PackSongState.Missing);
            ok.IsEnabled = chosen.Count > 0 || plan.Playlists.Count > 0 || plan.Tags.Count > 0;
            (line1.Text, line2.Text) = PackImporter.Summary(ok.IsEnabled, chosen.Count, add, get, have, miss, cloud, null);
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
            status.Text = L.T("Getting the import ready…");
            cts = new CancellationTokenSource();
            var plan = Plan();
            var report = Reporter(bar, status);
            try
            {
                if (AudioAnalysis.Native == null && Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await ensureEngines();
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
                status.Text = L.T("Import failed:") + " " + ex.Message;
            }
        };
        cancel.Click += (_, _) =>
        {
            if (busy)
            {
                cts?.Cancel();
                status.Text = L.T("Canceling…");
            }
            else sheets.Close(body);
        };
        changed();
        await sheets.Show(new ScrollViewer { Content = body }, dismissable: true, onDismiss: () => cts?.Cancel(), maxHeightShare: 0.94);
        return outcome;
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
                if (title != null) status.Text = L.F("Copying “{0}”…", title);
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
