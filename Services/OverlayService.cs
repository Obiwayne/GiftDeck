using System.Text.Json;
using System.Text.Json.Serialization;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Keeps goals, countdowns and stream totals up to date from live events, and tells the overlay pages.
public class OverlayService
{
    public OverlayConfig Config { get; private set; } = new OverlayConfig();

    public event Action<string> Broadcast;   // JSON messages for the overlay pages
    public event Action ListsChanged;        // goals or countdowns added/removed (for the UI)

    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    Timer _timer;
    bool _dirty;
    DateTime _lastSave = DateTime.Now;

    public void Load()
    {
        LoadConfig();
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    // Another profile was chosen: swap in its overlays and refresh the pages open in OBS.
    public void Reload()
    {
        LoadConfig();
        PushState();
    }

    void LoadConfig()
    {
        var cfg = Storage.Load<OverlayConfig>(Hub.Profiles.File("overlays.json")) ?? new OverlayConfig();
        if (Config != null) cfg.Port = Config.Port; // the overlay server keeps running on the port it started with
        foreach (var c in cfg.Countdowns)
            if (c.Running && (c.EndsAt == null || c.EndsAt < DateTime.UtcNow)) { c.Running = false; c.EndsAt = null; }
        Config = cfg;
    }

    public void Save()
    {
        Storage.Save(Hub.Profiles.File("overlays.json"), Config);
        _dirty = false;
        _lastSave = DateTime.Now;
    }

    // Call after editing anything in Config from the UI.
    public void Touch()
    {
        _dirty = true;
        PushState();
    }

    void Tick()
    {
        try
        {
            bool running = false;
            foreach (var c in Config.Countdowns.ToList())
            {
                if (!c.Running) continue;
                running = true;
                c.Tick();
                if (c.RemainingSeconds <= 0)
                {
                    c.Pause();
                    c.PausedSeconds = 0;
                    _dirty = true;
                    Log.Write($"Countdown \"{c.Title}\" reached zero");
                }
            }
            if (running) PushState();
            if (_dirty && (DateTime.Now - _lastSave).TotalSeconds > 3) Save();
        }
        catch (Exception e)
        {
            Log.Write("Overlay tick failed: " + e.Message);
        }
    }

    public void OnEvent(LiveEvent e)
    {
        var s = Config.Stats;
        switch (e.Type)
        {
            case "follow":
                s.Follows++;
                Bump(GoalMetric.Follows, 1);
                Extend(c => c.SecondsPerFollow);
                if (Config.AlertFollows) PushAlert("follow", e, null);
                break;
            case "share":
                s.Shares++;
                Bump(GoalMetric.Shares, 1);
                Extend(c => c.SecondsPerShare);
                if (Config.AlertShares) PushAlert("share", e, null);
                break;
            case "like":
                s.Likes += e.LikeCount;
                if (e.TotalLikes > s.Likes) s.Likes = e.TotalLikes;
                Bump(GoalMetric.Likes, e.LikeCount);
                Extend(c => (int)Math.Round(c.SecondsPer100Likes * e.LikeCount / 100.0));
                break;
            case "gift":
                s.Coins += e.Coins;
                s.Gifts += Math.Max(1, e.RepeatCount);
                Bump(GoalMetric.Coins, e.Coins);
                Bump(GoalMetric.Gifts, Math.Max(1, e.RepeatCount));
                Extend(c => c.SecondsPerCoin * e.Coins);
                if (e.Coins >= Config.AlertMinCoins) PushAlert("gift", e, Hub.Gifts.Find(e.GiftId, e.GiftName)?.ImageUrl);
                break;
            case "viewers":
                s.Viewers = e.ViewerCount;
                break;
            default:
                return;
        }
        _dirty = true;
        PushState();
    }

    void Bump(GoalMetric metric, int amount)
    {
        foreach (var g in Config.Goals) if (g.Metric == metric) g.Progress += amount;
    }

    void Extend(Func<Countdown, int> seconds)
    {
        foreach (var c in Config.Countdowns) if (c.Running) c.Add(seconds(c));
    }

    public string StateJson() => JsonSerializer.Serialize(new
    {
        type = "state",
        goals = Config.Goals.Select(g => new { id = g.Id, title = g.Title, metric = g.Metric, target = g.Target, progress = g.Progress }),
        countdowns = Config.Countdowns.Select(c => new { id = c.Id, title = c.Title, remaining = c.RemainingSeconds, running = c.Running }),
        stats = new { follows = Config.Stats.Follows, shares = Config.Stats.Shares, likes = Config.Stats.Likes, coins = Config.Stats.Coins, gifts = Config.Stats.Gifts, viewers = Config.Stats.Viewers },
        style = new { accent = Config.Accent, font = Config.Font, alertSeconds = Config.AlertSeconds },
        menu = new
        {
            title = Config.Menu.Title,
            columns = Math.Clamp(Config.Menu.Columns, 1, 12),
            tileSize = Math.Clamp(Config.Menu.TileSize, 0, 600),
            showLines = Config.Menu.ShowLines,
            lineColor = Config.Menu.LineColor,
            lineWidth = Math.Clamp(Config.Menu.LineWidth, 1, 20),
            showSubtitle = Config.Menu.ShowSubtitle,
            transparent = Config.Menu.Transparent,
            headerColor = Config.Menu.HeaderColor,
            font = Config.Menu.Font,
            tiles = Config.Menu.Tiles.Where(TileVisible).Select(t => new
            {
                id = t.Id,
                label = t.Label,
                subtitle = t.Subtitle,
                kind = TileKind(t),
                image = string.IsNullOrEmpty(ResolveTileImage(t)) ? "" : "/tile-image/" + t.Id,
            }),
        },
    }, Json);

    // ---- Gift menu board ----

    Rule FindRule(Guid? id) => id == null ? null : Hub.Rules.Rules.FirstOrDefault(r => r.Id == id.Value);

    // A tile made from an event only shows while that event is switched on. Custom tiles always show.
    public bool TileVisible(MenuTile t)
    {
        if (t.RuleId == null) return true;
        var rule = FindRule(t.RuleId);
        return rule != null && rule.Enabled;
    }

    public int VisibleTileCount => Config.Menu.Tiles.Count(TileVisible);

    public string TileKind(MenuTile t)
    {
        var rule = FindRule(t.RuleId);
        return rule == null ? "custom" : rule.Trigger.Type.ToString().ToLowerInvariant();
    }

    // The picture a tile should show: its own setting, else the gift picture of the event it came from.
    public string ResolveTileImage(MenuTile t)
    {
        if (!string.IsNullOrWhiteSpace(t.ImageUrl)) return t.ImageUrl.Trim();
        var rule = FindRule(t.RuleId);
        if (rule != null && rule.Trigger.Type == TriggerType.Gift)
            return Hub.Gifts.Find(rule.Trigger.GiftId, rule.Trigger.GiftName)?.ImageUrl;
        return null;
    }

    public MenuTile FindTile(Guid id) => Config.Menu.Tiles.FirstOrDefault(t => t.Id == id);

    static string DefaultSubtitle(Rule rule)
    {
        var t = rule.Trigger;
        switch (t.Type)
        {
            case TriggerType.Gift:
                if (t.MinCoins > 0) return t.MinCoins + " coins";
                var g = Hub.Gifts.Find(t.GiftId, t.GiftName);
                return g == null ? "" : g.Coins + (g.Coins == 1 ? " coin" : " coins");
            case TriggerType.AnyGift: return t.MinCoins > 0 ? t.MinCoins + "+ coins" : "any gift";
            case TriggerType.Like: return t.MinLikes + " likes";
            case TriggerType.Follow: return "follow";
            case TriggerType.Share: return "share";
            case TriggerType.Subscribe: return "subscribe";
            case TriggerType.Join: return "join";
            case TriggerType.Chat: return string.IsNullOrWhiteSpace(t.ChatCommand) ? "chat" : t.ChatCommand.Trim();
        }
        return "";
    }

    // Adds a tile for every enabled event that does not have one yet. Returns how many were added.
    public int AddTilesFromRules()
    {
        int added = 0;
        foreach (var rule in Hub.Rules.Rules.ToList())
        {
            if (!rule.Enabled) continue;
            if (Config.Menu.Tiles.Any(t => t.RuleId == rule.Id)) continue;
            Config.Menu.Tiles.Add(new MenuTile { RuleId = rule.Id, Label = rule.Name.ToUpperInvariant(), Subtitle = DefaultSubtitle(rule) });
            added++;
        }
        if (added > 0) { ListsChanged?.Invoke(); Touch(); }
        return added;
    }

    public MenuTile AddCustomTile()
    {
        var t = new MenuTile { Label = "NEW TILE" };
        Config.Menu.Tiles.Add(t);
        ListsChanged?.Invoke();
        Touch();
        return t;
    }

    public void RemoveTile(MenuTile t)
    {
        Config.Menu.Tiles.Remove(t);
        ListsChanged?.Invoke();
        Touch();
    }

    public void MoveTile(MenuTile t, int delta)
    {
        var list = Config.Menu.Tiles;
        int i = list.IndexOf(t);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.RemoveAt(i);
        list.Insert(j, t);
        ListsChanged?.Invoke();
        Touch();
    }

    public void PushState() => Broadcast?.Invoke(StateJson());

    void PushAlert(string kind, LiveEvent e, string image)
    {
        Broadcast?.Invoke(JsonSerializer.Serialize(new
        {
            type = "alert",
            kind,
            user = e.Nickname,
            gift = e.GiftName ?? "",
            count = Math.Max(1, e.RepeatCount),
            coins = e.Coins,
            image = image ?? "",
            avatar = e.PictureUrl ?? "",
        }, Json));
    }

    public void TestAlert()
    {
        var gift = Hub.Gifts.Gifts.FirstOrDefault(x => x.Name == "Rose") ?? Hub.Gifts.Gifts.FirstOrDefault();
        PushAlert("gift", new LiveEvent { Type = "gift", Nickname = "Test Viewer", GiftName = gift?.Name ?? "Rose", Diamonds = gift?.Coins ?? 1, RepeatCount = 3, IsTest = true }, gift?.ImageUrl);
    }

    public Goal AddGoal()
    {
        var g = new Goal();
        Config.Goals.Add(g);
        ListsChanged?.Invoke();
        Touch();
        return g;
    }

    public void RemoveGoal(Goal g)
    {
        Config.Goals.Remove(g);
        ListsChanged?.Invoke();
        Touch();
    }

    public Countdown AddCountdown()
    {
        var c = new Countdown();
        Config.Countdowns.Add(c);
        ListsChanged?.Invoke();
        Touch();
        return c;
    }

    public void RemoveCountdown(Countdown c)
    {
        Config.Countdowns.Remove(c);
        ListsChanged?.Invoke();
        Touch();
    }

    public void ResetStats()
    {
        Config.Stats.Reset();
        Touch();
    }
}
