using System.Text.Json.Nodes;
using GiftDeck.Models;

namespace GiftDeck.Services;

// GameLink: the local connection game mods use to receive commands from GiftDeck (ws://127.0.0.1:21216/),
// plus built-in targets. See docs/v2.1/plan.md. Skeleton: the WebSocket server comes in the GameLink package.
public class GameLinkService
{
    public const int Port = 21216;

    readonly List<IGameTarget> _targets = new List<IGameTarget>();
    public event Action Changed;

    public IReadOnlyList<IGameTarget> Targets { get { lock (_targets) return _targets.ToList(); } }

    public void Start() { }
    public void Stop() { }

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
        if (target == null || !target.Connected) return new GameResult(false, (target?.Name ?? targetId) + " isn't connected");
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
        return await target.RunAsync(command, args, e);
    }
}
