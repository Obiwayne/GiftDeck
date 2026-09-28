using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GiftDeck.Models;

namespace GiftDeck.Services;

// What the streamer typed on a console game's page. Saved in consoles\<pack id>.json.
public class ConsoleSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Password { get; set; } = "";
    public string Player { get; set; } = "";
}

// GameLink target "<pack>:console": a game's own server console (see PackConsole for the protocols).
// Commands come from the pack's commands.json in the Minecraft pack's format. Once it has a password it keeps
// itself connected in the background, like the Minecraft server, so events can run commands straight away.
public sealed class ConsoleTarget : IGameTarget, IDisposable
{
    public GamePack Pack { get; }
    public ConsoleSettings Settings { get; }
    public string Id => Pack.Id + ":console";
    public string Game => Pack.Id;
    public string Name => Pack.Name + " server";
    public bool Connected => _connected;
    public string Status { get; private set; } = "Not set up yet";
    public IReadOnlyList<GameCommandInfo> Commands => _commands;
    public event Action Changed;

    readonly List<MinecraftCommand> _commands;
    readonly IConsoleLink _link;
    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    volatile bool _connected;
    string _lastError;

    public ConsoleTarget(GamePack pack, ConsoleSettings settings = null)
    {
        Pack = pack;
        Settings = settings ?? Storage.Load<ConsoleSettings>(SettingsName(pack.Id)) ?? new ConsoleSettings();
        if (string.IsNullOrWhiteSpace(Settings.Host)) Settings.Host = pack.Console?.Host ?? "127.0.0.1";
        if (Settings.Port <= 0) Settings.Port = pack.Console?.Port ?? 0;
        var file = string.IsNullOrEmpty(pack.Dir) || string.IsNullOrEmpty(pack.Commands) ? null : Path.Combine(pack.Dir, pack.Commands);
        _commands = file != null && File.Exists(file) ? MinecraftTarget.LoadCommands(File.ReadAllText(file)) : new List<MinecraftCommand>();
        _link = (pack.Console?.Protocol ?? "rcon").ToLowerInvariant() switch
        {
            "telnet" => new TelnetLink(),
            "tshock" => new TShockLink(),
            _ => new RconLink(),
        };
    }

    static string SettingsName(string id) => Path.Combine("consoles", id + ".json");

    bool Configured => !string.IsNullOrWhiteSpace(Settings.Host) && Settings.Port > 0 && !string.IsNullOrEmpty(Settings.Password);

    public void SaveSettings()
    {
        Storage.Save(SettingsName(Pack.Id), Settings);
        _link.Close(); // reconnect with the new details
        SetState(false, Configured ? "Connecting…" : "Not set up yet");
        _wake.Release();
    }

    // ---- Staying connected ----

    readonly SemaphoreSlim _wake = new SemaphoreSlim(0);

    public void Start() => _ = Task.Run(WatchAsync);

