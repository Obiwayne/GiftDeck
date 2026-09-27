using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class OverlaysView : UserControl
{
    public static GoalMetric[] Metrics { get; } = (GoalMetric[])Enum.GetValues(typeof(GoalMetric));

    public class CounterRow : Observable
    {
        public string Metric { get; set; }
        public string Label { get; set; }
        public Func<int> Read { get; set; }
        public int Value => Read();
        public void Refresh() => Raise(nameof(Value));
    }

    readonly List<CounterRow> _counters;
    bool _loading = true;

    public OverlaysView()
    {
        InitializeComponent();
        var cfg = Hub.Overlays.Config;
        var stats = cfg.Stats;

        _counters = new List<CounterRow>
        {
            new CounterRow { Metric = "follows", Label = "New followers", Read = () => stats.Follows },
            new CounterRow { Metric = "likes", Label = "Likes", Read = () => stats.Likes },
            new CounterRow { Metric = "shares", Label = "Shares", Read = () => stats.Shares },
            new CounterRow { Metric = "coins", Label = "Coins", Read = () => stats.Coins },
            new CounterRow { Metric = "gifts", Label = "Gifts", Read = () => stats.Gifts },
            new CounterRow { Metric = "viewers", Label = "Watching now", Read = () => stats.Viewers },
        };
        CountersList.ItemsSource = _counters;
        stats.PropertyChanged += (s, e) => Dispatcher.BeginInvoke(() => { foreach (var c in _counters) c.Refresh(); });

        MenuTitle.Text = cfg.Menu.Title;
        MenuColumns.Text = cfg.Menu.Columns.ToString();
        MenuTileSize.Text = cfg.Menu.TileSize.ToString();
        MenuShowLines.IsChecked = cfg.Menu.ShowLines;
        MenuLineColor.Text = cfg.Menu.LineColor;
        MenuLineWidth.Text = cfg.Menu.LineWidth.ToString();
        MenuHeaderColor.Text = cfg.Menu.HeaderColor;
        MenuFont.Text = cfg.Menu.Font;
        MenuShowSubtitle.IsChecked = cfg.Menu.ShowSubtitle;
        MenuTransparent.IsChecked = cfg.Menu.Transparent;

        AlertSeconds.Text = cfg.AlertSeconds.ToString();
        AlertMinCoins.Text = cfg.AlertMinCoins.ToString();
        AlertFollows.IsChecked = cfg.AlertFollows;
        AlertShares.IsChecked = cfg.AlertShares;
        AccentBox.Text = cfg.Accent;
        FontBox.Text = cfg.Font;

        Hub.Overlays.ListsChanged += () => Dispatcher.BeginInvoke(RefreshLists);
        RefreshLists();
        UpdateServer();
        UpdateSizeHint();
        _loading = false;
        Loaded += async (s, e) => await StartPreview();
    }

    // ---- Live preview of the gift menu board (a small embedded browser showing the real overlay page) ----

    bool _previewReady;

    async Task StartPreview()
    {
        if (_previewReady || !Hub.Web.Running) return;
        try
        {
            // Keep WebView2's browser cache with GiftDeck's other local data, not next to the program.
            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, cacheDir);
            await Preview.EnsureCoreWebView2Async(env);
            Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
            Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Preview.Source = new Uri(Hub.Web.BaseUrl + "/overlay/menu");
            _previewReady = true;
            FitPreview();
        }
        catch (Exception ex)
        {
            Log.Write("Board preview unavailable: " + ex.Message);
        }
    }

    // Scales the page so the whole board fits the preview box.
    void FitPreview()
    {
        if (!_previewReady) return;
        var m = Hub.Overlays.Config.Menu;
        int cols = Math.Clamp(m.Columns, 1, 12);
        int gap = m.ShowLines ? Math.Clamp(m.LineWidth, 1, 20) : 0;
        int tiles = Math.Max(1, Hub.Overlays.VisibleTileCount);
        int rows = (int)Math.Ceiling(tiles / (double)cols);
        double boardW, boardH;
        if (m.TileSize > 0)
        {
            boardW = cols * (m.TileSize + gap);
            boardH = m.TileSize * 0.22 * 1.15 + 14 + rows * (m.TileSize + gap);
        }
        else
        {
            boardW = 1250;
            boardH = 700;
        }
        double zoom = Math.Min(400.0 / boardW, 1.0);
        Preview.Height = Math.Clamp(boardH * zoom, 120, 420);
        Preview.ZoomFactor = zoom;
    }

    void UpdateSizeHint()
    {
        var m = Hub.Overlays.Config.Menu;
        int cols = Math.Clamp(m.Columns, 1, 12);
        int gap = m.ShowLines ? Math.Clamp(m.LineWidth, 1, 20) : 0;
        int visible = Hub.Overlays.VisibleTileCount;
        int hidden = m.Tiles.Count - visible;
        int rows = (int)Math.Ceiling(Math.Max(1, visible) / (double)cols);
        var hiddenNote = hidden > 0 ? $" {hidden} tile{(hidden == 1 ? " is" : "s are")} hidden because the event is switched off." : "";
        if (m.TileSize > 0)
        {
            int w = cols * (m.TileSize + gap);
            int h = (int)(m.TileSize * 0.22 * 1.15 + 14 + rows * (m.TileSize + gap));
            MenuSizeHint.Text = $"Square tiles: the board is {w} x {h} pixels ({rows} rows). Make the Browser Source that size or larger.{hiddenNote}";
        }
        else MenuSizeHint.Text = "Square size 0: tiles stretch to fill whatever size you give the Browser Source." + hiddenNote;
    }

    async void RefreshPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewReady) { await StartPreview(); return; }
        Preview.Reload();
        FitPreview();
    }

    void UpdateServer()
    {
        var web = Hub.Web;
        ServerStatus.Text = web.Running ? "Overlay server is running" : "Overlay server is not running";
        ServerUrl.Text = web.Running ? web.BaseUrl : (web.LastError ?? "");
    }

    void RefreshLists()
    {
        var cfg = Hub.Overlays.Config;
        GoalsList.ItemsSource = null;
        GoalsList.ItemsSource = cfg.Goals.ToList();
        CountdownsList.ItemsSource = null;
        CountdownsList.ItemsSource = cfg.Countdowns.ToList();
        NoGoals.Visibility = cfg.Goals.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoCountdowns.Visibility = cfg.Countdowns.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TilesList.ItemsSource = null;
        TilesList.ItemsSource = cfg.Menu.Tiles.ToList();
        NoTiles.Visibility = cfg.Menu.Tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Gift menu board
    void Menu_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var m = Hub.Overlays.Config.Menu;
        m.Title = MenuTitle.Text.Trim();
        if (int.TryParse(MenuColumns.Text.Trim(), out int cols) && cols >= 1 && cols <= 12) m.Columns = cols;
        if (int.TryParse(MenuTileSize.Text.Trim(), out int size) && size >= 0 && size <= 600) m.TileSize = size;
        m.ShowLines = MenuShowLines.IsChecked == true;
        var line = MenuLineColor.Text.Trim();
        if (line.Length > 0 && !line.StartsWith("#")) line = "#" + line;
        if (line.Length == 7 || line.Length == 4) m.LineColor = line;
        if (int.TryParse(MenuLineWidth.Text.Trim(), out int lw) && lw >= 1 && lw <= 20) m.LineWidth = lw;
        UpdateSizeHint();
        FitPreview();
        var color = MenuHeaderColor.Text.Trim();
        if (color.Length > 0 && !color.StartsWith("#")) color = "#" + color;
        if (color.Length == 7 || color.Length == 4) m.HeaderColor = color;
        if (MenuFont.Text.Trim().Length > 0) m.Font = MenuFont.Text.Trim();
        m.ShowSubtitle = MenuShowSubtitle.IsChecked == true;
        m.Transparent = MenuTransparent.IsChecked == true;
        Hub.Overlays.Touch();
    }

    void MenuFromEvents_Click(object sender, RoutedEventArgs e)
    {
        int n = Hub.Overlays.AddTilesFromRules();
        Status.Text = n == 0 ? "Every enabled event already has a tile. Enable more events on the Events page, or add a custom tile." : $"Added {n} tile{(n == 1 ? "" : "s")}.";
    }

    void MenuCustom_Click(object sender, RoutedEventArgs e) => Hub.Overlays.AddCustomTile();
    void TileUp_Click(object sender, RoutedEventArgs e) { var t = Of<MenuTile>(sender); if (t != null) Hub.Overlays.MoveTile(t, -1); }
    void TileDown_Click(object sender, RoutedEventArgs e) { var t = Of<MenuTile>(sender); if (t != null) Hub.Overlays.MoveTile(t, 1); }
    void TileRemove_Click(object sender, RoutedEventArgs e) { var t = Of<MenuTile>(sender); if (t != null) Hub.Overlays.RemoveTile(t); }

    void TileBrowse_Click(object sender, RoutedEventArgs e)
    {
        var t = Of<MenuTile>(sender);
        if (t == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg|All files|*.*", Title = "Choose a picture for this tile" };
        if (dlg.ShowDialog() != true) return;
        t.ImageUrl = dlg.FileName;
        Hub.Overlays.Touch();
    }

    void CopyMenu_Click(object sender, RoutedEventArgs e) => Copy(Url("/overlay/menu"));
    async void ObsMenu_Click(object sender, RoutedEventArgs e) => await AddToObs("GiftDeck gift menu", Url("/overlay/menu"), 1250, 700);

    static T Of<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    string Url(string path) => Hub.Web.BaseUrl + path;

    void Copy(string url)
    {
        try { Clipboard.SetText(url); Status.Text = "Copied " + url; } catch { }
    }

    async Task AddToObs(string name, string url, int w, int h, bool audioViaObs = false)
    {
        try
        {
            if (!Hub.Obs.Connected) await Hub.Obs.ConnectAsync();
            Status.Text = await Hub.Obs.AddBrowserSourceAsync(name, url, w, h, audioViaObs);
        }
        catch (Exception ex)
        {
            Status.Text = "Could not add to OBS: " + ex.Message;
        }
    }

    void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Hub.Overlays.Touch();
    }

    void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (Hub.Web.Running) Process.Start(new ProcessStartInfo(Hub.Web.BaseUrl + "/") { UseShellExecute = true });
    }

    // Goals
    void AddGoal_Click(object sender, RoutedEventArgs e) => Hub.Overlays.AddGoal();
    void RemoveGoal_Click(object sender, RoutedEventArgs e) { var g = Of<Goal>(sender); if (g != null) Hub.Overlays.RemoveGoal(g); }
    void ResetGoal_Click(object sender, RoutedEventArgs e) { var g = Of<Goal>(sender); if (g == null) return; g.Progress = 0; Hub.Overlays.Touch(); }
    void CopyGoal_Click(object sender, RoutedEventArgs e) { var g = Of<Goal>(sender); if (g != null) Copy(Url("/overlay/goal/" + g.Id)); }
    async void ObsGoal_Click(object sender, RoutedEventArgs e) { var g = Of<Goal>(sender); if (g != null) await AddToObs("GiftDeck goal: " + g.Title, Url("/overlay/goal/" + g.Id), 800, 140); }

    // Countdowns
    void AddCountdown_Click(object sender, RoutedEventArgs e) => Hub.Overlays.AddCountdown();
    void RemoveCountdown_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c != null) Hub.Overlays.RemoveCountdown(c); }
    void StartPause_Click(object sender, RoutedEventArgs e)
    {
        var c = Of<Countdown>(sender);
        if (c == null) return;
        if (c.Running) c.Pause(); else c.Start();
        Hub.Overlays.Touch();
    }
    void AddMinute_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c == null) return; c.Add(60); Hub.Overlays.Touch(); }
    void AddTenMinutes_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c == null) return; c.Add(600); Hub.Overlays.Touch(); }
    void SetCountdown_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c == null) return; c.Set(Math.Max(0, c.SetMinutes) * 60); Hub.Overlays.Touch(); }
    void CopyCountdown_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c != null) Copy(Url("/overlay/countdown/" + c.Id)); }
    async void ObsCountdown_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c != null) await AddToObs("GiftDeck countdown: " + c.Title, Url("/overlay/countdown/" + c.Id), 500, 150); }

    // Counters
    void CopyCounter_Click(object sender, RoutedEventArgs e) { var c = Of<CounterRow>(sender); if (c != null) Copy(Url("/overlay/counter/" + c.Metric)); }
    async void ObsCounter_Click(object sender, RoutedEventArgs e) { var c = Of<CounterRow>(sender); if (c != null) await AddToObs("GiftDeck counter: " + c.Label, Url("/overlay/counter/" + c.Metric), 400, 100); }
    void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset this stream's totals to zero? Goals keep their progress.", "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hub.Overlays.ResetStats();
    }

    // Alerts and style
    void CopyAlerts_Click(object sender, RoutedEventArgs e) => Copy(Url("/overlay/alerts"));
    async void ObsAlerts_Click(object sender, RoutedEventArgs e) => await AddToObs("GiftDeck alerts", Url("/overlay/alerts"), 800, 250, audioViaObs: true); // alert videos' sound
    void TestAlert_Click(object sender, RoutedEventArgs e) => Hub.Overlays.TestAlert();

    void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = Hub.Overlays.Config;
        if (int.TryParse(AlertSeconds.Text.Trim(), out int secs) && secs >= 2) cfg.AlertSeconds = secs;
        if (int.TryParse(AlertMinCoins.Text.Trim(), out int min) && min >= 0) cfg.AlertMinCoins = min;
        cfg.AlertFollows = AlertFollows.IsChecked == true;
        cfg.AlertShares = AlertShares.IsChecked == true;
        var accent = AccentBox.Text.Trim();
        if (accent.Length > 0 && !accent.StartsWith("#")) accent = "#" + accent;
        if (accent.Length == 7 || accent.Length == 4) cfg.Accent = accent;
        if (FontBox.Text.Trim().Length > 0) cfg.Font = FontBox.Text.Trim();
        Hub.Overlays.Touch();
    }
}
