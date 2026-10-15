using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Foundation;
using ObjCRuntime;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Platform;

// Python inside the app, for yt-dlp (iOS can't start programs): Frameworks/Python.framework, its standard library in
// python/lib and the app's part in python/app (ump_ios.py, certifi, yt-dlp), put there by tools/ios-deps.sh when the
// .ipa is made. Started once, the first time it's needed, on a thread of its own that stays (Python's main thread); then
// anything goes through ump_ios.call, JSON in and out, from any thread: each call takes Python's lock (the GIL) for as long
// as it runs, and Python lets it go while it waits for the network or the disk, so several downloads go at once.
// The C API is reached through the library's symbols (dlopen, dlsym) rather than linked: an app built without Python (a
// build on a Mac without tools/ios-deps.sh) still starts, and says the engines aren't there.
public static unsafe class PythonHost
{
    // dlopen: now, and global (ctypes inside Python finds Python's own functions).
    private const int RtldNow = 0x2, RtldGlobal = 0x8;

    private static string Bundle => NSBundle.MainBundle.BundlePath;
    public static string Home => Path.Combine(Bundle, "python");
    public static string AppDir => Path.Combine(Home, "app");
    public static string Library => Path.Combine(NSBundle.MainBundle.PrivateFrameworksPath ?? Path.Combine(Bundle, "Frameworks"), "Python.framework", "Python");
    public static bool Bundled => File.Exists(Library) && Directory.Exists(Path.Combine(Home, "lib"));

    private static readonly object Gate = new();
    private static Task? _start;
    private static IntPtr _lib, _call;

    private static delegate* unmanaged<IntPtr> _configCreate;
    private static delegate* unmanaged<IntPtr, void> _configFree;
    private static delegate* unmanaged<IntPtr, byte**, int> _configGetError;
    private static delegate* unmanaged<IntPtr, byte*, int> _configHasOption;
    private static delegate* unmanaged<IntPtr, byte*, long, int> _configSetInt;
    private static delegate* unmanaged<IntPtr, byte*, byte*, int> _configSetStr;
    private static delegate* unmanaged<IntPtr, int> _initialize;
    private static delegate* unmanaged<IntPtr> _saveThread;
    private static delegate* unmanaged<int> _gilEnsure;
    private static delegate* unmanaged<int, void> _gilRelease;
    private static delegate* unmanaged<byte*, IntPtr> _importModule;
    private static delegate* unmanaged<IntPtr, byte*, IntPtr> _getAttr;
    private static delegate* unmanaged<IntPtr, nint, IntPtr, int> _listInsert;
    private static delegate* unmanaged<byte*, nint, IntPtr> _fromUtf8;
    private static delegate* unmanaged<IntPtr, nint*, byte*> _asUtf8;
    private static delegate* unmanaged<IntPtr, IntPtr, IntPtr> _callOneArg;
    private static delegate* unmanaged<IntPtr, void> _decRef;
    private static delegate* unmanaged<void> _errPrint;

    // Python running (started the first time). An error stays: Python can't be started twice.
    public static Task StartAsync()
    {
        lock (Gate) return _start ??= Start();
    }

