using System.ComponentModel;
using System.Diagnostics;

namespace UltimateMP3Player.Services;

// Windows Firewall: "Listen together" needs other computers to reach this app. The first time Windows asks by
// itself, but only for private networks (Radmin VPN and similar count as public) and a "Cancel" blocks it for good:
// this replaces whatever rules the app has with one that lets it in on every network (asks for administrator rights).
public static class Firewall
{
    public static bool Allow()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return false;
        string args = $"/c netsh advfirewall firewall delete rule name=all program=\"{exe}\" >nul 2>&1 & " +
                      $"netsh advfirewall firewall add rule name=\"Ultimate MP3 Player\" dir=in action=allow program=\"{exe}\" enable=yes profile=any";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) return false;
            p.WaitForExit(20000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }
}
