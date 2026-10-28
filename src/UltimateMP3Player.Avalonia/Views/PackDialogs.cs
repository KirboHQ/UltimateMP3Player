using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Path = System.IO.Path;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// .ump packs: export playlists, tags or the whole library into one file, and import one (what's new, what's already here).
public static class PackDialogs
{
    private static IBrush B(string key) => Ui.Res(key);

    private const string MusicGlyph = "";
    private const string LinkGlyph = "";

    private static Bitmap? _packImage;
    private static Bitmap PackImage => _packImage ??= new Bitmap(AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/pack-256.png")));

    // ================================================================== export

    // A playlist or a tag to choose, as the dialog shows it.
    public sealed record ExportList(Playlist P, string Name, string CountText, Func<Control> Cover);
    public sealed record ExportTag(Tag T, IBrush Brush, int Count);

    // many: several playlists chosen together (selected on Home).
    public static void Export(MainViewModel main, PlaylistViewModel? playlist = null, TagViewModel? tag = null, IReadOnlyCollection<PlaylistViewModel>? many = null)
    {
        var lists = main.Playlists.Where(p => !p.IsOther).Select(p => new ExportList(p.P, p.Name, p.CountText,
            () => new ContentControl { Content = p, ContentTemplate = Ui.Find<IDataTemplate>("PlaylistCover") })).ToList();
        var tags = main.Tags.Where(t => !main.IsTheirs(t.Id)).Select(t => new ExportTag(t.T, t.Brush, t.Count)).ToList();
        var chosen = (many ?? Array.Empty<PlaylistViewModel>()).Select(p => p.P).ToList();
        if (playlist != null) chosen.Add(playlist.P);
        BuildExport(main.Library, main.Profile, lists, tags, chosen, tag?.T, main.PhoneLinked ? main.PhoneLink.SendPack : null).ShowDialog();
    }

    // toPhone: "To the phone", with a phone connected (Connect phone): true when it went.
    public static DialogWindow BuildExport(Library lib, Profile profile, List<ExportList> lists, List<ExportTag> tags, IReadOnlyCollection<Playlist> chosen, Tag? tag,
        Func<PackExport, bool>? toPhone = null)
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
        inputs.Children.Add(Hint(L.T("Choose what goes into the pack: a .ump file to keep as a backup or to send to a friend. It opens with a double click (or by dragging it into the window) and the app adds everything, without duplicates.")));

        // ---- playlists
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        inputs.Children.Add(Header(L.T("Playlists"), lists.Count > 1 ? allLists : null, 16));
        var listPanel = new StackPanel();
        foreach (var p in lists)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            var cover = p.Cover();
            cover.Width = cover.Height = 34;
            row.Children.Add(cover);
            row.Children.Add(At(new TextBlock { Text = p.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) }, 1));
            row.Children.Add(At(new TextBlock { Text = p.CountText, FontSize = 12, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center }, 2));
            var cb = new CheckBox { IsChecked = pickedLists.Contains(p), Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            cb.IsCheckedChanged += (_, _) =>
            {
                if (cb.IsChecked == true) pickedLists.Add(p);
                else pickedLists.Remove(p);
                changed();
            };
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
            inputs.Children.Add(Header(L.T("Tags"), tags.Count > 1 ? allTags : null));
            var wrap = new WrapPanel();
            foreach (var t in tags)
            {
                var chip = Chip(t.T.Name, t.Brush, t.Count.ToString(), pickedTags.Contains(t));
                ToolTip.SetTip(chip, L.F("The tag and its {0}", L.Count(t.Count, "1 song", "{0} songs")));
                chip.PointerPressed += (_, e) =>
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
        inputs.Children.Add(Label(L.T("More"), tags.Count > 0 ? 10 : 18));
        var wholeBox = new CheckBox
        {
            Content = TwoLines(L.T("The whole library"),
                L.F("Also the songs that aren't in any playlist or tag ({0} in all): handy to move everything to another computer.", lib.Count)),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        wholeBox.IsCheckedChanged += (_, _) => { whole = wholeBox.IsChecked == true; changed(); };
        inputs.Children.Add(wholeBox);

        // ---- with the music or links only
        inputs.Children.Add(Label(L.T("Songs")));
        var (modeBar, withFiles, linksOnly) = Segmented(L.T("With the audio files"), MusicGlyph, L.T("Links only"), LinkGlyph);
        linksOnly.IsChecked = true;
        inputs.Children.Add(modeBar);
        var modeHint = Hint("", 8);
        inputs.Children.Add(modeHint);
        var videoBox = new CheckBox { Content = L.T("Include the videos of the songs that have one"), Margin = new Thickness(0, 12, 0, 0) };
        inputs.Children.Add(videoBox);
        withFiles.IsCheckedChanged += (_, _) => changed();
        linksOnly.IsCheckedChanged += (_, _) => changed();
        videoBox.IsCheckedChanged += (_, _) => changed();

        // ---- what it makes
        var (summary, line1, line2) = Summary();
        body.Children.Add(summary);
        var (progress, bar, status) = Progress();
        body.Children.Add(progress);

        var cancel = Dialogs.Button(L.T("Cancel"), "GhostButton", isCancel: true);
        var ok = Dialogs.Button(L.T("Export…"), "PrimaryButton", isDefault: true);
        // The same songs as plain MP3s in a folder, for a USB stick, an SD card or the car (FolderExport).
        var folder = Dialogs.Button(L.T("To a folder (MP3)…"), "GhostButton");
        ToolTip.SetTip(folder, L.T("For a USB stick, an SD card or the car stereo: one MP3 file per song, named “Artist - Title”, with title, artist, album and cover in its tags. Songs in the cloud are downloaded."));
        // Straight to the connected phone, without a file: it chooses there what to import.
        var phone = Dialogs.Button(L.T("To the phone"), "GhostButton");
        ToolTip.SetTip(phone, L.T("The connected phone opens its import window and takes the songs it doesn't have, through the network or the cable."));
        // While it writes the dialog can go: the export goes on in the downloads panel (BackgroundExport).
        var background = Dialogs.Button(L.T("In the background"), "GhostButton");
        ToolTip.SetTip(background, L.T("The export goes on in the downloads panel, with its bar and its Cancel: you can keep using the app."));
        background.IsVisible = false;
        var win = toPhone != null ? Dialogs.Frame(L.T("Export to a pack"), Fit(body), 640, background, folder, phone, cancel, ok)
            : Dialogs.Frame(L.T("Export to a pack"), Fit(body), 560, background, folder, cancel, ok);

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
            allTags.Content = pickedTags.Count == tags.Count ? L.T("Deselect all|m") : L.T("Select all|m");
            modeHint.Text = e.Music
                ? L.T("The music is inside the pack: it imports even without internet and with songs gone from the sites. It's as big as the songs.")
                : L.T("A tiny pack, perfect to send on Discord: whoever opens it downloads the songs again from their links. Those without a link (your own files, DJ mixes) still travel as files.");
            videoBox.IsVisible = e.Music && songs.Any(t => t.HasVideo);
            ok.IsEnabled = folder.IsEnabled = phone.IsEnabled = !e.IsEmpty;
            (line1.Text, line2.Text) = PackWriter.Summary(songs, e, SizeOf);
        }
        changed = Update;

        // 0 = choosing, 1 = writing, 2 = done.
        int stage = 0;
        string? saved = null;
        bool savedFolder = false;
        CancellationTokenSource? cts = null;
        BackgroundExport? bg = null;
        // (while it writes, "In the background" takes the place of the other ways out)
        void Working(bool on)
        {
            background.IsVisible = on;
            folder.IsVisible = !on;
            phone.IsVisible = !on;
        }
        ok.Click += async (_, _) =>
        {
            if (stage == 2)
            {
                if (savedFolder) MainViewModel.OpenFolder(saved!);
                else Ui.ShowInFolder(saved!);
                win.DialogResult = true;
                return;
            }
            if (stage != 0) return;
            var e = Current();
            if (e.IsEmpty) return;
            var file = Dialogs.PickSave(L.T("Save the pack"), PackWriter.FileName(profile, e), L.T("Ultimate MP3 Player pack"), Pack.Extension);
            if (file == null) return;
            var path = Pack.IsPack(file) ? file : file + Pack.Extension;

            stage = 1;
            inputs.IsEnabled = false;
            ok.IsEnabled = false;
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparing the pack…");
            cts = new CancellationTokenSource();
            var report = Reporter(bar, status);
            bg = new BackgroundExport(App.Host, L.F("Exporting “{0}”", System.IO.Path.GetFileName(path)), status.Text, cts);
            report += bg.Report;
            Working(true);
            try
            {
                var m = await Task.Run(() => PackWriter.WriteAsync(path, lib, profile, e, AppInfo.VersionText, report, cts.Token));
                stage = 2;
                saved = path;
                bar.Value = 1;
                status.Foreground = B("SuccessBrush");
                status.Text = "✓ " + L.F("Pack created: {0} ({1})", Path.GetFileName(path), Text.Size(PackWriter.SizeOf(path)));
                line1.Text = L.Count(m.Tracks.Count, "1 song", "{0} songs") + " · " + L.Count(m.Playlists.Count, "1 playlist", "{0} playlists") +
                             " · " + L.Count(m.Tags.Count, "1 tag", "{0} tags");
                ok.Content = L.T("Show in folder");
                bg?.Done(status.Text);
                background.IsVisible = false;
                ok.IsEnabled = true;
                cancel.Content = L.T("Close");
            }
            catch (OperationCanceledException)
            {
                stage = 0;
                bg?.Done(null);
                if (bg?.Hidden != true) win.DialogResult = false;
            }
            catch (Exception ex)
            {
                stage = 0;
                inputs.IsEnabled = true;
                ok.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Export failed:") + " " + ex.Message;
                bg?.Done(status.Text);
                Working(false);
            }
        };
        phone.Click += (_, _) =>
        {
            var e = Current();
            if (stage == 0 && !e.IsEmpty && toPhone?.Invoke(e) == true) win.DialogResult = true;
        };
        background.Click += (_, _) =>
        {
            if (stage != 1 || bg == null) return;
            bg.Hidden = true;
            win.DialogResult = false;
        };
        folder.Click += async (_, _) =>
        {
            var e = Current();
            if (stage != 0 || e.IsEmpty) return;
            if (Dialogs.PickFolder(L.T("Where to put the MP3s (a USB stick, an SD card…)")) is not { } dir) return;
            var songs = PackWriter.Songs(lib, profile, e);
            stage = 1;
            inputs.IsEnabled = false;
            ok.IsEnabled = folder.IsEnabled = false;
            progress.IsVisible = true;
            status.Foreground = B("SubTextBrush");
            status.Text = L.T("Preparing the MP3s…");
            cts = new CancellationTokenSource();
            var report = Reporter(bar, status);
            bg = new BackgroundExport(App.Host, L.F("Exporting “{0}”", dir), status.Text, cts);
            report += bg.Report;
            Working(true);
            var cookies = App.Host?.Settings.CookiesBrowserOrNull;
            try
            {
                var r = await Task.Run(() => FolderExport.WriteAsync(songs, cookies, (file, name, _) =>
                {
                    File.Copy(file, Path.Combine(dir, name), true);
                    return Task.CompletedTask;
                }, report, cts.Token));
                stage = 2;
                saved = dir;
                savedFolder = true;
                bar.Value = 1;
                status.Foreground = B(r.Written > 0 ? "SuccessBrush" : "WarningBrush");
                status.Text = "✓ " + L.F("{0} in “{1}”", L.Count(r.Written, "1 MP3", "{0} MP3s"), dir) +
                              (r.Skipped > 0 ? " · " + L.Count(r.Skipped, "1 skipped (no audio and no working link)", "{0} skipped (no audio and no working link)") : "");
                ok.Content = L.T("Show in folder");
                bg?.Done(status.Text);
                background.IsVisible = false;
                ok.IsEnabled = true;
                cancel.Content = L.T("Close");
            }
            catch (OperationCanceledException)
            {
                stage = 0;
                bg?.Done(null);
                if (bg?.Hidden != true) win.DialogResult = false;
            }
            catch (Exception ex)
            {
                stage = 0;
                inputs.IsEnabled = true;
                ok.IsEnabled = folder.IsEnabled = true;
                status.Foreground = B("ErrorBrush");
                status.Text = L.T("Export failed:") + " " + ex.Message;
                bg?.Done(status.Text);
                Working(false);
            }
        };
        // Closing while it writes: it stops (the half-written file is deleted), then the dialog goes.
        win.Closing += (_, e) =>
        {
            if (stage != 1 || bg?.Hidden == true) return;
            e.Cancel = true;
            cts?.Cancel();
            status.Text = L.T("Canceling…");
        };
        changed();
        return win;
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
            BuildImport(pack, songs, main.Library, main.Profile, main.Host.Settings.MusicDir, main.Host.EnsureEnginesAsync, r => result = r, main.Host.Settings).ShowDialog();
        }
        if (result == null) return;

        main.QueuePackDownloads(result);
        main.Toast(result.Report());

        // One playlist (or only one tag): straight to it, once the lists have caught up.
        var ids = result.PlaylistIds.Distinct().ToList();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (ids.Count == 1 && main.Playlists.FirstOrDefault(p => p.Id == ids[0]) is { } pl) main.OpenPlaylist(pl);
    }

