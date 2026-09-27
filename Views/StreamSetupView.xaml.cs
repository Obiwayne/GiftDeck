using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// One-time setup for Go LIVE: the vertical canvas and the Streamlabs token.
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
        _loading = false;
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
