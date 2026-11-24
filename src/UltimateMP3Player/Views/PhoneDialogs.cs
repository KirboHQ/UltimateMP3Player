#if AVALONIA
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using DialogKind = UltimateMP3Player.DialogWindow;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DialogKind = System.Windows.Window;
#endif
using System.Collections.Specialized;
using System.ComponentModel;
using UltimateMP3Player.Core;
using UltimateMP3Player.Core.Link;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Connect phone", in two states. No phone yet: three steps and the phones found nearby in "Connect to computer" (looked for while
// this is open), each with its Connect, the advice for Wi-Fi and cable folded under "Can't find it?". A phone: only it, whose music both
// show and where it's heard. Linked into the Avalonia app.
public static class PhoneDialogs
{
    private static Brush B(string key) => Both.Brush(key);

    private const string PhoneGlyph = "\uE8EA", ComputerGlyph = "\uE7F4", BothGlyph = "\uE71B";

    public static void Show(MainViewModel main) => Build(main).ShowDialog();

    // (apart, for the offscreen tests: the window without waiting for it)
    public static DialogKind Build(MainViewModel main)
    {
        var vm = main.PhoneLink;
        var body = new StackPanel();

        // ---- the one connected
        var name = new TextBlock { FontWeight = Both.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = Both.Text("", "Hint");
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Fill = B("SuccessBrush") };
        var spinner = Both.Spinner(12, new Thickness(0, 0, 8, 0));
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(spinner);
        statusRow.Children.Add(status);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        info.Children.Add(name);
        info.Children.Add(statusRow);
        var disconnect = Dialogs.Button(L.T("Disconnect"), "GhostButton");
        disconnect.Command = vm.DisconnectCommand;
        disconnect.VerticalAlignment = VerticalAlignment.Center;
        var current = Both.Columns(false, true, false);
        current.Children.Add(Both.Glyph(PhoneGlyph, 22, B("AccentTextBrush")));
        current.Children.Add(Both.At(info, 1));
        current.Children.Add(Both.At(disconnect, 2));
        var currentCard = new Border
        {
            CornerRadius = new CornerRadius(12), Background = B("AccentSoftBrush"), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 16),
            Child = current,
        };
        body.Children.Add(currentCard);

        // ---- whose music both show and where it's heard (the same on both devices: a change goes to the phone too)
        var music = Choices(L.T("Music shown"), (L.T("Both|f"), BothGlyph), (L.T("Phone only"), PhoneGlyph), (L.T("Computer only"), ComputerGlyph));
        var audio = Choices(L.T("Sound plays on"), (L.T("Computer"), ComputerGlyph), (L.T("Phone"), PhoneGlyph), (L.T("Both"), BothGlyph));
        var modes = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        modes.Children.Add(music.Panel);
        modes.Children.Add(audio.Panel);
        modes.Children.Add(Both.Text(L.T("The same on both devices: what you change here changes on the phone too."), "Hint", new Thickness(2, 2, 0, 0)));
        body.Children.Add(modes);
        bool filling = false;
        void ModeChosen(RadioButton chosen)
        {
            if (filling || main.Shared is not { } s) return;
            // (the one just chosen: Avalonia unchecks the one before it only after this event)
            int Picked(RadioButton[] group) => Array.IndexOf(group, chosen) is >= 0 and var i ? i : Array.FindIndex(group, b => b.IsChecked == true);
            int m = Picked(music.Buttons), a = Picked(audio.Buttons);
            if (m >= 0 && a >= 0) s.SetModes((LinkMusic)m, (LinkAudio)a);
        }
        foreach (var b in music.Buttons.Concat(audio.Buttons)) Both.OnTick(b, on => { if (on) ModeChosen(b); });

