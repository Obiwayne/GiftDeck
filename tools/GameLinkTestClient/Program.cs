// GameLink test client: behaves like a game mod connecting to GiftDeck, so events can be tried
// without a game. It is also the reference for mod authors: connect, say hello with your commands,
// answer every trigger with a result, answer pings. Protocol: docs/v2.1/plan.md, section 1.
//
//   dotnet run --project tools\GameLinkTestClient -- [options]
//     --url ws://127.0.0.1:21216/   where GiftDeck listens (default)
//     --game gta5 --mod testmod     the target id becomes "gta5:testmod"
//     --name "Test mod"             shown in GiftDeck
//     --many 600                    also send 600 made-up commands (to try the editor's search)
//     --quit-after 3                exit after answering 3 triggers
//     --once                        don't reconnect when the connection drops
//
// Special commands: "never_answer" is never answered (GiftDeck gives up after 5 s), "fail_me" answers ok=false.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

string Opt(string name, string def)
{
    int i = Array.IndexOf(args, "--" + name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
}

var url = Opt("url", "ws://127.0.0.1:21216/");
var game = Opt("game", "gta5");
var mod = Opt("mod", "testmod");
var name = Opt("name", "GameLink test client");
int many = int.Parse(Opt("many", "0"));
int quitAfter = int.Parse(Opt("quit-after", "0"));
bool once = args.Contains("--once");
int answered = 0;

JsonArray Commands()
{
    var list = new JsonArray
    {
        new JsonObject { ["id"] = "player_kickflip", ["name"] = "Kickflip", ["category"] = "Player", ["description"] = "The player does a kickflip", ["args"] = new JsonArray() },
        new JsonObject
        {
            ["id"] = "spawn_vehicle", ["name"] = "Spawn Vehicle", ["category"] = "Vehicles", ["description"] = "Spawns a vehicle next to the player",
            ["args"] = new JsonArray
            {
                new JsonObject { ["name"] = "model", ["label"] = "Vehicle model", ["type"] = "choice", ["default"] = "adder", ["choices"] = new JsonArray("adder", "rhino", "faggio") },
                new JsonObject { ["name"] = "plate", ["label"] = "Number plate", ["type"] = "text", ["default"] = "{user}" },
            },
        },
        new JsonObject
        {
            ["id"] = "add_money", ["name"] = "Add money", ["category"] = "Misc",
            ["args"] = new JsonArray { new JsonObject { ["name"] = "amount", ["label"] = "Amount ($)", ["type"] = "number", ["default"] = 1000 } },
        },
        new JsonObject { ["id"] = "never_answer", ["name"] = "Never answers (test)", ["category"] = "Test" },
        new JsonObject { ["id"] = "fail_me", ["name"] = "Always fails (test)", ["category"] = "Test" },
    };
    string[] cats = { "Player", "Peds", "Vehicle", "Misc", "Time", "Weather", "Screen", "Meta" };
    for (int i = 1; i <= many; i++)
        list.Add(new JsonObject { ["id"] = $"fake_{i:000}", ["name"] = $"Fake effect {i:000}", ["category"] = cats[i % cats.Length] });
    return list;
}

async Task Send(ClientWebSocket ws, JsonNode msg, SemaphoreSlim gate)
{
    var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
    await gate.WaitAsync();
    try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
    finally { gate.Release(); }
}

while (true)
{
    using var ws = new ClientWebSocket();
    var gate = new SemaphoreSlim(1, 1);
    try
    {
        await ws.ConnectAsync(new Uri(url), CancellationToken.None);
    }
    catch (Exception e)
    {
        Console.WriteLine($"Could not connect to {url}: {e.Message}");
        if (once) return 1;
        await Task.Delay(2000);
        continue;
    }
    Console.WriteLine($"Connected to {url} as {game}:{mod}");
    var cmds = Commands();
    await Send(ws, new JsonObject
    {
        ["type"] = "hello", ["game"] = game, ["mod"] = mod, ["name"] = name, ["version"] = "1.0.0", ["commands"] = cmds,
    }, gate);
    Console.WriteLine($"Sent hello with {cmds.Count} commands");
    await Send(ws, new JsonObject { ["type"] = "status", ["text"] = "Test client ready" }, gate);

    var buffer = new byte[64 * 1024];
    var message = new MemoryStream();
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.ToArray());
            message.SetLength(0);
            var o = JsonNode.Parse(text) as JsonObject;
            var type = (string)o?["type"];
            if (type == "ping") { Console.WriteLine("ping -> pong"); await Send(ws, new JsonObject { ["type"] = "pong" }, gate); continue; }
            if (type != "trigger") { Console.WriteLine("got " + text); continue; }

            var id = (string)o["id"];
            var command = (string)o["command"];
            Console.WriteLine($"TRIGGER {command} args={o["args"]?.ToJsonString()} user={o["user"]} gift={o["gift"]} count={o["count"]}");
            if (command == "never_answer") { Console.WriteLine("  (not answering, on purpose)"); continue; }
            bool ok = command != "fail_me";
            var reply = new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = ok, ["message"] = ok ? "did " + command : "failed on purpose" };
            await Send(ws, reply, gate);
            Console.WriteLine("  answered " + reply.ToJsonString());
            if (quitAfter > 0 && ++answered >= quitAfter)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                Console.WriteLine("Done, closed the connection");
                return 0;
            }
        }
    }
    catch (Exception e) { Console.WriteLine("Connection lost: " + e.Message); }
    Console.WriteLine("Disconnected");
    if (once) return 0;
    await Task.Delay(2000);
}
