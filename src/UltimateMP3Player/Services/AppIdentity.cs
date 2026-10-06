using System.IO;
using System.Runtime.InteropServices;

namespace UltimateMP3Player.Services;

// Who the app is for Windows. The media controls (quick settings, lock screen) name the app and show its icon from an
// "AppUserModelID": without one Windows only knew "UltimateMP3Player.exe" and wrote "Unknown app". The process and the
// hidden window of the media controls carry the id, and Windows finds the name and the icon in the Start-menu shortcut
// with the same id (the other shortcuts to the app get it too, so its windows keep grouping with them on the taskbar).
// Per user, no admin rights; checked at every start.
public static class AppIdentity
{
    public const string Id = "KirboHQ.UltimateMP3Player";
    public const string Name = "Ultimate MP3 Player";

    [DllImport("shell32.dll")]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string id);

    // Before the first window.
    public static void ApplyToProcess()
    {
        try { SetCurrentProcessExplicitAppUserModelID(Id); }
        catch { }
    }

    // The shortcuts: the Start-menu one is made for a copy of the app without one (the portable one; not for a test copy).
    public static void EnsureShortcuts()
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
            bool test = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UMP_DATA"));
            if (!File.Exists(start) && !test) Shortcut(start, exe, true);
            var links = new List<string> { start };
            foreach (var dir in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                try { links.AddRange(Directory.EnumerateFiles(Environment.GetFolderPath(dir), "*.lnk")); }
                catch { }
            }
            foreach (var lnk in links.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists)) Shortcut(lnk, null, false);
        }
        catch { }
    }

    // Opens (or creates, pointing at exe) a shortcut and gives it the id, if it leads to the app.
    private static void Shortcut(string path, string? exe, bool create)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))!);
            var shell = (IShellLinkW)link!;
            var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link!;
            if (create)
            {
                shell.SetPath(exe!);
                shell.SetWorkingDirectory(Path.GetDirectoryName(exe!)!);
                shell.SetIconLocation(exe!, 0);
            }
            else
            {
                file.Load(path, 2);
                var target = new System.Text.StringBuilder(1024);
                shell.GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (!Path.GetFileName(target.ToString()).Equals("UltimateMP3Player.exe", StringComparison.OrdinalIgnoreCase)) return;
            }
            var store = (IPropertyStore)link!;
            var key = AppIdKey;
            if (!create && store.GetValue(ref key, out var old) == 0)
            {
                bool same = old.Type == 31 && Marshal.PtrToStringUni(old.Pointer) == Id;
                PropVariantClear(ref old);
                if (same) return;
            }
            SetString(store, Id);
            file.Save(path, true);
        }
        catch { }
        finally
        {
            if (link != null) Marshal.ReleaseComObject(link);
        }
    }

    // The window speaks for the app with the id.
    public static void ApplyTo(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return;
            try { SetString(store, Id); }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { }
    }

    // PKEY_AppUserModel_ID
    private static PropertyKey AppIdKey => new() { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };

    private static void SetString(IPropertyStore store, string text)
    {
        var key = AppIdKey;
        var value = new PropVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(text) };
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally { Marshal.FreeCoTaskMem(value.Pointer); }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // A PROPVARIANT holding a VT_LPWSTR (24 bytes on 64 bit).
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }
}
