using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Services;
using GiftDeck.Views;

namespace GiftDeck;

public partial class MainWindow : Window
{
    readonly Dictionary<string, UserControl> _views = new Dictionary<string, UserControl>();

    public MainWindow()
    {
        InitializeComponent();
        FitToScreen();
        if (Storage.IsDevData) Title = "MayhemDeck v2 (development build)";
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Spotify.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        BridgeService.AccountChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        // Loading turns into an error after a while even if nothing reports a change.
        var statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        statusTimer.Tick += (_, _) => UpdateStatus();
        statusTimer.Start();
        UpdateStatus();
        BuildNav();
        _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); ProfileNote.Text = ""; };
        RefreshProfiles();
        Hub.Profiles.Changed += () => Dispatcher.BeginInvoke(OnProfilesChanged);
        ApplyCollapsed(Hub.Settings.SidebarCollapsed);
        StartTtsMuteNav();
        Setup.Start(); // covers the window until everything's set up and connected
        // GIFTDECK_START_PAGE (development builds) opens straight on a page, e.g. "scenes".
        Navigate(Environment.GetEnvironmentVariable("GIFTDECK_START_PAGE") is { Length: > 0 } page ? page : "dashboard");
        Loaded += (_, _) => WarnUnreadable();
    }

    // A settings or events file that couldn't be read starts over empty; say so, and where the old one was kept.
    int _unreadableShown;
    void WarnUnreadable()
    {
        string[] files;
        lock (Storage.Unreadable) files = Storage.Unreadable.Skip(_unreadableShown).ToArray();
        if (files.Length == 0) return;
        _unreadableShown += files.Length;
        Views.AppDialog.Show(this, "MayhemDeck couldn't read some of its saved files, so those settings or events started over empty. "
            + "The old files were kept, untouched, here:\n\n" + string.Join("\n", files)
            + "\n\nThis can happen after a crash or when a profile comes from a newer MayhemDeck.",
            "Some saved files couldn't be read", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // Menu: page key, label, icon (Segoe Fluent Icons / MDL2 code point).
    // The menu, grouped: an entry with a null glyph is a section heading.
    static readonly (string Key, string Label, string Glyph)[] NavItems =
    {
        ("dashboard", "Dashboard", ""),
        (null, "LIVE STREAM", null),
        ("golive", "Go LIVE", ""),
        ("scenes", "Scenes", ""),
        ("setup", "Stream Setup", ""),
        (null, "GAMES & EVENTS", null),
        ("games", "Games", ""),
        ("events", "Events", ""),
        ("profiles", "Profiles", ""),
        ("overlays", "Overlays", ""),
        (null, "SOUND", null),
        ("music", "Music", ""),
        ("spotify", "Spotify", ""),
        ("tts", "Text to speech", ""),
        (null, "SETTINGS", null),
        ("obs", "OBS", ""),
        ("settings", "Settings", ""),
    };

    readonly List<TextBlock> _navLabels = new List<TextBlock>();
    readonly List<(TextBlock Label, Border Line)> _navSections = new List<(TextBlock, Border)>();

    void BuildNav()
    {
        foreach (var (key, label, glyph) in NavItems)
        {
            if (glyph == null)
            {
                // Section heading: small grey caps; a thin line instead when the menu is collapsed to icons.
                var heading = new TextBlock { Text = label, Style = (Style)FindResource("Muted"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 16, 0, 6) };
                var line = new Border { Height = 1, Background = (Brush)FindResource("LineBrush"), Margin = new Thickness(8, 12, 8, 8), Visibility = Visibility.Collapsed };
                _navSections.Add((heading, line));
                NavPanel.Children.Add(heading);
                NavPanel.Children.Add(line);
                continue;
            }
            var text = new TextBlock { Text = label, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _navLabels.Add(text);
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 16,
                Width = 20,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            content.Children.Add(text);
            var rb = new RadioButton { Style = (Style)FindResource("NavButton"), Content = content, Tag = key, GroupName = "Nav" };
            System.Windows.Automation.AutomationProperties.SetName(rb, label);
            rb.Checked += Nav_Checked;
            NavPanel.Children.Add(rb);
        }
    }

    bool _loadingProfiles;
    string _shownProfile;
    string _currentPage = "dashboard";

    void RefreshProfiles()
    {
        _loadingProfiles = true;
        ProfileBox.ItemsSource = Hub.Profiles.List();
        ProfileBox.SelectedItem = Hub.Profiles.Active;
        _loadingProfiles = false;
        _shownProfile ??= Hub.Profiles.Active;
    }

    readonly System.Windows.Threading.DispatcherTimer _noteTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };

    void ProfileBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProfiles || ProfileBox.SelectedItem is not string name || name == Hub.Profiles.Active) return;
        SwitchProfile(name);
    }

    // Switches profile with the loading animation. Used by the dropdown and the Profiles page.
    public void SwitchProfile(string name)
    {
        if (name == Hub.Profiles.Active || SwitchOverlay.Busy) { RefreshProfiles(); return; }
        bool live = Hub.TikTok.Live || (Hub.TikFinity.Connected && Hub.TikFinity.TikTokLive == true);
        if (live && Views.AppDialog.Show(this, $"You're LIVE. Switch to \"{name}\" now? Gifts will start doing what that profile says straight away.",
                "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            RefreshProfiles(); // put the dropdown back
            return;
        }
        SwitchOverlay.Play(name, () =>
        {
            Hub.Profiles.Switch(name);
            var events = Hub.Rules.Rules.Count;
            return $"{events} {(events == 1 ? "event" : "events")}, overlays, title and category loaded";
        }, () =>
        {
            RefreshProfiles();
            if (Hub.Profiles.Active != name) return; // the switch failed; the overlay already said why
            ProfileNote.Text = $"\u2713 Using {name}";
            ProfileNote.Foreground = (Brush)FindResource("SuccessBrush");
            _noteTimer.Stop();
            _noteTimer.Start();
        });
    }

    // Re-read the list when the dropdown opens, but only swap it if profiles were added or removed meanwhile.
    void ProfileBox_Opened(object sender, EventArgs e)
    {
        var shown = (ProfileBox.ItemsSource as IEnumerable<string>)?.ToList() ?? new List<string>();
        if (!shown.SequenceEqual(Hub.Profiles.List())) RefreshProfiles();
    }

    void ManageProfiles_Click(object sender, RoutedEventArgs e) => Navigate("profiles");

    // After a switch, pages that show the profile's events, overlays or title are rebuilt.
    void OnProfilesChanged()
    {
        RefreshProfiles();
        WarnUnreadable();
        if (_shownProfile == Hub.Profiles.Active) return;
        _shownProfile = Hub.Profiles.Active;
        if (_views.TryGetValue("golive", out var oldGoLive) && oldGoLive is GoLiveView goLive) goLive.Detach();
        foreach (var key in new[] { "events", "overlays", "golive" }) _views.Remove(key);
        if (key_is_current()) Show(_currentPage);

        bool key_is_current() => _currentPage is "events" or "overlays" or "golive";
    }

    void Collapse_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.SidebarCollapsed = !Hub.Settings.SidebarCollapsed;
        Hub.SaveSettings();
        ApplyCollapsed(Hub.Settings.SidebarCollapsed);
    }

    // Collapsed: icons only (names become tooltips), logo without the name, connection dots without text.
    void ApplyCollapsed(bool collapsed)
    {
        SideColumn.Width = new GridLength(collapsed ? 70 : 228);
        var hidden = collapsed ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Visibility = hidden;
        Header.Margin = collapsed ? new Thickness(16, 22, 16, 22) : new Thickness(20, 22, 20, 22);
        NavPanel.Margin = collapsed ? new Thickness(10, 0, 10, 0) : new Thickness(12, 0, 12, 0);
        Footer.Margin = collapsed ? new Thickness(0, 0, 0, 20) : new Thickness(20, 0, 20, 20);
        foreach (var t in _navLabels) t.Visibility = hidden;
        foreach (var (heading, line) in _navSections)
        {
            heading.Visibility = hidden;
            line.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var rb in NavPanel.Children.OfType<RadioButton>())
            rb.ToolTip = collapsed ? ((TextBlock)((StackPanel)rb.Content).Children[1]).Text : null;

        CollapseLabel.Visibility = hidden;
        CollapseGlyph.Text = collapsed ? "\uE76C" : "\uE76B";
        CollapseButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        CollapseButton.ToolTip = collapsed ? "Expand menu" : null;
        ConnectionsLabel.Visibility = hidden;
        TikText.Visibility = ObsText.Visibility = SpotText.Visibility = hidden;
        foreach (var dot in new FrameworkElement[] { TikDot, ObsDot, SpotDot })
            ((StackPanel)dot.Parent).HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        foreach (var dot in new[] { TikDot, ObsDot, SpotDot }) dot.Margin = collapsed ? new Thickness(0) : new Thickness(0, 0, 9, 0);
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string key) Show(key);
    }

    public void Navigate(string key)
    {
        foreach (var rb in NavPanel.Children.OfType<RadioButton>())
            if ((string)rb.Tag == key) rb.IsChecked = true;
    }

    void Show(string key)
    {
        if (!_views.TryGetValue(key, out var view))
        {
            view = key switch
            {
                "golive" => new GoLiveView(),
                "scenes" => new ScenesView(),
                "setup" => new StreamSetupView(),
                "games" => new GamesView(),
                "profiles" => new ProfilesView(),
                "events" => new EventsView(),
                "overlays" => new OverlaysView(),
                "music" => new MusicView(),
                "spotify" => new SpotifyView(),
                "tts" => new TtsView(),
                "obs" => new ObsView(),
                "settings" => new SettingsView(),
                _ => new DashboardView(),
            };
            _views[key] = view;
        }
        ContentHost.Content = view;
        _currentPage = key;
        ShowLivePanel(!FullWidthPages.Contains(key));
    }

    // Pages that use the whole width: the live chat / gifts panel steps aside while they're open.
    static readonly HashSet<string> FullWidthPages = new HashSet<string> { "games" };
    GridLength _liveWidth = new GridLength(620);

    void ShowLivePanel(bool show)
    {
        bool shown = LiveHost.Visibility == Visibility.Visible;
        if (show == shown) return;
        if (!show) _liveWidth = LiveColumn.Width; // keep the width the user dragged it to
        LiveColumn.MinWidth = show ? 420 : 0;
        LiveColumn.Width = show ? _liveWidth : new GridLength(0);
        LiveHost.Visibility = LiveSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateStatus()
    {
        var ok = (Brush)FindResource("SuccessBrush");
        var warn = (Brush)FindResource("WarnBrush");
        var off = (Brush)FindResource("MutedBrush");

        Brush For(StatusKind k) => k switch
        {
            StatusKind.Ok => ok,
            StatusKind.Loading => warn,
            StatusKind.Error => (Brush)FindResource("DangerBrush"),
            _ => off,
        };

        // Full wording in the tooltip and on the right-hand panel; the sidebar keeps it short.
        var (rk, rtext) = StartupStatus.Reader();
        TikDot.Fill = For(rk);
        TikText.Text = rk == StatusKind.Error && !BridgeService.NeedsUsername
            ? (BridgeService.InUse ? "TikTok bridge: error" : "TikFinity: something's wrong")
            : rtext;
        var (ok2, otext) = StartupStatus.Obs();
        ObsDot.Fill = For(ok2);
        ObsText.Text = ok2 == StatusKind.Error ? "OBS: error" : otext;

        SpotDot.Fill = Hub.Spotify.Linked ? ok : off;
        SpotText.Text = Hub.Spotify.Linked ? "Spotify: " + (Hub.Spotify.AccountName ?? "linked") : "Spotify not linked";
        TikDot.ToolTip = TikText.ToolTip = rtext;
        ObsDot.ToolTip = ObsText.ToolTip = otext;
        SpotDot.ToolTip = SpotText.Text;
    }

    // The designed size is for big monitors; on a laptop (e.g. 1080p at 150%) keep the whole window on screen.
    void FitToScreen()
    {
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
        Width = Math.Min(Width, area.Width);
        Height = Math.Min(Height, area.Height);
        if (Width >= area.Width - 1 && Height >= area.Height - 1) WindowState = WindowState.Maximized;
    }

    // ---- Closing: stay on screen, spinner and steps, until everything has shut down ----

    bool _shuttingDown, _shutdownDone;

    protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownDone) { base.OnClosing(e); return; }
        e.Cancel = true;
        if (_shuttingDown) return; // the close button again while it's already shutting down
        bool live = Hub.TikTok.Live || (Hub.TikFinity.Connected && Hub.TikFinity.TikTokLive == true);
        if (live && Views.AppDialog.Show(this, "You're LIVE. Close MayhemDeck anyway?\n\nGifts will stop doing anything, and a LIVE MayhemDeck opened stays open on TikTok until it's ended. To end it first, press End LIVE on the Go LIVE page.",
                "Close MayhemDeck while LIVE?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _shuttingDown = true;

        ShutdownOverlay.Visibility = Visibility.Visible;
        ShutdownSpin.BeginAnimation(RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        ShutdownStep.Text = "Starting…";
        var step = new Progress<string>(text => ShutdownStep.Text = text);
        try { await Task.Run(() => Hub.StopServices(t => ((IProgress<string>)step).Report(t))); }
        catch (Exception ex) { Log.Write("Shutting down: " + ex.Message); }

        _shutdownDone = true;
        Close();
    }
}
