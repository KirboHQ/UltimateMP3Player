using System.Text.Encodings.Web;
using System.Text.Json;

namespace UltimateMP3Player.Platform;

// YouTube's challenges (yt-dlp's EJS scripts) in the system's web engine, whose JIT solves them several times faster than
// QuickJS (Android) or the app's own JavaScriptCore (iPhone): the WebView's V8 (WebViewJs), WKWebView (WebKitJs). The
// script runs in an empty page; what it prints with console.log is yt-dlp's answer, as from deno's output. Anything
// wrong → yt-dlp goes on with the engine it had before.
internal static class WebJs
{
    // The page's code: the script's output as a string, or {error} if the script throws.
    public static string Wrap(string script) => $$"""
        (function () {
          var out = [], before = globalThis.console;
          function log() { out.push(Array.prototype.map.call(arguments, String).join(' ')); }
          function none() {}
          function set(console) { Object.defineProperty(globalThis, 'console', { value: console, writable: true, configurable: true }); }
          set({ log: log, info: log, debug: none, warn: none, error: none, trace: none });
          try {
            (0, eval)("{{JsonEncodedText.Encode(script, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}}");
            return out.join('\n');
          } catch (e) {
            return { error: String(e && e.stack || e) };
          } finally {
            set(before);
          }
        })()
        """;
}
