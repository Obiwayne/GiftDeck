using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GiftDeck.Models;

namespace GiftDeck.Services;

// GameLink: the local connection game mods use to receive commands from GiftDeck (ws://127.0.0.1:21216/),
// plus built-in targets. See docs/v2.1/plan.md for the protocol.
//
// The server is an HttpListener on 127.0.0.1 (http.sys lets a normal, non-admin user listen on
// localhost prefixes, like the overlay server does), upgraded to WebSockets. Only this PC can connect.
public class GameLinkService
{
    public const int Port = 21216;
    static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(15);
    static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(50); // three missed pings
    const int MaxMessageBytes = 8 * 1024 * 1024;                   // a hello with 500+ commands is ~100 KB

    // Where the game packs live (Packs\<game>\pack.json, commands.json). Next to the exe; tests point it elsewhere.
    public static string PacksDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Packs");

    readonly int _port;
    readonly List<IGameTarget> _targets = new List<IGameTarget>();
    readonly ConcurrentDictionary<ModTarget, byte> _live = new ConcurrentDictionary<ModTarget, byte>();
    HttpListener _listener;
    CancellationTokenSource _cts;

    public event Action Changed;
    public bool Running { get; private set; }
    public string LastError { get; private set; }
    public string Url => $"ws://127.0.0.1:{_port}/";

    public GameLinkService(int port = Port)
    {
        _port = port;
        // Lets an event's summary say "Kickflip" instead of "player_kickflip".
        RuleAction.GameCommandName = FriendlyName;
    }

    public IReadOnlyList<IGameTarget> Targets { get { lock (_targets) return _targets.ToList(); } }

    // ---------------------------------------------------------------- server

    public void Start()
    {
        if (Running) return;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
            _listener.Prefixes.Add($"http://localhost:{_port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            Running = true;
            LastError = null;
            _ = Task.Run(AcceptLoop);
            _ = Task.Run(KeepAlive);
            Log.Write("Game mods can connect to MayhemDeck at " + Url);
        }
        catch (Exception e)
        {
            Running = false;
            LastError = e.Message;
            Log.Write("GameLink (game mod connection) could not start: " + e.Message);
        }
    }

    public void Stop()
    {
        if (!Running) return;
        Running = false;
        try { _cts?.Cancel(); } catch { }
        foreach (var t in _live.Keys) { t.MarkDisconnected(); _ = t.CloseAsync("MayhemDeck is closing"); }
        _live.Clear();
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        Changed?.Invoke();
    }

