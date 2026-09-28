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

    // ---- Combos (streak gifts like Rose): each update carries the running total, x1, x2, x3... ----
    // "Count streak gifts once" waits for the end of the combo and counts it all then. If the end never comes
    // (the feed reconnected mid-combo, or TikTok dropped it) the combo is counted after a few quiet seconds
    // rather than lost. With the setting off, each update counts only the gifts added since the last one.

    sealed class Streak
    {
        public LiveEvent Last;
        public int Counted;             // gifts of this combo already handled
        public System.Threading.Timer Timer;
    }

    readonly Dictionary<string, Streak> _streaks = new Dictionary<string, Streak>();
    const int StreakQuietMs = 5000;     // no update for this long: the combo is over
    const int StreakForgetMs = 60000;   // then keep what was counted this long, in case a late update arrives

    static string StreakKey(LiveEvent e) =>
        (e.Platform ?? "") + ":" + (string.IsNullOrEmpty(e.UserId) ? e.Nickname : e.UserId) + ":" + (e.GiftId != 0 ? e.GiftId.ToString() : e.GiftName);

    // The part of this combo update to handle now, or null for nothing yet.
    LiveEvent TrackStreak(LiveEvent e)
    {
        var key = StreakKey(e);
        lock (_streaks)
        {
            _streaks.TryGetValue(key, out var s);
            // A new combo: the count went back down, or this one was already closed (quiet timeout) and the count
            // didn't go up (a higher count is the same combo carrying on; the same count with an end is its late end).
            bool lateEnd = e.RepeatEnd && e.RepeatCount == s?.Last.RepeatCount;
            if (s != null && (e.RepeatCount < s.Last.RepeatCount
                              || (s.Last.RepeatEnd && e.RepeatCount <= s.Last.RepeatCount && !lateEnd)))
                s.Counted = 0;
            if (s == null)
            {
                s = new Streak();
                s.Timer = new System.Threading.Timer(_ => StreakQuiet(key, s));
                _streaks[key] = s;
            }
            s.Last = e;

            if (Hub.Settings.StreakGiftsOnce && !e.RepeatEnd)
            {
                s.Timer.Change(StreakQuietMs, Timeout.Infinite);
                return null;
            }

            var part = Remaining(s, e);
            if (e.RepeatEnd) Forget(key, s);
            else s.Timer.Change(StreakForgetMs, Timeout.Infinite);
            return part;
        }
    }

    // The combo went quiet without its end: count what's left of it now.
    void StreakQuiet(string key, Streak s)
    {
        LiveEvent part;
        lock (_streaks)
        {
            if (!_streaks.TryGetValue(key, out var current) || current != s) return;
            if (s.Last.RepeatEnd || s.Counted >= s.Last.RepeatCount) { Forget(key, s); return; }
            part = Remaining(s, s.Last);
            if (part != null)
            {
                part.RepeatEnd = true;
                Log.Write($"Combo from {s.Last.Nickname} ({s.Last.GiftName} x{s.Last.RepeatCount}) never said it ended; counted it anyway");
            }
            s.Timer.Change(StreakForgetMs, Timeout.Infinite);
            s.Last = s.Last.Copy();
            s.Last.RepeatEnd = true; // the next quiet timeout just forgets it
        }
        if (part != null) Handle(part);
    }

    // What hasn't been handled yet of a combo that's now up to e.RepeatCount, as its own event.
    static LiveEvent Remaining(Streak s, LiveEvent e)
    {
        int more = e.RepeatCount - s.Counted;
        if (more <= 0) return null;
        s.Counted = e.RepeatCount;
        var part = e.Copy();
        part.RepeatCount = more;
        part.GiftType = 0; // already dealt with here, so Handle doesn't track it again
        return part;
    }

    void Forget(string key, Streak s)
    {
        s.Timer.Dispose();
        _streaks.Remove(key);
    }

    public void ResetLikeCounters()
    {
        lock (_likeTotals) { _likeTotals.Clear(); _likeBuckets.Clear(); }
    }

    public void Handle(LiveEvent e)
    {
        if (e.Type == "gift" && e.GiftType == 1 && !e.IsTest)
        {
            e = TrackStreak(e);
            if (e == null) return; // part of a combo that's counted later (or already counted)
        }

        if (e.Type == "chat" && !e.IsTest)
        {
            if (Hub.Settings.TtsReadChat) Hub.Tts.SpeakChat(Template(Hub.Settings.TtsChatTemplate, e), e.Comment, e.UserId);
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
            bool cooling;
            lock (rule) // TikTok and Kick events arrive on different threads
            {
                cooling = rule.CooldownSeconds > 0 && (DateTime.Now - rule.LastFired).TotalSeconds < rule.CooldownSeconds;
                if (!cooling) rule.LastFired = DateTime.Now;
            }
            if (cooling)
            {
                Log.Write($"\"{rule.Name}\" skipped (cooldown)");
                continue;
            }
            fired.Add(rule);
            _ = RunActionsAsync(rule, e);
        }
        // Each listener on its own, so one that throws doesn't stop the others (the feed, stats) hearing about it.
        foreach (var listener in Handled?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<LiveEvent, List<Rule>>)listener)(e, fired); }
            catch (Exception ex) { Log.Write("Showing an event failed: " + ex.Message); }
        }
    }

    // One set of key presses at a time: two gifts at once would otherwise mix their keys (Ctrl held by one, Z by the other).
    static readonly SemaphoreSlim KeyGate = new SemaphoreSlim(1, 1);

    bool Matches(Rule rule, LiveEvent e)
    {
        var t = rule.Trigger;
        if (!string.IsNullOrEmpty(t.Platform) && !string.Equals(t.Platform, e.Platform ?? "tiktok", StringComparison.OrdinalIgnoreCase)) return false;
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
        // Actions run from inside this one (a spinner prize) share its chain, so they never wait behind its own interrupt.
        var chain = _chain.Value;
        bool outermost = chain == null;
        if (outermost) _chain.Value = chain = new System.Runtime.CompilerServices.StrongBox<int>();
        using var release = outermost ? new ChainEnd(this, chain) : null; // lets the others go when this event is done
        for (int i = 0; i < times; i++)
        {
            foreach (var a in actions)
            {
                if (chain.Value == 0) await WaitWhilePausedAsync();
                try { await RunAction(a, e); }
                catch (Exception ex) { Log.Write($"  {a.Summary()} failed: {ex.Message}"); }
            }
            if (times > 1 && !actions.Any(a => a.Type == ActionType.Delay)) await Task.Delay(150);
        }
    }

    sealed class ChainEnd : IDisposable
    {
        readonly RulesEngine _engine;
        readonly System.Runtime.CompilerServices.StrongBox<int> _chain;
        public ChainEnd(RulesEngine engine, System.Runtime.CompilerServices.StrongBox<int> chain) { _engine = engine; _chain = chain; }
        public void Dispose() { for (; _chain.Value > 0; _chain.Value--) _engine.ResumeQueue(); }
    }

    // ---- Interrupts: an alert can pause every event's actions while it plays ----
    // Actions that come up while paused wait in a line and are let go one by one, in the order they
    // arrived, when the interrupt ends. The event that started the interrupt finishes its own actions
    // first. Pauses nest (two interrupts in a row stay paused).

    readonly object _pauseLock = new object();
    readonly Queue<TaskCompletionSource<bool>> _waiting = new Queue<TaskCompletionSource<bool>>();
    readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>> _chain = new AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>>(); // interrupts held by the event running here
    int _pauses;

    public bool IsPaused { get { lock (_pauseLock) return _pauses > 0; } }

    public void PauseQueue()
    {
        lock (_pauseLock) _pauses++;
    }

    // An interrupt has finished playing. Inside an event's actions the others stay paused until that event is done.
    public void EndInterrupt()
    {
        var chain = _chain.Value;
        if (chain != null) chain.Value++;
        else ResumeQueue();
    }

    public void ResumeQueue()
    {
        lock (_pauseLock) if (_pauses > 0) _pauses--;
        while (true)
        {
            TaskCompletionSource<bool> next;
            lock (_pauseLock)
            {
                if (_pauses > 0 || _waiting.Count == 0) return; // a released action may have started a new interrupt
                next = _waiting.Dequeue();
            }
            next.TrySetResult(true); // runs that action up to its next await before the next one is let go
        }
    }

    public Task WaitWhilePausedAsync()
    {
        lock (_pauseLock)
        {
            if (_pauses == 0 && _waiting.Count == 0) return Task.CompletedTask;
            var t = new TaskCompletionSource<bool>();
            _waiting.Enqueue(t);
            return t.Task;
        }
    }

    public async Task RunAction(RuleAction a, LiveEvent e)
    {
        switch (a.Type)
        {
            case ActionType.KeyPress:
                await KeyGate.WaitAsync();
                try
                {
                    if (Hub.Settings.FocusWindowBeforeKeys && !WindowFocus.Focus(Hub.Settings.FocusWindowTitle))
                        Log.Write($"  Could not bring a window with \"{Hub.Settings.FocusWindowTitle}\" in its title to the front");
                    await Task.Run(() =>
                    {
                        var target = WindowFocus.ForegroundTitle();
                        KeySender.Send(a.Text, a.Number > 0 ? a.Number : Hub.Settings.KeyHoldMs);
                        Log.Write($"  Pressed {a.Text} in \"{(target.Length > 0 ? target : "(no window)")}\"");
                    });
                }
                finally { KeyGate.Release(); }
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
                Process.Start(new ProcessStartInfo(a.Text, Template(a.Text2 ?? "", SafeForCommandLine(e))) { UseShellExecute = true });
                break;
            case ActionType.GameCommand:
            {
                var r = await Hub.GameLink.RunAsync(a.Text, a.Text2, a.Args, e, s => Template(s, e));
                Log.Write(r.Ok ? $"  Game: {a.Text2}" + (string.IsNullOrEmpty(r.Message) ? "" : " (" + r.Message + ")") : $"  Game command {a.Text2} failed: {r.Message}");
                break;
            }
            case ActionType.SpinWheel:
                await Hub.Spinners.SpinAsync(a.Text, e);
                break;
            case ActionType.Alert:
                await Hub.Alerts.ShowAsync(a.Text, e);
                break;
        }
    }

    readonly Dictionary<string, DateTime> _lastRequest = new Dictionary<string, DateTime>();

    async Task HandleSongRequest(LiveEvent e)
    {
        var query = StripCommand(e.Comment);
        if (query.Length == 0) return;
        // One chat request per viewer every few minutes, so a single viewer can't fill the queue.
        int minutes = Math.Max(0, Hub.Settings.SpotifyRequestCooldownMinutes);
        var who = string.IsNullOrEmpty(e.UserId) ? e.Nickname : e.UserId;
        lock (_lastRequest)
        {
            if (minutes > 0 && _lastRequest.TryGetValue(who, out var last) && (DateTime.Now - last).TotalMinutes < minutes)
            {
                Log.Write($"Song request from {e.Nickname} skipped: one every {minutes} min per viewer");
                return;
            }
            _lastRequest[who] = DateTime.Now;
        }
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

    // Viewers choose their names and messages, so before they go into a program's command line anything that could
    // start another command or break out of quotes (& | < > ^ " % ` $ and so on) is taken out.
    static LiveEvent SafeForCommandLine(LiveEvent e)
    {
        static string Clean(string s) => s == null ? null : new string(s.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.' || c == ',' || c == '!' || c == '?').ToArray());
        return new LiveEvent
        {
            Type = e.Type, UserId = e.UserId, Nickname = Clean(e.Nickname), GiftName = Clean(e.GiftName), Comment = Clean(e.Comment),
            GiftId = e.GiftId, Diamonds = e.Diamonds, RepeatCount = e.RepeatCount, LikeCount = e.LikeCount, Platform = e.Platform, IsTest = e.IsTest,
        };
    }

    public LiveEvent FakeEvent(RuleTrigger t)
    {
        var e = new LiveEvent { UserId = "testuser", Nickname = "Test Viewer", IsTest = true, Platform = t.Platform == "kick" ? "kick" : "tiktok" };
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
