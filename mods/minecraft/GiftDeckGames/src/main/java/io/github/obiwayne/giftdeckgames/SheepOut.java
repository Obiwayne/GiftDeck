package io.github.obiwayne.giftdeckgames;

import org.bukkit.DyeColor;
import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.boss.BarColor;
import org.bukkit.entity.Entity;
import org.bukkit.entity.Player;
import org.bukkit.entity.Sheep;
import org.bukkit.inventory.ItemStack;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;

// Sheep Out: viewers fill a fenced meadow with coloured sheep named after them; the streamer has a sword and
// has to keep the flock down. Win: the timer runs out with fewer sheep than the limit. Lose: the meadow holds
// the limit (40 by default) at once, or the streamer dies.
//
// Layout (x/z inside the chunk): glass walls at 1 and 15 (barriers above them), meadow 2..14 (13x13).
// y: -1 bedrock, 0 grass, walls 1..4, invisible barrier 5..7.
final class SheepOut extends Game {
    static final List<String> COLORS;
    static {
        List<String> c = new ArrayList<>();
        c.add("random");
        c.add("rainbow");
        for (DyeColor d : DyeColor.values()) c.add(d.name().toLowerCase(Locale.ROOT));
        COLORS = c;
    }
    static final List<String> TIERS = Arrays.asList("wooden", "stone", "iron", "diamond", "netherite");
    static final String TAG = "giftdeckgames_sheep";

    private static final int A = 2, B = 14;
    private int cleared;
    private int maxSheep;

    SheepOut(GiftDeckGames plugin) {
        super(plugin, "sheepout", "Sheep Out", 2);
    }

    @Override int height() { return 9; }
    @Override int timerSeconds() { return Math.max(30, Math.min(3600, plugin.getConfig().getInt("sheepout.seconds", 180))); }
    @Override BarColor barColor() { return BarColor.WHITE; }
    @Override String goal() { return "Keep the sheep under " + maxSheep() + " for " + clock(timerSeconds()) + "!"; }

    private int maxSheep() {
        return Math.max(5, Math.min(200, plugin.getConfig().getInt("sheepout.max-sheep", 40)));
    }

    @Override
    void build() {
        cleared = 0;
        maxSheep = maxSheep();
        fill(A - 1, -1, A - 1, B + 1, -1, B + 1, Material.BEDROCK);
        fill(A - 1, 0, A - 1, B + 1, 0, B + 1, Material.GRASS_BLOCK);
        walls(A - 1, B + 1, 1, 4, Material.GLASS);
        walls(A - 1, B + 1, 5, 7, Material.BARRIER);
    }

    @Override
    Location spawn() {
        return at(8.5, 1, 8.5);
    }

    @Override
    void kit(Player p) {
        p.getInventory().addItem(Compat.item("stone_sword", 1, null), Compat.item("bread", 8, null));
    }

    int sheepCount() {
        if (world == null) return 0;
        int n = 0;
        for (Entity e : world.getNearbyEntities(bounds()))
            if (e instanceof Sheep && e.getScoreboardTags().contains(TAG) && !e.isDead()) n++;
        return n;
    }

    void sheepKilled() {
        cleared++;
    }

    @Override String barText() { return sheepCount() + "/" + maxSheep + " sheep, " + cleared + " cleared"; }
    @Override double barProgress() { return sheepCount() / (double) Math.max(1, maxSheep); }

    @Override
    void check() {
        int n = sheepCount();
        if (n >= maxSheep) end(false, "The meadow is full: " + n + " sheep!");
    }

    @Override
    void onTimeUp() {
        end(true, "Time's up! " + streamerLabel() + " kept the flock down (" + cleared + " cleared).");
    }

    @Override
    List<String> usage() {
        return Arrays.asList(
                "sheep <count 1-30> <color|random|rainbow> [viewer]  sheep named after the viewer",
                "sword <wooden|stone|iron|diamond|netherite> [viewer]  a better sword (help)",
                "smite <count 1-30> [viewer]  lightning takes out sheep (help)",
                "heal [viewer]  full health and food (help)");
    }

    static List<String> actions() {
        return Arrays.asList("sheep", "sword", "smite", "heal");
    }

    @Override
    String effect(String action, Args a) throws GameException {
        Player p = player();
        switch (action) {
            case "sheep": {
                int n = a.count(1, 1, 30);
                String color = a.choice("random", COLORS);
                String v = a.viewer();
                for (int i = 0; i < n; i++) spawnSheep(color, v);
                String what = color.equals("random") ? "" : color.replace('_', ' ') + " ";
                return credit(v, n, "sent " + n + " " + what + "sheep!");
            }
            case "sword": {
                String tier = a.choice("diamond", TIERS);
                String v = a.viewer();
                if (p != null) {
                    ItemStack s = Compat.enchant(Compat.item(tier + "_sword", 1, "§b" + v + "'s sword"), "sweeping_edge", 3);
                    p.getInventory().addItem(s);
                    Compat.sound(p, "minecraft:entity.item.pickup", 1f);
                }
                return credit(v, 2, "gave a " + tier + " sword");
            }
            case "smite": {
                int n = a.count(5, 1, 30);
                String v = a.viewer();
                int hit = 0;
                for (Entity e : world.getNearbyEntities(bounds())) {
                    if (hit >= n) break;
                    if (e instanceof Sheep && e.getScoreboardTags().contains(TAG) && !e.isDead()) {
                        world.strikeLightningEffect(e.getLocation());
                        e.remove();
                        hit++;
                    }
                }
                cleared += hit;
                return credit(v, n, "struck " + hit + " sheep with lightning");
            }
            case "heal": {
                String v = a.viewer();
                if (p != null) Compat.heal(p);
                return credit(v, 1, "healed the streamer");
            }
            default:
                throw new GameException("Could not do \"" + action + "\": Sheep Out has " + String.join(", ", actions()));
        }
    }

    private void spawnSheep(String color, String viewer) {
        int x = A + 1 + random.nextInt(B - A - 1), z = A + 1 + random.nextInt(B - A - 1);
        Sheep s = world.spawn(at(x + 0.5, 1, z + 0.5), Sheep.class);
        s.addScoreboardTag(TAG);
        s.addScoreboardTag("giftdeckgames");
        s.setRemoveWhenFarAway(false);
        s.setCustomNameVisible(true);
        if (color.equals("rainbow")) {
            s.setCustomName("jeb_"); // Minecraft's own rainbow sheep
        } else {
            DyeColor[] all = DyeColor.values();
            s.setColor(color.equals("random") ? all[random.nextInt(all.length)] : DyeColor.valueOf(color.toUpperCase(Locale.ROOT)));
            s.setCustomName(viewer);
        }
    }
}
