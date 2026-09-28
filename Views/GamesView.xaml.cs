using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The Games page: a library of every game GiftDeck has a page for (Hub.Packs.Packs), searchable and filtered by tier,
// as cover tiles. A game's own page depends on its tier:
//   ready   where the game was found, what to do before installing, its mods (Install/Update/Uninstall) or its server, presets
//   console how to set up the game's server, the connection (GameConsolePanel) and the commands it offers
//   keys    how to set it up, which window gets the key presses, and key ideas that become events in one click
public partial class GamesView : UserControl
{
    GamePackService Packs => Hub.Packs;

    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
    GamePack _selected;
    PackStatus _status;
    string _shownSignature;

    // What's happening on the selected pack (one install or uninstall at a time on this page).
    GamePack _busyPack;
    string _busyText;
    double _busyPart = -1;
    string _message;
    bool _messageIsError;
    ManualDownloadNeeded _manual;
    GamePack _manualPack;
    readonly Dictionary<string, Dictionary<string, string>> _manualFiles = new Dictionary<string, Dictionary<string, string>>();

    // The library: one tile per game, kept between refreshes so covers never flash.
    readonly Dictionary<string, GameTile> _tiles = new Dictionary<string, GameTile>(StringComparer.OrdinalIgnoreCase);
    string _tierFilter = "";

    // A game's page: what was drawn for which game, so the static parts aren't rebuilt every refresh.
    string _heroFor, _guideFor, _keysFor, _commandsSig;
    readonly Dictionary<string, GameConsolePanel> _consolePanels = new Dictionary<string, GameConsolePanel>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (bool ok, string text)> _commandResults = new Dictionary<string, (bool, string)>();
    string _windowMessage;

    public GamesView()
    {
        InitializeComponent();
        StatusSpin.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        Packs.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        Hub.GameLink.Changed += () => Dispatcher.BeginInvoke(() => Refresh());
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            // Coming back to Games always starts on the list of games (search and filter are kept).
            if (IsVisible) { _selected = null; _timer.Start(); Refresh(true); Scroller.ScrollToTop(); }
            else _timer.Stop();
        };

