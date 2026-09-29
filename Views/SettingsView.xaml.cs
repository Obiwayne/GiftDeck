using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class SettingsView : UserControl
{
    bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        Load();
        // The page is kept between visits; other pages (a game's "use this window") can change these meanwhile.
        IsVisibleChanged += (_, _) => { if (IsVisible) Load(); };
    }

    void Load()
    {
        _loading = true;
        var s = Hub.Settings;
        AutoLaunch.IsChecked = s.AutoLaunchTikFinity;
        ExeBox.Text = s.TikFinityExe;
        UrlBox.Text = s.TikFinityUrl;
        StreakOnce.IsChecked = s.StreakGiftsOnce;
        FocusWindow.IsChecked = s.FocusWindowBeforeKeys;
        FocusTitle.Text = s.FocusWindowTitle;
        HoldBox.Text = s.KeyHoldMs.ToString();
        SoundVolume.Value = s.SoundVolume;
        SoundLabel.Text = "Master volume: " + s.SoundVolume;
        VersionText.Text = "MayhemDeck " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "") + ". Rides on TikFinity's local event feed; not affiliated with TikFinity or TikTok.";
        DataFolder.Text = Storage.Dir;
        _loading = false;
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var s = Hub.Settings;
        s.AutoLaunchTikFinity = AutoLaunch.IsChecked == true;
        s.StreakGiftsOnce = StreakOnce.IsChecked == true;
        s.FocusWindowBeforeKeys = FocusWindow.IsChecked == true;
        Hub.SaveSettings();
        // Only the auto-launch box itself starts TikFinity; ticking an unrelated box shouldn't.
        if (sender == AutoLaunch && s.AutoLaunchTikFinity && !TikFinityService.IsProcessRunning()) Hub.TikFinity.Launch();
    }

    void Text_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        var s = Hub.Settings;
        s.TikFinityExe = ExeBox.Text.Trim();
        var url = UrlBox.Text.Trim();
        if (Flag(UrlBox, url.StartsWith("ws"), "The address has to start with ws:// (for example ws://localhost:21213). Not saved.")) s.TikFinityUrl = url;
        s.FocusWindowTitle = FocusTitle.Text.Trim();
        var holdOk = int.TryParse(HoldBox.Text.Trim(), out int hold) && hold >= 10;
        if (Flag(HoldBox, holdOk, "Type a whole number of milliseconds, 10 or more. Not saved.")) s.KeyHoldMs = hold;
        Hub.SaveSettings();
    }

    // Marks a box red with the reason while its text can't be saved, so it doesn't silently revert on restart.
    static bool Flag(TextBox box, bool ok, string why)
    {
        if (ok)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ClearValue(ToolTipProperty);
        }
        else
        {
            box.BorderBrush = System.Windows.Media.Brushes.IndianRed;
            box.ToolTip = why;
        }
        return ok;
    }

    void SoundVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SoundLabel == null) return; // fires while the page is still being built
        SoundLabel.Text = "Master volume: " + (int)e.NewValue;
        if (_loading) return;
        Hub.Settings.SoundVolume = (int)e.NewValue;
        Hub.SaveSettings();
    }

    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "TikFinity|TikFinity.exe|Programs|*.exe" };
        if (dlg.ShowDialog() == true) ExeBox.Text = dlg.FileName;
    }

    void Launch_Click(object sender, RoutedEventArgs e) => Hub.TikFinity.Launch();

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Storage.Dir);
        Process.Start(new ProcessStartInfo(Storage.Dir) { UseShellExecute = true });
    }

    // The welcome pages live on the setup screen that covers the main window.
    void ShowWelcome_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.FindName("Setup") is SetupWizard setup) setup.ShowWelcome();
    }

    void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        var p = Storage.PathFor("log.txt");
        if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
    }
}
