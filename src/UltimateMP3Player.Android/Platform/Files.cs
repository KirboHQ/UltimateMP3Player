using Android.Content;
using Android.Provider;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// Files across apps: Android hands over "content://" addresses, not paths. What comes in (a pack opened from a file
// manager, songs and pictures picked) is copied into the app's cache first; what goes out (an exported pack) is shared
// through the app's FileProvider.
public static class Files
{
    public static string IncomingDir => Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath, "incoming");
    public static string ShareDir => Path.Combine(Android.App.Application.Context.CacheDir!.AbsolutePath, "share");

    // The file's own name ("Mix estate.ump"), when the other app tells it.
    public static string? DisplayName(Context ctx, Android.Net.Uri uri)
    {
        try
        {
            if (uri.Scheme == "file") return Path.GetFileName(uri.Path);
            using var c = ctx.ContentResolver!.Query(uri, new[] { IOpenableColumns.DisplayName }, null, null, null);
            if (c != null && c.MoveToFirst()) return c.GetString(0);
        }
        catch { }
        return null;
    }

    // A copy of the file in the cache (with its name), or null.
    public static string? CopyIn(Context ctx, Android.Net.Uri uri, string? extension = null)
    {
        try
        {
            var name = DisplayName(ctx, uri) ?? ("file" + (extension ?? ""));
            name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            if (extension != null && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name += extension;
            var dir = Path.Combine(IncomingDir, Ids.New());
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            using (var input = ctx.ContentResolver!.OpenInputStream(uri))
            {
                if (input == null) return null;
                using var output = File.Create(path);
                input.CopyTo(output);
            }
            return path;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
    }

    // Leftovers of what came in (older than a day).
    public static void CleanUp()
    {
        try
        {
            foreach (var dir in new[] { IncomingDir, ShareDir })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var d in Directory.EnumerateFileSystemEntries(dir))
                {
                    try
                    {
                        if (File.GetLastWriteTime(d) > DateTime.Now.AddDays(-1)) continue;
                        if (Directory.Exists(d)) Directory.Delete(d, true);
                        else File.Delete(d);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    // The system's share sheet with a file of the app (Telegram, Discord, Drive, "Save to"...).
    public static void Share(string path, string mime, string title)
    {
        var ctx = (Context?)MainActivity.Current ?? Android.App.Application.Context;
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(ctx, ctx.PackageName + ".files", new Java.IO.File(path));
        var send = new Intent(Intent.ActionSend).SetType(mime).PutExtra(Intent.ExtraStream, uri).AddFlags(ActivityFlags.GrantReadUriPermission);
        var chooser = Intent.CreateChooser(send, title)!;
        if (MainActivity.Current == null) chooser.AddFlags(ActivityFlags.NewTask);
        ctx.StartActivity(chooser);
    }

    // A web page in the browser.
    public static void OpenUrl(string url)
    {
        try
        {
            var ctx = (Context?)MainActivity.Current ?? Android.App.Application.Context;
            var intent = new Intent(Intent.ActionView, Android.Net.Uri.Parse(url));
            if (MainActivity.Current == null) intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
        }
        catch { }
    }
}
