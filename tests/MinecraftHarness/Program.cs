using System.IO;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using GiftDeck.Models;
using GiftDeck.Services;

// Checks for the Minecraft pack. "unit" needs nothing; "live" downloads Paper (and Java if needed) into a
// scratch folder, accepts the EULA for that test server, starts it, runs commands over RCON and stops it.
static class Program
{
    static int _fail;

    static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "unit";
        if (mode == "unit") return await Unit();
        if (mode == "live" && args.Length > 1) return await Live(args[1], args.Length > 2 ? args[2] : "latest");
        if (mode == "make-preset" && args.Length > 1) { Preset.Write(args[1]); return 0; }
        if (mode == "make-game-presets" && args.Length > 1) { Preset.WriteGames(args[1]); return 0; }
        if (mode == "games" && args.Length > 1) return await GamesLive.Run(args[1], args.Length > 2 ? args[2] : "latest");
        Console.WriteLine("usage: unit | live <scratch folder> [paper version] | games <scratch folder> [paper version] | make-preset <file> | make-game-presets <folder>");
        return 2;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    // ---------------- unit ----------------

    static async Task<int> Unit()
    {
        // Logs go to a scratch data folder, never the user's GiftDeck folder.
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", Path.Combine(Path.GetTempPath(), "giftdeck-mc-unit"));

        Console.WriteLine("RCON packet encoding");
        var bytes = new RconPacket(7, RconPacket.TypeCommand, "list").Encode();
        var expect = new byte[] { 14, 0, 0, 0, 7, 0, 0, 0, 2, 0, 0, 0, (byte)'l', (byte)'i', (byte)'s', (byte)'t', 0, 0 };
        Check(bytes.SequenceEqual(expect), "list packet = " + Convert.ToHexString(bytes));
        var auth = new RconPacket(1, RconPacket.TypeAuth, "pw").Encode();
        Check(BinaryPrimitives.ReadInt32LittleEndian(auth) == 12 && auth[8] == 3 && auth.Length == 16, "auth packet size 12, type 3");
        var empty = new RconPacket(-1, RconPacket.TypeResponse, "").Encode();
        Check(empty.Length == 14 && BinaryPrimitives.ReadInt32LittleEndian(empty) == 10 && BinaryPrimitives.ReadInt32LittleEndian(empty.AsSpan(4)) == -1, "empty packet: size 10, id -1");
        var utf = new RconPacket(3, 2, "say héllo ✓").Encode();
        Check(BinaryPrimitives.ReadInt32LittleEndian(utf) == 10 + Encoding.UTF8.GetByteCount("say héllo ✓"), "UTF-8 body length");

        Console.WriteLine("RCON packet decoding");
        var two = bytes.Concat(auth).ToArray();
        int used = RconPacket.TryDecode(two, out var p1);
        Check(used == 18 && p1.Id == 7 && p1.Type == 2 && p1.Body == "list", "first of two packets");
        used = RconPacket.TryDecode(two.AsSpan(used), out var p2);
        Check(used == 16 && p2.Body == "pw" && p2.Type == 3, "second of two packets");
        Check(RconPacket.TryDecode(bytes.AsSpan(0, 10), out _) == 0, "partial packet waits for more");
        Check(RconPacket.TryDecode(bytes.AsSpan(0, 3), out _) == 0, "partial size waits for more");
        RconPacket.TryDecode(utf, out var p3);
        Check(p3.Body == "say héllo ✓", "UTF-8 round trip");
        bool threw = false;
        try { RconPacket.TryDecode(new byte[] { 2, 0, 0, 0, 0, 0 }, out _); } catch (InvalidDataException) { threw = true; }
        Check(threw, "bad size rejected");

        Console.WriteLine("RCON client against a fake server (auth, split replies, wrong password)");
        await FakeServerTests();

        Console.WriteLine("Command templates");
        var cmds = MinecraftTarget.LoadCommands(File.ReadAllText(PackFile("commands.json")));
        Check(cmds.Count >= 20, $"commands.json has {cmds.Count} commands");
        foreach (var c in cmds)
            foreach (var a in c.Args.Where(a => a.Type == "choice"))
                Check(a.Choices.Contains(a.Default), $"{c.Id}.{a.Name} default is one of its choices");
        var mob = cmds.First(c => c.Id == "summon_mob");
        var values = new Dictionary<string, string> { ["mob"] = "zombie", ["count"] = "3", ["name"] = "Bob \"x\" 'y'", ["player"] = "Steve" };
        var modern = MinecraftTarget.Render(mob.Run[0], mob, values, new Version(26, 2));
        Console.WriteLine("        " + modern);
        Check(modern.Contains("{CustomName:\"Bob \\\"x\\\" 'y'\",CustomNameVisible:1b}") && modern.StartsWith("execute at Steve run summon zombie ~"), "modern name tag (SNBT string)");
        var old = MinecraftTarget.Render(mob.Run[0], mob, values, new Version(1, 20, 1));
        Console.WriteLine("        " + old);
        Check(old.Contains("CustomName:'\"Bob \\\\\"x\\\\\" \\'y\\'\"'"), "1.20.1 name tag (JSON inside SNBT)");
        values["name"] = "";
        Check(!MinecraftTarget.Render(mob.Run[0], mob, values, null).Contains("CustomName"), "empty name tag is left out");
        var tnt = cmds.First(c => c.Id == "tnt");
        Check(tnt.RunFor(new Version(1, 20, 1))[0].Contains("{Fuse:"), "TNT uses Fuse before 1.20.3");
        Check(tnt.RunFor(new Version(1, 21, 8))[0].Contains("{fuse:"), "TNT uses fuse on 1.21");
        var tntLine = MinecraftTarget.Render(tnt.Run[0], tnt, new Dictionary<string, string> { ["count"] = "5", ["fuse"] = "60", ["player"] = "@p" }, null);
        Console.WriteLine("        " + tntLine);
        Check(tntLine.EndsWith("{fuse:60}") && !tntLine.Contains("{rand"), "TNT fuse and random offsets filled in");
        Check(MinecraftTarget.PlayerSelector("Steve_01") == "Steve_01" && MinecraftTarget.PlayerSelector("x y; op me") == "@p" && MinecraftTarget.PlayerSelector("") == "@p", "player names are checked");

        // A pretend 1.20.1 install, to see the legacy syntax through MinecraftTarget itself.
        var fake = Path.Combine(Path.GetTempPath(), "giftdeck-mc-unit", "fake-1.20.1");
        Directory.CreateDirectory(fake);
        File.WriteAllText(Path.Combine(fake, "giftdeck-server.json"), "{\"Version\":\"1.20.1\",\"Build\":196,\"JavaMin\":17,\"Jar\":\"paper.jar\"}");
        File.WriteAllText(Path.Combine(fake, "paper.jar"), "");
        using (var oldTarget = new MinecraftTarget(new MinecraftServer(new MinecraftSettings { PlayerName = "Steve" }, fake, fake), PackFile("commands.json")))
        {
            var ev = new LiveEvent { Nickname = "Bob" };
            var rule = oldTarget.Preview("gamerule", new JsonObject { ["rule"] = "advance_time", ["value"] = "false" }, ev);
            Check(rule.SequenceEqual(new[] { "gamerule doDaylightCycle false" }), "1.20.1 game rule name: " + rule[0]);
            var named = oldTarget.Preview("summon_mob", new JsonObject(), ev)[0];
            Check(named.StartsWith("execute at Steve run summon zombie") && named.Contains("CustomName:'\"Bob\"'"), "defaults fill {user}: " + named);
            var bad = oldTarget.Preview("effect", new JsonObject { ["effect"] = "op_me", ["seconds"] = "ten" }, ev)[0];
            Check(bad == "effect give Steve minecraft:speed 15 1 true", "unknown choice and bad number fall back to defaults: " + bad);
        }
        Check(MinecraftTarget.IsError("No player was found") && MinecraftTarget.IsError("Incorrect argument for command")
              && !MinecraftTarget.IsError("Summoned new Invalid Bob"), "error replies recognised, names don't count");

        Console.WriteLine("Preset");
        CheckPreset();

        GamesLive.Unit(Check);

        Console.WriteLine(_fail == 0 ? "ALL PASSED" : $"{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }

    static string PackFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "Packs", "minecraft", name);
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(name);
    }

