using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UltimateMP3Player.Audio;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// The life of the app on the iPhone, through Avalonia's IActivatableLifetime: in front or away (AppHost.SetVisible), a
// .ump pack opened with the app from Files, Mail or Telegram, a link (ump://, http). Also the audio session, set up once.
public static class Lifecycle
{
    public static void Hook(Avalonia.Application app)
    {
        AudioSession.Setup();
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime life) return;
        life.Activated += (_, e) =>
        {
            switch (e)
            {
                case FileActivatedEventArgs f:
                    _ = Open(f.Files);
                    break;
                case ProtocolActivatedEventArgs p:
                    Open(p.Uri);
                    break;
                default:
                    if (e.Kind == ActivationKind.Background) App.Host?.SetVisible(true);
                    break;
            }
        };
        life.Deactivated += (_, e) =>
        {
            if (e.Kind == ActivationKind.Background) App.Host?.SetVisible(false);
        };
    }

    // Packs opened with the app: copied in (their place may be another app's), then imported as on the other systems.
    private static async Task Open(IEnumerable<IStorageItem> items)
    {
        foreach (var item in items)
        {
            if (item is not IStorageFile file || !file.Name.EndsWith(Pack.Extension, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var dir = Path.Combine(Files.IncomingDir, Ids.New());
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, string.Join("_", file.Name.Split(Path.GetInvalidFileNameChars())));
                await using (var input = await file.OpenReadAsync())
                await using (var output = File.Create(path))
                    await input.CopyToAsync(output);
                App.Deliver(new Incoming(IncomingKind.Pack, path));
            }
            catch (Exception ex) { App.Log(ex); }
        }
    }

    private static void Open(Uri uri)
    {
        try
        {
            if (uri.IsFile)
            {
                if (uri.LocalPath.EndsWith(Pack.Extension, StringComparison.OrdinalIgnoreCase) && Files.CopyIn(Foundation.NSUrl.FromFilename(uri.LocalPath)) is { } pack)
                    Dispatcher.UIThread.Post(() => App.Deliver(new Incoming(IncomingKind.Pack, pack)));
            }
            else if (uri.Scheme is "http" or "https") Dispatcher.UIThread.Post(() => App.Deliver(new Incoming(IncomingKind.Link, uri.ToString())));
            else if (uri.Scheme == "ump" && LinkIn(uri.OriginalString) is { } link) Dispatcher.UIThread.Post(() => App.Deliver(new Incoming(IncomingKind.Link, link)));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // The app's own links (Info.plist): "ump://add?url=<link>" (a Shortcut in the share sheet can open one, iOS has no
    // "Share > the app" without an extension), or "ump://<link>". The link is then analyzed as if pasted.
    private static string? LinkIn(string text)
    {
        var rest = text["ump:".Length..].TrimStart('/');
        // (the link last, as it is or encoded: one with its own "&" isn't cut there)
        int at = rest.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
        var link = at >= 0 ? rest[(at + 4)..] : rest;
        if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase) || !link.Contains("://", StringComparison.Ordinal)) link = Uri.UnescapeDataString(link);
        return Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : null;
    }
}
