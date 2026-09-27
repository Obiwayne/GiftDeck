package io.github.obiwayne.giftdeckgames;

import org.bukkit.Bukkit;
import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.World;
import org.bukkit.block.Block;
import org.bukkit.boss.BarColor;
import org.bukkit.boss.BarStyle;
import org.bukkit.boss.BossBar;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.scheduler.BukkitTask;
import org.bukkit.scoreboard.Criteria;
import org.bukkit.scoreboard.DisplaySlot;
import org.bukkit.scoreboard.Objective;
import org.bukkit.scoreboard.Scoreboard;
import org.bukkit.util.BoundingBox;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Random;
import java.util.UUID;
import java.util.stream.Collectors;

// One mini-game: an arena in its own chunk far from spawn, the streamer (or nobody, then gifts land in the empty
// arena), a boss bar with the latest gift, a sidebar with the viewers who sent the most, and a win/lose check.
// Subclasses build the arena and turn gift commands into things happening in it.
abstract class Game {
    enum State { OFF, RUNNING, OVER }

    protected final GiftDeckGames plugin;
    final String id;       // the command word: bedrockbox
    final String title;    // Bedrock Box
    private final int slot; // which chunk along x the arena uses
    protected final Random random = new Random();

    State state = State.OFF;
    UUID playerId;          // the streamer while they are in the game
    String playerName;      // the streamer's name (kept after the game, for reset)
    String waitingFor;      // a name to pull in when they join ("" = the first player who joins); null = nobody
    World world;
    int ox, oy, oz;         // arena origin: the chunk's corner at floor height
    private BossBar bar;
    private Scoreboard board;
    private Objective sidebar;
    private final Map<String, Integer> points = new HashMap<>();
    private String last = "";
    private long startedAt, endsAt; // ms; endsAt 0 = no timer
    String result = "";
    private BukkitTask restoreTask;

    Game(GiftDeckGames plugin, String id, String title, int slot) {
        this.plugin = plugin;
        this.id = id;
        this.title = title;
        this.slot = slot;
    }

    // ---------------- what each game provides ----------------

    abstract int height();                       // blocks the arena uses above the floor
    abstract void build();                       // place the arena (the chunk is already empty)
    abstract Location spawn();                   // where the streamer starts
    abstract void kit(Player p);                 // what they get to play with
    abstract int timerSeconds();                 // 0 = no time limit
    abstract String goal();                      // one line shown when the game starts
    abstract String barText();                   // the boss bar's first part (depth, sheep count, ...)
    abstract double barProgress();
    abstract void check();                       // win/lose checks, every half second while running
    abstract String effect(String action, Args a) throws GameException; // a gift: returns what happened
    abstract List<String> usage();               // "tnt <count> [viewer]", ...
    void onTimeUp() { end(true, "Time's up! " + streamerLabel() + " survived."); }
    BarColor barColor() { return BarColor.YELLOW; }

    // ---------------- start / stop ----------------

    String start(String who) throws GameException {
        if (state == State.RUNNING)
            throw new GameException("Could not start " + title + ": it is already running (use reset or stop).");
        List<String> stopped = plugin.stopOthers(this);
        Player p = plugin.findPlayer(who);
        begin(p, p == null ? wantedName(who) : null);
        String pre = stopped.isEmpty() ? "" : "Stopped " + String.join(", ", stopped) + ". ";
        if (p != null) return pre + "Started " + title + " with " + p.getName() + ". " + goal();
        Location s = spawn();
        return pre + "Started " + title + " at " + s.getBlockX() + " " + s.getBlockY() + " " + s.getBlockZ() + ", but "
                + (waitingFor == null || waitingFor.isEmpty() ? "nobody is online" : waitingFor + " isn't online")
                + ": they join the game when they log in. Gifts land in the empty arena until then.";
    }

    private static String wantedName(String who) {
        if (who == null || who.isEmpty() || who.startsWith("@")) return "";
        return who;
    }

    // Starts again from scratch with the same streamer (keeps their saved inventory).
    String reset() throws GameException {
        if (state == State.OFF) return start(playerName == null ? "" : playerName);
        Player p = player();
        if (p == null && playerName != null) p = Bukkit.getPlayerExact(playerName);
        String wait = waitingFor;
        teardown(false);
        begin(p, p == null ? (wait == null ? "" : wait) : null);
        return "Reset " + title + (p != null ? " with " + p.getName() : "") + ". " + goal();
    }

    String stop() {
        if (state == State.OFF) return title + " isn't running.";
        teardown(true);
        return "Stopped " + title + ": the arena is cleared" + (playerName != null ? " and " + playerName + " is back where they were" : "") + ".";
    }

