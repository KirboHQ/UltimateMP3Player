// The few WPF names the shared files (view models, audio engine, lyrics) use, mapped onto Avalonia, so the same source
// compiles in both apps. Only those files import these namespaces; the Avalonia code uses Avalonia's own types.

global using Brush = Avalonia.Media.IBrush;
global using ImageSource = Avalonia.Media.IImage;
global using BitmapSource = Avalonia.Media.Imaging.Bitmap;
global using Color = Avalonia.Media.Color;
global using UIElement = Avalonia.Controls.Control;

using AvDispatcher = Avalonia.Threading.Dispatcher;
using AvPriority = Avalonia.Threading.DispatcherPriority;

namespace System.Windows
{
    // Application.Current.Dispatcher / .Resources of the shared code.
    public sealed class Application
    {
        public static Application Current { get; } = new();

        public Threading.Dispatcher Dispatcher { get; } = new();

        public ResourceLookup Resources { get; } = new();
    }

    // Reads and writes the resources of the Avalonia application (the brushes of the theme).
    public sealed class ResourceLookup
    {
        // Not nullable, like WPF's (the shared code casts what it reads).
        public object this[string key]
        {
            get => (Avalonia.Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) ? v : null)!;
            set
            {
                if (Avalonia.Application.Current is { } app) app.Resources[key] = value;
            }
        }
    }
}

namespace System.Windows.Threading
{
    public enum DispatcherPriority
    {
        SystemIdle, ApplicationIdle, ContextIdle, Background, Input, Loaded, Render, DataBind, Normal, Send,
    }

    internal static class Priorities
    {
        public static AvPriority Map(DispatcherPriority p) => p switch
        {
            DispatcherPriority.SystemIdle => AvPriority.SystemIdle,
            DispatcherPriority.ApplicationIdle => AvPriority.ApplicationIdle,
            DispatcherPriority.ContextIdle => AvPriority.ContextIdle,
            DispatcherPriority.Background => AvPriority.Background,
            DispatcherPriority.Input => AvPriority.Input,
            DispatcherPriority.Loaded => AvPriority.Loaded,
            DispatcherPriority.Render => AvPriority.Render,
            DispatcherPriority.DataBind => AvPriority.Default,
            DispatcherPriority.Send => AvPriority.Send,
            _ => AvPriority.Default,
        };
    }

    // The UI thread's dispatcher.
    public sealed class Dispatcher
    {
        public void BeginInvoke(Action a) => AvDispatcher.UIThread.Post(a);
        public void BeginInvoke(Action a, DispatcherPriority p) => AvDispatcher.UIThread.Post(a, Priorities.Map(p));
        public void BeginInvoke(DispatcherPriority p, Action a) => AvDispatcher.UIThread.Post(a, Priorities.Map(p));
        public bool CheckAccess() => AvDispatcher.UIThread.CheckAccess();
        public void Invoke(Action a) => AvDispatcher.UIThread.Invoke(a);
        public Task InvokeAsync(Action a) => AvDispatcher.UIThread.InvokeAsync(a).GetTask();
        public Task InvokeAsync(Action a, DispatcherPriority p) => AvDispatcher.UIThread.InvokeAsync(a, Priorities.Map(p)).GetTask();
    }

    public sealed class DispatcherTimer
    {
        private readonly Avalonia.Threading.DispatcherTimer _t;

        public DispatcherTimer() : this(DispatcherPriority.Background) { }

        public DispatcherTimer(DispatcherPriority priority)
        {
            _t = new Avalonia.Threading.DispatcherTimer(Priorities.Map(priority));
            _t.Tick += (s, e) => Tick?.Invoke(this, e);
        }

        public event EventHandler? Tick;

        public TimeSpan Interval { get => _t.Interval; set => _t.Interval = value; }
        public bool IsEnabled { get => _t.IsEnabled; set => _t.IsEnabled = value; }
        public void Start() => _t.Start();
        public void Stop() => _t.Stop();
    }
}

namespace System.Windows.Input
{
    // WPF asks the buttons to look at their commands again after any input; here the code says when (the shared code
    // already calls InvalidateRequerySuggested wherever a command's state changes).
    public static class CommandManager
    {
        public static event EventHandler? RequerySuggested;

        public static void InvalidateRequerySuggested()
        {
            if (AvDispatcher.UIThread.CheckAccess()) RequerySuggested?.Invoke(null, EventArgs.Empty);
            else AvDispatcher.UIThread.Post(() => RequerySuggested?.Invoke(null, EventArgs.Empty));
        }
    }
}

namespace System.Windows.Media
{
    // (Brush, ImageSource and Color are aliases of Avalonia's, above.)
    internal static class MediaShim { }
}

namespace System.Windows.Media.Imaging
{
    internal static class ImagingShim { }
}
