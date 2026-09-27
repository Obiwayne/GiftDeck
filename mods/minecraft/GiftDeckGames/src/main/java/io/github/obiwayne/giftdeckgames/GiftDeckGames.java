package io.github.obiwayne.giftdeckgames;

import org.bukkit.Bukkit;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.command.Command;
import org.bukkit.command.CommandSender;
import org.bukkit.command.PluginCommand;
import org.bukkit.command.TabCompleter;
import org.bukkit.entity.Player;
import org.bukkit.entity.Sheep;
import org.bukkit.event.EventHandler;
import org.bukkit.event.EventPriority;
import org.bukkit.event.Listener;
import org.bukkit.event.block.BlockBreakEvent;
import org.bukkit.event.entity.EntityDeathEvent;
import org.bukkit.event.entity.PlayerDeathEvent;
import org.bukkit.event.player.PlayerJoinEvent;
import org.bukkit.event.player.PlayerRespawnEvent;
import org.bukkit.plugin.java.JavaPlugin;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collection;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.stream.Collectors;

// GiftDeck Games: streamer-vs-viewers mini-games for TikTok LIVE, driven by server commands so GiftDeck (or
// anything with RCON or a console) can run them:
//   /gdg <game> start [player]    builds the arena far from spawn and puts the player in it (their things are saved)
//   /gdg <game> stop | reset      stop clears the arena and gives everything back; reset starts over
//   /gdg <game> <gift> ...        what a viewer's gift does, e.g. /gdg bedrockbox tnt 3 Bob
//   /gdg status | stopall | help
// Every reply is one line. Failures start with "Could not" so GiftDeck shows them as failed.
public final class GiftDeckGames extends JavaPlugin implements Listener, TabCompleter {
    final Map<String, Game> games = new LinkedHashMap<>();
    Snapshots snapshots;

    @Override
    public void onEnable() {
        saveDefaultConfig();
        snapshots = new Snapshots(this);
        for (Game g : new Game[]{new BedrockBox(this), new SandPour(this), new SheepOut(this)}) games.put(g.id, g);
        PluginCommand cmd = getCommand("gdg");
        if (cmd != null) {
            cmd.setExecutor(this);
            cmd.setTabCompleter(this);
        }
        getServer().getPluginManager().registerEvents(this, this);
        Bukkit.getScheduler().runTaskTimer(this, () -> {
            for (Game g : games.values()) {
                try { g.tick(); } catch (Exception e) { getLogger().warning(g.title + ": " + e); }
            }
        }, 10L, 10L);
        getLogger().info("GiftDeck Games ready: /gdg help (games: " + String.join(", ", games.keySet()) + ")");
    }

    @Override
    public void onDisable() {
        // Server stopping: give players their things back and leave no arena behind.
        for (Game g : games.values()) {
            try { if (g.state != Game.State.OFF) g.stop(); } catch (Exception e) { getLogger().warning(g.title + ": " + e); }
        }
    }

    // ---------------- arena settings ----------------

    World arenaWorld() {
        String name = getConfig().getString("arena.world", "");
        World w = name == null || name.isEmpty() ? null : Bukkit.getWorld(name);
        return w != null ? w : Bukkit.getWorlds().get(0);
    }

    int arenaX() { return Math.floorDiv(getConfig().getInt("arena.x", 20000), 16) * 16; }
    int arenaZ() { return Math.floorDiv(getConfig().getInt("arena.z", 0), 16) * 16; }

    int arenaY() {
        World w = arenaWorld();
        int y = getConfig().getInt("arena.y", 200);
        return Math.max(w.getMinHeight() + 3, Math.min(w.getMaxHeight() - 75, y));
    }

    // ---------------- players ----------------

    // A name, or a selector / nothing = the only (or first) player online. Null when there is nobody.
    Player findPlayer(String who) {
        if (who != null && !who.isEmpty() && !who.startsWith("@")) return Bukkit.getPlayerExact(who);
        Collection<? extends Player> online = Bukkit.getOnlinePlayers();
        return online.isEmpty() ? null : online.iterator().next();
    }

    // One game at a time: the streamer can only be in one arena.
    List<String> stopOthers(Game keep) {
        List<String> stopped = new ArrayList<>();
        for (Game g : games.values())
            if (g != keep && g.state != Game.State.OFF) {
                g.stop();
                stopped.add(g.title);
            }
        return stopped;
    }

    Game gameOf(Player p) {
        for (Game g : games.values())
            if (g.playerId != null && g.playerId.equals(p.getUniqueId())) return g;
        return null;
    }

    // ---------------- commands ----------------

    @Override
    public boolean onCommand(CommandSender sender, Command command, String label, String[] args) {
        String reply;
        try {
            reply = run(args);
        } catch (GameException e) {
            reply = e.getMessage();
        } catch (Exception e) {
            getLogger().log(java.util.logging.Level.WARNING, "/gdg " + String.join(" ", args), e);
            reply = "Could not do that: " + e;
        }
        sender.sendMessage(reply);
        return true;
    }

