using System.Windows;
using System.Windows.Input;
using UltimateMP3Player.Core;

namespace UltimateMP3Player.ViewModels;

// Settings › Integrations: this profile's Last.fm and ListenBrainz accounts (Scrobbler sends the plays). Last.fm is
// connected on its own page (the user's own API key first, like Spotify's); ListenBrainz with the user's token.
public sealed class ScrobblingViewModel : Observable
{
    private readonly MainViewModel _main;
    private readonly HashSet<string> _told = new();

    public ScrobblingViewModel(MainViewModel main)
    {
        _main = main;
        LastFmCommand = new RelayCommand(() => _ = LastFmOn ? Disconnect(Scrobbler.LastFmName) : ConnectLastFmAsync());
        ListenBrainzCommand = new RelayCommand(() => _ = ListenBrainzOn ? Disconnect(Scrobbler.ListenBrainzName) : ConnectListenBrainzAsync());
        ImportLastFmCommand = new RelayCommand(() => _ = LastFmImport.RunAsync(main));
        main.Scrobbler.Changed += () => Application.Current.Dispatcher.BeginInvoke(Refresh);
        main.Scrobbler.Refused += service => Application.Current.Dispatcher.BeginInvoke(() => OnRefused(service));
    }

    private ProfileData P => _main.Profile.Data;
    private AppSettings S => _main.Host.Settings;

    public ICommand LastFmCommand { get; }
    public ICommand ListenBrainzCommand { get; }
    public ICommand ImportLastFmCommand { get; }

    public bool LastFmOn => _main.Scrobbler.LastFmOn;
    public string LastFmText => LastFmOn ? L.F("Connected as {0}", P.LastFmUser) : L.T("Not connected");
    public string LastFmButton => LastFmOn ? L.T("Disconnect") : L.T("Connect");
    public bool ListenBrainzOn => _main.Scrobbler.ListenBrainzOn;
    public string ListenBrainzText => ListenBrainzOn ? L.F("Connected as {0}", P.ListenBrainzUser) : L.T("Not connected");
    public string ListenBrainzButton => ListenBrainzOn ? L.T("Disconnect") : L.T("Connect");
    public string WaitingText => L.Count(_main.Scrobbler.Waiting, "1 play waiting to be sent", "{0} plays waiting to be sent");
    public bool HasWaiting => _main.Scrobbler.Waiting > 0;

    public void Refresh() => OnChanged(nameof(LastFmOn), nameof(LastFmText), nameof(LastFmButton), nameof(ListenBrainzOn), nameof(ListenBrainzText),
        nameof(ListenBrainzButton), nameof(WaitingText), nameof(HasWaiting));

    private async Task ConnectLastFmAsync()
    {
        if (string.IsNullOrWhiteSpace(S.LastFmKey) || string.IsNullOrWhiteSpace(S.LastFmSecret))
        {
            // The guide: Last.fm's own page names (API account, Shared secret) stay as Last.fm writes them.
            var r = await Dialogs.FormAsync(L.T("Last.fm key"),
                L.T("Last.fm only answers apps with a key: make yours once, it's free.\n\n1. Open last.fm/api (the button below) and sign in with your Last.fm account.\n2. Create an API account: any application name and description, the other fields can stay empty.\n3. Copy the API key and the Shared secret it shows and paste them here. The key stays on this device."),
                (L.T("Open last.fm/api"), LastFm.KeysPage), (L.T("API key"), S.LastFmKey ?? ""), (L.T("Shared secret"), S.LastFmSecret ?? ""));
            if (r == null || string.IsNullOrWhiteSpace(r[0]) || string.IsNullOrWhiteSpace(r[1])) return;
            S.LastFmKey = r[0].Trim();
            S.LastFmSecret = r[1].Trim();
            S.Save();
        }
        string key = S.LastFmKey!, secret = S.LastFmSecret!, token;
        try { token = await Task.Run(() => LastFm.TokenAsync(key, secret, CancellationToken.None)); }
        catch (ScrobbleRefused)
        {
            // (a wrong key: asked again next time)
            S.LastFmKey = S.LastFmSecret = null;
            S.Save();
            _main.Toast(L.T("Last.fm refused the key: check the API key and the Shared secret"));
            return;
        }
        catch
        {
            _main.Toast(L.T("Last.fm didn't answer: check your connection"));
            return;
        }
        MainViewModel.OpenUrl(LastFm.AuthPage(key, token));
        if (!await Dialogs.ConfirmAsync("Last.fm", L.T("Allow the app on the Last.fm page that just opened (sign in if it asks), then come back here and press Done."), L.T("Done")))
            return;
        try
        {
            var (name, session) = await Task.Run(() => LastFm.SessionAsync(key, secret, token, CancellationToken.None));
            P.LastFmUser = name;
            P.LastFmSession = session;
            P.LastFmSince ??= DateTime.Now;
            _main.Profile.Save();
            _told.Remove(Scrobbler.LastFmName);
            _main.Toast(L.F("Last.fm: connected as {0}, your plays go there from now on", name));
        }
        catch (ScrobbleRefused) { _main.Toast(L.T("The app wasn't allowed on Last.fm: try again")); }
        catch { _main.Toast(L.T("Last.fm didn't answer: check your connection")); }
        Refresh();
    }

    private async Task ConnectListenBrainzAsync()
    {
        var r = await Dialogs.FormAsync("ListenBrainz",
            L.T("Copy your user token from ListenBrainz (Settings › User token: the button below) and paste it here. It stays on this device."),
            (L.T("Open ListenBrainz"), ListenBrainz.TokenPage), (L.T("User token"), ""));
        if (r == null || r[0].Trim() is not { Length: > 0 } token) return;
        try
        {
            var user = await Task.Run(() => ListenBrainz.UserAsync(token, CancellationToken.None));
            if (user == null)
            {
                _main.Toast(L.T("ListenBrainz didn't accept the token: copy it again"));
                return;
            }
            P.ListenBrainzUser = user;
            P.ListenBrainzToken = token;
            _main.Profile.Save();
            _told.Remove(Scrobbler.ListenBrainzName);
            _main.Toast(L.F("ListenBrainz: connected as {0}, your plays go there from now on", user));
        }
        catch (ScrobbleRefused) { _main.Toast(L.T("ListenBrainz didn't accept the token: copy it again")); }
        catch { _main.Toast(L.T("ListenBrainz didn't answer: check your connection")); }
        Refresh();
    }

    private async Task Disconnect(string service)
    {
        var name = service == Scrobbler.LastFmName ? "Last.fm" : "ListenBrainz";
        if (!await Dialogs.ConfirmAsync(name, L.F("Disconnect {0}? This profile's plays stop going there.", name), L.T("Disconnect"), danger: true)) return;
        if (service == Scrobbler.LastFmName) P.LastFmUser = P.LastFmSession = null;
        else P.ListenBrainzUser = P.ListenBrainzToken = null;
        _main.Profile.Save();
        _main.Scrobbler.Forget(service);
        Refresh();
    }

    // Once per session: the account isn't accepted any more (its plays wait until it's connected again).
    private void OnRefused(string service)
    {
        Refresh();
        if (!_told.Add(service)) return;
        _main.Toast(L.F("{0} refused your account: connect it again in Settings › Integrations",
            service == Scrobbler.LastFmName ? "Last.fm" : "ListenBrainz"));
    }
}
