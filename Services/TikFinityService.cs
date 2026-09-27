using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Watches the TikFinity process and listens to its local WebSocket feed.
public class TikFinityService
{
    public event Action<LiveEvent> EventReceived;
    public event Action<GiftInfo> GiftSeen;
    public event Action StatusChanged;

    public bool ProcessRunning { get; private set; }
    public bool Connected { get; private set; }
    public string LastError { get; private set; }
    public DateTime LastMessage { get; private set; }
    // Whether TikFinity itself is hooked up to the TikTok LIVE (null until it says). Its feed can be open while this is false.
    public bool? TikTokLive { get; private set; }

    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    DateTime _lastLaunch = DateTime.MinValue;
    bool _loggedFailure;

    public void Start()
    {
        _ = Task.Run(ProcessWatch);
        _ = Task.Run(SocketLoop);
    }

    public void Stop() => _cts.Cancel();

    public static bool IsProcessRunning() => Process.GetProcessesByName("TikFinity").Length > 0;

    public bool Launch()
    {
        var exe = Hub.Settings.TikFinityExe;
        if (!File.Exists(exe))
        {
            LastError = "TikFinity was not found at " + exe;
            Log.Write(LastError);
            StatusChanged?.Invoke();
            return false;
        }
        try
        {
            _lastLaunch = DateTime.Now;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
            bool hidden = StartsHidden;
            Log.Write(hidden ? "Launched TikFinity hidden" : "Launched TikFinity");
            if (hidden) { _startedHidden = true; _ = Task.Run(HideWindowsAsync); }
            return true;
        }
        catch (Exception e)
        {
            LastError = "Could not launch TikFinity: " + e.Message;
            Log.Write(LastError);
            StatusChanged?.Invoke();
            return false;
        }
    }

    // ---- TikFinity in the background ----

    static bool StartsHidden => Hub.Settings.LiveReader == "tikfinity" && Hub.Settings.TikFinityHidden;
    bool _startedHidden;

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // TikFinity's own top-level windows (with a title and no owner, so not tooltips or menus).
    static List<IntPtr> Windows(bool visibleOnly)
    {
        var pids = Process.GetProcessesByName("TikFinity").Select(p => (uint)p.Id).ToHashSet();
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var pid);
            if (pids.Contains(pid) && GetWindowTextLength(h) > 0 && GetWindow(h, 4 /* GW_OWNER */) == IntPtr.Zero
                && (!visibleOnly || IsWindowVisible(h)))
                found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // TikFinity opens its window a few seconds after starting (and sometimes a second one): hide what appears.
    async Task HideWindowsAsync()
    {
        var until = DateTime.Now.AddSeconds(45);
        while (DateTime.Now < until && !_cts.IsCancellationRequested)
        {
            foreach (var h in Windows(visibleOnly: true)) ShowWindow(h, 0 /* SW_HIDE */);
            await Task.Delay(250);
        }
    }

    public bool WindowHidden => IsProcessRunning() && Windows(visibleOnly: true).Count == 0;

    // Brings a hidden TikFinity back on screen (to log in or change its settings).
    public bool ShowWindow()
    {
        var all = Windows(visibleOnly: false);
        if (all.Count == 0) return false;
        _startedHidden = false;
        foreach (var h in all) ShowWindow(h, 5 /* SW_SHOW */);
        ShowWindow(all[0], 9 /* SW_RESTORE */);
        SetForegroundWindow(all[0]);
        return true;
    }

    public void HideWindow()
    {
        foreach (var h in Windows(visibleOnly: true)) ShowWindow(h, 0);
        _startedHidden = true;
    }

    // A TikFinity GiftDeck started hidden has no window to close it from, so it closes with GiftDeck.
    public void CloseIfHidden()
    {
        if (!_startedHidden || !WindowHidden) return;
        foreach (var p in Process.GetProcessesByName("TikFinity"))
            try { p.Kill(entireProcessTree: true); } catch { }
        Log.Write("Closed the hidden TikFinity");
    }