    private static Task Start()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Python's own threads have 16 MB of stack on Apple's systems: its main thread too.
        new Thread(() =>
        {
            try { Initialize(); }
            catch (Exception ex)
            {
                App.Log(ex);
                done.TrySetException(ex);
                return;
            }
            done.TrySetResult();
            // Python's main thread stays (some of Python looks for it), its lock given to whoever needs it.
            Thread.Sleep(Timeout.Infinite);
        }, 16 * 1024 * 1024) { IsBackground = true, Name = "Python" }.Start();
        return done.Task;
    }

    private static void Initialize()
    {
        if (!Bundled) throw new EngineException(L.T("Questa versione dell'app non contiene i motori di download."), Library);
        _lib = Dlfcn.dlopen(Library, RtldNow | RtldGlobal);
        if (_lib == IntPtr.Zero) throw new EngineException(L.T("Python non si avvia."), Dlfcn.dlerror() ?? Library);
        _configCreate = (delegate* unmanaged<IntPtr>)Symbol("PyInitConfig_Create");
        _configFree = (delegate* unmanaged<IntPtr, void>)Symbol("PyInitConfig_Free");
        _configGetError = (delegate* unmanaged<IntPtr, byte**, int>)Symbol("PyInitConfig_GetError");
        _configHasOption = (delegate* unmanaged<IntPtr, byte*, int>)Symbol("PyInitConfig_HasOption");
        _configSetInt = (delegate* unmanaged<IntPtr, byte*, long, int>)Symbol("PyInitConfig_SetInt");
        _configSetStr = (delegate* unmanaged<IntPtr, byte*, byte*, int>)Symbol("PyInitConfig_SetStr");
        _initialize = (delegate* unmanaged<IntPtr, int>)Symbol("Py_InitializeFromInitConfig");
        _saveThread = (delegate* unmanaged<IntPtr>)Symbol("PyEval_SaveThread");
        _gilEnsure = (delegate* unmanaged<int>)Symbol("PyGILState_Ensure");
        _gilRelease = (delegate* unmanaged<int, void>)Symbol("PyGILState_Release");
        _importModule = (delegate* unmanaged<byte*, IntPtr>)Symbol("PyImport_ImportModule");
        _getAttr = (delegate* unmanaged<IntPtr, byte*, IntPtr>)Symbol("PyObject_GetAttrString");
        _listInsert = (delegate* unmanaged<IntPtr, nint, IntPtr, int>)Symbol("PyList_Insert");
        _fromUtf8 = (delegate* unmanaged<byte*, nint, IntPtr>)Symbol("PyUnicode_FromStringAndSize");
        _asUtf8 = (delegate* unmanaged<IntPtr, nint*, byte*>)Symbol("PyUnicode_AsUTF8AndSize");
        _callOneArg = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Symbol("PyObject_CallOneArg");
        _decRef = (delegate* unmanaged<IntPtr, void>)Symbol("Py_DecRef");
        _errPrint = (delegate* unmanaged<void>)Symbol("PyErr_Print");

        var caches = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.CachesDirectory, NSSearchPathDomain.User)[0].Path!;
        var config = _configCreate();
        if (config == IntPtr.Zero) throw new EngineException(L.T("Python non si avvia."), "PyInitConfig_Create");
        try
        {
            // As the iOS embedding guide asks: UTF-8 everywhere, the output unbuffered, the signals (a write to a closed pipe
            // mustn't end the app), the output of Python itself in the system's log. The compiled code goes to the caches:
            // the app's own folder can't be written to.
            SetInt(config, "utf8_mode", 1);
            SetStr(config, "home", Home);
            // (the binary modules' .fwork files point into the app's Frameworks, from the folder of sys.executable)
            SetStr(config, "executable", NSBundle.MainBundle.ExecutablePath ?? Path.Combine(Bundle, "UltimateMP3Player"));
            SetInt(config, "buffered_stdio", 0);
            SetInt(config, "install_signal_handlers", 1);
            SetInt(config, "write_bytecode", 1);
            SetStr(config, "pycache_prefix", Path.Combine(caches, "python"));
            if (Has(config, "use_system_logger")) SetInt(config, "use_system_logger", 1);
            if (_initialize(config) < 0) throw new EngineException(L.T("Python non si avvia."), Error(config));
        }
        finally { _configFree(config); }

        // (this thread holds Python's lock until SaveThread)
        try
        {
            // The app's part first on Python's path, then ump_ios.call.
            var sys = Import("sys");
            var path = GetAttr(sys, "path");
            _decRef(sys);
            var dir = Str(AppDir);
            int inserted = _listInsert(path, 0, dir);
            _decRef(dir);
            _decRef(path);
            if (inserted != 0) Fail("sys.path");
            var module = Import("ump_ios");
            _call = GetAttr(module, "call");
            _decRef(module);
            var answer = CallHeld(Json(w =>
            {
                w.WriteString("op", "setup");
                w.WriteString("libpython", Library);
                w.WriteString("ytdlp", Engines.YtDlp);
                w.WriteString("bundled", IosEngines.BundledYtDlp);
                w.WriteString("engines", Engines.Dir);
                w.WriteString("cache", caches);
            }));
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.TryGetProperty("error", out var error)) throw new EngineException(L.T("Python non si avvia."), error.GetString());
        }
        finally { _saveThread(); }
    }

    private static IntPtr Symbol(string name)
    {
        var p = Dlfcn.dlsym(_lib, name);
        if (p == IntPtr.Zero) throw new EngineException(L.T("Python non si avvia."), "symbol missing: " + name);
        return p;
    }

    private static byte[] Utf8(string s)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
        Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    private static bool Has(IntPtr config, string name)
    {
        fixed (byte* n = Utf8(name)) return _configHasOption(config, n) == 1;
    }

    private static void SetInt(IntPtr config, string name, long value)
    {
        fixed (byte* n = Utf8(name))
            if (_configSetInt(config, n, value) < 0) throw new EngineException(L.T("Python non si avvia."), name + ": " + Error(config));
    }

    private static void SetStr(IntPtr config, string name, string value)
    {
        fixed (byte* n = Utf8(name))
        fixed (byte* v = Utf8(value))
            if (_configSetStr(config, n, v) < 0) throw new EngineException(L.T("Python non si avvia."), name + ": " + Error(config));
    }

    private static string Error(IntPtr config)
    {
        byte* message = null;
        return _configGetError(config, &message) == 1 && message != null ? Marshal.PtrToStringUTF8((IntPtr)message) ?? "?" : "?";
    }

    private static IntPtr Import(string name)
    {
        IntPtr module;
        fixed (byte* n = Utf8(name)) module = _importModule(n);
        if (module == IntPtr.Zero) Fail("import " + name);
        return module;
    }

    private static IntPtr GetAttr(IntPtr o, string name)
    {
        IntPtr value;
        fixed (byte* n = Utf8(name)) value = _getAttr(o, n);
        if (value == IntPtr.Zero) Fail(name);
        return value;
    }

    private static IntPtr Str(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        IntPtr value;
        fixed (byte* p = bytes) value = _fromUtf8(p, bytes.Length);
        if (value == IntPtr.Zero) Fail("str");
        return value;
    }

    // Python's error goes to its log (the system's); the app says what it was doing.
    private static void Fail(string what)
    {
        _errPrint();
        throw new EngineException(L.T("Python non si avvia."), what);
    }

    // ump_ios.call with a JSON request, from any thread; its JSON answer.
    public static string Call(string request)
    {
        if (_start is not { IsCompletedSuccessfully: true }) throw new InvalidOperationException("Python isn't running");
        int state = _gilEnsure();
        try { return CallHeld(request); }
        finally { _gilRelease(state); }
    }

    // (with Python's lock held)
    private static string CallHeld(string request)
    {
        var arg = Str(request);
        IntPtr result;
        try { result = _callOneArg(_call, arg); }
        finally { _decRef(arg); }
        if (result == IntPtr.Zero)
        {
            _errPrint();
            throw new EngineException(L.T("Errore interno di Python."));
        }
        try
        {
            nint size;
            byte* text = _asUtf8(result, &size);
            if (text == null)
            {
                _errPrint();
                throw new EngineException(L.T("Errore interno di Python."));
            }
            return Encoding.UTF8.GetString(text, (int)size);
        }
        finally { _decRef(result); }
    }

    // A request: {"op": ..., ...} (written by hand: no reflection in an app compiled ahead of time and trimmed).
    public static string Json(Action<Utf8JsonWriter> fill)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            fill(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