    static void CheckPreset()
    {
        var cmds = MinecraftTarget.LoadCommands(File.ReadAllText(PackFile("commands.json")));
        var dir = Path.GetDirectoryName(PackFile("pack.json"));
        foreach (var preset in Directory.GetFiles(Path.Combine(dir, "presets"), "*.giftdeck"))
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(preset);
            Check(zip.GetEntry("profile.json") != null && zip.GetEntry("rules.json") != null, Path.GetFileName(preset) + " has profile.json and rules.json");
            using var r = new StreamReader(zip.GetEntry("rules.json").Open());
            var rules = JsonNode.Parse(r.ReadToEnd()) as JsonArray;
            Check(rules?.Count >= 10, $"{rules?.Count} rules");
            foreach (var rule in rules.OfType<JsonObject>())
                foreach (var a in (rule["Actions"] as JsonArray).OfType<JsonObject>())
                {
                    var cmd = cmds.FirstOrDefault(c => c.Id == (string)a["Text2"]);
                    bool argsOk = cmd != null && (string)a["Text"] == MinecraftTarget.TargetId;
                    if (argsOk && JsonNode.Parse((string)a["Args"] ?? "{}") is JsonObject o)
                        argsOk = o.All(kv => cmd.Args.Any(x => x.Name == kv.Key));
                    Check(argsOk, $"  {rule["Name"]}: {a["Text2"]} {a["Args"]}");
                }
        }
    }

    // A tiny RCON server that splits long replies the way Minecraft does and answers unknown packet types.
    static async Task FakeServerTests()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        int pipelined = 0;
        var serverTask = Task.Run(async () =>
        {
            for (int conn = 0; conn < 2; conn++)
            {
                using var c = await listener.AcceptTcpClientAsync();
                var s = c.GetStream();
                var buf = new byte[65536];
                int len = 0;
                bool authed = false;
                while (true)
                {
                    int n;
                    try { n = await s.ReadAsync(buf.AsMemory(len)); } catch { break; }
                    if (n == 0) break;
                    len += n;
                    int inThisRead = 0;
                    while (true)
                    {
                        int used = RconPacket.TryDecode(buf.AsSpan(0, len), out var p);
                        if (used == 0) break;
                        // Like Minecraft: one read must hold exactly one packet, or the connection is dropped.
                        if (++inThisRead > 1 || len != used) { pipelined++; c.Close(); break; }
                        Buffer.BlockCopy(buf, used, buf, 0, len - used);
                        len -= used;
                        byte[] Reply(int id, int type, string body) => new RconPacket(id, type, body).Encode();
                        if (p.Type == RconPacket.TypeAuth)
                        {
                            authed = p.Body == "secret";
                            await s.WriteAsync(Reply(authed ? p.Id : -1, RconPacket.TypeCommand, ""));
                        }
                        else if (p.Type == RconPacket.TypeCommand && authed)
                        {
                            var body = p.Body == "big" ? new string('x', 4096) + new string('y', 4096) + "end" : "reply to " + p.Body;
                            // Minecraft sends at most 4096 bytes of body per packet.
                            for (int i = 0; i < body.Length; i += 4096)
                                await s.WriteAsync(Reply(p.Id, RconPacket.TypeResponse, body.Substring(i, Math.Min(4096, body.Length - i))));
                        }
                        else await s.WriteAsync(Reply(p.Id, RconPacket.TypeResponse, "Unknown request " + p.Type.ToString("x")));
                    }
                }
            }
        });

        using (var bad = new RconClient())
        {
            bool refused = false;
            try { await bad.ConnectAsync("127.0.0.1", port, "wrong", TimeSpan.FromSeconds(3)); }
            catch (RconAuthException) { refused = true; }
            Check(refused, "wrong password is refused");
        }
        using var rc = new RconClient();
        await rc.ConnectAsync("127.0.0.1", port, "secret", TimeSpan.FromSeconds(3));
        Check(rc.IsConnected, "logged in");
        Check(await rc.ExecuteAsync("list", TimeSpan.FromSeconds(3)) == "reply to list", "one-packet reply");
        var big = await rc.ExecuteAsync("big", TimeSpan.FromSeconds(3));
        Check(big.Length == 8195 && big.EndsWith("end"), $"reply split over 3 packets joined ({big.Length} chars)");
        Check(await rc.ExecuteAsync("again", TimeSpan.FromSeconds(3)) == "reply to again", "next command after a split reply");
        for (int i = 0; i < 20; i++) await rc.ExecuteAsync("fast " + i, TimeSpan.FromSeconds(3));
        Check(pipelined == 0 && rc.IsConnected, "never sends two packets at once (20 quick commands)");
        rc.Dispose();
        listener.Stop();
        try { await serverTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }

    // ---------------- live ----------------

    static async Task<int> Live(string scratch, string version)
    {
        scratch = Path.GetFullPath(scratch);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", Path.Combine(scratch, "giftdeck-data"));
        var settings = new MinecraftSettings { ServerPort = 25665, RconPort = 25675, MemoryMb = 2048, PlayerName = "" };
        var progress = new Progress<InstallProgress>(p => { if (p.Fraction < 0 || p.Fraction >= 1) Console.WriteLine("  " + p.Text); });
        Console.WriteLine($"Resolving Paper '{version}' from the Fill API…");
        var build = await MinecraftServer.ResolveAsync(version);
        var server = new MinecraftServer(settings, Path.Combine(scratch, "server-" + build.Version), Path.Combine(scratch, "java"));
        var target = new MinecraftTarget(server, PackFile("commands.json"));
        server.ConsoleLine += l => Console.WriteLine("  | " + l);
        Console.WriteLine($"  Paper {build.Version} build {build.Build} ({build.Channel}), needs Java {build.JavaMin}+, {build.Size / 1048576} MB, sha256 {build.Sha256}");

        Console.WriteLine("Java on this PC:");
        foreach (var j in server.FindJavas()) Console.WriteLine($"  Java {j.Version} (major {j.Major}) from {j.Source}: {j.Exe}");
        var java = server.FindJava(build.JavaMin);
        if (java == null)
        {
            Console.WriteLine($"Downloading Eclipse Temurin (Java {build.JavaMin}+) into the scratch folder…");
            java = await server.DownloadJavaAsync(build.JavaMin, progress);
        }
        Console.WriteLine($"  Using Java {java.Version}: {java.Exe}");

        if (!server.IsInstalled || server.InstalledVersion != build.Version || server.Info.Build != build.Build)
            await server.InstallAsync(build, progress);
        Console.WriteLine($"Installed: {server.InstalledVersion} in {server.Folder}");
        server.RevokeEula();
        Check(!server.EulaAccepted, "EULA not accepted before asking");
        server.AcceptEula(); // this is our own throwaway test server
        Check(server.EulaAccepted, "EULA accepted for the test server");

        var t0 = DateTime.Now;
        await server.StartAsync(java);
        while (server.State == MinecraftServerState.Starting && (DateTime.Now - t0).TotalMinutes < 6) await Task.Delay(500);
        Console.WriteLine($"State after {(DateTime.Now - t0).TotalSeconds:0} s: {server.State}, {server.StateText}");
        Check(server.State == MinecraftServerState.Running && target.Connected, "server running and RCON connected");

        Console.WriteLine("server.properties (GiftDeck's keys):");
        foreach (var l in File.ReadAllLines(Path.Combine(server.Folder, "server.properties")))
            if (l.StartsWith("rcon") || l.StartsWith("enable-rcon") || l.StartsWith("server-") || l.StartsWith("online-mode") || l.StartsWith("broadcast-rcon"))
                Console.WriteLine("  " + (l.StartsWith("rcon.password") ? "rcon.password=<hidden>" : l));
        Console.WriteLine("Listening sockets of the server process:");
        Console.WriteLine(Netstat(settings.ServerPort, settings.RconPort));

        Console.WriteLine("RCON commands:");
        foreach (var c in new[] { "list", "time set day", "say hello from GiftDeck", "summon zombie ~ ~ ~", "execute at @p run summon zombie ~ ~ ~",
                     "forceload add 0 0", "summon zombie 0 80 0 {CustomName:\"GiftDeck test\",CustomNameVisible:1b}", "kill @e[type=zombie]",
                     "gamerule keep_inventory", "gamerule keepInventory", "gamerule advance_weather", "gamerule doWeatherCycle", "weather thunder", "difficulty hard", "seed", "version" })
            await Rcon(server, c);

        Console.WriteLine("Pack commands through MinecraftTarget (no player online):");
        var e = new LiveEvent { Type = "gift", Nickname = "Tester \"q\"", GiftName = "Rose", Diamonds = 1 };
        foreach (var cmd in target.Commands)
        {
            var r = await target.RunAsync(cmd.Id, new JsonObject(), e);
            Console.WriteLine($"  {cmd.Id,-18} ok={r.Ok,-5} {r.Message}");
        }

        // No player can join this test, so aim the commands at an armor stand at 0 80 0 instead: commands that
        // only take players (give, effect on players...) then answer "No player was found", which still proves
        // Minecraft parsed them; mob, TNT and fill commands really run.
        Console.WriteLine("The same commands aimed at a zombie at 0 80 0 instead of a player (checks the syntax):");
        await Rcon(server, "forceload add 0 0", quiet: true);
        await Rcon(server, "summon zombie 0 80 0 {NoAI:1b,Invulnerable:1b,PersistenceRequired:1b,Tags:[\"gdtarget\"]}");
        foreach (var cmd in target.Commands)
            foreach (var line in target.Preview(cmd.Id, new JsonObject(), e, "@e[tag=gdtarget,limit=1]"))
                await Rcon(server, line);
        await Rcon(server, "kill @e[type=!player]", quiet: false);

        Console.WriteLine("Stopping…");
        var s0 = DateTime.Now;
        await server.StopAsync();
        Console.WriteLine($"State after {(DateTime.Now - s0).TotalSeconds:0} s: {server.State}");
        Check(server.State == MinecraftServerState.Stopped && !server.OwnsProcess, "stopped cleanly");
        Check(server.ConsoleTail().Any(l => l.Contains("exit code 0")), "exit code 0");
        target.Dispose();
        Console.WriteLine(_fail == 0 ? "LIVE PASSED" : $"{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }

    static async Task Rcon(MinecraftServer server, string cmd, bool quiet = false)
    {
        try
        {
            var reply = await server.ExecuteAsync(cmd);
            if (!quiet) Console.WriteLine($"  > {cmd}\n    < {(reply.Length == 0 ? "(empty reply)" : reply)}");
        }
        catch (Exception ex) { Console.WriteLine($"  > {cmd}\n    ! {ex.Message}"); }
    }

    static string Netstat(params int[] ports)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano -p TCP") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi);
        var lines = p.StandardOutput.ReadToEnd().Split('\n').Where(l => l.Contains("LISTENING") && ports.Any(port => l.Contains(":" + port + " ")));
        return string.Join("\n", lines.Select(l => "  " + l.Trim()));
    }
}
