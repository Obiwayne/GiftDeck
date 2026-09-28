using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The Minecraft pack's server controls, for the Games page to show under a pack with "server" in pack.json:
//   var panel = new MinecraftServerPanel();            // controls Hub.Minecraft
//   var panel = new MinecraftServerPanel { Target = t }; // or another MinecraftTarget (tests, a second server)
// It needs nothing else: it follows the target's events while it is on screen and saves its own settings.
public partial class MinecraftServerPanel : UserControl
{
    MinecraftTarget _target;
    bool _busy;
    bool _loading;

    public MinecraftServerPanel()
    {
        InitializeComponent();
    }

    // The server this panel controls. Defaults to GiftDeck's own (Hub.Minecraft).
    public MinecraftTarget Target
    {
        get => _target ?? Hub.Minecraft;
        set
        {
            if (IsLoaded) Detach();
            _target = value;
            if (IsLoaded) Attach();
        }
    }

    // Raised (on the UI thread) whenever the server's state changes, so a host can refresh its own status line.
    public event Action StatusChanged;

    MinecraftServer Server => Target?.Server;

    // ---------------- life cycle ----------------

    void Panel_Loaded(object sender, RoutedEventArgs e) => Attach();
    void Panel_Unloaded(object sender, RoutedEventArgs e) => Detach();

    void Attach()
    {
        if (Target == null) { StatusText.Text = "The Minecraft server isn't available."; IsEnabled = false; return; }
        Target.Changed += OnChanged;
        Server.ConsoleLine += OnConsoleLine;
        Target.Start();

        _loading = true;
        PlayerBox.Text = Target.Settings.PlayerName;
        MemoryBox.Text = Target.Settings.MemoryMb.ToString();
        LanCheck.IsChecked = Target.Settings.LanAccess;
        StopWithAppCheck.IsChecked = Target.Settings.StopWithGiftDeck;
        FolderText.Text = "Server folder: " + Server.Folder;
        ConsoleBox.Text = string.Join(Environment.NewLine, Server.ConsoleTail(300));
        ConsoleBox.ScrollToEnd();
        _loading = false;
        FillGames();

        Refresh();
        _ = LoadVersionsAsync();
        _ = CheckJavaAsync();
    }

    void Detach()
    {
        if (Target == null) return;
        Target.Changed -= OnChanged;
        Server.ConsoleLine -= OnConsoleLine;
    }

    void OnChanged() => Dispatcher.BeginInvoke(() => { Refresh(); StatusChanged?.Invoke(); });

    void OnConsoleLine(string line) => Dispatcher.BeginInvoke(() => AppendConsole(line));

    void AppendConsole(string line)
    {
        if (ConsoleBox.Text.Length > 200_000) ConsoleBox.Text = ConsoleBox.Text[^100_000..];
        ConsoleBox.AppendText((ConsoleBox.Text.Length > 0 ? Environment.NewLine : "") + line);
        ConsoleBox.ScrollToEnd();
    }

    // ---------------- showing the state ----------------