    async Task ProcessWatch()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool running = IsProcessRunning();
                if (running != ProcessRunning)
                {
                    ProcessRunning = running;
                    Log.Write(running ? "TikFinity is running" : "TikFinity is not running");
                    StatusChanged?.Invoke();
                }
                bool wanted = Hub.Settings.AutoLaunchTikFinity || Hub.Settings.LiveReader == "tikfinity";
                if (!running && wanted && (DateTime.Now - _lastLaunch).TotalSeconds > 60)
                    Launch();
            }
            catch (Exception e)
            {
                LastError = e.Message;
            }
            try { await Task.Delay(3000, _cts.Token); } catch { }
        }
    }

    async Task SocketLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await RunOnce();
                _loggedFailure = false;
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                LastError = e.InnerException?.Message ?? e.Message;
                if (!_loggedFailure)
                    Log.Write((BridgeService.InUse ? "TikTok bridge connection lost: " : "TikFinity feed unavailable: ") + LastError + " (will keep retrying)");
                _loggedFailure = true;
            }
            catch { }
            if (Connected)
            {
                Connected = false;
                TikTokLive = null;
                StatusChanged?.Invoke();
            }
            try { await Task.Delay(4000, _cts.Token); } catch { }
        }
    }

    ClientWebSocket _current;

    // Drops the current feed connection; the loop reconnects to whatever address the settings now say.
    public void Reconnect()
    {
        try { _current?.Abort(); } catch { }
    }

    async Task RunOnce()
    {
        using var ws = new ClientWebSocket();
        _current = ws;
        await ws.ConnectAsync(new Uri(Hub.Settings.TikFinityUrl), _cts.Token);
        Connected = true;
        LastError = null;
        Log.Write((BridgeService.InUse ? "Connected to the TikTok bridge at " : "Connected to TikFinity feed at ") + Hub.Settings.TikFinityUrl);
        StatusChanged?.Invoke();

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            LastMessage = DateTime.Now;
            try { Handle(text); }
            catch (Exception e) { Log.Write("Could not read a TikFinity message: " + e.Message); }
        }
        Log.Write("TikFinity feed closed");
    }

    void Handle(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        string ev = (J.Str(root, "event") ?? "").ToLowerInvariant();
        var dataN = J.Prop(root, "data");
        if (dataN == null) return;
        var data = dataN.Value;

        if (ev == "config") { LearnFromConfig(data); return; }
        if (ev == "livestatuschange")
        {
            bool live = J.Bool(data, "isLive");
            if (TikTokLive != live)
            {
                var who = BridgeService.InUse ? "TikTok bridge" : "TikFinity";
                Log.Write(live ? $"{who} is connected to your LIVE"
                    : BridgeService.InUse ? "TikTok bridge: not live (waiting for your LIVE)"
                    : "TikFinity says it is NOT connected to your LIVE, so gifts and chat won't come through");
                TikTokLive = live;
                StatusChanged?.Invoke();
            }
            return;
        }

        var user = J.Prop(data, "user");
        var e = new LiveEvent
        {
            Type = ev,
            UserId = J.Str(data, "uniqueId", "userId") ?? (user != null ? J.Str(user.Value, "uniqueId", "userId") : null) ?? "",
            Nickname = J.Str(data, "nickname") ?? (user != null ? J.Str(user.Value, "nickname") : null) ?? "",
            PictureUrl = Picture(data) ?? (user != null ? Picture(user.Value) : null),
        };
        if (string.IsNullOrEmpty(e.Nickname)) e.Nickname = e.UserId;
        var stamp = J.Num(data, 0, "createTime");
        if (stamp > 1_000_000_000_000) e.SentAt = DateTimeOffset.FromUnixTimeMilliseconds(stamp).LocalDateTime;

        switch (ev)
        {
            case "gift":
            {
                var gift = J.Prop(data, "gift");
                var ext = J.Prop(data, "extendedGiftInfo");
                e.GiftId = J.Num(data, 0, "giftId");
                if (e.GiftId == 0 && gift != null) e.GiftId = J.Num(gift.Value, 0, "gift_id", "id");
                e.GiftName = J.Str(data, "giftName") ?? (gift != null ? J.Str(gift.Value, "name") : null) ?? (ext != null ? J.Str(ext.Value, "name") : null) ?? "Gift";
                e.Diamonds = (int)J.Num(data, 0, "diamondCount");
                if (e.Diamonds == 0 && gift != null) e.Diamonds = (int)J.Num(gift.Value, 0, "diamond_count");
                if (e.Diamonds == 0 && ext != null) e.Diamonds = (int)J.Num(ext.Value, 0, "diamond_count");
                e.RepeatCount = Math.Max(1, (int)J.Num(data, 1, "repeatCount"));
                if (gift != null && J.Num(gift.Value, 0, "repeat_count") > 0) e.RepeatCount = Math.Max(1, (int)J.Num(gift.Value, 1, "repeat_count"));
                e.GiftType = (int)J.Num(data, 0, "giftType");
                if (e.GiftType == 0 && gift != null) e.GiftType = (int)J.Num(gift.Value, 0, "gift_type");
                if (e.GiftType == 0 && ext != null) e.GiftType = (int)J.Num(ext.Value, 0, "type");
                bool end = J.Has(data, "repeatEnd") ? J.Bool(data, "repeatEnd") : (gift != null && J.Has(gift.Value, "repeat_end") ? J.Bool(gift.Value, "repeat_end") : true);
                e.RepeatEnd = end;
                GiftSeen?.Invoke(new GiftInfo { Id = e.GiftId, Name = e.GiftName, Coins = e.Diamonds, ImageUrl = J.Str(data, "giftPictureUrl") });
                break;
            }
            case "like":
                e.LikeCount = Math.Max(1, (int)J.Num(data, 1, "likeCount"));
                e.TotalLikes = (int)J.Num(data, 0, "totalLikeCount");
                break;
            case "roomuser":
                e.Type = "viewers";
                e.ViewerCount = (int)J.Num(data, 0, "viewerCount");
                e.TotalViewers = (int)J.Num(data, 0, "totalViewers");
                break;
            case "chat":
                e.Comment = J.Str(data, "comment") ?? "";
                break;
            case "social":
            {
                var t = (J.Str(data, "displayType") ?? "").ToLowerInvariant();
                e.Type = t.Contains("follow") ? "follow" : t.Contains("share") ? "share" : "social";
                break;
            }
            case "member":
                e.Type = "join";
                break;
            case "follow":
            case "share":
            case "subscribe":
                break;
            case "streamend":
                Log.Write("TikFinity reports the LIVE has ended");
                return;
            case "envelope":
            case "emote":
            case "questionnew":
            case "linkmicbattle":
            case "linkmicarmies":
            case "livtintro":
                return;
        }
        EventReceived?.Invoke(e);
    }

    void LearnFromConfig(JsonElement data)
    {
        var events = J.Prop(data, "events");
        if (events == null || events.Value.ValueKind != JsonValueKind.Array) return;
        int n = 0;
        foreach (var ev in events.Value.EnumerateArray())
        {
            var trig = J.Prop(ev, "trigger");
            if (trig == null) continue;
            if ((J.Str(trig.Value, "type") ?? "") != "gift") continue;
            var name = J.Str(trig.Value, "name");
            if (string.IsNullOrEmpty(name)) continue;
            GiftSeen?.Invoke(new GiftInfo
            {
                Id = J.Num(trig.Value, 0, "id"),
                Name = name,
                Coins = (int)J.Num(trig.Value, 0, "coins"),
                ImageUrl = J.Str(trig.Value, "imageUrl"),
            });
            n++;
        }
        Log.Write($"TikFinity sent its config ({n} gift triggers learned)");
    }

    static string Picture(JsonElement d)
    {
        var pic = J.Prop(d, "profilePictureUrl");
        if (pic != null)
        {
            if (pic.Value.ValueKind == JsonValueKind.String) return pic.Value.GetString();
            if (pic.Value.ValueKind == JsonValueKind.Array && pic.Value.GetArrayLength() > 0) return pic.Value[0].GetString();
        }
        var details = J.Prop(d, "userDetails");
        if (details != null)
        {
            var urls = J.Prop(details.Value, "profilePictureUrls");
            if (urls != null && urls.Value.ValueKind == JsonValueKind.Array && urls.Value.GetArrayLength() > 0)
                return urls.Value[urls.Value.GetArrayLength() - 1].GetString();
        }
        return null;
    }
}

