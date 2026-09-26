using System.ComponentModel;
using System.Text.Json.Serialization;

namespace GiftDeck.Models;

public enum GoalMetric { Coins, Likes, Follows, Shares, Gifts }

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// A progress bar overlay: "Coin goal 350 / 1000".
public class Goal : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    string _title = "Coin goal";
    public string Title { get => _title; set { _title = value; Raise(nameof(Title)); } }

    GoalMetric _metric = GoalMetric.Coins;
    public GoalMetric Metric { get => _metric; set { _metric = value; Raise(nameof(Metric)); } }

    int _target = 1000;
    public int Target { get => _target; set { _target = Math.Max(1, value); Raise(nameof(Target)); } }

    int _progress;
    public int Progress { get => _progress; set { _progress = Math.Max(0, value); Raise(nameof(Progress)); } }
}

// A countdown timer overlay that viewers can extend with gifts, follows, shares and likes.
public class Countdown : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    string _title = "Stream ends in";
    public string Title { get => _title; set { _title = value; Raise(nameof(Title)); } }

    // Seconds left while paused. While running, EndsAt is the source of truth.
    public int PausedSeconds { get; set; } = 3600;
    public DateTime? EndsAt { get; set; }

    bool _running;
    public bool Running { get => _running; set { _running = value; Raise(nameof(Running)); Raise(nameof(StartPauseLabel)); } }

    public int SecondsPerCoin { get; set; } = 5;
    public int SecondsPerFollow { get; set; } = 30;
    public int SecondsPerShare { get; set; } = 15;
    public int SecondsPer100Likes { get; set; } = 10;

    [JsonIgnore] public int SetMinutes { get; set; } = 60;   // the "set to" box in the app
    [JsonIgnore] public int RemainingSeconds => Running && EndsAt != null ? Math.Max(0, (int)(EndsAt.Value - DateTime.UtcNow).TotalSeconds) : PausedSeconds;
    [JsonIgnore] public string RemainingText => TimeSpan.FromSeconds(RemainingSeconds).ToString(RemainingSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    [JsonIgnore] public string StartPauseLabel => Running ? "Pause" : "Start";

    public void Tick() { Raise(nameof(RemainingText)); }

    public void Start()
    {
        if (Running) return;
        EndsAt = DateTime.UtcNow.AddSeconds(PausedSeconds);
        Running = true;
        Tick();
    }

    public void Pause()
    {
        if (!Running) return;
        PausedSeconds = RemainingSeconds;
        EndsAt = null;
        Running = false;
        Tick();
    }

    public void Set(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (Running) EndsAt = DateTime.UtcNow.AddSeconds(seconds); else PausedSeconds = seconds;
        Tick();
    }

    public void Add(int seconds)
    {
        if (seconds == 0) return;
        Set(RemainingSeconds + seconds);
    }
}

// Totals for the current stream, shown by the counter overlays and used by goals.
public class StreamStats : Observable
{
    int _follows, _shares, _likes, _coins, _gifts, _viewers;
    public int Follows { get => _follows; set { _follows = value; Raise(nameof(Follows)); } }
    public int Shares { get => _shares; set { _shares = value; Raise(nameof(Shares)); } }
    public int Likes { get => _likes; set { _likes = value; Raise(nameof(Likes)); } }
    public int Coins { get => _coins; set { _coins = value; Raise(nameof(Coins)); } }
    public int Gifts { get => _gifts; set { _gifts = value; Raise(nameof(Gifts)); } }
    public int Viewers { get => _viewers; set { _viewers = value; Raise(nameof(Viewers)); } }
    public DateTime Started { get; set; } = DateTime.Now;

    public void Reset()
    {
        Follows = Shares = Likes = Coins = Gifts = 0;
        Started = DateTime.Now;
    }
}

// One square on the gift menu board: a picture and a label, usually made from an event.
public class MenuTile : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? RuleId { get; set; }

    string _label = "";
    public string Label { get => _label; set { _label = value ?? ""; Raise(nameof(Label)); } }

    string _subtitle = "";
    public string Subtitle { get => _subtitle; set { _subtitle = value ?? ""; Raise(nameof(Subtitle)); } }

    // A web address or a file on this PC. Blank means "use the event's gift picture".
    string _imageUrl = "";
    public string ImageUrl
    {
        get => _imageUrl;
        set { _imageUrl = value ?? ""; _image = null; _loading = false; Raise(nameof(ImageUrl)); Raise(nameof(Image)); }
    }

    System.Windows.Media.ImageSource _image;
    bool _loading;

    [JsonIgnore]
    public System.Windows.Media.ImageSource Image
    {
        get
        {
            if (_image == null && !_loading)
            {
                _loading = true;
                _ = LoadAsync();
            }
            return _image;
        }
    }

    public void RefreshImage() { _image = null; _loading = false; Raise(nameof(Image)); }

    async Task LoadAsync()
    {
        try
        {
            var src = GiftDeck.Services.Hub.Overlays?.ResolveTileImage(this);
            if (string.IsNullOrEmpty(src)) return;
            System.Windows.Media.ImageSource img;
            if (File.Exists(src))
            {
                img = await Task.Run(() =>
                {
                    var b = new System.Windows.Media.Imaging.BitmapImage();
                    b.BeginInit();
                    b.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 96;
                    b.UriSource = new Uri(src, UriKind.Absolute);
                    b.EndInit();
                    b.Freeze();
                    return (System.Windows.Media.ImageSource)b;
                });
            }
            else img = await GiftDeck.Services.GiftImages.LoadAsync(src);
            if (img == null) return;
            _image = img;
            Raise(nameof(Image));
        }
        catch { }
    }
}

public class MenuConfig
{
    public string Title { get; set; } = "WHAT THE GIFTS DO";
    public int Columns { get; set; } = 5;
    public int TileSize { get; set; } = 0;   // pixels per square tile; 0 = stretch to fill the Browser Source
    public bool ShowLines { get; set; } = true;
    public string LineColor { get; set; } = "#FFFFFF";
    public int LineWidth { get; set; } = 2;
    public bool ShowSubtitle { get; set; } = true;
    public bool Transparent { get; set; } = false;
    public string HeaderColor { get; set; } = "#1D5FB4";
    public string Font { get; set; } = "Impact";
    public List<MenuTile> Tiles { get; set; } = new List<MenuTile>();
}

public class OverlayConfig
{
    public MenuConfig Menu { get; set; } = new MenuConfig();
    public int Port { get; set; } = 21300;
    public List<Goal> Goals { get; set; } = new List<Goal>();
    public List<Countdown> Countdowns { get; set; } = new List<Countdown>();
    public StreamStats Stats { get; set; } = new StreamStats();
    public string Accent { get; set; } = "#7C5CFF";
    public string Font { get; set; } = "Segoe UI";
    public int AlertSeconds { get; set; } = 6;
    public int AlertMinCoins { get; set; } = 0;
    public bool AlertFollows { get; set; } = true;
    public bool AlertShares { get; set; } = false;
}
