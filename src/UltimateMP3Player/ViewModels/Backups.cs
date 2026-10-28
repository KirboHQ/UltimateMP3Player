using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.ViewModels;

// Settings › Backup: the data of the app (not the music) into a zip now and then (AppHost's update tick asks AutoAsync), in a
// folder of the user's choice; and back from one (the app starts again).
public sealed class BackupsViewModel : Observable
{
    private readonly MainViewModel _main;
    private bool _busy;

    public BackupsViewModel(MainViewModel main)
    {
        _main = main;
        BackupNowCommand = new RelayCommand(() => _ = RunAsync(false), () => !_busy);
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => !_busy);
        ChooseFolderCommand = new RelayCommand(() => _ = ChooseFolderAsync(), () => !_busy);
        DefaultFolderCommand = new RelayCommand(() => SetFolder(null), () => !_busy);
        OpenFolderCommand = new RelayCommand(() => MainViewModel.OpenFolder(LocalFolder!), () => LocalFolder != null);
    }

    private AppSettings S => _main.Host.Settings;

    public ICommand BackupNowCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand DefaultFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public List<Choice> EveryChoices { get; } = new()
    {
        new(L.T("Never"), 0), new(L.T("Every day"), 1), new(L.T("Every 3 days"), 3), new(L.T("Every week"), 7),
        new(L.T("Every 2 weeks"), 14), new(L.T("Every month"), 30),
    };

    public Choice Every
    {
        get => EveryChoices.FirstOrDefault(c => (int)c.Value! == S.BackupEvery) ?? EveryChoices[3];
        set
        {
            if (value == null) return;
            S.BackupEvery = (int)value.Value!;
            S.Save();
            OnChanged(nameof(Every), nameof(IsOn));
        }
    }

    public bool IsOn => S.BackupEvery > 0;

    public List<Choice> KeepChoices { get; } = new[] { 1, 3, 5, 10, 20 }.Select(n => new Choice(L.Count(n, "Only the last one", "The last {0}"), n)).ToList();

    public Choice Keep
    {
        get => KeepChoices.FirstOrDefault(c => (int)c.Value! == S.BackupKeep) ?? KeepChoices[2];
        set
        {
            if (value == null) return;
            S.BackupKeep = (int)value.Value!;
            S.Save();
            OnChanged(nameof(Keep));
        }
    }

    // A folder of this device (the default one, or one chosen on a computer); null for one reached through the system (phones).
    public string? LocalFolder => S.BackupFolder is { } f ? (f.StartsWith(Dialogs.BookmarkPrefix, StringComparison.Ordinal) ? null : f) : Dialogs.DefaultBackupFolder;
    public string FolderText => S.BackupFolder == null ? Dialogs.DefaultBackupFolder
        : LocalFolder ?? S.BackupFolderName ?? L.T("Chosen folder");
    public bool IsDefaultFolder => S.BackupFolder == null;

    public string LastText => S.LastBackup is { } d ? L.F("Last backup: {0}", d.ToString("g")) : L.T("No backup made yet.");

    private string? _status;
    // How the last backup made by hand went (or the one in progress).
    public string? Status { get => _status; private set => Set(ref _status, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!Set(ref _busy, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // The automatic one, when it's time (and nothing else is going on).
    public Task AutoAsync()
    {
        if (_busy || S.BackupEvery <= 0 || JsonStore.Frozen) return Task.CompletedTask;
        if (S.LastBackup is { } last && DateTime.Now - last < TimeSpan.FromDays(S.BackupEvery) && last <= DateTime.Now) return Task.CompletedTask;
        return RunAsync(true);
    }

    private void Flush()
    {
        try { _main.Library.Flush(); } catch { }
        Profile.FlushAll();
        S.Save();
    }

    private async Task<bool> RunAsync(bool auto)
    {
        if (_busy) return false;
        Busy = true;
        if (!auto) Status = L.T("Saving the backup…");
        try
        {
            Flush();
            var folder = await Dialogs.OpenBackupFolderAsync(S.BackupFolder);
            if (folder == null)
            {
                Status = L.T("The backup folder can't be reached: choose it again.");
                if (!auto) _main.Toast(Status);
                return false;
            }
            var name = Backup.FileName(DateTime.Now);
            var stream = await folder.CreateAsync(name);
            int songs = _main.Library.Count, profiles = ProfileList.Load().Profiles.Count;
            var device = DeviceName();
            await Task.Run(() => Backup.Write(stream, device, AppInfo.VersionText, songs, profiles));
            await Backup.PruneAsync(folder, S.BackupKeep);
            S.LastBackup = DateTime.Now;
            S.Save();
            Status = L.F("Backup saved: {0}", name);
            if (!auto) _main.Toast(L.T("Backup saved"));
            return true;
        }
        catch (Exception ex)
        {
            Status = L.T("The backup failed:") + " " + ex.Message;
            if (!auto) _main.Toast(Status);
            AppPaths.AppendLog($"\n\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] backup: {ex}\n");
            return false;
        }
        finally
        {
            Busy = false;
            OnChanged(nameof(LastText));
        }
    }

    private async Task ChooseFolderAsync()
    {
        if (await Dialogs.PickBackupFolderAsync() is not { } chosen) return;
        S.BackupFolderName = chosen.Name;
        SetFolder(chosen.Setting);
    }

    private void SetFolder(string? setting)
    {
        S.BackupFolder = setting;
        if (setting == null) S.BackupFolderName = null;
        S.Save();
        OnChanged(nameof(FolderText), nameof(LocalFolder), nameof(IsDefaultFolder));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task RestoreAsync()
    {
        if (await Dialogs.PickFileAsync(L.T("Choose a backup"), (L.T("Backups of Ultimate MP3 Player"), new[] { Backup.Extension })) is not { } path) return;
        if (Backup.Read(path) is not { } info)
        {
            _main.Toast(L.T("This file isn't a backup of Ultimate MP3 Player."));
            return;
        }
        var what = L.F("Backup of {0}", info.Created.ToString("g")) + (info.Device is { Length: > 0 } d ? " · " + d : "") + "\n" +
                   L.Count(info.Songs, "1 song", "{0} songs") + " · " + L.Count(info.Profiles, "1 profile", "{0} profiles");
        if (!await Dialogs.ConfirmAsync(L.T("Restore this backup?"),
                what + "\n\n" + L.T("Your library, playlists, tags, statistics and settings of today are replaced by the backup's (a copy of today's is saved first). The music files stay where they are. Then the app starts again."),
                L.T("Restore"), true))
            return;
        Busy = true;
        Status = L.T("Restoring…");
        try
        {
            // Today's data first, so the restore can be undone.
            Flush();
            if (await Dialogs.OpenBackupFolderAsync(S.BackupFolder) is { } folder)
                try
                {
                    var stream = await folder.CreateAsync(Backup.FileName(DateTime.Now, beforeRestore: true));
                    int songs = _main.Library.Count, profiles = ProfileList.Load().Profiles.Count;
                    var device = DeviceName();
                    await Task.Run(() => Backup.Write(stream, device, AppInfo.VersionText, songs, profiles));
                }
                catch { }
            var current = S;
            await Task.Run(() => Backup.Restore(path, current));
        }
        catch (Exception ex)
        {
            Busy = false;
            Status = L.T("The restore failed:") + " " + ex.Message;
            _main.Toast(Status);
            return;
        }
        _main.Host.RestartAfterRestore();
    }

    // The name the backups give for where they come from (the phones': the one in their settings).
    private static string DeviceName()
    {
#if ANDROID_APP
        try
        {
            if (Android.Provider.Settings.Global.GetString(Android.App.Application.Context.ContentResolver, "device_name") is { Length: > 0 } n) return n;
        }
        catch { }
        return Android.OS.Build.Model ?? "Android";
#elif IOS_APP
        try { return UIKit.UIDevice.CurrentDevice.Name; }
        catch { return "iPhone"; }
#else
        return Environment.MachineName;
#endif
    }
}
