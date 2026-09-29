using System.Windows;
using System.Windows.Controls;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The "All-in-one overlay" card at the top of the Overlays page: which parts show on /overlay/all and where.
public partial class AllInOnePanel : UserControl
{
    public static List<Choice> SpotList { get; } = new List<Choice>
    {
        new Choice(OverlaySpot.TopLeft, "Top left"),
        new Choice(OverlaySpot.Top, "Top"),
        new Choice(OverlaySpot.TopRight, "Top right"),
        new Choice(OverlaySpot.Left, "Left"),
        new Choice(OverlaySpot.Middle, "Middle"),
        new Choice(OverlaySpot.Right, "Right"),
        new Choice(OverlaySpot.BottomLeft, "Bottom left"),
        new Choice(OverlaySpot.Bottom, "Bottom"),
        new Choice(OverlaySpot.BottomRight, "Bottom right"),
    };

    public class PartRow
    {
        public string Name { get; set; }
        public string Hint { get; set; }
        public AllInOnePart Part { get; set; }
        public Visibility WidthVisibility { get; set; } = Visibility.Visible;
    }

    const string Path_ = "/overlay/all";
    bool _loading = true;

    public AllInOnePanel()
    {
        InitializeComponent();
        var c = Config;
        Parts.ItemsSource = new List<PartRow>
        {
            new PartRow { Name = "Alerts and interrupts", Part = c.Alerts, WidthVisibility = Visibility.Hidden,
                          Hint = "Gift, follow and share alerts and your own alerts. Where = where alert cards pop up; full-screen alerts cover everything." },
            new PartRow { Name = "Gift Spinner", Part = c.Spinner, Hint = "Shows whichever Gift Spinner an event spins." },
            new PartRow { Name = "Goals", Part = c.Goals, Hint = "Every goal bar, one under the other." },
            new PartRow { Name = "Top 3 gifters and next goal", Part = c.Strip },
            new PartRow { Name = "Gift board", Part = c.Board, Hint = "The gift menu board (tiles set up below)." },
            new PartRow { Name = "Gift list", Part = c.GiftList, Hint = "Gift picture, price and what it does, cheapest first." },
        };
        SpinnerIdle.IsChecked = c.SpinnerOnlyWhileSpinning;
        Room.IsChecked = c.LeaveRoomForTikTok;
        Shape.SelectedIndex = c.Landscape ? 1 : 0;
        ScaleSlider.Value = c.Scale;
        ScaleLabel.Text = "Size " + c.Scale + "%";
        SizePreview();
        _loading = false;
        Loaded += async (s, e) => await StartPreview();
    }

    static AllInOneConfig Config => Hub.Overlays.Config.AllInOne ??= new AllInOneConfig();

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (e is SelectionChangedEventArgs sc && sc.RemovedItems.Count == 0) return; // the list filling in, not a change
        var c = Config;
        c.SpinnerOnlyWhileSpinning = SpinnerIdle.IsChecked == true;
        c.LeaveRoomForTikTok = Room.IsChecked == true;
        Hub.Overlays.Touch();
    }

    void Scale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ScaleLabel != null) ScaleLabel.Text = "Size " + (int)ScaleSlider.Value + "%";
        if (_loading) return;
        Config.Scale = (int)ScaleSlider.Value;
        Hub.Overlays.Touch();
    }

    void Shape_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Config.Landscape = Shape.SelectedIndex == 1;
        Hub.Overlays.Touch();
        SizePreview();
    }

    (int w, int h) SourceSize => Config.Landscape ? (1920, 1080) : (1080, 1920);

    // The page scales everything to its own size, so a small preview with the same shape looks the same.
    void SizePreview()
    {
        if (Config.Landscape) { Preview.Width = 400; Preview.Height = 225; }
        else { Preview.Width = 270; Preview.Height = 480; }
        var (w, h) = SourceSize;
        PreviewLabel.Text = $"Preview ({w} x {h})";
    }

    bool _previewReady;

    async Task StartPreview()
    {
        if (!Hub.Web.Running) { PreviewLabel.Text = "Preview needs the overlay server"; return; }
        try
        {
            if (!_previewReady)
            {
                // Same browser cache folder as the other overlay previews.
                var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "WebView2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, cacheDir);
                await Preview.EnsureCoreWebView2Async(env);
                Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _previewReady = true;
            }
            Preview.Source = new Uri(Hub.Web.BaseUrl + Path_);
        }
        catch (Exception ex)
        {
            Log.Write("All-in-one preview unavailable: " + ex.Message);
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_previewReady) Preview.Reload();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var url = Hub.Web.BaseUrl + Path_;
        try { Clipboard.SetText(url); Status.Text = "Copied " + url; } catch { }
    }

    async void Obs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Hub.Obs.Connected) await Hub.Obs.ConnectAsync();
            var (w, h) = SourceSize;
            Status.Text = await Hub.Obs.AddBrowserSourceAsync("MayhemDeck all-in-one", Hub.Web.BaseUrl + Path_, w, h, audioViaObs: true);
        }
        catch (Exception ex)
        {
            Status.Text = "Could not add to OBS: " + ex.Message;
        }
    }

    void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        if (!Config.Alerts.On) Status.Text = "Alerts are switched off for this overlay: tick Alerts and interrupts to see them here.";
        Hub.Overlays.TestAlert();
    }

    // Just the wheel on the overlay: picks a prize at random and runs nothing.
    void TestSpin_Click(object sender, RoutedEventArgs e)
    {
        var s = Hub.Overlays.Config.Spinners.FirstOrDefault(x => x.Entries.Count > 0);
        if (s == null) { Status.Text = "Add a Gift Spinner with prizes first (further down this page)."; return; }
        if (!Config.Spinner.On) Status.Text = "The Gift Spinner is switched off for this overlay: tick it to see it here.";
        var viewer = new LiveEvent { Type = "gift", Nickname = "Test Viewer", GiftName = "Rose", Diamonds = 1, IsTest = true };
        Hub.Overlays.PushSpin(s, s.Entries, Random.Shared.Next(s.Entries.Count), viewer);
    }
}