    private String run(String[] args) throws GameException {
        if (args.length == 0 || args[0].equalsIgnoreCase("help")) return help();
        String first = args[0].toLowerCase(Locale.ROOT);
        if (first.equals("status"))
            return games.values().stream().map(Game::status).collect(Collectors.joining(" | "));
        if (first.equals("stopall")) {
            List<String> stopped = stopOthers(null);
            return stopped.isEmpty() ? "No game was running." : "Stopped " + String.join(", ", stopped) + ".";
        }
        Game g = games.get(first);
        if (g == null) throw new GameException("Could not find a game called \"" + args[0] + "\". Games: " + String.join(", ", games.keySet()));
        if (args.length < 2) return g.title + ": " + String.join(" | ", g.usage());
        String action = args[1].toLowerCase(Locale.ROOT);
        switch (action) {
            case "start": return g.start(args.length > 2 ? args[2] : "");
            case "stop": return g.stop();
            case "reset": return g.reset();
            case "status": return g.status();
            case "help": return g.title + ": start [player] | stop | reset | " + String.join(" | ", g.usage());
            default: return g.gift(action, new Args(args, 2));
        }
    }

    private String help() {
        return "GiftDeck Games " + getDescription().getVersion() + ": /gdg <" + String.join("|", games.keySet())
                + "> start [player] | stop | reset | help, and /gdg status | stopall. Gifts: "
                + games.values().stream().map(g -> g.id + " " + String.join("/", actionsOf(g))).collect(Collectors.joining("; "));
    }

    private static List<String> actionsOf(Game g) {
        if (g instanceof BedrockBox) return BedrockBox.actions();
        if (g instanceof SandPour) return SandPour.actions();
        if (g instanceof SheepOut) return SheepOut.actions();
        return new ArrayList<>();
    }

    @Override
    public List<String> onTabComplete(CommandSender sender, Command command, String alias, String[] args) {
        List<String> options = new ArrayList<>();
        if (args.length == 1) {
            options.addAll(games.keySet());
            options.addAll(Arrays.asList("status", "stopall", "help"));
        } else if (args.length == 2 && games.containsKey(args[0].toLowerCase(Locale.ROOT))) {
            options.addAll(Arrays.asList("start", "stop", "reset", "status", "help"));
            options.addAll(actionsOf(games.get(args[0].toLowerCase(Locale.ROOT))));
        } else if (args.length == 3 && args[1].equalsIgnoreCase("start")) {
            for (Player p : Bukkit.getOnlinePlayers()) options.add(p.getName());
        }
        String typed = args.length == 0 ? "" : args[args.length - 1].toLowerCase(Locale.ROOT);
        return options.stream().filter(o -> o.toLowerCase(Locale.ROOT).startsWith(typed)).collect(Collectors.toList());
    }

    // ---------------- events ----------------

    @EventHandler
    public void onDeath(PlayerDeathEvent e) {
        Player p = e.getEntity();
        Game g = gameOf(p);
        if (g == null) return;
        // The kit disappears (the real inventory is saved and comes back at the respawn).
        e.setKeepInventory(false);
        e.getDrops().clear();
        e.setDroppedExp(0);
        if (g.state == Game.State.RUNNING) g.end(false, p.getName() + " died. The viewers win!");
        else g.release();
    }

    @EventHandler(priority = EventPriority.HIGH)
    public void onRespawn(PlayerRespawnEvent e) {
        Player p = e.getPlayer();
        Game g = gameOf(p);
        if (g != null && g.state == Game.State.RUNNING) return;
        Location back = snapshots.savedLocation(p.getUniqueId());
        if (back == null) return;
        e.setRespawnLocation(back);
        Bukkit.getScheduler().runTaskLater(this, () -> { if (p.isOnline() && gameOf(p) == null) snapshots.restore(p); }, 2L);
    }

    @EventHandler
    public void onJoin(PlayerJoinEvent e) {
        Player p = e.getPlayer();
        // A game started before the streamer was online pulls them in now.
        for (Game g : games.values()) {
            if (g.state == Game.State.RUNNING && g.playerId == null && g.waitingFor != null
                    && (g.waitingFor.isEmpty() || g.waitingFor.equalsIgnoreCase(p.getName()))) {
                Bukkit.getScheduler().runTaskLater(this, () -> {
                    if (p.isOnline() && g.state == Game.State.RUNNING && g.playerId == null) g.enter(p);
                }, 20L);
                return;
            }
        }
        // Left in the middle of a game (or the server stopped): give their things back.
        if (snapshots.has(p.getUniqueId()))
            Bukkit.getScheduler().runTaskLater(this, () -> { if (p.isOnline() && gameOf(p) == null) snapshots.restore(p); }, 20L);
    }

    // The arena walls can't be broken, and nothing in the Sheep Out meadow.
    @EventHandler(ignoreCancelled = true)
    public void onBreak(BlockBreakEvent e) {
        for (Game g : games.values()) {
            if (!g.contains(e.getBlock().getLocation())) continue;
            Material m = e.getBlock().getType();
            if (g instanceof SheepOut || m == Material.GLASS || m == Material.BARRIER || m == Material.BEDROCK) e.setCancelled(true);
            return;
        }
    }

    // Cleared sheep count for the streamer and drop nothing (no wool everywhere).
    @EventHandler
    public void onEntityDeath(EntityDeathEvent e) {
        if (!e.getEntity().getScoreboardTags().contains("giftdeckgames")) return;
        e.getDrops().clear();
        e.setDroppedExp(0);
        if (e.getEntity() instanceof Sheep)
            for (Game g : games.values())
                if (g instanceof SheepOut && g.state == Game.State.RUNNING && g.contains(e.getEntity().getLocation()))
                    ((SheepOut) g).sheepKilled();
    }
}
