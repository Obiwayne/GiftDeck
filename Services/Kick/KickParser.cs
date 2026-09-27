using System.Text.Json;
using System.Text.RegularExpressions;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Turns Kick's public Pusher messages into GiftDeck LiveEvents (Platform = "kick").
// Kept free of any connection or settings code so it can be tested with captured payloads.
//
//   chatrooms.<chatroom id>.v2  App\Events\ChatMessageEvent        chat          -> "chat"
//                               App\Events\SubscriptionEvent        new sub/resub -> "subscribe"
//                               App\Events\GiftedSubscriptionsEvent (older name)  -> "subscribe" x count
//   chatroom_<chatroom id>      GiftedSubscriptionsEvent            gifted subs   -> "subscribe" x count (may come in chunks)
//   channel.<channel id>        App\Events\FollowersUpdated          follow        -> "follow" (older; not seen in 2026 captures)
//                               App\Events\StreamerIsLive / StopStreamBroadcast   -> LiveChanged
//   channel_<channel id>        KicksGifted                          Kicks gift    -> "gift" (1 Kick = 1 coin)
//                               GoalProgressUpdateEvent (followers)  goal count up -> unnamed "follow" per new follower
public class KickParser
{
    // Raised for Kick's own "went live" / "stream ended" messages.
    public event Action<bool> LiveChanged;

    readonly HashSet<string> _seenIds = new HashSet<string>();
    readonly Queue<string> _seenOrder = new Queue<string>();
    readonly Dictionary<string, GiftedChunks> _chunks = new Dictionary<string, GiftedChunks>();
    readonly Dictionary<string, DateTime> _recentGifted = new Dictionary<string, DateTime>();
    readonly Dictionary<string, long> _goalCounts = new Dictionary<string, long>();
    bool _namedFollows;

    sealed class GiftedChunks { public string Gifter; public int Count; public DateTime Started = DateTime.Now; }

    static readonly Regex Emote = new Regex(@"\[emote:\d+:([^\]]*)\]", RegexOptions.Compiled);
    static readonly Regex Spaces = new Regex(@" {2,}", RegexOptions.Compiled);

    // One Pusher frame ({"event":..,"channel":..,"data":"<json>"}); returns the events it holds (usually 0 or 1).
    public List<LiveEvent> ParseFrame(string frame)
    {
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        var ev = J.Str(root, "event") ?? "";
        var channel = J.Str(root, "channel") ?? "";
        var data = J.Prop(root, "data");
        if (data == null) return new List<LiveEvent>();
        var json = data.Value.ValueKind == JsonValueKind.String ? data.Value.GetString() : data.Value.GetRawText();
        return Parse(ev, channel, json);
    }

    public List<LiveEvent> Parse(string eventName, string channel, string dataJson)
    {
        var list = new List<LiveEvent>();
        if (string.IsNullOrEmpty(dataJson) || eventName.StartsWith("pusher")) return list;
        using var doc = JsonDocument.Parse(dataJson);
        var d = doc.RootElement;
        if (d.ValueKind != JsonValueKind.Object) return list;

        switch (eventName)
        {
            case @"App\Events\ChatMessageEvent":
            {
                if (!FirstTime("m:" + J.Str(d, "id"))) break;
                var type = J.Str(d, "type") ?? "message";
                if (type != "message" && type != "reply") break; // e.g. system/celebration lines
                var sender = J.Prop(d, "sender");
                var e = NewEvent("chat", sender);
                e.Comment = CleanText(J.Str(d, "content"));
                e.SentAt = Time(J.Str(d, "created_at"));
                if (e.Comment.Length > 0) list.Add(e);
                break;
            }
            case "KicksGifted":
            {
                if (!FirstTime("k:" + J.Str(d, "gift_transaction_id"))) break;
                var gift = J.Prop(d, "gift");
                var e = NewEvent("gift", J.Prop(d, "sender"));
                e.GiftName = (gift != null ? J.Str(gift.Value, "name", "gift_id") : null) ?? "Kicks";
                // Kicks are Kick's coins: viewers pay about $1 for 100 Kicks, close to TikTok's ~$1 for 70-100 coins,
                // so one Kick counts as one coin in GiftDeck's rules ("any gift worth 100+ coins").
                e.Diamonds = gift != null ? (int)J.Num(gift.Value, 0, "amount") : 0;
                e.RepeatCount = 1;
                e.RepeatEnd = true;
                e.Comment = CleanText(J.Str(d, "message"));
                e.SentAt = Time(J.Str(d, "created_at"));
                list.Add(e);
                break;
            }
            case @"App\Events\SubscriptionEvent":
            {
                var e = new LiveEvent { Type = "subscribe", Platform = "kick" };
                e.Nickname = e.UserId = J.Str(d, "username") ?? "Someone";
                e.RepeatCount = 1;
                list.Add(e);
                break;
            }
            case "GiftedSubscriptionsEvent":
            case @"App\Events\GiftedSubscriptionsEvent":
            {
                var e = Gifted(d);
                if (e != null) list.Add(e);
                break;
            }
            case "GoalProgressUpdateEvent":
            {
                // Kick's public feed no longer names new followers. A channel with a follower goal does get the
                // goal's count every few seconds, so each rise becomes an unnamed follow (the first count is the start).
                if (_namedFollows || !string.Equals(J.Str(d, "type"), "followers", StringComparison.OrdinalIgnoreCase)) break;
                var id = J.Str(d, "id") ?? "";
                long now = J.Num(d, -1, "current_value");
                if (now < 0) break;
                bool known = _goalCounts.TryGetValue(id, out long before);
                _goalCounts[id] = Math.Max(now, known ? before : now);
                if (!known || now <= before) break;
                for (long i = 0; i < Math.Min(5, now - before); i++)
                    list.Add(new LiveEvent { Type = "follow", Platform = "kick", Nickname = "Someone", UserId = "" });
                break;
            }
            case @"App\Events\FollowersUpdated":
            {
                // Kick sends this for follows and unfollows; only a follow fires anything.
                _namedFollows = true;
                if (J.Has(d, "followed") && !J.Bool(d, "followed")) break;
                var name = J.Str(d, "username");
                var e = new LiveEvent { Type = "follow", Platform = "kick", Nickname = string.IsNullOrWhiteSpace(name) ? "Someone" : name, UserId = name ?? "" };
                list.Add(e);
                break;
            }
            case @"App\Events\StreamerIsLive":
                LiveChanged?.Invoke(true);
                break;
            case @"App\Events\StopStreamBroadcast":
                LiveChanged?.Invoke(false);
                break;
        }
        return list;
    }

