using Avalonia;
using Avalonia.Logging;
using Avalonia.Media;

namespace UltimateMP3Player;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        App.Args = args;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Segoe UI on Windows; elsewhere Selawik, Microsoft's open font made with Segoe UI's metrics (same sizes and spacing).
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : "avares://UltimateMP3Player/Assets/Fonts#Selawik",
            })
            .With(new X11PlatformOptions { WmClass = "ultimate-mp3-player" })
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
        // UMP_LOG=<file>: Avalonia's warnings (bindings, layout) go there, for debugging.
        if (Environment.GetEnvironmentVariable("UMP_LOG") is { Length: > 0 } log)
            builder.AfterSetup(_ => Logger.Sink = new FileSink(log));
        return builder;
    }

    private sealed class FileSink(string path) : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, Array.Empty<object?>());

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            try
            {
                var text = messageTemplate;
                foreach (var v in propertyValues)
                {
                    int a = text.IndexOf('{'), b = a < 0 ? -1 : text.IndexOf('}', a);
                    if (b < 0) break;
                    text = text[..a] + v + text[(b + 1)..];
                }
                File.AppendAllText(path, $"[{level}] {area} {source?.GetType().Name}: {text}\n");
            }
            catch { }
        }
    }
}
