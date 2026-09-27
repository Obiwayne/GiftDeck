// GTA pack checks that need GiftDeck's own code but not the game (and never the user's GiftDeck data):
//
//   GtaPackCheck preset <out.giftdeck> <scratch>             build the starter preset from Presets.cs
//   GtaPackCheck verify <file.giftdeck> <Packs dir> <scratch> import it into a scratch profile, check every event
//   GtaPackCheck link <GameLinkHarness.exe> <Packs dir> <scratch>
//        run GiftDeck's real GameLink server on a spare port and the GiftDeck GTA client (GameLinkHarness "connect")
//        against it: hello, catalog merge with Packs\gta5\commands.json, triggers with args and templates.
//
// Build: dotnet build GiftDeck.csproj -o <gd>; dotnet build mods\gta5\tests\GtaPackCheck -p:GiftDeckDir=<gd>
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using GiftDeck.Models;
using GiftDeck.Services;

if (args.Length < 3) { Console.WriteLine("Usage: see the top of Program.cs"); return 2; }
var scratch = Path.GetFullPath(args[^1]);
Directory.CreateDirectory(scratch);
// Before anything touches Storage/Log, so the real %APPDATA%\GiftDeck is never used.
Environment.SetEnvironmentVariable("GIFTDECK_DATA", Path.Combine(scratch, "data"));

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what); if (!ok) failures++; }

var json = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
};

switch (args[0])
{
    case "preset": BuildPreset(args[1]); break;
    case "verify": VerifyPreset(args[1], args[2]); break;
    case "link": await Link(args[1], args[2]); break;
    default: Console.WriteLine("Unknown mode " + args[0]); return 2;
}
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

// ---------------------------------------------------------------- preset

void BuildPreset(string outFile)
{
    var (rules, overlays) = Presets.Starter();
    // Same layout as ProfileService.Export: profile.json, rules.json, overlays.json (no files/ needed)
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile)));
    if (File.Exists(outFile)) File.Delete(outFile);
    using (var zip = ZipFile.Open(outFile, ZipArchiveMode.Create))
    {
        Write(zip, "profile.json", JsonSerializer.Serialize(new { app = "GiftDeck", name = Presets.Name, version = 1 }));
        Write(zip, "rules.json", JsonSerializer.Serialize(rules, json));
        Write(zip, "overlays.json", JsonSerializer.Serialize(overlays, json));
    }
    Console.WriteLine($"Wrote {rules.Count} events and {overlays.Menu.Tiles.Count} gift board tiles to {Path.GetFullPath(outFile)}");
}

static void Write(ZipArchive zip, string name, string text)
{
    // Fixed timestamps so rebuilding the preset gives the same file
    var entry = zip.CreateEntry(name);
    entry.LastWriteTime = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    using var w = new StreamWriter(entry.Open());
    w.Write(text);
}

void VerifyPreset(string file, string packsDir)
{
    Console.WriteLine("Preset " + Path.GetFileName(file));
    var profiles = new ProfileService();
    var name = profiles.Import(Path.GetFullPath(file));
    Check(name == Presets.Name, "imports as a profile named \"" + name + "\"");
    var dir = ProfileService.DirOf(name);
    var rules = JsonSerializer.Deserialize<List<Rule>>(File.ReadAllText(Path.Combine(dir, "rules.json")), json);
    var overlays = JsonSerializer.Deserialize<OverlayConfig>(File.ReadAllText(Path.Combine(dir, "overlays.json")), json);
    Check(rules.Count >= 15, $"{rules.Count} events");

    GameLinkService.PacksDir = Path.GetFullPath(packsDir);
    var svc = new GameLinkService(21296); // never started: only its catalog is used
    var catalog = svc.CatalogFor("gta5");
    foreach (var r in rules)
    {
        var problems = new List<string>();
        if (r.Trigger.Type != TriggerType.Gift || string.IsNullOrEmpty(r.Trigger.GiftName)) problems.Add("not a gift event");
        if (r.Actions.Count == 0) problems.Add("no actions");
        foreach (var a in r.Actions)
        {
            if (a.Type != ActionType.GameCommand) { problems.Add("action isn't a game command"); continue; }
            Dictionary<string, JsonElement> argValues = string.IsNullOrWhiteSpace(a.Args) ? new() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(a.Args);
            if (a.Text == "gta5:giftdeck")
            {
                var cmd = catalog.FirstOrDefault(c => c.TargetId == a.Text && c.Id == a.Text2);
                if (cmd == null) { problems.Add("unknown GiftDeck GTA command " + a.Text2); continue; }
                foreach (var k in argValues.Keys)
                    if (!cmd.Command.Args.Any(x => x.Name == k)) problems.Add($"{a.Text2} has no argument '{k}'");
            }
            else if (a.Text == "gta5:chaosmod")
            {
                if (!Presets.ChaosEffects.ContainsKey(a.Text2)) problems.Add("unknown Chaos effect " + a.Text2);
            }
            else problems.Add("unknown target " + a.Text);
        }
        Check(problems.Count == 0, $"{r.Trigger.GiftName,-15} -> {r.Name}: {r.ActionsSummary}" + (problems.Count > 0 ? "  [" + string.Join("; ", problems) + "]" : ""));
    }
    var tiled = overlays.Menu.Tiles.Select(t => t.RuleId).ToHashSet();
    Check(rules.All(r => tiled.Contains(r.Id)), $"gift board has a tile for every event ({overlays.Menu.Tiles.Count} tiles)");
    var coins = rules.Select(r => Presets.GiftCoins(r.Trigger.GiftName)).ToList();
    Check(coins.Zip(coins.Skip(1)).All(p => p.First <= p.Second), "events go from cheap gifts to expensive ones");
}

