using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltimateMP3Player.ViewModels;

namespace UltimateMP3Player.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    // Wheel over a band changes it; when the EQ is off the page scrolls.
    private void Eq_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider { IsEnabled: true } s || s.DataContext is not EqBandViewModel band) return;
        band.Gain += e.Delta > 0 ? 0.5 : -0.5;
        e.Handled = true;
    }

    private void Eq_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider { IsEnabled: true } s && s.DataContext is EqBandViewModel band) band.Gain = 0;
    }
}
