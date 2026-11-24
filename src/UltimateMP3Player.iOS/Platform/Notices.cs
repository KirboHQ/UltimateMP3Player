using Foundation;
using UserNotifications;

namespace UltimateMP3Player.Platform;

// The system's notifications: a followed artist's new release (Settings turns them off), what the app's renewal needs. iOS asks the
// user the first time.
public static class Notices
{
    private static bool _asked;

    public static void Show(string title, string text) => Post(Guid.NewGuid().ToString("N"), title, text, null);

    // Later, whether the app is there or not; the same id again takes its place.
    public static void Schedule(string id, string title, string text, DateTime at)
        => Post(id, title, text, UNTimeIntervalNotificationTrigger.CreateTrigger(Math.Max(1, (at - DateTime.UtcNow).TotalSeconds), false));

    public static void Cancel(params string[] ids)
    {
        if (ids.Length == 0) return;
        UNUserNotificationCenter.Current.RemovePendingNotificationRequests(ids);
        UNUserNotificationCenter.Current.RemoveDeliveredNotifications(ids);
    }

    private static void Post(string id, string title, string text, UNNotificationTrigger? trigger)
    {
        var center = UNUserNotificationCenter.Current;
        // (with the app open iOS shows nothing unless asked to)
        center.Delegate ??= new Foreground();
        void Add()
        {
            var content = new UNMutableNotificationContent { Title = title, Body = text, Sound = UNNotificationSound.Default };
            center.AddNotificationRequest(UNNotificationRequest.FromIdentifier(id, content, trigger), _ => { });
        }
        if (_asked)
        {
            Add();
            return;
        }
        center.RequestAuthorization(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound, (granted, _) =>
        {
            _asked = true;
            if (granted) Add();
        });
    }

    private sealed class Foreground : UNUserNotificationCenterDelegate
    {
        public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler)
            => completionHandler(UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.List | UNNotificationPresentationOptions.Sound);
    }
}
