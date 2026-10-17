using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Connect phone": the phone connected, and the phones nearby in "Connect to computer" (looked for while this is open).
public static class PhoneDialogs
{
    private static ResourceDictionary Res => Application.Current.Resources;
    private static Brush B(string key) => (Brush)Res[key];

    private const string PhoneGlyph = "\uE8EA";

    public static void Show(MainViewModel main)
    {
        var vm = main.PhoneLink;
        var body = new StackPanel();

        // ---- the one connected
        var name = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Style = (Style)Res["Hint"], TextWrapping = TextWrapping.Wrap };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var spinner = new Ellipse { Style = (Style)Res["Spinner"], Width = 12, Height = 12, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
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
        var current = new Grid();
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.ColumnDefinitions.Add(new ColumnDefinition());
        current.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        current.Children.Add(Glyph(PhoneGlyph, 22, B("AccentTextBrush")));
        current.Children.Add(At(info, 1));
        current.Children.Add(At(disconnect, 2));
        var currentCard = new Border
        {
            CornerRadius = new CornerRadius(12), Background = B("AccentSoftBrush"), Padding = new Thickness(16, 12, 12, 12), Margin = new Thickness(0, 0, 0, 16),
            Child = current,
        };
        body.Children.Add(currentCard);

        body.Children.Add(new TextBlock
        {
            Style = (Style)Res["Hint"], TextWrapping = TextWrapping.Wrap,
            Text = L.T("On the phone open Settings › Connect to computer: it shows up here. The phone controls the music of this computer (its songs play here, its sound stays off) and receives music with “To the phone” in the export window."),
        });
        body.Children.Add(new TextBlock
        {
            Style = (Style)Res["Hint"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Text = L.T("Same Wi-Fi as this computer, or a cable: no need to confirm on the phone then. Android: turn on USB tethering in the phone's settings. iPhone: it needs Apple Devices (or iTunes) on this computer."),
        });

        // ---- the phones nearby
        body.Children.Add(new TextBlock { Text = L.T("Phones nearby"), Style = (Style)Res["FieldLabel"], Margin = new Thickness(2, 18, 0, 8) });
        var list = new StackPanel();
        var looking = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 10, 10, 10) };
        looking.Children.Add(new Ellipse { Style = (Style)Res["Spinner"], Width = 14, Height = 14, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center });
        looking.Children.Add(new TextBlock { Text = L.T("Looking for phones in “Connect to computer”…"), Foreground = B("SubTextBrush"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var both = new StackPanel();
        both.Children.Add(list);
        both.Children.Add(looking);
        body.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10), Background = B("BgBrush"), BorderBrush = B("BorderBrush"), BorderThickness = new Thickness(1), Padding = new Thickness(4),
            Child = new ScrollViewer { Content = both, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false },
        });

        var close = Dialogs.Button(L.T("Close"), "GhostButton", isCancel: true);
        var win = Dialogs.Frame(L.T("Connect phone"), body, 520, close);

        void Refresh()
        {
            currentCard.Visibility = vm.HasPhone ? Visibility.Visible : Visibility.Collapsed;
            name.Text = vm.PhoneName;
            status.Text = vm.StatusText;
            bool busy = vm.IsConnecting || vm.IsReconnecting;
            spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            dot.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            dot.Fill = B("SuccessBrush");
            disconnect.Content = vm.IsConnecting ? L.T("Cancel") : L.T("Disconnect");
            list.Children.Clear();
            foreach (var p in vm.Phones) list.Children.Add(Row(p, vm));
            looking.Visibility = vm.Phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private static UIElement Row(PhoneRowViewModel p, PhoneLinkViewModel vm)
    {
        var grid = new Grid { Margin = new Thickness(8, 6, 6, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Glyph(PhoneGlyph, 20, B("SubTextBrush")));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        text.Children.Add(new TextBlock { Text = p.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = p.Detail, Style = (Style)Res["Hint"], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(At(text, 1));
        var connect = Dialogs.Button(L.T("Connect"), "PrimaryButton");
        connect.Command = p.ConnectCommand;
        connect.IsEnabled = !vm.IsConnecting;
        connect.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(At(connect, 2));
        return grid;
    }

    private static TextBlock Glyph(string glyph, double size, Brush brush)
        => new() { Text = glyph, Style = (Style)Res["Glyph"], FontSize = size, Foreground = brush, VerticalAlignment = VerticalAlignment.Center };

    private static T At<T>(T e, int column) where T : UIElement
    {
        Grid.SetColumn(e, column);
        return e;
    }
}