    async Task WatchAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (Configured && !_connected)
            {
                try
                {
                    await _link.OpenAsync(Settings, _cts.Token);
                    SetState(true, "Connected");
                }
                catch (Exception e) { Fail(e); }
            }
            else if (_connected && !_link.IsOpen) SetState(false, "The connection closed. Reconnecting…");
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(_connected ? 10 : 20), _cts.Token); } catch { return; }
        }
    }

    void Fail(Exception e)
    {
        var msg = Explain(e);
        if (msg != _lastError) { _lastError = msg; Log.Write($"{Pack.Name} console: {e.Message}"); }
        SetState(false, msg);
    }

    string Explain(Exception e) => e switch
    {
        ConsoleAuthException => $"The {Pack.Console?.PasswordLabel?.ToLowerInvariant() ?? "password"} was refused. Check it matches the server's.",
        SocketException or HttpRequestException or TimeoutException or OperationCanceledException =>
            $"Can't reach the server at {Settings.Host}:{Settings.Port}. Is it running, with its console switched on?",
        _ => e.Message,
    };

    void SetState(bool connected, string status)
    {
        bool changed = connected != _connected || status != Status;
        _connected = connected;
        Status = status;
        if (connected) _lastError = null;
        if (changed) Changed?.Invoke();
    }

    // "Test connection": connects if needed and runs the pack's harmless test command.
    public async Task<GameResult> TestAsync()
    {
        if (!Configured) return new GameResult(false, "Fill in the address, port and " + (Pack.Console?.PasswordLabel ?? "password").ToLowerInvariant() + " first.");
        try
        {
            if (!_link.IsOpen) await _link.OpenAsync(Settings, _cts.Token);
            SetState(true, "Connected");
            var test = Pack.Console?.TestCommand;
            if (string.IsNullOrWhiteSpace(test)) return new GameResult(true, "Connected.");
            var reply = Clean(await _link.SendAsync(test, _cts.Token));
            return new GameResult(!IsError(reply), reply.Length > 0 ? reply : "Connected. The server answered.");
        }
        catch (Exception e)
        {
            Fail(e);
            return new GameResult(false, Status);
        }
    }

    public async Task<string> RunRawAsync(string line)
    {
        if (!_link.IsOpen) await _link.OpenAsync(Settings, _cts.Token);
        return Clean(await _link.SendAsync(line, _cts.Token));
    }

    // ---- Running pack commands ----

    public async Task<GameResult> RunAsync(string command, JsonObject args, LiveEvent e)
    {
        var cmd = _commands.FirstOrDefault(c => c.Id == command);
        if (cmd == null) return new GameResult(false, $"Unknown {Pack.Name} command \"{command}\"");
        var values = ArgValues(cmd, args, e);
        int times = 1;
        if (!string.IsNullOrEmpty(cmd.Repeat) && int.TryParse(values.GetValueOrDefault(cmd.Repeat), out var n))
            times = Math.Clamp(n, 1, Math.Max(1, cmd.MaxRepeat));
        var replies = new List<string>();
        bool ok = true;
        try
        {
            if (!_link.IsOpen) await _link.OpenAsync(Settings, _cts.Token);
            for (int i = 0; i < times && ok; i++)
                foreach (var template in cmd.Run)
                {
                    var reply = Clean(await _link.SendAsync(Render(template, cmd, values), _cts.Token));
                    if (IsError(reply)) ok = false;
                    if (reply.Length > 0) replies.Add(reply);
                    if (!ok) break;
                }
        }
        catch (Exception ex)
        {
            Fail(ex);
            return new GameResult(false, Status);
        }
        var distinct = replies.Distinct().ToList();
        var msg = string.Join(" | ", distinct.Take(3)) + (distinct.Count > 3 ? " | …" : "");
        if (times > 1 && replies.Count > 1 && distinct.Count == 1) msg += $" (x{replies.Count})";
        return new GameResult(ok, msg);
    }

    // The console lines one run becomes (for tests and the page's preview).
    public List<string> Preview(string command, JsonObject args, LiveEvent e)
    {
        var cmd = _commands.FirstOrDefault(c => c.Id == command);
        return cmd == null ? new List<string>() : cmd.Run.Select(t => Render(t, cmd, ArgValues(cmd, args, e))).ToList();
    }

    static string Render(string template, MinecraftCommand cmd, Dictionary<string, string> values) =>
        MinecraftTarget.Render(template, cmd, values, null);

    Dictionary<string, string> ArgValues(MinecraftCommand cmd, JsonObject args, LiveEvent e)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in cmd.Args)
        {
            string v = null;
            if (args != null && args.TryGetPropertyValue(a.Name, out var node) && node != null)
                v = node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            if (v == null) v = e != null ? RulesEngine.Template(a.Default ?? "", e) : a.Default ?? "";
            values[a.Name] = CleanArg(a, v);
        }
        values["player"] = SafeText(Settings.Player ?? "", 64);
        return values;
    }

    static string CleanArg(GameCommandArg a, string v)
    {
        v = (v ?? "").Trim();
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
                return SafeText(v, 100);
        }
    }

    // Viewer names and messages end up in a console line. Line breaks would start a new command, ";" chains
    // commands on Source servers and quotes break out of arguments, so those are taken out.
    static string SafeText(string v, int max)
    {
        var s = new string(v.Where(c => !char.IsControl(c) && c != ';' && c != '"' && c != '`' && c != '\\').ToArray()).Trim();
        return s.Length > max ? s[..max] : s;
    }

    // Any line of the reply starting with one of the pack's error marks. Log lines some servers echo first
    // ("2026-09-28T21:00:00 123.456 INF Executing command …") are checked without their time stamp.
    static readonly System.Text.RegularExpressions.Regex Stamp = new(@"^\d{4}-\d\d-\d\dT[\d:]+(\s+[\d.]+)?\s+");

    bool IsError(string reply)
    {
        var marks = (Pack.Console?.ErrorMarks ?? new List<string>()).Where(m => m.Length > 0).ToList();
        return reply.Split(" / ").Select(l => Stamp.Replace(l, ""))
            .Any(l => marks.Any(m => l.StartsWith(m, StringComparison.OrdinalIgnoreCase)));
    }

    static string Clean(string reply)
    {
        reply = MinecraftTarget.StripColors(reply ?? "");
        var lines = reply.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return string.Join(" / ", lines.Take(4)) + (lines.Count > 4 ? " / …" : "");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _link.Close();
    }
}

public class ConsoleAuthException : Exception
{
    public ConsoleAuthException(string message) : base(message) { }
}

interface IConsoleLink
{
    bool IsOpen { get; }
    Task OpenAsync(ConsoleSettings s, CancellationToken ct);
    Task<string> SendAsync(string line, CancellationToken ct);
    void Close();
}

// Source RCON (Project Zomboid, Palworld, Left 4 Dead 2): the same client the Minecraft server uses.
sealed class RconLink : IConsoleLink
{
    RconClient _rcon;
    public bool IsOpen => _rcon?.IsConnected == true;

