using System.Windows;
using System.Windows.Controls;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The stream tools on the Overlays page: overlay templates, Gift Spinners and custom alerts.
public partial class StreamToolsPanel : UserControl
{
    public static Rarity[] RarityList { get; } = Rarities.All;

    bool _loading = true;

    public StreamToolsPanel()
    {
        InitializeComponent();
        var t = Hub.Overlays.Config.Templates;
        GiftListTitle.Text = t.GiftListTitle;
        GiftListMax.Text = t.GiftListMax.ToString();
        StripTitle.Text = t.StripTitle;
        Hub.Overlays.ListsChanged += () => Dispatcher.BeginInvoke(RefreshLists);
        RefreshLists();
        _loading = false;
        Loaded += async (s, e) => await StartPreview("/overlay/giftlist", "Gift list", 420, 800);
    }

    void RefreshLists()
    {
        var cfg = Hub.Overlays.Config;
        foreach (var s in cfg.Spinners) UpdateChances(s);
        SpinnersList.ItemsSource = null;
        SpinnersList.ItemsSource = cfg.Spinners.ToList();
        NoSpinners.Visibility = cfg.Spinners.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertsList.ItemsSource = null;
        AlertsList.ItemsSource = cfg.CustomAlerts.ToList();
        NoAlerts.Visibility = cfg.CustomAlerts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static void UpdateChances(Spinner s)
    {
        foreach (var x in s.Entries)
        {
            var pct = SpinnerService.Chance(s, x);
            x.ChanceText = pct >= 10 ? pct.ToString("0") + "%" : pct.ToString("0.#") + "%";
        }
    }

    // ---- Preview: a small embedded browser showing the real overlay page ----

    bool _previewReady;
    string _previewPath;

    async Task StartPreview(string path, string label, int w, int h)
    {
        _previewPath = path;
        PreviewLabel.Text = "Preview: " + label;
        if (!Hub.Web.Running) { PreviewLabel.Text = "Preview needs the overlay server"; return; }
        try
        {
            if (!_previewReady)
            {
                // Same browser cache folder as the gift board preview.
                var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "WebView2");
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, cacheDir);
                await Preview.EnsureCoreWebView2Async(env);
                Preview.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Preview.CoreWebView2.Settings.IsStatusBarEnabled = false;
                Preview.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _previewReady = true;
            }
            // Scale the page so a Browser Source of w x h fits the 400 px wide box.
            double zoom = Math.Min(400.0 / w, 1.0);
            Preview.Height = Math.Clamp(h * zoom, 90, 460);
            Preview.ZoomFactor = zoom;
            Preview.Source = new Uri(Hub.Web.BaseUrl + path);
        }
        catch (Exception ex)
        {
            Log.Write("Stream tools preview unavailable: " + ex.Message);
        }
    }

    void RefreshPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_previewReady) Preview.Reload();
    }

    // ---- Templates ----

    void Templates_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var t = Hub.Overlays.Config.Templates;
        t.GiftListTitle = GiftListTitle.Text.Trim();
        if (int.TryParse(GiftListMax.Text.Trim(), out int max) && max >= 0) t.GiftListMax = max;
        t.StripTitle = StripTitle.Text.Trim();
        Hub.Overlays.Touch();
    }

    async void PreviewGiftList_Click(object sender, RoutedEventArgs e) => await StartPreview("/overlay/giftlist", "Gift list", 420, 800);
    void CopyGiftList_Click(object sender, RoutedEventArgs e) => Copy(Url("/overlay/giftlist"));
    async void ObsGiftList_Click(object sender, RoutedEventArgs e) => await AddToObs("GiftDeck gift list", Url("/overlay/giftlist"), 420, 800);
    async void PreviewStrip_Click(object sender, RoutedEventArgs e) => await StartPreview("/overlay/strip", "Top 3 gifters and next goal", 900, 90);
    void CopyStrip_Click(object sender, RoutedEventArgs e) => Copy(Url("/overlay/strip"));
    async void ObsStrip_Click(object sender, RoutedEventArgs e) => await AddToObs("GiftDeck top gifters", Url("/overlay/strip"), 900, 90);

    // ---- Gift Spinner ----

    static string SpinnerPath(Spinner s) => "/overlay/spinner?id=" + s.Id;

    void AddSpinner_Click(object sender, RoutedEventArgs e) => Hub.Overlays.AddSpinner();

    void RemoveSpinner_Click(object sender, RoutedEventArgs e)
    {
        var s = Of<Spinner>(sender);
        if (s == null) return;
        if (MessageBox.Show($"Remove the spinner \"{s.Name}\" and its prizes?", "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hub.Overlays.RemoveSpinner(s);
    }

    void AddEntry_Click(object sender, RoutedEventArgs e)
    {
        var s = Of<Spinner>(sender);
        if (s == null) return;
        s.Entries.Add(new SpinnerEntry());
        Hub.Overlays.Touch();
        RefreshLists();
    }

    void RemoveEntry_Click(object sender, RoutedEventArgs e)
    {
        var entry = Of<SpinnerEntry>(sender);
        var s = Hub.Overlays.Config.Spinners.FirstOrDefault(x => x.Entries.Contains(entry));
        if (s == null) return;
        s.Entries.Remove(entry);
        Hub.Overlays.Touch();
        RefreshLists();
    }

    void Entry_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        foreach (var s in Hub.Overlays.Config.Spinners) UpdateChances(s);
        Hub.Overlays.Touch();
    }

    void EntryActions_Click(object sender, RoutedEventArgs e)
    {
        var entry = Of<SpinnerEntry>(sender);
        if (entry == null) return;
        var win = new PrizeActionsWindow(entry) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true) Hub.Overlays.Touch();
    }

    async void SpinNow_Click(object sender, RoutedEventArgs e)
    {
        var s = Of<Spinner>(sender);
        if (s == null) return;
        await StartPreview(SpinnerPath(s), s.Name, 600, 700);
        await Task.Delay(700); // let the preview connect so it shows this spin
        try
        {
            var viewer = new LiveEvent { Type = "gift", UserId = "testuser", Nickname = "Test Viewer", GiftName = "Rose", Diamonds = 1, IsTest = true };
            var won = await Hub.Spinners.SpinAsync(s.Id.ToString(), viewer, s.TestRunsActions);
            Status.Text = $"Landed on {won.Label} ({won.Rarity})" + (s.TestRunsActions ? ", and ran its actions." : ". Its actions were not run (tick the box to run them too).");
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
        }
    }

    async void PreviewSpinner_Click(object sender, RoutedEventArgs e) { var s = Of<Spinner>(sender); if (s != null) await StartPreview(SpinnerPath(s), s.Name, 600, 700); }
    void CopySpinner_Click(object sender, RoutedEventArgs e) { var s = Of<Spinner>(sender); if (s != null) Copy(Url(SpinnerPath(s))); }
    async void ObsSpinner_Click(object sender, RoutedEventArgs e) { var s = Of<Spinner>(sender); if (s != null) await AddToObs("GiftDeck spinner: " + s.Name, Url(SpinnerPath(s)), 600, 700); }

    // ---- Alerts and interrupts ----

    void AddAlert_Click(object sender, RoutedEventArgs e) => Hub.Overlays.AddCustomAlert();

    void RemoveAlert_Click(object sender, RoutedEventArgs e)
    {
        var a = Of<AlertDef>(sender);
        if (a == null) return;
        if (MessageBox.Show($"Remove the alert \"{a.Name}\"? Events that show it will skip it.", "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hub.Overlays.RemoveCustomAlert(a);
    }

    void AlertMedia_Click(object sender, RoutedEventArgs e)
    {
        var a = Of<AlertDef>(sender);
        if (a == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures and videos|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.webm;*.mp4|All files|*.*", Title = "Choose a picture or video for this alert" };
        if (dlg.ShowDialog() != true) return;
        a.Media = dlg.FileName;
        Hub.Overlays.Touch();
    }

    void AlertSound_Click(object sender, RoutedEventArgs e)
    {
        var a = Of<AlertDef>(sender);
        if (a == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.ogg|All files|*.*", Title = "Choose a sound for this alert" };
        if (dlg.ShowDialog() != true) return;
        a.Sound = dlg.FileName;
        Hub.Overlays.Touch();
    }

    // Green screen: every slider step is saved (the overlay reads it when the alert next shows).
    void KeySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        Hub.Overlays.Touch();
    }

    // "Pick from video": the preview browser reads the colour of the video's corners (Overlays/chroma.js).
    async void PickKeyColor_Click(object sender, RoutedEventArgs e)
    {
        var a = Of<AlertDef>(sender);
        if (a == null) return;
        if (string.IsNullOrWhiteSpace(a.Media)) { Status.Text = "Choose the alert's video first."; return; }
        Hub.Overlays.Touch();
        if (!_previewReady)
        {
            await StartPreview("/overlay/alerts", "Gift alerts", 800, 450);
            await Task.Delay(800);
        }
        if (!_previewReady) { Status.Text = "Picking the colour needs the preview (the overlay server isn't running)."; return; }
        try
        {
            var expr = "(async () => { if (!window.GDChroma) await new Promise((ok, no) => { const s = document.createElement('script'); s.src = '/lib/chroma.js'; s.onload = ok; s.onerror = no; document.head.appendChild(s); });"
                     + " return await GDChroma.pickFromUrl('/alert-media/" + a.Id + "?v=' + Date.now()); })()";
            var json = await Preview.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                System.Text.Json.JsonSerializer.Serialize(new { expression = expr, awaitPromise = true, returnByValue = true }));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("exceptionDetails", out _) || !root.GetProperty("result").TryGetProperty("value", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new Exception("no colour");
            a.KeyColor = v.GetString();
            a.KeyGreen = true;
            Hub.Overlays.Touch();
            Status.Text = $"Colour to remove: {a.KeyColor} (from the corners of \"{a.Name}\")";
        }
        catch
        {
            Status.Text = "Couldn't read the colour from that video. Videos from websites can't be read: use a file on this PC, or type the colour in.";
        }
    }

    async void TestAlert_Click(object sender, RoutedEventArgs e)
    {
        var a = Of<AlertDef>(sender);
        if (a == null) return;
        Hub.Overlays.Touch();
        if (_previewPath != "/overlay/alerts")
        {
            await StartPreview("/overlay/alerts", "Gift alerts", a.FullScreen ? 1080 : 800, a.FullScreen ? 1920 : 450);
            await Task.Delay(700);
        }
        Hub.Alerts.Test(a);
    }

    // ---- Shared ----

    void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Hub.Overlays.Touch();
    }

    static T Of<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    static string Url(string path) => Hub.Web.BaseUrl + path;

    void Copy(string url)
    {
        try { Clipboard.SetText(url); Status.Text = "Copied " + url; } catch { }
    }

    async Task AddToObs(string name, string url, int w, int h)
    {
        try
        {
            if (!Hub.Obs.Connected) await Hub.Obs.ConnectAsync();
            Status.Text = await Hub.Obs.AddBrowserSourceAsync(name, url, w, h);
        }
        catch (Exception ex)
        {
            Status.Text = "Could not add to OBS: " + ex.Message;
        }
    }
}
