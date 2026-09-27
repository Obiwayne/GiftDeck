using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Speaker toggle for text to speech. Muting stops what's being said now and skips every
// Speak action and chat message until it's clicked again.
public partial class TtsMuteButton : UserControl
{
    public TtsMuteButton()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (Hub.Tts != null) Hub.Tts.MuteChanged += OnMuteChanged; Show(); };
        Unloaded += (_, _) => { if (Hub.Tts != null) Hub.Tts.MuteChanged -= OnMuteChanged; };
        Show();
    }

    // True: always says "TTS on" / "TTS muted" (Go LIVE, where a bare speaker could pass for stream audio).
    // False: just the speaker icon until muted (the narrow Live panel header).
    public bool AlwaysShowLabel { get; set; }

    void OnMuteChanged() => Dispatcher.BeginInvoke(Show);

    void Btn_Click(object sender, RoutedEventArgs e) => Hub.Tts?.ToggleMute();

    void Show()
    {
        bool muted = Hub.Tts?.Muted ?? Hub.Settings.TtsMuted;
        Glyph.Text = ((char)(muted ? 0xE74F : 0xE767)).ToString(); // Segoe icons "Mute" / "Volume"
        Word.Text = muted ? "TTS muted" : "TTS on";
        Word.Visibility = muted || AlwaysShowLabel ? Visibility.Visible : Visibility.Collapsed;
        Btn.Foreground = (Brush)FindResource(muted ? "DangerBrush" : "MutedBrush");
        Btn.Background = muted ? (Brush)FindResource("DangerDimBrush") : Brushes.Transparent;
        Btn.ToolTip = muted
            ? "Text to speech is muted. Click to turn it back on."
            : "Text to speech is on. Click to mute it (stops what's being said now).";
        AutomationProperties.SetName(Btn, muted ? "Unmute text to speech" : "Mute text to speech");
    }
}
