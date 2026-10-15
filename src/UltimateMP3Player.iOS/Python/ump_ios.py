# The download engines of Ultimate MP3 Player on the iPhone: yt-dlp run inside the app's own process, since iOS can't
# start programs. Platform/Python.cs starts Python (the app's Python.framework) and calls call() with a JSON request,
# from threads of its own, and gets JSON back:
#   setup   once, right after Python starts: the folders (see _setup);
#   run     yt-dlp with the arguments the other systems give the program (Platform/PythonProcess.cs): what it writes
#           goes to two pipes the app reads as the program's output and errors, the exit code comes back when it ends;
#   cancel  a run stops (the app's Kill).
# Several runs go at once (the downloads in parallel): sys.stdout and sys.stderr send each write to the run of the thread
# writing, and the threads a run starts (yt-dlp's fragment downloads) belong to it too.
# YouTube's JavaScript challenges are solved by the system's JavaScriptCore (_JavaScriptCore), the updates of yt-dlp come
# from its GitHub releases (_update).

import contextvars
import hashlib
import importlib
import io
import json
import os
import platform
import re
import shutil
import ssl
import sys
import threading
import traceback
import urllib.request
import zipfile

try:
    # (the cancellation of a run that waits, and YouTube's JavaScript: without it, both less well)
    import ctypes
except Exception:
    ctypes = None

_paths = {}
_runs = {}
_cancelled_early = set()
_runs_lock = threading.Lock()
_switch_lock = threading.Lock()
_current = contextvars.ContextVar('ump_run', default=None)
# The folder the yt-dlp in use was unpacked into, and the yt_dlp module the JavaScript provider was registered with.
_active = None
_registered = None
_bundled_checked = False
# Python's PyThreadState_SetAsyncExc, to stop a run that's waiting for the network (see _cancel).
_async_exc = None

_RELEASES = 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/'


def call(request):
    """The app's way in: a JSON request, a JSON answer (never an exception)."""
    try:
        req = json.loads(request)
        op = req.get('op')
        if op == 'run':
            result = _run(req)
        elif op == 'cancel':
            result = _cancel(req)
        elif op == 'setup':
            result = _setup(req)
        else:
            result = {'error': f'unknown request {op!r}'}
    except BaseException as e:
        result = {'error': f'{type(e).__name__}: {e}'}
    return json.dumps(result)


def _log(text):
    # (the system's log: Xcode's console, Console.app)
    stream = getattr(sys.stderr, 'fallback', sys.stderr)
    try:
        stream.write(text + '\n')
    except Exception:
        pass


# ---------------------------------------------------------------------------------------------------- setup

def _setup(req):
    """req: libpython (the framework's binary), ytdlp (the engines' yt-dlp, a zip run by Python, as on Android),
    bundled (the app's own copy of it), engines (the engines' folder), cache (Library/Caches)."""
    global _async_exc
    _paths.update(req)
    sys.argv = ['yt-dlp']
    os.environ['NO_COLOR'] = '1'
    # yt-dlp's cache (YouTube's players, the solved challenges) where iOS may clean up when space runs low.
    os.environ['XDG_CACHE_HOME'] = req['cache']
    # Python has no certificates of its own on iOS: certifi's, for yt-dlp and for OpenSSL itself.
    cafile = _cafile()
    if cafile:
        os.environ['SSL_CERT_FILE'] = cafile
    sys.stdout = _Stream(sys.stdout, False)
    sys.stderr = _Stream(sys.stderr, True)
    _inherit_runs()
    try:
        if ctypes is None:
            raise ImportError('ctypes')
        setter = ctypes.PyDLL(req['libpython']).PyThreadState_SetAsyncExc
        setter.argtypes = [ctypes.c_ulong, ctypes.py_object]
        setter.restype = ctypes.c_int
        _async_exc = setter
    except Exception:
        _async_exc = None
    _jsc.load()
    return {'python': sys.version.split()[0], 'jsc': _jsc.lib is not None}


