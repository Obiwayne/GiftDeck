using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class SpotifyView : UserControl
{
    readonly DispatcherTimer _volumeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
    bool _loading = true;
    bool _settingVolume;

    public SpotifyView()
    {
        InitializeComponent();
        ClientIdBox.Text = Hub.Settings.SpotifyClientId;
        RedirectBox.Text = Hub.Spotify.RedirectUri;
        ChatRequests.IsChecked = Hub.Settings.SpotifyChatRequests;
        CommandBox.Text = Hub.Settings.SpotifyRequestCommand;
        CooldownBox.Text = Hub.Settings.SpotifyRequestCooldownMinutes.ToString();
        BlockExplicit.IsChecked = Hub.Settings.SpotifyBlockExplicit;
        UpdateCommandHint();
        _loading = false;

        Hub.Spotify.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateStatus();

        _volumeDebounce.Tick += async (s, e) =>
        {
            _volumeDebounce.Stop();
            await Player(() => Hub.Spotify.SetVolumeAsync((int)VolumeSlider.Value));
        };
        _poll.Tick += async (s, e) => { if (IsVisible && Hub.Spotify.Linked) await RefreshNowPlaying(); };
        IsVisibleChanged += async (s, e) =>
        {
            if (IsVisible) { _poll.Start(); if (Hub.Spotify.Linked) await RefreshNowPlaying(); }
            else _poll.Stop();
        };
    }

    void UpdateStatus()
    {
        var sp = Hub.Spotify;
        LinkStatus.Text = sp.Linking ? "Waiting for you to approve GiftDeck in the browser."
            : sp.Linked ? "Linked" + (sp.AccountName != null ? " as " + sp.AccountName : "") + "."
            : "Not linked.";
        LinkButton.IsEnabled = !sp.Linking;
        UnlinkButton.Visibility = sp.Linked ? Visibility.Visible : Visibility.Collapsed;
        LinkError.Text = !sp.Linked && sp.LastError != null ? sp.LastError : "";
    }

    void ClientId_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Hub.Settings.SpotifyClientId = ClientIdBox.Text.Trim();
        Hub.SaveSettings();
    }

    void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(RedirectBox.Text); } catch { }
    }

    async void Link_Click(object sender, RoutedEventArgs e)
    {
        LinkError.Text = "";
        try
        {
            await Hub.Spotify.LinkAsync();
            await RefreshNowPlaying();
        }
        catch (Exception ex) { LinkError.Text = ex.Message; }
    }

    void Unlink_Click(object sender, RoutedEventArgs e)
    {
        Hub.Spotify.Unlink();
        NowPlaying.Text = "Nothing playing.";
    }

    async Task RefreshNowPlaying()
    {
        try
        {
            var np = await Hub.Spotify.NowPlayingAsync();
            NowPlaying.Text = np ?? "Nothing playing.";
            var vol = await Hub.Spotify.GetVolumeAsync();
            if (vol != null)
            {
                _settingVolume = true;
                VolumeSlider.Value = vol.Value;
                VolumeText.Text = vol.Value.ToString();
                _settingVolume = false;
            }
            PlayerError.Text = "";
        }
        catch (Exception ex) { PlayerError.Text = ex.Message; }
    }

    async Task Player(Func<Task> action)
    {
        try
        {
            await action();
            PlayerError.Text = "";
            await Task.Delay(400);
            await RefreshNowPlaying();
        }
        catch (Exception ex) { PlayerError.Text = ex.Message; }
    }

    async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshNowPlaying();
    async void Play_Click(object sender, RoutedEventArgs e) => await Player(Hub.Spotify.PlayAsync);
    async void Pause_Click(object sender, RoutedEventArgs e) => await Player(Hub.Spotify.PauseAsync);
    async void Next_Click(object sender, RoutedEventArgs e) => await Player(Hub.Spotify.NextAsync);
    async void Prev_Click(object sender, RoutedEventArgs e) => await Player(Hub.Spotify.PreviousAsync);

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return; // fired while the page is still being built
        VolumeText.Text = ((int)e.NewValue).ToString();
        if (_settingVolume || _loading) return;
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    void Cooldown_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        bool ok = int.TryParse(CooldownBox.Text.Trim(), out int m) && m >= 0;
        if (!ok)
        {
            CooldownBox.BorderBrush = System.Windows.Media.Brushes.IndianRed;
            CooldownBox.ToolTip = "Type a whole number of minutes, 0 or more. Not saved.";
            return;
        }
        CooldownBox.ClearValue(Control.BorderBrushProperty);
        CooldownBox.ClearValue(ToolTipProperty);
        Hub.Settings.SpotifyRequestCooldownMinutes = m;
        Hub.SaveSettings();
    }

    void BlockExplicit_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.SpotifyBlockExplicit = BlockExplicit.IsChecked == true;
        Hub.SaveSettings();
    }

    void ChatRequests_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.SpotifyChatRequests = ChatRequests.IsChecked == true;
        Hub.SaveSettings();
    }

    void Command_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var cmd = CommandBox.Text.Trim();
        if (cmd.Length == 0) cmd = "!sr";
        Hub.Settings.SpotifyRequestCommand = cmd;
        Hub.SaveSettings();
        UpdateCommandHint();
    }

    void UpdateCommandHint()
    {
        var cmd = Hub.Settings.SpotifyRequestCommand;
        CommandHint.Text = $"Viewers type \"{cmd} song name\" or paste a Spotify track link. The song is added to your queue.";
    }

    async void TryRequest_Click(object sender, RoutedEventArgs e)
    {
        TryResult.Text = "Searching";
        try
        {
            var label = await Hub.Spotify.RequestAsync(TryBox.Text);
            TryResult.Text = "Queued " + label;
        }
        catch (Exception ex) { TryResult.Text = ex.Message; }
    }
}
