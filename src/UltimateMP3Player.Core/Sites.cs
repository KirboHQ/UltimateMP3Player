namespace UltimateMP3Player.Core;

// Featured = shown as a music source on the download page.
public sealed record SiteInfo(string Name, string Color, string[] Domains, bool Music = false, bool GalleryFirst = false, bool Featured = false);

public static class Sites
{
    public static readonly SiteInfo[] All =
    {
        new("YouTube Music", "#FF0033", new[] { "music.youtube.com" }, Music: true, Featured: true),
        new("YouTube", "#FF0033", new[] { "youtube.com", "youtu.be", "youtube-nocookie.com" }, Featured: true),
        new("Spotify", "#1DB954", new[] { "spotify.com", "spotify.link" }, Music: true, Featured: true),
        new("SoundCloud", "#FF5500", new[] { "soundcloud.com", "snd.sc" }, Music: true, Featured: true),
        new("TikTok", "#FE2C55", new[] { "tiktok.com" }, Featured: true),
        new("Instagram", "#E1306C", new[] { "instagram.com", "instagr.am" }, GalleryFirst: true, Featured: true),
        new("Facebook", "#1877F2", new[] { "facebook.com", "fb.watch", "fb.com" }, Featured: true),
        new("X / Twitter", "#E7E9EA", new[] { "twitter.com", "x.com", "t.co" }, GalleryFirst: true, Featured: true),
        new("Reddit", "#FF4500", new[] { "reddit.com", "redd.it" }, GalleryFirst: true, Featured: true),
        new("Twitch", "#9146FF", new[] { "twitch.tv" }, Featured: true),
        new("Imgur", "#1BB76E", new[] { "imgur.com" }, GalleryFirst: true),
        new("Pinterest", "#E60023", new[] { "pinterest.com", "pinterest.it", "pinterest.co.uk", "pinterest.fr", "pinterest.de", "pinterest.es", "pin.it" }, GalleryFirst: true),
        new("Giphy", "#A970FF", new[] { "giphy.com", "gph.is" }),
        new("Deezer", "#A238FF", new[] { "deezer.com", "deezer.page.link", "dzr.page.link" }, Music: true, Featured: true),
        new("Vimeo", "#1AB7EA", new[] { "vimeo.com" }, Featured: true),
        new("Bandcamp", "#629AA9", new[] { "bandcamp.com" }, Music: true, Featured: true),
        new("Bluesky", "#1185FE", new[] { "bsky.app" }, GalleryFirst: true, Featured: true),
        new("Tumblr", "#7C8AA5", new[] { "tumblr.com" }, GalleryFirst: true),
        new("Kick", "#53FC18", new[] { "kick.com" }, Featured: true),
        new("Dailymotion", "#0A8BFF", new[] { "dailymotion.com", "dai.ly" }, Featured: true),
        new("Bilibili", "#00A1D6", new[] { "bilibili.com", "b23.tv" }, Featured: true),
        new("Mixcloud", "#5000FF", new[] { "mixcloud.com" }, Music: true, Featured: true),
        new("Tenor", "#4A90E2", new[] { "tenor.com" }, GalleryFirst: true),
        new("Flickr", "#FF0084", new[] { "flickr.com", "flic.kr" }, GalleryFirst: true),
        new("DeviantArt", "#05CC47", new[] { "deviantart.com" }, GalleryFirst: true),
        new("ArtStation", "#13AFF0", new[] { "artstation.com" }, GalleryFirst: true),
        new("Pixiv", "#0096FA", new[] { "pixiv.net" }, GalleryFirst: true),
        new("Streamable", "#0F90FA", new[] { "streamable.com" }),
        new("Rumble", "#85C742", new[] { "rumble.com" }),
        new("Audiomack", "#FFA200", new[] { "audiomack.com" }, Music: true, Featured: true),
        new("Wallhaven", "#8C7AE6", new[] { "wallhaven.cc" }, GalleryFirst: true),
        new("Unsplash", "#E7E9EA", new[] { "unsplash.com" }, GalleryFirst: true),
        new("Imgbb", "#2A7AE2", new[] { "imgbb.com", "ibb.co" }, GalleryFirst: true),
        new("Postimages", "#3C8DBC", new[] { "postimg.cc", "postimages.org" }, GalleryFirst: true),
        new("VSCO", "#E7E9EA", new[] { "vsco.co" }, GalleryFirst: true),
        new("Weibo", "#E6162D", new[] { "weibo.com", "weibo.cn" }, GalleryFirst: true),
        new("Threads", "#E7E9EA", new[] { "threads.net", "threads.com" }),
        new("9GAG", "#E7E9EA", new[] { "9gag.com" }),
        new("Snapchat", "#FFFC00", new[] { "snapchat.com" }),
        new("LinkedIn", "#0A66C2", new[] { "linkedin.com" }),
    };

    public static string HostOf(string url)
    {
        try
        {
            var h = new Uri(url).Host.ToLowerInvariant();
            foreach (var p in new[] { "www.", "m.", "mobile.", "old.", "new.", "vm.", "vt." })
                if (h.StartsWith(p)) { h = h[p.Length..]; break; }
            return h;
        }
        catch { return ""; }
    }

    public static SiteInfo? Find(string url)
    {
        var host = HostOf(url);
        if (host == "") return null;
        foreach (var s in All)
            foreach (var d in s.Domains)
                if (host == d || host.EndsWith("." + d)) return s;
        return null;
    }

    public static string NameFor(string url)
    {
        var s = Find(url);
        if (s != null) return s.Name;
        var host = HostOf(url);
        return host.Length > 0 ? host : "Web";
    }

    public static string ColorFor(string siteName)
        => All.FirstOrDefault(s => s.Name == siteName)?.Color ?? "#8B93A7";
}
