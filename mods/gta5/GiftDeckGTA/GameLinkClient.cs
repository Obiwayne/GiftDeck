using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GiftDeckGTA
{
    // One command GiftDeck asked us to run.
    public class Trigger
    {
        public string Id;
        public string Command;
        public Dictionary<string, object> Args = new Dictionary<string, object>();
        public string User;
        public string Gift;
        public int Count = 1;
        public bool FromMenu; // tested from the F10 menu, not sent by GiftDeck
    }

    // GameLink client: connects to GiftDeck's local WebSocket (ws://127.0.0.1:21216/), says hello with our
    // command list, and receives triggers. It runs on its own thread and keeps reconnecting while GiftDeck
    // is closed. Nothing here touches the game: triggers go into a queue that the script drains on its tick.
    // Protocol: docs/v2.1/plan.md, section 1.
    public class GameLinkClient
    {
        public const int PingSeconds = 15;
        const int SilenceSeconds = 45;      // no message at all for this long = the connection is dead
        const int ConnectTimeoutMs = 5000;

        public readonly ConcurrentQueue<Trigger> Triggers = new ConcurrentQueue<Trigger>();

        public volatile bool Connected;
        public string Url { get; private set; }
        public string LastError { get; private set; } = "";
        public int ConnectCount { get; private set; }

        readonly Func<Dictionary<string, object>> _hello;
        readonly Action<string> _log;
        readonly CancellationTokenSource _cts = new CancellationTokenSource();
        readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        volatile ClientWebSocket _ws;
        CancellationTokenSource _connectionCts;
        int _lastReceived;
        Thread _thread;

        public GameLinkClient(string host, int port, Func<Dictionary<string, object>> hello, Action<string> log)
        {
            Url = $"ws://{host}:{port}/";
            _hello = hello;
            _log = log ?? (_ => { });
        }

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "GiftDeck GameLink" };
            _thread.Start();
        }

        public void Stop()
        {
            _cts.Cancel();
            var ws = _ws;
            if (ws != null)
            {
                // Say goodbye so GiftDeck marks us offline straight away (best effort, the game may be closing)
                try { ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "script stopped", CancellationToken.None).Wait(500); }
                catch { }
            }
        }

        // Drop the current connection and connect again (the menu's "Reconnect now").
        public void Reconnect()
        {
            try { _connectionCts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        public void SendResult(string id, bool ok, string message) =>
            Send(new Dictionary<string, object> { ["type"] = "result", ["id"] = id ?? "", ["ok"] = ok, ["message"] = message ?? "" });

        public void SendStatus(string text) =>
            Send(new Dictionary<string, object> { ["type"] = "status", ["text"] = text ?? "" });

        // Fire and forget from any thread (the game's tick must never wait on the network).
        public void Send(Dictionary<string, object> message)
        {
            if (!Connected) return;
            string text = Json.Write(message);
            Task.Run(() => SendAsync(text));
        }

        async Task SendAsync(string text)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ws.State != WebSocketState.Open) return;
                var bytes = Encoding.UTF8.GetBytes(text);
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (!(e is OperationCanceledException))
            {
                _log("GameLink send failed: " + e.Message);
            }
            catch (OperationCanceledException) { }
            finally { _sendLock.Release(); }
        }

        void Run()
        {
            bool loggedFailure = false;
            int delay = 2000;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    RunOnce().GetAwaiter().GetResult();
                    loggedFailure = false;
                    delay = 2000;
                }
                catch (Exception e) when (!_cts.IsCancellationRequested)
                {
                    LastError = Unwrap(e).Message;
                    // GiftDeck not running yet is normal: only log the first failure in a row
                    if (!loggedFailure) _log($"GameLink: can't reach GiftDeck at {Url} ({LastError}), will keep trying");
                    loggedFailure = true;
                    delay = Math.Min(delay + 1000, 5000);
                }
                catch { break; }
                Connected = false;
                _cts.Token.WaitHandle.WaitOne(delay);
            }
            Connected = false;
        }

        async Task RunOnce()
        {
            using (var ws = new ClientWebSocket())
            using (var connection = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                ws.Options.KeepAliveInterval = TimeSpan.Zero; // we send our own pings, as the protocol asks
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
                {
                    timeout.CancelAfter(ConnectTimeoutMs);
                    await ws.ConnectAsync(new Uri(Url), timeout.Token).ConfigureAwait(false);
                }
                _ws = ws;
                _connectionCts = connection;
                _lastReceived = Environment.TickCount;
                Connected = true;
                ConnectCount++;
                LastError = "";
                _log("GameLink: connected to GiftDeck at " + Url);

                try
                {
                    await SendAsync(Json.Write(_hello())).ConfigureAwait(false);
                    var keepAlive = KeepAlive(ws, connection);
                    await ReceiveLoop(ws, connection.Token).ConfigureAwait(false);
                    connection.Cancel();
                    try { await keepAlive.ConfigureAwait(false); } catch { }
                }
                catch (OperationCanceledException) when (!_cts.IsCancellationRequested)
                {
                    // Reconnect requested, or the connection went quiet
                }
                finally
                {
                    Connected = false;
                    _ws = null;
                    _connectionCts = null;
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); }
                        catch { }
                    }
                    _log("GameLink: disconnected from GiftDeck");
                }
            }
        }

        async Task KeepAlive(ClientWebSocket ws, CancellationTokenSource connection)
        {
            string ping = Json.Write(new Dictionary<string, object> { ["type"] = "ping" });
            while (!connection.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(PingSeconds * 1000, connection.Token).ConfigureAwait(false);
                if (unchecked(Environment.TickCount - _lastReceived) > SilenceSeconds * 1000)
                {
                    _log("GameLink: nothing heard from GiftDeck for " + SilenceSeconds + " s, reconnecting");
                    connection.Cancel();
                    return;
                }
                await SendAsync(ping).ConfigureAwait(false);
            }
        }

        async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                _lastReceived = Environment.TickCount;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    message.SetLength(0);
                    continue;
                }
                string text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                try { Handle(text); }
                catch (Exception e) { _log("GameLink: couldn't read a message from GiftDeck: " + e.Message); }
            }
        }

        void Handle(string text)
        {
            var msg = Json.ParseObject(text);
            if (msg == null) return;
            switch (Json.Str(msg, "type"))
            {
                case "ping":
                    Send(new Dictionary<string, object> { ["type"] = "pong" });
                    break;
                case "trigger":
                    var t = new Trigger
                    {
                        Id = Json.Str(msg, "id") ?? "",
                        Command = Json.Str(msg, "command") ?? "",
                        User = Json.Str(msg, "user") ?? "",
                        Gift = Json.Str(msg, "gift") ?? "",
                        Count = Math.Max(1, Json.Int(msg, "count", 1)),
                    };
                    if (Json.Get(msg, "args") is Dictionary<string, object> args) t.Args = args;
                    Triggers.Enqueue(t);
                    break;
                // "pong" and anything newer than this script: nothing to do
            }
        }

        static Exception Unwrap(Exception e)
        {
            while ((e is AggregateException || e is WebSocketException) && e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
