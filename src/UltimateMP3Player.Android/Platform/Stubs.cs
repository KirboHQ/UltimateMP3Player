using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

// What the shared view models know of the computer apps and the phone doesn't have: the DJ and Listen together (their
// view models come along but the app never opens them), Discord, the firewall, the smooth scrolling of the mouse wheel.

namespace UltimateMP3Player.Services
{
    // Discord's Rich Presence talks to the desktop Discord: not on a phone.
    public sealed class DiscordPresence : IDisposable
    {
        public bool Enabled { get; set; }
        public bool Available => false;
        public void Update(Track? t, bool playing, TimeSpan position, TimeSpan duration) { }
        public void Dispose() { }
    }

    // Scrolling on a phone is the finger's.
    public static class SmoothScroll
    {
        public static bool Enabled { get; set; }
    }

    public static class Firewall
    {
        public static string DoneMessage => "";
        public static bool Allow() => false;
    }
}

namespace UltimateMP3Player.Views
{
    public static class DjDialogs
    {
        public static MixSave? SaveRecording(DjViewModel dj, IReadOnlyList<DjDeckViewModel> decks) => null;
    }

    public static class DjSongPicker
    {
        public sealed record Result(DjDeckViewModel Deck, TrackViewModel? Track = null, string? File = null, DjDeckViewModel? FromDeck = null);

        public static Result? Show(DjViewModel dj, DjDeckViewModel deck) => null;
    }
}
