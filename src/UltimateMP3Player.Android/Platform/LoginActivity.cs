using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Webkit;
using Android.Widget;
using UltimateMP3Player.Core;
using AColor = Android.Graphics.Color;
using Orientation = Android.Widget.Orientation;

namespace UltimateMP3Player.Platform;

// A site's own page inside the app, to sign in to it (SiteLogins): a bar on top (close, the page's title and whether the
// login is done, "Fatto"), a thin line while the page loads, the page. Its cookies stay in the app's web pages; once it's
// closed SiteLogins writes them where the downloads find them. An Android screen of its own (not drawn by Avalonia): the
// page is the system's web view.
[Android.App.Activity(Name = "com.kirbohq.ultimatemp3player.LoginActivity", Exported = false, Theme = "@style/UmpTheme",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode | ConfigChanges.Density,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public sealed class LoginActivity : Android.App.Activity
{
    public const string ExtraUrl = "ump.login_url", ExtraSite = "ump.login_site", ExtraAccent = "ump.login_accent";

    private WebView? _web;
    private TextView? _title, _status, _done;
    private ProgressBar? _progress;
    private LoginSite? _site;
    private AColor _accent = AColor.ParseColor("#7C5CFF");
    private bool _signedIn;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var url = Intent?.GetStringExtra(ExtraUrl) ?? "https://www.youtube.com/";
        var siteName = Intent?.GetStringExtra(ExtraSite);
        _site = SiteLogins.Sites.FirstOrDefault(s => s.Name == siteName);
        if (Intent?.HasExtra(ExtraAccent) == true) _accent = new AColor(Intent.GetIntExtra(ExtraAccent, unchecked((int)0xFF7C5CFF)));
        float density = Resources?.DisplayMetrics?.Density ?? 2;
        int Dp(double v) => (int)Math.Round(v * density);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(AColor.ParseColor("#0E1014"));

        // ---- the bar
        var bar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetBackgroundColor(AColor.ParseColor("#161920"));
        bar.SetPadding(Dp(4), 0, Dp(10), 0);

        var close = new ImageButton(this) { ContentDescription = L.T("Chiudi") };
        close.SetImageResource(Resource.Drawable.ic_close);
        close.SetColorFilter(AColor.ParseColor("#C9CDD6"));
        close.Background = Ripple(AColor.Transparent, Dp(24));
        close.Click += (_, _) => Finish();
        bar.AddView(close, new LinearLayout.LayoutParams(Dp(48), Dp(48)));

        var texts = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _title = new TextView(this) { Text = siteName != null ? L.F("Accedi a {0}", siteName) : L.T("Accedi al sito") };
        _title.SetTextColor(AColor.White);
        _title.SetTextSize(Android.Util.ComplexUnitType.Sp, 16);
        _title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        _title.SetSingleLine(true);
        _title.Ellipsize = TextUtils.TruncateAt.End;
        _status = new TextView(this) { Text = L.T("Accedi come fai di solito, poi tocca Fatto") };
        _status.SetTextColor(AColor.ParseColor("#9AA0AC"));
        _status.SetTextSize(Android.Util.ComplexUnitType.Sp, 12.5f);
        _status.SetSingleLine(true);
        _status.Ellipsize = TextUtils.TruncateAt.End;
        texts.AddView(_title);
        texts.AddView(_status);
        bar.AddView(texts, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f) { LeftMargin = Dp(6), RightMargin = Dp(8) });

        _done = new TextView(this) { Text = L.T("Fatto"), Gravity = GravityFlags.Center };
        _done.SetTextColor(AColor.White);
        _done.SetTextSize(Android.Util.ComplexUnitType.Sp, 14.5f);
        _done.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        _done.SetPadding(Dp(18), 0, Dp(18), 0);
        _done.Background = Ripple(AColor.ParseColor("#2A2E38"), Dp(18));
        _done.Clickable = true;
        _done.Click += (_, _) => Finish();
        bar.AddView(_done, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(36)));
        root.AddView(bar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(56)));

        // ---- loading line and page
        _progress = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 100, Indeterminate = false };
        _progress.ProgressTintList = ColorStateList.ValueOf(_accent);
        _progress.ProgressBackgroundTintList = ColorStateList.ValueOf(AColor.Transparent);
        root.AddView(_progress, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(3)) { TopMargin = -Dp(1) });

        _web = new WebView(this);
        _web.SetBackgroundColor(AColor.ParseColor("#0E1014"));
        root.AddView(_web, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        SetContentView(root);

        // From the edges of the screen (Android 15 always draws apps there): the bar under the status bar, the page above
        // the gesture line and the keyboard.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            Window?.SetDecorFitsSystemWindows(false);
            root.SetOnApplyWindowInsetsListener(new EdgeInsets());
        }
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, new Back(this));

        var s = _web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        s.LoadWithOverviewMode = true;
        s.UseWideViewPort = true;
        s.AllowFileAccess = false;
        s.AllowContentAccess = false;
        s.MixedContentMode = MixedContentHandling.NeverAllow;
        s.SetSupportZoom(true);
        s.BuiltInZoomControls = true;
        s.DisplayZoomControls = false;
        // Google won't let anyone sign in from a page that says it's an app's web view ("; wv"): it's the phone's
        // Chrome engine anyway.
        s.UserAgentString = CleanAgent(s.UserAgentString);
        var cookies = CookieManager.Instance;
        cookies?.SetAcceptCookie(true);
        cookies?.SetAcceptThirdPartyCookies(_web, true);
        _web.SetWebViewClient(new Client(this));
        _web.SetWebChromeClient(new Chrome(this));
        _web.LoadUrl(url);
    }

    private static string CleanAgent(string? agent)
    {
        if (string.IsNullOrEmpty(agent)) return "";
        agent = agent.Replace("; wv)", ")", StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(agent, @"\s*Version/\d+(\.\d+)*", "");
    }

    private static Drawable Ripple(AColor fill, int radius)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetCornerRadius(radius);
        var mask = new GradientDrawable();
        mask.SetColor(AColor.White);
        mask.SetCornerRadius(radius);
        return new RippleDrawable(ColorStateList.ValueOf(AColor.Argb(48, 255, 255, 255)), shape, mask);
    }

    // The site's login cookies are there: the bar says so and "Fatto" lights up.
    private void CheckSignedIn()
    {
        if (_site == null || _done == null || _status == null) return;
        bool now = SiteLogins.SignedInNow(_site);
        if (now == _signedIn) return;
        _signedIn = now;
        _status.Text = now ? L.T("Accesso fatto: tocca Fatto per tornare all'app") : L.T("Accedi come fai di solito, poi tocca Fatto");
        _status.SetTextColor(now ? AColor.ParseColor("#4ADE80") : AColor.ParseColor("#9AA0AC"));
        float density = Resources?.DisplayMetrics?.Density ?? 2;
        _done.Background = Ripple(now ? _accent : AColor.ParseColor("#2A2E38"), (int)(18 * density));
    }

    private void GoBack()
    {
        if (_web?.CanGoBack() == true) _web.GoBack();
        else Finish();
    }

    public override void OnBackPressed() => GoBack();

    protected override void OnDestroy()
    {
        try
        {
            _web?.StopLoading();
            (_web?.Parent as ViewGroup)?.RemoveView(_web);
            _web?.Destroy();
        }
        catch { }
        _web = null;
        base.OnDestroy();
        SiteLogins.Closed();
    }

    private sealed class Client : WebViewClient
    {
        private readonly LoginActivity _a;
        public Client(LoginActivity a) => _a = a;

        // Web pages stay here; links to apps (intent:, market:...) go nowhere.
        public override bool ShouldOverrideUrlLoading(WebView? view, IWebResourceRequest? request)
        {
            var scheme = request?.Url?.Scheme;
            return scheme is not ("http" or "https");
        }

        public override void OnPageStarted(WebView? view, string? url, Bitmap? favicon)
        {
            base.OnPageStarted(view, url, favicon);
            if (url != null && Android.Net.Uri.Parse(url)?.Host is { } host) SiteLogins.Visit(host);
        }

        public override void OnPageFinished(WebView? view, string? url)
        {
            base.OnPageFinished(view, url);
            _a.CheckSignedIn();
        }
    }

    private sealed class Chrome : WebChromeClient
    {
        private readonly LoginActivity _a;
        public Chrome(LoginActivity a) => _a = a;

        public override void OnProgressChanged(WebView? view, int newProgress)
        {
            if (_a._progress is not { } p) return;
            p.Progress = newProgress;
            p.Visibility = newProgress >= 100 ? ViewStates.Invisible : ViewStates.Visible;
        }

        public override void OnReceivedTitle(WebView? view, string? title)
        {
            if (_a._site == null && !string.IsNullOrWhiteSpace(title) && _a._title != null) _a._title.Text = title;
        }
    }

    private sealed class EdgeInsets : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var i = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout());
            v.SetPadding(i.Left, i.Top, i.Right, i.Bottom);
            return WindowInsets.Consumed!;
        }
    }

    private sealed class Back : Java.Lang.Object, Android.Window.IOnBackInvokedCallback
    {
        private readonly LoginActivity _a;
        public Back(LoginActivity a) => _a = a;
        public void OnBackInvoked() => _a.GoBack();
    }
}
