using Android.App;
using Android.Content;
using Android.OS;
using UltimateMP3Player.Services;

namespace UltimateMP3Player.Platform;

// "Ok Google, play … on Ultimate MP3 Player" on the phone: the Assistant opens this (MEDIA_PLAY_FROM_SEARCH), which has no screen of
// its own and closes at once; the music starts like the car's voice (MediaControls.PlayFromSearch), the app left where it was. Its
// start makes the app the one in front for that moment, so the playback service may come to the front too.
[Activity(Name = "com.kirbohq.ultimatemp3player.VoiceActivity", Exported = true, NoHistory = true, ExcludeFromRecents = true, TaskAffinity = "",
    Theme = "@android:style/Theme.Translucent.NoTitleBar")]
[IntentFilter(new[] { "android.media.action.MEDIA_PLAY_FROM_SEARCH" }, Categories = new[] { Intent.CategoryDefault })]
public sealed class VoiceActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try { MediaControls.Shared.PlayFromSearch(Intent?.GetStringExtra(SearchManager.Query), Intent?.Extras); }
        catch (Exception ex) { App.Log(ex); }
        Finish();
    }
}