    void Refresh()
    {
        if (Server == null) return;
        var state = Server.State;
        var info = Server.Info;
        bool installed = Server.IsInstalled;
        bool running = state is MinecraftServerState.Running or MinecraftServerState.Starting or MinecraftServerState.Stopping;

        StatusText.Text = Server.StateText;
        StatusDot.Fill = (Brush)FindResource(state switch
        {
            MinecraftServerState.Running when Server.RconConnected => "SuccessBrush",
            MinecraftServerState.Starting or MinecraftServerState.Stopping or MinecraftServerState.Running => "WarnBrush",
            _ => "MutedBrush",
        });

        InstalledText.Text = installed
            ? $"Set up: Paper {info.Version} (build {info.Build}). Play on Minecraft {info.Version}."
            : "Not set up yet: pick a version and press Set up server (about 60 MB).";
        SetupButton.Content = installed ? "Change version" : "Set up server";
        SetupButton.IsEnabled = !_busy && !running;
        VersionCombo.IsEnabled = !_busy && !running;

        EulaCheck.IsChecked = Server.EulaAccepted;
        EulaCheck.IsEnabled = !running;
        StartButton.IsEnabled = !_busy && installed && !running;
        StopButton.IsEnabled = state is MinecraftServerState.Running or MinecraftServerState.Starting;
        OpButton.IsEnabled = Server.RconConnected;
        GameStartButton.IsEnabled = GameResetButton.IsEnabled = GameStopButton.IsEnabled = Server.RconConnected;

        var port = Target.Settings.ServerPort == 25565 ? "" : ":" + Target.Settings.ServerPort;
        JoinText.Text = installed
            ? $"To play: open Minecraft {info.Version}, Multiplayer, Direct Connection, and type localhost{port}"
            : "";
        PlayersText.Text = Server.RconConnected && Server.PlayersOnline > 0 ? "Online now: " + Server.PlayerList : "";
    }