def _cafile():
    try:
        import certifi
        return certifi.where()
    except Exception:
        return None


# ---------------------------------------------------------------------------------------------------- output

class _Pipe:
    """One side of a run's output: a pipe the app reads (the program's standard output or error). Closed when the run
    ends; a thread of the run still writing after that is ignored (the app closes its end of the pipe right after)."""

    def __init__(self, fd):
        self.fd = fd
        self.lock = threading.Lock()
        self.closed = fd < 0

    def write(self, data):
        with self.lock:
            if self.closed:
                return
            view = memoryview(data)
            try:
                while view:
                    view = view[os.write(self.fd, view):]
            except OSError:
                # (the app doesn't read any more: the run was cancelled)
                self.closed = True

    def close(self):
        with self.lock:
            self.closed = True


class _Run:
    def __init__(self, req):
        self.id = req['id']
        self.out = _Pipe(req.get('out', -1))
        self.err = _Pipe(req.get('err', -1))
        self.cancelled = False
        self.thread = threading.get_ident()


class _Stream(io.TextIOBase):
    """sys.stdout or sys.stderr: a write goes to the run of the thread writing, or, outside the runs, to the system's log
    (fallback). A cancelled run stops at its next write."""

    def __init__(self, fallback, error):
        self.fallback = fallback
        self._error = error

    @property
    def encoding(self):
        return 'utf-8'

    @property
    def errors(self):
        return 'replace'

    def writable(self):
        return True

    def isatty(self):
        return False

    def fileno(self):
        raise io.UnsupportedOperation('fileno')

    def write(self, s):
        run = _current.get()
        if run is None:
            if self.fallback is not None:
                try:
                    self.fallback.write(s)
                except Exception:
                    pass
            return len(s)
        if run.cancelled:
            raise KeyboardInterrupt
        (run.err if self._error else run.out).write(s.encode('utf-8', 'replace'))
        return len(s)

    def flush(self):
        if _current.get() is None and self.fallback is not None:
            try:
                self.fallback.flush()
            except Exception:
                pass


def _inherit_runs():
    """A thread started during a run (yt-dlp's fragment downloads) writes to that run too."""
    start = threading.Thread.start

    def start_in_run(self):
        if _current.get() is not None:
            context = contextvars.copy_context()
            target = self.run
            self.run = lambda: context.run(target)
        return start(self)

    threading.Thread.start = start_in_run


# ---------------------------------------------------------------------------------------------------- runs

def _run(req):
    run = _Run(req)
    with _runs_lock:
        if run.id in _cancelled_early:
            _cancelled_early.discard(run.id)
            run.out.close()
            run.err.close()
            return {'code': 1}
        _runs[run.id] = run
    token = _current.set(run)
    try:
        code = _main(list(req.get('argv') or []))
    except SystemExit as e:
        code = _exit_code(run, e.code)
    except BaseException as e:
        code = 1
        if not run.cancelled:
            run.err.write(f'ERROR: {type(e).__name__}: {e}\n{traceback.format_exc()}'.encode('utf-8', 'replace'))
    finally:
        _current.reset(token)
        with _runs_lock:
            _runs.pop(run.id, None)
        run.out.close()
        run.err.close()
    return {'code': code}


def _main(argv):
    if argv[:1] == ['-U']:
        return _update()
    ytdlp = _activate()
    _register_jsc(ytdlp)
    try:
        ytdlp.main(argv)
    except SystemExit as e:
        return _exit_code(_current.get(), e.code)
    return 0


def _exit_code(run, code):
    if code is None:
        return 0
    if isinstance(code, int):
        return code
    # (a message instead of a number: written out, as Python does when a program ends with one)
    if run is not None:
        run.err.write(f'{code}\n'.encode('utf-8', 'replace'))
    return 1