    private void begin(Player p, String wait) {
        world = plugin.arenaWorld();
        ox = plugin.arenaX() + slot * 32;
        oz = plugin.arenaZ();
        oy = plugin.arenaY();
        world.getChunkAt(ox >> 4, oz >> 4).addPluginChunkTicket(plugin); // keeps it running with nobody near
        clearArena();
        build();
        points.clear();
        last = "";
        result = "";
        state = State.RUNNING;
        startedAt = System.currentTimeMillis();
        endsAt = timerSeconds() > 0 ? startedAt + timerSeconds() * 1000L : 0;
        bar = Bukkit.createBossBar(title, barColor(), BarStyle.SEGMENTED_10);
        board = Bukkit.getScoreboardManager().getNewScoreboard();
        sidebar = board.registerNewObjective("gdg", Criteria.DUMMY, "§6§l" + title + " §7viewers");
        sidebar.setDisplaySlot(DisplaySlot.SIDEBAR);
        playerId = null;
        waitingFor = wait;
        if (p != null) enter(p);
        updateBar();
        plugin.getLogger().info("Started " + title + " at " + ox + " " + oy + " " + oz + (p != null ? " with " + p.getName() : " (no player)"));
    }

    // Puts the streamer into the arena with the game's kit (their own things are saved first).
    void enter(Player p) {
        if (restoreTask != null) { restoreTask.cancel(); restoreTask = null; }
        plugin.snapshots.saveIfMissing(p);
        playerId = p.getUniqueId();
        playerName = p.getName();
        waitingFor = null;
        p.teleport(spawn());
        p.getInventory().clear();
        Compat.clearEffects(p);
        p.setGameMode(GameMode.SURVIVAL);
        Compat.heal(p);
        p.setFallDistance(0);
        kit(p);
        if (bar != null) bar.addPlayer(p);
        if (board != null) p.setScoreboard(board);
        Compat.title(p, "§6" + title, "§f" + goal());
        Compat.sound(p, "minecraft:block.note_block.pling", 1.2f);
    }

    // Takes the streamer out: back where they were with their own things (after they respawn, if dead).
    void release() {
        Player p = player();
        playerId = null;
        if (p == null) return;
        if (bar != null) bar.removePlayer(p);
        p.setScoreboard(Bukkit.getScoreboardManager().getMainScoreboard());
        if (!p.isDead()) plugin.snapshots.restore(p);
    }

    private void teardown(boolean restorePlayer) {
        if (restoreTask != null) { restoreTask.cancel(); restoreTask = null; }
        if (restorePlayer) release();
        else {
            Player p = player();
            if (p != null) p.setScoreboard(Bukkit.getScoreboardManager().getMainScoreboard());
            playerId = null;
        }
        if (bar != null) { bar.removeAll(); bar = null; }
        if (sidebar != null) { try { sidebar.unregister(); } catch (Exception ignored) { } sidebar = null; }
        board = null;
        if (world != null) {
            clearArena();
            world.getChunkAt(ox >> 4, oz >> 4).removePluginChunkTicket(plugin);
        }
        waitingFor = null;
        state = State.OFF;
    }

    // The game is decided. The streamer gets their things back 5 seconds later (or when they respawn).
    void end(boolean streamerWon, String why) {
        if (state != State.RUNNING) return;
        state = State.OVER;
        result = why;
        String top = topViewers(3);
        last = why;
        updateBar();
        if (bar != null) bar.setColor(streamerWon ? BarColor.GREEN : BarColor.RED);
        Player p = player();
        Compat.title(p, streamerWon ? "§a§lYOU WIN" : "§c§lVIEWERS WIN", "§f" + why);
        Compat.sound(p, streamerWon ? "minecraft:ui.toast.challenge_complete" : "minecraft:entity.wither.death", 1f);
        tell(p, (streamerWon ? "§a" : "§c") + why + (top.isEmpty() ? "" : " §7Top viewers: §f" + top));
        plugin.getLogger().info(title + " over: " + why + (top.isEmpty() ? "" : " Top viewers: " + top));
        if (p != null && !p.isDead())
            restoreTask = Bukkit.getScheduler().runTaskLater(plugin, () -> { restoreTask = null; release(); }, 100L);
        else release(); // dead (or nobody): the respawn puts them back
    }

    // ---------------- every half second ----------------

    void tick() {
        if (state != State.RUNNING) return;
        Player p = player();
        if (p != null && !p.isOnline()) playerId = null;
        check();
        if (state == State.RUNNING && endsAt > 0 && System.currentTimeMillis() >= endsAt) onTimeUp();
        updateBar();
    }

    private void updateBar() {
        if (bar == null) return;
        String text = "§6" + title + "§f  " + barText();
        if (state == State.RUNNING && endsAt > 0) text += "§7  ·  §f" + clock(secondsLeft()) + " left";
        if (!last.isEmpty()) text += "§7  ·  §e" + last;
        bar.setTitle(text);
        bar.setProgress(Math.max(0, Math.min(1, barProgress())));
    }

