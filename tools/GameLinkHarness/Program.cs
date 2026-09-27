// Proves the GameLink round trip without starting GiftDeck: runs GameLinkService on a spare port with a
// scratch data folder and scratch Packs folder, starts tools\GameLinkTestClient against it and checks
// hello, triggers, results, timeouts, catalogs, reconnects and disconnects.
//
//   dotnet run --project tools\GameLinkHarness -p:GiftDeckDir=<GiftDeck build folder> -- <GameLinkTestClient.dll> [scratch folder] [--no-ui]
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using GiftDeck.Models;
using GiftDeck.Services;

if (args.Length < 1) { Console.WriteLine("Usage: GameLinkHarness <GameLinkTestClient.dll> [scratch folder]"); return 2; }
var clientDll = Path.GetFullPath(args[0]);
var scratch = Path.GetFullPath(args.Length > 1 && !args[1].StartsWith("--") ? args[1] : Path.Combine(Path.GetTempPath(), "gamelink-harness"));
Directory.CreateDirectory(scratch);
// Must be set before anything touches Storage/Log, so the real %APPDATA%\GiftDeck is never used.
Environment.SetEnvironmentVariable("GIFTDECK_DATA", Path.Combine(scratch, "data"));
const int port = 21298; // not GiftDeck's 21216, so a running GiftDeck is never involved
int failures = 0;

void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }

var packs = Path.Combine(scratch, "Packs");
Directory.CreateDirectory(Path.Combine(packs, "gta5"));
File.WriteAllText(Path.Combine(packs, "gta5", "pack.json"), """
{ "id": "gta5", "name": "GTA V (Legacy)", "targets": ["gta5:chaosmod", "gta5:giftdeck"] }
""");
File.WriteAllText(Path.Combine(packs, "gta5", "commands.json"), """
[
  { "target": "gta5:chaosmod", "name": "Chaos Mod V", "commands": [
      { "id": "player_suicide", "name": "Suicide", "category": "Player" },
      { "id": "time_night", "name": "Night time", "category": "Time" } ] },
  { "id": "spawn_vehicle", "name": "Spawn Vehicle (from file)", "category": "Vehicles", "target": "gta5:testmod" },
  { "id": "jail_player", "name": "Jail Player", "category": "Player",
    "args": [ { "name": "seconds", "type": "number", "default": 30 } ] }
]
""");
GameLinkService.PacksDir = packs;

Log.Written += line => Console.WriteLine("  [GiftDeck log] " + line);
var svc = new GameLinkService(port);
svc.Start();
Check(svc.Running, "server started on " + svc.Url);

// Static catalog, game closed
var games = svc.Games();
Check(games.Any(g => g.Id == "gta5" && g.Name == "GTA V (Legacy)"), "games list from the pack: " + string.Join(", ", games.Select(g => g.Id + "=" + g.Name)));
var cat = svc.CatalogFor("gta5");
Check(cat.Count == 4, $"static catalog has 4 commands: {string.Join(", ", cat.Select(c => c.TargetId + "/" + c.Id))}");
Check(cat.Any(c => c.TargetId == "gta5:chaosmod" && c.Id == "jail_player"), "a command without a target goes to the pack's first target (gta5:chaosmod)");
Check(svc.TargetName("gta5:chaosmod") == "Chaos Mod V", "target name from commands.json");
var saved = new RuleAction { Type = ActionType.GameCommand, Text = "gta5:chaosmod", Text2 = "time_night" };
Check(saved.Summary() == "Game: Night time", "summary uses the friendly name while the game is closed: " + saved.Summary());

// Plain HTTP visit
using (var http = new HttpClient())
{
    var resp = await http.GetAsync($"http://127.0.0.1:{port}/");
    Check((int)resp.StatusCode == 426, "plain HTTP visit answers 426 with a hint: " + await resp.Content.ReadAsStringAsync());
}

Process StartClient(string tag, params string[] extra)
{
    var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    psi.ArgumentList.Add(clientDll);
    foreach (var a in new[] { "--url", $"ws://127.0.0.1:{port}/", "--once" }.Concat(extra)) psi.ArgumentList.Add(a);
    var p = Process.Start(psi);
    p.OutputDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  [{tag}] {e.Data}"); };
    p.ErrorDataReceived += (s, e) => { if (e.Data != null) Console.WriteLine($"  [{tag} err] {e.Data}"); };
    p.BeginOutputReadLine();
    p.BeginErrorReadLine();
    return p;
}

async Task<bool> WaitFor(Func<bool> cond, int ms)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; await Task.Delay(50); }
    return cond();
}

int changes = 0;
svc.Changed += () => Interlocked.Increment(ref changes);