// Small helpers for reading loosely typed JSON.
static class J
{
    public static JsonElement? Prop(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v : (JsonElement?)null;

    public static bool Has(JsonElement e, string name) => Prop(e, name) != null;

    public static string Str(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(e, n);
            if (v == null) continue;
            if (v.Value.ValueKind == JsonValueKind.String) return v.Value.GetString();
            if (v.Value.ValueKind == JsonValueKind.Number) return v.Value.GetRawText();
        }
        return null;
    }

    public static long Num(JsonElement e, long def, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(e, n);
            if (v == null) continue;
            if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt64(out var l)) return l;
            if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDouble(out var d)) return (long)d;
            if (v.Value.ValueKind == JsonValueKind.String && long.TryParse(v.Value.GetString(), out var s)) return s;
        }
        return def;
    }

    public static bool Bool(JsonElement e, params string[] names)
    {
        foreach (var n in names)
        {
            var v = Prop(e, n);
            if (v == null) continue;
            switch (v.Value.ValueKind)
            {
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                case JsonValueKind.Number: return v.Value.GetDouble() != 0;
                case JsonValueKind.String: return string.Equals(v.Value.GetString(), "true", StringComparison.OrdinalIgnoreCase) || v.Value.GetString() == "1";
            }
        }
        return false;
    }
}
