using System.Windows.Controls;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // Settings › Layout: the rows in the order chosen, the hidden ones out.
        Loaded += (_, _) =>
        {
            Layout.Changed += ArrangeRows;
            ArrangeRows();
        };
        Unloaded += (_, _) => Layout.Changed -= ArrangeRows;
    }

    private void ArrangeRows() => Rearrange.Apply(HomeRows, Layout.Home);
}
