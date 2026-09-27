using GiftDeck.Models;

// The GTA pack's starter preset: cheap gifts do small, funny things, expensive gifts bring the chaos.
// Built into Packs\gta5\presets\gta5-starter.giftdeck by "GtaPackCheck preset".
static class Presets
{
    public const string Name = "GTA V Gift Chaos";

    // Chaos Mod V effect ids used here, exactly as registered in gta-chaos-mod/ChaosModV
    // (ChaosMod/Effects/db/**: REGISTER_EFFECT ... .Id = "...").
    public static readonly Dictionary<string, string> ChaosEffects = new Dictionary<string, string>
    {
        ["player_kickflip"] = "Kickflip",               // Player/PlayerKickflip.cpp
        ["player_ignite"] = "Ignite Player",            // Player/PlayerIgnitePlayer.cpp
        ["misc_fireworks"] = "Fireworks!",              // Misc/MiscFireworks.cpp
        ["player_moneydrops"] = "Money Rain",           // Misc/MiscMoneyRain.cpp
        ["meteorrain"] = "Meteor Shower",               // Misc/MiscMeteorRain.cpp
        ["misc_airstrike"] = "Airstrike Inbound",       // Misc/MiscAirstrike.cpp
        ["peds_killerclowns"] = "Killer Clowns",        // Peds/PedsKillerClowns.cpp
        ["world_whalerain"] = "Whale Rain",             // Misc/MiscWhaleRain.cpp
    };

    // Coins per gift, from GiftDeck's built-in gift list (Services/GiftCatalog.cs)
    static readonly Dictionary<string, (int Coins, long Id)> Gifts = new Dictionary<string, (int, long)>
    {
        ["Rose"] = (1, 5655), ["TikTok"] = (1, 5269), ["GG"] = (1, 6064), ["Ice Cream Cone"] = (1, 5827), ["Heart Me"] = (1, 7934),
        ["Finger Heart"] = (5, 5487), ["Perfume"] = (20, 5658), ["Doughnut"] = (30, 5879), ["Paper Crane"] = (99, 0),
        ["Hand Hearts"] = (100, 0), ["Confetti"] = (100, 0), ["Sunglasses"] = (199, 5509), ["Corgi"] = (299, 0),
        ["Money Gun"] = (500, 0), ["Swan"] = (699, 0), ["Train"] = (899, 0), ["Galaxy"] = (1000, 11046), ["Fireworks"] = (1088, 0),
        ["Motorcycle"] = (2988, 0), ["Private Jet"] = (4888, 0), ["Sports Car"] = (7000, 0), ["Lion"] = (29999, 0),
    };

    public static int GiftCoins(string gift) => Gifts.TryGetValue(gift, out var g) ? g.Coins : 0;

    static RuleAction Gta(string command, string args = "") =>
        new RuleAction { Type = ActionType.GameCommand, Text = "gta5:giftdeck", Text2 = command, Args = args };

    static RuleAction Chaos(string effect) =>
        new RuleAction { Type = ActionType.GameCommand, Text = "gta5:chaosmod", Text2 = effect };

    public static (List<Rule> Rules, OverlayConfig Overlays) Starter()
    {
        var rules = new List<Rule>();
        // Fixed ids so rebuilding the preset gives the same file
        int n = 0;
        void Add(string gift, string name, int cooldown, int repeatMax, params RuleAction[] actions)
        {
            var (coins, id) = Gifts[gift];
            rules.Add(new Rule
            {
                Id = new Guid($"6d7a0000-0000-4000-8000-{++n:D12}"),
                Name = name,
                Trigger = new RuleTrigger { Type = TriggerType.Gift, GiftName = gift, GiftId = id },
                Actions = actions.ToList(),
                CooldownSeconds = cooldown,
                RepeatPerGift = repeatMax > 0,
                MaxRepeats = repeatMax,
            });
        }

        // 1 coin: small and silly, fine to spam (combos repeat, capped)
        Add("Rose", "Kickflip", 0, 5, Chaos("player_kickflip"));
        Add("TikTok", "Pigeons", 0, 3, Gta("spawn_pigeons", """{"count":6}"""));
        Add("GG", "Heal + armour", 0, 1, Gta("heal_armour"));
        Add("Ice Cream Cone", "+1 wanted star", 0, 5, Gta("wanted_up"));
        Add("Heart Me", "Lose the cops", 0, 1, Gta("wanted_clear"));
        // A few coins: things that change the next minute
        Add("Finger Heart", "Random car", 0, 3, Gta("spawn_random_vehicle"));
        Add("Perfume", "Random weapon", 0, 3, Gta("give_random_weapon"));
        Add("Doughnut", "Set on fire", 5, 0, Chaos("player_ignite"));
        Add("Paper Crane", "Skydive", 10, 0, Gta("skydive", """{"height":800}"""));
        Add("Hand Hearts", "3 attackers", 0, 0, Gta("spawn_attackers", """{"count":3,"weapon":"pistol"}"""));
        Add("Confetti", "Fireworks", 0, 0, Chaos("misc_fireworks"));
        Add("Sunglasses", "Drunk", 0, 0, Gta("drunk", """{"seconds":30}"""));
        Add("Corgi", "Poodle party", 0, 0, Gta("spawn_poodles", """{"count":8}"""));
        // Hundreds of coins: proper chaos
        Add("Money Gun", "Money rain", 0, 0, Chaos("player_moneydrops"), Gta("add_money", """{"amount":100000}"""));
        Add("Swan", "Killer monkeys", 0, 0, Gta("monkey_killers", """{"count":4}"""));
        Add("Train", "5 stars + moto cops", 0, 0, Gta("wanted_max"), Gta("moto_cops", """{"count":3}"""));
        Add("Galaxy", "Meteor shower", 0, 0, Chaos("meteorrain"));
        Add("Fireworks", "Airstrike", 0, 0, Chaos("misc_airstrike"));
        Add("Motorcycle", "Biker gang", 0, 0, Gta("moto_bandits", """{"count":4}"""));
        Add("Private Jet", "Fighter jet", 0, 0, Gta("spawn_vehicle", """{"model":"lazer","enter":"yes"}"""));
        Add("Sports Car", "Tuned supercar", 0, 0, Gta("spawn_vehicle", """{"model":"zentorno","enter":"yes"}"""), Gta("tune_full"));
        Add("Lion", "Doomsday", 0, 0, Chaos("peds_killerclowns"), Chaos("world_whalerain"), Chaos("meteorrain"),
            Gta("earthquake", """{"seconds":30}"""), Gta("set_weather", """{"weather":"THUNDER"}"""), Gta("wanted_max"));

        // Gift board overlay (TikTok LIVE Studio / OBS): one tile per event, the gift's own picture
        var overlays = new OverlayConfig();
        overlays.Stats.Started = new DateTime(2026, 9, 27);
        overlays.Menu.Title = "GIFTS CONTROL MY GAME";
        overlays.Menu.Columns = 4;
        overlays.Menu.HeaderColor = "#1D8F3A"; // GTA green
        foreach (var r in rules)
        {
            int coins = GiftCoins(r.Trigger.GiftName);
            overlays.Menu.Tiles.Add(new MenuTile
            {
                Id = new Guid($"6d7a0001-0000-4000-8000-{rules.IndexOf(r) + 1:D12}"),
                RuleId = r.Id,
                Label = r.Name.ToUpperInvariant(),
                Subtitle = coins + (coins == 1 ? " coin" : " coins"),
            });
        }
        return (rules, overlays);
    }
}
