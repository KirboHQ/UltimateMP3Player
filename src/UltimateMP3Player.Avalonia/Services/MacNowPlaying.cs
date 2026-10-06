using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UltimateMP3Player.Services;

// macOS: the song in Control Center's Now Playing (title, artist, album, cover, bar) and the media keys, through the
// MediaPlayer framework (MPNowPlayingInfoCenter and MPRemoteCommandCenter) called with the Objective-C runtime.
internal sealed unsafe class MacNowPlaying : IDisposable
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string MediaPlayerPath = "/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer";
    private const string AppKitPath = "/System/Library/Frameworks/AppKit.framework/AppKit";

    private static MacNowPlaying? _instance;
    private readonly MediaControls _owner;
    private readonly IntPtr _center, _commands, _target;
    private readonly IntPtr _keyTitle, _keyArtist, _keyAlbum, _keyDuration, _keyElapsed, _keyRate, _keyArtwork;
    private string? _coverPath;
    private IntPtr _artwork;
    private static IntPtr _image; // what the cover's block returns
    private static IntPtr _block;

    private MacNowPlaying(MediaControls owner, IntPtr mediaPlayer)
    {
        _owner = owner;
        _center = Send(Class("MPNowPlayingInfoCenter"), Sel("defaultCenter"));
        _commands = Send(Class("MPRemoteCommandCenter"), Sel("sharedCommandCenter"));
        if (_center == IntPtr.Zero || _commands == IntPtr.Zero) throw new PlatformNotSupportedException();
        _keyTitle = Constant(mediaPlayer, "MPMediaItemPropertyTitle");
        _keyArtist = Constant(mediaPlayer, "MPMediaItemPropertyArtist");
        _keyAlbum = Constant(mediaPlayer, "MPMediaItemPropertyAlbumTitle");
        _keyDuration = Constant(mediaPlayer, "MPMediaItemPropertyPlaybackDuration");
        _keyArtwork = Constant(mediaPlayer, "MPMediaItemPropertyArtwork");
        _keyElapsed = Constant(mediaPlayer, "MPNowPlayingInfoPropertyElapsedPlaybackTime");
        _keyRate = Constant(mediaPlayer, "MPNowPlayingInfoPropertyPlaybackRate");

        // An object of our own class receives the commands.
        var cls = objc_allocateClassPair(Class("NSObject"), "UMPRemoteCommandTarget", IntPtr.Zero);
        if (cls != IntPtr.Zero)
        {
            class_addMethod(cls, Sel("onCommand:"), (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&OnCommand, "q@:@");
            objc_registerClassPair(cls);
        }
        else cls = Class("UMPRemoteCommandTarget");
        _target = Send(Send(cls, Sel("alloc")), Sel("init"));
        foreach (var name in new[] { "playCommand", "pauseCommand", "togglePlayPauseCommand", "nextTrackCommand", "previousTrackCommand", "changePlaybackPositionCommand" })
        {
            var cmd = Send(_commands, Sel(name));
            if (cmd == IntPtr.Zero) continue;
            SendBool(cmd, Sel("setEnabled:"), 1);
            Send(cmd, Sel("addTarget:action:"), _target, Sel("onCommand:"));
        }
        // The other commands stay greyed out.
        foreach (var name in new[] { "stopCommand", "skipForwardCommand", "skipBackwardCommand", "seekForwardCommand", "seekBackwardCommand", "changeRepeatModeCommand", "changeShuffleModeCommand" })
        {
            var cmd = Send(_commands, Sel(name));
            if (cmd != IntPtr.Zero) SendBool(cmd, Sel("setEnabled:"), 0);
        }
    }

    public static MacNowPlaying? Create(MediaControls owner)
    {
        if (!NativeLibrary.TryLoad(MediaPlayerPath, out var mp)) return null;
        NativeLibrary.TryLoad(AppKitPath, out _);
        return _instance = new MacNowPlaying(owner, mp);
    }

    // ------------------------------------------------------------------ the song

    public void OnChanged()
    {
        var pool = objc_autoreleasePoolPush();
        try
        {
            var t = _owner.Current;
            if (t == null)
            {
                SendPtr(_center, Sel("setNowPlayingInfo:"), IntPtr.Zero);
                SendLong(_center, Sel("setPlaybackState:"), 3); // stopped
                return;
            }
            var info = Send(Class("NSMutableDictionary"), Sel("dictionary"));
            Put(info, _keyTitle, Str(t.Title));
            if (t.Artist.Length > 0) Put(info, _keyArtist, Str(t.Artist));
            if (t.Album.Length > 0) Put(info, _keyAlbum, Str(t.Album));
            if (t.Duration > TimeSpan.Zero) Put(info, _keyDuration, Number(t.Duration.TotalSeconds));
            Put(info, _keyElapsed, Number(_owner.Position.TotalSeconds));
            Put(info, _keyRate, Number(_owner.Playing ? _owner.Speed : 0));
            if (t.Cover != _coverPath) SetCover(t.Cover);
            if (_artwork != IntPtr.Zero) Put(info, _keyArtwork, _artwork);
            SendPtr(_center, Sel("setNowPlayingInfo:"), info);
            SendLong(_center, Sel("setPlaybackState:"), _owner.Playing ? 1 : 2);
        }
        catch { }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
    }

    private void SetCover(string? path)
    {
        _coverPath = path;
        if (_artwork != IntPtr.Zero) Send(_artwork, Sel("release"));
        _artwork = IntPtr.Zero;
        if (_image != IntPtr.Zero) Send(_image, Sel("release"));
        _image = IntPtr.Zero;
        if (path == null || _keyArtwork == IntPtr.Zero) return;
        _image = Send(Send(Class("NSImage"), Sel("alloc")), Sel("initWithContentsOfFile:"), Str(path));
        if (_image == IntPtr.Zero) return;
        _artwork = SendArtwork(Send(Class("MPMediaItemArtwork"), Sel("alloc")), Sel("initWithBoundsSize:requestHandler:"), new CGSize(600, 600), Block());
    }

    // The cover is handed over with a block that returns the current image (one block for the whole run).
    private static IntPtr Block()
    {
        if (_block != IntPtr.Zero) return _block;
        var signature = Marshal.StringToHGlobalAnsi("@24@?0{CGSize=dd}8");
        var descriptor = (IntPtr*)Marshal.AllocHGlobal(3 * IntPtr.Size);
        descriptor[0] = IntPtr.Zero;
        descriptor[1] = 32; // sizeof the block below
        descriptor[2] = signature;
        var block = (byte*)Marshal.AllocHGlobal(32);
        if (!NativeLibrary.TryGetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock", out var globalBlock))
            globalBlock = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/system/libsystem_blocks.dylib"), "_NSConcreteGlobalBlock");
        *(IntPtr*)block = globalBlock;
        *(int*)(block + 8) = (1 << 28) | (1 << 30); // global, has signature
        *(int*)(block + 12) = 0;
        *(IntPtr*)(block + 16) = (IntPtr)(delegate* unmanaged<IntPtr, CGSize, IntPtr>)&CoverRequested;
        *(IntPtr*)(block + 24) = (IntPtr)descriptor;
        return _block = (IntPtr)block;
    }

    [UnmanagedCallersOnly]
    private static IntPtr CoverRequested(IntPtr block, CGSize size) => _image;

    // ------------------------------------------------------------------ the media keys and Control Center's buttons

    [UnmanagedCallersOnly]
    private static long OnCommand(IntPtr self, IntPtr cmd, IntPtr evt)
    {
        try
        {
            if (_instance is not { } me) return 1;
            var command = Send(evt, Sel("command"));
            if (command == Send(me._commands, Sel("playCommand"))) me._owner.Play();
            else if (command == Send(me._commands, Sel("pauseCommand"))) me._owner.Pause();
            else if (command == Send(me._commands, Sel("togglePlayPauseCommand"))) me._owner.PlayPause();
            else if (command == Send(me._commands, Sel("nextTrackCommand"))) me._owner.Next();
            else if (command == Send(me._commands, Sel("previousTrackCommand"))) me._owner.Previous();
            else if (command == Send(me._commands, Sel("changePlaybackPositionCommand")))
                me._owner.Seek(TimeSpan.FromSeconds(SendDouble(evt, Sel("positionTime"))));
            else return 1;
            return 0; // MPRemoteCommandHandlerStatusSuccess
        }
        catch { return 1; }
    }

    public void Dispose()
    {
        try
        {
            SendPtr(_center, Sel("setNowPlayingInfo:"), IntPtr.Zero);
            SendLong(_center, Sel("setPlaybackState:"), 3);
            foreach (var name in new[] { "playCommand", "pauseCommand", "togglePlayPauseCommand", "nextTrackCommand", "previousTrackCommand", "changePlaybackPositionCommand" })
            {
                var cmd = Send(_commands, Sel(name));
                if (cmd != IntPtr.Zero) SendPtr(cmd, Sel("removeTarget:"), _target);
            }
        }
        catch { }
        _instance = null;
    }

    // ------------------------------------------------------------------ Objective-C

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    private static IntPtr Class(string name) => objc_getClass(name);
    private static IntPtr Sel(string name) => sel_registerName(name);
    private static IntPtr Str(string s) => SendStr(Class("NSString"), Sel("stringWithUTF8String:"), s);
    private static IntPtr Number(double v) => SendDoubleArg(Class("NSNumber"), Sel("numberWithDouble:"), v);
    private static void Put(IntPtr dict, IntPtr key, IntPtr value)
    {
        if (key != IntPtr.Zero && value != IntPtr.Zero) Send(dict, Sel("setObject:forKey:"), value, key);
    }

    // The framework's NSString constants (their values aren't documented).
    private static IntPtr Constant(IntPtr lib, string name)
        => NativeLibrary.TryGetExport(lib, name, out var p) ? Marshal.ReadIntPtr(p) : IntPtr.Zero;

    [DllImport(Objc)] private static extern IntPtr objc_getClass(string name);
    [DllImport(Objc)] private static extern IntPtr sel_registerName(string name);
    [DllImport(Objc)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
    [DllImport(Objc)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(Objc)] private static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(Objc)] private static extern IntPtr objc_autoreleasePoolPush();
    [DllImport(Objc)] private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr a, IntPtr b);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendPtr(IntPtr receiver, IntPtr sel, IntPtr a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendLong(IntPtr receiver, IntPtr sel, long a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern void SendBool(IntPtr receiver, IntPtr sel, byte a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr receiver, IntPtr sel);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDoubleArg(IntPtr receiver, IntPtr sel, double a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendStr(IntPtr receiver, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string a);
    [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern IntPtr SendArtwork(IntPtr receiver, IntPtr sel, CGSize size, IntPtr block);
}
