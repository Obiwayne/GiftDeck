using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class MusicView : UserControl
{
    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    bool _loading = true;
    string _coverFor;

    void Duck_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.MusicDuckForTts = DuckBox.IsChecked == true;
        Hub.SaveSettings();
    }

    public MusicView()
    {
        InitializeComponent();
        var s = Hub.Settings;
        ClientIdBox.Text = s.JamendoClientId;
        VolumeSlider.Value = s.MusicVolume;
        VolumeText.Text = s.MusicVolume.ToString();
        GenreBox.ItemsSource = MusicService.Genres.Select(g => g.Label).ToList();
        GenreBox.SelectedIndex = Math.Max(0, Array.FindIndex(MusicService.Genres, g => g.Tag == s.MusicGenre));
        InstrumentalBox.IsChecked = s.MusicInstrumental;
        DuckBox.IsChecked = s.MusicDuckForTts;
        _loading = false;

        Hub.Music.Changed += () => Dispatcher.BeginInvoke(Refresh);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) return;
            _loading = true; // the Go LIVE mini player may have changed these
            VolumeSlider.Value = Hub.Settings.MusicVolume;
            GenreBox.SelectedIndex = Math.Max(0, Array.FindIndex(MusicService.Genres, g => g.Tag == Hub.Settings.MusicGenre));
            _loading = false;
        };
        _timer.Tick += (_, _) => UpdateProgress();
        _timer.Start();
        Refresh();
    }

    void Refresh()
    {
        var m = Hub.Music;
        var t = m.Current;
        KeyStatus.Text = string.IsNullOrWhiteSpace(Hub.Settings.JamendoClientId) ? "Not set up yet. Music can't play until a Client ID is saved below." : "Client ID saved.";

        PlayButton.Content = m.Loading ? "Loading…" : m.Playing ? "Pause" : "Play";
        PlayButton.IsEnabled = SkipButton.IsEnabled = !m.Loading;
        OpenButton.Visibility = t != null && !string.IsNullOrEmpty(t.PageUrl) ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = m.LastError ?? "";

        if (t == null) return;
        TitleText.Text = t.Title;
        ArtistText.Text = "by " + t.Artist;
        LicenseText.Text = string.IsNullOrEmpty(t.LicenseUrl) ? "" : "Licence: " + t.LicenseUrl;
        if (_coverFor != t.ImageUrl)
        {
            _coverFor = t.ImageUrl;
            try { Cover.Source = string.IsNullOrEmpty(t.ImageUrl) ? null : new BitmapImage(new Uri(t.ImageUrl)); }
            catch { Cover.Source = null; }
        }
        UpdateProgress();
    }

    void UpdateProgress()
    {
        var m = Hub.Music;
        if (m.Current == null) return;
        var len = m.Length;
        var pos = m.Position;
        Progress.Value = len.TotalSeconds > 0 ? Math.Clamp(pos.TotalSeconds / len.TotalSeconds, 0, 1) : 0;
        PosText.Text = Time(pos);
        LenText.Text = Time(len);
    }

    static string Time(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Hub.Music.Playing) Hub.Music.Pause();
        else await Hub.Music.PlayAsync();
    }

    async void Skip_Click(object sender, RoutedEventArgs e) => await Hub.Music.NextAsync();

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return; // fires while the page is still being built
        VolumeText.Text = ((int)e.NewValue).ToString();
        if (_loading) return;
        Hub.Settings.MusicVolume = (int)e.NewValue;
        Hub.Music.ApplyVolume();
        Hub.SaveSettings();
    }

    void Genre_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || GenreBox.SelectedIndex < 0) return;
        Hub.Settings.MusicGenre = MusicService.Genres[GenreBox.SelectedIndex].Tag;
        Hub.SaveSettings();
        Hub.Music.FilterChanged();
    }

    void Instrumental_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.MusicInstrumental = InstrumentalBox.IsChecked == true;
        Hub.SaveSettings();
        Hub.Music.FilterChanged();
    }

    void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        Hub.Settings.JamendoClientId = ClientIdBox.Text.Trim();
        Hub.SaveSettings();
        Hub.Music.FilterChanged();
        Refresh();
    }

    void Portal_Click(object sender, RoutedEventArgs e) => OpenUrl("https://devportal.jamendo.com/");

    void Open_Click(object sender, RoutedEventArgs e) => OpenUrl(Hub.Music.Current?.PageUrl);

    static void OpenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
