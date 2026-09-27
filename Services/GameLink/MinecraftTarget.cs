using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GiftDeck.Models;

namespace GiftDeck.Services;

// A Minecraft command from Packs/minecraft/commands.json. Besides the shared GameLink fields it carries the
// server command(s) to run. Template tokens:
//   {player}           the player set on the Games page (a Minecraft name), or @p when none is set
//   {arg}              an argument's value (numbers are checked, choices must be in the list, text loses line breaks)
//   {arg:str}          a text argument as a quoted string: "Bob \"the\" builder"
//   {arg:text}         a text argument as a chat text component in a command (title, tellraw)
//   {arg:nbttext}      a text component inside NBT (CustomName): '"Bob"' before 1.21.5, "Bob" after
//   {rand:-4:4}        a random whole number, new for each run (spreads mobs and TNT around the player)
//   {?arg}...{/arg}    kept only when the argument isn't empty (e.g. the optional name tag)
// "repeat": the name of a number argument that says how many times to run it (capped by "maxRepeat").
// "legacy": [{"before":"1.20.3","run":...}] older syntax for servers older than that version.
public class MinecraftCommand : GameCommandInfo
{
    public List<string> Run { get; set; } = new List<string>();
    public string Repeat { get; set; } = "";
    public int MaxRepeat { get; set; } = 20;
    public string Warning { get; set; } = "";
    public List<MinecraftLegacyRun> Legacy { get; } = new List<MinecraftLegacyRun>();

    // The syntax for this server: the oldest "legacy" entry the server is older than, else the current one.
    public MinecraftLegacyRun LegacyFor(Version server) =>
        server == null ? null : Legacy.Where(l => server < l.Before).OrderBy(l => l.Before).FirstOrDefault();

    public List<string> RunFor(Version server) => LegacyFor(server)?.Run ?? Run;
}

// Older syntax for servers before a version. Values renames argument values (e.g. game rules were
// camelCase before 1.21.11: keep_inventory was keepInventory).
public class MinecraftLegacyRun
{
    public Version Before { get; set; }
    public List<string> Run { get; set; } = new List<string>();
    public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

// GameLink target "minecraft:server": the local Paper server GiftDeck runs, reached over RCON.
public sealed class MinecraftTarget : IGameTarget, IDisposable
{
    public const string TargetId = "minecraft:server";

    public string Id => TargetId;
    public string Game => "minecraft";
    public string Name => "Minecraft server";
    public bool Connected => Server.RconConnected;
    public string Status => Server.StateText;
    public IReadOnlyList<GameCommandInfo> Commands => _commands;

    public MinecraftServer Server { get; }
    public MinecraftSettings Settings => Server.Settings;
    public event Action Changed;

    readonly List<MinecraftCommand> _commands;
    readonly string _settingsFile; // storage name to save settings to (null in tests)

    public MinecraftTarget(MinecraftServer server, string commandsPath, string settingsFile = null)
    {
        Server = server;
        _settingsFile = settingsFile;
        _commands = File.Exists(commandsPath) ? LoadCommands(File.ReadAllText(commandsPath)) : new List<MinecraftCommand>();
        Server.Changed += () => Changed?.Invoke();
    }

    // The one GiftDeck uses: settings in minecraft.json, commands from the pack next to the exe.
    public static MinecraftTarget CreateDefault()
    {
        var settings = Storage.Load<MinecraftSettings>("minecraft.json") ?? new MinecraftSettings();
        bool fresh = string.IsNullOrEmpty(settings.RconPassword);
        var server = new MinecraftServer(settings);
        var t = new MinecraftTarget(server, Path.Combine(AppContext.BaseDirectory, "Packs", "minecraft", "commands.json"), "minecraft.json");
        if (fresh) t.SaveSettings(); // keep the new random RCON password
        return t;
    }

    // Keeps an eye on the server (connects to one that is already running) once one is set up.
    public void Start()
    {
        if (Server.IsInstalled) Server.StartWatching();
    }

    public void SaveSettings()
    {
        if (_settingsFile != null) Storage.Save(_settingsFile, Settings);
    }

    // GiftDeck is closing: save the world and stop the server it started (if the user wants that).
    public void Shutdown()
    {
        Server.StopWatching();
        if (Settings.StopWithGiftDeck && Server.OwnsProcess)
            try { Server.StopAsync(TimeSpan.FromSeconds(30)).Wait(TimeSpan.FromSeconds(35)); } catch { }
        Server.Dispose();
    }

    public void Dispose() => Server.Dispose();

    // ---------------- running commands ----------------

