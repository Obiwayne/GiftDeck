using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using GiftDeck.Models;
using GiftDeck.Services;

// Builds Packs/minecraft/presets/minecraft-chaos.giftdeck with GiftDeck's own model classes, in the same zip
// layout ProfileService.Export writes (profile.json, rules.json, overlays.json).
static class Preset
{
    const string T = MinecraftTarget.TargetId;

    static RuleAction Cmd(string command, object args) => new RuleAction
    {
        Type = ActionType.GameCommand, Text = T, Text2 = command,
        Args = JsonSerializer.Serialize(args),
    };

    static Rule Gift(string name, string gift, long id, bool perGift, int max, params RuleAction[] actions)
    {
        var r = new Rule { Name = name, Trigger = new RuleTrigger { Type = TriggerType.Gift, GiftName = gift, GiftId = id }, RepeatPerGift = perGift, MaxRepeats = max };
        r.Actions.AddRange(actions);
        return r;
    }

    static Rule Other(string name, TriggerType type, params RuleAction[] actions)
    {
        var r = new Rule { Name = name, Trigger = new RuleTrigger { Type = type }, CooldownSeconds = 5 };
        r.Actions.AddRange(actions);
        return r;
    }

    public static List<Rule> Rules() => new List<Rule>
    {
        Gift("Zombie with your name", "Rose", 5655, true, 10,
            Cmd("summon_mob", new { mob = "zombie", count = "1", name = "{user}" })),
        Gift("Speed boost", "GG", 6064, false, 0,
            Cmd("effect", new { effect = "speed", seconds = "20", amplifier = "2" })),
        Gift("Heal and feed", "Ice Cream Cone", 5827, false, 0,
            Cmd("heal", new { }),
            Cmd("actionbar", new { text = "{user} healed you" })),
        Gift("A cat named after you", "Heart Me", 7934, true, 5,
            Cmd("summon_mob", new { mob = "cat", count = "1", name = "{user}" })),
        Gift("Lightning strike", "Finger Heart", 5487, true, 5,
            Cmd("lightning", new { count = "1" })),
        Gift("Launch into the air", "Perfume", 5658, false, 0,
            Cmd("launch", new { power = "30" }),
            Cmd("title", new { title = "YEET", subtitle = "{user} sent {gift}" })),
        Gift("Three creepers", "Doughnut", 5879, false, 0,
            Cmd("summon_mob", new { mob = "creeper", count = "3", name = "{user}" })),
        Gift("Blindness", "Sunglasses", 5509, false, 0,
            Cmd("effect", new { effect = "blindness", seconds = "10", amplifier = "0" }),
            Cmd("title", new { title = "{user}", subtitle = "turned off the lights" })),
        Gift("Night and a zombie horde", "Corgi", 0, false, 0,
            Cmd("time", new { time = "midnight" }),
            Cmd("summon_mob", new { mob = "zombie", count = "6", name = "{user}" })),
        Gift("Anvil rain", "Money Gun", 0, false, 0,
            Cmd("anvil_rain", new { count = "8" })),
        Gift("TNT party", "Galaxy", 11046, false, 0,
            Cmd("title", new { title = "RUN", subtitle = "{user} sent TNT" }),
            Cmd("tnt", new { count = "10", fuse = "80" })),
        Gift("Teleport somewhere random", "Glowing Jellyfish", 8972, false, 0,
            Cmd("teleport_random", new { radius = "300" })),
        Gift("Charged creeper storm", "Lion", 0, false, 0,
            Cmd("weather", new { weather = "thunder" }),
            Cmd("charged_creeper", new { count = "3", name = "{user}" })),
        Other("New follower", TriggerType.Follow,
            Cmd("actionbar", new { text = "{user} followed!" })),
        Other("Stream shared", TriggerType.Share,
            Cmd("give_item", new { item = "golden_apple", amount = "1" }),
            Cmd("actionbar", new { text = "{user} shared the stream: golden apple!" })),
    };

    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(string zipPath)
    {
        var rules = Rules();
        var overlays = new OverlayConfig { Accent = "#3C8527" };
        overlays.Menu.Title = "GIFTS CONTROL MY WORLD";
        overlays.Menu.HeaderColor = "#3C8527";
        overlays.Menu.Columns = 4;
        foreach (var r in rules.Where(r => r.Trigger.Type == TriggerType.Gift))
            overlays.Menu.Tiles.Add(new MenuTile { RuleId = r.Id, Label = r.Name, Subtitle = r.Trigger.GiftName });

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(zipPath)));
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }
        Entry("profile.json", JsonSerializer.Serialize(new { app = "GiftDeck", name = "Minecraft Chaos", version = 1 }));
        Entry("rules.json", JsonSerializer.Serialize(rules, Json));
        Entry("overlays.json", JsonSerializer.Serialize(overlays, Json));
        Console.WriteLine($"Wrote {zipPath}: {rules.Count} events");
    }
}
