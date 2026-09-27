using System.IO;
using System.Net.Http;
using System.Text.Json;
using GiftDeck.Models;
using GiftDeck.Services;

// Checks for GiftDeck's Kick reader. Read-only: it only listens to Kick's public chat feed, never posts.
static class Program
{
    static int _fail;

    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        // Logs go to a scratch data folder, never the user's GiftDeck folder (set before anything touches Storage).
        var data = Path.Combine(Path.GetTempPath(), "giftdeck-kick-test");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", data);

        var mode = args.Length > 0 ? args[0] : "unit";
        if (mode == "unit") return Unit(args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "samples.jsonl"));
        if (mode == "live" && args.Length > 1) return await Live(args[1], args.Length > 2 ? int.Parse(args[2]) : 60);
        Console.WriteLine("usage: unit [samples.jsonl] | live <channel> [seconds]");
        return 2;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    static string Frame(string ev, string channel, object data) =>
        JsonSerializer.Serialize(new { @event = ev, data = JsonSerializer.Serialize(data), channel });

    // ---------------- unit ----------------

    static int Unit(string samplesPath)
    {
        Console.WriteLine("Real captured payloads (" + Path.GetFileName(samplesPath) + ")");
        var lines = File.ReadAllLines(samplesPath).Where(l => l.Trim().Length > 0).ToList();
        var parser = new KickParser();
        var byType = new Dictionary<string, int>();
        var firstOf = new Dictionary<string, LiveEvent>();
        foreach (var l in lines)
            foreach (var e in parser.ParseFrame(l))
            {
                byType[e.Type] = byType.GetValueOrDefault(e.Type) + 1;
                firstOf.TryAdd(e.Type, e);
                if (e.Platform != "kick") Check(false, "platform is kick: " + e.Describe());
            }
        foreach (var e in firstOf.Values) Console.WriteLine("  e.g. " + e.Describe());
        Console.WriteLine($"  {lines.Count} frames -> " + string.Join(", ", byType.Select(kv => kv.Key + " " + kv.Value)));
        Check(byType.GetValueOrDefault("chat") > 0, "chat messages parsed");
        Check(byType.GetValueOrDefault("gift") > 0, "Kicks gifts parsed");
        Check(byType.GetValueOrDefault("subscribe") > 0, "subscription parsed");

        Console.WriteLine("Chat");
        var chat = Pick(lines, "ChatMessageEvent", "[emote:");
        if (chat != null)
        {
            var e = new KickParser().ParseFrame(chat).Single();
            Check(e.Type == "chat" && e.Nickname.Length > 0, $"chat from {e.Nickname}: \"{e.Comment}\"");
            Check(!e.Comment.Contains("[emote:"), "emote codes become their names");
            Check(e.SentAt != null, "sent time read");
        }
        Check(KickParser.CleanText("[emote:37225:KEKLEO] hi [emote:1:x]") == "KEKLEO hi x", "CleanText");

        Console.WriteLine("Kicks gifts");
        var gift = Pick(lines, "KicksGifted", "rage_quit") ?? Pick(lines, "KicksGifted", "");
        {
            var e = new KickParser().ParseFrame(gift).Single();
            Check(e.Type == "gift" && e.Diamonds > 0 && e.Coins == e.Diamonds, $"{e.Describe()}");
            Check(e.RepeatEnd && e.RepeatCount == 1, "counts once (no TikTok-style streak)");
        }

        Console.WriteLine("Subscriptions");
        var sub = Pick(lines, "SubscriptionEvent", "");
        {
            var e = new KickParser().ParseFrame(sub).Single();
            Check(e.Type == "subscribe" && e.Nickname.Length > 0, e.Describe());
        }
        // Frames Kick sends alongside a sub that must NOT count as a second sub.
        foreach (var dupe in new[] { "ChannelSubscriptionEvent", "NewSubscriberUpdatedEvent", "NewActivityFeedEvent", "ChatMessageSentEvent", "KicksLeaderboardUpdated" })
        {
            var f = Pick(lines, dupe, "");
            if (f != null) Check(new KickParser().ParseFrame(f).Count == 0, dupe + " ignored (no double count)");
        }

        Console.WriteLine("Gifted subs (a real captured one)");
        {
            var real = Pick(lines, "GiftedSubscriptionsEvent", "gifter_username");
            var e = new KickParser().ParseFrame(real).Single();
            Check(e.Type == "subscribe" && e.Nickname.Length > 0 && e.RepeatCount >= 1, e.Describe() + ", {count}=" + RulesEngine.Template("{count}", e));
            var stop = Pick(lines, "StopStreamBroadcast", "");
            bool? live = null;
            var p = new KickParser();
            p.LiveChanged += l => live = l;
            Check(p.ParseFrame(stop).Count == 0 && live == false, "real StopStreamBroadcast -> not live");
        }

        Console.WriteLine("Gifted subs, older name and big gifts in chunks (shapes from kick.com's JS; not seen live)");
        {
            var p = new KickParser();
            var old = Frame(@"App\Events\GiftedSubscriptionsEvent", "chatrooms.1.v2", new { chatroom_id = 1, gifted_usernames = new[] { "a", "b", "c" }, gifter_username = "bob", gifter_total = 40 });
            var e = p.ParseFrame(old).Single();
            Check(e.Type == "subscribe" && e.Nickname == "bob" && e.RepeatCount == 3, e.Describe() + " {count}=" + RulesEngine.Template("{count}", e));
            var again = Frame("GiftedSubscriptionsEvent", "chatroom_1", new { chatroom_id = 1, gifted_usernames = new[] { "a", "b", "c" }, gifter_username = "bob" });
            Check(p.ParseFrame(again).Count == 0, "same gift under the newer name isn't counted twice");

            var c1 = Frame("GiftedSubscriptionsEvent", "chatroom_1", new { gifter_username = "amy", gifted_usernames = new[] { "a", "b" }, chunk_details = new { correlation_id = "x1", chunk_index = 0, total_chunks = 2 } });
            var c2 = Frame("GiftedSubscriptionsEvent", "chatroom_1", new { gifter_username = "amy", gifted_usernames = new[] { "c", "d", "e" }, chunk_details = new { correlation_id = "x1", chunk_index = 1, total_chunks = 2 } });
            Check(p.ParseFrame(c1).Count == 0, "first chunk waits");
            var total = p.ParseFrame(c2).Single();
            Check(total.RepeatCount == 5 && total.Nickname == "amy", "chunks add up: " + total.Describe());
        }

        Console.WriteLine("Follows from a follower goal (real captured goal updates)");
        {
            var goals = lines.Where(l => l.Contains("\"event\":\"GoalProgressUpdateEvent\"") && l.Contains("followers")).ToList();
            var ch = goals.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("channel").GetString()).GroupBy(c => c).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            var mine = goals.Where(l => l.Contains("\"channel\":\"" + ch + "\"")).ToList();
            var values = mine.Select(l => JsonDocument.Parse(JsonDocument.Parse(l).RootElement.GetProperty("data").GetString()).RootElement.GetProperty("current_value").GetInt64()).ToList();
            var p = new KickParser();
            var first = p.ParseFrame(mine[0]);
            int follows = mine.Skip(1).Sum(l => p.ParseFrame(l).Count(e => e.Type == "follow"));
            long expected = 0, top = values[0];
            foreach (var v in values.Skip(1)) { if (v > top) expected += Math.Min(5, v - top); top = Math.Max(top, v); }
            Check(first.Count == 0, "first goal update is only the starting count");
            Check(follows == expected && follows > 0, $"{ch}: goal went {values[0]} -> {values[^1]} over {mine.Count} updates = {follows} follows (expected {expected})");
        }

        Console.WriteLine("Follows (named FollowersUpdated: payload shape from community clients; none arrived during the capture)");
        {
            var f = Frame(@"App\Events\FollowersUpdated", "channel.1", new { followersCount = 10, channel_id = 1, username = "newfan", created_at = 1, followed = true });
            var e = new KickParser().ParseFrame(f).Single();
            Check(e.Type == "follow" && e.Nickname == "newfan", e.Describe());
            var un = Frame(@"App\Events\FollowersUpdated", "channel.1", new { followersCount = 9, channel_id = 1, username = "newfan", followed = false });
            Check(new KickParser().ParseFrame(un).Count == 0, "unfollow ignored");
        }

        Console.WriteLine("Duplicates and live status");
        {
            var p = new KickParser();
            Check(p.ParseFrame(chat ?? gift).Count == 1 && p.ParseFrame(chat ?? gift).Count == 0, "same message id only once");
            bool? live = null;
            p.LiveChanged += l => live = l;
            p.ParseFrame(Frame(@"App\Events\StreamerIsLive", "channel.1", new { livestream = new { id = 1 } }));
            Check(live == true, "StreamerIsLive -> live");
            p.ParseFrame(Frame(@"App\Events\StopStreamBroadcast", "channel.1", new { livestream = new { id = 1 } }));
            Check(live == false, "StopStreamBroadcast -> not live");
        }

        Console.WriteLine("Rules engine (rules with no actions; nothing is sent anywhere)");
        {
            var engine = new RulesEngine();
            var anyChat = new Rule { Name = "any chat", Trigger = new RuleTrigger { Type = TriggerType.Chat } };
            var kickChat = new Rule { Name = "kick chat", Trigger = new RuleTrigger { Type = TriggerType.Chat, Platform = "kick" } };
            var tiktokChat = new Rule { Name = "tiktok chat", Trigger = new RuleTrigger { Type = TriggerType.Chat, Platform = "tiktok" } };
            var bigGift = new Rule { Name = "100+ coins", Trigger = new RuleTrigger { Type = TriggerType.AnyGift, MinCoins = 100 } };
            foreach (var r in new[] { anyChat, kickChat, tiktokChat, bigGift }) engine.Rules.Add(r);
            List<Rule> fired = null;
            engine.Handled += (_, f) => fired = f;

            engine.Handle(new LiveEvent { Type = "chat", Platform = "kick", Nickname = "k", Comment = "hi" });
            Check(Names(fired) == "any chat, kick chat", "Kick chat fires: " + Names(fired));
            engine.Handle(new LiveEvent { Type = "chat", Nickname = "t", Comment = "hi" });
            Check(Names(fired) == "any chat, tiktok chat", "TikTok chat fires: " + Names(fired));
            var g = new KickParser().ParseFrame(gift).Single();
            engine.Handle(g);
            Check(Names(fired) == (g.Coins >= 100 ? "100+ coins" : ""), $"Kicks gift worth {g.Coins} coins fires: '{Names(fired)}'");
            Check(RulesEngine.Template("{user} sent {gift} ({coins})", g) == $"{g.Nickname} sent {g.GiftName} ({g.Coins})", "templates: " + RulesEngine.Template("{user} sent {gift} ({coins})", g));
            Check(kickChat.Trigger.Summary() == "Any chat message (Kick only)", "summary: " + kickChat.Trigger.Summary());
            Check(kickChat.Clone().Trigger.Platform == "kick", "clone keeps the platform");
        }

        Console.WriteLine(_fail == 0 ? "ALL OK" : _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }

    static string Names(List<Rule> r) => r == null ? "" : string.Join(", ", r.Select(x => x.Name));

    static string Pick(List<string> lines, string ev, string contains) =>
        lines.FirstOrDefault(l => (l.Contains("\"event\":\"" + ev + "\"") || l.Contains("\"event\":\"App\\\\Events\\\\" + ev + "\"")) && l.Contains(contains));

    // ---------------- live ----------------

    static async Task<int> Live(string channel, int seconds)
    {
        Console.WriteLine("Channel lookup");
        using (var plain = new HttpClient())
        {
            try
            {
                var r = await plain.GetAsync("https://kick.com/api/v2/channels/" + KickApi.CleanName(channel));
                Console.WriteLine($"  plain HttpClient (no browser headers): HTTP {(int)r.StatusCode}");
            }
            catch (Exception e) { Console.WriteLine("  plain HttpClient: " + e.Message); }
        }
        var info = await KickApi.GetChannelAsync(channel);
        Console.WriteLine($"  kick.com/{info.Slug}: channel {info.ChannelId}, chatroom {info.ChatroomId}, live {info.Live} (via {KickApi.LastRoute})");
        KickApi.SkipHttpClient = true;
        var viaCurl = await KickApi.GetChannelAsync(channel);
        Check(KickApi.LastRoute == "curl.exe" && viaCurl.ChatroomId == info.ChatroomId, $"curl.exe fallback finds the same chatroom {viaCurl.ChatroomId} (via {KickApi.LastRoute})");
        KickApi.SkipHttpClient = false;
        try { await KickApi.GetChannelAsync("no-such-channel-giftdeck-test-7f3a"); Check(false, "unknown channel should fail"); }
        catch (Exception e) { Check(e.Message.StartsWith("There's no Kick channel"), "unknown channel: " + e.Message); }
        var app = await KickApi.GetPusherAppAsync(info.ChannelId);
        Console.WriteLine($"  Pusher app {app.key} / {app.cluster} (via {KickApi.LastRoute})");

        var engine = new RulesEngine();
        engine.Rules.Add(new Rule { Name = "Kick chat", Trigger = new RuleTrigger { Type = TriggerType.Chat, Platform = "kick" } });
        engine.Rules.Add(new Rule { Name = "Any gift", Trigger = new RuleTrigger { Type = TriggerType.AnyGift } });
        engine.Rules.Add(new Rule { Name = "Subscribe", Trigger = new RuleTrigger { Type = TriggerType.Subscribe } });
        engine.Rules.Add(new Rule { Name = "Follow", Trigger = new RuleTrigger { Type = TriggerType.Follow } });
        int fired = 0;
        engine.Handled += (_, f) => fired += f.Count;

        var counts = new Dictionary<string, int>();
        var kick = new KickService(() => true, () => channel);
        kick.StatusChanged += () => Console.WriteLine($"  [status] connected={kick.Connected} live={kick.Live} error={kick.LastError}");
        kick.EventReceived += e =>
        {
            lock (counts) counts[e.Type] = counts.GetValueOrDefault(e.Type) + 1;
            Console.WriteLine($"  {e.Time:HH:mm:ss} [{e.Platform}] {e.Type,-9} {e.Describe()}" + (e.Type == "gift" ? $"  (coins {e.Coins})" : ""));
            engine.Handle(e);
        };
        Console.WriteLine($"Listening to kick.com/{info.Slug} for {seconds} s (read-only)");
        kick.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        Console.WriteLine("  -- Restart() (what changing the channel name in Stream Setup does) --");
        kick.Restart();
        await Task.Delay(TimeSpan.FromSeconds(seconds / 2.0));
        kick.Stop();
        Check(kick.LastError == null, "no error after the restart");
        Console.WriteLine("Summary: " + string.Join(", ", counts.Select(kv => kv.Key + " " + kv.Value)) + $"; rules fired {fired} times");
        Check(counts.GetValueOrDefault("chat") > 0, "real chat messages parsed");
        Console.WriteLine(_fail == 0 ? "ALL OK" : _fail + " FAILED");
        return _fail == 0 ? 0 : 1;
    }
}
