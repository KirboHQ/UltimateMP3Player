using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Connect phone": the phone connected, and the phones nearby in "Connect to computer" (looked for while this is open).
public static class PhoneDialogs
{
    private static IBrush B(string key) => Ui.Res(key);

    private const string PhoneGlyph = "\uE8EA";

    public static void Show(MainViewModel main)
    {
        var vm = main.PhoneLink;
        var body = new StackPanel();

        // ---- the one connected
        var name = new TextBlock { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = Ui.Text("", "hint");
        status.TextWrapping = TextWrapping.Wrap;
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Fill = B("SuccessBrush") };
        var spinner = new Ellipse { Width = 12, Height = 12, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        spinner.Classes.Add("spinner");
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
        var current = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        current.Children.Add(Glyph(PhoneGlyph, 22, B("AccentTextBrush")));
        current.Children.Add(At(info, 1));
        current.Children.Add(At(disconnect, 2));
        var currentCard = new Border
        {
            CornerRadius = new CornerRadius(12), Background = B("AccentSoftBrush"), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 16),
            Child = current,
        };
        body.Children.Add(currentCard);

        var hint = Ui.Text(L.T("On the phone open Settings › Connect to computer: it shows up here. The phone controls the music of this computer (its songs play here, its sound stays off) and receives music with “To the phone” in the export window."), "hint");
        hint.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(hint);
        var cable = Ui.Text(OperatingSystem.IsMacOS()
            ? L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: just the cable (the iPhone must trust this Mac).")
            : L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: it needs usbmuxd on this computer (the usbmuxd package)."),
            "hint", new Thickness(0, 8, 0, 0));
        cable.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(cable);

        // ---- the phones nearby
        body.Children.Add(Ui.Text(L.T("Phones nearby"), "field-label", new Thickness(2, 18, 0, 8)));
        var list = new StackPanel();
        var looking = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
        var lookingSpinner = new Ellipse { Width = 14, Height = 14, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
        lookingSpinner.Classes.Add("spinner");
        looking.Children.Add(lookingSpinner);
        looking.Children.Add(new TextBlock
        {
            Text = L.T("Looking for phones in “Connect to computer”…"), Foreground = B("SubTextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        var both = new StackPanel();
        both.Children.Add(list);
        both.Children.Add(looking);
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = both, MaxHeight = 260 },
        });

        var close = Dialogs.Button(L.T("Close"), "GhostButton", isCancel: true);
        var win = Dialogs.Frame(L.T("Connect phone"), body, 520, close);

        void Refresh()
        {
            currentCard.IsVisible = vm.HasPhone;
            name.Text = vm.PhoneName;
            status.Text = vm.StatusText;
            bool busy = vm.IsConnecting || vm.IsReconnecting;
            spinner.IsVisible = busy;
            dot.IsVisible = !busy;
            disconnect.Content = vm.IsConnecting ? L.T("Cancel") : L.T("Disconnect");
            list.Children.Clear();
            foreach (var p in vm.Phones) list.Children.Add(Row(p, vm));
            looking.IsVisible = vm.Phones.Count == 0;
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
        win.ShowDialog();
    }

    private static Control Row(PhoneRowViewModel p, PhoneLinkViewModel vm)
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 6, 6), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(Glyph(PhoneGlyph, 20, B("SubTextBrush")));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        text.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var detail = Ui.Text(p.Detail, "hint");
        detail.FontSize = 12;
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        grid.Children.Add(At(text, 1));
        var connect = Dialogs.Button(L.T("Connect"), "PrimaryButton");
        connect.Command = p.ConnectCommand;
        connect.IsEnabled = !vm.IsConnecting;
        connect.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(At(connect, 2));
        return grid;
    }

    private static TextBlock Glyph(string glyph, double size, IBrush brush)
    {
        var t = new TextBlock { Text = Icons.Map(glyph), FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };
        t.Classes.Add("glyph");
        return t;
    }

    private static T At<T>(T e, int column) where T : Control
    {
        Grid.SetColumn(e, column);
        return e;
    }
}
