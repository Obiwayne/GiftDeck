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

        LoadMenuStyle();
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
        PreviewNote.Visibility = Hub.Web.Running ? Visibility.Collapsed : Visibility.Visible;
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
            _previewReady = true;
            FitPreview();
        }
        catch (Exception ex)
        {
            Log.Write("Board preview unavailable: " + ex.Message);
        }
    }

    // Scales the page so the whole board fits the preview box.
    // The preview box takes the board's shape (a tall box for side columns, a wide one for a strip) and the
    // helper page shows the real board at its real size, shrunk to fit, so nothing is cut off.
    string _previewUrl;

    void FitPreview()
    {
        var (w, h) = BoardSize(Hub.Overlays.Config.Menu);
        Preview.Height = Math.Clamp(420.0 * h / w, 150, 560);
        if (!_previewReady) return;
        var url = $"{Hub.Web.BaseUrl}/overlay/preview?src=/overlay/menu&w={w}&h={h}";
        if (url == _previewUrl) return;
        _previewUrl = url;
        Preview.Source = new Uri(url);
    }

    // The size the board needs in a Browser Source.
    static (int W, int H) BoardSize(MenuConfig m)
    {
        if (m.Layout is "sides" or "carousel" or "ticker" || m.TileSize <= 0) return LayoutSize(m);
        int cols = Math.Clamp(m.Columns, 1, 12);
        int gap = m.ShowLines ? Math.Clamp(m.LineWidth, 1, 20) : 0;
        int rows = (int)Math.Ceiling(Math.Max(1, Hub.Overlays.VisibleTileCount) / (double)cols);
        return (cols * (m.TileSize + gap), (int)(m.TileSize * 0.22 * 1.15 + 14 + rows * (m.TileSize + gap)));
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
        var (lw, lh) = LayoutSize(m);
        if (m.Layout == "sides") { MenuSizeHint.Text = $"Side columns cover the whole screen: make the Browser Source {lw} x {lh} (or 1920 x 1080 for landscape) and put it over the game. The middle stays see-through.{hiddenNote}"; return; }
        if (m.Layout is "carousel" or "ticker") { MenuSizeHint.Text = $"A strip across the screen: a Browser Source of {lw} x {lh} works well; place it where you like.{hiddenNote}"; return; }
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
        _previewUrl = null;
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
        foreach (var t in cfg.Menu.Tiles) t.IsOff = !Hub.Overlays.TileVisible(t);
        TilesList.ItemsSource = null;
        TilesList.ItemsSource = cfg.Menu.Tiles.ToList();
        NoTiles.Visibility = cfg.Menu.Tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TilesHeader.Visibility = cfg.Menu.Tiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int off = cfg.Menu.Tiles.Count(t => t.IsOff);
        TilesHead.Text = cfg.Menu.Tiles.Count == 0 ? "Tiles" : $"Tiles ({cfg.Menu.Tiles.Count - off} on the board" + (off > 0 ? $", {off} with the event off)" : ")");
        FitPreview();
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
        m.Layout = TagOf(MenuLayout) is { Length: > 0 } layout ? layout : "grid";
        m.Look = TagOf(MenuLook) is { Length: > 0 } look ? look : "classic";
        m.CardColor = Hex(MenuCardColor.Text, m.CardColor);
        m.CardColor2 = Hex(MenuCardColor2.Text, m.CardColor2);
        m.TextColor = Hex(MenuTextColor.Text, m.TextColor);
        m.Speed = (int)Math.Round(MenuSpeed.Value);
        m.Direction = TagOf(MenuDirection);
        if (int.TryParse(MenuPerView.Text.Trim(), out int per) && per >= 1 && per <= 12) m.PerView = per;
        m.TextOutline = MenuOutline.IsChecked == true;
        m.Highlight = MenuHighlight.IsChecked == true;
        ShowMenuFields();
        UpdateSizeHint();
        FitPreview();
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

    void TileUrl_Click(object sender, RoutedEventArgs e)
    {
        var t = Of<MenuTile>(sender);
        if (t == null) return;
        var url = PromptWindow.Ask(Window.GetWindow(this), "Web address of the picture (https://…)", t.ImageUrl.StartsWith("http") ? t.ImageUrl : "", "Use it");
        if (url == null) return;
        if (url.Length > 0 && !url.StartsWith("http://") && !url.StartsWith("https://")) { Status.Text = "That isn't a web address: it has to start with https://"; return; }
        t.ImageUrl = url;
        Hub.Overlays.Touch();
    }

    void TileResetPicture_Click(object sender, RoutedEventArgs e)
    {
        var t = Of<MenuTile>(sender);
        if (t == null) return;
        t.ImageUrl = "";
        Hub.Overlays.Touch();
    }

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
    async void ObsMenu_Click(object sender, RoutedEventArgs e)
    {
        var (w, h) = BoardSize(Hub.Overlays.Config.Menu);
        await AddToObs("MayhemDeck gift menu", Url("/overlay/menu"), w, h);
    }

    // ---- Gift menu board styles ----

    // The Browser Source size each layout wants.
    static (int W, int H) LayoutSize(MenuConfig m) => m.Layout switch
    {
        "sides" => (1080, 1920),
        "carousel" => (1080, 300),
        "ticker" => (1080, 300),
        _ => (1250, 700),
    };

    static void Select(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    // The quick style a layout and look match (so its chip shows as picked), or null.
    static string PresetFor(string layout, string look) => (layout, look) switch
    {
        ("grid" or "", "classic") => "grid",
        ("grid", "cards") => "neongrid",
        ("sides", "cards") => "sidecards",
        ("sides", "clean") => "sideicons",
        ("carousel", _) => "carousel",
        ("ticker", _) => "ticker",
        _ => null,
    };

    // The tile list's Colour and Side columns only show when the style uses them.
    public static readonly DependencyProperty ShowTileColorProperty = DependencyProperty.Register(nameof(ShowTileColor), typeof(bool), typeof(OverlaysView));
    public static readonly DependencyProperty ShowTileSideProperty = DependencyProperty.Register(nameof(ShowTileSide), typeof(bool), typeof(OverlaysView));
    public bool ShowTileColor { get => (bool)GetValue(ShowTileColorProperty); set => SetValue(ShowTileColorProperty, value); }
    public bool ShowTileSide { get => (bool)GetValue(ShowTileSideProperty); set => SetValue(ShowTileSideProperty, value); }

    static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    void LoadMenuStyle()
    {
        bool was = _loading;
        _loading = true;
        var m = Hub.Overlays.Config.Menu;
        Select(MenuLayout, m.Layout);
        Select(MenuLook, m.Look);
        MenuCardColor.Text = m.CardColor;
        MenuCardColor2.Text = m.CardColor2;
        MenuTextColor.Text = m.TextColor;
        MenuSpeed.Value = Math.Clamp(m.Speed, 1, 10);
        MenuPerView.Text = m.PerView.ToString();
        MenuOutline.IsChecked = m.TextOutline;
        MenuHighlight.IsChecked = m.Highlight;
        FillDirections(m.Layout, m.Direction);
        ShowMenuFields();
        _loading = was;
    }

    void FillDirections(string layout, string current)
    {
        MenuDirection.Items.Clear();
        var items = layout == "ticker"
            ? new[] { ("up", "Bottom to top"), ("down", "Top to bottom") }
            : new[] { ("left", "Right to left"), ("right", "Left to right") };
        foreach (var (tag, text) in items) MenuDirection.Items.Add(new ComboBoxItem { Tag = tag, Content = text });
        Select(MenuDirection, string.IsNullOrEmpty(current) ? items[0].Item1 : current);
    }

    // Only the settings that do something in this layout and look.
    void ShowMenuFields()
    {
        var layout = TagOf(MenuLayout);
        var look = TagOf(MenuLook);
        bool grid = layout == "grid" || layout == "";
        bool moving = layout is "carousel" or "ticker";
        // Columns, square size and header colour also shape the carousel (tiles across, tile size, bar colour).
        MenuGridRow.Visibility = grid || layout == "carousel" ? Visibility.Visible : Visibility.Collapsed;
        MenuLinesRow.Visibility = grid || (layout == "carousel" && look == "classic") ? Visibility.Visible : Visibility.Collapsed;
        MenuColumnsLabel.Text = layout == "carousel" ? "Tiles across" : "Columns";
        MenuHeaderColorLabel.Text = layout == "carousel" ? "Bar colour" : "Header colour";
        MenuTransparent.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        MenuGridHead.Text = grid ? "GRID" : "TILES ACROSS THE STRIP";
        MenuHeaderColorPanel.Visibility = grid || layout == "carousel" ? Visibility.Visible : Visibility.Collapsed;
        MenuColorsGroup.Visibility = look == "cards" || MenuHeaderColorPanel.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ShowTileColor = look == "cards";
        ShowTileSide = layout == "sides";
        foreach (var chip in PresetPanel.Children.OfType<RadioButton>())
            chip.IsChecked = (string)chip.Tag == PresetFor(layout, look);
        MenuMotionRow.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        MenuPerViewPanel.Visibility = layout == "ticker" ? Visibility.Visible : Visibility.Collapsed;
        MenuSpeedLabel.Text = layout == "ticker" ? "How often it rolls" : "Speed";
        MenuCardColorPanel.Visibility = MenuCardColor2Panel.Visibility = look == "cards" ? Visibility.Visible : Visibility.Collapsed;
        MenuTitleLabel.Text = grid ? "Header text" : "Title (big text above the tiles; leave blank for none)";
    }

    void MenuCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MenuLayout == null || MenuDirection == null) return;
        if (sender == MenuLayout)
        {
            _loading = true;
            FillDirections(TagOf(MenuLayout), "");
            _loading = false;
        }
        Menu_Changed(sender, e);
    }

    void MenuSpeed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || MenuLayout == null) return;
        Menu_Changed(sender, e);
    }

    void TileSide_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        Hub.Overlays.Touch();
    }

    // Quick styles: set the layout, look and colours in one go (the tiles stay as they are).
    void MenuPreset_Click(object sender, RoutedEventArgs e)
    {
        var m = Hub.Overlays.Config.Menu;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "grid":
                m.Layout = "grid"; m.Look = "classic"; m.ShowLines = true; m.LineColor = "#FFFFFF"; m.Transparent = false; m.TextColor = "#FFFFFF";
                break;
            case "neongrid":
                m.Layout = "grid"; m.Look = "cards"; m.CardColor = "#EC4899"; m.CardColor2 = "#BE185D"; m.ShowLines = true; m.LineColor = "#831843"; m.LineWidth = 3;
                m.Transparent = true; m.TextColor = "#FFFFFF"; m.TextOutline = true;
                break;
            case "sidecards":
                m.Layout = "sides"; m.Look = "cards"; m.CardColor = "#22C55E"; m.CardColor2 = "#15803D"; m.TextColor = "#FFFFFF"; m.TextOutline = true;
                break;
            case "sideicons":
                m.Layout = "sides"; m.Look = "clean"; m.TextColor = "#FFFFFF"; m.TextOutline = true;
                break;
            case "carousel":
                m.Layout = "carousel"; m.Look = "cards"; m.CardColor = "#7C3AED"; m.CardColor2 = "#DB2777"; m.TextColor = "#FFFFFF"; m.TextOutline = true;
                m.HeaderColor = "#F59E0B"; m.Speed = 4; m.Direction = "left";
                if (m.Columns < 3) m.Columns = 5;
                break;
            case "ticker":
                m.Layout = "ticker"; m.Look = "cards"; m.CardColor = "#7C3AED"; m.CardColor2 = "#DB2777"; m.TextColor = "#FFFFFF"; m.TextOutline = true;
                m.Speed = 4; m.Direction = "up"; m.PerView = 4;
                break;
            default: return;
        }
        LoadMenuStyle();
        MenuHeaderColor.Text = m.HeaderColor;
        MenuColumns.Text = m.Columns.ToString();
        MenuShowLines.IsChecked = m.ShowLines;
        MenuLineColor.Text = m.LineColor;
        MenuLineWidth.Text = m.LineWidth.ToString();
        MenuTransparent.IsChecked = m.Transparent;
        UpdateSizeHint();
        FitPreview();
        Hub.Overlays.Touch();
        Status.Text = "Board style changed. The preview and your stream show it straight away.";
    }

    static string Hex(string text, string fallback)
    {
        var c = (text ?? "").Trim();
        if (c.Length > 0 && !c.StartsWith("#")) c = "#" + c;
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$") ? c : fallback;
    }

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
    async void ObsGoal_Click(object sender, RoutedEventArgs e) { var g = Of<Goal>(sender); if (g != null) await AddToObs("MayhemDeck goal: " + g.Title, Url("/overlay/goal/" + g.Id), 800, 140); }

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
    async void ObsCountdown_Click(object sender, RoutedEventArgs e) { var c = Of<Countdown>(sender); if (c != null) await AddToObs("MayhemDeck countdown: " + c.Title, Url("/overlay/countdown/" + c.Id), 500, 150); }

    // Counters
    void CopyCounter_Click(object sender, RoutedEventArgs e) { var c = Of<CounterRow>(sender); if (c != null) Copy(Url("/overlay/counter/" + c.Metric)); }
    async void ObsCounter_Click(object sender, RoutedEventArgs e) { var c = Of<CounterRow>(sender); if (c != null) await AddToObs("MayhemDeck counter: " + c.Label, Url("/overlay/counter/" + c.Metric), 400, 100); }
    void ResetStats_Click(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show("Reset this stream's totals to zero? Goals keep their progress.", "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hub.Overlays.ResetStats();
    }

    // Alerts and style
    void CopyAlerts_Click(object sender, RoutedEventArgs e) => Copy(Url("/overlay/alerts"));
    async void ObsAlerts_Click(object sender, RoutedEventArgs e) => await AddToObs("MayhemDeck alerts", Url("/overlay/alerts"), 800, 250, audioViaObs: true); // alert videos' sound
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
