using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GiftDeckGTA.Tests
{
    // Runs GiftDeck GTA's GameLink client against a stand-in for GiftDeck's server, following the protocol in
    // docs/v2.1/plan.md, and checks the command catalog. Exit code 0 = everything passed.
    static class Program
    {
        static int _failures;

        static int Main(string[] args)
        {
            try
            {
                string mode = args.Length > 0 ? args[0] : "test";
                switch (mode)
                {
                    case "dump":
                        Dump(args.Length > 1 ? args[1] : "commands.json");
                        return 0;
                    case "connect":
                        // Acts as the script against a real GiftDeck GameLink server on the given port
                        // (never 21216 while the user's GiftDeck is running), answering like the game would.
                        return Connect(int.Parse(args[1]), args.Length > 2 ? int.Parse(args[2]) : 3);
                    case "check":
                        CheckCatalog();
                        if (args.Length > 1) CheckFile(args[1]);
                        break;
                    default:
                        CheckCatalog();
                        TestGameLink().GetAwaiter().GetResult();
                        break;
                }
            }
            catch (Exception e)
            {
                Fail("Unexpected error: " + e);
            }
            Console.WriteLine(_failures == 0 ? "ALL PASSED" : $"{_failures} FAILED");
            return _failures == 0 ? 0 : 1;
        }

        static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("  PASS  " + what);
            else Fail(what);
        }

        static void Fail(string what)
        {
            _failures++;
            Console.WriteLine("  FAIL  " + what);
        }

        // ---------------------------------------------------------------- catalog

        static void Dump(string path)
        {
            File.WriteAllText(path, Json.Write(Catalog.PackCommands(), true) + "\n", new UTF8Encoding(false));
            Console.WriteLine($"Wrote {Catalog.Commands.Count} commands to {Path.GetFullPath(path)}");
        }

        static void CheckCatalog()
        {
            Console.WriteLine("Catalog");
            var cmds = Catalog.Commands;
            Check(cmds.Count > 0, $"{cmds.Count} commands");
            var dupes = cmds.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Check(dupes.Count == 0, "command ids are unique" + (dupes.Count > 0 ? ": " + string.Join(", ", dupes) : ""));
            foreach (var c in cmds)
            {
                var problems = new List<string>();
                if (string.IsNullOrWhiteSpace(c.Id) || c.Id.Any(ch => !(char.IsLower(ch) || char.IsDigit(ch) || ch == '_'))) problems.Add("id must be lower_snake_case");
                if (string.IsNullOrWhiteSpace(c.Name)) problems.Add("no name");
                if (!Catalog.Categories.Contains(c.Category)) problems.Add("unknown category " + c.Category);
                foreach (var a in c.Args)
                {
                    if (string.IsNullOrWhiteSpace(a.Name) || string.IsNullOrWhiteSpace(a.Label)) problems.Add("arg without name/label");
                    if (a.Type != "text" && a.Type != "number" && a.Type != "choice") problems.Add($"arg {a.Name}: bad type {a.Type}");
                    if (a.Type == "choice" && (a.Choices.Length == 0 || !a.Choices.Contains(a.Default))) problems.Add($"arg {a.Name}: default not in choices");
                    if (a.Type == "number" && !double.TryParse(a.Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) problems.Add($"arg {a.Name}: default isn't a number");
                }
                if (problems.Count > 0) Fail($"{c.Id}: {string.Join("; ", problems)}");
            }
            Check(true, "every command has a valid id, name, category and args");
            foreach (var cat in Catalog.Categories)
                Console.WriteLine($"        {cat,-9} {cmds.Count(c => c.Category == cat)}");

            // The hello message must survive a round trip through a JSON parser
            var hello = Json.ParseObject(Json.Write(Catalog.Hello()));
            Check(hello != null && Json.Str(hello, "type") == "hello" && Json.Str(hello, "game") == "gta5" && Json.Str(hello, "mod") == "giftdeck"
                  && Json.Str(hello, "name") == "GiftDeck GTA", "hello has type/game/mod/name");
            Check(Json.Get(hello, "commands") is object[] list && list.Length == cmds.Count, "hello lists every command");
        }

        static void CheckFile(string path)
        {
            Console.WriteLine("commands.json");
            string text = File.ReadAllText(path);
            string expected = Json.Write(Catalog.PackCommands(), true) + "\n";
            Check(text.Replace("\r\n", "\n") == expected, $"{path} matches the catalog in Catalog.cs (run 'dump' to regenerate)");
            var groups = Json.Parse(text) as object[];
            var group = groups?.OfType<Dictionary<string, object>>().FirstOrDefault(g => Json.Str(g, "target") == Catalog.TargetId);
            var commands = Json.Get(group, "commands") as object[];
            Check(group != null && Json.Str(group, "name") == Catalog.Name, "it parses as an array with a gta5:giftdeck group");
            Check(commands != null && commands.Length == Catalog.Commands.Count
                  && commands.All(o => o is Dictionary<string, object> d && d.ContainsKey("id") && d.ContainsKey("args")),
                "the group lists every command with its args");
        }

        // ---------------------------------------------------------------- GameLink

        static int Connect(int port, int answer)
        {
            var client = new GameLinkClient("127.0.0.1", port, Catalog.Hello, m => Console.WriteLine("[client] " + m));
            client.Start();
            int answered = 0;
            var until = DateTime.UtcNow.AddSeconds(90);
            while (answered < answer && DateTime.UtcNow < until)
            {
                while (client.Triggers.TryDequeue(out var t))
                {
                    var info = Catalog.Find(t.Command);
                    string args = string.Join(", ", t.Args.Select(kv => kv.Key + "=" + kv.Value));
                    Console.WriteLine($"[client] trigger {t.Command} ({args}) from {t.User} / {t.Gift} x{t.Count}");
                    if (info == null) client.SendResult(t.Id, false, "GiftDeck GTA doesn't know the command '" + t.Command + "'");
                    else client.SendResult(t.Id, true, $"ran {info.Name} ({args})");
                    answered++;
                }
                Thread.Sleep(16);
            }
            Thread.Sleep(300); // let the last result go out
            client.Stop();
            return answered >= answer ? 0 : 1;
        }

        static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static async Task TestGameLink()
        {
            Console.WriteLine("GameLink");
            int port = FreePort();
            var server = new TestServer(port);
            server.Start();
            Console.WriteLine($"        test server on ws://localhost:{port}/");

            var log = new List<string>();
            var client = new GameLinkClient("localhost", port, Catalog.Hello, m => { lock (log) log.Add(m); Console.WriteLine("        [client] " + m); });
            client.Start();

            // Stand-in for the script's tick: run whatever arrives and answer
            var cts = new CancellationTokenSource();
            var executed = new List<Trigger>();
            var tick = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    while (client.Triggers.TryDequeue(out var t))
                    {
                        lock (executed) executed.Add(t);
                        var info = Catalog.Find(t.Command);
                        if (info == null) client.SendResult(t.Id, false, "GiftDeck GTA doesn't know the command '" + t.Command + "'");
                        else client.SendResult(t.Id, true, $"ran {info.Name} for {t.User} ({string.Join(", ", t.Args.Select(kv => kv.Key + "=" + kv.Value))})");
                    }
                    await Task.Delay(16);
                }
            });

            // 1. hello
            var conn = await server.NextConnection(TimeSpan.FromSeconds(10));
            Check(conn != null, "client connects to the server");
            if (conn == null) return;
            var hello = await conn.Receive(TimeSpan.FromSeconds(5));
            Console.WriteLine("        <- " + Short(hello));
            Check(hello != null && Json.Str(hello, "type") == "hello", "first message is hello");
            Check(Json.Str(hello, "game") == "gta5" && Json.Str(hello, "mod") == "giftdeck" && Json.Str(hello, "name") == "GiftDeck GTA", "hello: game gta5, mod giftdeck, name GiftDeck GTA");
            var commands = Json.Get(hello, "commands") as object[];
            Check(commands != null && commands.Length == Catalog.Commands.Count, $"hello carries {commands?.Length} commands");
            var spawn = commands?.OfType<Dictionary<string, object>>().FirstOrDefault(c => Json.Str(c, "id") == "spawn_vehicle");
            Check(spawn != null && Json.Get(spawn, "args") is object[] a && a.Length == 2, "spawn_vehicle is listed with its args");
            await Wait(() => client.Connected, 2000);
            Check(client.Connected, "client reports Connected");

            // 2. triggers -> results
            var triggers = new[]
            {
                Trigger("nothing", null, "RoseQueen", "Rose", 1),
                Trigger("spawn_vehicle", new Dictionary<string, object> { ["model"] = "rhino", ["enter"] = "yes" }, "TankFan", "Doughnut", 1),
                Trigger("add_money", new Dictionary<string, object> { ["amount"] = 5000 }, "Rich", "Hand Hearts", 3),
                Trigger("no_such_command", null, "Oops", "GG", 1),
            };
            foreach (var t in triggers)
            {
                await conn.Send(t);
                Console.WriteLine("        -> " + Json.Write(t));
            }
            var results = new Dictionary<string, Dictionary<string, object>>();
            for (int i = 0; i < triggers.Length; i++)
            {
                var r = await conn.Receive(TimeSpan.FromSeconds(5));
                if (r == null) break;
                Console.WriteLine("        <- " + Json.Write(r));
                if (Json.Str(r, "type") == "result") results[Json.Str(r, "id")] = r;
                else i--; // e.g. a status message
            }
            Check(results.Count == triggers.Length, $"a result for every trigger ({results.Count}/{triggers.Length})");
            bool Ok(Dictionary<string, object> t) => results.TryGetValue((string)t["id"], out var r) && Json.Get(r, "ok") is bool b && b;
            Check(Ok(triggers[0]) && Ok(triggers[1]) && Ok(triggers[2]), "known commands answer ok:true with the same id");
            Check(!Ok(triggers[3]) && results.ContainsKey((string)triggers[3]["id"]), "unknown command answers ok:false");
            lock (executed)
            {
                var sv = executed.FirstOrDefault(t => t.Command == "spawn_vehicle");
                Check(sv != null && (string)sv.Args["model"] == "rhino" && sv.User == "TankFan" && sv.Gift == "Doughnut", "trigger args, user and gift reach the script");
                var money = executed.FirstOrDefault(t => t.Command == "add_money");
                Check(money != null && money.Count == 3 && Convert.ToInt32(money.Args["amount"]) == 5000, "numeric args and count arrive intact");
            }

            // 3. keep-alive both ways
            await conn.Send(new Dictionary<string, object> { ["type"] = "ping" });
            var pong = await conn.Receive(TimeSpan.FromSeconds(5));
            Check(pong != null && Json.Str(pong, "type") == "pong", "server ping gets a pong");
            Console.WriteLine($"        waiting up to {GameLinkClient.PingSeconds + 5} s for the client's own ping...");
            var ping = await conn.Receive(TimeSpan.FromSeconds(GameLinkClient.PingSeconds + 5));
            Check(ping != null && Json.Str(ping, "type") == "ping", $"client pings every {GameLinkClient.PingSeconds} s");
            await conn.Send(new Dictionary<string, object> { ["type"] = "pong" });

            // 4. GiftDeck closes (or restarts): the client reconnects and says hello again
            await conn.Close();
            await Wait(() => !client.Connected, 3000);
            Check(!client.Connected, "client notices the server closed the connection");
            var conn2 = await server.NextConnection(TimeSpan.FromSeconds(15));
            Check(conn2 != null, "client reconnects by itself");
            var hello2 = conn2 == null ? null : await conn2.Receive(TimeSpan.FromSeconds(5));
            Check(hello2 != null && Json.Str(hello2, "type") == "hello", "and sends hello again");

            // 5. messages from a newer GiftDeck that this script doesn't know are ignored
            if (conn2 != null)
            {
                await conn2.Send(new Dictionary<string, object> { ["type"] = "something_new", ["x"] = 1 });
                await conn2.Send(Trigger("heal", null, "Medic", "Rose", 1));
                var r = await conn2.Receive(TimeSpan.FromSeconds(5));
                Check(r != null && Json.Str(r, "type") == "result" && Json.Get(r, "ok") is bool b && b, "unknown message types are ignored, the connection stays usable");
            }

            // 6. the script stopping closes the socket cleanly
            client.Stop();
            var closed = conn2 == null ? false : await conn2.WaitClosed(TimeSpan.FromSeconds(5));
            Check(closed, "client closes the connection when the script stops");

            cts.Cancel();
            try { await tick; } catch { }
            server.Stop();
        }

        static Dictionary<string, object> Trigger(string command, Dictionary<string, object> args, string user, string gift, int count) =>
            new Dictionary<string, object>
            {
                ["type"] = "trigger",
                ["id"] = Guid.NewGuid().ToString(),
                ["command"] = command,
                ["args"] = args ?? new Dictionary<string, object>(),
                ["user"] = user,
                ["gift"] = gift,
                ["count"] = count,
            };

        static string Short(Dictionary<string, object> msg)
        {
            if (msg == null) return "(nothing)";
            string s = Json.Write(msg);
            return s.Length > 220 ? s.Substring(0, 220) + $"... ({s.Length} chars)" : s;
        }

        static async Task Wait(Func<bool> condition, int ms)
        {
            for (int waited = 0; waited < ms && !condition(); waited += 50) await Task.Delay(50);
        }
    }

    // A minimal WebSocket server standing in for GiftDeck's GameLink (HttpListener, localhost only).
    class TestServer
    {
        readonly HttpListener _listener = new HttpListener();
        readonly System.Collections.Concurrent.BlockingCollection<Connection> _connections = new System.Collections.Concurrent.BlockingCollection<Connection>();

        public TestServer(int port) => _listener.Prefixes.Add($"http://localhost:{port}/");

        public void Start()
        {
            _listener.Start();
            Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { return; }
                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                        continue;
                    }
                    var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
                    _connections.Add(new Connection(ws));
                }
            });
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch { }
        }

        public Task<Connection> NextConnection(TimeSpan timeout) =>
            Task.Run(() => _connections.TryTake(out var c, timeout) ? c : null);
    }

    class Connection
    {
        readonly WebSocket _ws;
        readonly System.Collections.Concurrent.BlockingCollection<Dictionary<string, object>> _inbox = new System.Collections.Concurrent.BlockingCollection<Dictionary<string, object>>();
        readonly TaskCompletionSource<bool> _closed = new TaskCompletionSource<bool>();

        public Connection(WebSocket ws)
        {
            _ws = ws;
            Task.Run(ReadLoop);
        }

        async Task ReadLoop()
        {
            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        break;
                    }
                    message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;
                    _inbox.Add(Json.ParseObject(Encoding.UTF8.GetString(message.ToArray())));
                    message.SetLength(0);
                }
            }
            catch { }
            _closed.TrySetResult(true);
        }

        public Task<Dictionary<string, object>> Receive(TimeSpan timeout) =>
            Task.Run(() => _inbox.TryTake(out var m, timeout) ? m : null);

        public Task Send(Dictionary<string, object> msg) =>
            _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(Json.Write(msg))), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task Close()
        {
            try { await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            await WaitClosed(TimeSpan.FromSeconds(3));
        }

        public async Task<bool> WaitClosed(TimeSpan timeout) =>
            await Task.WhenAny(_closed.Task, Task.Delay(timeout)) == _closed.Task;
    }
}
