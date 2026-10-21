using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// Settings › "Connect to computer": while it's open the computers nearby can find this phone (Connect phone, on the
// computer); then it says to which computer it's connected, with "Disconnect".
public static class LinkSheet
{
    private static bool _open;
    private const string PhoneGlyph = "\uE8EA", ComputerGlyph = "\uE7F4", BothGlyph = "\uE71B";

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
        linked.Children.Add(Ui.Text(L.T("Use the player as always, also from the lock screen: it controls the computer. Each one shows the other's music; the computer can send you music with “To the phone” in its export window."),
            "hint", new Thickness(0, 0, 0, 16)));
        // Whose music both show and where it's heard (the same on both devices: a change goes to the computer too).
        var music = Choices(L.T("Music shown"), (L.T("Both|f"), BothGlyph), (L.T("Phone"), PhoneGlyph), (L.T("Computer"), ComputerGlyph));
        var audio = Choices(L.T("Sound plays on"), (L.T("Computer"), ComputerGlyph), (L.T("Phone"), PhoneGlyph), (L.T("Both"), BothGlyph));
        linked.Children.Add(music.Panel);
        linked.Children.Add(audio.Panel);
        linked.Children.Add(Ui.Text(L.T("The same on both devices: what you change here changes on the computer too."), "hint", new Thickness(0, 0, 0, 16)));
        bool filling = false;
        void ModeChosen()
        {
            if (filling || App.Host.Session?.Shared is not { } s) return;
            int m = Array.FindIndex(music.Buttons, b => b.IsChecked == true), a = Array.FindIndex(audio.Buttons, b => b.IsChecked == true);
            if (m >= 0 && a >= 0) s.SetModes((LinkMusic)m, (LinkAudio)a);
        }
        foreach (var b in music.Buttons.Concat(audio.Buttons))
            b.IsCheckedChanged += (_, _) =>
            {
                if (b.IsChecked == true) ModeChosen();
            };
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
            if (App.Host.Session?.Shared is { } shared)
            {
                filling = true;
                music.Buttons[(int)shared.Music].IsChecked = true;
                audio.Buttons[(int)shared.Audio].IsChecked = true;
                filling = false;
            }
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

    // A label and its choices side by side (one ticked).
    private static (StackPanel Panel, RadioButton[] Buttons) Choices(string label, params (string Text, string Glyph)[] options)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var row = new UniformGrid { Rows = 1 };
        var buttons = new RadioButton[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var r = buttons[i] = new RadioButton
            {
                Theme = Ui.Theme("TouchSegment"), Content = options[i].Text, GroupName = group,
                Margin = new Thickness(i > 0 && i < options.Length - 1 ? 4 : 0, 0, i > 0 && i < options.Length - 1 ? 4 : 0, 0),
            };
            Ui.SetGlyph(r, options[i].Glyph);
            row.Children.Add(r);
        }
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(Ui.Text(label, "field-label", new Thickness(4, 0, 0, 6)));
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(22), Padding = new Thickness(4), Background = Ui.Res("Surface2Brush"), Child = row });
        return (panel, buttons);
    }

    private static TextBlock Glyph(string glyph, double size, IBrush brush)
    {
        var t = new TextBlock { Text = Icons.Map(glyph), FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("glyph");
        return t;
    }
}
