package io.github.obiwayne.giftdeckgames;

import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.boss.BarColor;
import org.bukkit.entity.Player;
import org.bukkit.inventory.ItemStack;

import java.util.Arrays;
import java.util.List;

// Sand Pour: the streamer stands in a glass pit and viewers pour sand, gravel, concrete powder and anvils on
// their head. Bigger gifts pour more, over a wider area. The streamer digs out to keep breathing.
// Win: still alive when the timer runs out. Lose: buried (or crushed).
//
// Layout (x/z inside the chunk): glass walls at 2 and 12, pit 3..11 (9x9). y: 0 bedrock floor, glass walls
// 1..28, barrier roof at 29; things are poured in at 27.
final class SandPour extends Game {
    static final List<String> BLOCKS = Arrays.asList("sand", "red_sand", "gravel", "concrete", "anvil");
    static final List<String> TIERS = Arrays.asList("wooden", "stone", "iron", "diamond", "netherite");
    private static final String[] COLORS = {"white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "cyan", "purple", "blue", "red", "green"};

    private static final int A = 3, B = 11, TOP = 29, DROP = 27;

    SandPour(GiftDeckGames plugin) {
        super(plugin, "sandpour", "Sand Pour", 1);
    }

    @Override int height() { return TOP + 1; }
    @Override int timerSeconds() { return Math.max(30, Math.min(3600, plugin.getConfig().getInt("sandpour.seconds", 180))); }
    @Override BarColor barColor() { return BarColor.YELLOW; }
    @Override String goal() { return "Survive " + clock(timerSeconds()) + " of sand!"; }

    @Override
    void build() {
        fill(A - 1, 0, A - 1, B + 1, 0, B + 1, Material.BEDROCK);
        walls(A - 1, B + 1, 1, TOP - 1, Material.GLASS);
        fill(A - 1, TOP, A - 1, B + 1, TOP, B + 1, Material.BARRIER);
    }

    @Override
    Location spawn() {
        return at(7.5, 1, 7.5);
    }

    @Override
    void kit(Player p) {
        p.getInventory().addItem(Compat.item("iron_shovel", 1, null), Compat.item("bread", 8, null));
    }

    // How full the pit is: the highest block over the middle 3x3, as a share of the height.
    private int pileHeight() {
        int best = 0;
        for (int x = 6; x <= 8; x++)
            for (int z = 6; z <= 8; z++)
                for (int y = DROP; y >= 1; y--)
                    if (block(x, y, z).getType() != Material.AIR) { best = Math.max(best, y); break; }
        return best;
    }

    @Override String barText() { return "pile " + pileHeight() + " high"; }
    @Override double barProgress() { return timeFraction(); }

    @Override
    void check() {
        // Death is the only way to lose (the plugin's death listener ends the game).
    }

    @Override
    List<String> usage() {
        return Arrays.asList(
                "pour <count 1-200> <sand|red_sand|gravel|concrete|anvil> [viewer]  pours onto the streamer (anvils: up to 12)",
                "shovel <wooden|stone|iron|diamond|netherite> [viewer]  a better shovel (help)",
                "dig [viewer]  clears the sand above the streamer's head (help)",
                "haste <seconds 5-120> [viewer]  dig faster (help)",
                "heal [viewer]  full health and food (help)");
    }

    static List<String> actions() {
        return Arrays.asList("pour", "shovel", "dig", "haste", "heal");
    }

    @Override
    String effect(String action, Args a) throws GameException {
        Player p = player();
        switch (action) {
            case "pour": {
                int n = a.count(10, 1, 200);
                String what = a.choice("sand", BLOCKS);
                String v = a.viewer();
                if (what.equals("anvil")) n = Math.min(n, 12);
                int placed = pour(what, n);
                String name = what.equals("concrete") ? "concrete powder" : what.replace('_', ' ') + (what.equals("anvil") && placed != 1 ? "s" : "");
                return credit(v, what.equals("anvil") ? n * 5 : n, "poured " + placed + " " + name + "!");
            }
            case "shovel": {
                String tier = a.choice("diamond", TIERS);
                String v = a.viewer();
                if (p != null) {
                    ItemStack s = Compat.enchant(Compat.item(tier + "_shovel", 1, "§b" + v + "'s shovel"), "efficiency", 3);
                    p.getInventory().addItem(s);
                    Compat.sound(p, "minecraft:entity.item.pickup", 1f);
                }
                return credit(v, 2, "gave a " + tier + " shovel");
            }
            case "dig": {
                String v = a.viewer();
                int n = dig();
                return credit(v, 2, "dug out " + n + " block" + (n == 1 ? "" : "s") + " above the streamer");
            }
            case "haste": {
                int s = a.count(30, 5, 120);
                String v = a.viewer();
                if (p != null) Compat.effect(p, "haste", s, 1);
                return credit(v, 1, "gave " + s + " s of haste");
            }
            case "heal": {
                String v = a.viewer();
                if (p != null) Compat.heal(p);
                return credit(v, 1, "healed the streamer");
            }
            default:
                throw new GameException("Could not do \"" + action + "\": Sand Pour has " + String.join(", ", actions()));
        }
    }

    // The column the streamer is in, or the middle of the pit.
    private int[] center() {
        Player p = player();
        if (p != null && contains(p.getLocation()))
            return new int[]{clamp(p.getLocation().getBlockX() - ox), clamp(p.getLocation().getBlockZ() - oz)};
        return new int[]{7, 7};
    }

    private static int clamp(int v) {
        return Math.max(A, Math.min(B, v));
    }

    // Bigger pours cover a wider square around the streamer: 1 block straight down, up to 9 a 3x3, up to 25 a
    // 5x5, more a 7x7. Blocks go in the air under the roof and fall.
    private int pour(String what, int n) {
        int r = n <= 1 ? 0 : n <= 9 ? 1 : n <= 25 ? 2 : 3;
        int[] c = center();
        int placed = 0;
        for (int i = 0; i < n; i++) {
            Material m = material(what);
            // A column still full of the last pour (gifts come faster than sand falls): try another spot.
            for (int attempt = 0; attempt < 6; attempt++) {
                int rr = attempt == 0 ? r : Math.max(r, 1);
                int x = clamp(c[0] + (rr == 0 ? 0 : random.nextInt(2 * rr + 1) - rr));
                int z = clamp(c[1] + (rr == 0 ? 0 : random.nextInt(2 * rr + 1) - rr));
                int y = DROP;
                while (y >= 1 && block(x, y, z).getType() != Material.AIR) y--;
                if (y >= 1) {
                    block(x, y, z).setType(m, true);
                    placed++;
                    break;
                }
            }
        }
        return placed;
    }

    private Material material(String what) {
        switch (what) {
            case "concrete": {
                Material m = Material.matchMaterial(COLORS[random.nextInt(COLORS.length)] + "_concrete_powder");
                return m != null ? m : Material.SAND;
            }
            case "anvil": return Material.ANVIL;
            case "gravel": return Material.GRAVEL;
            case "red_sand": return Material.RED_SAND;
            default: return Material.SAND;
        }
    }

    // Clears the 3x3 over the streamer from their feet up (sand, gravel, powder, anvils: never the glass).
    private int dig() {
        int[] c = center();
        Player p = player();
        int from = p != null && contains(p.getLocation()) ? p.getLocation().getBlockY() - oy : 1;
        int n = 0;
        for (int x = clamp(c[0] - 1); x <= clamp(c[0] + 1); x++)
            for (int z = clamp(c[1] - 1); z <= clamp(c[1] + 1); z++)
                for (int y = Math.max(1, from); y < TOP; y++) {
                    Material m = block(x, y, z).getType();
                    if (m != Material.AIR && m != Material.GLASS && m != Material.BARRIER) {
                        block(x, y, z).setType(Material.AIR, false);
                        n++;
                    }
                }
        return n;
    }
}