    async Task AcceptLoop()
    {
        while (Running && !_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { if (!Running || _cts.IsCancellationRequested) break; continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    async Task HandleAsync(HttpListenerContext ctx)
    {
        if (!ctx.Request.IsWebSocketRequest)
        {
            // A plain browser visit: say what this is instead of an empty error.
            try
            {
                var text = Encoding.UTF8.GetBytes("MayhemDeck GameLink. Game mods connect here with a WebSocket: " + Url);
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.StatusCode = 426; // Upgrade Required
                ctx.Response.OutputStream.Write(text, 0, text.Length);
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
            return;
        }

        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception e) { Log.Write("A game mod could not connect: " + e.Message); return; }

        ModTarget target = null;
        try
        {
            var buffer = new byte[16 * 1024];
            var message = new MemoryStream();
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Bye", CancellationToken.None); } catch { }
                    break;
                }
                message.Write(buffer, 0, r.Count);
                if (message.Length > MaxMessageBytes) { Log.Write("A game mod sent a message that is too big; disconnected it"); break; }
                if (!r.EndOfMessage) continue;
                var text = r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length) : null;
                message.SetLength(0);
                if (text == null) continue;
                if (target != null) target.LastHeard = DateTime.UtcNow;
                target = await OnMessageAsync(ws, target, text) ?? target;
            }
        }
        catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException || e is HttpListenerException) { }
        catch (Exception e) { Log.Write("GameLink connection error: " + e.Message); }
        finally
        {
            if (target != null)
            {
                _live.TryRemove(target, out _);
                target.MarkDisconnected();
                if (!target.Replaced && Running) Log.Write($"{target.Name} disconnected ({ShortGameName(target.Game)})");
                Changed?.Invoke();
            }
            try { ws.Abort(); } catch { }
            try { ws.Dispose(); } catch { }
        }
    }

    // Handles one JSON message; returns the new target when it was a hello.
    async Task<ModTarget> OnMessageAsync(WebSocket ws, ModTarget target, string text)
    {
        JsonObject o;
        try { o = JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
        if (o == null) return null;
        var type = Str(o["type"]).ToLowerInvariant();

        if (type == "hello")
        {
            var game = Slug(Str(o["game"]));
            var mod = Slug(Str(o["mod"]));
            if (game.Length == 0 || mod.Length == 0)
            {
                Log.Write("A game mod said hello without a game or mod name; ignored it");
                return null;
            }
            var commands = ParseCommands(o["commands"] as JsonArray);
            var fresh = new ModTarget(ws, game, mod, Str(o["name"]), Str(o["version"]), commands);
            if (target != null)
            {
                // A second hello on the same connection (e.g. the mod reloaded its scripts): the new one counts.
                target.Replaced = true;
                target.MarkDisconnected();
                _live.TryRemove(target, out _);
            }

            ModTarget older;
            lock (_targets)
            {
                older = _targets.FirstOrDefault(t => t.Id == fresh.Id) as ModTarget;
                _targets.RemoveAll(t => t.Id == fresh.Id);
                _targets.Add(fresh);
            }
            if (older != null && older != target && older.Connected)
            {
                // The mod reconnected (e.g. the game restarted) before the old connection timed out.
                older.Replaced = true;
                older.MarkDisconnected();
                _live.TryRemove(older, out _);
                _ = older.CloseAsync("Replaced by a newer connection");
            }
            _live[fresh] = 0;
            Log.Write($"{fresh.Name} connected ({ShortGameName(game)}, {commands.Count} {(commands.Count == 1 ? "command" : "commands")})");
            Changed?.Invoke();
            return fresh;
        }

        if (type == "ping")
        {
            if (target != null) await target.SendAsync(new JsonObject { ["type"] = "pong" });
            else await SendRawAsync(ws, "{\"type\":\"pong\"}");
            return null;
        }
        if (target == null) return null; // everything else needs a hello first

        switch (type)
        {
            case "result":
                target.OnResult(o);
                break;
            case "status":
                target.Status = Str(o["text"]);
                Changed?.Invoke();
                break;
        }
        return null;
    }

    static async Task SendRawAsync(WebSocket ws, string text)
    {
        try { await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None); }
        catch { }
    }

    async Task KeepAlive()
    {
        while (Running && !_cts.IsCancellationRequested)
        {
            try { await Task.Delay(PingEvery, _cts.Token); } catch { break; }
            foreach (var t in _live.Keys)
            {
                if (DateTime.UtcNow - t.LastHeard > DeadAfter)
                {
                    // The game froze or was killed without closing the connection.
                    _ = t.CloseAsync("No answer to pings");
                    continue;
                }
                _ = t.SendAsync(new JsonObject { ["type"] = "ping" });
            }
        }
    }

    // ---------------------------------------------------------------- registry

    // Built-in targets (e.g. Minecraft over RCON) register themselves here.
    public void Register(IGameTarget target)
    {
        lock (_targets) { _targets.RemoveAll(t => t.Id == target.Id); _targets.Add(target); }
        Changed?.Invoke();
    }

    public void Unregister(string id)
    {
        lock (_targets) _targets.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    public IGameTarget Find(string id) { lock (_targets) return _targets.FirstOrDefault(t => t.Id == id); }

    // Runs one command. template fills {user}, {gift} etc. into text argument values.
    public async Task<GameResult> RunAsync(string targetId, string command, string argsJson, LiveEvent e, Func<string, string> template)
    {
        var target = Find(targetId);
        if (target == null || !target.Connected) return new GameResult(false, (target?.Name ?? TargetName(targetId)) + " isn't connected");
        var info = target.Commands.FirstOrDefault(c => c.Id == command);
        var args = new JsonObject();
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try
            {
                if (JsonNode.Parse(argsJson) is JsonObject o)
                    foreach (var kv in o)
                        args[kv.Key] = kv.Value is JsonValue v && v.TryGetValue<string>(out var text) ? JsonValue.Create(template(text)) : kv.Value?.DeepClone();
            }
            catch (Exception ex) { return new GameResult(false, "Bad arguments: " + ex.Message); }
        }
        if (info != null)
        {
            foreach (var a in info.Args)
            {
                // An argument added to the mod after the event was saved: use its default.
                if (!args.ContainsKey(a.Name) && a.Default.Length > 0) args[a.Name] = JsonValue.Create(template(a.Default));
                // Number arguments go out as JSON numbers, also when they came from a template like {count}.
                if (a.Type == "number" && args[a.Name] is JsonValue nv && nv.TryGetValue<string>(out var s)
                    && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    args[a.Name] = d == Math.Floor(d) && Math.Abs(d) < long.MaxValue ? JsonValue.Create((long)d) : JsonValue.Create(d);
            }
        }
        return await target.RunAsync(command, args, e);
    }

    // ---------------------------------------------------------------- catalogs

    // Games that have commands: from a pack's commands.json or a mod that connected. Id + display name.
    public List<(string Id, string Name)> Games()
    {
        var ids = new List<string>();
        try
        {
            if (Directory.Exists(PacksDir))
                foreach (var dir in Directory.GetDirectories(PacksDir))
                    if (StaticCatalog(Path.GetFileName(dir)).Count > 0) ids.Add(Path.GetFileName(dir));
        }
        catch { }
        foreach (var t in Targets) if (!ids.Contains(t.Game)) ids.Add(t.Game);
        return ids.Select(id => (id, GameName(id))).OrderBy(g => g.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Every command for a game: from connected (or earlier connected) mods, plus the pack's commands.json
    // so events can be set up while the game is closed. A mod's own list wins over the file for the same command.
    public List<GameCatalogItem> CatalogFor(string game)
    {
        var items = new Dictionary<(string, string), GameCatalogItem>();
        foreach (var s in StaticCatalog(game))
            items[(s.TargetId, s.Command.Id)] = new GameCatalogItem(s.TargetId, TargetName(s.TargetId), false, s.Command);
        foreach (var t in Targets.Where(t => t.Game == game))
            foreach (var c in t.Commands)
                items[(t.Id, c.Id)] = new GameCatalogItem(t.Id, t.Name, t.Connected, c);
        foreach (var item in items.Values)
        {
            var t = Find(item.TargetId);
            if (t != null) { item.TargetName = t.Name; item.Online = t.Connected; }
        }
        return items.Values
            .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public GameCommandInfo FindCommand(string targetId, string commandId)
    {
        if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(commandId)) return null;
        var live = Find(targetId)?.Commands.FirstOrDefault(c => c.Id == commandId);
        if (live != null) return live;
        return StaticCatalog(GameOf(targetId)).FirstOrDefault(s => s.TargetId == targetId && s.Command.Id == commandId)?.Command;
    }

    public string FriendlyName(string targetId, string commandId)
    {
        var c = FindCommand(targetId, commandId);
        return c == null || string.IsNullOrWhiteSpace(c.Name) ? null : c.Name;
    }

    public static string GameOf(string targetId) => (targetId ?? "").Split(':')[0];

    // The pack's display name (pack.json "name"), or the id when there is no pack.
    public string GameName(string game)
    {
        var pack = ReadJson(Path.Combine(PacksDir, game, "pack.json")) as JsonObject;
        var name = Str(pack?["name"]);
        return name.Length > 0 ? name : game;
    }

    // For log lines: "GTA V (Legacy)" -> "GTA V", so "Chaos Mod V connected (GTA V, 512 commands)" reads well.
    string ShortGameName(string game)
    {
        var name = GameName(game);
        var i = name.IndexOf(" (");
        return i > 0 ? name.Substring(0, i) : name;
    }

    readonly ConcurrentDictionary<string, string> _knownNames = new ConcurrentDictionary<string, string>();

    // A target's display name: the connected mod's, else the name its commands.json gave, else the mod id.
    public string TargetName(string targetId)
    {
        var t = Find(targetId);
        if (t != null) return t.Name;
        StaticCatalog(GameOf(targetId));
        if (_knownNames.TryGetValue(targetId ?? "", out var n)) return n;
        var i = (targetId ?? "").IndexOf(':');
        return i >= 0 ? targetId.Substring(i + 1) : targetId;
    }

    record StaticCommand(string TargetId, GameCommandInfo Command);
    readonly ConcurrentDictionary<string, (DateTime Stamp, List<StaticCommand> List)> _static = new ConcurrentDictionary<string, (DateTime, List<StaticCommand>)>();

    // Packs\<game>\commands.json, re-read when the file changes. Accepted shapes:
    //   [ {command}, ... ]                                   commands for the pack's first target
    //   [ {command, "target":"gta5:chaosmod"}, ... ]         per-command target
    //   {"target":"gta5:chaosmod","name":"Chaos Mod V","commands":[...]}
    //   [ {"target":..., "name":..., "commands":[...]}, ... ]  several mods in one file
    // The default target is pack.json "targets"[0], else "<game>:<game>".
    List<StaticCommand> StaticCatalog(string game)
    {
        if (string.IsNullOrEmpty(game)) return new List<StaticCommand>();
        var path = Path.Combine(PacksDir, game, "commands.json");
        DateTime stamp;
        try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { stamp = DateTime.MinValue; }
        if (_static.TryGetValue(game, out var cached) && cached.Stamp == stamp) return cached.List;

        var list = new List<StaticCommand>();
        if (stamp != DateTime.MinValue)
        {
            var pack = ReadJson(Path.Combine(PacksDir, game, "pack.json")) as JsonObject;
            var fallback = (pack?["targets"] as JsonArray)?.Select(Str).FirstOrDefault(s => s.Contains(':')) ?? game + ":" + game;
            var root = ReadJson(path);
            if (root == null) Log.Write($"Could not read the command list for {game} (Packs\\{game}\\commands.json)");

            void Group(JsonObject g, string defaultTarget)
            {
                var target = Str(g["target"]);
                if (target.Length == 0) target = defaultTarget;
                var name = Str(g["name"]);
                if (name.Length > 0) _knownNames[target] = name;
                AddCommands(g["commands"] as JsonArray, target);
            }
            void AddCommands(JsonArray arr, string defaultTarget)
            {
                if (arr == null) return;
                foreach (var node in arr)
                {
                    if (node is not JsonObject c) continue;
                    if (c["commands"] is JsonArray) { Group(c, defaultTarget); continue; }
                    var cmd = ParseCommand(c);
                    if (cmd == null) continue;
                    var target = Str(c["target"]);
                    list.Add(new StaticCommand(target.Length > 0 ? target : defaultTarget, cmd));
                }
            }

            if (root is JsonArray a) AddCommands(a, fallback);
            else if (root is JsonObject o) Group(o, fallback);
        }
        _static[game] = (stamp, list);
        return list;
    }

    // ---------------------------------------------------------------- parsing

    // Lenient: mods written in any language send these, so numbers or booleans where text is expected are fine.
    public static List<GameCommandInfo> ParseCommands(JsonArray arr)
    {
        var list = new List<GameCommandInfo>();
        if (arr == null) return list;
        var seen = new HashSet<string>();
        foreach (var node in arr)
            if (node is JsonObject o && ParseCommand(o) is { } c && seen.Add(c.Id)) list.Add(c);
        return list;
    }

    static GameCommandInfo ParseCommand(JsonObject o)
    {
        var id = Str(o["id"]);
        if (id.Length == 0) return null;
        var c = new GameCommandInfo
        {
            Id = id,
            Name = Str(o["name"]) is { Length: > 0 } n ? n : id,
            Category = Str(o["category"]) is { Length: > 0 } cat ? cat : "Other",
            Description = Str(o["description"]),
        };
        if (o["args"] is JsonArray args)
            foreach (var an in args)
            {
                if (an is not JsonObject a) continue;
                var name = Str(a["name"]);
                if (name.Length == 0) continue;
                var type = Str(a["type"]).ToLowerInvariant();
                var arg = new GameCommandArg
                {
                    Name = name,
                    Label = Str(a["label"]) is { Length: > 0 } l ? l : name,
                    Type = type == "number" || type == "choice" ? type : "text",
                    Default = Str(a["default"]),
                };
                if (a["choices"] is JsonArray ch) arg.Choices = ch.Select(Str).Where(s => s.Length > 0).ToList();
                if (arg.Type == "choice" && arg.Choices.Count == 0) arg.Type = "text";
                c.Args.Add(arg);
            }
        return c;
    }

    static string Str(JsonNode n)
    {
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<string>(out var s)) return s.Trim();
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        if (v.TryGetValue<double>(out var d)) return d.ToString(CultureInfo.InvariantCulture);
        return v.ToJsonString();
    }

    // Ids go into target ids ("gta5:chaosmod"): lower case, no ':' or spaces.
    static string Slug(string s) => new string((s ?? "").Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.').ToArray());

    static JsonNode ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch { return null; }
    }
}

// One command in a game's catalog, with the mod that runs it (for the event editor).
public class GameCatalogItem
{
    public GameCatalogItem(string targetId, string targetName, bool online, GameCommandInfo command)
    {
        TargetId = targetId; TargetName = targetName; Online = online; Command = command;
    }
    public string TargetId { get; }
    public string TargetName { get; set; }
    public bool Online { get; set; }
    public GameCommandInfo Command { get; }
    public string Id => Command.Id;
    public string Name => Command.Name;
    public string Category => Command.Category;
    public string Description => Command.Description;
    public string Detail => TargetName + (string.IsNullOrWhiteSpace(Description) ? "" : "  ·  " + Description);
}
