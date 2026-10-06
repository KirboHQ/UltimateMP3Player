using System.Diagnostics;
using Avalonia.Platform;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// Linux: the app in the applications menu (name, icon: also what GNOME and KDE show next to the song in the media
// controls) and ".ump" packs that open with it, with their icon. Per user, written at every start if missing or
// pointing at another copy, like the Windows app's registry keys. macOS: the .app's Info.plist does all this.
public static class FileAssociation
{
    public const string DesktopId = "ultimate-mp3-player";
    public const string PackMime = "application/x-ultimate-mp3-player-pack";
    private const string PackIcon = "application-x-ultimate-mp3-player-pack";

    public static void Register()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A test copy (UMP_DATA) must not take the files over from the real app.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UMP_DATA"))) return;
        var exe = Environment.ProcessPath;
        if (exe == null || Path.GetFileName(exe) != "UltimateMP3Player") return;
        _ = Task.Run(() =>
        {
            try { Write(exe); } catch { }
        });
    }

    private static void Write(string exe)
    {
        var share = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var icons = Path.Combine(share, "icons", "hicolor", "256x256");
        bool iconsChanged = Asset("app-256.png", Path.Combine(icons, "apps", DesktopId + ".png")) |
                            Asset("pack-256.png", Path.Combine(icons, "mimetypes", PackIcon + ".png"));

        var mime = Path.Combine(share, "mime");
        bool mimeChanged = Text(Path.Combine(mime, "packages", DesktopId + ".xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
              <mime-type type="{PackMime}">
                <comment>Ultimate MP3 Player pack</comment>
                <comment xml:lang="it">Pacchetto di Ultimate MP3 Player</comment>
                <icon name="{PackIcon}"/>
                <glob pattern="*{Pack.Extension}"/>
              </mime-type>
            </mime-info>

            """);

        var apps = Path.Combine(share, "applications");
        bool desktopChanged = Text(Path.Combine(apps, DesktopId + ".desktop"), $"""
            [Desktop Entry]
            Type=Application
            Name=Ultimate MP3 Player
            GenericName=Music Player
            GenericName[it]=Lettore musicale
            Comment=Offline music player that downloads songs and playlists from almost any site
            Comment[it]=Lettore musicale offline che scarica brani e playlist da quasi ogni sito
            Exec={Quote(exe)} %F
            Icon={DesktopId}
            Terminal=false
            Categories=AudioVideo;Audio;Player;
            MimeType={PackMime};
            Keywords=music;mp3;player;download;playlist;musica;
            StartupWMClass={DesktopId}
            StartupNotify=true

            """);

        if (iconsChanged) Run("gtk-update-icon-cache", "-f", "-t", Path.Combine(share, "icons", "hicolor"));
        if (mimeChanged) Run("update-mime-database", mime);
        if (desktopChanged || mimeChanged)
        {
            Run("update-desktop-database", apps);
            Run("xdg-mime", "default", DesktopId + ".desktop", PackMime);
        }
    }

    // Exec= wants the program quoted, with \ " ` $ escaped (and \ doubled again by the file format).
    private static string Quote(string path)
    {
        var s = path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$");
        return "\"" + s.Replace("\\", "\\\\") + "\"";
    }

    private static bool Text(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }

    private static bool Asset(string name, string path)
    {
        using var s = AssetLoader.Open(new Uri("avares://UltimateMP3Player/Assets/" + name));
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return true;
    }

    private static void Run(string program, params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(program, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(15000);
        }
        catch { }
    }
}

// "Listen together" needs other computers to reach this app (the Windows app's Firewall). Linux: opens the app's ports
// in ufw or firewalld when one of them is on (asks for the password); macOS: lets the app through the application
// firewall (asks for an administrator).
public static class Firewall
{
    public static bool Allow()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var exe = Environment.ProcessPath;
                if (exe == null) return false;
                var q = exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("'", "'\\''");
                var fw = "/usr/libexec/ApplicationFirewall/socketfilterfw";
                var script = $"{fw} --add '{q}' && {fw} --unblockapp '{q}'";
                return Run("osascript", "-e", $"do shell script \"{script.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges");
            }
            if (OperatingSystem.IsLinux())
            {
                int udp = Core.Together.Discovery.Port;
                int first = Core.Together.TogetherSession.FirstPort, last = Core.Together.TogetherSession.LastPort;
                var script =
                    "ok=0; " +
                    $"if command -v ufw >/dev/null 2>&1 && ufw status | grep -q 'Status: active'; then ufw allow {udp}/udp && ufw allow {first}:{last}/tcp || ok=1; fi; " +
                    $"if command -v firewall-cmd >/dev/null 2>&1 && firewall-cmd --state >/dev/null 2>&1; then firewall-cmd --permanent --add-port={udp}/udp --add-port={first}-{last}/tcp && firewall-cmd --reload || ok=1; fi; " +
                    "exit $ok";
                return Run("pkexec", "sh", "-c", script);
            }
        }
        catch { }
        return false;
    }

    public static string DoneMessage => L.T("Fatto: il firewall ora lascia entrare le connessioni degli altri su ogni rete.");

    private static bool Run(string program, params string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(program, args) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true });
        if (p == null) return false;
        if (!p.WaitForExit(120000)) return false;
        return p.ExitCode == 0;
    }
}