        // ---- no phone yet: the steps, the phones nearby, the advice folded
        var finding = new StackPanel();
        body.Children.Add(finding);
        finding.Children.Add(Step(1, L.T("Open Ultimate MP3 Player on the phone.")));
        finding.Children.Add(Step(2, L.T("Go to Settings › Connect to computer.")));
        finding.Children.Add(Step(3, L.T("The phone shows up below: Connect.")));
        finding.Children.Add(Both.Text(L.T("Phones nearby"), "FieldLabel", new Thickness(2, 14, 0, 8)));
        var list = new StackPanel();
        var looking = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 10, 10) };
        looking.Children.Add(Both.Spinner(14));
        looking.Children.Add(new TextBlock { Text = L.T("Looking for phones in “Connect to computer”…"), Foreground = B("SubTextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var both = new StackPanel();
        both.Children.Add(list);
        both.Children.Add(looking);
        finding.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = both, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
        });
        var advice = Both.Text(OperatingSystem.IsWindows()
            ? L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: it needs Apple Devices (or iTunes) on this computer.")
            : OperatingSystem.IsMacOS()
            ? L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: just the cable (the iPhone must trust this Mac).")
            : L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: it needs usbmuxd on this computer (the usbmuxd package)."),
            "Hint", new Thickness(2, 8, 0, 0));
        Both.Show(advice, false);
        var help = Dialogs.Button(L.T("Can't find it?"), "LinkButton");
        help.HorizontalAlignment = HorizontalAlignment.Left;
        help.Margin = new Thickness(2, 12, 0, 0);
        help.Click += (_, _) => Both.Show(advice, !Both.IsShown(advice));
        finding.Children.Add(help);
        finding.Children.Add(advice);
        finding.Children.Add(Both.Text(L.T("Each one then shows the other's music, the phone controls the music of this computer and receives music with “To the phone” in the export window."), "Hint", new Thickness(2, 12, 0, 0)));

        var close = Dialogs.Button(L.T("Close"), "GhostButton", isCancel: true);
        var win = Dialogs.Frame(L.T("Connect phone"), body, 520, close);

        void Refresh()
        {
            Both.Show(currentCard, vm.HasPhone);
            Both.Show(finding, !vm.HasPhone);
            name.Text = vm.PhoneName;
            status.Text = vm.StatusText;
            bool busy = vm.IsConnecting || vm.IsReconnecting;
            Both.Show(spinner, busy);
            Both.Show(dot, !busy);
            disconnect.Content = vm.IsConnecting ? L.T("Cancel") : L.T("Disconnect");
            Both.Show(modes, vm.IsLinked && main.Shared != null);
            if (main.Shared is { } shared)
            {
                filling = true;
                music.Buttons[(int)shared.Music].IsChecked = true;
                audio.Buttons[(int)shared.Audio].IsChecked = true;
                filling = false;
            }
            list.Children.Clear();
            foreach (var p in vm.Phones) list.Children.Add(Row(p, vm));
            Both.Show(looking, vm.Phones.Count == 0);
        }

        PropertyChangedEventHandler changed = (_, _) => Refresh();
        NotifyCollectionChangedEventHandler listChanged = (_, _) => Refresh();
        vm.PropertyChanged += changed;
        vm.Phones.CollectionChanged += listChanged;
        win.Closed += (_, _) =>
        {
            vm.PropertyChanged -= changed;
            vm.Phones.CollectionChanged -= listChanged;
            vm.StopScan();
        };
        vm.StartScan();
        Refresh();
        return win;
    }

    private static UIElement Row(PhoneRowViewModel p, PhoneLinkViewModel vm)
    {
        var grid = Both.Columns(false, true, false);
        grid.Margin = new Thickness(8, 6, 6, 6);
        grid.Children.Add(Both.Glyph(PhoneGlyph, 20, B("SubTextBrush")));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        text.Children.Add(new TextBlock { Text = p.Name, FontWeight = Both.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = Both.Text(p.Detail, "Hint");
        detail.FontSize = 12;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        grid.Children.Add(Both.At(text, 1));
        var connect = Dialogs.Button(L.T("Connect"), "PrimaryButton");
        connect.Command = p.ConnectCommand;
        connect.IsEnabled = !vm.IsConnecting;
        connect.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(Both.At(connect, 2));
        return grid;
    }

    // A step of the three: its number in a circle, then what to do.
    private static UIElement Step(int n, string text)
    {
        var grid = Both.Columns(false, true);
        grid.Margin = new Thickness(0, 0, 0, 8);
        var number = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        number.Children.Add(new Ellipse { Fill = B("AccentSoftBrush") });
        number.Children.Add(new TextBlock { Text = n.ToString(), FontWeight = Both.SemiBold, Foreground = B("AccentTextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(number);
        grid.Children.Add(Both.At(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }, 1));
        return grid;
    }

    // A label and its choices in a pill (one ticked).
    private static (StackPanel Panel, RadioButton[] Buttons) Choices(string label, params (string Text, string Glyph)[] options)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new RadioButton[options.Length];
        for (int i = 0; i < options.Length; i++)
        {
            var r = buttons[i] = Both.Look(new RadioButton { Content = options[i].Text, GroupName = group, Margin = new Thickness(i == 0 ? 0 : 4, 0, 0, 0) }, "Segment");
            Ui.SetGlyph(r, options[i].Glyph);
            row.Children.Add(r);
        }
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(Both.Text(label, "FieldLabel", new Thickness(2, 0, 0, 6)));
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(20), Padding = new Thickness(4), Background = B("BgBrush"), BorderBrush = B("BorderBrush"),
            BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left, Child = row,
        });
        return (panel, buttons);
    }
}
