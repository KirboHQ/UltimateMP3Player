using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Foundation;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// What the installer brought from the computer's app (Core FromComputer): asked about as the app opens and comes back in front, until it's
// added or deleted; a row of Settings meanwhile.
public static class FromComputerSheet
{
    private const string Computer = "\uE7F4", Tick = "\uE73E", Need = "\uE7BA";

    private static readonly string Folder = Path.Combine(NSSearchPath.GetDirectories(NSSearchPathDirectory.LibraryDirectory, NSSearchPathDomain.User)[0], "FromComputer");

    // What's there changed (on the screen's thread).
    private static event Action? Changed;

    // The one asked about since the app started: "Not now" holds until it starts again, another one that arrives is asked about.
    private static string? _asked;
    private static bool _busy;

    // The app opened, or came back in front.
    public static async void Shown()
    {
        try
        {
            Renewal.KeepOutOfBackups(Folder);
            Changed?.Invoke();
            if (App.Host.Session is { InRoom: false } && !RenewalNotice.Open) await AskAsync(false);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static async Task AskAsync(bool byHand)
    {
        var (there, ready) = FromComputer.Look(Folder);
        if (!there || _busy || App.Host.View is not { IsReady: true } || App.Host.Session is not { } session) return;
        var which = ready?.Id ?? File.GetLastWriteTimeUtc(Path.Combine(Folder, FromComputer.ReadyName)).Ticks.ToString();
        if (!byHand && _asked == which) return;
        _asked = which;

        if (ready == null) await RenewalNotice.ShowAsync(Unreadable(null));
        else if (ready.Newer)
            await RenewalNotice.ShowAsync(new(L.T("NEEDED"), Need, "Warning", L.T("Update the app"),
                L.T("What came from the computer is for a newer version of the app: update it, then it can be added. It stays here meanwhile."), Away: L.T("Delete it"), Discard: () => Discard(ready.Id)));
        else if (await RenewalNotice.ShowAsync(Ask(ready)) && await session.ConfirmLeaveRoomAsync(L.T("Adding it opens the profile again, so you'll leave the room."), L.T("Leave and add it")))
            await RunAsync(session, ready);
    }

    // ------------------------------------------------------------------ what's said

    internal static RenewalNotice.Notice Ask(ComputerReady ready)
    {
        var parts = new List<string>();
        if (ready.WithSongs)
        {
            parts.Add(L.Count(ready.Songs, "1 song", "{0} songs"));
            parts.Add(L.Count(ready.Playlists, "1 playlist", "{0} playlists"));
            if (ready.Files > 0) parts.Add(L.Count(ready.Files, "1 file", "{0} files") + " (" + Text.Size(ready.Bytes) + ")");
        }
        if (ready.WithStats) parts.Add(L.T("statistics"));
        if (ready.WithLook) parts.Add(L.T("look"));
        if (ready.WithSettings) parts.Add(L.T("settings"));
        var name = ready.Computer?.Trim() ?? "";
        return new(L.T("YOUR DECISION"), Computer, "Accent", L.T("From your computer"),
            (name.Length > 0 ? L.F("From {0}:", name.Length > 40 ? name[..40] : name) + "\n" : "") + string.Join("\u00A0· ", parts) + "\n\n" + L.T("It's added to what's on the iPhone: nothing is deleted.")
            + (ready.WithSettings ? " " + L.T("The settings become the computer's.") : "") + (ready.WithLook ? " " + L.T("The look you have now stays among the looks.") : ""),
            L.T("Add it all"), Away: L.T("Delete it"), Discard: () => Discard(ready.Id));
    }

    internal static RenewalNotice.Notice Done(ComputerResult r) => new(L.T("DONE"), Tick, "Success", L.T("Added to your iPhone"), string.Join("\n\n", r.Report()));

    private static RenewalNotice.Notice Unreadable(string? id) => Failed(L.T("It can't be read"),
        L.T("What arrived from the computer can't be read: send it again from there. If it happens again, update the app on both."), id);

    internal static RenewalNotice.Notice Failed(string title, string text, string? id)
        => new(L.T("NOT ADDED"), Need, "Warning", title, text, Away: L.T("Delete it"), Discard: () => Discard(id));

    private static async void Discard(string? id)
    {
        try
        {
            if (!await Dialogs.ConfirmAsync(L.T("Delete what came from the computer?"), L.T("Nothing of what's in the app changes. The installer can send it again."), L.T("Delete"), true)) return;
            FromComputer.Delete(Folder, id);
        }
        catch (Exception ex) { App.Log(ex); }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ adding it

    private static async Task RunAsync(MainViewModel session, ComputerReady ready)
    {
        var host = App.Host;
        _busy = true;
        var (show, close) = RenewalNotice.ShowWorking(new(L.T("DON'T CLOSE THE APP"), Computer, "Accent", L.T("From your computer"), L.T("If it's closed, it goes on from where it was the next time.")));
        show(L.T("Adding…"), 0);
        RenewalNotice.Notice outcome;
        int shown = 0;
        try
        {
            await session.Backups.SafetyCopyAsync();
            session.Radio.Save();
            var result = await FromComputer.RunAsync(Folder, ready, host.Library, host.Profiles, session.Profile.Info, host.Settings.MusicDir, Playable,
                file => AudioAnalysis.Native?.ProbeVideo(file) != null,
                (profile, songs) =>
                {
                    if (profile == session.Profile) session.Radio.Bring(songs);
                    else profile.Data.Radio = profile.Data.Radio.Concat(songs).ToList();
                },
                part =>
                {
                    int now = (int)(part * 100);
                    if (Interlocked.Exchange(ref shown, now) != now) Dispatcher.UIThread.Post(() => show(L.T("Adding…"), part));
                });
            string? language = host.Settings.Language;
            FromComputer.Apply(result, session.Profile, host.Settings);
            host.SaveAll();
            FromComputer.Delete(Folder, ready.Id);
            close();
            // (everything of the profile read again: its name, its songs, its look)
            SettingsTransfer.Reopen(session, language);
            outcome = Done(result);
        }
        catch (Exception ex)
        {
            close();
            App.Log(ex);
            outcome = ex is InvalidDataException ? Unreadable(ready.Id) : Failed(L.T("Something didn't work"), L.T("What was added stays, the rest is still here: try again from Settings.") + "\n\n" + ex.Message, ready.Id);
        }
        _busy = false;
        Changed?.Invoke();
        await RenewalNotice.ShowAsync(outcome);
    }

    private static bool Playable(string file)
    {
        try { return AudioAnalysis.Native is { } native && native.CanDecode(Codecs.Family(native.ProbeAsync(file, CancellationToken.None).GetAwaiter().GetResult().Audio?.Codec)); }
        catch { return false; }
    }

    // ------------------------------------------------------------------ Settings

    // At the top of the list, only while there's something to add.
    public static void AddRow(Panel groups)
    {
        var words = new StackPanel
        {
            Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = L.T("From your computer"), Classes = { "setting" } }, new TextBlock { Text = L.T("Waiting to be added"), Classes = { "value" }, Foreground = RenewalNotice.Strong("Warning") } },
        };
        Grid.SetColumn(words, 1);
        var row = new Button
        {
            Theme = Ui.Theme("SettingRow"), Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { RenewalNotice.Badge(Computer, "Accent", 34), words } },
        };
        row.Click += async (_, _) =>
        {
            try { await AskAsync(true); }
            catch (Exception ex) { App.Log(ex); }
        };
        var line = new Border { Classes = { "line" } };
        groups.Children.InsertRange(0, new Control[] { row, line });
        void Refresh() => row.IsVisible = line.IsVisible = FromComputer.Look(Folder).There;
        // (only while the page is in the window, as RenewalCards)
        groups.AttachedToVisualTree += (_, _) =>
        {
            Changed -= Refresh;
            Changed += Refresh;
            Refresh();
        };
        groups.DetachedFromVisualTree += (_, _) => Changed -= Refresh;
        Refresh();
    }
}
