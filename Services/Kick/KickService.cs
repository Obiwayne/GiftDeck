using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Reads a Kick channel's chat, subs, gifted subs, follows and Kicks gifts, read-only and without logging in,
// through the same public Pusher feed kick.com's own chat uses. Each one becomes a LiveEvent with
// Platform = "kick", fed to the rules engine just like TikTok's. Runs alongside TikTok (both at once).
public class KickService
{
    public event Action<LiveEvent> EventReceived;
    public event Action StatusChanged;

    public bool Connected { get; private set; }
    public bool? Live { get; private set; }
    public string LastError { get; private set; }
    public DateTime LastMessage { get; private set; }
    public KickChannelInfo Channel { get; private set; }
    public string Route => KickApi.LastRoute;

    readonly Func<bool> _enabled;
    readonly Func<string> _channelName;
    readonly KickParser _parser = new KickParser();
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    readonly SemaphoreSlim _wake = new SemaphoreSlim(0);
    readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    ClientWebSocket _current;
    bool _loggedFailure;
    volatile bool _restartRequested;
    int _failures;

    public KickService(Func<bool> enabled, Func<string> channelName)
    {
        _enabled = enabled;
        _channelName = channelName;
        _parser.LiveChanged += live => { Live = live; Log.Write(live ? "Kick: your channel went live" : "Kick: your stream ended"); StatusChanged?.Invoke(); };
    }

    public bool Enabled => _enabled() && !string.IsNullOrWhiteSpace(KickApi.CleanName(_channelName()));
    public string ChannelName => KickApi.CleanName(_channelName());

    public void Start() => _ = Task.Run(Loop);

    public void Stop()
    {
        _cts.Cancel();
        try { _current?.Abort(); } catch { }
    }

    // Settings changed (switched on/off or a new channel name): drop the connection and start again straight away.
    public void Restart()
    {
        LastError = null;
        _failures = 0;
        _restartRequested = true;
        try { _current?.Abort(); } catch { }
        _wake.Release();
        StatusChanged?.Invoke();
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (!Enabled)
            {
                SetDisconnected();
                try { await _wake.WaitAsync(TimeSpan.FromSeconds(5), _cts.Token); } catch { }
                continue;
            }
            _restartRequested = false;
            try
            {
                await RunOnce();
                _loggedFailure = false;
            }
            catch (Exception) when (_restartRequested) { } // dropped on purpose by Restart()
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                _failures++;
                LastError = e.InnerException?.Message ?? e.Message;
                if (!_loggedFailure) Log.Write("Kick: " + LastError + " (will keep retrying)");
                _loggedFailure = true;
            }
            catch { }
            SetDisconnected();
            // Back off a little each time it fails (2 s, 5 s, 10 s ... up to 30 s); a Restart() wakes it early.
            int wait = _failures switch { 0 => 2, 1 => 2, 2 => 5, 3 => 10, _ => 30 };
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(wait), _cts.Token); } catch { }
            while (_wake.CurrentCount > 0) _wake.Wait(0); // several Restart()s in a row count as one
        }
    }

    void SetDisconnected()
    {
        if (!Connected) return;
        Connected = false;
        StatusChanged?.Invoke();
    }

    async Task RunOnce()
    {
        var name = ChannelName;
        if (Channel == null || !string.Equals(Channel.Slug, name, StringComparison.OrdinalIgnoreCase))
        {
            Channel = null;
            Live = null;
        }
        // Looked up again on every connect (cheap), which also refreshes whether the channel is live.
        var info = await KickApi.GetChannelAsync(name, _cts.Token);
        if (ChannelName != name) return; // the name changed while looking it up
        Channel = info;
        Live = info.Live;
        var (key, cluster) = await KickApi.GetPusherAppAsync(info.ChannelId, _cts.Token);

        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        ws.Options.SetRequestHeader("Origin", "https://kick.com");
        _current = ws;
        var url = $"wss://ws-{cluster}.pusher.com/app/{key}?protocol=7&client=js&version=8.4.0&flash=false";
        await ws.ConnectAsync(new Uri(url), _cts.Token);

        foreach (var ch in new[] { $"chatrooms.{info.ChatroomId}.v2", $"chatroom_{info.ChatroomId}", $"channel.{info.ChannelId}", $"channel_{info.ChannelId}" })
            await SendAsync(ws, "{\"event\":\"pusher:subscribe\",\"data\":{\"auth\":\"\",\"channel\":\"" + ch + "\"}}");

        using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Run(() => PingLoop(ws, keepAlive.Token));

        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        LastMessage = DateTime.Now;
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                LastMessage = DateTime.Now;
                try { await Handle(ws, text); }
                catch (Exception e) { Log.Write("Could not read a Kick message: " + e.Message); }
            }
        }
        finally { keepAlive.Cancel(); }
        if (!_cts.IsCancellationRequested && Enabled && ChannelName == name) throw new Exception("Kick's chat connection closed");
    }

    async Task Handle(ClientWebSocket ws, string text)
    {
        // Pusher's own messages: connected, subscribed, ping.
        string ev;
        using (var doc0 = JsonDocument.Parse(text)) ev = J.Str(doc0.RootElement, "event") ?? "";
        if (ev.StartsWith("pusher"))
        {
            using var doc = JsonDocument.Parse(text);
            if (ev == "pusher:ping") await SendAsync(ws, "{\"event\":\"pusher:pong\",\"data\":{}}");
            else if (ev == "pusher:error") throw new Exception("Kick's chat server said: " + (J.Str(doc.RootElement, "data") ?? "error"));
            else if (ev == "pusher_internal:subscription_succeeded" && !Connected)
            {
                Connected = true;
                LastError = null;
                _failures = 0;
                Log.Write($"Connected to Kick chat: kick.com/{Channel?.Slug} (" + (Live == true ? "live now" : "not live right now") + ")");
                StatusChanged?.Invoke();
            }
            return;
        }
        foreach (var e in _parser.ParseFrame(text))
            EventReceived?.Invoke(e);
    }

    // Pusher closes quiet connections: ping after a minute without messages; if nothing comes back, reconnect.
    async Task PingLoop(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
            var quiet = (DateTime.Now - LastMessage).TotalSeconds;
            if (quiet > 150) { Log.Write("Kick chat went quiet; reconnecting"); try { ws.Abort(); } catch { } return; }
            if (quiet > 60) try { await SendAsync(ws, "{\"event\":\"pusher:ping\",\"data\":{}}"); } catch { }
        }
    }

    async Task SendAsync(ClientWebSocket ws, string json)
    {
        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token); }
        finally { _sendLock.Release(); }
    }
}
