using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UltimateMP3Player.Core;
using UltimateMP3Player.Platform;

namespace UltimateMP3Player.Views;

// Signing in to Apple again without the computer, when Apple has ended what the app renews itself with: the password, then the code.
// Opened only by a press on the screen. Who it is can't be changed here (another Apple ID would make it another app to iOS), and the
// password is in its box no longer than it takes to send it: emptied as it goes, and whenever the app is left.
public sealed class RenewalSignIn
{
    private const string Lock = "", Eye = "";

    private readonly SheetHost _sheets;
    private readonly string _email;
    private readonly StackPanel _body = new() { Margin = new Thickness(24, 6, 24, 4) };
    // What scrolls, and what's always on the screen, over the keyboard too: going on, and the way out.
    private readonly DockPanel _sheet = new();
    private readonly TextBlock _title = new() { FontSize = 23, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 6) };
    private readonly TextBlock _text = new() { Classes = { "hint" }, FontSize = 15, LineHeight = 22, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _first = new(), _ways = new();
    // (no undo: it would bring the password back into the emptied box. Nothing is copied from a password's box either.)
    private readonly TextBox _password = new() { Theme = Ui.Theme("TouchBox"), PasswordChar = '•', IsUndoEnabled = false };
    private readonly TextBox _code = new() { Theme = Ui.Theme("TouchBox"), IsUndoEnabled = false, FontSize = 20, Watermark = L.T("Six digits"), Margin = new Thickness(0, 18, 0, 0) };
    private readonly Button _eye = new() { Theme = Ui.Theme("TouchIcon"), Width = 48, Height = 48, Content = Icons.Map(Eye), Focusable = false };
    private readonly TextBlock _why = new() { FontSize = 14, LineHeight = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly TextBlock _promise = new() { Classes = { "hint" }, FontSize = 13, LineHeight = 18, Margin = new Thickness(4, 14, 4, 0) };
    private readonly Button _go, _verify, _computer;

    private CancellationTokenSource? _stop;
    private TaskCompletionSource<string?>? _answer;
    // Asking for the code (else for the password), waiting for Apple, nothing but another way of getting the code will do, tried once already.
    private bool _asking, _working, _failed, _again;
    private bool _over, _signedIn, _toComputer;

    // "Sign in here", "Sign in again".
    public static async void Open()
    {
        if (Renewal.Busy || RenewalNotice.Open || App.Host.View?.Sheets is not { } sheets) return;
        try
        {
            if (await Renewal.AccountAsync() is { } email) await new RenewalSignIn(sheets, email).ShowAsync();
            else await RenewalNotice.ShowAsync(RenewalNotice.NoAccount());
        }
        catch (Exception ex) { App.Log(ex); }
    }

    private RenewalSignIn(SheetHost sheets, string email)
    {
        _sheets = sheets;
        _email = email;
        TextInputOptions.SetContentType(_password, TextInputContentType.Password);
        TextInputOptions.SetIsSensitive(_password, true);
        TextInputOptions.SetShowSuggestions(_password, false);
        TextInputOptions.SetAutoCapitalization(_password, false);
        TextInputOptions.SetReturnKeyType(_password, TextInputReturnKeyType.Go);
        TextInputOptions.SetContentType(_code, TextInputContentType.Digits);
        TextInputOptions.SetShowSuggestions(_code, false);
        AutomationProperties.SetName(_password, L.T("Password"));
        AutomationProperties.SetName(_code, L.T("Six digits"));
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_why, AutomationLiveSetting.Assertive);
        _why.Foreground = RenewalNotice.Strong("Warning");
        _promise.Text = L.T("The password goes only to Apple and isn't kept anywhere. An outside service (anisette) makes up a random identity for the request: it never sees the email or the password. It's up to you: the computer's sign-in does the same.");

        _go = Wide("", "TouchPrimary", 10, SignIn);
        _verify = Wide("", "TouchPrimary", 10, Verify);
        _computer = Wide(L.T("Use the computer's sign-in"), "SmallGhost", 14, () =>
        {
            _toComputer = true;
            Leave();
        });
        _eye.Click += (_, _) => _password.RevealPassword = !_password.RevealPassword;
        _password.PropertyChanged += (_, e) =>
        {
            // (hidden again by itself too, when the box is left)
            if (e.Property == TextBox.TextProperty || e.Property == TextBox.RevealPasswordProperty) Refresh();
        };
        _code.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) Refresh();
        };
        _password.KeyDown += (_, e) => OnEnter(e, SignIn);
        _code.KeyDown += (_, e) => OnEnter(e, Verify);

        var who = new TextBlock { Text = email, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var locked = RenewalNotice.Drawn(Lock, 17, Ui.Res("SubTextBrush"));
        locked.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(locked, 1);
        Grid.SetColumn(_eye, 2);
        _first.Children.AddRange(new Control[]
        {
            Ui.Text("Apple ID", "field-label", new Thickness(4, 14, 0, 8)),
            new Border
            {
                Height = 48, CornerRadius = new CornerRadius(24), Padding = new Thickness(18, 0), Background = Ui.Res("Surface2Brush"),
                Child = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { who, locked } },
            },
            new TextBlock
            {
                Text = L.T("The one the app was installed with: to change it, use the installer on the computer."), Classes = { "hint" }, FontSize = 13, LineHeight = 18, Margin = new Thickness(4, 8, 4, 0),
            },
            Ui.Text(L.T("Password"), "field-label", new Thickness(4, 14, 0, 8)),
            new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,Auto"), Children = { _password, _eye } },
        });
        var badge = RenewalNotice.Badge(Lock, "Accent", 52);
        badge.HorizontalAlignment = HorizontalAlignment.Center;
        _body.Children.AddRange(new Control[] { badge, _title, _text, _first, _code, _why, _promise, _ways, _computer });
        var always = new StackPanel { Margin = new Thickness(24, 0, 24, 18), Children = { _go, _verify, Wide(L.T("Cancel"), "TouchGhost", 10, Leave) } };
        DockPanel.SetDock(always, Dock.Bottom);
        var scroll = new ScrollViewer { Content = _body };
        // (the keyboard comes after the box is touched and takes half of the sheet)
        scroll.SizeChanged += (_, _) => Reach();
        _password.GotFocus += (_, _) => Reach();
        _code.GotFocus += (_, _) => Reach();
        _sheet.Children.AddRange(new Control[] { always, scroll });
        ShowPassword("", false);
    }

    private static Button Wide(string text, string theme, double top, Action press)
    {
        var b = RenewalNotice.Wide(text, theme, top);
        b.Click += (_, _) => press();
        return b;
    }

    // What's being written stays in sight, its label over it and why the last one wasn't good under it.
    private void Reach() => Dispatcher.UIThread.Post(() =>
    {
        var box = _asking ? _code : _password;
        if (box.IsFocused) box.BringIntoView(new Rect(box.Bounds.Size).Inflate(new Thickness(0, 34, 0, _why.IsVisible ? 14 + _why.Bounds.Height : 14)));
    }, DispatcherPriority.Background);

    private static void OnEnter(KeyEventArgs e, Action press)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        press();
    }

    // ------------------------------------------------------------------ from its opening to its end

    private async Task ShowAsync()
    {
        RenewalNotice.OpenOn = _sheets;
        Renewal.Leaving += Forget;
        // (the screen built again takes it away without closing it)
        _sheet.DetachedFromVisualTree += (_, _) => End();
        // (by its button only: a tap beside it, or a drag, would throw a sign-in half done away)
        await _sheets.Show(_sheet, dismissable: false);
        End();
        RenewalNotice.OpenOn = null;
        if (_toComputer) await RenewalNotice.ShowAsync(RenewalNotice.Computer());
        else if (_signedIn)
        {
            App.Host.Session?.Toast(L.T("Signed in to Apple"));
            RenewalNotice.RenewNow();
        }
    }

    private void Leave()
    {
        End();
        _sheets.Close(_sheet);
    }

    private void End()
    {
        if (_over) return;
        _over = true;
        Renewal.Leaving -= Forget;
        Forget();
        // (first: what's answered after isn't sent)
        _stop?.Cancel();
        Answer(null);
    }

    private void Forget()
    {
        _password.RevealPassword = false;
        _password.Text = "";
    }

    private async void SignIn()
    {
        if (_working || _over || string.IsNullOrEmpty(_password.Text)) return;
        _working = true;
        _why.Text = "";
        _stop = new CancellationTokenSource();
        try
        {
            // (as it was typed: a space at either end may be part of it)
            var signing = Renewal.SignInAsync(_email, _password.Text, AskAsync, _stop.Token);
            Forget();
            Refresh();
            var (problem, text) = await signing;
            if (_over) return;
            if (problem != SignInProblem.None) ShowPassword(RenewalRules.Why(problem, text), problem == SignInProblem.Internet);
            else
            {
                _signedIn = true;
                Leave();
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            if (!_over) ShowPassword(RenewalRules.Why(SignInProblem.Other, ""), true);
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
    }

    // ------------------------------------------------------------------ the code

    // Each thing the library asks takes one answer, and no more: a second one would be taken as the answer to what it asks next.
    private Task<string?> AskAsync(RenewalEvent e)
    {
        if (_over) return Task.FromResult<string?>(null);
        _answer = new TaskCompletionSource<string?>();
        _asking = true;
        _working = false;
        _failed = e.Failed;
        _code.Text = "";
        _title.Text = L.T(e.Failed ? "Another way for the code" : "Type the code");
        _text.Text = e.Failed ? L.T("The code couldn't be sent that way: choose another one.")
            : e.Sms ? L.T("Apple sent it in a text message. It may well arrive on this same iPhone: you can go and read it, this stays here.")
            : L.T("Apple shows it on the devices signed in with this Apple ID. This iPhone may be one of them: tap Allow, read the code and type it here.");
        _why.Text = e.Error.Length == 0 ? "" : RenewalRules.SignIn(e.Error) is var problem && problem != SignInProblem.Other ? RenewalRules.Why(problem, "") : RenewalRules.Shown(e.Error);
        _ways.Children.Clear();
        if (!e.Failed) _ways.Children.Add(Wide(L.T("Send it again"), "TouchGhost", 10, () => Answer("resend")));
        if (e.Sms || e.Failed) _ways.Children.Add(Wide(L.T("Show it on my devices"), "TouchGhost", 10, () => Answer("devices")));
        foreach (var n in e.Numbers ?? Array.Empty<RenewalNumber>())
            _ways.Children.Add(Wide(L.F("Text message to {0}", n.Number), "TouchGhost", 10, () => Answer("sms:" + n.Id)));
        Refresh();
        if (!e.Failed) Dispatcher.UIThread.Post(() => _code.Focus(), DispatcherPriority.Background);
        return _answer.Task;
    }

    private string Digits => new((_code.Text ?? "").Where(char.IsAsciiDigit).ToArray());

    private void Verify()
    {
        if (Digits.Length >= 6 && !_failed) Answer(Digits);
    }

    private void Answer(string? text)
    {
        if (_answer is not { } asked) return;
        _answer = null;
        _working = true;
        _why.Text = "";
        Refresh();
        asked.TrySetResult(text);
    }

    // ------------------------------------------------------------------ what's shown

    private void ShowPassword(string why, bool again)
    {
        _answer = null;
        _asking = _working = false;
        _again = again;
        _title.Text = L.T("Sign in to Apple");
        _text.Text = L.T("The app then renews itself again.");
        _why.Text = why;
        Refresh();
    }

    private void Refresh()
    {
        _first.IsVisible = _promise.IsVisible = _go.IsVisible = _computer.IsVisible = !_asking;
        _code.IsVisible = _verify.IsVisible = _asking && !_failed;
        _ways.IsVisible = _asking;
        _why.IsVisible = !string.IsNullOrEmpty(_why.Text);
        _password.IsEnabled = _eye.IsEnabled = _code.IsEnabled = _ways.IsEnabled = _computer.IsEnabled = !_working;
        _go.Content = L.T(_working ? "Signing in…" : _again ? "Try again" : "Sign in");
        _go.IsEnabled = !_working && !string.IsNullOrEmpty(_password.Text);
        _verify.Content = L.T(_working ? "One moment…" : "Verify");
        _verify.IsEnabled = !_working && Digits.Length >= 6;
        _eye.Foreground = _password.RevealPassword ? RenewalNotice.Strong("Accent") : Ui.Res("SubTextBrush");
        AutomationProperties.SetName(_eye, L.T(_password.RevealPassword ? "Hide the password" : "Show the password"));
    }
}