    // Gifted subs. The newer event can be split into chunks for big gifts (chunk_details); they're added up and
    // fire once, on the last chunk. The same gift is sometimes sent under both names, so repeats are dropped.
    LiveEvent Gifted(JsonElement d)
    {
        var gifterEl = J.Prop(d, "gifter");
        var gifter = J.Str(d, "gifter_username")
                     ?? (gifterEl != null ? (gifterEl.Value.ValueKind == JsonValueKind.String ? gifterEl.Value.GetString() : J.Str(gifterEl.Value, "username", "slug")) : null)
                     ?? "Someone";
        int count = Count(J.Prop(d, "gifted_usernames")) + Count(J.Prop(d, "giftees")) + Count(J.Prop(d, "gifted_users"));
        if (count == 0) count = (int)Math.Max(1, J.Num(d, 1, "quantity", "gifted_count", "count"));

        var chunk = J.Prop(d, "chunk_details");
        if (chunk == null && J.Num(d, 0, "gifted_total") > 0) count = (int)J.Num(d, 0, "gifted_total"); // this gift's size (gifter_total is their all-time total)
        if (chunk != null)
        {
            var id = J.Str(chunk.Value, "correlation_id") ?? "";
            int index = (int)J.Num(chunk.Value, 0, "chunk_index");
            int total = (int)J.Num(chunk.Value, 1, "total_chunks");
            if (!_chunks.TryGetValue(id, out var acc)) _chunks[id] = acc = new GiftedChunks { Gifter = gifter };
            acc.Count += count;
            foreach (var old in _chunks.Where(kv => (DateTime.Now - kv.Value.Started).TotalMinutes > 2).Select(kv => kv.Key).ToList())
                _chunks.Remove(old);
            if (index < total - 1) return null;
            _chunks.Remove(id);
            count = acc.Count;
        }

        var key = gifter.ToLowerInvariant() + ":" + count;
        foreach (var old in _recentGifted.Where(kv => (DateTime.Now - kv.Value).TotalSeconds > 15).Select(kv => kv.Key).ToList())
            _recentGifted.Remove(old);
        if (_recentGifted.ContainsKey(key)) return null;
        _recentGifted[key] = DateTime.Now;

        return new LiveEvent
        {
            Type = "subscribe", Platform = "kick", Nickname = gifter, UserId = gifter,
            RepeatCount = Math.Max(1, count), GiftName = count == 1 ? "a gifted sub" : count + " gifted subs",
        };
    }

    static int Count(JsonElement? arr) => arr != null && arr.Value.ValueKind == JsonValueKind.Array ? arr.Value.GetArrayLength() : 0;

    static LiveEvent NewEvent(string type, JsonElement? sender)
    {
        var e = new LiveEvent { Type = type, Platform = "kick" };
        if (sender != null)
        {
            e.Nickname = J.Str(sender.Value, "username") ?? "";
            e.UserId = J.Str(sender.Value, "slug", "username", "id") ?? e.Nickname;
            e.PictureUrl = J.Str(sender.Value, "profile_picture", "profile_pic");
        }
        if (string.IsNullOrEmpty(e.Nickname)) e.Nickname = string.IsNullOrEmpty(e.UserId) ? "Someone" : e.UserId;
        return e;
    }

    // "[emote:37225:KEKLEO] hi" -> "KEKLEO hi", so chat reads (and is read aloud) sensibly.
    public static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (!s.Contains("[emote:")) return s.Trim();
        return Spaces.Replace(Emote.Replace(s, m => " " + m.Groups[1].Value + " "), " ").Trim();
    }

    static DateTime? Time(string s) => DateTimeOffset.TryParse(s, out var t) ? t.LocalDateTime : null;

    bool FirstTime(string id)
    {
        if (string.IsNullOrEmpty(id) || id.EndsWith(":")) return true;
        if (!_seenIds.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 1000) _seenIds.Remove(_seenOrder.Dequeue());
        return true;
    }
}
