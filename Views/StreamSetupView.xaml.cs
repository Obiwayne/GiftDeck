using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// One-time setup: the checklist, the TikTok account, how the LIVE is read, OBS, and the Streamlabs token.
public partial class StreamSetupView : UserControl
{
    bool _loading = true;

    TikTokLiveService Tt => Hub.TikTok;

    public StreamSetupView()
    {
        InitializeComponent();
        var s = Tt.State;
        TokenBox.Text = s.Token;
        UsernameBox.Text = Hub.Settings.BridgeUsername;
        UpdateUsernameNote();
        // Learn which account the saved login belongs to, for the note above (read-only Streamlabs call).
        if (!string.IsNullOrWhiteSpace(s.Token) && string.IsNullOrWhiteSpace(s.AccountUsername))
            Loaded += async (_, _) => await CheckAccount();
        VerticalBox.IsChecked = s.SendVertical;
        RelayServerBox.Text = RelayService.LocalServer;
        RelayKeyBox.Text = RelayService.LocalKey;
        UpdateFfmpeg();
        foreach (ComboBoxItem item in ReaderBox.Items)
            if ((string)item.Tag == Hub.Settings.LiveReader) ReaderBox.SelectedItem = item;
        TikFinityHiddenBox.IsChecked = Hub.Settings.TikFinityHidden;
        Hub.PageReader.StatusChanged += () => Dispatcher.BeginInvoke(UpdateReader);
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateReader);
        Loaded += (_, _) => UpdateReader();
        ManagedBox.IsChecked = Hub.Settings.ObsManaged;
        UpdateEngine();
        Hub.Engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateEngine);
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateEngine);
        // OBS can be opened or closed outside GiftDeck; a light check while the page is on screen.
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        poll.Tick += (_, _) => { if (IsVisible) { UpdateEngine(); UpdateReader(); } };
        poll.Start();
        InitKick();
        _loading = false;
    }

    // ---- Reading the LIVE ----

    string Reader => (ReaderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? Hub.Settings.LiveReader;
    bool? _loggedIn;
    bool _checkingLogin;

    void Reader_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        TikFinityService.UseReader(Reader);
        _loggedIn = null;
        UpdateReader();
    }

    void UpdateReader()
    {
        var r = Reader;
        ReaderHelp.Text = r switch
        {
            "page" => "GiftDeck opens your LIVE in its own TikTok page, logged in as you, muted and out of sight, whenever you're live. Nothing else to install or run, and it works with 18+ LIVEs.",
            "tikfinity" => "GiftDeck listens to TikFinity's feed (needed for 18+ LIVEs). TikFinity has to be installed and logged in to your TikTok account, with its own Events switched off; GiftDeck starts it for you, hidden in the background.",
            _ => "The bridge connects to your LIVE by itself without logging in. TikTok doesn't send chat or gifts to logged-out viewers of 18+ LIVEs, so use TikFinity if your LIVE is 18+.",
        };
        TikTokLoginButton.Visibility = r == "page" ? Visibility.Visible : Visibility.Collapsed;
        TikFinityHiddenBox.Visibility = ShowTikFinityButton.Visibility = HideTikFinityButton.Visibility = r == "tikfinity" ? Visibility.Visible : Visibility.Collapsed;

        string status, detail = "", brush = "TextBrush";
        var feed = Hub.TikFinity;
        if (r == "tikfinity")
        {
            bool running = TikFinityService.IsProcessRunning();
            status = !running ? "TikFinity isn't running" : feed.Connected ? "Connected to TikFinity \u2713" : "TikFinity is running; connecting\u2026";
            brush = running && feed.Connected ? "SuccessBrush" : "WarnBrush";
            if (running) detail = feed.WindowHidden ? "It's running hidden. Show it to log in or change its settings." : "Its window is showing.";
            ShowTikFinityButton.IsEnabled = running && feed.WindowHidden;
            HideTikFinityButton.IsEnabled = running && !feed.WindowHidden;
        }
        else if (r == "page")
        {
            if (_loggedIn == null && !_checkingLogin) _ = CheckLoginAsync();
            status = _loggedIn == false ? "Not logged in to TikTok" : feed.TikTokLive == true ? "Reading your LIVE \u2713" : "Ready";
            brush = _loggedIn == false ? "WarnBrush" : feed.TikTokLive == true ? "SuccessBrush" : "TextBrush";
            detail = _loggedIn == false ? "Click TikTok login and log in with the account you stream from." : Hub.PageReader.Status;
        }
        else
        {
            status = feed.TikTokLive == true ? "Reading your LIVE \u2713" : feed.Connected ? "Bridge running" : "Starting the bridge\u2026";
            brush = feed.TikTokLive == true ? "SuccessBrush" : "TextBrush";
            if (Tt.State.Mature) { detail = "Your LIVE is set to 18+: the bridge won't see chat or gifts. Choose TikFinity instead (see the setup checklist above)."; brush = "WarnBrush"; }
        }
        ReaderStatus.Text = status;
        ReaderStatus.Foreground = (System.Windows.Media.Brush)FindResource(brush);
        ReaderDetail.Text = detail;
        ReaderDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        UpdateChecklist();
    }

    async Task CheckLoginAsync()
    {
        _checkingLogin = true;
        try { _loggedIn = await TikTokChatWindow.Instance.IsLoggedInAsync(); }
        catch { _loggedIn = null; }
        finally { _checkingLogin = false; }
        UpdateReader();
    }

    async void TikTokLogin_Click(object sender, RoutedEventArgs e)
    {
        await TikTokChatWindow.Instance.ShowToLogInAsync();
        _loggedIn = null; // looked at again on the next refresh
        Hub.PageReader.CheckSoon();
    }

    void TikFinityHidden_Changed(object sender, RoutedEventArgs e)
    {
        Hub.Settings.TikFinityHidden = TikFinityHiddenBox.IsChecked == true;
        Hub.SaveSettings();
    }

    void ShowTikFinity_Click(object sender, RoutedEventArgs e) { Hub.TikFinity.ShowWindow(); UpdateReader(); }
    void HideTikFinity_Click(object sender, RoutedEventArgs e) { Hub.TikFinity.HideWindow(); UpdateReader(); }

    bool _engineBusy;

    void UpdateEngine()
    {
        var (running, visible, ours) = Hub.Engine.State();
        bool managed = Hub.Settings.ObsManaged;
        string status, detail;
        if (!managed)
        {
            status = "GiftDeck isn't running OBS";
            detail = running ? "OBS is open; you run it yourself." : "OBS isn't open. Tick the box above to let GiftDeck run it.";
        }
        else if (!running)
        {
            status = "OBS isn't running";
            detail = ObsHost.PortraitExists() ? "GiftDeck starts it when it opens, and when you Go LIVE." : "Click Set up portrait OBS to make the portrait canvas first.";
        }
        else
        {
            status = visible ? "OBS is running (window showing)" : "OBS is running hidden ✓";
            detail = (ours ? "Started by GiftDeck. " : "Opened outside GiftDeck, so GiftDeck won't close it. ")
                     + (Hub.Obs.Connected ? "Connected." : "Connecting…")
                     + (visible ? " Minimize it to hide it again; closing it stops OBS." : "");
        }
        if (!string.IsNullOrEmpty(Hub.Engine.LastError) && managed && !running) detail = "Last try: " + Hub.Engine.LastError;
        EngineStatus.Text = status;
        EngineStatus.Foreground = (System.Windows.Media.Brush)FindResource(managed && running ? "SuccessBrush" : managed ? "WarnBrush" : "TextBrush");
        EngineDetail.Text = detail;
        ShowObsButton.IsEnabled = running && !visible && !_engineBusy;
        SetUpPortraitButton.IsEnabled = !_engineBusy;
        UpdateChecklist();
    }

    void EngineSay(string text, string brush)
    {
        EngineMessage.Text = text;
        EngineMessage.Foreground = (System.Windows.Media.Brush)FindResource(brush);
        EngineMessage.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void Managed_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Hub.Settings.ObsManaged = ManagedBox.IsChecked == true;
        Hub.SaveSettings();
        EngineSay(Hub.Settings.ObsManaged
            ? "GiftDeck will start OBS hidden next time it opens (or when you Go LIVE)."
            : "GiftDeck won't start or close OBS any more. Go LIVE uses the Vertical canvas settings below again.", "MutedBrush");
        UpdateEngine();
    }

    async void SetUpPortrait_Click(object sender, RoutedEventArgs e)
    {
        var convert = ObsHost.PortraitConverter;
        if (convert == null)
        {
            EngineSay("Setting up the portrait canvas isn't available in this build yet.", "WarnBrush");
            return;
        }
        var ask = ObsHost.IsRunning
            ? "GiftDeck will close OBS, make a \"GiftDeck Portrait\" scene collection and profile (1080x1920) from your current setup, and open OBS again hidden on them. Your own scenes aren't changed.\n\nContinue?"
            : "GiftDeck will make a \"GiftDeck Portrait\" scene collection and profile (1080x1920) from your current OBS setup, and start OBS hidden on them. Your own scenes aren't changed.\n\nContinue?";
        if (MessageBox.Show(ask, "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _engineBusy = true;
        UpdateEngine();
        EngineSay("Setting up portrait OBS…", "MutedBrush");
        try
        {
            await Hub.Engine.SetUpPortraitAsync(convert);
            _loading = true;
            ManagedBox.IsChecked = true;
            _loading = false;
            EngineSay("Done. OBS is running hidden on your portrait canvas.", "SuccessBrush");
        }
        catch (Exception ex) { EngineSay(ex.Message, "DangerBrush"); Log.Write("Set up portrait OBS failed: " + ex.Message); }
        finally { _engineBusy = false; UpdateEngine(); }
    }

    void ShowObs_Click(object sender, RoutedEventArgs e)
    {
        if (!Hub.Engine.ShowWindow()) EngineSay("Couldn't find OBS's window.", "WarnBrush");
        else EngineSay("", "MutedBrush");
        UpdateEngine();
    }

    void Details_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Tt.State;
        s.SendVertical = VerticalBox.IsChecked == true;
        Tt.Save();
    }

    void Token_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Tt.State.Token = TokenBox.Text.Trim();
        Tt.Save();
        UpdateChecklist();
    }

    void Username_Changed(object sender, RoutedEventArgs e)
    {
        var name = UsernameBox.Text.Trim().TrimStart('@');
        if (name == Hub.Settings.BridgeUsername) return;
        SetUsername(name);
    }

    void SetUsername(string name)
    {
        Hub.Settings.BridgeUsername = name;
        Hub.SaveSettings();
        UsernameBox.Text = name;
        Hub.Bridge.Restart(); // reconnect to the new account's LIVE
        UpdateUsernameNote();
    }

    static string Clean(string name) => (name ?? "").Trim().TrimStart('@');

    // Only informs: reading someone else's public LIVE is allowed, but it should be a deliberate choice (or a caught typo).
    void UpdateUsernameNote()
    {
        var mine = Clean(Tt.State.AccountUsername);
        var watching = Clean(Hub.Settings.BridgeUsername);
        bool differs = mine.Length > 0 && watching.Length > 0 && !string.Equals(mine, watching, StringComparison.OrdinalIgnoreCase);
        UsernameNote.Text = differs ? $"This isn't the account you're logged in with (@{mine}). GiftDeck will read @{watching}'s LIVE." : "";
        UsernameNote.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        UpdateChecklist();
    }

    async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        TokenError.Text = "";
        AccountText.Text = "Finish logging in in your browser (you have 5 minutes)";
        try
        {
            var token = await StreamlabsLogin.LoginAsync(TimeSpan.FromMinutes(5));
            Tt.State.Token = token;
            Tt.Save();
            _loading = true;
            TokenBox.Text = token;
            _loading = false;
            await CheckAccount();
        }
        catch (Exception ex) { AccountText.Text = ""; TokenError.Text = ex.Message; }
        finally { LoginButton.IsEnabled = true; }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Tt.State.GeneratorConfigPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) { Browse_Click(sender, e); return; }
            Tt.ImportTokenFromGenerator(path);
            _loading = true;
            TokenBox.Text = Tt.State.Token;
            _loading = false;
            TokenError.Text = "";
            AccountText.Text = "Token imported from " + path;
            _ = CheckAccount();
        }
        catch (Exception ex) { TokenError.Text = ex.Message; }
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "config.json|config.json|All files|*.*", Title = "Choose the Stream Key Generator's config.json" };
        if (dlg.ShowDialog() != true) return;
        Tt.State.GeneratorConfigPath = dlg.FileName;
        Tt.Save();
        Import_Click(sender, e);
    }

    async void Check_Click(object sender, RoutedEventArgs e) => await CheckAccount();

    async Task CheckAccount()
    {
        AccountText.Text = "Checking with Streamlabs";
        try
        {
            var a = await Tt.InfoAsync();
            AccountText.Text = $"TikTok account: {a.Username ?? "unknown"}. LIVE access: {(a.CanBeLive ? "yes" : "no")}" + (a.Status != null ? $" (status {a.Status})" : "") + ".";
            TokenError.Text = a.CanBeLive ? "" : "This account cannot go LIVE through Streamlabs yet. Apply for Streamlabs TikTok LIVE access first.";
            if (!string.IsNullOrWhiteSpace(a.Username))
            {
                Tt.State.AccountUsername = Clean(a.Username);
                Tt.Save();
                // First login: fill in whose LIVE to read, so there's nothing to type.
                if (string.IsNullOrWhiteSpace(Hub.Settings.BridgeUsername))
                {
                    SetUsername(Clean(a.Username));
                    AccountText.Text += $" GiftDeck will read @{Clean(a.Username)}'s LIVE.";
                }
            }
            UpdateUsernameNote();
        }
        catch (Exception ex) { AccountText.Text = ""; TokenError.Text = ex.Message; }
    }

    void UpdateFfmpeg()
    {
        var found = RelayService.FindFfmpeg();
        FfmpegStatus.Text = found != null ? "ffmpeg is installed \u2713" : "ffmpeg isn't installed yet";
        FfmpegStatus.Foreground = (System.Windows.Media.Brush)FindResource(found != null ? "SuccessBrush" : "WarnBrush");
        FfmpegButton.Visibility = found != null ? Visibility.Collapsed : Visibility.Visible;
    }

    async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        FfmpegButton.IsEnabled = false;
        FfmpegProgress.Visibility = Visibility.Visible;
        FfmpegStatus.Text = "Downloading ffmpeg\u2026";
        try
        {
            await FfmpegDownloader.DownloadAsync(new Progress<double>(p =>
            {
                FfmpegProgress.Value = p;
                FfmpegStatus.Text = $"Downloading ffmpeg\u2026 {p:P0}";
            }));
        }
        catch (Exception ex)
        {
            FfmpegStatus.Text = "Download failed: " + ex.Message;
            FfmpegButton.IsEnabled = true;
            FfmpegProgress.Visibility = Visibility.Collapsed;
            return;
        }
        FfmpegProgress.Visibility = Visibility.Collapsed;
        FfmpegButton.IsEnabled = true;
        UpdateFfmpeg();
    }

    void CopyRelayServer_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(RelayService.LocalServer); } catch { } }
    void CopyRelayKey_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(RelayService.LocalKey); } catch { } }
}
