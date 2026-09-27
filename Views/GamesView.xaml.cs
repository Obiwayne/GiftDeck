using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The Games page: a card per supported game (Packs\<id>\pack.json); the selected game shows where it was found,
// what to do before installing, its mods with their versions, Install/Update/Uninstall, and its presets.
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
            // Coming back to Games always starts on the list of games.
            if (IsVisible) { _selected = null; _timer.Start(); Refresh(true); Scroller.ScrollToTop(); }
            else _timer.Stop();
        };
        Refresh(true); // opens on the list of games; nothing is picked until one is clicked
    }

    Brush Brush(string key) => (Brush)FindResource(key);

    // ---- Refresh: re-render only when something visible changed (the timer runs every 2 s) ----

    void Refresh(bool force = false)
    {
        if (_selected != null) _selected = Packs.Find(_selected.Id); // packs reloaded (null if it's gone: back to the list)
        var statuses = Packs.Packs.ToDictionary(p => p.Id, p => SafeStatus(p));
        _status = _selected != null ? statuses[_selected.Id] : null;

        var sig = string.Join("|", statuses.Select(kv => kv.Key + Signature(kv.Value))) + "|" + _selected?.Id + "|" + LinkSignature()
                  + "|" + Hub.Profiles.List().Count + "|" + (_busyPack != null) + "|" + (_manual != null) + "|" + _message;
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

    async void CheckLatest(bool force)
    {
        var p = _selected;
        if (p == null) return;
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

    // ---- Game cards ----

    void RenderCards(Dictionary<string, PackStatus> statuses)
    {
        CardsPanel.Children.Clear();
        EmptyText.Visibility = Packs.Packs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var p in Packs.Packs) CardsPanel.Children.Add(Card(p, statuses[p.Id]));
    }

    const double CardWidth = 300, CardHeight = 169; // every card the same 16:9 picture size

    // A game card is just its cover picture (the name is in the picture), with a tag in the top-right
    // corner once it's set up. Name and description show on hover.
    UIElement Card(GamePack p, PackStatus st)
    {
        var picture = new Border
        {
            Width = CardWidth, Height = CardHeight,
            CornerRadius = new CornerRadius(10),
            Background = Brush("Panel2Brush"),
            BorderBrush = Brush("LineBrush"), BorderThickness = new Thickness(1),
            Child = new Image { Source = LoadImage(p.CoverPath), Stretch = Stretch.UniformToFill },
        };
        picture.Child.Clip = new RectangleGeometry(new Rect(0, 0, CardWidth - 2, CardHeight - 2), 9, 9);
        var grid = new Grid { Width = CardWidth, Height = CardHeight };
        grid.Children.Add(picture);
        var (tag, tagBrush) = CardTag(p, st);
        if (tag != null)
            grid.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 10, 0), Padding = new Thickness(9, 3, 9, 4),
                CornerRadius = new CornerRadius(11), Background = Brush(tagBrush),
                Child = new TextBlock { Text = tag, Foreground = Brushes.White, FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            });

        // A real button round the card, so Tab + Enter (and screen readers) open a game too.
        var open = new Button
        {
            Content = grid, Padding = new Thickness(0), MinHeight = 0, Margin = new Thickness(0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(0),
            Template = CardButtonTemplate, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = p.Name + (p.Short.Length > 0 ? Environment.NewLine + p.Short : ""),
        };
        System.Windows.Automation.AutomationProperties.SetName(open, "Open " + p.Name + (tag != null ? " (" + tag + ")" : ""));
        open.Click += (_, _) => Select(p);
        return open;
    }

    // Installed = the game's mods are in its folder, whether GiftDeck put them there or you did
    // (for a server pack: the server is set up). Nothing is changed by showing this.
    (string, string) CardTag(GamePack p, PackStatus st)
    {
        if (_busyPack == p) return ("Working…", "AccentBrush");
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

    // Just the card itself: no button chrome around it; a light lift on hover and a focus outline for the keyboard.
    static readonly ControlTemplate CardButtonTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
        "<Border x:Name='Bd' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' CornerRadius='10' BorderThickness='2' BorderBrush='Transparent' Margin='0,0,14,14'>" +
        "<ContentPresenter/></Border>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Opacity' Value='0.88'/></Trigger>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='#7C5CFF'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");

    void Back_Click(object sender, RoutedEventArgs e)
    {
        _selected = null;
        if (_busyPack == null) _message = null;
        Refresh(true);
        Scroller.ScrollToTop();
    }

    void Select(GamePack p)
    {
        if (p == _selected) return;
        _selected = p;
        if (_busyPack == null) { _message = null; }
        Refresh(true);
        Scroller.ScrollToTop();
        CheckLatest(false);
    }

    static ImageSource LoadImage(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // don't keep the file open
            bmp.UriSource = new Uri(path);
            bmp.DecodePixelWidth = 560;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ---- Detail ----

    void RenderDetail()
    {
        var p = _selected;
        DetailPanel.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        // The list and a game's page are two views: the list alone, or the game with a way back.
        ListHeader.Visibility = CardsPanel.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null) return;
        var st = _status;

        CoverImage.Source = LoadImage(p.CoverPath);
        TitleText.Text = p.Name;
        DescText.Text = p.Description;
        WarningText.Text = "⚠  " + p.Warning;
        WarningBorder.Visibility = string.IsNullOrWhiteSpace(p.Warning) ? Visibility.Collapsed : Visibility.Visible;

        var linked = Connected(p);
        LinkBorder.Visibility = linked.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LinkText.Text = "Connected in game ✓  " + string.Join(", ", linked.Select(t => t.Name));
        LinkDetail.Text = string.Join("\n", linked.Where(t => !string.IsNullOrWhiteSpace(t.Status)).Select(t => t.Name + ": " + t.Status));
        LinkDetail.Visibility = LinkDetail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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
        RenderPresets(p);
    }

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
        else ServerHost.Content = new TextBlock { Text = "This version of GiftDeck can't run this kind of server yet.", Style = (Style)FindResource("Muted") };
    }

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

    void RenderPresets(GamePack p)
    {
        PresetRows.Children.Clear();
        var presets = Packs.Presets(p);
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
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = mark, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brush(markBrush), Margin = new Thickness(0, 1, 0, 0) });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(detail))
            text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
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

    // ---- Actions ----

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