    // done: what was imported (the dialog closes by itself right after).
    public static DialogWindow BuildImport(PackFile pack, List<PackSong> songs, Library lib, Profile profile, string musicDir, Func<Task> ensureEngines,
        Action<PackImportResult> done, AppSettings? settings = null)
    {
        // The songs that travelled as links: downloaded, or into the library in the cloud (as Settings › Download say).
        bool cloud = settings is not { SaveAudio: true };
        var m = pack.Manifest;
        var rows = songs.Select(s => new PackSongRow(s, pack)).ToList();
        var pickedLists = m.Playlists.Select(p => p.Id).ToHashSet();
        var merge = m.Playlists.ToDictionary(p => p.Id, _ => true);
        var pickedTags = m.Tags.Select(t => t.Id).ToHashSet();
        var loose = PackImporter.Loose(m);
        bool others = loose.Count > 0;
        // Every choice refreshes the songs and the summary (set once all the parts exist).
        Action changed = () => { };

        var body = new StackPanel();

        // ---- the pack: icon, name, who made it
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        head.Children.Add(new Image { Source = PackImage, Width = 58, Height = 58 });
        var info = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = pack.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        info.Children.Add(new TextBlock { Text = m.Credits(pack.FileSize), FontSize = 12.5, Foreground = B("SubTextBrush"), Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
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

        // ---- playlists: new, or merged into the one with the same name
        var listBoxes = new List<CheckBox>();
        var allLists = Link();
        if (m.Playlists.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Playlists"), m.Playlists.Count > 1 ? allLists : null, 12));
            var listPanel = new StackPanel();
            foreach (var pp in m.Playlists)
            {
                var existing = PackImporter.SameName(profile, pp);
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                row.Children.Add(PlaylistCover(pack, pp));
                var names = new StackPanel { Margin = new Thickness(12, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = pp.Favorites ? L.T("Favorites") : pp.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(new TextBlock
                {
                    Text = L.Count(pp.Tracks.Count, "1 song", "{0} songs") + (existing != null && !pp.Favorites ? " · " + L.T("you already have one with this name") : ""),
                    FontSize = 12, Foreground = B("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
                });
                row.Children.Add(At(names, 1));
                var cb = new CheckBox { IsChecked = true, Content = row };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (cb.IsChecked == true) pickedLists.Add(pp.Id);
                    else pickedLists.Remove(pp.Id);
                    changed();
                };
                listBoxes.Add(cb);

                // What happens to it, on the right (outside the tick box).
                var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                line.Children.Add(cb);
                if (existing != null)
                {
                    var choices = pp.Favorites
                        ? new List<Choice> { new(L.T("Into your Favorites"), true), new(L.T("As a separate playlist"), false) }
                        : new List<Choice> { new(L.T("Merge into yours"), true), new(L.T("Make a copy"), false) };
                    var combo = new ComboBox { ItemsSource = choices, SelectedIndex = 0, Width = 190, Height = 32, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                    ToolTip.SetTip(combo, pp.Favorites
                        ? L.T("The songs are added to your Favorites, or become a new playlist.")
                        : L.T("The missing songs are added to your playlist (without duplicates), or another one is created."));
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is Choice c) merge[pp.Id] = (bool)c.Value!; };
                    line.Children.Add(At(combo, 1));
                }
                else line.Children.Add(At(new TextBlock { Text = L.T("New|f"), FontSize = 12, Foreground = B("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }, 1));
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
        var tagChips = new List<(PackTag T, IBrush Dot, Border Chip)>();
        var allTags = Link();
        if (m.Tags.Count > 0)
        {
            inputs.Children.Add(Header(L.T("Tags"), m.Tags.Count > 1 ? allTags : null));
            var wrap = new WrapPanel();
            foreach (var pt in m.Tags)
            {
                var mine = PackImporter.SameName(profile, pt);
                var dot = Ui.BrushFrom(mine?.Color ?? pt.Color);
                int count = m.Tracks.Count(t => t.Tags.Contains(pt.Id));
                var chip = Chip(pt.Name, dot, count + (mine != null ? " · " + L.T("already yours") : ""), true);
                ToolTip.SetTip(chip, mine != null
                    ? L.T("You already have a tag with this name: the songs get yours.")
                    : L.F("A new tag, on its {0}", L.Count(count, "1 song", "{0} songs")));
                chip.PointerPressed += (_, e) =>
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
                IsChecked = !cloud,
                Margin = new Thickness(0, 12, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Top,
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

        // ---- every song, with what happens to it
        if (rows.Count > 0)
        {
            var toggle = Link();
            toggle.Margin = new Thickness(2, 16, 0, 0);
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            var list = new ListBox { Theme = Ui.Theme("PlainList"), ItemsSource = rows, ItemTemplate = Ui.Find<IDataTemplate>("PackSongTemplate") };
            var listBox = new Border
            {
                Height = 236, CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"),
                BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 8, 0, 0), Child = list, IsVisible = false,
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

        var cancel = Dialogs.Button(L.T("Cancel"), "GhostButton", isCancel: true);
        var ok = Dialogs.Button(L.T("Import"), "PrimaryButton", isDefault: true);
        var win = Dialogs.Frame(L.T("Import a pack"), Fit(body), 580, cancel, ok);

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
            allTags.Content = pickedTags.Count == m.Tags.Count ? L.T("Deselect all|m") : L.T("Select all|m");
            int Count(PackSongState s) => rows.Count(r => r.IsIncluded && r.Song.State == s);
            int add = Count(PackSongState.New), get = Count(PackSongState.Download), have = Count(PackSongState.InLibrary), miss = Count(PackSongState.Missing);
            ok.IsEnabled = chosen.Count > 0 || plan.Playlists.Count > 0 || plan.Tags.Count > 0;
            (line1.Text, line2.Text) = PackImporter.Summary(ok.IsEnabled, chosen.Count, add, get, have, miss, cloud, musicDir);
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
                if (Engines.Missing().Contains(Path.GetFileName(Engines.Ffmpeg))) await ensureEngines();
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
                status.Text = L.T("Import failed:") + " " + ex.Message;
            }
        };
        // Closing while it copies: it stops (the songs already copied stay), then the dialog goes.
        win.Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            cts?.Cancel();
            status.Text = L.T("Canceling…");
        };
        changed();
        return win;
    }

    // The playlist's own picture, the cover of its first song in the pack, the Favorites heart or a note.
    private static Control PlaylistCover(PackFile pack, PackPlaylist p)
    {
        var grid = new Panel { Width = 34, Height = 34 };
        var glyph = new TextBlock
        {
            Text = Icons.Map(p.Favorites ? "" : MusicGlyph), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = p.Favorites ? Brushes.White : B("MutedBrush"),
        };
        glyph.Classes.Add("glyph");
        if (p.Favorites) glyph.Classes.Add("filled");
        var back = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = p.Favorites ? Ui.Res("FavoritesGradient") : B("PlaceholderGradient"),
            Child = glyph,
        };
        grid.Children.Add(back);
        if (p.Favorites) return grid;
        var image = new Border { CornerRadius = new CornerRadius(6), IsVisible = false };
        grid.Children.Add(image);
        _ = Load();
        return grid;

        async Task Load()
        {
            var entry = p.HasCover ? Pack.PlaylistCoverEntry(p.Id) : p.Tracks.Select(Pack.CoverEntry).FirstOrDefault(pack.Has);
            if (entry == null || await PackImages.LoadAsync(pack, entry, 72) is not Bitmap img) return;
            image.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            image.IsVisible = true;
        }
    }

    // ================================================================== pieces

    // On a short screen the dialog scrolls instead of going past the bottom (title and buttons stay).
    private static ScrollViewer Fit(Control body)
    {
        var screen = (DialogWindow.ActiveOwner() as TopLevel)?.Screens?.Primary;
        double height = screen != null ? screen.WorkingArea.Height / screen.Scaling : 900;
        return new ScrollViewer { Content = body, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(360, height - 200) };
    }

    // Progress from a worker thread, a few times per percent at most.
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

    private static TextBlock Label(string text, double top = 18) => Ui.Text(text, "field-label", new Thickness(2, top, 0, 8));

    private static TextBlock Hint(string text, double top = 0) => Ui.Text(text, "hint", new Thickness(0, top, 0, 0));

    private static StackPanel TwoLines(string title, string hint)
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        s.Children.Add(Ui.Text(hint, "hint"));
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
        var label = Ui.Text(text, "field-label", new Thickness(2, 0, 0, 0));
        label.VerticalAlignment = VerticalAlignment.Center;
        dock.Children.Add(label);
        return dock;
    }

    private static Button Link() => new() { Theme = Ui.Theme("LinkButton"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    // Rows in a dark box that scrolls when long.
    private static Border Box(Control content, double maxHeight) => new()
    {
        CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1),
        Padding = new Thickness(4),
        Child = new ScrollViewer { Content = content, MaxHeight = maxHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
    };

    private static Border HoverRow(Control child)
    {
        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 5, 8, 5), Background = Brushes.Transparent, Child = child };
        row.PointerEntered += (_, _) => row.Background = B("SurfaceBrush");
        row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    // A tag that can be ticked: dot, name, how many songs.
    private static Border Chip(string name, IBrush dot, string? extra, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = dot, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = name, FontSize = 13, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(extra))
            row.Children.Add(new TextBlock { Text = extra, FontSize = 12, Foreground = B("MutedBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border
        {
            CornerRadius = new CornerRadius(15), Padding = new Thickness(11, 5, 13, 5), Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1.5), Cursor = Ui.Hand, Child = row,
        };
        SetChip(chip, dot, on);
        return chip;
    }

    private static void SetChip(Border chip, IBrush dot, bool on)
    {
        chip.Background = on ? B("AccentSoftBrush") : B("Surface3Brush");
        chip.BorderBrush = on ? dot : Brushes.Transparent;
        chip.Opacity = on ? 1 : 0.75;
    }

    // A small rounded label with a coloured dot.
    private static Border Pill(string text, IBrush? dot)
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

    // The box at the bottom: the pack icon and what will happen.
    private static (Border Box, TextBlock Line1, TextBlock Line2) Summary()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(new Image { Source = PackImage, Width = 38, Height = 38, VerticalAlignment = VerticalAlignment.Center });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var line1 = new TextBlock { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
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
        var panel = new StackPanel { Margin = new Thickness(0, 14, 0, 0), IsVisible = false };
        var status = new TextBlock { FontSize = 12.5, Foreground = B("SubTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(status);
        panel.Children.Add(bar);
        return (panel, bar, status);
    }
}
