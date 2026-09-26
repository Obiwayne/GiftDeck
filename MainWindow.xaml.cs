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
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Spotify.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateStatus();
        BuildNav();
        ApplyCollapsed(Hub.Settings.SidebarCollapsed);
        Navigate("dashboard");
    }

    // Menu: page key, label, icon (Segoe Fluent Icons / MDL2 code point).
    static readonly (string Key, string Label, string Glyph)[] NavItems =
    {
        ("dashboard", "Dashboard", "\uE80F"),
        ("golive", "Go LIVE", "\uE714"),
        ("setup", "Stream Setup", "\uE90F"),
        ("events", "Events", "\uE945"),
        ("overlays", "Overlays", "\uE7F4"),
        ("music", "Music", "\uE8D6"),
        ("spotify", "Spotify", "\uEC4F"),
        ("tts", "Text to speech", "\uE767"),
        ("obs", "OBS", "\uE722"),
        ("settings", "Settings", "\uE713"),
    };

    readonly List<TextBlock> _navLabels = new List<TextBlock>();

    void BuildNav()
    {
        foreach (var (key, label, glyph) in NavItems)
        {
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
        foreach (RadioButton rb in NavPanel.Children)
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
                "setup" => new StreamSetupView(),
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
    }

    void UpdateStatus()
    {
        var ok = (Brush)FindResource("SuccessBrush");
        var warn = (Brush)FindResource("WarnBrush");
        var off = (Brush)FindResource("MutedBrush");

        var t = Hub.TikFinity;
        if (BridgeService.InUse)
        {
            // GiftDeck's own bridge reads the LIVE directly; being offline is normal, not an error.
            TikDot.Fill = !t.Connected ? warn : t.TikTokLive == true ? ok : off;
            TikText.Text = !t.Connected ? "TikTok bridge starting" : t.TikTokLive == true ? "Connected to your LIVE" : "Waiting for your LIVE";
        }
        else
        {
            bool notOnLive = t.Connected && t.TikTokLive == false;
            TikDot.Fill = notOnLive ? (Brush)FindResource("DangerBrush") : t.Connected ? ok : t.ProcessRunning ? warn : off;
            TikText.Text = notOnLive ? "TikFinity not on your LIVE" : t.Connected ? "TikFinity feed live" : t.ProcessRunning ? "TikFinity starting up" : "TikFinity not running";
        }

        ObsDot.Fill = Hub.Obs.Connected ? ok : off;
        ObsText.Text = Hub.Obs.Connected ? "OBS connected" : "OBS not connected";

        SpotDot.Fill = Hub.Spotify.Linked ? ok : off;
        SpotText.Text = Hub.Spotify.Linked ? "Spotify: " + (Hub.Spotify.AccountName ?? "linked") : "Spotify not linked";
        TikDot.ToolTip = TikText.Text;
        ObsDot.ToolTip = ObsText.Text;
        SpotDot.ToolTip = SpotText.Text;
    }
}