// ---------------------------------------------------------------- GameLink against the real server

async Task Link(string harnessExe, string packsDir)
{
    Console.WriteLine("GiftDeck GTA client <-> GiftDeck GameLinkService");
    GameLinkService.PacksDir = Path.GetFullPath(packsDir);
    const int port = 21297; // not GiftDeck's 21216, so a running GiftDeck is never involved
    var svc = new GameLinkService(port);
    svc.Start();
    Check(svc.Running, "server started on " + svc.Url);

    // Game closed: the pack's commands.json gives the catalog
    var offline = svc.CatalogFor("gta5").Where(c => c.TargetId == "gta5:giftdeck").ToList();
    Check(offline.Count == 47, $"commands.json: {offline.Count} GiftDeck GTA commands while the game is closed");
    Check(svc.TargetName("gta5:giftdeck") == "GiftDeck GTA", "target name from commands.json: " + svc.TargetName("gta5:giftdeck"));
    var sv = offline.FirstOrDefault(c => c.Id == "spawn_vehicle");
    Check(sv != null && sv.Command.Args.Count == 2 && sv.Command.Args[0].Type == "choice" && sv.Command.Args[0].Choices.Contains("rhino"), "spawn_vehicle has its model choices");

    var psi = new ProcessStartInfo(Path.GetFullPath(harnessExe)) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add("connect");
    psi.ArgumentList.Add(port.ToString());
    psi.ArgumentList.Add("4");
    var client = Process.Start(psi);
    client.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("        " + e.Data); };
    client.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine("        [err] " + e.Data); };
    client.BeginOutputReadLine();
    client.BeginErrorReadLine();

    bool connected = false;
    for (int i = 0; i < 200 && !connected; i++) { await Task.Delay(50); connected = svc.Find("gta5:giftdeck")?.Connected == true; }
    Check(connected, "GiftDeck GTA connected and said hello");
    var target = svc.Find("gta5:giftdeck");
    Check(target != null && target.Name == "GiftDeck GTA" && target.Commands.Count == 47, $"live target: {target?.Name}, {target?.Commands.Count} commands");

    var ev = new LiveEvent { Type = "gift", Nickname = "RoseQueen", GiftName = "Money Gun", RepeatCount = 2 };
    string Template(string s) => s.Replace("{user}", ev.Nickname).Replace("{gift}", ev.GiftName).Replace("{count}", ev.RepeatCount.ToString());
    var r = await svc.RunAsync("gta5:giftdeck", "nothing", "", ev, Template);
    Check(r.Ok, "nothing: " + r.Message);
    r = await svc.RunAsync("gta5:giftdeck", "spawn_vehicle", """{"model":"rhino","enter":"yes"}""", ev, Template);
    Check(r.Ok && r.Message.Contains("model=rhino"), "spawn_vehicle with args: " + r.Message);
    r = await svc.RunAsync("gta5:giftdeck", "add_money", """{"amount":"{count}000"}""", ev, Template);
    Check(r.Ok && r.Message.Contains("amount=2000"), "template in an argument: " + r.Message);
    r = await svc.RunAsync("gta5:giftdeck", "no_such_thing", "", ev, Template);
    Check(!r.Ok, "unknown command answers ok=false: " + r.Message);

    Check(client.WaitForExit(10000), "client stopped after 4 triggers");
    bool offlineAgain = false;
    for (int i = 0; i < 100 && !offlineAgain; i++) { await Task.Delay(50); offlineAgain = svc.Find("gta5:giftdeck")?.Connected == false; }
    Check(offlineAgain, "GiftDeck sees the script disconnect");
    svc.Stop();
}
