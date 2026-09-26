using System.Text.Json.Serialization;

namespace GiftDeck.Models;

public enum TriggerType { Gift, AnyGift, Follow, Share, Like, Chat, Subscribe, Join }

public enum ActionType { KeyPress, Sound, ObsScene, ObsShowSource, ObsHideSource, Delay, Tts, SpotifyRequest, SpotifyControl, RunProgram, ObsCanvasScene }

public class RuleTrigger
{
    public TriggerType Type { get; set; } = TriggerType.Gift;
    public long GiftId { get; set; }
    public string GiftName { get; set; } = "";
    public int MinCoins { get; set; }
    public int MaxCoins { get; set; }
    public int MinLikes { get; set; } = 100;
    public string ChatCommand { get; set; } = "";

    public string Summary()
    {
        switch (Type)
        {
            case TriggerType.Gift:
                var s = "Gift: " + (string.IsNullOrEmpty(GiftName) ? "(none chosen)" : GiftName);
                if (MinCoins > 0) s += $", worth {MinCoins}+ coins";
                return s;
            case TriggerType.AnyGift:
                if (MinCoins > 0 && MaxCoins > 0) return $"Any gift worth {MinCoins} to {MaxCoins} coins";
                if (MinCoins > 0) return $"Any gift worth {MinCoins}+ coins";
                if (MaxCoins > 0) return $"Any gift worth up to {MaxCoins} coins";
                return "Any gift";
            case TriggerType.Follow: return "New follower";
            case TriggerType.Share: return "Stream shared";
            case TriggerType.Like: return $"Every {Math.Max(1, MinLikes)} likes from a viewer";
            case TriggerType.Chat: return string.IsNullOrWhiteSpace(ChatCommand) ? "Any chat message" : $"Chat command {ChatCommand.Trim()}";
            case TriggerType.Subscribe: return "New subscriber";
            case TriggerType.Join: return "Viewer joined";
        }
        return Type.ToString();
    }
}

public class RuleAction
{
    public ActionType Type { get; set; } = ActionType.KeyPress;
    public string Text { get; set; } = "";
    public string Text2 { get; set; } = "";
    public int Number { get; set; }

    public string Summary()
    {
        switch (Type)
        {
            case ActionType.KeyPress: return "Press " + (string.IsNullOrWhiteSpace(Text) ? "(no key set)" : Text);
            case ActionType.Sound: return "Play " + (string.IsNullOrWhiteSpace(Text) ? "(no file)" : System.IO.Path.GetFileName(Text));
            case ActionType.ObsScene: return "OBS scene " + Text;
            case ActionType.ObsCanvasScene: return (string.IsNullOrEmpty(Text2) ? "Vertical" : Text2) + " scene " + Text;
            case ActionType.ObsShowSource: return "OBS show " + Text;
            case ActionType.ObsHideSource: return "OBS hide " + Text;
            case ActionType.Delay: return $"Wait {Number} ms";
            case ActionType.Tts: return "Say \"" + Text + "\"";
            case ActionType.SpotifyRequest: return "Spotify song request";
            case ActionType.SpotifyControl: return "Spotify " + Text + (Text == "SetVolume" ? " " + Number : "");
            case ActionType.RunProgram: return "Run " + (string.IsNullOrWhiteSpace(Text) ? "(no program)" : System.IO.Path.GetFileName(Text));
        }
        return Type.ToString();
    }

    public RuleAction Clone() => new RuleAction { Type = Type, Text = Text, Text2 = Text2, Number = Number };
}

public class Rule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public RuleTrigger Trigger { get; set; } = new RuleTrigger();
    public List<RuleAction> Actions { get; set; } = new List<RuleAction>();
    public int CooldownSeconds { get; set; }

    // For gift combos: run the actions once per gift in the combo (5 roses = 5 times), optionally capped.
    public bool RepeatPerGift { get; set; }
    public int MaxRepeats { get; set; }

    [JsonIgnore] public DateTime LastFired { get; set; }
    [JsonIgnore] public string TriggerSummary => Trigger.Summary();
    [JsonIgnore] public GiftInfo TriggerGift => Trigger.Type == TriggerType.Gift ? GiftDeck.Services.Hub.Gifts?.Find(Trigger.GiftId, Trigger.GiftName) : null;
    [JsonIgnore] public string ActionsSummary =>
        (Actions.Count == 0 ? "No actions yet" : string.Join("   ·   ", Actions.Select(a => a.Summary())))
        + (RepeatPerGift ? "   ·   repeated for each gift in a combo" + (MaxRepeats > 0 ? $" (max {MaxRepeats})" : "") : "");

    public Rule Clone()
    {
        var r = new Rule
        {
            Name = Name,
            Enabled = Enabled,
            CooldownSeconds = CooldownSeconds,
            RepeatPerGift = RepeatPerGift,
            MaxRepeats = MaxRepeats,
            Trigger = new RuleTrigger
            {
                Type = Trigger.Type, GiftId = Trigger.GiftId, GiftName = Trigger.GiftName,
                MinCoins = Trigger.MinCoins, MaxCoins = Trigger.MaxCoins, MinLikes = Trigger.MinLikes, ChatCommand = Trigger.ChatCommand
            },
        };
        foreach (var a in Actions) r.Actions.Add(a.Clone());
        return r;
    }
}