def _cancel(req):
    with _runs_lock:
        run = _runs.get(req.get('id'))
        if run is None:
            # (cancelled before it began: it won't)
            _cancelled_early.add(req.get('id'))
            return {'found': False}
    run.cancelled = True
    # Its next write stops it (the downloads write their progress all the time); one waiting for a site is woken by an
    # exception raised in its thread as soon as the wait ends.
    if _async_exc is not None:
        try:
            _async_exc(run.thread, KeyboardInterrupt)
        except Exception:
            pass
    return {'found': True}


# ---------------------------------------------------------------------------------------------------- yt-dlp's code

def _activate():
    """The yt-dlp to run: the engines' one (the app's own copy, then its updates), unpacked once per version next to it
    so that Python keeps its compiled code (a zip is compiled again at every start). A new version takes over when no
    other run is using the one before: their modules would change under them."""
    global _active
    with _switch_lock:
        zip_path = _paths['ytdlp']
        _take_bundled(zip_path)
        stat = os.stat(zip_path)
        target = os.path.join(_paths['engines'], 'yt-dlp-lib', f'{stat.st_size}-{stat.st_mtime_ns}')
        if target != _active:
            with _runs_lock:
                alone = len(_runs) <= 1
            if _active is None or alone:
                if not os.path.isdir(target):
                    try:
                        _unpack(zip_path, target)
                    except (OSError, zipfile.BadZipFile):
                        # A broken file (an update cut off): the app's own copy again.
                        bundled = _paths.get('bundled')
                        if not bundled or not os.path.isfile(bundled):
                            raise
                        _copy(bundled, zip_path)
                        stat = os.stat(zip_path)
                        target = os.path.join(_paths['engines'], 'yt-dlp-lib', f'{stat.st_size}-{stat.st_mtime_ns}')
                        _unpack(zip_path, target)
                _switch(target)
        import yt_dlp
        return yt_dlp


def _unpack(zip_path, target):
    part = target + '.part'
    shutil.rmtree(part, ignore_errors=True)
    with zipfile.ZipFile(zip_path) as z:
        if 'yt_dlp/__init__.py' not in z.namelist():
            raise zipfile.BadZipFile('yt-dlp is not inside')
        z.extractall(part)
    shutil.rmtree(target, ignore_errors=True)
    os.rename(part, target)


def _switch(target):
    global _active
    for name in [n for n in sys.modules if n == 'yt_dlp' or n.startswith(('yt_dlp.', 'yt_dlp_'))]:
        del sys.modules[name]
    if _active in sys.path:
        sys.path.remove(_active)
    sys.path.insert(0, target)
    importlib.invalidate_caches()
    _active = target
    # The versions before, now unused.
    root = os.path.dirname(target)
    for name in os.listdir(root):
        path = os.path.join(root, name)
        if path != target:
            shutil.rmtree(path, ignore_errors=True)


def _take_bundled(zip_path):
    """The app's own yt-dlp when it's newer than the engines' one (an update of the app brought it): checked once."""
    global _bundled_checked
    if _bundled_checked:
        return
    _bundled_checked = True
    bundled = _paths.get('bundled')
    if not bundled or not os.path.isfile(bundled):
        return
    mine = _zip_version(zip_path) if os.path.isfile(zip_path) else None
    theirs = _zip_version(bundled)
    if theirs and (mine is None or _version_tuple(theirs) > _version_tuple(mine)):
        _copy(bundled, zip_path)


def _copy(source, target):
    part = target + '.part'
    shutil.copyfile(source, part)
    os.replace(part, target)


def _zip_version(path):
    try:
        with zipfile.ZipFile(path) as z:
            text = z.read('yt_dlp/version.py').decode('utf-8', 'replace')
        m = re.search(r'''^__version__\s*=\s*['"]([^'"]+)['"]''', text, re.M)
        return m.group(1) if m else None
    except Exception:
        return None


def _version_tuple(version):
    return tuple(int(n) for n in re.findall(r'\d+', version))


