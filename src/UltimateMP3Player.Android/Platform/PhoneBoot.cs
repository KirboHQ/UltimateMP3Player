using System.Reflection;
using Avalonia;
using Avalonia.Android;
using Avalonia.Controls.ApplicationLifetimes;

namespace UltimateMP3Player.Platform;

// The car can ask for music while the app is closed: a process with no Activity, none coming. Avalonia only starts from its
// Activity, and everything the app is (App, AppHost, the player) lives on Avalonia's dispatcher, so this starts it the way the
// Activity would. An Activity that comes later finds Avalonia's lifetime already there and only adds its window: same App, same
// Host, one player.
public static class PhoneBoot
{
    // Started for a service: App makes no screen, MainActivity does when it comes.
    public static bool WithoutScreen { get; private set; }

    // Avalonia sets AppCompat's night mode to the look it shows (for the system bars), and from then this window reports
    // that one instead of the device's: "same as the device" stayed on the first look until a restart. The system's own
    // settings still say what the device shows.
    public static void FollowDevice() => Themes.DeviceLook = () =>
        (Android.Content.Res.Resources.System!.Configuration!.UiMode & Android.Content.Res.UiMode.NightMask) == Android.Content.Res.UiMode.NightYes
            ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;

    // On the main thread. False when it can't start: the car says to open the app on the phone.
    public static bool Ensure()
    {
        if (App.Host != null) return true;
        // shortcut: Avalonia keeps this field and its class private, so an update of Avalonia can rename them. Then this returns
        // false and the app still opens normally from its icon; use its own way if it ever offers a start without an Activity.
        var field = typeof(AvaloniaMainActivity).GetField("Lifetime", BindingFlags.NonPublic | BindingFlags.Static);
        var type = typeof(AvaloniaMainActivity).Assembly.GetType("Avalonia.Android.SingleViewLifetime");
        try
        {
            if (field == null || type == null) throw new MissingMemberException("Avalonia.Android: no Lifetime to start without an Activity");
            var lifetime = (IApplicationLifetime)Activator.CreateInstance(type, nonPublic: true)!;
            field.SetValue(null, lifetime);
            WithoutScreen = true;
            FollowDevice();
            FallbackFonts.Add(AppBuilder.Configure<App>().UseAndroid()).SetupWithLifetime(lifetime);
            App.Host?.SetVisible(false);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            WithoutScreen = false;
            if (Avalonia.Application.Current == null) field?.SetValue(null, null);
        }
        return App.Host != null;
    }
}
