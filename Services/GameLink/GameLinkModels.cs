using GiftDeck.Models;

namespace GiftDeck.Services;

// One command a game can run, as a mod (or a pack's commands.json) describes it. See docs/v2.1/plan.md.
public class GameCommandInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public List<GameCommandArg> Args { get; set; } = new List<GameCommandArg>();
}

public class GameCommandArg
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text"; // text | number | choice
    public string Default { get; set; } = "";
    public List<string> Choices { get; set; } = new List<string>();
}

public record GameResult(bool Ok, string Message);

// Something GiftDeck can send game commands to: a mod connected over GameLink, or a built-in target
// such as a Minecraft server over RCON.
public interface IGameTarget
{
    string Id { get; }          // "<game>:<mod>", e.g. "gta5:chaosmod"
    string Game { get; }        // "gta5"
    string Name { get; }        // shown in the UI, e.g. "Chaos Mod V"
    bool Connected { get; }
    string Status { get; }
    IReadOnlyList<GameCommandInfo> Commands { get; }
    Task<GameResult> RunAsync(string command, System.Text.Json.Nodes.JsonObject args, LiveEvent e);
}
