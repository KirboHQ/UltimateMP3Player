using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using UltimateMP3Player.Core;

namespace UltimateMP3Player;

// Small dark dialogs: confirmations and text fields.
public static class Dialogs
{
    public static bool Confirm(string title, string message, string ok, bool danger = false)
        => Show(title, message, Array.Empty<(string, string)>(), ok, danger) != null;

    public static string? Prompt(string title, string label, string initial)
        => Show(title, null, new[] { (label, initial) }, L.T("Salva"), false)?[0];

    public static (string Title, string Artist, string Album)? EditTrack(Track t)
    {
        var r = Show(L.T("Modifica informazioni"), null,
            new[] { (L.T("Titolo"), t.Title), (L.T("Artista"), t.Artist ?? ""), (L.T("Album"), t.Album ?? "") }, L.T("Salva"), false);
        return r == null ? null : (r[0], r[1], r[2]);
    }

    private static string[]? Show(string title, string? message, (string Label, string Value)[] fields, string ok, bool danger)
    {
        var res = Application.Current.Resources;
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? App.Host?.Window;
        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsLoaded: true } ? owner : null,
            FontFamily = (FontFamily)res["UiFont"],
            FontSize = 14,
            Foreground = (Brush)res["TextBrush"],
            Title = title,
            UseLayoutRounding = true,
        };

        var stack = new StackPanel { Width = 400 };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) });
        if (message != null)
            stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)res["SubTextBrush"], LineHeight = 20 });

        var boxes = new List<TextBox>();
        foreach (var (label, value) in fields)
        {
            stack.Children.Add(new TextBlock { Text = label, Style = (Style)res["FieldLabel"], Margin = new Thickness(2, 10, 0, 6) });
            var box = new TextBox { Text = value, Style = (Style)res["BoxTextBox"] };
            boxes.Add(box);
            stack.Children.Add(box);
        }

        string[]? result = null;
        var okButton = new Button { Content = ok, Style = (Style)res[danger ? "DangerButton" : "PrimaryButton"], IsDefault = true, MinWidth = 96, Margin = new Thickness(10, 0, 0, 0) };
        var cancel = new Button { Content = L.T("Annulla"), Style = (Style)res["GhostButton"], IsCancel = true, MinWidth = 96 };
        okButton.Click += (_, _) =>
        {
            result = boxes.Select(b => b.Text).ToArray();
            win.DialogResult = true;
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(okButton);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Background = (Brush)res["SurfaceBrush"],
            BorderBrush = (Brush)res["BorderStrongBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(18),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black },
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { win.DragMove(); } catch { } };
        win.Content = card;
        win.Loaded += (_, _) =>
        {
            if (boxes.Count > 0)
            {
                boxes[0].Focus();
                boxes[0].SelectAll();
            }
            else okButton.Focus();
        };
        return win.ShowDialog() == true ? result : null;
    }
}
