using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using UIKit;
using WebKit;

namespace UltimateMP3Player.Platform;

// YouTube's challenges in WKWebView (WebJs): its JavaScriptCore has the JIT that the app's own (ump_ios.py's
// _JavaScriptCore) can't have. Python calls Run from a run's thread (ctypes, without Python's lock) and waits; null →
// it solves them itself as before: the app not in the foreground (iOS stops web pages in the background), a script that
// fails, a page that doesn't answer (then off for the session).
internal static unsafe class WebKitJs
{
    private static readonly object Turn = new();
    private static volatile bool _broken;

    // For Python (Platform/Python.cs's setup): run(script, length) → UTF-8 output or null, free(output).
    public static IntPtr RunPointer => (IntPtr)(delegate* unmanaged[Cdecl]<byte*, int, byte*>)&Run;
    public static IntPtr FreePointer => (IntPtr)(delegate* unmanaged[Cdecl]<byte*, void>)&Free;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte* Run(byte* script, int length)
    {
        try
        {
            if (_broken || NSThread.IsMain) return null;
            var code = WebJs.Wrap(Encoding.UTF8.GetString(script, length));
            // (one at a time, like _JavaScriptCore)
            lock (Turn)
            {
                var output = Evaluate(code);
                return output == null ? null : (byte*)Marshal.StringToCoTaskMemUTF8(output);
            }
        }
        catch { return null; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Free(byte* output) => Marshal.FreeCoTaskMem((IntPtr)output);

    private static string? Evaluate(string code)
    {
        var answer = new TaskCompletionSource<string?>();
        WKWebView? view = null;
        DispatchQueue.MainQueue.DispatchAsync(() =>
        {
            try
            {
                if (!Active())
                {
                    answer.TrySetResult(null);
                    return;
                }
                // (a store of its own: nothing of the sites the user signed in to)
                var config = new WKWebViewConfiguration { WebsiteDataStore = WKWebsiteDataStore.NonPersistentDataStore };
                view = new WKWebView(CGRect.Empty, config) { NavigationDelegate = new Page(code, answer) };
                view.LoadHtmlString("<html></html>", null!);
            }
            catch (Exception ex) { answer.TrySetException(ex); }
        });
        try
        {
            // (a second or two with the JIT; long enough also for an old iPhone, short for one that went to the background)
            return answer.Task.Wait(TimeSpan.FromSeconds(15)) ? answer.Task.Result : throw new TimeoutException("WKWebView didn't answer");
        }
        catch (Exception ex)
        {
            // (unless the app went to the background meanwhile)
            bool active = false;
            DispatchQueue.MainQueue.DispatchSync(() => active = Active());
            if (active)
            {
                _broken = true;
                App.Log(ex);
            }
            return null;
        }
        finally { DispatchQueue.MainQueue.DispatchAsync(() => view?.Dispose()); }
    }

    private static bool Active() => UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active;

    // The empty page loaded: the script in it, once.
    private sealed class Page : WKNavigationDelegate
    {
        private readonly string _code;
        private readonly TaskCompletionSource<string?> _answer;
        private bool _sent;

        public Page(string code, TaskCompletionSource<string?> answer)
        {
            _code = code;
            _answer = answer;
        }

        public override void DidFinishNavigation(WKWebView webView, WKNavigation navigation)
        {
            if (_sent) return;
            _sent = true;
            webView.EvaluateJavaScript(_code, (result, error) =>
            {
                // (a string: the output; {error}: the script failed, _JavaScriptCore will say why)
                if (result is NSString output) _answer.TrySetResult(output.ToString());
                else if (result != null) _answer.TrySetResult(null);
                else _answer.TrySetException(new InvalidOperationException("WKWebView: " + (error?.LocalizedDescription ?? "no answer")));
            });
        }

        public override void ContentProcessDidTerminate(WKWebView webView) =>
            _answer.TrySetException(new InvalidOperationException("WKWebView: the page's process ended"));
    }
}