    public async Task<GameResult> RunAsync(string command, JsonObject args, LiveEvent e)
    {
        var cmd = _commands.FirstOrDefault(c => c.Id == command);
        if (cmd == null) return new GameResult(false, $"Unknown Minecraft command \"{command}\"");
        var (lines, values, times) = Prepare(cmd, args, e, null);
        var replies = new List<string>();
        bool ok = true;
        try
        {
            for (int i = 0; i < times && ok; i++)
                foreach (var template in lines)
                {
                    var text = Render(template, cmd, values, ServerVersion);
                    var reply = CleanReply(await Server.ExecuteAsync(text));
                    // "execute at <player> run ..." says nothing at all when the player isn't there.
                    if (reply.Length == 0 && text.StartsWith("execute ")) reply = $"Nothing happened (is {values["player"]} in the game?)";
                    if (IsError(reply)) ok = false;
                    if (reply.Length > 0) replies.Add(reply);
                    if (!ok) break; // no point repeating what failed
                }
        }
        catch (Exception ex)
        {
            return new GameResult(false, ex.Message);
        }
        var distinct = replies.Distinct().ToList();
        var msg = string.Join(" | ", distinct.Take(3)) + (distinct.Count > 3 ? " | …" : "");
        if (times > 1 && replies.Count > 1 && distinct.Count == 1) msg += $" (x{replies.Count})";
        return new GameResult(ok, msg);
    }

    // The server commands one run of a pack command becomes (random offsets differ each time). player
    // replaces the configured player (the test harness aims at an armor stand).
    public List<string> Preview(string command, JsonObject args, LiveEvent e, string player = null)
    {
        var cmd = _commands.FirstOrDefault(c => c.Id == command);
        if (cmd == null) return new List<string>();
        var (lines, values, _) = Prepare(cmd, args, e, player);
        return lines.Select(l => Render(l, cmd, values, ServerVersion)).ToList();
    }

    (List<string> Lines, Dictionary<string, string> Values, int Times) Prepare(MinecraftCommand cmd, JsonObject args, LiveEvent e, string player)
    {
        var values = ArgValues(cmd, args, e);
        if (player != null) values["player"] = player;
        int times = 1;
        if (!string.IsNullOrEmpty(cmd.Repeat) && int.TryParse(values.GetValueOrDefault(cmd.Repeat), out var n))
            times = Math.Clamp(n, 1, Math.Max(1, cmd.MaxRepeat));
        var legacy = cmd.LegacyFor(ServerVersion);
        if (legacy != null)
            foreach (var key in values.Keys.ToList())
                if (legacy.Values.TryGetValue(values[key], out var old)) values[key] = old;
        return (legacy?.Run ?? cmd.Run, values, times);
    }

    // Runs a typed command (the panel's console box).
    public Task<string> RunRawAsync(string line) => Server.ExecuteAsync(line.TrimStart('/'));

    // Minecraft answers errors in plain text, starting with one of these (the rest may hold a viewer's name).
    static readonly string[] ErrorMarks =
    {
        "Unknown or incomplete command", "Incorrect argument", "No player was found", "No entity was found", "No targets matched",
        "Unable to summon", "Unknown item", "Unknown effect", "Can't find element", "Invalid ", "Expected ", "That position is not loaded",
        "Unknown game rule", "Unknown gamerule", "Could not", "Failed to", "Only players may be affected", "Nothing happened",
    };
    public static bool IsError(string reply) => ErrorMarks.Any(m => reply.StartsWith(m, StringComparison.OrdinalIgnoreCase));

    static readonly Regex ColorCodes = new Regex("§.", RegexOptions.Compiled);

    public static string StripColors(string reply) => ColorCodes.Replace(reply ?? "", "").Trim();

    // First line only (errors repeat the command with "<--[HERE]" on the next line), without colour codes.
    public static string CleanReply(string reply)
    {
        reply = StripColors(reply);
        int nl = reply.IndexOf('\n');
        return (nl >= 0 ? reply[..nl] : reply).Trim();
    }

    Version ServerVersion => ParseVersion(Server.InstalledVersion);

    public static Version ParseVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var m = Regex.Match(v, @"^(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    Dictionary<string, string> ArgValues(MinecraftCommand cmd, JsonObject args, LiveEvent e)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in cmd.Args)
        {
            string v = null;
            if (args != null && args.TryGetPropertyValue(a.Name, out var node) && node != null)
                v = node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            if (v == null) v = e != null ? RulesEngine.Template(a.Default ?? "", e) : a.Default ?? "";
            values[a.Name] = Clean(a, v);
        }
        values["player"] = PlayerSelector(Settings.PlayerName);
        return values;
    }