var client1 = StartClient("client1", "--many", "600");
Check(await WaitFor(() => svc.Find("gta5:testmod")?.Connected == true, 15000), "test client connected and said hello");
var t1 = svc.Find("gta5:testmod");
Check(t1.Name == "GameLink test client" && t1.Commands.Count == 605, $"target has its name and 605 commands ({t1.Commands.Count})");
Check(await WaitFor(() => t1.Status == "Test client ready", 2000), "status message stored: " + t1.Status);

var ev = new LiveEvent { Type = "gift", Nickname = "Viewer1", GiftName = "Rose", RepeatCount = 5 };
string Template(string s) => s.Replace("{user}", ev.Nickname).Replace("{gift}", ev.GiftName).Replace("{count}", ev.RepeatCount.ToString());

var r = await svc.RunAsync("gta5:testmod", "player_kickflip", "", ev, Template);
Check(r.Ok && r.Message == "did player_kickflip", $"kickflip round trip: ok={r.Ok} message={r.Message}");
r = await svc.RunAsync("gta5:testmod", "spawn_vehicle", """{"model":"rhino","plate":"{user}"}""", ev, Template);
Check(r.Ok, $"spawn_vehicle with args: ok={r.Ok} (client output shows the args)");
r = await svc.RunAsync("gta5:testmod", "add_money", """{"amount":"{count}"}""", ev, Template);
Check(r.Ok, "add_money with a {count} template in a number arg (client should see amount=5 as a number)");
r = await svc.RunAsync("gta5:testmod", "fail_me", "", ev, Template);
Check(!r.Ok && r.Message == "failed on purpose", $"a failing command reports its message: {r.Message}");
var sw = Stopwatch.StartNew();
r = await svc.RunAsync("gta5:testmod", "never_answer", "", ev, Template);
Check(!r.Ok && r.Message == "no answer" && sw.ElapsedMilliseconds >= 4900 && sw.ElapsedMilliseconds < 6500, $"no answer after {sw.ElapsedMilliseconds} ms: {r.Message}");
r = await svc.RunAsync("gta5:chaosmod", "time_night", "", ev, Template);
Check(!r.Ok && r.Message == "Chaos Mod V isn't connected", "a target that isn't connected: " + r.Message);

// Several at once
var many = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => svc.RunAsync("gta5:testmod", $"fake_{i:000}", "", ev, Template)));
Check(many.All(x => x.Ok) && many.Select((x, i) => x.Message == $"did fake_{i + 1:000}").All(b => b), "20 triggers at once each get their own answer");

cat = svc.CatalogFor("gta5");
Check(cat.Count == 3 + 605, $"merged catalog: 3 from the file + 605 live = {cat.Count}");
Check(cat.Single(c => c.TargetId == "gta5:testmod" && c.Id == "spawn_vehicle").Name == "Spawn Vehicle", "the live command wins over the file's");
Check(new RuleAction { Type = ActionType.GameCommand, Text = "gta5:testmod", Text2 = "spawn_vehicle" }.Summary() == "Game: Spawn Vehicle", "summary uses the live name");

// The event editor, on the real WPF control (off screen, no mouse)
if (!args.Contains("--no-ui")) { var uiCrashed = UiCheck.Run(svc, Path.Combine(scratch, "shots"), Check); failures += uiCrashed; }

Console.WriteLine("Waiting 17 s for a keep-alive ping...");
await Task.Delay(17000);
Check(t1.Connected, "still connected after the keep-alive");

// Reconnect: a second connection with the same id replaces the first
var client2 = StartClient("client2", "--quit-after", "1");
Check(await WaitFor(() => svc.Find("gta5:testmod") != t1 && svc.Find("gta5:testmod")?.Connected == true, 15000), "second connection with the same id replaced the first");
Check(!t1.Connected, "old connection marked offline");
Check(await WaitFor(() => client1.HasExited, 5000), "old client was disconnected by GiftDeck");
Check(svc.Targets.Count(t => t.Id == "gta5:testmod") == 1, "only one gta5:testmod in the list");

r = await svc.RunAsync("gta5:testmod", "player_kickflip", "", ev, Template);
Check(r.Ok, "the new connection answers");
Check(await WaitFor(() => svc.Find("gta5:testmod")?.Connected == false, 5000), "after the client quits the target is offline");
Check(svc.Find("gta5:testmod") != null && svc.CatalogFor("gta5").Any(c => c.TargetId == "gta5:testmod" && c.Id == "player_kickflip"), "offline target is still listed with its commands");
r = await svc.RunAsync("gta5:testmod", "player_kickflip", "", ev, Template);
Check(!r.Ok && r.Message.EndsWith("isn't connected"), "running on the offline target: " + r.Message);
Check(changes >= 4, $"Changed fired {changes} times");

svc.Stop();
Check(!svc.Running, "server stopped");
try { client1.Kill(); } catch { }
try { client2.Kill(); } catch { }
Console.WriteLine(failures == 0 ? "ALL PASSED" : failures + " FAILED");
return failures == 0 ? 0 : 1;
