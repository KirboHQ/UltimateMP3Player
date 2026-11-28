using System.IO.Compression;

namespace UltimateMP3Player.Core.Mods;

// A .umpmod file: a zip with mod.json at its root (or inside one folder, as zipping a folder makes it), the scripts and pictures.
// Installed unzipped in mods/<id>; nothing in it may land outside that folder.
public static class ModPackage
{
    public const string Extension = ".umpmod";
    private const int MaxEntries = 5000;
    private const long MaxBytes = 300L * 1024 * 1024;

    public static string ModsDir => Path.Combine(AppPaths.DataDir, "mods");
    public static string DataDirOf(string id) => Path.Combine(AppPaths.DataDir, "mods-data", id);

    // A path inside the mod: relative, no "..", no drive or root.
    public static bool IsSafePath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p) || p.Length > 260 || Path.IsPathRooted(p) || p.Contains(':') || p.Contains('\0')) return false;
        return p.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part != "." && part != "..");
    }

    // The mod inside a file, read without installing it (to show what it is and what it asks for).
    public static ModManifest Peek(string file)
    {
        using var zip = Open(file);
        var (manifest, _) = Find(zip);
        using var r = new StreamReader(manifest.Open());
        return ModManifest.Parse(r.ReadToEnd());
    }

    // Unzips the mod into mods/<id>, in place of an older version of it; its data (mods-data/<id>) stays.
    public static ModManifest Install(string file)
    {
        using var zip = Open(file);
        var (entry, prefix) = Find(zip);
        ModManifest m;
        using (var r = new StreamReader(entry.Open())) m = ModManifest.Parse(r.ReadToEnd());

        var entries = zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')).ToList();
        if (entries.Count > MaxEntries || entries.Sum(e => e.Length) > MaxBytes) throw new ModException(L.T("The mod is too big."));
        Directory.CreateDirectory(ModsDir);
        var target = Path.Combine(ModsDir, m.Id);
        var fresh = target + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        try
        {
            var root = Path.GetFullPath(fresh) + Path.DirectorySeparatorChar;
            foreach (var e in entries)
            {
                var rel = e.FullName[prefix.Length..];
                if (!IsSafePath(rel)) throw new ModException(L.F("The mod has a file in a wrong place: {0}", e.FullName));
                var path = Path.GetFullPath(Path.Combine(fresh, rel));
                if (!path.StartsWith(root, StringComparison.Ordinal)) throw new ModException(L.F("The mod has a file in a wrong place: {0}", e.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                e.ExtractToFile(path, true);
            }
            if (!File.Exists(Path.Combine(fresh, m.Main))) throw new ModException(L.F("Its main script {0} is missing.", m.Main));
            var old = target + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            Directory.Move(fresh, target);
            try { if (Directory.Exists(old)) Directory.Delete(old, true); } catch { }
        }
        catch
        {
            try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch { }
            throw;
        }
        return m;
    }

    // Removes an installed mod; its data too when asked.
    public static void Remove(string id, bool data)
    {
        var dir = Path.Combine(ModsDir, id);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (data && Directory.Exists(DataDirOf(id))) Directory.Delete(DataDirOf(id), true);
    }

    private static ZipArchive Open(string file)
    {
        try { return ZipFile.OpenRead(file); }
        catch (InvalidDataException) { throw new ModException(L.T("This file isn't a mod.")); }
    }

    private static (ZipArchiveEntry Manifest, string Prefix) Find(ZipArchive zip)
    {
        if (zip.GetEntry(ModManifest.FileName) is { } root) return (root, "");
        // Zipped as a folder: mod.json one level down, and every file in that folder.
        var nested = zip.Entries.Where(e => e.FullName.Count(c => c == '/') == 1 && e.Name == ModManifest.FileName).ToList();
        if (nested.Count == 1)
        {
            var prefix = nested[0].FullName[..^ModManifest.FileName.Length];
            if (zip.Entries.All(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal))) return (nested[0], prefix);
        }
        throw new ModException(L.T("This file isn't a mod: mod.json is missing."));
    }
}
