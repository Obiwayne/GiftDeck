using System.IO;
using System.Reflection;
using System.Text.Json;
using GiftDeck.Models;
using GiftDeck.Services;

// Checks for events on the Gift Spinner. Everything runs in this process against a scratch data folder:
// no window, no TikTok, no OBS, and nothing is sent to a running GiftDeck.
static class Program
{
    static int _fail, _pass;

    static async Task<int> Main(string[] args)
    {
        var data = Path.Combine(Path.GetTempPath(), "giftdeck-spinner-events-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", data);
        if (args.Length > 1 && args[0] == "render")
        {
            if (!Storage.Dir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return 2;
            var code = Render.Run(args[1]);
            try { Directory.Delete(data, true); } catch { }
            return code;
        }
        return await Run(data);
    }

    static async Task<int> Run(string data)
    {
        // Storage reads GIFTDECK_DATA once; make sure it took before anything is written.
        if (!string.Equals(Path.GetFullPath(Storage.Dir), Path.GetFullPath(data), StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Refusing to run: data folder is " + Storage.Dir);
            return 2;
        }
        Console.WriteLine("Scratch data folder: " + data);

        Pool();
        Weights();
        Disabled();
        OldPrizes();
        RoundTrip();
        OldRulesJson();
        await EndToEnd();

        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        try { Directory.Delete(data, true); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (ok) _pass++; else _fail++;
    }

    static void SetHub(string property, object value) =>
        typeof(Hub).GetProperty(property, BindingFlags.Public | BindingFlags.Static).GetSetMethod(true).Invoke(null, new[] { value });

    static Rule Event(string name, Rarity? rarity, string spinnerId = "", bool enabled = true, params RuleAction[] actions) =>
        new Rule { Name = name, SpinRarity = rarity, SpinnerId = spinnerId, Enabled = enabled, Actions = actions.ToList() };

    static RuleAction Wait(int ms) => new RuleAction { Type = ActionType.Delay, Number = ms };

    static string Labels(IEnumerable<SpinnerEntry> pool) => string.Join(", ", pool.Select(x => x.Label));

    // ---- The pool is the events with a rarity (on this spinner) plus the old prizes ----
    static void Pool()
    {
        Console.WriteLine("Pool = events on the spinner + old prizes");
        var a = new Spinner { Name = "Main" };
        var b = new Spinner { Name = "Second" };
        a.Entries.Add(new SpinnerEntry { Label = "Old prize", Rarity = Rarity.Rare });
        var spinners = new List<Spinner> { a, b };
        var rules = new List<Rule>
        {
            Event("Rose car", Rarity.Common),                                    // blank spinner = first
            Event("On B", Rarity.Rare, b.Id.ToString()),
            Event("Not on a wheel", null),
            Event("Switched off", Rarity.Legendary, enabled: false),
            Event("Spins itself", Rarity.Epic, "", true, new RuleAction { Type = ActionType.SpinWheel }),
            Event("Lost spinner", Rarity.Uncommon, Guid.NewGuid().ToString()),   // removed spinner = first
            Event("By name", Rarity.Epic, "second"),
        };
        var poolA = SpinnerService.PoolFor(a, spinners, rules);
        var poolB = SpinnerService.PoolFor(b, spinners, rules);
        Check(Labels(poolA) == "Rose car, Lost spinner, Old prize", "spinner A: " + Labels(poolA));
        Check(Labels(poolB) == "On B, By name", "spinner B: " + Labels(poolB));
        Check(poolA[0].RuleId == rules[0].Id && poolA[0].Rarity == Rarity.Common, "event slice keeps its event id and rarity");
        Check(poolA[2].RuleId == null, "old prize has no event id");
        Check(ReferenceEquals(poolA[0].Actions, rules[0].Actions), "event slice runs the event's own actions");
        Check(SpinnerService.PoolFor(a, new List<Spinner>(), rules).Count == 1, "no spinners list: only the old prizes");
        Check(SpinnerService.EventEntries(b, spinners, rules).Count == 2, "EventEntries = only the events");
    }

    // ---- Rarity weights over many spins ----
    static void Weights()
    {
        Console.WriteLine("Weights respected over many spins");
        var s = new Spinner();
        var spinners = new List<Spinner> { s };
        var rules = new List<Rule>
        {
            Event("Common", Rarity.Common), Event("Uncommon", Rarity.Uncommon), Event("Rare", Rarity.Rare),
            Event("Epic", Rarity.Epic), Event("Legendary", Rarity.Legendary),
        };
        var pool = SpinnerService.PoolFor(s, spinners, rules);
        const int spins = 200_000;
        var counts = pool.ToDictionary(x => x.Label, _ => 0);
        var rnd = new Random(1234);
        for (int i = 0; i < spins; i++) counts[SpinnerService.Pick(pool, rnd).Label]++;
        foreach (var x in pool)
        {
            double expect = SpinnerService.Chance(pool, x);
            double got = 100.0 * counts[x.Label] / spins;
            Check(Math.Abs(got - expect) < 0.5, $"{x.Label,-10} expected {expect:0.00}% got {got:0.00}%");
        }
        Check(Math.Abs(SpinnerService.Chance(pool, pool[0]) - 50) < 1e-9, "Common alone out of 100 = 50%");

        // Two Commons and a Legendary: 50 / 50 / 2.
        var mixed = SpinnerService.PoolFor(s, spinners, new[] { Event("C1", Rarity.Common), Event("C2", Rarity.Common), Event("L", Rarity.Legendary) });
        Check(SpinnerService.ChanceText(SpinnerService.Chance(mixed, mixed[2])) == "2%", "chance text for 2/102 (1.96%) = " + SpinnerService.ChanceText(SpinnerService.Chance(mixed, mixed[2])));
    }

    // ---- Switched-off events are left off the wheel ----
    static void Disabled()
    {
        Console.WriteLine("Disabled events excluded");
        var s = new Spinner();
        var off = Event("Off", Rarity.Legendary, enabled: false);
        var rules = new List<Rule> { Event("On", Rarity.Common), off };
        var spinners = new List<Spinner> { s };
        Check(Labels(SpinnerService.PoolFor(s, spinners, rules)) == "On", "off event not in the pool");
        var rnd = new Random(7);
        var pool = SpinnerService.PoolFor(s, spinners, rules);
        Check(Enumerable.Range(0, 20_000).All(_ => SpinnerService.Pick(pool, rnd).Label == "On"), "20000 spins never land on it");
        off.Enabled = true;
        Check(Labels(SpinnerService.PoolFor(s, spinners, rules)) == "On, Off", "switched back on: back in the pool");
        Check(SpinnerService.PoolFor(s, spinners, new List<Rule>()).Count == 0, "no events, no prizes: empty pool");
    }

    // ---- Old prize lists still work on their own ----
    static void OldPrizes()
    {
        Console.WriteLine("Old prize lists still work");
        var s = new Spinner();
        s.Entries.Add(new SpinnerEntry { Label = "Big", Rarity = Rarity.Common, Weight = 30 });
        s.Entries.Add(new SpinnerEntry { Label = "Small", Rarity = Rarity.Legendary, Weight = 10 });
        s.Entries.Add(new SpinnerEntry { Label = "Tier", Rarity = Rarity.Rare }); // 0 = tier weight 15
        var pool = SpinnerService.PoolFor(s, new List<Spinner> { s }, new List<Rule>());
        Check(Labels(pool) == "Big, Small, Tier", "pool = the prizes: " + Labels(pool));
        var counts = new Dictionary<string, int> { ["Big"] = 0, ["Small"] = 0, ["Tier"] = 0 };
        var rnd = new Random(99);
        const int spins = 100_000;
        for (int i = 0; i < spins; i++) counts[SpinnerService.Pick(pool, rnd).Label]++;
        Check(Math.Abs(100.0 * counts["Big"] / spins - 30 * 100.0 / 55) < 0.6, $"custom weight 30/55: {100.0 * counts["Big"] / spins:0.0}%");
        Check(Math.Abs(100.0 * counts["Small"] / spins - 10 * 100.0 / 55) < 0.6, $"custom weight 10/55: {100.0 * counts["Small"] / spins:0.0}%");
        Check(Math.Abs(100.0 * counts["Tier"] / spins - 15 * 100.0 / 55) < 0.6, $"tier weight 15/55: {100.0 * counts["Tier"] / spins:0.0}%");

        // An old overlays.json spinner (no new fields anywhere) still loads with its prizes.
        var oldJson = "{\"Spinners\":[{\"Id\":\"" + Guid.NewGuid() + "\",\"Name\":\"Old\",\"SpinSeconds\":5,\"Entries\":[{\"Label\":\"Kickflip\",\"Rarity\":\"Epic\",\"Weight\":0,\"Actions\":[{\"Type\":\"Delay\",\"Number\":5}]}]}]}";
        Directory.CreateDirectory(Storage.Dir);
        File.WriteAllText(Storage.PathFor("old-overlays.json"), oldJson);
        var cfg = Storage.Load<OverlayConfig>("old-overlays.json");
        var sp = cfg?.Spinners.FirstOrDefault();
        Check(sp != null && sp.Entries.Count == 1 && sp.Entries[0].Rarity == Rarity.Epic && sp.Entries[0].Actions.Count == 1, "old overlays.json spinner loads its prize and actions");
    }

    // ---- Rules save and load with the new fields ----
    static void RoundTrip()
    {
        Console.WriteLine("Rule round trip through rules.json");
        var settings = new AppSettings();
        SetHub("Settings", settings);
        var profiles = new ProfileService();
        SetHub("Profiles", profiles);
        profiles.Init();
        var engine = new RulesEngine();
        SetHub("Rules", engine);

        var spinnerId = Guid.NewGuid().ToString();
        engine.Rules.Add(Event("Legendary one", Rarity.Legendary, spinnerId, true, Wait(10)));
        engine.Rules.Add(Event("Plain", null));
        engine.Save();

        var json = File.ReadAllText(Storage.PathFor(profiles.File("rules.json")));
        Check(json.Contains("\"SpinRarity\": \"Legendary\""), "rarity saved by name");
        Check(json.Contains("\"SpinnerId\": \"" + spinnerId + "\""), "spinner id saved");

        var fresh = new RulesEngine();
        SetHub("Rules", fresh);
        fresh.Load();
        Check(fresh.Rules.Count == 2, "two events loaded");
        Check(fresh.Rules[0].SpinRarity == Rarity.Legendary && fresh.Rules[0].SpinnerId == spinnerId, "rarity and spinner came back");
        Check(fresh.Rules[1].SpinRarity == null && fresh.Rules[1].SpinnerId == "", "event not on a wheel stays off it");
        var copy = fresh.Rules[0].Clone();
        Check(copy.SpinRarity == Rarity.Legendary && copy.SpinnerId == spinnerId, "Duplicate keeps the spinner settings");
        Check(fresh.Rules[0].SpinBadge == "Legendary" && fresh.Rules[1].SpinBadge == "", "Events page badge text");

        // The profile export reads rules.json the same way.
        var viaProfile = JsonSerializer.Deserialize<List<Rule>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Check(viaProfile[0].SpinRarity == Rarity.Legendary, "profile export/import options read it too");
    }

    // ---- A rules.json from before this change ----
    static void OldRulesJson()
    {
        Console.WriteLine("Old rules.json without the new fields");
        var old = """
        [
          {
            "Id": "0b1c1e9e-7e1f-4a53-9d7c-3c7d2f7c1a11",
            "Name": "Rose",
            "Enabled": true,
            "Trigger": { "Type": "Gift", "GiftId": 5655, "GiftName": "Rose", "MinCoins": 0, "MaxCoins": 0, "MinLikes": 100, "ChatCommand": "" },
            "Actions": [ { "Type": "KeyPress", "Text": "F5", "Text2": "", "Number": 0 } ],
            "CooldownSeconds": 0,
            "RepeatPerGift": false,
            "MaxRepeats": 0
          },
          { "Name": "Nulls", "SpinRarity": null, "SpinnerId": null, "Actions": [] }
        ]
        """;
        File.WriteAllText(Storage.PathFor(Hub.Profiles.File("rules.json")), old);
        var engine = new RulesEngine();
        SetHub("Rules", engine);
        engine.Load();
        Check(engine.Rules.Count == 2, "old file loads (" + engine.Rules.Count + " events)");
        Check(engine.Rules[0].SpinRarity == null && engine.Rules[0].SpinnerId == "", "no rarity: not on a spinner");
        Check(engine.Rules[0].Actions.Count == 1 && engine.Rules[0].Trigger.GiftName == "Rose", "the rest is unchanged");
        var s = new Spinner();
        Check(SpinnerService.PoolFor(s, new List<Spinner> { s }, engine.Rules).Count == 0, "old events don't land on a wheel by themselves");
        engine.Rules[1].SpinRarity = Rarity.Rare; // SpinnerId null from the file
        Check(SpinnerService.PoolFor(s, new List<Spinner> { s }, engine.Rules).Count == 1, "null spinner id = first spinner");
    }

    // ---- A real spin: overlay message, then the event's actions run through the rules engine ----
    static async Task EndToEnd()
    {
        Console.WriteLine("Spin end to end (overlay message + event actions, no window)");
        var overlays = new OverlayService();
        SetHub("Overlays", overlays);
        var engine = new RulesEngine();
        SetHub("Rules", engine);
        var spinners = new SpinnerService();
        SetHub("Spinners", spinners);

        var s = new Spinner { Name = "Test wheel", SpinSeconds = 2 };
        overlays.Config.Spinners.Add(s);

        var viewer = new LiveEvent { Type = "gift", Nickname = "Tester", GiftName = "Rose", Diamonds = 1, IsTest = true };
        try { await spinners.SpinAsync(s.Id.ToString(), viewer, true); Check(false, "empty spinner should say so"); }
        catch (Exception ex) { Check(ex.Message.Contains("nothing on it yet"), "empty spinner: " + ex.Message); }

        var ev = Event("Kickflip time", Rarity.Epic, "", true, Wait(5));
        engine.Rules.Add(ev);
        engine.Rules.Add(Event("Off one", Rarity.Common, enabled: false, actions: new[] { Wait(5) }));

        var messages = new List<string>();
        overlays.Broadcast += m => { lock (messages) messages.Add(m); };
        var logs = new List<string>();
        Log.Written += l => { lock (logs) logs.Add(l); };

        overlays.PushState();
        var state = JsonDocument.Parse(messages.Last()).RootElement;
        var wheel = state.GetProperty("spinners")[0].GetProperty("entries");
        Check(wheel.GetArrayLength() == 1 && wheel[0].GetProperty("label").GetString() == "Kickflip time" && wheel[0].GetProperty("rarity").GetString() == "epic",
            "overlay state shows the event on the wheel: " + wheel.ToString());

        var won = await spinners.SpinAsync(s.Id.ToString(), viewer, true);
        Check(won.RuleId == ev.Id, "landed on the event");
        var spin = messages.Select(m => JsonDocument.Parse(m).RootElement).FirstOrDefault(m => m.GetProperty("type").GetString() == "spin");
        Check(spin.ValueKind == JsonValueKind.Object && spin.GetProperty("index").GetInt32() == 0
              && spin.GetProperty("entries")[0].GetProperty("label").GetString() == "Kickflip time", "spin message sent to the overlay");
        await Task.Delay(200);
        Check(logs.Any(l => l.Contains("\"Test wheel: Kickflip time\" fired for Tester")), "the event's actions ran through the rules engine");
        Check(logs.Any(l => l.Contains("landed on Kickflip time (Epic)")), "landing logged");

        // "Spin now" without running actions.
        logs.Clear();
        await spinners.SpinAsync(s.Id.ToString(), viewer, false);
        await Task.Delay(100);
        Check(!logs.Any(l => l.Contains("fired for")), "test spin with the box unticked runs nothing");
    }
}
