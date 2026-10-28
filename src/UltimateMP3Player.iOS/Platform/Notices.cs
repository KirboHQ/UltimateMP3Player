using Foundation;
using UserNotifications;

namespace UltimateMP3Player.Platform;

// The system's notifications: a followed artist's new release (Settings turns them off). iOS asks the user the first time.
public static class Notices
{
    private static bool _asked;

    public static void Show(string title, string text)
    {
        var center = UNUserNotificationCenter.Current;
        // (with the app open iOS shows nothing unless asked to)
        center.Delegate ??= new Foreground();
        void Post()
        {
            var content = new UNMutableNotificationContent { Title = title, Body = text, Sound = UNNotificationSound.Default };
            var request = UNNotificationRequest.FromIdentifier(Guid.NewGuid().ToString("N"), content, null);
            center.AddNotificationRequest(request, _ => { });
        }
        if (_asked)
        {
            Post();
            return;
        }
        center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, _) =>
        {
            _asked = true;
            if (granted) Post();
        });
    }

    private sealed class Foreground : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
            => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);
    }
}
