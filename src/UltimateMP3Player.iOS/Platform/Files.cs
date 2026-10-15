using CoreGraphics;
using Foundation;
using UIKit;
using UniformTypeIdentifiers;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// Files across apps on the iPhone: what comes in (a pack opened from Files or Mail, songs picked) is copied into the app's
// temporary folder first; what goes out (an exported pack) goes through the share sheet (AirDrop, Telegram, "Save to
// Files"...).
public static class Files
{
    public static string IncomingDir => Path.Combine(Path.GetTempPath(), "incoming");
    public static string ShareDir => Path.Combine(Path.GetTempPath(), "share");

    // The type of the app's packs (Info.plist).
    public const string PackType = "com.kirbohq.ultimatemp3player.pack";

    // Leftovers of what came in or went out (older than a day).
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

    // A copy of a file opened with the app (its own place may be in another app's folder, reachable only for a moment),
    // or null.
    public static string? CopyIn(NSUrl url)
    {
        bool scoped = url.StartAccessingSecurityScopedResource();
        try
        {
            var name = url.LastPathComponent ?? "file";
            name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            var dir = Path.Combine(IncomingDir, Ids.New());
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            File.Copy(url.Path!, path, true);
            return path;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
        finally
        {
            if (scoped) url.StopAccessingSecurityScopedResource();
        }
    }

    // The system's share sheet with a file of the app.
    public static void Share(string path, string mime, string title)
    {
        if (TopController() is not { View: { } view } top) return;
        var sheet = new UIActivityViewController(new NSObject[] { NSUrl.FromFilename(path) }, null);
        // (on an iPad it's a bubble: from the middle of the screen)
        if (sheet.PopoverPresentationController is { } pop)
        {
            pop.SourceView = view;
            pop.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 0, 0);
            pop.PermittedArrowDirections = 0;
        }
        top.PresentViewController(sheet, true, null);
    }

    // A web page in Safari.
    public static void OpenUrl(string url)
    {
        try { UIApplication.SharedApplication.OpenUrl(new NSUrl(url), new UIApplicationOpenUrlOptions(), null); }
        catch { }
    }

    // The types the system's picker goes by for these extensions (public.mp3, public.jpeg..., the app's packs).
    public static List<string>? TypesOf(IEnumerable<string> extensions)
    {
        var list = new List<string>();
        foreach (var e in extensions)
        {
            var ext = e.TrimStart('.');
            string? id = e == Pack.Extension ? PackType : UTType.CreateFromExtension(ext)?.Identifier;
            if (id != null && !list.Contains(id)) list.Add(id);
        }
        return list.Count > 0 ? list : null;
    }

    // The screen on top, to show another over it (the share sheet, a site's login page).
    internal static UIViewController? TopController()
    {
        var app = UIApplication.SharedApplication;
        var window = app.KeyWindow ?? app.Windows.FirstOrDefault();
        var top = window?.RootViewController;
        while (top?.PresentedViewController is { } shown) top = shown;
        return top;
    }
}
