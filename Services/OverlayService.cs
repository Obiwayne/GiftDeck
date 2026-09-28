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
        // Tests still show their alert so it can be previewed, but never move goals, timers or top gifters.
        if (e.IsTest)
        {
            if (e.Type == "follow" && Config.AlertFollows) PushAlert("follow", e, null);
            else if (e.Type == "share" && Config.AlertShares) PushAlert("share", e, null);
            else if (e.Type == "gift" && e.Coins >= Config.AlertMinCoins) PushAlert("gift", e, Hub.Gifts.Find(e.GiftId, e.GiftName)?.ImageUrl);
            return;
        }
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
                Extend(c =>
                {
                    c.LikeCarry += c.SecondsPer100Likes * e.LikeCount / 100.0;
                    var whole = (int)Math.Floor(c.LikeCarry);
                    c.LikeCarry -= whole;
                    return whole;
                });
                break;
            case "gift":
                s.Coins += e.Coins;
                s.Gifts += Math.Max(1, e.RepeatCount);
                Bump(GoalMetric.Coins, e.Coins);
                Bump(GoalMetric.Gifts, Math.Max(1, e.RepeatCount));
                Extend(c => c.SecondsPerCoin * e.Coins);
                AddGifter(e);
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
        spinners = Config.Spinners.Select(s => new { id = s.Id, name = s.Name, hideWhenIdle = s.HideWhenIdle, entries = SpinEntries(SpinnerService.Pool(s)) }),
        giftList = new { title = Config.Templates.GiftListTitle, items = GiftList() },
        strip = new { title = Config.Templates.StripTitle, top = TopGifters(3), goal = NextGoal() },
        allInOne = Config.AllInOne,
    }, Json);

    // ---- Stream tools: Gift Spinner, custom alerts, gift list and top gifters templates ----

    static object SpinEntries(IEnumerable<SpinnerEntry> entries) =>
        entries.Select(x => new { label = x.Label, rarity = x.Rarity, color = x.EffectiveColor, image = x.RuleId != null && !string.IsNullOrEmpty(x.Image) ? "/rule-image/" + x.RuleId : "" }).ToList();

    public void PushSpin(Spinner s, List<SpinnerEntry> entries, int index, LiveEvent e)
    {
        Broadcast?.Invoke(JsonSerializer.Serialize(new
        {
            type = "spin",
            spinner = s.Id,
            name = s.Name,
            entries = SpinEntries(entries),
            index,
            seconds = s.SpinSeconds,
            revealMs = SpinnerService.RevealMs,
            user = e.Nickname ?? "",
            avatar = e.PictureUrl ?? "",
        }, Json));
    }

    public void PushCustomAlert(AlertDef a, LiveEvent e)
    {
        string media = "", mediaType = "";
        if (!string.IsNullOrWhiteSpace(a.Media))
        {
            media = "/alert-media/" + a.Id + "?v=" + DateTime.UtcNow.Ticks;
            var ext = Path.GetExtension(a.Media.Trim().Split('?')[0]).ToLowerInvariant();
            mediaType = ext == ".webm" || ext == ".mp4" || ext == ".mov" ? "video" : "image";
        }
        Broadcast?.Invoke(JsonSerializer.Serialize(new
        {
            type = "alert",
            kind = "custom",
            id = a.Id,
            text = RulesEngine.Template(a.Text, e),
            media,
            mediaType,
            seconds = a.Seconds,
            interrupt = a.Interrupt,
            fullScreen = a.FullScreen,
            // Green-screen removal (videos only): colour blank = pick it from the video's corners.
            key = a.KeyGreen && mediaType == "video" ? new { color = a.KeyColor?.Trim() ?? "", strength = a.KeyStrength, softness = a.KeySoftness } : null,
            user = e.Nickname ?? "",
            avatar = e.PictureUrl ?? "",
        }, Json));
    }

    public AlertDef FindAlert(Guid id) => Config.CustomAlerts.FirstOrDefault(a => a.Id == id);

    void AddGifter(LiveEvent e)
    {
        if (e.Coins <= 0) return;
        var key = string.IsNullOrEmpty(e.UserId) ? e.Nickname ?? "" : e.UserId;
        var list = Config.Stats.Gifters;
        lock (list)
        {
            var g = list.FirstOrDefault(x => x.UserId == key);
            if (g == null) list.Add(g = new GifterTotal { UserId = key });
            g.Coins += e.Coins;
            if (!string.IsNullOrEmpty(e.Nickname)) g.Name = e.Nickname;
            if (!string.IsNullOrEmpty(e.PictureUrl)) g.Avatar = e.PictureUrl;
        }
    }

    public List<GifterTotal> TopGifters(int n)
    {
        var list = Config.Stats.Gifters;
        lock (list) return list.OrderByDescending(x => x.Coins).Take(n).Select(x => new GifterTotal { Name = x.Name, Coins = x.Coins, Avatar = x.Avatar }).ToList();
    }

    // The first goal that isn't reached yet, else the last one (so the strip always has something to show).
    object NextGoal()
    {
        var g = Config.Goals.FirstOrDefault(x => x.Progress < x.Target) ?? Config.Goals.LastOrDefault();
        return g == null ? null : new { title = g.Title, metric = g.Metric, target = g.Target, progress = g.Progress };
    }

    // Every enabled gift event, cheapest first: the gift's picture, its price and what it does.
    public List<GiftListItem> GiftList()
    {
        var rules = Hub.Rules?.Rules.ToList() ?? new List<Rule>();
        var items = new List<GiftListItem>();
        foreach (var r in rules)
        {
            if (!r.Enabled) continue;
            var t = r.Trigger;
            if (t.Type == TriggerType.Gift)
            {
                var g = Hub.Gifts?.Find(t.GiftId, t.GiftName);
                items.Add(new GiftListItem
                {
                    Id = r.Id, Label = r.Name, Gift = g?.Name ?? t.GiftName,
                    Coins = t.MinCoins > 0 ? t.MinCoins : g?.Coins ?? 0,
                    Image = string.IsNullOrEmpty(g?.ImageUrl) ? "" : "/rule-image/" + r.Id,
                });
            }
            else if (t.Type == TriggerType.AnyGift)
                items.Add(new GiftListItem { Id = r.Id, Label = r.Name, Gift = "Any gift", Coins = t.MinCoins, AnyGift = true });
        }
        items = items.OrderBy(x => x.Coins).ThenBy(x => x.Label, StringComparer.OrdinalIgnoreCase).ToList();
        if (Config.Templates.GiftListMax > 0) items = items.Take(Config.Templates.GiftListMax).ToList();
        return items;
    }

    public class GiftListItem
    {
        public Guid Id { get; set; }
        public string Label { get; set; }
        public string Gift { get; set; }
        public int Coins { get; set; }
        public string Image { get; set; }
        public bool AnyGift { get; set; }
    }

    // The gift picture for a gift event (for the gift list template).
    public string ResolveRuleImage(Guid ruleId)
    {
        var rule = FindRule(ruleId);
        return rule == null ? null : Hub.Gifts?.Find(rule.Trigger.GiftId, rule.Trigger.GiftName)?.ImageUrl;
    }

    public Spinner AddSpinner()
    {
        var s = new Spinner { Name = Config.Spinners.Count == 0 ? "Gift Spinner" : "Gift Spinner " + (Config.Spinners.Count + 1) };
        Config.Spinners.Add(s);
        ListsChanged?.Invoke();
        Touch();
        return s;
    }

    public void RemoveSpinner(Spinner s)
    {
        Config.Spinners.Remove(s);
        ListsChanged?.Invoke();
        Touch();
    }

    public AlertDef AddCustomAlert()
    {
        var a = new AlertDef { Name = "Alert " + (Config.CustomAlerts.Count + 1) };
        Config.CustomAlerts.Add(a);
        ListsChanged?.Invoke();
        Touch();
        return a;
    }

    public void RemoveCustomAlert(AlertDef a)
    {
        Config.CustomAlerts.Remove(a);
        ListsChanged?.Invoke();
        Touch();
    }

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