    static string Clean(GameCommandArg a, string v)
    {
        v = new string((v ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        switch (a.Type)
        {
            case "number":
                return double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? ((long)Math.Round(d)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : (a.Default is { Length: > 0 } ? a.Default : "1");
            case "choice":
                var hit = a.Choices.FirstOrDefault(c => c.Equals(v, StringComparison.OrdinalIgnoreCase));
                return hit ?? (a.Choices.Contains(a.Default) ? a.Default : a.Choices.FirstOrDefault() ?? "");
            default:
                return v.Length > 120 ? v[..120] : v;
        }
    }

    // A Minecraft name (3-16 letters, digits, _) or a selector; anything else falls back to the nearest player.
    public static string PlayerSelector(string name)
    {
        name = (name ?? "").Trim();
        if (Regex.IsMatch(name, "^[A-Za-z0-9_]{1,16}$")) return name;
        if (Regex.IsMatch(name, "^@[aprs]$")) return name;
        return "@p";
    }

    static readonly Regex Optional = new Regex(@"\{\?([a-z_][a-z0-9_]*)\}(.*?)\{/\1\}", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex Token = new Regex(@"\{([a-z_][a-z0-9_]*)(?::([a-z]+|-?\d+:-?\d+))?\}", RegexOptions.Compiled);

    public static string Render(string template, MinecraftCommand cmd, Dictionary<string, string> values, Version server)
    {
        var text = Optional.Replace(template, m => string.IsNullOrEmpty(values.GetValueOrDefault(m.Groups[1].Value)) ? "" : m.Groups[2].Value);
        bool legacyText = server != null && server < new Version(1, 21, 5);
        return Token.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            var mod = m.Groups[2].Success ? m.Groups[2].Value : "";
            if (name == "rand")
            {
                var parts = mod.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out var lo) && int.TryParse(parts[1], out var hi) && hi >= lo)
                    return Random.Shared.Next(lo, hi + 1).ToString();
                return m.Value;
            }
            if (!values.TryGetValue(name, out var v)) return m.Value; // not ours (e.g. SNBT such as {fuse:80})
            switch (mod)
            {
                case "": return v;
                case "str":
                case "text": return Quote(v);  // a JSON string and an SNBT string look the same
                case "nbttext": return legacyText ? "'" + Quote(v).Replace("\\", "\\\\").Replace("'", "\\'") + "'" : Quote(v);
                default: return m.Value;
            }
        });
    }

    static string Quote(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ---------------- commands.json ----------------

    public static List<MinecraftCommand> LoadCommands(string json)
    {
        var list = new List<MinecraftCommand>();
        var root = JsonNode.Parse(json);
        var arr = root as JsonArray ?? root?["commands"] as JsonArray ?? new JsonArray();
        foreach (var n in arr)
        {
            if (n is not JsonObject o) continue;
            var c = new MinecraftCommand
            {
                Id = (string)o["id"] ?? "",
                Name = (string)o["name"] ?? "",
                Category = (string)o["category"] ?? "",
                Description = (string)o["description"] ?? "",
                Repeat = (string)o["repeat"] ?? "",
                MaxRepeat = (int?)o["maxRepeat"] ?? 20,
                Warning = (string)o["warning"] ?? "",
                Run = Lines(o["run"]),
            };
            if (o["args"] is JsonArray args)
                foreach (var a in args.OfType<JsonObject>())
                    c.Args.Add(new GameCommandArg
                    {
                        Name = (string)a["name"] ?? "",
                        Label = (string)a["label"] ?? "",
                        Type = (string)a["type"] ?? "text",
                        Default = a["default"] is JsonValue dv ? (dv.TryGetValue<string>(out var ds) ? ds : dv.ToJsonString()) : "",
                        Choices = a["choices"] is JsonArray ch ? ch.Select(x => (string)x).Where(x => x != null).ToList() : new List<string>(),
                    });
            if (o["legacy"] is JsonArray legacy)
                foreach (var l in legacy.OfType<JsonObject>())
                    if (ParseVersion((string)l["before"]) is { } before)
                    {
                        var entry = new MinecraftLegacyRun { Before = before, Run = Lines(l["run"]) };
                        if (entry.Run.Count == 0) entry.Run = c.Run;
                        if (l["values"] is JsonObject map)
                            foreach (var kv in map) entry.Values[kv.Key] = (string)kv.Value ?? kv.Key;
                        c.Legacy.Add(entry);
                    }
            if (c.Id.Length > 0 && c.Run.Count > 0) list.Add(c);
        }
        return list;
    }

    static List<string> Lines(JsonNode n) => n switch
    {
        JsonArray a => a.Select(x => (string)x).Where(x => !string.IsNullOrWhiteSpace(x)).ToList(),
        JsonValue v when v.TryGetValue<string>(out var s) => new List<string> { s },
        _ => new List<string>(),
    };
}