    public async Task OpenAsync(ConsoleSettings s, CancellationToken ct)
    {
        Close();
        var c = new RconClient();
        try { await c.ConnectAsync(s.Host.Trim(), s.Port, s.Password, TimeSpan.FromSeconds(6), ct); }
        catch (RconAuthException e) { c.Dispose(); throw new ConsoleAuthException(e.Message); }
        catch { c.Dispose(); throw; }
        _rcon = c;
    }

    public async Task<string> SendAsync(string line, CancellationToken ct)
    {
        try { return await _rcon.ExecuteAsync(line, TimeSpan.FromSeconds(8), ct); }
        catch { Close(); throw; }
    }

    public void Close() { try { _rcon?.Dispose(); } catch { } _rcon = null; }
}

// A plain telnet console (7 Days to Die): asks for the password, then takes one command per line. Replies have
// no end marker, so a reply is whatever arrives until the line goes quiet for a moment.
sealed class TelnetLink : IConsoleLink
{
    TcpClient _tcp;
    NetworkStream _stream;
    readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    public bool IsOpen => _tcp?.Connected == true && _stream != null;

    public async Task OpenAsync(ConsoleSettings s, CancellationToken ct)
    {
        Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(s.Host.Trim(), s.Port, cts.Token);
            _tcp = tcp;
            _stream = tcp.GetStream();
            var greeting = await ReadQuietAsync(TimeSpan.FromMilliseconds(1500), cts.Token);
            if (greeting.Contains("password", StringComparison.OrdinalIgnoreCase))
            {
                await WriteLineAsync(s.Password, cts.Token);
                var answer = await ReadQuietAsync(TimeSpan.FromMilliseconds(1500), cts.Token);
                if (answer.Contains("incorrect", StringComparison.OrdinalIgnoreCase) || answer.Contains("wrong", StringComparison.OrdinalIgnoreCase)
                    || answer.Contains("password", StringComparison.OrdinalIgnoreCase) && !answer.Contains("correct", StringComparison.OrdinalIgnoreCase))
                    throw new ConsoleAuthException("The telnet password was refused");
            }
        }
        catch
        {
            Close();
            throw;
        }
    }

    public async Task<string> SendAsync(string line, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            await ReadQuietAsync(TimeSpan.Zero, cts.Token); // drop log lines that arrived in between
            await WriteLineAsync(line, cts.Token);
            return await ReadQuietAsync(TimeSpan.FromMilliseconds(700), cts.Token);
        }
        catch { Close(); throw; }
        finally { _lock.Release(); }
    }

    async Task WriteLineAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\r\n");
        await _stream.WriteAsync(bytes, ct);
    }

    // Reads until nothing new has arrived for `quiet` (or right away when nothing is waiting and quiet is zero).
    async Task<string> ReadQuietAsync(TimeSpan quiet, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[8192];
        var deadline = DateTime.UtcNow + quiet;
        while (true)
        {
            if (_stream.DataAvailable)
            {
                int n = await _stream.ReadAsync(buf, ct);
                if (n == 0) throw new IOException("The server closed the connection");
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                deadline = DateTime.UtcNow + quiet;
                continue;
            }
            if (DateTime.UtcNow >= deadline) return sb.ToString();
            await Task.Delay(50, ct);
        }
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _stream = null;
        _tcp = null;
    }
}

// TShock's REST API (Terraria): each command is one web request with the REST token.
sealed class TShockLink : IConsoleLink
{
    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    ConsoleSettings _s;
    public bool IsOpen => _s != null;

    string Url(string path) => $"http://{_s.Host.Trim()}:{_s.Port}{path}";

    public async Task OpenAsync(ConsoleSettings s, CancellationToken ct)
    {
        _s = s;
        try
        {
            var body = await Http.GetStringAsync(Url("/tokentest?token=" + Uri.EscapeDataString(s.Password)), ct);
            using var doc = JsonDocument.Parse(body);
            if (Status(doc.RootElement) != "200") throw new ConsoleAuthException("The REST token was refused");
        }
        catch (HttpRequestException e) when (e.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            _s = null;
            throw new ConsoleAuthException("The REST token was refused");
        }
        catch
        {
            _s = null;
            throw;
        }
    }

    public async Task<string> SendAsync(string line, CancellationToken ct)
    {
        if (!line.StartsWith("/")) line = "/" + line;
        string body;
        using (var r = await Http.GetAsync(Url("/v3/server/rawcmd?token=" + Uri.EscapeDataString(_s.Password) + "&cmd=" + Uri.EscapeDataString(line)), ct))
            body = await r.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (Status(root) is "401" or "403") throw new ConsoleAuthException("The REST token was refused");
        if (root.TryGetProperty("response", out var resp))
            return resp.ValueKind == JsonValueKind.Array ? string.Join("\n", resp.EnumerateArray().Select(x => x.ToString())) : resp.ToString();
        return root.TryGetProperty("error", out var err) ? err.ToString() : "";
    }

    static string Status(JsonElement root) => root.TryGetProperty("status", out var st) ? st.ToString() : "";

    public void Close() => _s = null;
}
