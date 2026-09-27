using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The two columns on the right of the main window: live chat and the gifts people have sent.
public partial class LivePanel : UserControl
{
    const int MaxItems = 300;

    static readonly Brush[] NameColours = new[] { "#A78BFA", "#22D3EE", "#34D399", "#FBBF24", "#F472B6", "#60A5FA", "#FB923C", "#F87171" }
        .Select(c => (Brush)new BrushConverter().ConvertFromString(c)).ToArray();

    int _giftCount;
    long _coinTotal;

    public LivePanel()
    {
        InitializeComponent();
        Hub.Rules.Handled += (e, fired) =>
        {
            if (e.Type == "chat" || e.Type == "gift") Dispatcher.BeginInvoke(() => Add(e));
            else if (e.Type == "viewers") Dispatcher.BeginInvoke(() => ShowViewers(e));
        };
        UpdateEmpty();
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateLive);
        UpdateLive();
        StartStatusStrip();
    }

    int _peak;

    void ShowViewers(LiveEvent e)
    {
        ViewerText.Text = e.ViewerCount.ToString("N0");
        if (e.ViewerCount > _peak) _peak = e.ViewerCount;
        PeakText.Text = $"Peak {_peak:N0}";
        if (e.TotalViewers > 0) TotalText.Text = $"{e.TotalViewers:N0} viewers in total";
    }

    // Green dot while the feed can see the LIVE; the numbers reset when a new LIVE starts.
    void UpdateLive()
    {
        var t = Hub.TikFinity;
        bool live = t.Connected && t.TikTokLive == true;
        LiveDot.Fill = (Brush)FindResource(live ? "SuccessBrush" : "MutedBrush");
        if (live && !_wasLive) { _peak = 0; PeakText.Text = "Peak 0"; TotalText.Text = ""; }
        if (!live) ViewerText.Text = "0";
        _wasLive = live;
    }

    bool _wasLive;

    void Add(LiveEvent e)
    {
        var who = string.IsNullOrWhiteSpace(e.Nickname) ? e.UserId : e.Nickname;
        if (string.IsNullOrWhiteSpace(who)) who = "Viewer";
        who = who.Trim();
        var time = e.Time.ToString("HH:mm") + (e.IsTest ? " · test" : "");

        if (e.Type == "chat")
        {
            var row = new ChatRow
            {
                Name = who,
                Initial = char.ToUpperInvariant(who[0]).ToString(),
                Message = e.Comment,
                TimeText = time,
                Colour = NameColours[(int)((uint)StableHash(who) % NameColours.Length)],
            };
            bool atBottom = IsAtBottom(ChatList);
            ChatList.Items.Add(row);
            while (ChatList.Items.Count > MaxItems) ChatList.Items.RemoveAt(0);
            if (atBottom) ChatList.ScrollIntoView(row);
        }
        else
        {
            var count = Math.Max(1, e.RepeatCount);
            var gift = Hub.Gifts.Find(e.GiftId, e.GiftName);
            var row = new GiftRow
            {
                Name = who,
                Gift = gift,
                GiftText = "sent " + (e.GiftName ?? gift?.Name ?? "a gift") + (count > 1 ? $" x{count}" : "") + "  ",
                CoinText = e.Coins > 0 ? $"{e.Coins:N0} {(e.Coins == 1 ? "coin" : "coins")}" : "",
                TimeText = time,
            };
            GiftList.Items.Insert(0, row);
            while (GiftList.Items.Count > MaxItems) GiftList.Items.RemoveAt(GiftList.Items.Count - 1);
            GiftList.ScrollIntoView(row);
            _giftCount += count;
            _coinTotal += e.Coins;
            UpdateTotals();
        }
        UpdateEmpty();
    }

    // Same viewer, same colour, every time the app runs (string.GetHashCode changes per run).
    static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    // Only follow new chat when the list is already at the bottom, so scrolling back to read isn't yanked away.
    static bool IsAtBottom(ListBox list)
    {
        if (VisualTreeHelper.GetChildrenCount(list) == 0) return true;
        var border = VisualTreeHelper.GetChild(list, 0) as Decorator;
        if (border?.Child is not ScrollViewer sv) return true;
        return sv.VerticalOffset >= sv.ScrollableHeight - 1;
    }

    void UpdateTotals() =>
        GiftTotals.Text = $"{_giftCount:N0} {(_giftCount == 1 ? "gift" : "gifts")} · {_coinTotal:N0} {(_coinTotal == 1 ? "coin" : "coins")}";

    void UpdateEmpty()
    {
        ChatEmpty.Visibility = ChatList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        GiftEmpty.Visibility = GiftList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        ChatList.Items.Clear();
        UpdateEmpty();
    }

    void ClearGifts_Click(object sender, RoutedEventArgs e)
    {
        GiftList.Items.Clear();
        _giftCount = 0;
        _coinTotal = 0;
        UpdateTotals();
        UpdateEmpty();
    }

    public class ChatRow
    {
        public string Name { get; set; }
        public string Initial { get; set; }
        public string Message { get; set; }
        public string TimeText { get; set; }
        public Brush Colour { get; set; }
    }

    public class GiftRow
    {
        public string Name { get; set; }
        public GiftInfo Gift { get; set; }
        public string GiftText { get; set; }
        public string CoinText { get; set; }
        public string TimeText { get; set; }
    }
}
