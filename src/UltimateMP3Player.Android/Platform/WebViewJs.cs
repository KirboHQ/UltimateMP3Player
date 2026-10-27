using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Android.OS;
using Android.Webkit;

namespace UltimateMP3Player.Platform;

// YouTube's challenges in the system's WebView (WebJs). yt-dlp runs in a process of its own: its provider (AndroidEngines'
// Boot) sends each script here on a loopback socket (port and key in UMP_WEBJS) and gets the output back. A WebView that
// fails or doesn't answer is left alone for the session: the provider steps aside and yt-dlp uses QuickJS.
internal static class WebViewJs
{
    private static readonly Handler Main = new(Looper.MainLooper!);
    // (one script at a time: the WebViews share one renderer anyway, and a solve takes a lot of memory)
    private static readonly SemaphoreSlim Turn = new(1, 1);
    private static int _started;
    private static volatile bool _broken;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            // (other apps can reach a loopback port too: only yt-dlp knows the key)
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            System.Environment.SetEnvironmentVariable("UMP_WEBJS", $"{((IPEndPoint)listener.LocalEndpoint).Port}:{key}");
            _ = AcceptAsync(listener, Encoding.ASCII.GetBytes(key));
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static async Task AcceptAsync(TcpListener listener, byte[] key)
    {
        try
        {
            while (true) _ = ServeAsync(await listener.AcceptTcpClientAsync(), key);
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // In: the key, the script's length (4 bytes, big-endian), the script (UTF-8). Out: 0 or 1 (failed), the length, the
    // output or why not.
    private static async Task ServeAsync(TcpClient client, byte[] key)
    {
        try
        {
            var stream = client.GetStream();
            var head = new byte[key.Length + 4];
            await stream.ReadExactlyAsync(head);
            if (!CryptographicOperations.FixedTimeEquals(head.AsSpan(0, key.Length), key)) return;
            var script = new byte[BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(key.Length))];
            await stream.ReadExactlyAsync(script);
            byte failed = 0;
            string text;
            try { text = await RunAsync(Encoding.UTF8.GetString(script)); }
            catch (Exception ex) { failed = 1; text = ex.Message; }
            var body = Encoding.UTF8.GetBytes(text);
            var reply = new byte[5 + body.Length];
            reply[0] = failed;
            BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(1), body.Length);
            body.CopyTo(reply, 5);
            await stream.WriteAsync(reply);
        }
        catch { } // (yt-dlp gone meanwhile)
        finally { client.Dispose(); }
    }

    private static async Task<string> RunAsync(string script)
    {
        await Turn.WaitAsync();
        try
        {
            if (_broken) throw new InvalidOperationException("off");
            var code = WebJs.Wrap(script);
            var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            WebView? view = null;
            Main.Post(() =>
            {
                try
                {
                    view = new WebView(Android.App.Application.Context);
                    view.Settings.JavaScriptEnabled = true;
                    view.Settings.BlockNetworkLoads = true;
                    view.SetWebViewClient(new Page(code, answer));
                    view.LoadDataWithBaseURL(null, "<html></html>", "text/html", "utf-8", null);
                }
                catch (Exception ex) { answer.TrySetException(ex); }
            });
            string value;
            try { value = await answer.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (Exception ex)
            {
                _broken = true;
                App.Log(ex);
                throw;
            }
            finally { Main.Post(() => view?.Destroy()); }
            // (a JSON value: the script's output, or {error} if it failed: QuickJS will say why)
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString()!
                : throw new InvalidOperationException(doc.RootElement.GetProperty("error").GetString());
        }
        finally { Turn.Release(); }
    }

    // The empty page loaded: the script in it, once.
    private sealed class Page : WebViewClient
    {
        private readonly string _code;
        private readonly TaskCompletionSource<string> _answer;
        private bool _sent;

        public Page(string code, TaskCompletionSource<string> answer)
        {
            _code = code;
            _answer = answer;
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            if (_sent || view == null) return;
            _sent = true;
            view.EvaluateJavascript(_code, new Result(_answer));
        }

        // A renderer that crashed or was ended for memory: without this, Android ends the app with it.
        public override bool OnRenderProcessGone(WebView? view, RenderProcessGoneDetail? detail)
        {
            _answer.TrySetException(new InvalidOperationException("WebView: the page's process ended"));
            return true;
        }
    }

    private sealed class Result : Java.Lang.Object, IValueCallback
    {
        private readonly TaskCompletionSource<string> _answer;
        public Result(TaskCompletionSource<string> answer) => _answer = answer;

        public void OnReceiveValue(Java.Lang.Object? value)
        {
            // (null: the page didn't get to answer)
            var json = value?.ToString();
            if (json is null or "null") _answer.TrySetException(new InvalidOperationException("WebView: no answer"));
            else _answer.TrySetResult(json);
        }
    }
}
