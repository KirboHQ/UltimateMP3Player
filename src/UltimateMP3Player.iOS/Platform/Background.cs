using UIKit;

namespace UltimateMP3Player.Platform;

// iOS stops an app soon after it goes into the background, unless music plays (UIBackgroundModes audio). While downloads
// are in progress the app asks for the extra time iOS gives to finish a job (about half a minute): a song that's almost
// done gets saved; the others go on when the app comes back (or right away, while the music plays).
public static class Background
{
    private static nint _task = UIApplication.BackgroundTaskInvalid;

    // (from the screen, on the main thread)
    public static void Downloading(bool active)
    {
        try
        {
            var app = UIApplication.SharedApplication;
            if (active && _task == UIApplication.BackgroundTaskInvalid)
                _task = app.BeginBackgroundTask("downloads", End);
            else if (!active) End();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private static void End()
    {
        if (_task == UIApplication.BackgroundTaskInvalid) return;
        var task = _task;
        _task = UIApplication.BackgroundTaskInvalid;
        UIApplication.SharedApplication.EndBackgroundTask(task);
    }
}