        // Rounded corners for the hero's blurred backdrop and the cover picture.
        HeroGrid.SizeChanged += (_, e) =>
        {
            HeroGrid.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
            // The blurred cover overhangs the card (Margin -60) so its soft edges are cut off; sized here so it never sizes the card.
            HeroBackdrop.Width = e.NewSize.Width + 120;
            HeroBackdrop.Height = e.NewSize.Height + 120;
        };
        // Full width up to 1320 px, left-aligned like the other pages (and so short pages don't shrink to their text).
        Scroller.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) SizePage(); };
        Scroller.SizeChanged += (_, _) => SizePage();
        CoverClip.SizeChanged += (_, e) => CoverClip.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
        DetailPanel.SizeChanged += (_, e) => { if (e.WidthChanged) LayoutSetup(); };

        Refresh(true); // opens on the list of games; nothing is picked until one is clicked
    }

    Brush Brush(string key) => (Brush)FindResource(key);

    void SizePage()
    {
        var w = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        if (w > 0) PageBody.Width = Math.Max(200, Math.Min(1320, w - PageBody.Margin.Left - PageBody.Margin.Right));
    }

    static string TierOf(GamePack p) => GameCatalog.TierOf(p);

    // Packs with a folder and mods/a server: the only ones with a status to look up (the game folder, installed files).
    static bool Installable(GamePack p) => !p.IsCatalogOnly && TierOf(p) == GameCatalog.Ready;

    static ConsoleTarget ConsoleOf(GamePack p)
    {
        if (p == null || TierOf(p) != GameCatalog.Console) return null;
        try { return Hub.Console(p.Id); }
        catch (Exception e) { Log.Write($"{p.Name}: {e.Message}"); return null; }
    }

    // ---- Refresh: re-render only when something visible changed (the timer runs every 2 s) ----

    void Refresh(bool force = false)
    {
        if (_selected != null) _selected = Packs.Find(_selected.Id); // packs reloaded (null if it's gone: back to the list)
        var statuses = new Dictionary<string, PackStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Packs.Packs)
            statuses[p.Id] = Installable(p) ? SafeStatus(p) : new PackStatus();
        _status = _selected != null && statuses.TryGetValue(_selected.Id, out var st) ? st : _selected != null ? new PackStatus() : null;

        var sig = string.Join("|", statuses.Select(kv => kv.Key + Signature(kv.Value))) + "|" + _selected?.Id + "|" + LinkSignature()
                  + "|" + ConsoleSignature() + "|" + Hub.Profiles.List().Count + "|" + (_busyPack != null) + "|" + (_manual != null) + "|" + _message
                  + "|" + Packs.Packs.Count;
        if (!force && sig == _shownSignature) return;
        _shownSignature = sig;

        RenderCards(statuses);
        RenderDetail();
    }

    PackStatus SafeStatus(GamePack p)
    {
        try { return Packs.GetStatus(p); }
        catch (Exception e) { Log.Write($"{p.Name}: {e.Message}"); return new PackStatus(); }
    }

    static string Signature(PackStatus s) =>
        s.GameFolder + "," + s.FoundBy + "," + string.Join("+", s.Running) + "," + s.Installed?.Complete + s.Installed?.Files.Count + ","
        + string.Join(";", s.Components.Select(c => c.InstalledVersion + "/" + c.OnDisk + "/" + c.DiskVersion + "/" + c.LatestVersion));

    List<IGameTarget> Connected(GamePack p) =>
        Hub.GameLink.Targets.Where(t => string.Equals(t.Game, p.Id, StringComparison.OrdinalIgnoreCase) && t.Connected).ToList();

    string LinkSignature() => string.Join(",", Hub.GameLink.Targets.Where(t => t.Connected).Select(t => t.Id + t.Status));

    string ConsoleSignature() => string.Join(",", Packs.Packs.Where(p => TierOf(p) == GameCatalog.Console)
        .Select(p => ConsoleOf(p) is { } t ? p.Id + (t.Connected ? "+" : "-") + t.Commands?.Count : ""));

    async void CheckLatest(bool force)
    {
        var p = _selected;
        if (p == null || !Installable(p)) return;
        try { await Packs.CheckLatestAsync(p, force); }
        catch (Exception e) { Log.Write($"{p.Name}: checking for updates failed: {e.Message}"); }
        if (force && p == _selected && _busyPack == null)
        {
            var st = SafeStatus(p);
            _message = st.Components.Any(c => c.UpdateAvailable) ? "There's a newer version: press Update." : "Checked: nothing new.";
            _messageIsError = false;
        }
        Refresh(true);
    }

    // =====================================================================================
    // The library
    // =====================================================================================

    void RenderCards(Dictionary<string, PackStatus> statuses)
    {
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Packs.Packs)
        {
            if (!alive.Add(p.Id)) continue; // the same id twice: the first one wins
            if (!_tiles.TryGetValue(p.Id, out var tile) || tile.Pack != p)
            {
                tile = new GameTile(p);
                tile.Click += (_, _) => Select(p);
                _tiles[p.Id] = tile;
            }
            var (tag, brush) = CardTag(p, statuses.TryGetValue(p.Id, out var st) ? st : new PackStatus());
            tile.SetStatus(tag, brush);
        }
        foreach (var gone in _tiles.Keys.Where(k => !alive.Contains(k)).ToList()) _tiles.Remove(gone);
        ApplyFilter();
    }

    // Installed = the game's mods are in its folder, whether GiftDeck put them there or you did
    // (for a server pack: the server is set up). Connected = its mod or console answers. Nothing is changed by showing this.
    (string, string) CardTag(GamePack p, PackStatus st)
    {
        if (_busyPack == p) return ("Working…", "AccentBrush");
        switch (TierOf(p))
        {
            case GameCatalog.Console:
                return ConsoleOf(p)?.Connected == true || Connected(p).Count > 0 ? ("Connected", "SuccessBrush") : (null, null);
            case GameCatalog.Keys:
                return Connected(p).Count > 0 ? ("Connected", "SuccessBrush") : (null, null);
        }
        if (p.Server != null)
        {
            if (Hub.GameLink.Find(p.Server.Target)?.Connected == true) return ("Running", "SuccessBrush");
            return Hub.Minecraft?.Server.IsInstalled == true ? ("Installed", "SuccessBrush") : (null, null);
        }
        if (Connected(p).Count > 0) return ("Connected", "SuccessBrush");
        if (st.AnyInstalledByUs && st.UpdateAvailable) return ("Update", "AccentBrush");
        if ((st.AnyInstalledByUs && st.Installed.Complete) || st.Components.Any(c => c.OnDisk)) return ("Installed", "SuccessBrush");
        return (null, null);
    }

    // Search + tier chip: which tiles show, in which order (ready first, then console, then keys; by name within each).
    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var all = _tiles.Values.ToList();
        var matching = all.Where(t => GameCatalog.Matches(t.Pack, q)).ToList();

        CountAll.Text = matching.Count.ToString();
        CountReady.Text = matching.Count(t => t.Tier == GameCatalog.Ready).ToString();
        CountConsole.Text = matching.Count(t => t.Tier == GameCatalog.Console).ToString();
        CountKeys.Text = matching.Count(t => t.Tier == GameCatalog.Keys).ToString();

        var shown = matching.Where(t => _tierFilter.Length == 0 || t.Tier == _tierFilter)
                            .OrderBy(t => GameCatalog.TierRank(t.Tier)).ThenBy(t => t.Pack.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (!CardsPanel.Children.Cast<UIElement>().SequenceEqual(shown))
        {
            CardsPanel.Children.Clear();
            foreach (var t in shown) CardsPanel.Children.Add(t);
        }

        bool filtered = q.Length > 0 || _tierFilter.Length > 0;
        ResultText.Text = all.Count == 0 ? "" : filtered ? $"{shown.Count} of {all.Count} games" : all.Count == 1 ? "1 game" : $"{all.Count} games";

        bool listShown = _selected == null;
        EmptyPanel.Visibility = listShown && shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown.Count > 0) return;
        if (all.Count == 0)
        {
            EmptyTitle.Text = "No games here yet";
            EmptyText.Text = "Game packs live in the Packs folder next to GiftDeck.";
            EmptyButton.Visibility = Visibility.Collapsed;
        }
        else if (q.Length > 0)
        {
            EmptyTitle.Text = $"No games match “{q}”" + (_tierFilter.Length > 0 ? " in " + GameCatalog.TierLabel(_tierFilter) : "");
            EmptyText.Text = matching.Count > 0
                ? $"{matching.Count} {(matching.Count == 1 ? "game matches" : "games match")} in other groups. Try All."
                : "Check the spelling, or search for a genre such as “survival” or “racing”.";
            EmptyButton.Content = matching.Count > 0 ? "Show all matches" : "Clear search";
            EmptyButton.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyTitle.Text = $"No {GameCatalog.TierLabel(_tierFilter).ToLowerInvariant()} games yet";
            EmptyText.Text = GameCatalog.TierExplain(_tierFilter);
            EmptyButton.Content = "Show all games";
            EmptyButton.Visibility = Visibility.Visible;
        }
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        bool any = SearchBox.Text.Length > 0;
        SearchHint.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (CardsPanel != null) ApplyFilter();
    }

    void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    void Chip_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton chip || !IsInitialized || CardsPanel == null) return;
        _tierFilter = chip.Tag as string ?? "";
        ApplyFilter();
    }

    // The empty state's button: "Show all matches" keeps the search, "Clear search" drops it.
    void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        var q = SearchBox.Text.Trim();
        bool elsewhere = q.Length > 0 && _tiles.Values.Any(t => GameCatalog.Matches(t.Pack, q));
        if (!elsewhere) SearchBox.Clear();
        ChipAll.IsChecked = true;
        (elsewhere ? (UIElement)CardsPanel.Children.OfType<GameTile>().FirstOrDefault() : SearchBox)?.Focus();
    }

    void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox || Keyboard.FocusedElement is PasswordBox;
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _selected == null)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _selected == null && Keyboard.FocusedElement == SearchBox && SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
        else if (_selected != null && ((e.Key == Key.Escape && !typing) || e.Key == Key.BrowserBack
                                       || (e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            Back_Click(this, null);
            e.Handled = true;
        }
        else if (_selected == null && Keyboard.FocusedElement == SearchBox && (e.Key == Key.Down || e.Key == Key.Enter))
        {
            // From the search box straight to the first result.
            if (CardsPanel.Children.OfType<GameTile>().FirstOrDefault() is { } first) { first.Focus(); e.Handled = true; }
        }
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        var from = _selected;
        _selected = null;
        if (_busyPack == null) _message = null;
        Refresh(true);
        Scroller.ScrollToTop();
        // Keyboard users land back on the game they came from.
        if (from != null && _tiles.TryGetValue(from.Id, out var tile) && CardsPanel.Children.Contains(tile))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => tile.Focus());
    }

    void Select(GamePack p)
    {
        if (p == _selected) return;
        _selected = p;
        if (_busyPack == null) { _message = null; }
        _windowMessage = null;
        KeysStatus.Visibility = Visibility.Collapsed;
        Refresh(true);
        Scroller.ScrollToTop();
        CheckLatest(false);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BackButton.Focus());
    }

    // =====================================================================================
    // A game's page
    // =====================================================================================

    void RenderDetail()
    {
        var p = _selected;
        DetailPanel.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        // The list and a game's page are two views: the list alone, or the game with a way back.
        ListHeader.Visibility = CardsPanel.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        if (p != null) EmptyPanel.Visibility = Visibility.Collapsed;
        BackButton.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) { _heroFor = _guideFor = _keysFor = _commandsSig = null; return; }
        var st = _status ?? new PackStatus();
        var tier = TierOf(p);

        RenderHero(p, st, tier);

        bool ready = tier == GameCatalog.Ready && !p.IsCatalogOnly;
        ReadyPanel.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready)
        {
            bool server = p.Server != null;
            GameCard.Visibility = ModsCard.Visibility = server ? Visibility.Collapsed : Visibility.Visible;
            ServerCard.Visibility = server ? Visibility.Visible : Visibility.Collapsed;
            if (server) RenderServer(p);
            else
            {
                RenderGame(p, st);
                RenderMods(p, st);
            }
            RenderRequirements(p, st);
        }

        RenderGuide(p, tier);
        RenderConsole(p, tier);
        RenderWindowCard(p, tier);
        RenderKeys(p, tier);
        LayoutSetup();

        // Presets: always shown for ready games (with a pointer to Events when there are none), otherwise only if the pack has some.
        var presets = p.IsCatalogOnly ? new List<PackPreset>() : SafePresets(p);
        PresetsCard.Visibility = ready || presets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (PresetsCard.Visibility == Visibility.Visible) RenderPresets(p, presets);
    }

    List<PackPreset> SafePresets(GamePack p)
    {
        try { return Packs.Presets(p); }
        catch (Exception e) { Log.Write($"{p.Name}: {e.Message}"); return new List<PackPreset>(); }
    }

    // ---- Hero ----

    void RenderHero(GamePack p, PackStatus st, string tier)
    {
        if (_heroFor != p.Id + "|" + p.GetHashCode())
        {
            _heroFor = p.Id + "|" + p.GetHashCode();
            CoverFallback.Content = CoverCache.Fallback(p, 60);
            CoverCache.Apply(CoverImage, p, () => _selected?.Id == p.Id, HeroBackdrop);
            TitleText.Text = p.Name;
            ShortText.Text = p.Short;
            ShortText.Visibility = string.IsNullOrWhiteSpace(p.Short) ? Visibility.Collapsed : Visibility.Visible;
            DescText.Text = string.IsNullOrWhiteSpace(p.Description) ? GameCatalog.TierExplain(tier) : p.Description;
            WarningText.Text = "⚠  " + p.Warning;
            WarningBorder.Visibility = string.IsNullOrWhiteSpace(p.Warning) ? Visibility.Collapsed : Visibility.Visible;

            var link = tier != GameCatalog.Ready ? GameCatalog.StoreLink(p) : null;
            GetGameButton.Visibility = link != null ? Visibility.Visible : Visibility.Collapsed;
            GetGameButton.Content = (p.SteamAppId > 0 ? "Get it on Steam" : "Get the game") + "  ↗";
            GetGameButton.ToolTip = link;
            HeroButtons.Visibility = GetGameButton.Visibility;
        }

        // Badges change with the connection / install state.
        HeroBadges.Children.Clear();
        HeroBadges.Children.Add(Spaced(GameTile.TierBadge(tier, compact: true)));
        if (!string.IsNullOrWhiteSpace(p.Genre)) HeroBadges.Children.Add(Spaced(Pill(p.Genre, "Panel2Brush", "MutedBrush")));
        var (tag, brush) = CardTag(p, st);
        if (tag != null) HeroBadges.Children.Add(Spaced(Pill(tag, brush, null)));

        var linked = tier == GameCatalog.Console ? new List<IGameTarget>() : Connected(p); // console: the Connection card says it
        LinkBorder.Visibility = linked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LinkText.Text = "Connected in game ✓  " + string.Join(", ", linked.Select(t => t.Name));
        LinkDetail.Text = string.Join("\n", linked.Where(t => !string.IsNullOrWhiteSpace(t.Status)).Select(t => t.Name + ": " + t.Status));
        LinkDetail.Visibility = LinkDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static FrameworkElement Spaced(FrameworkElement e) { e.Margin = new Thickness(0, 0, 8, 4); return e; }

    Border Pill(string text, string background, string foreground)
    {
        bool solid = foreground == null;
        return new Border
        {
            CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 10, 4),
            Background = Brush(background), BorderBrush = solid ? Brush(background) : Brush("LineBrush"), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap,
                Foreground = solid ? (background == "SuccessBrush" ? new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x17)) : Brushes.White) : Brush(foreground),
            },
        };
    }

    void GetGame_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) GameCatalog.OpenUrl(GameCatalog.StoreLink(_selected));
    }

    // ---- Guide ("How to set it up") ----

    void RenderGuide(GamePack p, string tier)
    {
        bool show = p.Guide.Count > 0;
        GuideCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _guideFor == p.Id) return;
        _guideFor = p.Id;
        GuideIntro.Text = tier == GameCatalog.Console
            ? "Do this once. After that, GiftDeck reaches the server by itself whenever it's running."
            : tier == GameCatalog.Keys ? "Do this once before you go LIVE." : "Do this once.";
        GuideRows.Children.Clear();
        for (int i = 0; i < p.Guide.Count; i++) GuideRows.Children.Add(GuideStep(i + 1, p.Guide[i], i == p.Guide.Count - 1));
    }

    UIElement GuideStep(int n, PackGuideStep s, bool last)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The number, with a line down to the next step.
        var rail = new Grid();
        if (!last)
            rail.Children.Add(new Border { Width = 2, Background = Brush("LineBrush"), Margin = new Thickness(0, 30, 0, 2), HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform(13, 0) });
        rail.Children.Add(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush("AccentDimBrush"), BorderBrush = Brush("AccentBrush"), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = n.ToString(), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        });
        grid.Children.Add(rail);

        var text = new StackPanel { Margin = new Thickness(0, 4, 0, last ? 0 : 20) };
        if (!string.IsNullOrWhiteSpace(s.Title)) text.Children.Add(new TextBlock { Text = s.Title, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
        if (!string.IsNullOrWhiteSpace(s.Text))
            text.Children.Add(new TextBlock { Text = s.Text, Style = (Style)FindResource("Muted"), LineHeight = 19, Margin = new Thickness(0, string.IsNullOrWhiteSpace(s.Title) ? 0 : 3, 0, 0) });
        if (!string.IsNullOrWhiteSpace(s.Link))
        {
            var url = s.Link.Trim();
            var label = string.IsNullOrWhiteSpace(s.LinkText) ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? "Open " + u.Host.Replace("www.", "") : "Open the page") : s.LinkText;
            var b = new Button { Content = label + "  ↗", Style = (Style)FindResource("Small"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0), ToolTip = url };
            b.Click += (_, _) => GameCatalog.OpenUrl(url);
            text.Children.Add(b);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    // Guide on the left, connection / key set-up on the right; stacked when the page is narrow.
    void LayoutSetup()
    {
        bool side = ConsoleCard.Visibility == Visibility.Visible || WindowCard.Visibility == Visibility.Visible;
        bool guide = GuideCard.Visibility == Visibility.Visible;
        SideColumn.Visibility = side ? Visibility.Visible : Visibility.Collapsed;
        SetupGrid.Visibility = side || guide ? Visibility.Visible : Visibility.Collapsed;
        bool wide = side && guide && DetailPanel.ActualWidth >= 960;
        SetupSideColumn.Width = wide ? new GridLength(DetailPanel.ActualWidth >= 1180 ? 440 : 400) : new GridLength(0);
        Grid.SetColumn(SideColumn, wide ? 1 : 0);
        Grid.SetRow(SideColumn, wide || !guide ? 0 : 1);
        Grid.SetColumnSpan(SideColumn, wide ? 1 : 2);
        Grid.SetColumnSpan(GuideCard, wide ? 1 : 2);
        SideColumn.Margin = new Thickness(wide ? 14 : 0, 0, 0, 0);
    }

    // ---- Server console ----

    void RenderConsole(GamePack p, string tier)
    {
        bool console = tier == GameCatalog.Console;
        ConsoleCard.Visibility = CommandsCard.Visibility = console ? Visibility.Visible : Visibility.Collapsed;
        if (!console) { ConsoleHost.Content = null; return; }

        var target = ConsoleOf(p);
        if (target == null)
        {
            ConsoleHost.Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Connection", Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 6) },
                    new TextBlock { Text = "This version of GiftDeck can't reach this game's console yet. Update GiftDeck to connect to it.", Style = (Style)FindResource("Muted") },
                },
            };
        }
        else
        {
            if (!_consolePanels.TryGetValue(p.Id, out var panel) || panel.Target != target)
            {
                panel = new GameConsolePanel(target);
                panel.StatusChanged += () => Refresh();
                _consolePanels[p.Id] = panel;
            }
            if (ConsoleHost.Content != panel) ConsoleHost.Content = panel;
        }
        RenderCommands(p, target);
    }

    void RenderCommands(GamePack p, ConsoleTarget target)
    {
        var commands = target?.Commands ?? (IReadOnlyList<GameCommandInfo>)Array.Empty<GameCommandInfo>();
        var sig = p.Id + "|" + (target != null) + "|" + string.Join(",", commands.Select(c => c.Id));
        if (sig == _commandsSig) return;
        _commandsSig = sig;
        CommandRows.Children.Clear();

        bool needsPlayer = !string.IsNullOrWhiteSpace(p.Console?.PlayerLabel);
        CommandsIntro.Text = "Use these in an event: Run a game command. Test runs one on your server right now"
                             + (needsPlayer ? " (on the player named under Connection)." : ".");
        if (target == null || commands.Count == 0)
        {
            CommandRows.Children.Add(Row("–", "MutedBrush", target == null ? "No commands yet" : "This game has no commands listed",
                target == null ? "Once GiftDeck can reach this game's console, its commands show here." : "You can still send any command by hand under Connection."));
            return;
        }

        var groups = commands.GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "Other" : c.Category.Trim()).ToList();
        foreach (var g in groups)
        {
            CommandRows.Children.Add(new TextBlock
            {
                Text = g.Key.ToUpperInvariant(), FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brush("MutedBrush"),
                Margin = new Thickness(2, CommandRows.Children.Count == 0 ? 0 : 14, 0, 6),
            });
            var list = new StackPanel();
            var items = g.ToList();
            for (int i = 0; i < items.Count; i++) list.Children.Add(CommandRow(target, items[i], i < items.Count - 1));
            CommandRows.Children.Add(new Border { Style = (Style)FindResource("SubCard"), Padding = new Thickness(14, 4, 10, 4), Child = list });
        }
    }

    UIElement CommandRow(ConsoleTarget target, GameCommandInfo c, bool divider)
    {
        var grid = new Grid { Margin = new Thickness(0), MinHeight = 48 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
        text.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrWhiteSpace(c.Description))
            text.Children.Add(new TextBlock { Text = c.Description, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(text);

        var result = new TextBlock { FontSize = 12, MaxWidth = 300, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        if (_commandResults.TryGetValue(c.Id, out var last)) ShowResult(result, last.ok, last.text);
        else result.Visibility = Visibility.Collapsed;
        Grid.SetColumn(result, 1);
        grid.Children.Add(result);

        var test = new Button { Content = "Test", Style = (Style)FindResource("Small"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Run it on your server now" };
        System.Windows.Automation.AutomationProperties.SetName(test, "Test " + c.Name);
        test.Click += (_, _) => TestCommand(target, c, result, test);
        Grid.SetColumn(test, 2);
        grid.Children.Add(test);

        if (!divider) return grid;
        return new Border { BorderBrush = Brush("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
    }

    void ShowResult(TextBlock t, bool? ok, string text)
    {
        t.Text = text;
        t.Visibility = Visibility.Visible;
        t.Foreground = Brush(ok == true ? "SuccessBrush" : ok == false ? "DangerBrush" : "MutedBrush");
    }

    async void TestCommand(ConsoleTarget target, GameCommandInfo c, TextBlock result, Button button)
    {
        button.IsEnabled = false;
        ShowResult(result, null, "Running…");
        bool ok;
        string text;
        try
        {
            var r = await target.RunAsync(c.Id, DefaultArgs(c), null);
            ok = r?.Ok == true;
            text = string.IsNullOrWhiteSpace(r?.Message) ? (ok ? "Done ✓" : "It didn't work.") : (ok ? "✓ " : "") + r.Message.Trim();
        }
        catch (Exception ex) { ok = false; text = ex.Message; }
        finally { button.IsEnabled = true; }
        if (text.Length > 160) text = text[..157] + "…";
        _commandResults[c.Id] = (ok, text);
        ShowResult(result, ok, text);
    }

    // A command's arguments at their defaults ("Test" doesn't ask for any).
    static JsonObject DefaultArgs(GameCommandInfo c)
    {
        var args = new JsonObject();
        foreach (var a in c.Args ?? new List<GameCommandArg>())
        {
            if (string.IsNullOrEmpty(a.Name) || a.Default == null) continue;
            if (a.Type == "number" && double.TryParse(a.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                args[a.Name] = d == Math.Floor(d) && Math.Abs(d) < long.MaxValue ? JsonValue.Create((long)d) : JsonValue.Create(d);
            else args[a.Name] = a.Default;
        }
        return args;
    }

    // ---- Key presses ----

    void RenderWindowCard(GamePack p, string tier)
    {
        bool show = tier == GameCatalog.Keys && !string.IsNullOrWhiteSpace(p.WindowTitle);
        WindowCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var title = p.WindowTitle.Trim();
        bool using_ = Hub.Settings.FocusWindowBeforeKeys && string.Equals((Hub.Settings.FocusWindowTitle ?? "").Trim(), title, StringComparison.OrdinalIgnoreCase);
        WindowIntro.Text = $"Key presses go to whichever window is in front. GiftDeck can bring {p.Name} to the front first, so the keys land in the game even while you're clicking around in GiftDeck or OBS.";
        FocusWindowButton.Content = using_ ? "Using this game's window ✓" : "Use this game's window for key presses";
        FocusWindowButton.Style = (Style)FindResource(using_ ? typeof(Button) : "Primary");
        FocusWindowButton.IsEnabled = !using_;

        string line;
        bool success = false;
        if (_windowMessage != null) { line = _windowMessage; success = true; }
        else if (using_) line = $"Key presses go to the window with “{title}” in its title. You can change this in Settings.";
        else if (Hub.Settings.FocusWindowBeforeKeys) line = $"Right now key presses go to “{Hub.Settings.FocusWindowTitle}”.";
        else line = "Right now key presses go to whichever window is in front.";
        FocusWindowStatus.Text = line;
        FocusWindowStatus.Foreground = Brush(success ? "SuccessBrush" : "MutedBrush");
        FocusWindowStatus.Visibility = Visibility.Visible;
    }

    void FocusWindow_Click(object sender, RoutedEventArgs e)
    {
        var p = _selected;
        if (p == null || string.IsNullOrWhiteSpace(p.WindowTitle)) return;
        Hub.Settings.FocusWindowBeforeKeys = true;
        Hub.Settings.FocusWindowTitle = p.WindowTitle.Trim();
        try { Hub.SaveSettings(); }
        catch (Exception ex) { _windowMessage = null; FocusWindowStatus.Text = "Couldn't save: " + ex.Message; FocusWindowStatus.Foreground = Brush("DangerBrush"); return; }
        _windowMessage = $"Done ✓  Before pressing keys, GiftDeck now brings the window with “{p.WindowTitle.Trim()}” in its title to the front.";
        RenderWindowCard(p, TierOf(p));
    }

    void RenderKeys(GamePack p, string tier)
    {
        bool show = tier == GameCatalog.Keys;
        KeysCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show || _keysFor == p.Id) return;
        _keysFor = p.Id;
        KeyRows.Children.Clear();
        if (p.Keys.Count == 0)
        {
            KeyRows.Children.Add(Row("–", "MutedBrush", "No key ideas for this game yet",
                "Make your own on the Events page: New event, then Press a key.",
                ("Open Events", () => (Window.GetWindow(this) as MainWindow)?.Navigate("events"), false)));
            return;
        }
        KeyRows.Children.Add(KeyRow(null, null, header: true));
        for (int i = 0; i < p.Keys.Count; i++) KeyRows.Children.Add(KeyRow(p, p.Keys[i], header: false, divider: i < p.Keys.Count - 1));
    }

    UIElement KeyRow(GamePack p, PackKeyIdea k, bool header, bool divider = false)
    {
        var grid = new Grid { MinHeight = header ? 0 : 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "KeyCaps", MinWidth = 150 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "KeyButton" });

        UIElement Cell(UIElement e, int col) { Grid.SetColumn(e, col); grid.Children.Add(e); return e; }

        if (header)
        {
            string[] heads = { "WHAT IT DOES", "KEYS", "GIFT IDEA" };
            for (int i = 0; i < heads.Length; i++)
                Cell(new TextBlock { Text = heads[i], FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("MutedBrush"), Margin = new Thickness(i == 0 ? 12 : 0, 0, 12, 6) }, i);
            return new Border { BorderBrush = Brush("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = grid };
        }

        Cell(new TextBlock { Text = k.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 10, 12, 10) }, 0);
        Cell(Keycaps(k.Keys, k.HoldMs), 1);
        Cell(new TextBlock { Text = k.Idea, Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) }, 2);
        var add = new Button { Content = "+  Add as event", Style = (Style)FindResource("Small"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Opens the event editor with this key press filled in" };
        System.Windows.Automation.AutomationProperties.SetName(add, $"Add {k.Name} as an event");
        add.Click += (_, _) => AddKeyEvent(p, k);
        Cell(add, 3);

        var row = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Child = grid,
            BorderBrush = Brush("LineBrush"), BorderThickness = new Thickness(0, 0, 0, divider ? 1 : 0),
        };
        row.MouseEnter += (_, _) => row.Background = Brush("Panel2Brush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    // "Ctrl+Shift+C" as three keycaps; "hold 2 s" underneath when the key is held down.
    UIElement Keycaps(string keys, int holdMs)
    {
        var caps = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var parts = (keys ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) caps.Children.Add(new TextBlock { Text = "+", Foreground = Brush("MutedBrush"), FontSize = 12, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            caps.Children.Add(new Border
            {
                MinWidth = 26, Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(5),
                Background = Brush("Panel3Brush"), BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x4B, 0x5E)),
                BorderThickness = new Thickness(1, 1, 1, 3), Margin = new Thickness(0, 2, 0, 2),
                Child = new TextBlock { Text = parts[i], FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, HorizontalAlignment = HorizontalAlignment.Center },
            });
        }
        if (parts.Length == 0) caps.Children.Add(new TextBlock { Text = "(no key)", Style = (Style)FindResource("Muted") });
        var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        box.Children.Add(caps);
        if (holdMs > 0)
            box.Children.Add(new TextBlock
            {
                Text = "hold " + (holdMs >= 1000 ? (holdMs / 1000.0).ToString("0.#", CultureInfo.CurrentCulture) + " s" : holdMs + " ms"),
                FontSize = 11.5, Foreground = Brush("MutedBrush"), Margin = new Thickness(1, 3, 0, 0),
            });
        return box;
    }

    // Opens the event editor with a new event that presses this key; the streamer picks the trigger and saves.
    void AddKeyEvent(GamePack p, PackKeyIdea k)
    {
        var rule = new Rule { Name = $"{p.Name}: {k.Name}" };
        rule.Actions.Add(new RuleAction { Type = ActionType.KeyPress, Text = k.Keys, Number = Math.Max(0, k.HoldMs) });
        var win = new RuleEditorWindow(rule, isNew: true) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true) return;
        Hub.Rules.Rules.Add(win.Result);
        Hub.Rules.Save();
        KeysStatus.Text = $"Added “{win.Result.Name}” to your events ✓";
        KeysStatus.Foreground = Brush("SuccessBrush");
        KeysStatus.Visibility = Visibility.Visible;
    }

    void OpenEvents_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("events");

    // ---- Ready to go: server ----

    // The server's own panel (start/stop, version, Java, EULA, console) is kept alive between refreshes.
    void RenderServer(GamePack p)
    {
        JoinText.Text = string.IsNullOrWhiteSpace(p.Server.Join) ? "" : "How to join: " + p.Server.Join;
        JoinText.Visibility = JoinText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (p.Server.Panel == "MinecraftServerPanel")
        {
            if (ServerHost.Content is not MinecraftServerPanel)
            {
                var panel = new MinecraftServerPanel();
                panel.StatusChanged += () => Refresh();
                ServerHost.Content = panel;
            }
        }
        else if (ServerHost.Content is not TextBlock)
            ServerHost.Content = new Border
            {
                Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 14),
                Child = new TextBlock { Text = "This version of GiftDeck can't run this kind of server yet.", Style = (Style)FindResource("Muted") },
            };
    }

    // ---- Ready to go: game folder, requirements, mods ----

    void RenderGame(GamePack p, PackStatus st)
    {
        GameRows.Children.Clear();
        var buttons = new List<(string, Action, bool)>();
        if (st.GameFound)
        {
            buttons.Add(("Open folder", () => OpenFolder(st.GameFolder), false));
            buttons.Add(("Choose folder…", () => ChooseFolder(p), false));
            if (st.FoundBy == "you") buttons.Add(("Find it by itself", () => Packs.ForgetGameFolder(p), false));
            var how = st.FoundBy == "you" ? "You chose this folder." : $"Found through {st.FoundBy}.";
            GameRows.Children.Add(Row("✓", "SuccessBrush", $"Found {p.Name}", st.GameFolder + "\n" + how, buttons.ToArray()));
        }
        else
        {
            buttons.Add(("Choose folder…", () => ChooseFolder(p), true));
            GameRows.Children.Add(Row("○", "WarnBrush", $"GiftDeck can't find {p.Name}",
                $"It looked in Steam, Epic Games and the game's launcher. If it's installed, press Choose folder… and pick the folder with {p.Exe} in it.", buttons.ToArray()));
        }
    }

    void RenderRequirements(GamePack p, PackStatus st)
    {
        ReqRows.Children.Clear();
        if (p.Requirements.Count == 0)
        {
            ReqRows.Children.Add(new TextBlock { Text = "Nothing special: just press Install.", Style = (Style)FindResource("Muted") });
            return;
        }
        foreach (var r in p.Requirements)
        {
            switch ((r.Check ?? "").ToLowerInvariant())
            {
                case "closed":
                    bool closed = st.Running.Count == 0;
                    ReqRows.Children.Add(Row(closed ? "✓" : "○", closed ? "SuccessBrush" : "WarnBrush", r.Text,
                        closed ? (string.IsNullOrEmpty(r.Detail) ? "It's closed." : r.Detail) : $"It's running ({string.Join(", ", st.Running)}). Close it to install or uninstall."));
                    break;
                case "found":
                    ReqRows.Children.Add(Row(st.GameFound ? "✓" : "○", st.GameFound ? "SuccessBrush" : "WarnBrush", r.Text, r.Detail));
                    break;
                default:
                    ReqRows.Children.Add(Row("•", "AccentBrush", r.Text, r.Detail));
                    break;
            }
        }
    }

    void RenderMods(GamePack p, PackStatus st)
    {
        ComponentRows.Children.Clear();
        bool busyHere = _busyPack == p;
        bool installed = st.AnyInstalledByUs;
        ModsIntro.Text = installed
            ? $"Installed in {st.Installed.GameFolder}. Uninstall removes them and puts back any file they replaced."
            : "GiftDeck downloads each of these from its maker, copies them into the game folder and backs up any file it replaces.";

        foreach (var c in st.Components)
        {
            string mark, brush, line;
            if (c.UpdateAvailable) { mark = "↑"; brush = "AccentBrush"; line = $"Installed {c.InstalledVersion}. Newer: {c.LatestVersion}."; }
            else if (c.InstalledVersion != null) { mark = "✓"; brush = "SuccessBrush"; line = $"Installed {c.InstalledVersion}."; }
            else if (c.OnDisk) { mark = "○"; brush = "MutedBrush"; line = $"Already in the game folder{(c.DiskVersion != null ? " (" + c.DiskVersion + ")" : "")}, not put there by GiftDeck. Installing replaces it and keeps a backup."; }
            else { mark = "○"; brush = "MutedBrush"; line = "Not installed" + (c.LatestVersion != null ? $". Newest: {c.LatestVersion}." : "."); }
            if (c.InstalledVersion == null && c.OnDisk && c.LatestVersion != null) line += $" Newest: {c.LatestVersion}.";
            var detail = line + (string.IsNullOrWhiteSpace(c.Component.License) ? "" : "\n" + c.Component.License);
            ComponentRows.Children.Add(Row(mark, brush, c.Component.Name, detail));
        }

        // Buttons
        bool canAct = _busyPack == null && st.GameFound && st.Running.Count == 0;
        bool update = installed && st.UpdateAvailable;
        InstallButton.Content = !installed ? "Install" : !st.Installed.Complete ? "Finish installing" : update ? "Update" : "Reinstall";
        InstallButton.Style = (Style)FindResource(!installed || update || !st.Installed.Complete ? "Primary" : typeof(Button));
        InstallButton.IsEnabled = canAct;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.IsEnabled = _busyPack == null && st.Running.Count == 0;
        CheckButton.IsEnabled = _busyPack == null;

        // Download it yourself
        bool manual = _manual != null && _manualPack == p && !busyHere;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        if (manual)
        {
            ManualTitle.Text = $"Download {_manual.Component.Name} yourself";
            ManualText.Text = $"{_manual.Component.Name}'s site doesn't let GiftDeck download it directly. Open its page in your browser and download it there."
                              + (string.IsNullOrWhiteSpace(_manual.Instructions) ? "" : "\n" + _manual.Instructions)
                              + "\nGiftDeck then finishes the install with that file.";
            ManualOpenButton.Content = "Open " + (Uri.TryCreate(_manual.PageUrl, UriKind.Absolute, out var u) ? u.Host : "its page");
            ManualOpenButton.Visibility = string.IsNullOrEmpty(_manual.PageUrl) ? Visibility.Collapsed : Visibility.Visible;
        }

        RenderStatus();
    }

    void RenderStatus()
    {
        bool busyHere = _busyPack != null && _busyPack == _selected;
        string text = busyHere ? _busyText : _busyPack != null ? $"Working on {_busyPack.Name}…" : _message;
        StatusText.Text = text ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Foreground = Brush(!busyHere && _messageIsError ? "DangerBrush" : "TextBrush");
        StatusSpinner.Visibility = busyHere ? Visibility.Visible : Visibility.Collapsed;
        Bar.Visibility = busyHere ? Visibility.Visible : Visibility.Collapsed;
        Bar.IsIndeterminate = busyHere && _busyPart < 0;
        if (_busyPart >= 0) Bar.Value = _busyPart;
    }

    void RenderPresets(GamePack p, List<PackPreset> presets)
    {
        PresetRows.Children.Clear();
        if (presets.Count == 0)
        {
            PresetRows.Children.Add(Row("–", "MutedBrush", "No ready-made events for this game yet",
                "You can still set up your own on the Events page: once the mods are installed and the game is running, its commands are there to pick.",
                ("Open Events", () => (Window.GetWindow(this) as MainWindow)?.Navigate("events"), false)));
            return;
        }
        foreach (var preset in presets)
        {
            var mine = Packs.ImportedProfile(p, preset);
            bool active = mine != null && mine == Hub.Profiles.Active;
            var detail = active ? $"You're using it: it's your \"{mine}\" profile." : mine != null ? $"You have it as the \"{mine}\" profile." : "Adds a new profile with its events and gift board.";
            PresetRows.Children.Add(Row(active ? "✓" : "•", active ? "SuccessBrush" : "AccentBrush", preset.Name, detail,
                active ? Array.Empty<(string, Action, bool)>() : new[] { ("Use this preset", (Action)(() => UsePreset(p, preset)), true) }));
        }
    }

    // A checklist row like Stream Setup's: mark, title, detail, buttons on the right.
    UIElement Row(string mark, string markBrush, string title, string detail, params (string label, Action click, bool primary)[] buttons)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = mark, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush(markBrush), Margin = new Thickness(0, 1, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (buttons.Length > 0)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var (label, click, primary) in buttons)
            {
                var b = new Button { Content = label, Style = (Style)FindResource(primary ? "Primary" : "Ghost"), Margin = new Thickness(6, 0, 0, 0) };
                b.Click += (_, _) => Try(click);
                panel.Children.Add(b);
            }
            Grid.SetColumn(panel, 2);
            grid.Children.Add(panel);
        }
        return grid;
    }

    void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { _message = ex.Message; _messageIsError = true; Refresh(true); }
    }

    // ---- Actions (ready to go) ----

    void ChooseFolder(GamePack p)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Pick the folder {p.Name} is installed in (the one with {p.Exe})" };
        if (_status?.GameFolder != null) dlg.InitialDirectory = _status.GameFolder;
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        Packs.SetGameFolder(p, dlg.FolderName);
        _message = null;
        Refresh(true);
    }

    static void OpenFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true }); } catch { }
    }

    void Check_Click(object sender, RoutedEventArgs e)
    {
        _message = "Checking for updates…";
        _messageIsError = false;
        RenderStatus();
        CheckLatest(true);
    }

    void Install_Click(object sender, RoutedEventArgs e) => Install(_selected);

    async void Install(GamePack p)
    {
        if (p == null || _busyPack != null) return;
        _busyPack = p;
        _busyText = "Starting…";
        _busyPart = -1;
        _message = null;
        _manual = null;
        Refresh(true);
        try
        {
            _manualFiles.TryGetValue(p.Id, out var manual);
            await Packs.InstallAsync(p, new Progress<(double part, string text)>(x =>
            {
                _busyText = x.text;
                _busyPart = x.part;
                RenderStatus();
            }), manual);
            _manualFiles.Remove(p.Id);
            _message = "Installed ✓  Start the game, load Story Mode, and look for GiftDeck's green greeting.";
            if (Packs.Presets(p).Count > 0) _message += " Then pick a preset below.";
            _messageIsError = false;
        }
        catch (ManualDownloadNeeded m)
        {
            _manual = m;
            _manualPack = p;
            _message = m.Message;
            _messageIsError = false;
        }
        catch (Exception ex)
        {
            _message = ex.Message;
            _messageIsError = true;
            Log.Write($"{p.Name}: install failed: {ex.Message}");
        }
        finally
        {
            _busyPack = null;
            Refresh(true);
        }
    }

    void ManualOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_manual?.PageUrl is { Length: > 0 } url)
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void ManualPick_Click(object sender, RoutedEventArgs e)
    {
        if (_manual == null || _manualPack == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Pick the {_manual.Component.Name} file you downloaded",
            Filter = "Zip files|*.zip|All files|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!_manualFiles.TryGetValue(_manualPack.Id, out var files)) _manualFiles[_manualPack.Id] = files = new Dictionary<string, string>();
        files[_manual.Component.Id] = dlg.FileName;
        Install(_manualPack);
    }

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var p = _selected;
        if (p == null || _busyPack != null || _status?.Installed == null) return;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Remove {p.Name}'s mods from {_status.Installed.GameFolder}?\n\nGiftDeck deletes the files it put there and puts back any file they replaced. Your profiles and events stay.",
                "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busyPack = p;
        _busyText = "Removing the mods…";
        _busyPart = -1;
        _message = null;
        _manual = null;
        Refresh(true);
        try
        {
            await Packs.UninstallAsync(p, new Progress<(double part, string text)>(x => { _busyText = x.text; _busyPart = x.part; RenderStatus(); }));
            _message = "Uninstalled ✓  The game folder is back the way it was.";
            _messageIsError = false;
        }
        catch (Exception ex)
        {
            _message = ex.Message;
            _messageIsError = true;
            Log.Write($"{p.Name}: uninstall failed: {ex.Message}");
        }
        finally
        {
            _busyPack = null;
            Refresh(true);
        }
    }

    void UsePreset(GamePack p, PackPreset preset)
    {
        var owner = Window.GetWindow(this);
        var name = Packs.ImportedProfile(p, preset);
        if (name != null)
        {
            var answer = MessageBox.Show(owner,
                $"You already have this preset as the \"{name}\" profile.\n\nYes: switch to it (with any changes you made).\nNo: add a fresh copy as a new profile.",
                "GiftDeck", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No) name = null;
        }
        name ??= Packs.ImportPreset(p, preset);
        (owner as MainWindow)?.SwitchProfile(name);
        Refresh(true);
    }
}
