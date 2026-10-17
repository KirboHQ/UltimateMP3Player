using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Settings › "Connect to computer": while it's open the computers nearby can find this phone (Connect phone, on the
// computer); then it says to which computer it's connected, with "Disconnect".
public static class LinkSheet
{
    private static bool _open;

    public static void Show()
    {
        if (_open || App.Host.View?.Sheets is not { } sheets || App.Host.Session?.ComputerLink is not { } vm) return;
        _open = true;
        var body = new StackPanel { Margin = new Thickness(20, 4, 20, 18) };
        body.Children.Add(new TextBlock { Text = L.T("Connect to computer"), FontSize = 20, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) });

        // A phone and a computer, side by side.
        var art = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 14) };
        art.Children.Add(Glyph("\uE8EA", 34, Ui.Res("AccentTextBrush")));
        var dots = new TextBlock { Text = "· · ·", FontSize = 22, Foreground = Ui.Res("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 16, 0) };
        art.Children.Add(dots);
        art.Children.Add(Glyph("\uE7F4", 40, Ui.Res("AccentTextBrush")));
        body.Children.Add(art);

        var spinner = new Ellipse { Width = 14, Height = 14, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        spinner.Classes.Add("spinner");
        var dot = new Ellipse { Width = 9, Height = 9, Fill = Ui.Res("SuccessBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var status = new TextBlock { FontSize = 15.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var statusRow = new DockPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
        statusRow.Children.Add(spinner);
        statusRow.Children.Add(dot);
        statusRow.Children.Add(status);
        body.Children.Add(statusRow);

        var waiting = new StackPanel();
        waiting.Children.Add(Ui.Text(L.F("This phone is visible as “{0}”. On the computer, in Ultimate MP3 Player, open Settings › Connect phone and choose it: then allow it here.", vm.MyName),
            "hint", new Thickness(0, 0, 0, 8)));
#if ANDROID_APP
        waiting.Children.Add(Ui.Text(L.T("Same Wi-Fi as the computer. With a USB cable there's nothing to allow: plug the phone into the computer and turn on USB tethering (Settings › Network › Hotspot & tethering)."), "hint"));
#else
        waiting.Children.Add(Ui.Text(L.T("Same Wi-Fi as the computer. With the cable there's nothing to allow: plug the iPhone into the computer (on Windows it needs Apple Devices or iTunes)."), "hint"));
#endif
        waiting.Children.Add(Ui.Text(L.T("Keep this page open until the computer has connected."), "hint", new Thickness(0, 8, 0, 0)));
        body.Children.Add(waiting);

        var linked = new StackPanel();
        linked.Children.Add(Ui.Text(L.T("The music plays on the computer and this phone stays silent: use the player as always, also from the lock screen. Your songs go to the computer by themselves; the computer can send you music with “To the phone” in its export window."),
            "hint", new Thickness(0, 0, 0, 16)));
        var disconnect = new Button
        {
            Theme = Ui.Theme("TouchGhost"), Content = L.T("Disconnect"), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Command = vm.DisconnectCommand,
        };
        linked.Children.Add(disconnect);
        body.Children.Add(linked);

        var error = Ui.Text("", "hint", new Thickness(0, 12, 0, 0));
        error.Foreground = Ui.Res("ErrorBrush");
        body.Children.Add(error);

        void Refresh()
        {
            bool on = vm.IsLinked;
            status.Text = vm.StatusText;
            spinner.IsVisible = !on || vm.IsReconnecting;
            dot.IsVisible = on && !vm.IsReconnecting;
            dots.Foreground = on ? Ui.Res("SuccessBrush") : Ui.Res("MutedBrush");
            waiting.IsVisible = !on;
            linked.IsVisible = on;
            error.Text = vm.Error is { } e ? L.T("The phone can't wait for computers:") + " " + e : "";
            error.IsVisible = vm.Error != null && !on;
        }

        PropertyChangedEventHandler changed = (_, _) => Refresh();
        vm.PropertyChanged += changed;
        vm.Visible = true;
        Refresh();
        _ = sheets.Show(new ScrollViewer { Content = body }, maxHeightShare: 0.92).ContinueWith(_ =>
        {
            vm.PropertyChanged -= changed;
            vm.Visible = false;
            _open = false;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static TextBlock Glyph(string glyph, double size, IBrush brush)
    {
        var t = new TextBlock { Text = Icons.Map(glyph), FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("glyph");
        return t;
    }
}
