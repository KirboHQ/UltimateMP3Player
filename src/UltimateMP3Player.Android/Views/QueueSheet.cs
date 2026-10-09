using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using UltimateMP3Player.Core;
using Avalonia.Controls.Primitives;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

// "Next up" in a sheet (the computer apps' queue card): the songs queued by hand, then the ones of the list; the handle
// on the right of a song drags it to another place, the × takes it away, a tap plays it. On top: generate it again or
// empty it.
public static class QueueSheet
{
    public static void Show(PlayerViewModel player)
    {
        var sheets = App.Host.View?.Sheets;
        if (sheets == null) return;
        var body = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(20, 0, 10, 4) };
        DockPanel.SetDock(head, Dock.Top);
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = L.T("Successivi"), FontSize = 20, FontWeight = FontWeight.Bold });
        var hint = new TextBlock { Classes = { "hint" }, FontSize = 13, Margin = new Thickness(0, 2, 0, 0) };
        title.Children.Add(hint);
        head.Children.Add(title);
        var generate = new Button { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(""), Command = player.GenerateQueueCommand };
        Grid.SetColumn(generate, 1);
        head.Children.Add(generate);
        var clear = new Button { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(""), Command = player.ClearQueueCommand };
        Grid.SetColumn(clear, 2);
        head.Children.Add(clear);
        body.Children.Add(head);

        var foot = new TextBlock { Classes = { "hint" }, FontSize = 13, Margin = new Thickness(20, 8, 20, 4), TextAlignment = TextAlignment.Center };
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(foot);

        var rows = new StackPanel();
        var scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        body.Children.Add(scroll);
        var template = Ui.Find<IDataTemplate>("QueueRowTemplate");

        // Choosing several songs of the queue ("Select" in a song's menu): to the top or bottom, out, saved.
        var picks = new Selection();
        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        root.Children.Add(body);
        root.Children.Add(new PickTop { Picks = picks });
        var actions = new PickActions { Picks = picks };
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Selection.SetOwner(root, picks);

        void Fill()
        {
            rows.Children.Clear();
            picks.SetItems(player.UpNext);
            hint.Text = player.GenerateHint;
            foreach (var r in player.UpNext)
            {
                if (r.Header != null)
                {
                    // A long list name ("Songs like «…»") scrolls instead of being cut at the edge.
                    var header = new TextBlock
                    {
                        Text = r.Header, Classes = { "small-caps" }, Margin = new Thickness(20, rows.Children.Count == 0 ? 6 : 16, 20, 6),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Marquee.SetIsEnabled(header, true);
                    Marquee.SetAuto(header, true);
                    rows.Children.Add(header);
                }
                var row = new ContentControl { Content = r, ContentTemplate = template };
                rows.Children.Add(row);
                DragToMove(row, r, player, rows, scroll);
            }
            if (player.UpNext.Count == 0)
                rows.Children.Add(new TextBlock { Text = L.T("Nessun brano dopo questo."), Classes = { "hint" }, FontSize = 14.5, Margin = new Thickness(20, 20, 20, 8), TextAlignment = TextAlignment.Center });
            foot.Text = string.Join("\n", new[] { player.MoreText, player.QueueEndHint }.Where(s => !string.IsNullOrEmpty(s)));
        }

        void OnPlayer(object? s, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(PlayerViewModel.UpNext) or nameof(PlayerViewModel.QueueEndHint)) Fill();
        }
        player.UpNextVisible = true;
        player.RefreshUpNext();
        Fill();
        player.PropertyChanged += OnPlayer;
        _ = Show(sheets, root, () => player.PropertyChanged -= OnPlayer);
    }

    private static async Task Show(SheetHost sheets, Control body, Action closed)
    {
        await sheets.Show(body, maxHeightShare: 0.85);
        closed();
    }

    // The handle drags the song: it follows the finger over the others, a line says where it lands.
    private static void DragToMove(ContentControl row, QueueRow r, PlayerViewModel player, StackPanel rows, ScrollViewer scroll)
    {
        row.TemplateApplied += (_, _) => { };
        double startY = 0;
        bool dragging = false;
        var shift = new TranslateTransform();
        row.RenderTransform = shift;
        Border? line = null;
        int target = r.Index;

        row.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual v || v.FindAncestorOfType<Border>(true) is not { } b || !IsHandle(v)) return;
            dragging = true;
            startY = e.GetPosition(rows).Y;
            e.Pointer.Capture(row);
            row.ZIndex = 10;
            row.Opacity = 0.92;
            Haptics.LongPress();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        row.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (!dragging) return;
            double y = e.GetPosition(rows).Y;
            shift.Y = y - startY;
            // The row under the finger: the song lands before or after it.
            var others = rows.Children.OfType<ContentControl>().Where(c => c != row && c.Content is QueueRow).ToList();
            ContentControl? over = null;
            bool after = false;
            foreach (var o in others)
            {
                var b = o.Bounds;
                if (y < b.Top || y > b.Bottom) continue;
                over = o;
                after = y > b.Top + b.Height / 2;
                break;
            }
            if (over?.Content is QueueRow q)
            {
                target = Math.Clamp(after ? (q.Index > r.Index ? q.Index : q.Index + 1) : (q.Index > r.Index ? q.Index - 1 : q.Index), 0, Math.Max(0, player.UpNext.Count - 1));
                line ??= new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = Ui.Res("AccentBrush"), IsHitTestVisible = false, Margin = new Thickness(16, -2, 16, -1) };
                rows.Children.Remove(line);
                int at = rows.Children.IndexOf(over) + (after ? 1 : 0);
                rows.Children.Insert(Math.Clamp(at, 0, rows.Children.Count), line);
            }
            // Near the edges the list scrolls by itself.
            var inView = e.GetPosition(scroll).Y;
            if (inView < 40) scroll.Offset = new Vector(0, Math.Max(0, scroll.Offset.Y - 12));
            else if (inView > scroll.Bounds.Height - 40) scroll.Offset = new Vector(0, scroll.Offset.Y + 12);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        void End(bool move)
        {
            if (!dragging) return;
            dragging = false;
            shift.Y = 0;
            row.ZIndex = 0;
            row.Opacity = 1;
            if (line != null) rows.Children.Remove(line);
            if (move && target != r.Index) player.MoveUpcoming(r.Index, target);
        }

        row.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!dragging) return;
            e.Pointer.Capture(null);
            End(true);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        row.PointerCaptureLost += (_, _) => End(false);
    }

    private static bool IsHandle(Visual v)
    {
        for (var x = v; x != null; x = x.GetVisualParent())
            if (x is Border { Classes: var c } && c.Contains("handle")) return true;
        return false;
    }
}

