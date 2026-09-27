using System.Windows;
using System.Windows.Controls;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class DashboardView : UserControl
{
    const int MaxLines = 400;

    public DashboardView()
    {
        InitializeComponent();
        TestGift.ItemsSource = Hub.Gifts.Gifts;
        if (Hub.Gifts.Gifts.Count > 0) TestGift.SelectedIndex = 0;

        Hub.Rules.Handled += OnHandled;
        Log.Written += OnLog;
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        BridgeService.AccountChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        Hub.Spotify.StatusChanged += () => Dispatcher.BeginInvoke(UpdateStatus);
        UpdateStatus();
        Add("Ready. Events from your LIVE will show up here.");
    }

    void OnLog(string line) => Dispatcher.BeginInvoke(() => Add(line));

    void OnHandled(LiveEvent e, List<Rule> fired) => Dispatcher.BeginInvoke(() =>
    {
        if (e.Type == "viewers") return; // viewer counts arrive constantly; the Overlays page shows them
        var s = $"{e.Time:HH:mm:ss}  {e.Describe()}";
        // How long TikTok -> GiftDeck took (ignored if the PC clock is too far off to be meaningful)
        if (e.SentAt != null && (e.Type == "gift" || e.Type == "chat"))
        {
            var delay = (e.Time - e.SentAt.Value).TotalSeconds;
            if (delay >= 0 && delay < 120) s += $"  \u00b7 {delay:0.0} s";
        }
        if (fired.Count > 0) s += "   »  " + string.Join(", ", fired.Select(r => r.Name));
        Add(s);
    });

    void Add(string line)
    {
        FeedList.Items.Add(line);
        while (FeedList.Items.Count > MaxLines) FeedList.Items.RemoveAt(0);
        FeedList.ScrollIntoView(line);
    }

    void UpdateStatus()
    {
        var t = Hub.TikFinity;
        if (BridgeService.NeedsUsername)
        {
            TikTitle.Text = "TikTok LIVE";
            TikStatus.Text = "Set your TikTok username so GiftDeck knows whose LIVE to read. Logging in with TikTok on Stream Setup fills it in for you.";
            TikButton.Content = "Go to Stream Setup";
            TikButton.Visibility = Visibility.Visible;
        }
        else if (BridgeService.InUse)
        {
            TikTitle.Text = "TikTok LIVE";
            TikStatus.Text = !t.Connected ? "GiftDeck's TikTok bridge is starting."
                : t.TikTokLive == true ? "Connected to your LIVE. Every gift, follow, like and chat message reaches GiftDeck."
                : "Waiting for you to go LIVE. GiftDeck connects by itself within about 30 seconds of the LIVE starting (it doesn't need TikFinity).";
            TikButton.Visibility = Visibility.Collapsed;
        }
        else
        {
        TikTitle.Text = "TikFinity";
        TikButton.Content = "Launch TikFinity";
        // TikFinity reports "not live" whenever you're offline; only a problem once GiftDeck has put you LIVE.
        TikStatus.Text = t.Connected && t.TikTokLive == false
                ? (Hub.TikTok.Live
                    ? "Running, but TikFinity is NOT on your LIVE, so gifts and chat won't arrive. In TikFinity, click Connect (or restart it)."
                    : "Connected. Waiting for you to go LIVE; every gift, follow, like and chat message will reach GiftDeck.")
            : t.Connected ? "Running and connected. Every gift, follow, like and chat message reaches GiftDeck."
            : t.ProcessRunning ? "Running. Waiting for its event feed to open."
            : "Not running. GiftDeck needs it for the TikTok connection.";
        TikButton.Visibility = t.ProcessRunning ? Visibility.Collapsed : Visibility.Visible;
        }

        ObsStatus.Text = Hub.Obs.Connected ? $"Connected. {Hub.Obs.Scenes.Count} scenes available."
            : Hub.Obs.LastError != null ? "Not connected: " + Hub.Obs.LastError
            : "Not connected. Needed only for scene and source actions.";
        ObsButton.Visibility = Hub.Obs.Connected ? Visibility.Collapsed : Visibility.Visible;

        SpotStatus.Text = Hub.Spotify.Linked ? "Linked" + (Hub.Spotify.AccountName != null ? " as " + Hub.Spotify.AccountName : "") + ". Song requests and player control are ready."
            : "Not linked. Needed only for Spotify actions.";
    }

    void LaunchTik_Click(object sender, RoutedEventArgs e)
    {
        if (BridgeService.NeedsUsername) (Window.GetWindow(this) as MainWindow)?.Navigate("setup");
        else Hub.TikFinity.Launch();
    }

    async void ConnectObs_Click(object sender, RoutedEventArgs e)
    {
        try { await Hub.Obs.ConnectAsync(); }
        catch (Exception ex) { Add("OBS: " + ex.Message); }
        UpdateStatus();
    }

    void GoObs_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("obs");
    void GoSpotify_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("spotify");
    void ClearFeed_Click(object sender, RoutedEventArgs e) => FeedList.Items.Clear();

    LiveEvent Base(string type)
    {
        var name = TestUser.Text.Trim();
        return new LiveEvent { Type = type, UserId = "testuser", Nickname = name.Length > 0 ? name : "Test Viewer", IsTest = true };
    }

    void TestGift_Click(object sender, RoutedEventArgs e)
    {
        if (!(TestGift.SelectedItem is GiftInfo g)) return;
        int.TryParse(TestCount.Text, out int count);
        var ev = Base("gift");
        ev.GiftId = g.Id;
        ev.GiftName = g.Name;
        ev.Diamonds = g.Coins;
        ev.RepeatCount = Math.Max(1, count);
        Hub.Rules.Handle(ev);
    }

    void TestFollow_Click(object sender, RoutedEventArgs e) => Hub.Rules.Handle(Base("follow"));
    void TestShare_Click(object sender, RoutedEventArgs e) => Hub.Rules.Handle(Base("share"));
    void TestSub_Click(object sender, RoutedEventArgs e) => Hub.Rules.Handle(Base("subscribe"));
    void TestJoin_Click(object sender, RoutedEventArgs e) => Hub.Rules.Handle(Base("join"));

    void TestLikes_Click(object sender, RoutedEventArgs e)
    {
        int.TryParse(TestLikes.Text, out int n);
        var ev = Base("like");
        ev.LikeCount = Math.Max(1, n);
        ev.IsTest = false; // let the like counter behave like a real stream
        Hub.Rules.Handle(ev);
    }

    void TestChat_Click(object sender, RoutedEventArgs e)
    {
        var ev = Base("chat");
        ev.Comment = TestChat.Text;
        ev.IsTest = false; // so chat TTS and song requests can be tried out
        Hub.Rules.Handle(ev);
    }
}
