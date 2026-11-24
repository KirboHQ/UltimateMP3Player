using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Settings › Backup, export and import: the settings (Core SettingsFile) into a file and back, or straight from the device linked. The
// ticks choose the groups; all of them = the whole profile's way of being. Coming in, the profile opens again with them (the music goes
// on from where it was): every part of the app reads its settings as it starts.
public static class SettingsTransfer
{
    // (the phones have no Discord: their settings for it would only undo a computer's)
    private static readonly (string Key, string Name)[] Groups = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? SettingsFile.All.Where(g => g.Key != "discord").ToArray() : SettingsFile.All;

    public static SettingsFile Everything(MainViewModel s) => Make(s, Groups.Select(g => g.Key));

    private static SettingsFile Make(MainViewModel s, IEnumerable<string> groups)
    {
        // (the look's preset as it is now)
        s.Settings.Looks.Keep();
        return SettingsFile.Make(s.Profile.Data, s.Host.Settings, s.Profile.Info.Id, s.Profile.Info.Name, BackupsViewModel.DeviceName(), s.Profile.BackdropPicture, groups);
    }

    public static async Task ExportAsync(MainViewModel s)
    {
        var groups = Groups;
        var ticks = await Dialogs.ChecksAsync(L.T("Export settings"), L.T("What goes into the file (all of it: the whole profile, its look and its looks too; not the music, playlists or plays, which are the backups')."),
            groups.Select(g => (L.T(g.Name), true)).ToArray(), L.T("Export"));
        if (ticks == null || !ticks.Any(t => t)) return;
        var file = Make(s, groups.Where((_, i) => ticks[i]).Select(g => g.Key));
        var name = Text.SafeFileName(L.F("{0} - settings", s.Profile.Info.Name)) + SettingsFile.Extension;
        if (await Dialogs.SaveTextAsync(name, SettingsFile.Extension, file.ToJson())) s.Toast(L.T("Settings exported"));
    }

    public static async Task ImportAsync(MainViewModel s)
    {
        var path = await Dialogs.PickFileAsync(L.T("Import settings"), (L.T("Settings"), new[] { SettingsFile.Extension }));
        if (path == null) return;
        SettingsFile? file = null;
        try { file = SettingsFile.TryRead(await System.IO.File.ReadAllTextAsync(path)); }
        catch (Exception ex) { App.Log(ex); }
        if (file == null)
        {
            s.Toast(L.T("This file isn't settings of Ultimate MP3 Player."));
            return;
        }
        await ImportAsync(s, file, file.Profile is { Length: > 0 } p ? $"{p} ({file.Device})" : file.Device ?? "");
    }

    // The groups the file has, ticked, then into this profile.
    public static async Task ImportAsync(MainViewModel s, SettingsFile file, string from)
    {
        var groups = Groups.Where(g => file.Has.Contains(g.Key)).ToArray();
        if (groups.Length == 0) return;
        var ticks = await Dialogs.ChecksAsync(L.T("Import settings"), L.F("From {0}, into the profile {1}: what to take. The look comes in as a look of its own.", from, s.Profile.Info.Name),
            groups.Select(g => (L.T(g.Name), true)).ToArray(), L.T("Import"));
        if (ticks == null || !ticks.Any(t => t)) return;
        var chosen = groups.Where((_, i) => ticks[i]).Select(g => g.Key).ToList();
        var host = s.Host;
        string? language = host.Settings.Language;
        s.Settings.Looks.Keep();
        file.Apply(s.Profile.Data, host.Settings, s.Profile.Info.Id, s.Profile.BackdropPicture, chosen);
        Reopen(s, language)?.Toast(L.T("Settings imported"));
    }

    // The profile opened again with what came in, the music going on; language: the one until now.
    internal static MainViewModel? Reopen(MainViewModel s, string? language)
    {
        var host = s.Host;
        host.Settings.Save();
        s.Profile.Save();
        bool playing = s.Player.IsPlaying;
        // (linked: the profile opened again ends the connection; the new one takes it up again. The computer connects to the phone
        // once more, the phone waits for the computer again, which tries again by itself)
        bool phone = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
        var linkedPhone = phone ? null : s.PhoneLink.LinkedPhone;
        bool linkedComputer = phone && s.Shared != null && s.ComputerLink.IsLinked;
        if (host.Settings.Language != language && host.Settings.Language is { } code)
        {
            host.Settings.Language = language;
            host.SetLanguage(code);
        }
        if (linkedComputer) s.ComputerLink.Drop();
        host.OpenProfile(s.Profile.Info);
        if (host.Session is not { } next) return null;
        if (playing) next.SetPlaying(true);
        if (linkedPhone != null) _ = next.PhoneLink.Connect(linkedPhone);
        if (linkedComputer) next.ComputerLink.Open();
        return next;
    }

    // "Copy from the computer/phone" (only while linked).
    public static void AskLinked(MainViewModel s)
    {
        if (s.Shared is { } shared) shared.AskSettings();
        else s.Toast(L.T("Connect the phone and the computer first."));
    }
}
