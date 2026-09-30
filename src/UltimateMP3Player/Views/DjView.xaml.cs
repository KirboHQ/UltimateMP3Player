using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DjView : UserControl
{
    public DjView() => InitializeComponent();

    private static DjDeckViewModel? DeckOf(object sender) => (sender as FrameworkElement)?.DataContext as DjDeckViewModel;

    // Load: the song browser (library, playlists, tags, the song playing, the other deck, a file).
    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (DeckOf(sender) is { } deck) deck.Dj.Pick(deck);
    }

    // « / » nudge while the button is held.
    private void Nudge_Down(object sender, MouseButtonEventArgs e)
    {
        if (DeckOf(sender) is { } deck && (sender as FrameworkElement)?.Tag is string dir) deck.StartNudge(int.Parse(dir));
    }

    private void Nudge_Up(object sender, MouseEventArgs e) => DeckOf(sender)?.StopNudge();

    // Tap tab in a short window: the waveform strip gives its room to the pads.
    private void TapArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height > 0) TapWave.Visibility = e.NewSize.Height < 500 ? Visibility.Collapsed : Visibility.Visible;
    }

    // TAP reacts on press, not on release: the tap lands on the beat you hear (the pad still lights up while held).
    private void Tap_Down(object sender, MouseButtonEventArgs e) => DeckOf(sender)?.Tap();

    // Enter confirms the typed BPM.
    private void Bpm_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ shared playhead

    private double _bothX;

    private void Both_Down(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not DjViewModel dj || sender is not FrameworkElement fe) return;
        _bothX = e.GetPosition(this).X;
        dj.BeginScrubBoth();
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void Both_Move(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { IsMouseCaptured: true } fe || DataContext is not DjViewModel dj) return;
        // The waveforms show DjWave.Window seconds across their width; dragging right goes back, like pulling the record.
        double width = (fe.Parent as FrameworkElement)?.ActualWidth ?? ActualWidth;
        dj.ScrubBoth(-(e.GetPosition(this).X - _bothX) * DjWave.Window / Math.Max(1, width));
    }

    private void Both_Up(object sender, MouseButtonEventArgs e) => (sender as FrameworkElement)?.ReleaseMouseCapture();

    // Wheel on the tempo fader: 0.1 % a notch (the knob goes the way the wheel turns: up = slower, as the fader is drawn).
    private void Tempo_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (DeckOf(sender) is not { } deck) return;
        deck.NudgeTempo(e.Delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    // Double click puts a knob or fader back to its default (its Tag).
    private void Reset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s) return;
        var value = s.Tag is string t && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        s.SetCurrentValue(RangeBase.ValueProperty, value);
        e.Handled = true;
    }
}