    void ShowError(string text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowProgress(InstallProgress p)
    {
        ProgressText.Visibility = Visibility.Visible;
        ProgressText.Text = p.Text;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = p.Fraction < 0;
        if (p.Fraction >= 0) Progress.Value = p.Fraction;
    }

    void HideProgress()
    {
        Progress.Visibility = Visibility.Collapsed;
        ProgressText.Visibility = Visibility.Collapsed;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        JavaButton.IsEnabled = !busy;
        Refresh();
    }

    async Task LoadVersionsAsync()
    {
        VersionCombo.Items.Clear();
        VersionCombo.Items.Add(new ComboBoxItem { Content = "Latest (recommended)", Tag = "latest" });
        var want = string.IsNullOrWhiteSpace(Target.Settings.Version) ? "latest" : Target.Settings.Version;
        VersionCombo.SelectedIndex = 0;
        try
        {
            foreach (var v in await MinecraftServer.GetVersionsAsync())
            {
                var item = new ComboBoxItem { Content = "Minecraft " + v, Tag = v };
                VersionCombo.Items.Add(item);
                if (v == want) VersionCombo.SelectedItem = item;
            }
        }
        catch
        {
            // Offline: offer what's set up (if anything) so the page still makes sense.
            var installed = Server.InstalledVersion;
            if (installed.Length > 0) VersionCombo.Items.Add(new ComboBoxItem { Content = "Minecraft " + installed, Tag = installed });
        }
    }

    string ChosenVersion => (VersionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "latest";

    // Shows which Java the set-up server will use, or offers the download.
    async Task<JavaInstall> CheckJavaAsync(int? need = null)
    {
        int min = need ?? Server.Info?.JavaMin ?? 0;
        if (min == 0)
        {
            JavaText.Text = "Java: GiftDeck checks this when you set up the server.";
            JavaButton.Visibility = Visibility.Collapsed;
            return null;
        }
        JavaText.Text = "Java: checking…";
        var java = await Task.Run(() => Server.FindJava(min));
        if (java != null)
        {
            JavaText.Text = $"Java {java.Major} found ({java.Source})";
            JavaButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            JavaText.Text = $"This server needs Java {min} or newer, and this PC doesn't have it.";
            JavaButton.Content = $"Download Java {min}";
            JavaButton.Tag = min;
            JavaButton.Visibility = Visibility.Visible;
        }
        return java;
    }

    // ---------------- buttons ----------------

    async void Setup_Click(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        SetBusy(true);
        var progress = new Progress<InstallProgress>(ShowProgress);
        try
        {
            ShowProgress(new InstallProgress("Asking PaperMC for the download…"));
            var build = await MinecraftServer.ResolveAsync(ChosenVersion);
            var installed = Server.Info;
            if (Server.IsInstalled && installed.Version == build.Version && installed.Build == build.Build)
            {
                ShowProgress(new InstallProgress($"Paper {build.Version} is already the newest build.", 1));
                return;
            }
            if (Server.IsInstalled && installed.Version != build.Version &&
                MessageBox.Show(Window.GetWindow(this),
                    $"Change the server from Minecraft {installed.Version} to {build.Version}?\n\nYour world is kept. Minecraft can bring a world up to a newer version, but a world opened on a newer version can't go back to an older one.",
                    "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var java = await CheckJavaAsync(build.JavaMin);
            if (java == null && !await OfferJavaAsync(build.JavaMin, progress)) return;

            await Server.InstallAsync(build, progress);
            Target.Settings.Version = ChosenVersion;
            Target.SaveSettings();
            Target.Start();
            await CheckJavaAsync();
        }
        catch (Exception ex) { ShowError("Couldn't set up the server: " + ex.Message); }
        finally
        {
            SetBusy(false);
            HideProgressLater();
        }
    }

    async Task<bool> OfferJavaAsync(int min, IProgress<InstallProgress> progress)
    {
        if (MessageBox.Show(Window.GetWindow(this),
                $"This Minecraft version needs Java {min} or newer.\n\nDownload Eclipse Temurin Java (free, about 55 MB) into GiftDeck's folder now? It isn't installed system-wide and only GiftDeck's server uses it.",
                "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            ShowError($"The server can't run without Java {min}.");
            return false;
        }
        await Server.DownloadJavaAsync(min, progress);
        await CheckJavaAsync(min);
        return true;
    }

    async void DownloadJava_Click(object sender, RoutedEventArgs e)
    {
        if (JavaButton.Tag is not int min) return;
        ShowError(null);
        SetBusy(true);
        try { await OfferJavaAsync(min, new Progress<InstallProgress>(ShowProgress)); }
        catch (Exception ex) { ShowError("Couldn't download Java: " + ex.Message); }
        finally { SetBusy(false); HideProgressLater(); }
    }

    async void HideProgressLater()
    {
        await Task.Delay(4000);
        if (!_busy) HideProgress();
    }

    async void Start_Click(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        SavePlayer();
        if (!Server.EulaAccepted) { ShowError("Tick \"I accept the Minecraft EULA\" first. The server won't start without it."); return; }
        SetBusy(true);
        try
        {
            var java = await CheckJavaAsync();
            if (java == null)
            {
                int min = Server.Info.JavaMin;
                if (!await OfferJavaAsync(min, new Progress<InstallProgress>(ShowProgress))) return;
                java = await CheckJavaAsync();
            }
            await Server.StartAsync(java);
            Target.Start();
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { SetBusy(false); HideProgressLater(); }
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Stop the Minecraft server? Anyone on it is disconnected, and gift commands stop working until it's started again.",
                "Stop server", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        ShowError(null);
        StopButton.IsEnabled = false;
        try { await Server.StopAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
        Refresh();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Server.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Server.Folder + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    void Eula_Click(object sender, RoutedEventArgs e)
    {
        if (EulaCheck.IsChecked == true) Server.AcceptEula();
        else Server.RevokeEula();
    }

    void Link_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    void Player_Changed(object sender, RoutedEventArgs e) => SavePlayer();

    void SavePlayer()
    {
        if (_loading || Target == null) return;
        var name = PlayerBox.Text.Trim();
        if (name.Length > 0 && MinecraftTarget.PlayerSelector(name) != name)
        {
            ShowError("A Minecraft name is 3 to 16 letters, numbers or _ (no spaces).");
            return;
        }
        if (Target.Settings.PlayerName == name) return;
        Target.Settings.PlayerName = name;
        Target.SaveSettings();
    }

    async void Op_Click(object sender, RoutedEventArgs e)
    {
        SavePlayer();
        var name = Target.Settings.PlayerName;
        if (string.IsNullOrEmpty(name)) { ShowError("Type your Minecraft name first."); return; }
        await RunConsoleCommand("op " + name);
    }

    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Target == null) return;
        var s = Target.Settings;
        if (int.TryParse(MemoryBox.Text.Trim(), out var mb)) s.MemoryMb = Math.Clamp(mb, 1024, 32768);
        MemoryBox.Text = s.MemoryMb.ToString();
        s.LanAccess = LanCheck.IsChecked == true;
        s.StopWithGiftDeck = StopWithAppCheck.IsChecked == true;
        Target.SaveSettings();
    }

    // ---------------- mini-games (GiftDeck Games plugin) ----------------

    static readonly (string Id, string Name, string Text)[] MiniGames =
    {
        ("bedrockbox", "Bedrock Box", "Dig down through 30 layers inside bedrock walls while viewers drop TNT, sand, anvils and mobs on you. Reach the emerald floor to win."),
        ("sandpour", "Sand Pour", "Viewers pour sand, gravel and anvils on you in a glass pit. Dig to keep breathing and survive 3 minutes."),
        ("sheepout", "Sheep Out", "Viewers fill a meadow with sheep named after them. Keep it under 40 sheep for 3 minutes with your sword."),
    };

    void FillGames()
    {
        if (GameCombo.Items.Count > 0) return;
        foreach (var g in MiniGames) GameCombo.Items.Add(new ComboBoxItem { Content = g.Name, Tag = g.Id });
        GameCombo.SelectedIndex = 0;
    }

    string ChosenGame => (GameCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? MiniGames[0].Id;

    void GameCombo_Changed(object sender, SelectionChangedEventArgs e) =>
        GameDescText.Text = MiniGames.FirstOrDefault(g => g.Id == ChosenGame).Text ?? "";

    async void GameStart_Click(object sender, RoutedEventArgs e)
    {
        SavePlayer();
        await RunGameCommand($"gdg {ChosenGame} start {MinecraftTarget.PlayerSelector(Target.Settings.PlayerName)}");
    }

    async void GameReset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Start this mini-game over? The current round's progress is lost.",
                "Start over", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunGameCommand($"gdg {ChosenGame} reset");
    }
    async void GameStop_Click(object sender, RoutedEventArgs e) => await RunGameCommand($"gdg {ChosenGame} stop");

    async Task RunGameCommand(string line)
    {
        if (!Server.RconConnected && Server.State != MinecraftServerState.Running)
        {
            GameStatusText.Text = "Start the server first.";
            return;
        }
        AppendConsole("> " + line);
        try
        {
            var reply = MinecraftTarget.CleanReply(await Target.RunRawAsync(line));
            if (reply.StartsWith("Unknown or incomplete command", StringComparison.OrdinalIgnoreCase))
                reply = "The GiftDeck Games plugin isn't loaded. Stop and start the server so GiftDeck can install it.";
            GameStatusText.Text = reply;
            AppendConsole(reply);
        }
        catch (Exception ex) { GameStatusText.Text = ex.Message; }
    }

    // ---------------- console ----------------

    void Command_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Send_Click(sender, e); e.Handled = true; }
    }

    async void Send_Click(object sender, RoutedEventArgs e)
    {
        var line = CommandBox.Text.Trim();
        if (line.Length == 0) return;
        CommandBox.Clear();
        await RunConsoleCommand(line);
    }

    async Task RunConsoleCommand(string line)
    {
        AppendConsole("> " + line);
        if (Server.RconConnected || Server.State == MinecraftServerState.Running)
        {
            try
            {
                var reply = MinecraftTarget.StripColors(await Target.RunRawAsync(line));
                if (reply.Length > 0) AppendConsole(reply);
            }
            catch (Exception ex) { AppendConsole("[GiftDeck] " + ex.Message); }
        }
        else if (Server.OwnsProcess) Server.SendConsole(line); // still starting: the console answers in the log
        else AppendConsole("[GiftDeck] The server isn't running.");
    }
}