    int secondsLeft() {
        return endsAt == 0 ? 0 : (int) Math.max(0, (endsAt - System.currentTimeMillis() + 999) / 1000);
    }

    double timeFraction() {
        return endsAt == 0 ? 1 : secondsLeft() / (double) Math.max(1, timerSeconds());
    }

    static String clock(int s) {
        return (s / 60) + ":" + String.format("%02d", s % 60);
    }

    String status() {
        switch (state) {
            case OFF: return title + ": off";
            case OVER: return title + ": over (" + result + ")";
            default:
                String who = player() != null ? player().getName() : (waitingFor == null || waitingFor.isEmpty() ? "no player" : "waiting for " + waitingFor);
                return title + ": running, " + who + ", " + stripColors(barText()) + (endsAt > 0 ? ", " + clock(secondsLeft()) + " left" : "");
        }
    }

    // ---------------- gifts ----------------

    // Runs a gift command: checks the game is on, then the game does it and the viewer gets the credit.
    String gift(String action, Args a) throws GameException {
        if (state == State.OFF) throw new GameException("Could not " + action + ": " + title + " isn't running (start it first).");
        if (state == State.OVER) throw new GameException("Could not " + action + ": " + title + " is over (" + result + "). Reset or stop it.");
        return effect(action, a);
    }

    // Credits a viewer and tells the streamer. Returns the reply for the command's sender.
    String credit(String viewer, int amount, String what) {
        points.merge(viewer, Math.max(1, amount), Integer::sum);
        if (sidebar != null) {
            try { sidebar.getScore(viewer).setScore(points.get(viewer)); } catch (Exception ignored) { }
        }
        last = viewer + " " + what;
        updateBar();
        Player p = player();
        tell(p, "§e" + viewer + "§f " + what);
        return title + ": " + viewer + " " + what + (p == null ? " (no player in the game: it happened in the empty arena)" : "");
    }

    String topViewers(int n) {
        return points.entrySet().stream()
                .sorted(Map.Entry.<String, Integer>comparingByValue(Comparator.reverseOrder()))
                .limit(n).map(e -> e.getKey() + " " + e.getValue()).collect(Collectors.joining(", "));
    }

    void tell(Player p, String text) {
        if (p != null) p.sendMessage("§6[" + title + "] §r" + text);
    }

    // ---------------- helpers ----------------

    Player player() {
        return playerId == null ? null : Bukkit.getPlayer(playerId);
    }

    String streamerLabel() {
        return playerName != null && playerId != null ? playerName : "The streamer";
    }

    Block block(int x, int y, int z) {
        return world.getBlockAt(ox + x, oy + y, oz + z);
    }

    void set(int x, int y, int z, Material m) {
        block(x, y, z).setType(m, false);
    }

    void fill(int x1, int y1, int z1, int x2, int y2, int z2, Material m) {
        for (int x = Math.min(x1, x2); x <= Math.max(x1, x2); x++)
            for (int y = Math.min(y1, y2); y <= Math.max(y1, y2); y++)
                for (int z = Math.min(z1, z2); z <= Math.max(z1, z2); z++)
                    set(x, y, z, m);
    }

    // Four walls of a square from (a,a) to (b,b), from y1 to y2.
    void walls(int a, int b, int y1, int y2, Material m) {
        for (int y = y1; y <= y2; y++)
            for (int i = a; i <= b; i++) {
                set(i, y, a, m);
                set(i, y, b, m);
                set(a, y, i, m);
                set(b, y, i, m);
            }
    }

    Location at(double x, double y, double z) {
        return new Location(world, ox + x, oy + y, oz + z);
    }

    // The whole chunk column the arena may use (for clean-up and "is this in the arena").
    BoundingBox bounds() {
        return new BoundingBox(ox, oy - 2, oz, ox + 16, oy + height() + 2, oz + 16);
    }

    boolean contains(Location l) {
        return world != null && state != State.OFF && l.getWorld() == world && bounds().contains(l.toVector());
    }

    // Empties the chunk from just under the floor to above the roof: blocks, mobs, TNT, falling sand, items.
    private void clearArena() {
        world.getChunkAt(ox >> 4, oz >> 4).load(true);
        for (Entity e : new ArrayList<>(world.getNearbyEntities(bounds())))
            if (!(e instanceof Player)) e.remove();
        for (int y = -2; y <= height() + 1; y++)
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++) {
                    Block b = block(x, y, z);
                    if (b.getType() != Material.AIR) b.setType(Material.AIR, false);
                }
    }

    static String stripColors(String s) {
        return s.replaceAll("§.", "");
    }
}
