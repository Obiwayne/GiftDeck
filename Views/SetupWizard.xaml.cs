using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The screen GiftDeck opens on. It walks through what GiftDeck needs, in order, and only lets the user
// into the app once all of it is in place and OBS and TikFinity are connected:
//   Streamlabs (its TikTok login) -> OBS Studio -> Aitum Stream Suite -> portrait OBS -> TikFinity -> username -> ready check.
// Someone who's already set up only sees the ready check for a few seconds.
public partial class SetupWizard : UserControl
{
    enum Step { Streamlabs, Obs, Aitum, Portrait, TikFinity, Username, Kick, Ready }

    static readonly (Step step, string title)[] Steps =
    {
        (Step.Streamlabs, "Streamlabs login"),
        (Step.Obs, "OBS Studio"),
        (Step.Aitum, "Vertical canvas (Aitum)"),
        (Step.Portrait, "Portrait OBS"),
        (Step.TikFinity, "TikFinity"),
        (Step.Username, "TikTok username"),
        (Step.Kick, "Kick channel"),
        (Step.Ready, "Ready check"),
    };

    public event Action Finished;

    readonly HashSet<Step> _skipped = new HashSet<Step>();
    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    Step? _shown;
    bool _busy;                 // a download, install or setup is running
    string _busyText, _message; // progress text; last error or note
    double? _busyPart;
    bool _messageIsError;
    bool _streamlabsLinkedNow;  // got the login this session: say so and offer to close Streamlabs
    string _streamlabsNote;     // e.g. no LIVE access yet
    DateTime _readySince, _allOkSince;
    bool _done;

    public SetupWizard()
    {
        InitializeComponent();
        StatusSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Visibility = Visibility.Visible;
        SetupCard.Visibility = Visibility.Visible;
        // First run (or first run since the welcome pages were added): the welcome pages come before the steps.
        // The steps only start once it's closed, so the ready check can't finish behind it.
        if (!Hub.Settings.WelcomeSeen)
        {
            Welcome.Show(replay: false, upgraded: LooksUpgraded(), done: () =>
            {
                Hub.Settings.WelcomeSeen = true;
                Hub.SaveSettings();
                Log.Write("Welcome pages seen");
                StartSteps();
            });
            return;
        }
        StartSteps();
    }

    void StartSteps()
    {
        _timer.Start();
        Tick();
    }

    // Settings' "Show the welcome pages again": just the welcome, then back to wherever the user was.
    public void ShowWelcome()
    {
        if (Welcome.IsShowing) return;
        bool setupRunning = Visibility == Visibility.Visible && !_done;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        if (!setupRunning) SetupCard.Visibility = Visibility.Hidden; // the finished setup isn't shown behind it
        Welcome.Show(replay: true, upgraded: false, done: () =>
        {
            if (setupRunning) return;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { Visibility = Visibility.Collapsed; BeginAnimation(OpacityProperty, null); Opacity = 1; };
            BeginAnimation(OpacityProperty, fade);
        });
    }

