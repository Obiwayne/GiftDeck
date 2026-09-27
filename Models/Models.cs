using System.Text.Json.Serialization;

namespace GiftDeck.Models;

public enum TriggerType { Gift, AnyGift, Follow, Share, Like, Chat, Subscribe, Join }

public enum ActionType { KeyPress, Sound, ObsScene, ObsShowSource, ObsHideSource, Delay, Tts, SpotifyRequest, SpotifyControl, RunProgram, ObsCanvasScene, GameCommand, SpinWheel, Alert }

public class RuleTrigger
{
    public TriggerType Type { get; set; } = TriggerType.Gift;
    public long GiftId { get; set; }
    public string GiftName { get; set; } = "";
    public int MinCoins { get; set; }
    public int MaxCoins { get; set; }
    public int MinLikes { get; set; } = 100;
    public string ChatCommand { get; set; } = "";
    // Which platform's events fire this: "" = any (TikTok or Kick), "tiktok" or "kick".
    public string Platform { get; set; } = "";

    public string Summary() => SummaryText() + (Platform == "kick" ? " (Kick only)" : Platform == "tiktok" ? " (TikTok only)" : "");

    string SummaryText()
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
    // GameCommand: the command's argument values as a JSON object (text values may use {user} etc.).
    public string Args { get; set; } = "";

    // GameCommand: looks up a command's friendly name (target id, command id); set by GameLinkService.
    public static Func<string, string, string> GameCommandName;

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
            case ActionType.GameCommand: return "Game: " + (string.IsNullOrWhiteSpace(Text2) ? "(no command chosen)" : GameCommandName?.Invoke(Text, Text2) ?? Text2);
            case ActionType.SpinWheel: return "Spin the Gift Spinner" + (string.IsNullOrWhiteSpace(Text) ? "" : " " + Text);
            case ActionType.Alert: return "Show alert" + (string.IsNullOrWhiteSpace(Text) ? "" : " " + Text);
        }
        return Type.ToString();
    }

    public RuleAction Clone() => new RuleAction { Type = Type, Text = Text, Text2 = Text2, Number = Number, Args = Args };
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

    // Gift Spinner: null = not on a spinner; otherwise this event is a slice of the wheel and runs when it lands there.
    public Rarity? SpinRarity { get; set; }
    // Which spinner (its id). Blank, or a spinner that was removed, = the first spinner.
    public string SpinnerId { get; set; } = "";

    [JsonIgnore] public string SpinBadge => SpinRarity?.ToString() ?? "";
    [JsonIgnore] public string SpinBadgeColor => SpinRarity is Rarity sr ? Rarities.DefaultColor(sr) : "#9CA3AF";

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
            SpinRarity = SpinRarity,
            SpinnerId = SpinnerId,
            Trigger = new RuleTrigger
            {
                Type = Trigger.Type, GiftId = Trigger.GiftId, GiftName = Trigger.GiftName,
                MinCoins = Trigger.MinCoins, MaxCoins = Trigger.MaxCoins, MinLikes = Trigger.MinLikes, ChatCommand = Trigger.ChatCommand, Platform = Trigger.Platform
            },
        };
        foreach (var a in Actions) r.Actions.Add(a.Clone());
        return r;
    }
}

public class LiveEvent
{
    public string Type { get; set; } = "";
    // Where it came from: "tiktok" or "kick".
    public string Platform { get; set; } = "tiktok";
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
    // When TikTok stamped the event, if the feed says (the bridge does); used to show the delay.
    public DateTime? SentAt { get; set; }

    public int Coins => Diamonds * Math.Max(1, RepeatCount);

    public string Describe()
    {
        var who = string.IsNullOrEmpty(Nickname) ? UserId : Nickname;
        switch (Type)
        {
            case "gift":
                var count = RepeatCount > 1 ? $" x{RepeatCount}" : "";
                return $"{who} sent {GiftName}{count} ({Coins} {(Coins == 1 ? "coin" : "coins")})";
            case "follow": return $"{who} followed";
            case "share": return $"{who} shared the stream";
            case "like": return $"{who} sent {LikeCount} likes";
            case "chat": return $"{who}: {Comment}";
            case "subscribe": return RepeatCount > 1 ? $"{who} gifted {RepeatCount} subs" : !string.IsNullOrEmpty(GiftName) ? $"{who} gifted a sub" : $"{who} subscribed";
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
    // Kick: also read this Kick channel's chat, subs, follows and Kicks gifts (read-only, no login).
    public bool KickEnabled { get; set; }
    public string KickChannel { get; set; } = "";
    public bool SidebarCollapsed { get; set; }
    // Go LIVE scene buttons: "small", "medium" or "large".
    public string SceneButtonSize { get; set; } = "small";
    public string ActiveProfile { get; set; } = "";
    // How GiftDeck reads the LIVE: "page" = its own logged-in TikTok page (works with 18+, no outside service),
    // "bridge" = the bridge connecting anonymously, "tikfinity" = TikFinity's feed.
    public string LiveReader { get; set; } = "bridge";
    public bool TikFinityHidden { get; set; } = true;
    // Ticked on the setup checklist: logged in to TikFinity and its own Events are off (so gifts don't fire twice).
    public bool TikFinityConfirmed { get; set; }
    public bool StreakGiftsOnce { get; set; } = true;

    public bool FocusWindowBeforeKeys { get; set; } = false;
    public string FocusWindowTitle { get; set; } = "Grand Theft Auto V";
    public int KeyHoldMs { get; set; } = 60;
    public int SoundVolume { get; set; } = 100;

    public string ObsHost { get; set; } = "127.0.0.1";
    public int ObsPort { get; set; } = 4455;
    public string ObsPassword { get; set; } = "";
    public bool ObsAutoConnect { get; set; } = true;

    // GiftDeck runs OBS itself, hidden, on the "GiftDeck Portrait" collection and profile (1080x1920),
    // and puts back the user's own collection and profile (remembered here) when it closes OBS.
    public bool ObsManaged { get; set; } = false;
    public string ObsRestoreCollection { get; set; } = "";
    public string ObsRestoreCollectionFile { get; set; } = "";
    public string ObsRestoreProfile { get; set; } = "";
    public string ObsRestoreProfileDir { get; set; } = "";

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
    // Quick mute from the Live panel / Go LIVE. Voice, speed, volume and read-chat are the active
    // profile's (profiles\<name>\tts.json, copied in here on switch); mute, template and cut-off are for all.
    public bool TtsMuted { get; set; } = false;
    // Key for the free Google voices; kept out of the source so it never lands in the public repo.
    public string GoogleTtsKey { get; set; } = "";
}
