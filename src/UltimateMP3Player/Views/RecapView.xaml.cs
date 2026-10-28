using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// The recap (RecapViewModel) as stories: each page in its own colours, its content coming in from below, the minutes
// counted up, the hours' bars growing. A click on the right goes on, on the left back (the arrows too); the summary is
// saved as a picture. Avalonia's twin (also the phones'): Avalonia/Views/RecapView.axaml.
public partial class RecapView : UserControl
{
    // Each page's two colours.
    private static readonly Dictionary<string, (string From, string To)> Colours = new()
    {
        ["intro"] = ("#7C5CFF", "#FF4FA3"), ["time"] = ("#1E9BFF", "#12B5A5"), ["song"] = ("#FF7A1A", "#E5484D"),
        ["songs"] = ("#E5484D", "#7C5CFF"), ["artist"] = ("#1DB954", "#1E9BFF"), ["artists"] = ("#12B5A5", "#3E63DD"),
        ["clock"] = ("#F5B400", "#FF7A1A"), ["summary"] = ("#3E63DD", "#B45CFF"),
    };

    private RecapViewModel? _vm;
    private DispatcherTimer? _count;

    public RecapView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) =>
        {
            Focus();
            Show();
        };
        Unloaded += (_, _) => _count?.Stop();
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
        if (e.PropertyName is nameof(RecapViewModel.Page) or "" or null) Dispatcher.BeginInvoke(Show, DispatcherPriority.Loaded);
    }

    private void Show()
    {
        if (_vm == null || !IsLoaded) return;
        var (from, to) = Colours.GetValueOrDefault(_vm.Page, Colours["intro"]);
        Card.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 45);
        FrameworkElement page = _vm.Page switch
        {
            "intro" => Intro, "time" => Time, "song" => Song, "songs" => SongsPage, "artist" => Artist, "artists" => ArtistsPage, "clock" => Clock,
            _ => SummaryPage,
        };
        if (Ui.Animations)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
            var move = new TranslateTransform(0, 30);
            page.RenderTransform = move;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(30, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
            if (_vm.IsClock && BarsHost.RenderTransform is ScaleTransform grow)
                grow.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(150) });
        }
        if (_vm.IsTime) CountUp(_vm.Minutes);
    }

    // The minutes counted up from zero (1.2 s, slowing down at the end).
    private void CountUp(int target)
    {
        _count?.Stop();
        if (!Ui.Animations || target <= 0)
        {
            MinutesBig.Text = target.ToString("N0", L.Culture);
            return;
        }
        var started = DateTime.Now;
        var timer = _count = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, (DateTime.Now - started).TotalMilliseconds / 1200);
            MinutesBig.Text = ((int)Math.Round(target * (1 - Math.Pow(1 - t, 3)))).ToString("N0", L.Culture);
            if (t >= 1) timer.Stop();
        };
        timer.Start();
    }

    private void Pages_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (e.GetPosition(Pages).X < Pages.ActualWidth * 0.35) _vm.PrevCommand.Execute(null);
        else _vm.NextCommand.Execute(null);
    }

    private void OnKey(object sender, KeyEventArgs e)
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

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var dlg = new SaveFileDialog { FileName = _vm.ShareName, Filter = "PNG|*.png", DefaultExt = ".png" };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            using (var file = File.Create(dlg.FileName)) Picture().Save(file);
            _vm.Toast(L.T("Picture saved"));
        }
        catch (Exception ex) { _vm.Toast(L.F("Couldn't save the picture: {0}", ex.Message)); }
    }

    // The summary on the page's colours, three times its size on screen.
    private PngBitmapEncoder Picture()
    {
        double w = Summary.ActualWidth, h = Summary.ActualHeight;
        const double scale = 3;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRoundedRectangle(Card.Background, null, new Rect(0, 0, w, h), 18, 18);
            dc.DrawRectangle(new VisualBrush(Summary), null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        return png;
    }
}
