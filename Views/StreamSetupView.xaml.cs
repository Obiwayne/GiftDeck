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
        VerticalBox.IsChecked = s.SendVertical;
        RelayServerBox.Text = RelayService.LocalServer;
        RelayKeyBox.Text = RelayService.LocalKey;
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
        Hub.Settings.BridgeUsername = name;
        Hub.SaveSettings();
        Hub.Bridge.Restart(); // reconnect to the new account's LIVE
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
        }
        catch (Exception ex) { AccountText.Text = ""; TokenError.Text = ex.Message; }
    }

    void CopyRelayServer_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(RelayService.LocalServer); } catch { } }
    void CopyRelayKey_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(RelayService.LocalKey); } catch { } }
}