    // Someone upgrading from GiftDeck already has settings or events: the welcome mentions the new name.
    static bool LooksUpgraded()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Hub.Settings.BridgeUsername)
                || !string.IsNullOrWhiteSpace(Hub.Settings.KickChannel)
                || Hub.Settings.TikFinityConfirmed
                || Hub.Rules?.Rules.Count > 0;
        }
        catch { return false; }
    }

    bool IsDone(Step s) => _skipped.Contains(s) || s switch
    {
        Step.Streamlabs => SetupSteps.HasStreamlabsToken && !_streamlabsLinkedNow,
        Step.Obs => SetupSteps.ObsInstalled,
        Step.Aitum => SetupSteps.AitumInstalled || SetupSteps.PortraitReady, // a finished portrait setup no longer needs the plugin
        Step.Portrait => SetupSteps.PortraitReady,
        Step.TikFinity => SetupSteps.TikFinityReady,
        Step.Username => SetupSteps.HasUsername,
        Step.Kick => !string.IsNullOrWhiteSpace(Hub.Settings.KickChannel),
        _ => false,
    };

    // The Kick step only shows for someone who has switched Kick on (in Stream Setup).
    static (Step step, string title)[] VisibleSteps => Steps.Where(x => x.step != Step.Kick || Hub.Settings.KickEnabled).ToArray();

    Step Current => VisibleSteps.Select(x => x.step).First(s => s == Step.Ready || !IsDone(s));

    void Tick()
    {
        if (_done) return;
        var step = Current;
        if (step == Step.Streamlabs && !_busy) PollStreamlabs();
        if (_shown != step)
        {
            _shown = step;
            _message = null;
            OnEnter(step);
        }
        Render(step);
    }

    void OnEnter(Step step)
    {
        if (step == Step.Ready) { _readySince = DateTime.Now; _allOkSince = default; }
        // TikFinity installed: read the LIVE through it from now on (GiftDeck starts it hidden).
        if (step == Step.TikFinity && TikFinityInstaller.Installed && Hub.Settings.LiveReader != "tikfinity")
            TikFinityService.UseReader("tikfinity");
        if (step == Step.Kick)
        {
            KickBox.Text = Hub.Settings.KickChannel;
            Dispatcher.BeginInvoke(() => KickBox.Focus(), DispatcherPriority.Input);
        }
        if (step == Step.Username)
        {
            var guess = (Hub.TikTok.State.AccountUsername ?? "").Trim().TrimStart('@');
            UsernameBox.Text = guess;
            Dispatcher.BeginInvoke(() => { UsernameBox.Focus(); UsernameBox.SelectAll(); }, DispatcherPriority.Input);
        }
    }

    // ---- Rendering ----

    void Render(Step step)
    {
        RenderStepList(step);
        var steps = VisibleSteps;
        int index = Array.FindIndex(steps, x => x.step == step);
        StepNumber.Text = step == Step.Ready ? "LAST STEP" : $"STEP {index + 1} OF {steps.Length}";
        UsernamePanel.Visibility = step == Step.Username ? Visibility.Visible : Visibility.Collapsed;
        KickPanel.Visibility = step == Step.Kick ? Visibility.Visible : Visibility.Collapsed;
        ReadyRows.Visibility = step == Step.Ready ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = step is Step.Ready or Step.Username or Step.Kick || _busy ? Visibility.Collapsed : Visibility.Visible;
        _pending.Clear();

        string status = null;
        bool spinning = false;
        switch (step)
        {
            case Step.Streamlabs: status = RenderStreamlabs(ref spinning); break;
            case Step.Obs:
                StepTitle.Text = "Install OBS Studio";
                StepBody.Text = "OBS Studio is what sends your video to TikTok. MayhemDeck runs it for you, hidden in the background, so you don't have to manage it.\n\nMayhemDeck downloads the official installer from OBS's own GitHub page. Follow its window when it opens (Windows may ask for permission).";
                if (!_busy)
                {
                    AddButton("Download and install OBS Studio (about 150 MB)", true, () => RunBusy((p, c) => SetupSteps.InstallObsAsync(p, c)));
                    AddButton("obsproject.com", false, () => OpenUrl("https://obsproject.com/download"));
                    AddButton("Check again", false, () => { _message = null; Tick(); });
                }
                break;
            case Step.Aitum:
                StepTitle.Text = "Add the vertical canvas";
                StepBody.Text = "Aitum Stream Suite (free) adds a vertical, portrait canvas to OBS. That's where you build your TikTok layout: camera, game, overlays.\n\nClose OBS first if it's open, then MayhemDeck downloads the installer from Aitum's GitHub page. Follow its window when it opens.";
                if (!_busy)
                {
                    if (ObsHost.IsRunning) AddButton("Close OBS", false, () => { ObsHost.FindRunning()?.CloseMainWindow(); });
                    AddButton("Download and install Aitum Stream Suite", true, () => RunBusy((p, c) => SetupSteps.InstallAitumAsync(p, c)), enabled: !ObsHost.IsRunning);
                    AddButton("aitum.tv", false, () => OpenUrl("https://aitum.tv/"));
                    AddButton("Check again", false, () => { _message = null; Tick(); });
                }
                break;
            case Step.Portrait:
                StepTitle.Text = "Set up portrait OBS";
                StepBody.Text = "MayhemDeck makes a portrait (1080x1920) copy of your vertical scenes and runs OBS on it, hidden, whenever MayhemDeck is open. What OBS sends is then exactly your TikTok layout. Your own OBS scenes aren't changed, and they come back when MayhemDeck closes.\n\nNew to OBS? Open OBS, build your TikTok layout in the Vertical canvas (at least one scene), close OBS, then set up portrait OBS.";
                if (!_busy)
                {
                    AddButton("Set up portrait OBS", true, SetUpPortrait);
                    if (!ObsHost.IsRunning) AddButton("Open OBS", false, OpenObs);
                }
                break;
            case Step.TikFinity: status = RenderTikFinity(); break;
            case Step.Username:
                StepTitle.Text = "Your TikTok username";
                StepBody.Text = "Which TikTok account do you go LIVE on? MayhemDeck reads that LIVE's chat, gifts and viewers. It's the part after @ in your profile link.";
                AddButton("Save", true, SaveUsername);
                break;
            case Step.Kick:
                StepTitle.Text = "Your Kick channel";
                StepBody.Text = "You've switched Kick on, so MayhemDeck also reads your Kick chat, follows, subs and Kicks gifts. Which channel? It's the part after kick.com/ in your channel link.";
                AddButton("Save", true, SaveKick);
                AddButton("Don't use Kick", false, () => { Hub.Settings.KickEnabled = false; Hub.SaveSettings(); Hub.Kick.Restart(); Tick(); });
                break;
            case Step.Ready: status = RenderReady(ref spinning); break;
        }

        if (_busy && _busyCancel != null) AddButton("Cancel", false, CancelBusy);
        ApplyButtons();
        if (_busy) { status = _busyText; spinning = true; }
        else if (_message != null) status = _message;
        StepStatus.Text = status ?? "";
        StepStatus.Foreground = Brush(_message != null && _messageIsError && !_busy ? "DangerBrush" : "TextBrush");
        StatusSpinner.Visibility = spinning ? Visibility.Visible : Visibility.Collapsed;
        StepProgress.Visibility = _busy && _busyPart is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_busyPart != null) StepProgress.Value = _busyPart.Value;
    }

    string RenderStreamlabs(ref bool spinning)
    {
        if (_streamlabsLinkedNow)
        {
            StepTitle.Text = "Got your Streamlabs login ✓";
            var who = string.IsNullOrWhiteSpace(Hub.TikTok.State.AccountUsername) ? "" : " (@" + Hub.TikTok.State.AccountUsername + ")";
            StepBody.Text = "MayhemDeck has your TikTok login" + who + " and can now start your LIVE and get its stream key.\n\nYou can close Streamlabs: MayhemDeck doesn't need it open." + (_streamlabsNote != null ? "\n\n" + _streamlabsNote : "");
            if (SetupSteps.StreamlabsRunning) AddButton("Close Streamlabs and continue", true, () => { SetupSteps.CloseStreamlabs(); _streamlabsLinkedNow = false; Tick(); });
            AddButton(SetupSteps.StreamlabsRunning ? "Leave it open and continue" : "Continue", !SetupSteps.StreamlabsRunning, () => { _streamlabsLinkedNow = false; Tick(); });
            return null;
        }
        if (!SetupSteps.StreamlabsInstalled)
        {
            StepTitle.Text = "Install Streamlabs";
            StepBody.Text = "MayhemDeck starts your TikTok LIVE through your Streamlabs login, which is how it gets a stream key. You log in once in Streamlabs Desktop (free) and MayhemDeck takes it from there; you won't need Streamlabs open after that.\n\nMayhemDeck downloads the installer from Streamlabs' own site. Follow its window when it opens.";
            if (!_busy)
            {
                AddButton("Download and install Streamlabs (about 275 MB)", true, () => RunBusy((p, c) => SetupSteps.InstallStreamlabsAsync(p, c)));
                AddButton("streamlabs.com", false, () => OpenUrl("https://streamlabs.com/streamlabs-live-streaming-software"));
                AddButton("Check again", false, () => { _message = null; Tick(); });
            }
            return null;
        }
        StepTitle.Text = "Log in to Streamlabs with TikTok";
        StepBody.Text = "1.  Open Streamlabs.\n2.  Log in with TikTok. Use the QR code: scan it with the TikTok app on your phone. It's the quickest way, and it works even if you normally sign in with Google.\n3.  That's it. MayhemDeck spots your login by itself and moves on.";
        if (!SetupSteps.StreamlabsRunning) AddButton("Open Streamlabs", true, SetupSteps.OpenStreamlabs);
        spinning = true;
        return SetupSteps.StreamlabsRunning ? "Waiting for you to log in to Streamlabs…" : "Waiting for Streamlabs…";
    }

    DateTime _lastTokenCheck;
    bool _checkingToken;

    // While on the Streamlabs step: pick up the login as soon as Streamlabs has saved it.
    // Reading Streamlabs' files happens off the UI thread, so the screen never stutters.
    async void PollStreamlabs()
    {
        if (_checkingToken || SetupSteps.HasStreamlabsToken || !SetupSteps.StreamlabsInstalled) return;
        if ((DateTime.Now - _lastTokenCheck).TotalSeconds < 2) return;
        _checkingToken = true;
        string token;
        try { token = await Task.Run(SetupSteps.ReadStreamlabsToken); }
        catch (Exception e) { Log.Write("Couldn't read the Streamlabs login: " + e.Message); return; }
        finally { _checkingToken = false; _lastTokenCheck = DateTime.Now; }
        if (string.IsNullOrEmpty(token) || _done || SetupSteps.HasStreamlabsToken) return;
        Hub.TikTok.State.Token = token;
        Hub.TikTok.Save();
        _streamlabsLinkedNow = true;
        Log.Write("Picked up the Streamlabs login from Streamlabs Desktop");
        _ = LearnAccountAsync();
        Tick();
    }

    async Task LearnAccountAsync()
    {
        try
        {
            var a = await Hub.TikTok.InfoAsync();
            if (!string.IsNullOrWhiteSpace(a.Username))
            {
                Hub.TikTok.State.AccountUsername = a.Username.Trim().TrimStart('@');
                Hub.TikTok.Save();
            }
            _streamlabsNote = a.CanBeLive ? null : "Heads up: this TikTok account can't go LIVE from a computer through Streamlabs yet. TikTok decides that (it depends on your account). MayhemDeck still works for everything else.";
        }
        catch (Exception e) { Log.Write("Streamlabs account check failed: " + e.Message); }
        Tick();
    }

    string RenderTikFinity()
    {
        if (!TikFinityInstaller.Installed)
        {
            StepTitle.Text = "Install TikFinity";
            StepBody.Text = "TikTok only sends the chat and gifts of 18+ LIVEs to viewers who are logged in. TikFinity (free) is logged in as you, so MayhemDeck reads your LIVE through it. MayhemDeck starts it for you, hidden in the background.\n\nMayhemDeck downloads it from TikFinity's own site and installs it.";
            if (!_busy)
            {
                AddButton("Install TikFinity (about 95 MB)", true, () => RunBusy((p, cancel) => TikFinityInstaller.InstallAsync(p, cancel), after: () => TikFinityService.UseReader("tikfinity")));
                AddButton("Check again", false, () => { _message = null; Tick(); });
            }
            return null;
        }
        StepTitle.Text = "Log in to TikFinity";
        StepBody.Text = "1.  Show TikFinity and log in with your TikTok account. Use the QR code: scan it with the TikTok app on your phone. It's quicker, and Google sign-in is often blocked there.\n2.  In TikFinity, go to Events and switch every event off. MayhemDeck runs your events; if TikFinity's stay on, every gift fires twice.\n3.  Press Done. MayhemDeck hides TikFinity again and keeps it running in the background.";
        AddButton("Show TikFinity", false, ShowTikFinity);
        AddButton("Done", true, () =>
        {
            Hub.Settings.TikFinityConfirmed = true;
            Hub.SaveSettings();
            Hub.TikFinity.HideWindow();
            Tick();
        });
        return TikFinityService.IsProcessRunning() ? null : "Starting TikFinity…";
    }

    async void ShowTikFinity()
    {
        if (!TikFinityService.IsProcessRunning()) Hub.TikFinity.Launch();
        for (int i = 0; i < 40 && !Hub.TikFinity.ShowWindow(); i++) await Task.Delay(500); // its window appears a few seconds after it starts
    }

    void SaveUsername()
    {
        var name = UsernameBox.Text.Trim().TrimStart('@');
        if (name.Length == 0) { Say("Type your TikTok username first.", true); return; }
        Hub.Settings.BridgeUsername = name;
        Hub.SaveSettings();
        Hub.Bridge.Restart();
        Tick();
    }

    void SaveKick()
    {
        var name = KickApi.CleanName(KickBox.Text);
        if (name.Length == 0) { Say("Type your Kick channel name first.", true); return; }
        Hub.Settings.KickChannel = name;
        Hub.SaveSettings();
        Hub.Kick.Restart();
        Tick();
    }

    void KickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveKick(); }
    }

    void UsernameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveUsername(); }
    }

    // The last step, every time GiftDeck opens: wait for OBS and TikFinity (or the bridge) to be connected.
    string RenderReady(ref bool spinning)
    {
        var obs = StartupStatus.Obs();
        var reader = StartupStatus.Reader();
        ReadyRows.Children.Clear();
        if (obs.kind != StatusKind.Off) ReadyRows.Children.Add(StatusLine(obs.kind, obs.text));
        ReadyRows.Children.Add(StatusLine(reader.kind, reader.text));
        var kick = StartupStatus.Kick();
        if (kick.kind != StatusKind.Off) ReadyRows.Children.Add(StatusLine(kick.kind, kick.text));

        bool ok = (obs.kind is StatusKind.Ok or StatusKind.Off) && reader.kind == StatusKind.Ok && (kick.kind is StatusKind.Ok or StatusKind.Off);
        bool error = obs.kind == StatusKind.Error || reader.kind == StatusKind.Error || kick.kind == StatusKind.Error;
        if (ok)
        {
            StepTitle.Text = "You're ready to go LIVE ✓";
            StepBody.Text = "Everything's installed and connected.";
            if (_allOkSince == default) _allOkSince = DateTime.Now;
            if ((DateTime.Now - _allOkSince).TotalSeconds >= 1.5) Finish();
            AddButton("Open MayhemDeck", true, Finish);
            return null;
        }
        _allOkSince = default;
        StepTitle.Text = error ? "Something's not right" : "Getting everything ready";
        StepBody.Text = error
            ? "MayhemDeck couldn't get everything connected. The line in red says what's wrong. You can try again, or open MayhemDeck anyway and fix it from Stream Setup."
            : "Starting OBS and TikFinity in the background and connecting to them. This usually takes a few seconds.";
        if (error)
        {
            AddButton("Try again", true, Retry);
            AddButton("Open MayhemDeck anyway", false, Finish);
        }
        else if ((DateTime.Now - _readySince).TotalSeconds > 20)
            AddButton("Open MayhemDeck anyway", false, Finish);
        return null;
    }

    void Retry()
    {
        if (!Hub.Obs.Connected) Hub.Engine.OnAppStart();
        if (Hub.Settings.LiveReader == "tikfinity" && !TikFinityService.IsProcessRunning()) Hub.TikFinity.Launch();
        Hub.TikFinity.Reconnect();
        if (Hub.Settings.KickEnabled && !Hub.Kick.Connected) Hub.Kick.Restart();
        _readySince = DateTime.Now;
        Tick();
    }

    UIElement StatusLine(StatusKind kind, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        row.Children.Add(new Ellipse
        {
            Width = 10, Height = 10, Margin = new Thickness(2, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
            Fill = Brush(kind switch { StatusKind.Ok => "SuccessBrush", StatusKind.Error => "DangerBrush", StatusKind.Loading => "WarnBrush", _ => "MutedBrush" }),
        });
        row.Children.Add(new TextBlock
        {
            Text = kind == StatusKind.Ok ? text + " ✓" : text, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 540,
            Foreground = Brush(kind == StatusKind.Error ? "DangerBrush" : "TextBrush"),
        });
        return row;
    }

    void RenderStepList(Step current)
    {
        StepList.Children.Clear();
        int i = 1;
        foreach (var (step, title) in VisibleSteps)
        {
            bool skipped = _skipped.Contains(step);
            bool done = step != current && IsDone(step) && !skipped;
            bool isCurrent = step == current;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var badge = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0),
                Background = Brush(done ? "SuccessBrush" : isCurrent ? "AccentBrush" : "LineBrush"),
                Opacity = skipped ? 0.7 : 1,
                Child = new TextBlock
                {
                    Text = done ? "✓" : skipped ? "–" : i.ToString(), FontSize = 12, FontWeight = FontWeights.Bold,
                    Foreground = done || isCurrent ? Brushes.White : Brush("MutedBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            row.Children.Add(badge);
            var label = new TextBlock
            {
                Text = title + (_skipped.Contains(step) ? " (skipped)" : ""), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(isCurrent ? "TextBrush" : "MutedBrush"),
            };
            row.Children.Add(label);
            StepList.Children.Add(row);
            i++;
        }
        Subtitle.Text = current == Step.Ready ? "Getting ready" : "Setting up";
    }

    // ---- Helpers ----

    // The screen re-renders every second; buttons are only rebuilt when they change, so a click never lands
    // on a button that's just been replaced. Each button runs whatever its slot's action is now.
    readonly List<(string text, bool primary, Action click, bool enabled)> _pending = new();
    readonly List<Action> _actions = new();
    string _buttonsKey;

    void AddButton(string text, bool primary, Action click, bool enabled = true) => _pending.Add((text, primary, click, enabled));

    void ApplyButtons()
    {
        var key = string.Join("|", _pending.Select(b => b.text + (b.primary ? "*" : "") + (b.enabled ? "" : "-")));
        _actions.Clear();
        _actions.AddRange(_pending.Select(b => b.click));
        if (key == _buttonsKey) return;
        _buttonsKey = key;
        Buttons.Children.Clear();
        for (int i = 0; i < _pending.Count; i++)
        {
            int slot = i;
            var spec = _pending[i];
            var b = new Button { Content = spec.text, Style = (Style)FindResource(spec.primary ? "Primary" : "Ghost"), Margin = new Thickness(0, 0, 8, 0), IsEnabled = spec.enabled };
            b.Click += (_, _) => { if (slot < _actions.Count) _actions[slot](); };
            Buttons.Children.Add(b);
        }
    }

    CancellationTokenSource _busyCancel; // set while a cancellable download or install runs
    string _cancelMessage;

    // Runs a download, install or setup with its progress on screen. A cancellable one gets a Cancel button;
    // downloads also give up by themselves if no data arrives for 30 seconds.
    async void RunBusy(Func<IProgress<(double, string)>, CancellationToken, Task> work, Action after = null, bool cancellable = true)
    {
        if (_busy) return;
        _busy = true;
        _busyCancel = cancellable ? new CancellationTokenSource() : null;
        var cancel = _busyCancel?.Token ?? CancellationToken.None;
        _cancelMessage = null;
        _busyText = "Starting…";
        _busyPart = null;
        _message = null;
        Tick();
        try
        {
            var progress = new Progress<(double part, string text)>(p =>
            {
                if (cancel.IsCancellationRequested) return;
                _busyPart = p.part; _busyText = p.text; Render(Current);
            });
            // WaitAsync: Cancel returns at once, even from work that can't stop part-way.
            await work(progress, cancel).WaitAsync(cancel);
            after?.Invoke();
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Log.Write("Setup: cancelled");
            EndBusy();
            Say(_cancelMessage ?? "Cancelled.", false);
            return;
        }
        catch (Exception e)
        {
            Log.Write("Setup: " + e.Message);
            EndBusy();
            // Network errors come with technical wording; the ones GiftDeck raises are already plain.
            Say(e is HttpRequestException ? "Couldn't download it. Check your internet connection and try again." : e.Message, true);
            return;
        }
        EndBusy();
        Tick();
    }

    void EndBusy()
    {
        _busy = false;
        _busyCancel = null; // not disposed: work that couldn't stop may still hold its token
    }

    void CancelBusy()
    {
        if (!_busy || _busyCancel == null || _busyCancel.IsCancellationRequested) return;
        _cancelMessage = _busyText != null && _busyText.StartsWith("Installing")
            ? "Stopped waiting for the installer. If its window is still open, you can finish it there and then press Check again."
            : "Download cancelled. Nothing was installed.";
        _busyText = "Stopping…";
        _busyCancel.Cancel();
        Render(Current);
    }

    // Asks first, like Stream Setup does: this may close the user's own OBS.
    void SetUpPortrait()
    {
        var convert = ObsHost.PortraitConverter;
        if (convert == null) { Say("Setting up the portrait canvas isn't available in this build.", true); return; }
        if (!StreamSetupView.ConfirmPortraitSetup()) return;
        RunBusy(async (_, _) =>
        {
            bool wasRunning = ObsHost.IsRunning;
            try { await Hub.Engine.SetUpPortraitAsync(convert); }
            catch (Exception ex)
            {
                Log.Write("Set up portrait OBS failed: " + ex.Message);
                throw new Exception(StreamSetupView.PortraitFailureMessage(ex, wasRunning));
            }
        }, cancellable: false); // stopping half-way could leave OBS closed with a half-written setup
    }

    void Say(string text, bool error)
    {
        _message = text;
        _messageIsError = error;
        Render(Current);
    }

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_shown is Step s && s != Step.Ready)
        {
            _skipped.Add(s);
            _streamlabsLinkedNow = false;
            Log.Write("Setup: skipped " + s);
        }
        Tick();
    }

    void OpenObs()
    {
        var exe = ObsConfig.FindExe();
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) });
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void Finish()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fade.Completed += (_, _) => { Visibility = Visibility.Collapsed; Opacity = 1; };
        BeginAnimation(OpacityProperty, fade);
        Finished?.Invoke();
    }

    Brush Brush(string key) => (Brush)FindResource(key);
}
