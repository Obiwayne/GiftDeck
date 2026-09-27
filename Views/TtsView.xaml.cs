using System.Windows;
using System.Windows.Controls;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class TtsView : UserControl
{
    bool _loading = true;

    public TtsView()
    {
        InitializeComponent();
        VoiceCombo.ItemsSource = Hub.Tts.Voices;
        Fill();
        // The page stays open across profile switches: show the new profile's voice.
        void OnProfile() => Dispatcher.BeginInvoke(Fill);
        void OnMute() => Dispatcher.BeginInvoke(ShowMuted);
        Loaded += (_, _) => { Hub.Profiles.Changed += OnProfile; Hub.Tts.MuteChanged += OnMute; Fill(); };
        Unloaded += (_, _) => { Hub.Profiles.Changed -= OnProfile; Hub.Tts.MuteChanged -= OnMute; };
    }

    void Fill()
    {
        _loading = true;
        var voices = (List<string>)VoiceCombo.ItemsSource;
        VoiceCombo.SelectedItem = voices.Contains(Hub.Settings.TtsVoice) ? Hub.Settings.TtsVoice : voices.FirstOrDefault(v => !TtsService.IsOnline(v)) ?? voices.FirstOrDefault();
        RateSlider.Value = Hub.Settings.TtsRate;
        VolumeSlider.Value = Hub.Settings.TtsVolume;
        ReadChat.IsChecked = Hub.Settings.TtsReadChat;
        TemplateBox.Text = Hub.Settings.TtsChatTemplate;
        MaxCharsBox.Text = Hub.Settings.TtsMaxChars.ToString();
        ProfileName.Text = Hub.Profiles?.Active ?? Hub.Settings.ActiveProfile;
        UpdateLabels();
        ShowMuted();
        _loading = false;
    }

    void ShowMuted() => MutedBanner.Visibility = Hub.Tts.Muted ? Visibility.Visible : Visibility.Collapsed;

    void Unmute_Click(object sender, RoutedEventArgs e) => Hub.Tts.SetMuted(false);

    // Voice, speed, volume and read-chat are the active profile's: saved to its tts.json too.
    void Apply()
    {
        if (_loading) return;
        Hub.SaveSettings();
        TtsProfile.Save(Hub.Settings.ActiveProfile);
        Hub.Tts.Apply();
    }

    void UpdateLabels()
    {
        RateLabel.Text = "Speed: " + (int)RateSlider.Value;
        VolumeLabel.Text = "Volume: " + (int)VolumeSlider.Value;
    }

    void Voice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Hub.Settings.TtsVoice = VoiceCombo.SelectedItem as string ?? "";
        Apply();
    }

    void Rate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || RateLabel == null) return; // fires while the page is still being built
        Hub.Settings.TtsRate = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || VolumeLabel == null) return;
        Hub.Settings.TtsVolume = (int)e.NewValue;
        UpdateLabels();
        Apply();
    }

    void Speak_Click(object sender, RoutedEventArgs e) => Hub.Tts.Speak(TestBox.Text);
    void Stop_Click(object sender, RoutedEventArgs e) => Hub.Tts.Stop();

    void ReadChat_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.TtsReadChat = ReadChat.IsChecked == true;
        Apply();
    }

    void Template_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        Hub.Settings.TtsChatTemplate = TemplateBox.Text;
        Hub.SaveSettings();
    }

    void MaxChars_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(MaxCharsBox.Text.Trim(), out int n) && n >= 20)
        {
            Hub.Settings.TtsMaxChars = n;
            Hub.SaveSettings();
        }
    }
}