public class LiveEvent
{
    public string Type { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string PictureUrl { get; set; }
    public long GiftId { get; set; }
    public string GiftName { get; set; }
    public int Diamonds { get; set; }
    public int RepeatCount { get; set; } = 1;
    public bool RepeatEnd { get; set; } = true;
    public int GiftType { get; set; }
    public int LikeCount { get; set; }
    public int TotalLikes { get; set; }
    public int ViewerCount { get; set; }
    public int TotalViewers { get; set; }
    public string Comment { get; set; } = "";
    public bool IsTest { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;

    public int Coins => Diamonds * Math.Max(1, RepeatCount);

    public string Describe()
    {
        var who = string.IsNullOrEmpty(Nickname) ? UserId : Nickname;
        switch (Type)
        {
            case "gift":
                var count = RepeatCount > 1 ? $" x{RepeatCount}" : "";
                return $"{who} sent {GiftName}{count} ({Coins} coins)";
            case "follow": return $"{who} followed";
            case "share": return $"{who} shared the stream";
            case "like": return $"{who} sent {LikeCount} likes";
            case "chat": return $"{who}: {Comment}";
            case "subscribe": return $"{who} subscribed";
            case "join": return $"{who} joined";
            default: return $"{who}: {Type}";
        }
    }
}

public class GiftInfo : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Coins { get; set; }

    string _imageUrl;
    public string ImageUrl
    {
        get => _imageUrl;
        set
        {
            if (_imageUrl == value) return;
            _imageUrl = value;
            _image = null;
            _loadingImage = false;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
        }
    }

    [JsonIgnore] public string Display => $"{Name}  ·  {Coins} {(Coins == 1 ? "coin" : "coins")}";

    System.Windows.Media.ImageSource _image;
    bool _loadingImage;

    // The gift's picture, fetched in the background the first time it is asked for.
    [JsonIgnore]
    public System.Windows.Media.ImageSource Image
    {
        get
        {
            if (_image == null && !_loadingImage && !string.IsNullOrEmpty(_imageUrl))
            {
                _loadingImage = true;
                _ = LoadImageAsync();
            }
            return _image;
        }
    }

    async Task LoadImageAsync()
    {
        var img = await GiftDeck.Services.GiftImages.LoadAsync(_imageUrl);
        if (img == null) return;
        _image = img;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Image)));
    }
}

public class AppSettings
{
    public string TikFinityUrl { get; set; } = "ws://localhost:21214/"; // GiftDeck's own TikTok bridge; TikFinity's feed is ws://localhost:21213/
    public string TikFinityExe { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "tikfinity", "TikFinity.exe");
    public bool AutoLaunchTikFinity { get; set; } = false;
    public string BridgeUsername { get; set; } = "";
    public bool SidebarCollapsed { get; set; }
    public string ActiveProfile { get; set; } = "";
    public bool StreakGiftsOnce { get; set; } = true;

    public bool FocusWindowBeforeKeys { get; set; } = false;
    public string FocusWindowTitle { get; set; } = "Grand Theft Auto V";
    public int KeyHoldMs { get; set; } = 60;
    public int SoundVolume { get; set; } = 100;

    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";
    public bool ObsAutoConnect { get; set; } = true;

    public string SpotifyClientId { get; set; } = "";

    public string JamendoClientId { get; set; } = "";
    public string JamendoApiBase { get; set; } = "https://api.jamendo.com/v3.0";
    public int MusicVolume { get; set; } = 40;
    public string MusicGenre { get; set; } = "chillout+lounge";
    public bool MusicInstrumental { get; set; } = true;
    public int SpotifyCallbackPort { get; set; } = 8890;
    public bool SpotifyChatRequests { get; set; } = false;
    public string SpotifyRequestCommand { get; set; } = "!sr";

    public string TtsVoice { get; set; } = "";
    public int TtsRate { get; set; } = 0;
    public int TtsVolume { get; set; } = 100;
    public bool TtsReadChat { get; set; } = false;
    public string TtsChatTemplate { get; set; } = "{user} says {comment}";
    public int TtsMaxChars { get; set; } = 200;
}
