using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Services;

// ".ump" packs open with the app (double click) and have their own icon. Per user, no admin rights: written at every
// start if missing or pointing at another copy of the exe; the installer writes the same keys (and removes them).
public static class FileAssociation
{
    public const string ProgId = "UltimateMP3Player.Pack";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        // A test copy (UMP_DATA) must not take the files over from the real app.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UMP_DATA"))) return;
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var (icon, iconChanged) = IconFile();
            bool changed = iconChanged;
            using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            using (var prog = classes.CreateSubKey(ProgId))
            {
                changed |= Set(prog, "", L.T("Pacchetto di Ultimate MP3 Player"));
                using (var di = prog.CreateSubKey("DefaultIcon"))
                    changed |= Set(di, "", icon != null ? $"\"{icon}\",0" : $"\"{exe}\",0");
                using (var cmd = prog.CreateSubKey(@"shell\open\command"))
                    changed |= Set(cmd, "", $"\"{exe}\" \"%1\"");
            }
            using (var ext = classes.CreateSubKey(Pack.Extension))
            {
                changed |= Set(ext, "", ProgId);
                using var with = ext.CreateSubKey("OpenWithProgids");
                changed |= Set(with, ProgId, "");
            }
            // Explorer picks up the new icon and "open with" right away.
            if (changed) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static bool Set(RegistryKey key, string name, string value)
    {
        if (key.GetValue(name) is string old && old == value) return false;
        key.SetValue(name, value);
        return true;
    }

    // The installed pack.ico next to the exe, otherwise the one inside the app copied next to the data.
    private static (string? Path, bool Changed) IconFile()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "pack.ico");
        if (File.Exists(beside)) return (beside, false);
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/pack.ico"));
            if (res == null) return (null, false);
            byte[] bytes;
            using (var s = res.Stream)
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }
            var target = Path.Combine(AppPaths.DataDir, "pack.ico");
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return (target, false);
            File.WriteAllBytes(target, bytes);
            return (target, true);
        }
        catch { return (null, false); }
    }
}
