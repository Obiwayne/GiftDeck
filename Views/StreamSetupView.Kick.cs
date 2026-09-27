using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The Kick card: switch it on, type the channel name, see whether GiftDeck is connected.
public partial class StreamSetupView
{
    void InitKick()
    {
        KickEnabledBox.IsChecked = Hub.Settings.KickEnabled;
        KickChannelBox.Text = Hub.Settings.KickChannel;
        Hub.Kick.StatusChanged += () => Dispatcher.BeginInvoke(UpdateKick);
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        poll.Tick += (_, _) => { if (IsVisible) UpdateKick(); };
        poll.Start();
        UpdateKick();
    }

    void KickEnabled_Changed(object sender, RoutedEventArgs e)
    {
        Hub.Settings.KickEnabled = KickEnabledBox.IsChecked == true;
        SaveKickChannel();
        Log.Write(Hub.Settings.KickEnabled ? "Kick switched on" : "Kick switched off");
        if (Hub.Settings.KickEnabled && string.IsNullOrWhiteSpace(KickChannelBox.Text)) KickChannelBox.Focus();
    }

    void KickChannel_Changed(object sender, RoutedEventArgs e) => SaveKickChannel();

    void KickChannel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveKickChannel(); }
    }

    void SaveKickChannel()
    {
        if (_loading) return;
        var name = KickApi.CleanName(KickChannelBox.Text);
        if (KickChannelBox.Text != name) KickChannelBox.Text = name;
        bool changed = name != Hub.Settings.KickChannel;
        Hub.Settings.KickChannel = name;
        Hub.SaveSettings();
        if (changed || !Hub.Kick.Connected || !Hub.Settings.KickEnabled) Hub.Kick.Restart();
        UpdateKick();
    }

    void UpdateKick()
    {
        var k = Hub.Kick;
        var s = Hub.Settings;
        string status, detail = "", brush = "TextBrush";
        if (!s.KickEnabled)
        {
            status = "Kick is off";
            detail = "Tick the box to read your Kick channel next to TikTok.";
            brush = "MutedBrush";
        }
        else if (string.IsNullOrWhiteSpace(k.ChannelName))
        {
            status = "Type your Kick channel name";
            brush = "WarnBrush";
        }
        else if (k.Connected)
        {
            status = "Connected to kick.com/" + k.ChannelName + " ✓";
            brush = "SuccessBrush";
            detail = (k.Live == true ? "Your Kick stream is live. " : "Not live on Kick right now; chat still comes through. ")
                     + "Kicks gifts count as coins (1 Kick = 1 coin), so \"Any gift\" events work for both.";
        }
        else
        {
            status = "Connecting to kick.com/" + k.ChannelName + "…";
            brush = string.IsNullOrWhiteSpace(k.LastError) ? "TextBrush" : "WarnBrush";
        }
        if (s.KickEnabled && !k.Connected && !string.IsNullOrWhiteSpace(k.LastError))
            detail = "Last error: " + k.LastError;
        KickStatus.Text = status;
        KickStatus.Foreground = (Brush)FindResource(brush);
        KickDetail.Text = detail;
        KickDetail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
    }
}