def _update():
    """yt-dlp -U: the latest release's yt-dlp (the zip the other systems run with Python), checked against the release's
    SHA2-256SUMS, into the engines' folder; it takes over at the next run (_activate). The messages are yt-dlp's own."""
    zip_path = _paths['ytdlp']
    current = _zip_version(zip_path)
    sums = _get(_RELEASES + 'SHA2-256SUMS').decode('utf-8', 'replace')
    expected = next((p[0].lower() for p in (line.split() for line in sums.splitlines()) if len(p) == 2 and p[1] == 'yt-dlp'), None)
    if not expected:
        print('ERROR: The release has no checksum for yt-dlp', file=sys.stderr)
        return 1
    if _sha256(zip_path) == expected:
        print(f'yt-dlp is up to date ({current})')
        return 0
    data = _get(_RELEASES + 'yt-dlp')
    if hashlib.sha256(data).hexdigest() != expected:
        print('ERROR: Unable to verify the new version of yt-dlp', file=sys.stderr)
        return 1
    part = zip_path + '.part'
    with open(part, 'wb') as f:
        f.write(data)
    latest = _zip_version(part)
    if latest is None:
        os.remove(part)
        print('ERROR: The new version of yt-dlp is not readable', file=sys.stderr)
        return 1
    if current and _version_tuple(latest) < _version_tuple(current):
        os.remove(part)
        print(f'yt-dlp is up to date ({current})')
        return 0
    os.replace(part, zip_path)
    print(f'Updated yt-dlp to {latest}')
    return 0


def _get(url):
    context = ssl.create_default_context(cafile=_cafile())
    req = urllib.request.Request(url, headers={'User-Agent': 'UltimateMP3Player'})
    with urllib.request.urlopen(req, context=context, timeout=60) as r:
        return r.read()


def _sha256(path):
    try:
        h = hashlib.sha256()
        with open(path, 'rb') as f:
            for chunk in iter(lambda: f.read(1 << 20), b''):
                h.update(chunk)
        return h.hexdigest()
    except OSError:
        return None


# ---------------------------------------------------------------------------------------------------- YouTube's JavaScript

_CONSOLE = '''
var __umpOut = [];
(function () {
    function log() { __umpOut.push(Array.prototype.map.call(arguments, String).join(' ')); }
    function none() {}
    var console = { log: log, info: log, debug: none, warn: none, error: none, trace: none };
    Object.defineProperty(globalThis, 'console', { value: console, writable: true, configurable: true });
})();
'''


