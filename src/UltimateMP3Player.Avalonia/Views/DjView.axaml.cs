using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DjView : UserControl
{
    public DjView()
    {
        InitializeComponent();
        // « / » nudge while the button is held.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (Ui.FindAncestor<Button>(e.Source as Visual) is { Classes: var c } b && c.Contains("nudge") && DeckOf(b) is { } deck && b.Tag is string dir
                && e.GetCurrentPoint(b).Properties.IsLeftButtonPressed)
            {
                _nudging = b;
                deck.StartNudge(int.Parse(dir));
            }
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, _) => StopNudge(), RoutingStrategies.Tunnel, true);
        AddHandler(PointerCaptureLostEvent, (_, _) => StopNudge(), RoutingStrategies.Bubble, true);
        // Double click puts a knob or fader back to its default (its Tag).
        AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (Ui.FindAncestor<Slider>(e.Source as Visual) is not { Classes: var c } s || !c.Contains("reset")) return;
            s.Value = s.Tag is string t && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
            e.Handled = true;
        }, RoutingStrategies.Bubble, true);
        // Wheel on the tempo fader: 0.1 % a notch (the knob goes the way the wheel turns: up = slower, as the fader is drawn).
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (Ui.FindAncestor<Slider>(e.Source as Visual) is not { Classes: var c } s || !c.Contains("tempo") || DeckOf(s) is not { } deck || e.Delta.Y == 0) return;
            deck.NudgeTempo(e.Delta.Y > 0 ? -1 : 1);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Enter confirms the typed BPM.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.Source is not TextBox { Classes: var c } || !c.Contains("bpm")) return;
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Tap tab in a short window: the waveform strip gives its room to the pads.
        TapArea.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Height > 0) TapWave.IsVisible = e.NewSize.Height >= 500;
        };
        Both.PointerPressed += Both_Down;
        Both.PointerMoved += Both_Move;
        Both.PointerReleased += (_, e) =>
        {
            _scrubbing = false;
            e.Pointer.Capture(null);
        };
    }

    private Button? _nudging;

    private void StopNudge()
    {
        if (_nudging == null) return;
        DeckOf(_nudging)?.StopNudge();
        _nudging = null;
    }

    private static DjDeckViewModel? DeckOf(object? sender) => (sender as Control)?.DataContext as DjDeckViewModel;

    // Load: the song browser (library, playlists, tags, the song playing, the other deck, a file).
    private void Load_Click(object? sender, RoutedEventArgs e)
    {
        if (DeckOf(sender) is { } deck) deck.Dj.Pick(deck);
    }

    // TAP reacts on press, not on release: the tap lands on the beat you hear (the pad still lights up while held).
    private void Tap_Click(object? sender, RoutedEventArgs e) => DeckOf(sender)?.Tap();

    // ------------------------------------------------------------------ shared playhead

    private double _bothX;
    private bool _scrubbing;

    private void Both_Down(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not DjViewModel dj || !e.GetCurrentPoint(Both).Properties.IsLeftButtonPressed) return;
        _bothX = e.GetPosition(this).X;
        dj.BeginScrubBoth();
        _scrubbing = true;
        e.Pointer.Capture(Both);
        e.Handled = true;
    }

    private void Both_Move(object? sender, PointerEventArgs e)
    {
        if (!_scrubbing || DataContext is not DjViewModel dj) return;
        // The waveforms show DjWave.Window seconds across their width; dragging right goes back, like pulling the record.
        double width = (Both.Parent as Control)?.Bounds.Width ?? Bounds.Width;
        dj.ScrubBoth(-(e.GetPosition(this).X - _bothX) * DjWave.Window / Math.Max(1, width));
    }
}
