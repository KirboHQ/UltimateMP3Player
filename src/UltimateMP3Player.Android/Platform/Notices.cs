using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using UltimateMP3Player.Core;
using Application = Android.App.Application;

namespace UltimateMP3Player.Platform;

// The system's notifications that aren't the player's or the downloads': a followed artist's new release (Settings turns
// them off). A tap opens the app.
public static class Notices
{
    private const string Channel = "releases";
    private static int _next = 300;

    public static void Show(string title, string text)
    {
        var ctx = Application.Context;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu && ctx.CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted) return;
        if (ctx.GetSystemService(Context.NotificationService) is not NotificationManager m) return;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && m.GetNotificationChannel(Channel) == null)
            m.CreateNotificationChannel(new NotificationChannel(Channel, L.T("New releases"), NotificationImportance.Default)
                { Description = L.T("New albums and songs of the artists you follow") });
        using var b = Build.VERSION.SdkInt >= BuildVersionCodes.O ? new Notification.Builder(ctx, Channel) : new Notification.Builder(ctx);
        b.SetSmallIcon(Resource.Drawable.ic_notification)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetContentIntent(PlaybackService.OpenAppIntent(ctx, false))
            .SetAutoCancel(true)
            .SetColor(unchecked((int)0xFF7C5CFF));
        m.Notify(_next++, b.Build());
    }
}