class _JavaScriptCore:
    """yt-dlp's scripts for YouTube's challenges (EJS) in the system's JavaScriptCore, through its C API: the other systems
    start a JavaScript program (deno, QuickJS), iOS can't. What the scripts print with console.log is their answer.
    One at a time: a solve takes a lot of memory."""

    def __init__(self):
        self.lib = None
        self.lock = threading.Lock()

    def load(self):
        try:
            if ctypes is None:
                raise ImportError('ctypes')
            lib = ctypes.CDLL('/System/Library/Frameworks/JavaScriptCore.framework/JavaScriptCore')
            p = ctypes.c_void_p
            for name, result, args in (
                ('JSGlobalContextCreate', p, [p]),
                ('JSGlobalContextRelease', None, [p]),
                ('JSGarbageCollect', None, [p]),
                ('JSStringCreateWithCharacters', p, [p, ctypes.c_size_t]),
                ('JSStringRelease', None, [p]),
                ('JSStringGetLength', ctypes.c_size_t, [p]),
                ('JSStringGetCharactersPtr', p, [p]),
                ('JSEvaluateScript', p, [p, p, p, p, ctypes.c_int, ctypes.POINTER(p)]),
                ('JSValueToStringCopy', p, [p, p, ctypes.POINTER(p)]),
                ('JSValueProtect', None, [p, p]),
                ('JSValueUnprotect', None, [p, p]),
            ):
                f = getattr(lib, name)
                f.restype = result
                f.argtypes = args
            self.lib = lib
        except Exception as e:
            self.lib = None
            _log(f'JavaScriptCore not available: {e}')

    def run(self, script):
        with self.lock:
            lib = self.lib
            context = lib.JSGlobalContextCreate(None)
            if not context:
                raise RuntimeError('no JavaScript context')
            try:
                self._evaluate(context, _CONSOLE)
                self._evaluate(context, script)
                return self._evaluate(context, '__umpOut.join("\\n")')
            finally:
                lib.JSGarbageCollect(context)
                lib.JSGlobalContextRelease(context)

    def _evaluate(self, context, code):
        lib = self.lib
        # (as UTF-16 with its length: a NUL inside the code doesn't cut it short)
        data = code.encode('utf-16-le', 'surrogatepass')
        buffer = ctypes.create_string_buffer(data, len(data))
        source = lib.JSStringCreateWithCharacters(buffer, len(data) // 2)
        exception = ctypes.c_void_p()
        try:
            value = lib.JSEvaluateScript(context, source, None, None, 1, ctypes.byref(exception))
        finally:
            lib.JSStringRelease(source)
        if exception.value:
            raise RuntimeError(self._text(context, exception.value))
        return self._text(context, value) if value else ''

    def _text(self, context, value):
        lib = self.lib
        lib.JSValueProtect(context, value)
        try:
            exception = ctypes.c_void_p()
            string = lib.JSValueToStringCopy(context, value, ctypes.byref(exception))
            if not string:
                return ''
            try:
                length = lib.JSStringGetLength(string)
                chars = lib.JSStringGetCharactersPtr(string)
                return ctypes.string_at(chars, length * 2).decode('utf-16-le', 'surrogatepass') if length and chars else ''
            finally:
                lib.JSStringRelease(string)
        finally:
            lib.JSValueUnprotect(context, value)


_jsc = _JavaScriptCore()


def _ios_version():
    try:
        return platform.ios_ver().release or '0'
    except Exception:
        return '0'


def _register_jsc(ytdlp):
    """JavaScriptCore as a JavaScript runtime of yt-dlp ("--js-runtimes jsc", the app's EngineHost.JsRuntime) and the
    provider that solves YouTube's challenges with it, the way yt-dlp's own deno and QuickJS ones do. Once per yt_dlp
    module (an update brings new ones, with registries of their own)."""
    global _registered
    if _registered is ytdlp or _jsc.lib is None:
        return
    _registered = ytdlp
    try:
        from yt_dlp.extractor.youtube.jsc._builtin.ejs import EJSBaseJCP
        from yt_dlp.extractor.youtube.jsc.provider import JsChallengeProviderError, register_preference, register_provider
        from yt_dlp.globals import supported_js_runtimes
        from yt_dlp.utils._jsruntime import JsRuntime, JsRuntimeInfo
    except Exception:
        # (a yt-dlp whose insides changed: YouTube goes on without the challenges, some formats may be missing)
        _log('JavaScript provider not registered:\n' + traceback.format_exc())
        return

    version = _ios_version()

    class JavaScriptCoreRuntime(JsRuntime):
        def _info(self):
            return JsRuntimeInfo(name='jsc', path='JavaScriptCore', version=version, version_tuple=_version_tuple(version), supported=True)

    supported_js_runtimes.value['jsc'] = JavaScriptCoreRuntime

    class JavaScriptCoreJCP(EJSBaseJCP):
        PROVIDER_NAME = 'javascriptcore'
        PROVIDER_VERSION = '1.0'
        BUG_REPORT_LOCATION = 'https://github.com/KirboHQ/UltimateMP3Player/issues'
        JS_RUNTIME_NAME = 'jsc'

        def _run_js_runtime(self, stdin, /):
            try:
                return _jsc.run(stdin)
            except Exception as e:
                raise JsChallengeProviderError(f'JavaScriptCore: {e}') from e

    register_provider(JavaScriptCoreJCP)
    register_preference(JavaScriptCoreJCP)(lambda provider, requests: 1000)
