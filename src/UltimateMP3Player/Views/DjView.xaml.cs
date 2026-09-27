using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using UltimateMP3Player.Core;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class DjView : UserControl
{
    public DjView() => InitializeComponent();

    private static DjDeckViewModel? DeckOf(object sender) => (sender as FrameworkElement)?.DataContext as DjDeckViewModel;

    // Load: the song playing, from the library or from a file.
    private void Load_Click(object sender, RoutedEventArgs e)
    {
        if (DeckOf(sender) is not { } deck) return;
        var dj = deck.Dj;
        var menu = new ContextMenu();
        MenuItem Item(string text, string glyph, Action click, bool enabled = true)
        {
            var m = new MenuItem { Header = text, IsEnabled = enabled };
            Ui.SetGlyph(m, glyph);
            m.Click += (_, _) => click();
            return m;
        }
        var current = dj.Main.Player.Current;
        menu.Items.Add(Item(current != null ? L.F("In riproduzione: {0}", current.Title) : L.T("Nessun brano in riproduzione"), "",
            () => dj.LoadCurrent(deck), current != null));
        menu.Items.Add(Item(L.T("Dalla libreria…"), "", () => dj.PickFromLibrary(deck)));
        menu.Items.Add(Item(L.T("Da file…"), "", () => dj.PickFile(deck)));
        // The tap tab can take a deck's song.
        if (deck.IsTapper)
            foreach (var d in dj.Decks.Where(d => d.HasTrack && d.FilePath != null))
                menu.Items.Add(Item(L.F("Dal deck {0}: {1}", d.Name, d.Title), "", () => dj.LoadFrom(d, deck)));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // « / » nudge while the button is held.
    private void Nudge_Down(object sender, MouseButtonEventArgs e)
    {
        if (DeckOf(sender) is { } deck && (sender as FrameworkElement)?.Tag is string dir) deck.StartNudge(int.Parse(dir));
    }

    private void Nudge_Up(object sender, MouseEventArgs e) => DeckOf(sender)?.StopNudge();

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

    // Double click puts a knob or fader back to its default (its Tag).
    private void Reset_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider s) return;
        var value = s.Tag is string t && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        s.SetCurrentValue(RangeBase.ValueProperty, value);
        e.Handled = true;
    }
}
