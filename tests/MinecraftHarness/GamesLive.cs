using System.IO;
using System.Text.Json.Nodes;
using GiftDeck.Models;
using GiftDeck.Services;

// Checks for the GiftDeck Games plugin (mods/minecraft/GiftDeckGames, shipped as Packs/minecraft/plugins/GiftDeckGames.jar).
//   unit:  the bundled jar, and how MinecraftServer copies plugins into a server (missing / same / newer / older)
//   games: a real Paper server in a scratch folder with the plugin: every mini-game started with nobody online,
//          every gift command through MinecraftTarget (commands.json), blocks and entities checked with vanilla
//          "execute if", a lose (full meadow) and a timer win, stop and clean-up, then the server log for errors.
static class GamesLive
{
    static int _fail;
    static Action<bool, string> _check;

    static void Check(bool ok, string what)
    {
        if (_check != null) { _check(ok, what); return; }
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    static string PackDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "Packs", "minecraft");
            if (File.Exists(Path.Combine(p, "pack.json"))) return p;
        }
        throw new DirectoryNotFoundException("Packs/minecraft");
    }

    // ---------------- unit ----------------

    public static void Unit(Action<bool, string> check)
    {
        _check = check;
        Console.WriteLine("GiftDeck Games plugin");
        var src = Path.Combine(PackDir(), "plugins");
        var jar = Path.Combine(src, "GiftDeckGames.jar");
        Check(File.Exists(jar), "Packs/minecraft/plugins/GiftDeckGames.jar is bundled");
        var ver = MinecraftServer.PluginVersion(jar);
        Check(MinecraftTarget.ParseVersion(ver) != null, "its plugin.yml has a version: " + ver);

        var cmds = MinecraftTarget.LoadCommands(File.ReadAllText(Path.Combine(PackDir(), "commands.json")));
        foreach (var id in new[] { "bb_start", "bb_tnt", "bb_sand", "bb_mob", "sp_start", "sp_pour", "so_start", "so_sheep", "gdg_status" })
            Check(cmds.Any(c => c.Id == id), "commands.json has " + id);
        var tnt = cmds.First(c => c.Id == "bb_tnt");
        var line = MinecraftTarget.Render(tnt.Run[0], tnt, new Dictionary<string, string> { ["count"] = "3", ["viewer"] = "Big Bob", ["player"] = "@p" }, null);
        Check(line == "gdg bedrockbox tnt 3 Big Bob", "bb_tnt renders: " + line);

        // Copying into a server folder: missing -> copied, same -> left alone, user's newer -> kept, older -> replaced.
        var root = Path.Combine(Path.GetTempPath(), "giftdeck-mc-unit", "plugins-test");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var server = new MinecraftServer(new MinecraftSettings(), Path.Combine(root, "server"), Path.Combine(root, "java")) { PluginsSource = src };
        var dest = Path.Combine(server.Folder, "plugins", "GiftDeckGames.jar");
        Check(server.InstallPlugins().Count == 1 && File.Exists(dest), "copied into plugins/ when missing");
        Check(server.InstallPlugins().Count == 0, "left alone when it is the same jar");
        File.WriteAllBytes(dest, MakeJar("99.0.0"));
        Check(server.InstallPlugins().Count == 0 && MinecraftServer.PluginVersion(dest) == "99.0.0", "a newer jar the user put there is kept");
        File.WriteAllBytes(dest, MakeJar("0.1.0"));
        Check(server.InstallPlugins().Count == 1 && MinecraftServer.PluginVersion(dest) == ver, "an older jar is replaced");
        Directory.Delete(root, true);
    }

    static byte[] MakeJar(string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
        using (var w = new StreamWriter(zip.CreateEntry("plugin.yml").Open()))
            w.Write($"name: GiftDeckGames\nversion: '{version}'\nmain: x.Y\n");
        return ms.ToArray();
    }

    // ---------------- live ----------------

    public static async Task<int> Run(string scratch, string version)
    {
        scratch = Path.GetFullPath(scratch);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", Path.Combine(scratch, "giftdeck-data"));
        var settings = new MinecraftSettings { ServerPort = 25665, RconPort = 25675, MemoryMb = 2048, PlayerName = "" };
        var progress = new Progress<InstallProgress>(p => { if (p.Fraction < 0 || p.Fraction >= 1) Console.WriteLine("  " + p.Text); });
        Console.WriteLine($"Resolving Paper '{version}'…");
        var build = await MinecraftServer.ResolveAsync(version);
        var server = new MinecraftServer(settings, Path.Combine(scratch, "server-" + build.Version), Path.Combine(scratch, "java"))
        {
            PluginsSource = Path.Combine(PackDir(), "plugins"),
        };
        var target = new MinecraftTarget(server, Path.Combine(PackDir(), "commands.json"));
        var log = new List<string>();
        server.ConsoleLine += l => { lock (log) log.Add(l); if (l.Contains("GiftDeck") || l.Contains("ERROR") || l.Contains("WARN") || l.Contains("Done (")) Console.WriteLine("  | " + l); };
        Console.WriteLine($"  Paper {build.Version} build {build.Build}, needs Java {build.JavaMin}+");

        var java = server.FindJava(build.JavaMin) ?? await server.DownloadJavaAsync(build.JavaMin, progress);
        Console.WriteLine($"  Java {java.Version}: {java.Exe}");
        if (!server.IsInstalled || server.InstalledVersion != build.Version || server.Info.Build != build.Build)
            await server.InstallAsync(build, progress);
        Check(File.Exists(Path.Combine(server.Folder, "plugins", "GiftDeckGames.jar")), "set-up put GiftDeckGames.jar in plugins/");
        server.AcceptEula(); // our own throwaway test server

        // A short Sand Pour timer so the test sees a win; everything else as shipped.
        var cfgDir = Path.Combine(server.Folder, "plugins", "GiftDeckGames");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "config.yml"),
            "arena:\n  world: \"\"\n  x: 20000\n  z: 0\n  y: 200\nbedrockbox:\n  depth: 30\n  obsidian-layer: true\nsandpour:\n  seconds: 30\nsheepout:\n  seconds: 180\n  max-sheep: 40\n");

        var t0 = DateTime.Now;
        await server.StartAsync(java);
        while (server.State == MinecraftServerState.Starting && (DateTime.Now - t0).TotalMinutes < 6) await Task.Delay(500);
        Console.WriteLine($"Started in {(DateTime.Now - t0).TotalSeconds:0} s: {server.StateText}");
        Check(server.State == MinecraftServerState.Running && target.Connected, "server running, RCON connected");
        lock (log) Check(log.Any(l => l.Contains("GiftDeck Games ready")), "plugin enabled");
        await Rcon(server, "plugins");
        await Rcon(server, "gdg help");

        var ev = new LiveEvent { Type = "gift", Nickname = "Tester \"Q\" 1", GiftName = "Rose", Diamonds = 1 };

        Console.WriteLine("Errors are errors (GiftDeck shows them as failed):");
        await Expect(target, "bb_tnt", new JsonObject(), ev, false, "gift while the game is off");
        var r = await server.ExecuteAsync("gdg nosuchgame start");
        Check(MinecraftTarget.IsError(MinecraftTarget.CleanReply(r)), "unknown game: " + r);

        // ---- Bedrock Box ----
        Console.WriteLine("Bedrock Box (nobody online):");
        await Expect(target, "bb_start", new JsonObject(), ev, true, "start");
        await Test(server, "execute if block 20007 200 7 minecraft:bedrock", "bedrock floor at 20007 200 7");
        await Test(server, "execute if block 20007 201 7 minecraft:emerald_block", "emerald goal at y 201");
        await Test(server, "execute if block 20004 230 7 minecraft:bedrock", "bedrock wall");
        await Test(server, "execute if block 20007 238 7 minecraft:barrier", "barrier roof");
        await Test(server, "execute if block 20007 204 7 minecraft:obsidian", "obsidian layer");
        await Expect(target, "bb_tnt", new JsonObject { ["count"] = "3" }, ev, true, "3 TNT");
        await Count(server, "@e[type=tnt,tag=giftdeckgames]", 3, "TNT entities named after the viewer");
        await Expect(target, "bb_sand", new JsonObject { ["count"] = "10", ["block"] = "gravel" }, ev, true, "10 gravel", "poured 10 gravel");
        await Expect(target, "bb_anvil", new JsonObject { ["count"] = "2" }, ev, true, "2 anvils", "dropped 2 anvils");
        await Expect(target, "bb_mob", new JsonObject { ["mob"] = "creeper", ["count"] = "2" }, ev, true, "2 creepers");
        await Count(server, "@e[type=creeper,tag=giftdeckgames]", 2, "creepers");
        await Expect(target, "bb_mob", new JsonObject { ["mob"] = "op_me", ["count"] = "2" }, ev, true, "an unknown mob falls back to the default (zombie)");
        await Expect(target, "bb_pickaxe", new JsonObject(), ev, true, "pickaxe (nobody to give it to)");
        await Expect(target, "bb_haste", new JsonObject(), ev, true, "haste");
        await Expect(target, "bb_drill", new JsonObject { ["layers"] = "5" }, ev, true, "drill 5", "drilled 5 blocks");
        await Expect(target, "bb_heal", new JsonObject(), ev, true, "heal");
        await Rcon(server, "gdg bedrockbox tnt 2");                  // no viewer name: "Someone"
        await Rcon(server, "gdg bedrockbox sand 4 lava Bob");        // a word that isn't a block
        await Rcon(server, "gdg bedrockbox dance 3 Bob");            // no such gift
        await Task.Delay(6000); // TNT goes off, sand lands
        await Expect(target, "gdg_status", new JsonObject(), ev, true, "status");
        await Expect(target, "bb_reset", new JsonObject(), ev, true, "reset");
        await Count(server, "@e[tag=giftdeckgames]", 0, "reset removed the gifts");

        // ---- Sand Pour (starting it stops the Bedrock Box) ----
        Console.WriteLine("Sand Pour (30 s timer for the test):");
        await Expect(target, "sp_start", new JsonObject(), ev, true, "start (stops Bedrock Box)");
        await Test(server, "execute if block 20007 200 7 minecraft:air", "Bedrock Box arena cleared");
        await Test(server, "execute if block 20039 200 7 minecraft:bedrock", "Sand Pour floor at 20039 200 7");
        await Test(server, "execute if block 20034 210 7 minecraft:glass", "glass wall");
        await Expect(target, "sp_pour", new JsonObject { ["count"] = "1" }, ev, true, "pour 1");
        await Expect(target, "sp_pour", new JsonObject { ["count"] = "25", ["block"] = "concrete" }, ev, true, "pour 25 concrete powder");
        await Expect(target, "sp_pour", new JsonObject { ["count"] = "200", ["block"] = "sand" }, ev, true, "pour 200 sand", "poured 200 sand");
        await Expect(target, "sp_pour", new JsonObject { ["count"] = "50", ["block"] = "anvil" }, ev, true, "pour anvils (capped at 12)", "poured 12 anvils");
        await Expect(target, "sp_shovel", new JsonObject(), ev, true, "shovel");
        await Expect(target, "sp_haste", new JsonObject(), ev, true, "haste");
        await Expect(target, "sp_heal", new JsonObject(), ev, true, "heal");
        await Task.Delay(5000);
        var landed = MinecraftTarget.CleanReply(await server.ExecuteAsync("execute unless block 20039 201 7 minecraft:air")).StartsWith("Test passed");
        var falling = MinecraftTarget.CleanReply(await server.ExecuteAsync("execute if entity @e[type=falling_block,x=20032,y=190,z=0,dx=16,dy=50,dz=16]"));
        Console.WriteLine($"  sand on the pit floor: {landed}; falling blocks still in the air: {falling}");
        Check(landed || falling.StartsWith("Test passed"),
            landed ? "sand landed on the pit floor" : "sand is falling blocks waiting in the air (this Paper doesn't move them with nobody online)");
        await Expect(target, "sp_dig", new JsonObject(), ev, true, "dig out");
        Console.WriteLine("  waiting for the 30 s timer…");
        var until = DateTime.Now.AddSeconds(40);
        string st = "";
        while (DateTime.Now < until)
        {
            st = await server.ExecuteAsync("gdg sandpour status");
            if (st.Contains("over")) break;
            await Task.Delay(2000);
        }
        Check(st.Contains("over") && st.Contains("Time's up"), "timer ran out: " + st);
        await Expect(target, "sp_pour", new JsonObject(), ev, false, "gift after the game is over");
        await Expect(target, "sp_stop", new JsonObject(), ev, true, "stop");
        await Test(server, "execute if block 20039 200 7 minecraft:air", "Sand Pour arena cleared");
        await Count(server, "@e[x=20032,y=190,z=0,dx=16,dy=50,dz=16,type=!player]", 0, "no sand, anvils or items left");

        // ---- Sheep Out ----
        Console.WriteLine("Sheep Out:");
        await Expect(target, "so_start", new JsonObject(), ev, true, "start");
        await Test(server, "execute if block 20072 200 7 minecraft:grass_block", "meadow at 20072 200 7");
        await Test(server, "execute if block 20065 202 7 minecraft:glass", "glass fence");
        await Expect(target, "so_sheep", new JsonObject { ["count"] = "5", ["color"] = "red" }, ev, true, "5 red sheep");
        await Count(server, "@e[type=sheep,tag=giftdeckgames_sheep]", 5, "sheep in the meadow");
        await Expect(target, "so_sheep", new JsonObject { ["count"] = "3", ["color"] = "rainbow" }, ev, true, "3 rainbow sheep");
        await Expect(target, "so_smite", new JsonObject { ["count"] = "4" }, ev, true, "lightning on 4");
        await Count(server, "@e[type=sheep,tag=giftdeckgames_sheep]", 4, "4 left");
        await Expect(target, "so_sword", new JsonObject(), ev, true, "sword");
        await Expect(target, "so_heal", new JsonObject(), ev, true, "heal");
        await Expect(target, "so_sheep", new JsonObject { ["count"] = "30" }, ev, true, "30 sheep");
        await Expect(target, "so_sheep", new JsonObject { ["count"] = "10", ["color"] = "black" }, ev, true, "10 black sheep");
        await Task.Delay(1500);
        st = await server.ExecuteAsync("gdg sheepout status");
        Check(st.Contains("over") && st.Contains("meadow is full"), "44 sheep = the viewers win: " + st);
        await Expect(target, "so_reset", new JsonObject(), ev, true, "reset after the loss");
        st = await server.ExecuteAsync("gdg sheepout status");
        Check(st.Contains("running") && st.Contains("0/40"), "running again, meadow empty: " + st);
        await Expect(target, "gdg_stopall", new JsonObject(), ev, true, "stop all");
        await Count(server, "@e[tag=giftdeckgames]", 0, "no game entities left anywhere");
        await Test(server, "execute if block 20072 200 7 minecraft:air", "Sheep Out arena cleared");
        await Rcon(server, "gdg status");

        Console.WriteLine("Stopping…");
        await server.StopAsync();
        Check(server.State == MinecraftServerState.Stopped, "stopped");
        Check(server.ConsoleTail().Any(l => l.Contains("exit code 0")), "exit code 0");
        List<string> bad;
        lock (log) bad = log.Where(l => (l.Contains("ERROR") || l.Contains("Exception") || (l.Contains("WARN") && l.Contains("GiftDeckGames"))) && !l.Contains("Could not")).ToList();
        foreach (var b in bad) Console.WriteLine("  log: " + b);
        Check(bad.Count == 0, "no errors or plugin warnings in the server log");
        target.Dispose();
        Console.WriteLine(_fail == 0 ? "GAMES PASSED" : $"{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }

    static async Task Expect(MinecraftTarget target, string command, JsonObject args, LiveEvent e, bool ok, string what, string mustSay = null)
    {
        var r = await target.RunAsync(command, args, e);
        Check(r.Ok == ok && (mustSay == null || r.Message.Contains(mustSay)), $"{command,-11} {what}: {r.Message}");
    }

    static async Task Test(MinecraftServer server, string cmd, string what)
    {
        var reply = MinecraftTarget.CleanReply(await server.ExecuteAsync(cmd));
        Check(reply.StartsWith("Test passed"), $"{what} ({reply})");
    }

    static async Task Count(MinecraftServer server, string selector, int expect, string what)
    {
        var reply = MinecraftTarget.CleanReply(await server.ExecuteAsync("execute if entity " + selector));
        // "Test passed, count: 3" or "Test failed"
        int n = reply.StartsWith("Test passed") && int.TryParse(reply.Split(':').Last().Trim(), out var c) ? c : 0;
        Check(n == expect, $"{what}: {n} (expected {expect})");
    }

    static async Task Rcon(MinecraftServer server, string cmd)
    {
        try
        {
            var reply = MinecraftTarget.StripColors(await server.ExecuteAsync(cmd));
            Console.WriteLine($"  > {cmd}\n    < {(reply.Length == 0 ? "(empty reply)" : reply)}");
        }
        catch (Exception ex) { Console.WriteLine($"  > {cmd}\n    ! {ex.Message}"); }
    }
}
