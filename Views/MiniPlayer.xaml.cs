using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Compact music controls (same player as the Music page), for the Go LIVE page.
public partial class MiniPlayer : UserControl
{
    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
    bool _loading = true;
    string _coverFor;

    public MiniPlayer()
    {
        InitializeComponent();
        GenreBox.ItemsSource = MusicService.Genres.Select(g => g.Label).ToList();
        Hub.Music.Changed += () => Dispatcher.BeginInvoke(Refresh);
        _timer.Tick += (_, _) => UpdateProgress();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { _timer.Stop(); return; }
            LoadSettings(); // the Music page may have changed them
            Refresh();
            _timer.Start();
        };
        LoadSettings();
        Refresh();
    }

    void LoadSettings()
    {
        _loading = true;
        VolumeSlider.Value = Hub.Settings.MusicVolume;
        VolumeText.Text = Hub.Settings.MusicVolume.ToString();
        GenreBox.SelectedIndex = Math.Max(0, Array.FindIndex(MusicService.Genres, g => g.Tag == Hub.Settings.MusicGenre));
        _loading = false;
    }

    void Refresh()
    {
        var m = Hub.Music;
        PlayButton.Content = m.Loading ? "…" : m.Playing ? "Pause" : "Play";
        PlayButton.IsEnabled = SkipButton.IsEnabled = !m.Loading;
        ErrorText.Text = m.LastError ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(m.LastError) ? Visibility.Collapsed : Visibility.Visible;
        var t = m.Current;
        if (t == null) return;
        TitleText.Text = t.Title;
        ArtistText.Text = t.Artist;
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
        var len = m.Length.TotalSeconds;
        Progress.Value = m.Current != null && len > 0 ? Math.Clamp(m.Position.TotalSeconds / len, 0, 1) : 0;
    }

    async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Hub.Music.Playing) Hub.Music.Pause();
        else await Hub.Music.PlayAsync();
    }

    async void Skip_Click(object sender, RoutedEventArgs e) => await Hub.Music.NextAsync();

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText == null) return;
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
}
