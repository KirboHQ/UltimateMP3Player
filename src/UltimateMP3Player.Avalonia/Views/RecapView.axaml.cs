using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The recap (RecapViewModel) as stories: each page in its own colours, its content coming in from below, the minutes
// counted up, the hours' bars growing. A tap on the right goes on, on the left back (the arrows too); the summary is saved
// as a picture (the phones: handed to the share sheet).
public partial class RecapView : UserControl
{
    // Each page's two colours (the same as WPF's).
    private static readonly Dictionary<string, (string From, string To)> Colours = new()
    {
        ["intro"] = ("#7C5CFF", "#FF4FA3"), ["time"] = ("#1E9BFF", "#12B5A5"), ["song"] = ("#FF7A1A", "#E5484D"),
        ["songs"] = ("#E5484D", "#7C5CFF"), ["artist"] = ("#1DB954", "#1E9BFF"), ["artists"] = ("#12B5A5", "#3E63DD"),
        ["clock"] = ("#F5B400", "#FF7A1A"), ["summary"] = ("#3E63DD", "#B45CFF"),
    };

    private RecapViewModel? _vm;
    // (a newer page stops the movements of the one before)
    private int _run;

    public RecapView()
    {
        InitializeComponent();
#if ANDROID_APP || IOS_APP
        ShareIcon.Text = Icons.Map("\uE72D");
        ShareText.Text = L.T("Share");
#endif
        DataContextChanged += (_, _) => Attach();
        Pages.AddHandler(PointerReleasedEvent, Pages_Released, RoutingStrategies.Bubble);
        KeyDown += OnKey;
        AttachedToVisualTree += (_, _) =>
        {
            Focus();
            Show();
        };
    }

    private void Attach()
    {
        if (_vm != null) _vm.PropertyChanged -= OnVm;
        _vm = DataContext as RecapViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVm;
        Show();
    }

    private void OnVm(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecapViewModel.Page) or "" or null) Dispatcher.UIThread.Post(Show, DispatcherPriority.Loaded);
    }

    private void Show()
    {
        if (_vm == null || this.GetVisualRoot() == null) return;
        var (from, to) = Colours.GetValueOrDefault(_vm.Page, Colours["intro"]);
        Card.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(from), 0), new GradientStop(Color.Parse(to), 1) },
        };
        Control page = _vm.Page switch
        {
            "intro" => Intro, "time" => Time, "song" => Song, "songs" => SongsPage, "artist" => Artist, "artists" => ArtistsPage, "clock" => Clock,
            _ => SummaryPage,
        };
        int run = ++_run;
        var grow = BarsHost.RenderTransform as ScaleTransform;
        if (Ui.Animations)
        {
            var move = new TranslateTransform(0, 30);
            page.RenderTransform = move;
            Ui.Tween(page, 450, Ui.CubicOut, t =>
            {
                if (run != _run) return;
                page.Opacity = Math.Min(1, t * 1.4);
                move.Y = 30 * (1 - t);
            });
            if (_vm.IsClock && grow != null)
            {
                grow.ScaleY = 0;
                Ui.Tween(BarsHost, 700, Ui.CubicOut, t => { if (run == _run) grow.ScaleY = t; });
            }
        }
        else
        {
            page.Opacity = 1;
            page.RenderTransform = null;
            if (grow != null) grow.ScaleY = 1;
        }
        if (_vm.IsTime) CountUp(_vm.Minutes, run);
    }

    // The minutes counted up from zero (1.2 s, slowing down at the end).
    private void CountUp(int target, int run)
    {
        if (!Ui.Animations || target <= 0)
        {
            MinutesBig.Text = target.ToString("N0", L.Culture);
            return;
        }
        Ui.Tween(MinutesBig, 1200, Ui.CubicOut, t =>
        {
            if (run == _run) MinutesBig.Text = ((int)Math.Round(target * t)).ToString("N0", L.Culture);
        });
    }

    private void Pages_Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm == null || e.Pointer.Type == PointerType.Mouse && e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Source is Visual v && (v.FindAncestorOfType<Button>(true) != null || v.FindAncestorOfType<ComboBox>(true) != null)) return;
        if (e.GetPosition(Pages).X < Pages.Bounds.Width * 0.35) _vm.PrevCommand.Execute(null);
        else _vm.NextCommand.Execute(null);
        e.Handled = true;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        switch (e.Key)
        {
            case Key.Right or Key.Space or Key.PageDown: _vm.NextCommand.Execute(null); break;
            case Key.Left or Key.PageUp: _vm.PrevCommand.Execute(null); break;
            case Key.Escape: _vm.CloseCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Share_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        try
        {
            var picture = Picture();
#if ANDROID_APP || IOS_APP
            Directory.CreateDirectory(UltimateMP3Player.Platform.Files.ShareDir);
            var path = Path.Combine(UltimateMP3Player.Platform.Files.ShareDir, _vm.ShareName);
            picture.Save(path);
            UltimateMP3Player.Platform.Files.Share(path, "image/png", _vm.ShareName);
#else
            var path = Dialogs.PickSave(L.T("Save the picture"), _vm.ShareName, "PNG", ".png");
            if (path == null) return;
            picture.Save(path);
            _vm.Toast(L.T("Picture saved"));
#endif
        }
        catch (Exception ex) { _vm.Toast(L.F("Couldn't save the picture: {0}", ex.Message)); }
    }

    // The summary on the page's colours, three times its size on screen.
    private RenderTargetBitmap Picture()
    {
        const double scale = 3;
        var size = Summary.Bounds.Size;
        var veil = Summary.Background;
        Summary.Background = Card.Background;
        try
        {
            var bmp = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
            bmp.Render(Summary);
            return bmp;
        }
        finally { Summary.Background = veil; }
    }
}
