using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Matches incoming live events against the user's rules and runs their actions.
public class RulesEngine
{
    public ObservableCollection<Rule> Rules { get; } = new ObservableCollection<Rule>();

    // Raised for every event that reached the engine, with the rules it fired (for the dashboard feed).
    public event Action<LiveEvent, List<Rule>> Handled;

    readonly Dictionary<string, int> _likeTotals = new Dictionary<string, int>();
    readonly Dictionary<string, int> _likeBuckets = new Dictionary<string, int>();

    public void Load()
    {
        var list = Storage.Load<List<Rule>>(Hub.Profiles.File("rules.json")) ?? new List<Rule>();
        Rules.Clear();
        foreach (var r in list) Rules.Add(r);
    }

    public void Save()
    {
        Storage.Save(Hub.Profiles.File("rules.json"), Rules.ToList());
        Hub.Overlays?.PushState(); // the gift menu board hides tiles for events that are switched off
    }

    public void ResetLikeCounters()
    {
        lock (_likeTotals) { _likeTotals.Clear(); _likeBuckets.Clear(); }
    }

    public void Handle(LiveEvent e)
    {
        if (e.Type == "gift" && Hub.Settings.StreakGiftsOnce && e.GiftType == 1 && !e.RepeatEnd && !e.IsTest)
            return; // wait for the end of the streak, then count the whole combo once

        if (e.Type == "chat" && !e.IsTest)
        {
            if (Hub.Settings.TtsReadChat) Hub.Tts.Speak(Template(Hub.Settings.TtsChatTemplate, e));
            if (Hub.Settings.SpotifyChatRequests && StartsWithCommand(e.Comment, Hub.Settings.SpotifyRequestCommand))
                _ = HandleSongRequest(e);
        }

        Rule[] rules;
        try
        {
            var app = Application.Current;
            rules = app != null ? app.Dispatcher.Invoke(() => Rules.ToArray()) : Rules.ToArray();
        }
        catch { rules = Rules.ToArray(); }

        var fired = new List<Rule>();
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !Matches(rule, e)) continue;
            if (rule.CooldownSeconds > 0 && (DateTime.Now - rule.LastFired).TotalSeconds < rule.CooldownSeconds)
            {
                Log.Write($"\"{rule.Name}\" skipped (cooldown)");
                continue;
            }
            rule.LastFired = DateTime.Now;
            fired.Add(rule);
            _ = RunActionsAsync(rule, e);
        }
        Handled?.Invoke(e, fired);
    }

    bool Matches(Rule rule, LiveEvent e)
    {
        var t = rule.Trigger;
        switch (t.Type)
        {
            case TriggerType.Gift:
                if (e.Type != "gift") return false;
                bool same = (t.GiftId != 0 && e.GiftId != 0 && t.GiftId == e.GiftId)
                            || (!string.IsNullOrEmpty(t.GiftName) && string.Equals(t.GiftName, e.GiftName, StringComparison.OrdinalIgnoreCase));
                if (!same) return false;
                return t.MinCoins <= 0 || e.Coins >= t.MinCoins;
            case TriggerType.AnyGift:
                if (e.Type != "gift") return false;
                if (t.MinCoins > 0 && e.Coins < t.MinCoins) return false;
                if (t.MaxCoins > 0 && e.Coins > t.MaxCoins) return false;
                return true;
            case TriggerType.Follow: return e.Type == "follow";
            case TriggerType.Share: return e.Type == "share";
            case TriggerType.Subscribe: return e.Type == "subscribe";
            case TriggerType.Join: return e.Type == "join";
            case TriggerType.Chat:
                if (e.Type != "chat") return false;
                return string.IsNullOrWhiteSpace(t.ChatCommand) || StartsWithCommand(e.Comment, t.ChatCommand);
            case TriggerType.Like:
            {
                if (e.Type != "like") return false;
                if (e.IsTest) return true;
                int step = Math.Max(1, t.MinLikes);
                var key = rule.Id + ":" + e.UserId;
                lock (_likeTotals)
                {
                    _likeTotals.TryGetValue(key, out int total);
                    total += e.LikeCount;
                    _likeTotals[key] = total;
                    _likeBuckets.TryGetValue(key, out int bucket);
                    int newBucket = total / step;
                    if (newBucket > bucket)
                    {
                        _likeBuckets[key] = newBucket;
                        return true;
                    }
                }
                return false;
            }
        }
        return false;
    }

    public static bool StartsWithCommand(string comment, string command)
    {
        if (string.IsNullOrWhiteSpace(comment) || string.IsNullOrWhiteSpace(command)) return false;
        comment = comment.TrimStart();
        command = command.Trim();
        if (!comment.StartsWith(command, StringComparison.OrdinalIgnoreCase)) return false;
        return comment.Length == command.Length || char.IsWhiteSpace(comment[command.Length]);
    }

    public static string StripCommand(string comment)
    {
        comment = (comment ?? "").Trim();
        if (comment.StartsWith("!"))
        {
            int sp = comment.IndexOf(' ');
            return sp < 0 ? "" : comment.Substring(sp + 1).Trim();
        }
        return comment;
    }

    public async Task RunActionsAsync(Rule rule, LiveEvent e)
    {
        int times = 1;
        if (rule.RepeatPerGift && e.Type == "gift")
        {
            times = Math.Max(1, e.RepeatCount);
            if (rule.MaxRepeats > 0) times = Math.Min(times, rule.MaxRepeats);
        }
        Log.Write($"\"{rule.Name}\" fired for {e.Nickname}" + (times > 1 ? $" x{times}" : "") + (e.IsTest ? " (test)" : ""));
        var actions = rule.Actions.ToList();
        for (int i = 0; i < times; i++)
        {
            foreach (var a in actions)
            {
                try { await RunAction(a, e); }
                catch (Exception ex) { Log.Write($"  {a.Summary()} failed: {ex.Message}"); }
            }
            if (times > 1 && !actions.Any(a => a.Type == ActionType.Delay)) await Task.Delay(150);
        }
    }

    public async Task RunAction(RuleAction a, LiveEvent e)
    {
        switch (a.Type)
        {
            case ActionType.KeyPress:
                if (Hub.Settings.FocusWindowBeforeKeys && !WindowFocus.Focus(Hub.Settings.FocusWindowTitle))
                    Log.Write($"  Could not bring a window with \"{Hub.Settings.FocusWindowTitle}\" in its title to the front");
                await Task.Run(() =>
                {
                    var target = WindowFocus.ForegroundTitle();
                    KeySender.Send(a.Text, a.Number > 0 ? a.Number : Hub.Settings.KeyHoldMs);
                    Log.Write($"  Pressed {a.Text} in \"{(target.Length > 0 ? target : "(no window)")}\"");
                });
                break;
            case ActionType.Sound:
                Hub.Sounds.Play(a.Text, a.Number > 0 ? a.Number : 100);
                break;
            case ActionType.ObsScene:
                await Hub.Obs.SetSceneAsync(Template(a.Text, e));
                break;
            case ActionType.ObsCanvasScene:
                await Hub.Obs.SetCanvasSceneAsync(a.Text2, Template(a.Text, e));
                break;
            case ActionType.ObsShowSource:
                await Hub.Obs.SetSourceVisibleAsync(a.Text2, a.Text, true);
                break;
            case ActionType.ObsHideSource:
                await Hub.Obs.SetSourceVisibleAsync(a.Text2, a.Text, false);
                break;
            case ActionType.Delay:
                await Task.Delay(Math.Clamp(a.Number, 0, 600000));
                break;
            case ActionType.Tts:
                Hub.Tts.Speak(Template(a.Text, e));
                break;
            case ActionType.SpotifyRequest:
            {
                var query = Template(string.IsNullOrWhiteSpace(a.Text) ? "{request}" : a.Text, e);
                var label = await Hub.Spotify.RequestAsync(query);
                Log.Write($"  Queued {label} for {e.Nickname}");
                break;
            }
            case ActionType.SpotifyControl:
                switch (a.Text)
                {
                    case "Play": await Hub.Spotify.PlayAsync(); break;
                    case "Pause": await Hub.Spotify.PauseAsync(); break;
                    case "Next": await Hub.Spotify.NextAsync(); break;
                    case "Previous": await Hub.Spotify.PreviousAsync(); break;
                    case "VolumeUp": await Hub.Spotify.ChangeVolumeAsync(a.Number > 0 ? a.Number : 10); break;
                    case "VolumeDown": await Hub.Spotify.ChangeVolumeAsync(-(a.Number > 0 ? a.Number : 10)); break;
                    case "SetVolume": await Hub.Spotify.SetVolumeAsync(a.Number); break;
                    default: throw new Exception("Unknown Spotify command " + a.Text);
                }
                break;
            case ActionType.RunProgram:
                if (string.IsNullOrWhiteSpace(a.Text)) throw new Exception("No program set");
                Process.Start(new ProcessStartInfo(a.Text, Template(a.Text2 ?? "", e)) { UseShellExecute = true });
                break;
        }
    }

    async Task HandleSongRequest(LiveEvent e)
    {
        var query = StripCommand(e.Comment);
        if (query.Length == 0) return;
        try
        {
            var label = await Hub.Spotify.RequestAsync(query);
            Log.Write($"Queued {label} for {e.Nickname}");
        }
        catch (Exception ex)
        {
            Log.Write($"Song request from {e.Nickname} failed: {ex.Message}");
        }
    }

    public static string Template(string text, LiveEvent e)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("{user}", e.Nickname ?? "")
            .Replace("{gift}", e.GiftName ?? "")
            .Replace("{coins}", e.Coins.ToString())
            .Replace("{count}", Math.Max(1, e.RepeatCount).ToString())
            .Replace("{likes}", e.LikeCount.ToString())
            .Replace("{comment}", e.Comment ?? "")
            .Replace("{request}", StripCommand(e.Comment));
    }

    public LiveEvent FakeEvent(RuleTrigger t)
    {
        var e = new LiveEvent { UserId = "testuser", Nickname = "Test Viewer", IsTest = true };
        switch (t.Type)
        {
            case TriggerType.Gift:
            {
                var g = Hub.Gifts.Find(t.GiftId, t.GiftName);
                e.Type = "gift";
                e.GiftId = g?.Id ?? t.GiftId;
                e.GiftName = g?.Name ?? t.GiftName;
                e.Diamonds = g?.Coins ?? 1;
                e.RepeatCount = t.MinCoins > 0 && e.Diamonds > 0 ? Math.Max(1, (int)Math.Ceiling(t.MinCoins / (double)e.Diamonds)) : 1;
                break;
            }
            case TriggerType.AnyGift:
                e.Type = "gift"; e.GiftName = "Test Gift"; e.Diamonds = Math.Max(1, t.MinCoins); break;
            case TriggerType.Follow: e.Type = "follow"; break;
            case TriggerType.Share: e.Type = "share"; break;
            case TriggerType.Subscribe: e.Type = "subscribe"; break;
            case TriggerType.Join: e.Type = "join"; break;
            case TriggerType.Like: e.Type = "like"; e.LikeCount = Math.Max(1, t.MinLikes); break;
            case TriggerType.Chat:
                e.Type = "chat";
                e.Comment = string.IsNullOrWhiteSpace(t.ChatCommand) ? "hello from the test" : t.ChatCommand.Trim() + " test message";
                break;
        }
        return e;
    }

    public void Test(Rule rule) => _ = RunActionsAsync(rule, FakeEvent(rule.Trigger));
}