// The playback speed (the computer apps' panel, YouTube-like): 0.5-2×, the steps, the presets, and whether the key follows.
public static class SpeedSheet
{
    public static void Show(PlayerViewModel player)
    {
        var sheets = App.Host.View?.Sheets;
        if (sheets == null) return;
        var body = new StackPanel { Margin = new Thickness(22, 4, 22, 20), DataContext = player };
        body.Children.Add(new TextBlock { Text = L.T("Velocità di riproduzione"), FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Ui.Res("SubTextBrush") });
        body.Children.Add(new TextBlock
        {
            FontSize = 36, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 10),
            [!TextBlock.TextProperty] = new Avalonia.Data.Binding(nameof(PlayerViewModel.SpeedValueText)),
        });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(new Button { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(""), Command = player.SpeedStepCommand, CommandParameter = "-0.05" });
        var slider = new Slider
        {
            Theme = Ui.Theme("KnobSlider"), Minimum = 0.5, Maximum = 2, SmallChange = 0.05, LargeChange = 0.25, IsSnapToTickEnabled = true, TickFrequency = 0.05,
            Height = 44, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
            [!Slider.ValueProperty] = new Avalonia.Data.Binding(nameof(PlayerViewModel.Speed), Avalonia.Data.BindingMode.TwoWay),
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        var plus = new Button { Theme = Ui.Theme("TouchIcon"), Content = Icons.Map(""), Command = player.SpeedStepCommand, CommandParameter = "0.05" };
        Grid.SetColumn(plus, 2);
        row.Children.Add(plus);
        body.Children.Add(row);
        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(-3, 14, -3, 0) };
        foreach (var p in player.SpeedPresets)
        {
            var b = new ToggleButtonLike(p);
            presets.Children.Add(b);
        }
        body.Children.Add(presets);
        var pitch = new CheckBox
        {
            Margin = new Thickness(0, 18, 0, 0), VerticalContentAlignment = VerticalAlignment.Top,
            [!ToggleButtonBase.IsCheckedProperty] = new Avalonia.Data.Binding(nameof(PlayerViewModel.SpeedPitch), Avalonia.Data.BindingMode.TwoWay),
        };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = L.T("Cambia anche il tono"), FontSize = 15 });
        text.Children.Add(Ui.Text(L.T("Più veloce = più acuto, come un disco girato più in fretta."), "hint"));
        pitch.Content = text;
        body.Children.Add(pitch);
        body.Children.Add(Ui.Text(player.SpeedHint, "hint", new Thickness(0, 14, 0, 0)));
        _ = sheets.Show(new ScrollViewer { Content = body });
    }

    private static class ToggleButtonBase
    {
        public static readonly AvaloniaProperty IsCheckedProperty = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
    }

    // A preset: lit when it's the speed playing.
    private sealed class ToggleButtonLike : Button
    {
        protected override Type StyleKeyOverride => typeof(Button);

        public ToggleButtonLike(SpeedPreset p)
        {
            Theme = Ui.Theme("SmallGhost");
            Content = p.Label;
            Command = p.Command;
            Margin = new Thickness(3, 0);
            HorizontalAlignment = HorizontalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Center;
            DataContext = p;
            void Paint()
            {
                Background = p.IsCurrent ? Ui.Res("AccentSoftBrush") : Ui.Res("Surface2Brush");
                BorderBrush = p.IsCurrent ? Ui.Res("AccentBrush") : Ui.Res("BorderBrush");
                Foreground = p.IsCurrent ? Ui.Res("AccentTextBrush") : Ui.Res("TextBrush");
            }
            p.PropertyChanged += (_, _) => Paint();
            Paint();
        }
    }
}
