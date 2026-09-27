using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GiftDeck.Models;

namespace GiftDeck.Services;

// A game mod connected to GiftDeck over the GameLink WebSocket (see docs/v2.1/plan.md). One object per
// hello: when the mod reconnects, a new ModTarget replaces this one in the registry, and this one stays
// behind as offline so it never marks the new connection offline.
public class ModTarget : IGameTarget
{
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(5);

    readonly WebSocket _ws;
    readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<GameResult>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<GameResult>>();

    public string Id { get; }
    public string Game { get; }
    public string Mod { get; }
    public string Name { get; }
    public string Version { get; }
    public bool Connected { get; private set; } = true;
    public string Status { get; internal set; } = "";
    public IReadOnlyList<GameCommandInfo> Commands { get; }
    public DateTime LastHeard { get; internal set; } = DateTime.UtcNow;
    // Set when a newer connection with the same id took over, so the old one closes quietly.
    internal bool Replaced { get; set; }

    internal ModTarget(WebSocket ws, string game, string mod, string name, string version, List<GameCommandInfo> commands)
    {
        _ws = ws;
        Game = game;
        Mod = mod;
        Id = game + ":" + mod;
        Name = string.IsNullOrWhiteSpace(name) ? mod : name;
        Version = version ?? "";
        Commands = commands;
    }

    public async Task<GameResult> RunAsync(string command, JsonObject args, LiveEvent e)
    {
        if (!Connected) return new GameResult(false, Name + " isn't connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var msg = new JsonObject
            {
                ["type"] = "trigger",
                ["id"] = id,
                ["command"] = command,
                ["args"] = args ?? new JsonObject(),
                ["user"] = e?.Nickname ?? "",
                ["gift"] = e?.GiftName ?? "",
                ["count"] = Math.Max(1, e?.RepeatCount ?? 1),
            };
            if (!await SendAsync(msg)) return new GameResult(false, Name + " isn't connected");
            var done = await Task.WhenAny(tcs.Task, Task.Delay(AnswerTimeout));
            return done == tcs.Task ? tcs.Task.Result : new GameResult(false, "no answer");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    internal async Task<bool> SendAsync(JsonNode msg)
    {
        if (_ws.State != WebSocketState.Open) return false;
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await _sendLock.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
            return true;
        }
        catch { return false; }
        finally { _sendLock.Release(); }
    }

    internal void OnResult(JsonObject o)
    {
        var id = (string)o["id"] ?? "";
        if (!_pending.TryGetValue(id, out var tcs)) return; // late answer after the timeout: nothing waits for it
        bool ok = o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        tcs.TrySetResult(new GameResult(ok, (string)o["message"] ?? ""));
    }

    internal void MarkDisconnected()
    {
        Connected = false;
        foreach (var kv in _pending) kv.Value.TrySetResult(new GameResult(false, Name + " disconnected"));
    }

    internal async Task CloseAsync(string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
        }
        catch { }
        try { _ws.Abort(); } catch { }
    }
}
