package io.github.obiwayne.giftdeckgames;

import org.bukkit.Location;
import org.bukkit.Material;
import org.bukkit.boss.BarColor;
import org.bukkit.entity.Entity;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.LivingEntity;
import org.bukkit.entity.Player;
import org.bukkit.entity.TNTPrimed;
import org.bukkit.inventory.ItemStack;

import java.util.Arrays;
import java.util.List;

// Bedrock Box: the streamer stands on top of a 5x5 shaft of stone inside bedrock walls and digs to the emerald
// floor at the bottom. Viewers drop TNT, sand, anvils and mobs into the box, or help with pickaxes, haste,
// healing and a drill. Win: reach the bottom. Lose: die.
//
// Layout (x/z inside the chunk): walls at 4 and 10, shaft 5..9. y: 0 bedrock floor, 1 emerald (the goal),
// 2..depth+1 the layers to dig, depth+2 where the streamer starts, walls up to depth+7, barrier roof at depth+8.
final class BedrockBox extends Game {
    static final List<String> MOBS = Arrays.asList("zombie", "husk", "skeleton", "stray", "spider", "cave_spider", "creeper",
            "silverfish", "endermite", "slime", "magma_cube", "witch", "pillager", "vindicator", "bee", "chicken", "pig", "sheep",
            "cow", "rabbit", "fox", "frog", "goat", "axolotl", "wolf", "cat");
    static final List<String> FALLING = Arrays.asList("sand", "red_sand", "gravel");
    static final List<String> TIERS = Arrays.asList("wooden", "stone", "iron", "diamond", "netherite");

    private static final int A = 5, B = 9; // the shaft
    private int depth;

    BedrockBox(GiftDeckGames plugin) {
        super(plugin, "bedrockbox", "Bedrock Box", 0);
    }

    private int top() { return depth + 2; }      // the streamer's feet at the start
    private int dropY() { return depth + 7; }    // where things are dropped in

    @Override int height() { return Math.max(depth, depth()) + 9; }

    private int depth() {
        return Math.max(5, Math.min(60, plugin.getConfig().getInt("bedrockbox.depth", 30)));
    }

    @Override int timerSeconds() { return 0; }
    @Override BarColor barColor() { return BarColor.PURPLE; }
    @Override String goal() { return "Dig down to the emerald floor!"; }

    @Override
    void build() {
        depth = depth();
        fill(A - 1, 0, A - 1, B + 1, 0, B + 1, Material.BEDROCK);
        walls(A - 1, B + 1, 1, depth + 7, Material.BEDROCK);
        fill(A - 1, depth + 8, A - 1, B + 1, depth + 8, B + 1, Material.BARRIER);
        fill(A, 1, A, B, 1, B, Material.EMERALD_BLOCK);
        boolean obsidian = plugin.getConfig().getBoolean("bedrockbox.obsidian-layer", true) && depth >= 8;
        for (int y = 2; y <= depth + 1; y++) {
            int fromTop = depth + 2 - y; // 1 = the top layer
            for (int x = A; x <= B; x++)
                for (int z = A; z <= B; z++)
                    set(x, y, z, layerBlock(fromTop, y, obsidian));
        }
    }

    private Material layerBlock(int fromTop, int y, boolean obsidian) {
        if (obsidian && y == 4) return Material.OBSIDIAN;
        int r = random.nextInt(100);
        if (fromTop <= 3) return r < 70 ? Material.DIRT : r < 85 ? Material.GRAVEL : Material.COARSE_DIRT;
        boolean deep = fromTop > depth / 2;
        if (r < 3) return deep ? Material.DEEPSLATE_DIAMOND_ORE : Material.IRON_ORE;
        if (r < 8) return deep ? Material.DEEPSLATE_IRON_ORE : Material.COAL_ORE;
        if (r < 15) return deep ? Material.TUFF : Material.ANDESITE;
        if (r < 22) return Material.COBBLESTONE;
        return deep ? Material.DEEPSLATE : Material.STONE;
    }

    @Override
    Location spawn() {
        return at(7.5, top(), 7.5);
    }

    @Override
    void kit(Player p) {
        p.getInventory().addItem(Compat.item("stone_pickaxe", 1, null), Compat.item("stone_shovel", 1, null),
                Compat.item("bread", 8, null), Compat.item("torch", 16, null));
        Compat.effect(p, "night_vision", 99999, 0);
    }

    // ---------------- progress ----------------

    private int feetY() {
        Player p = player();
        if (p == null || !contains(p.getLocation())) return oy + top();
        return p.getLocation().getBlockY();
    }

    private int dug() {
        return Math.max(0, Math.min(depth, oy + top() - feetY()));
    }

    @Override String barText() { return "depth " + dug() + "/" + depth; }
    @Override double barProgress() { return dug() / (double) depth; }

    @Override
    void check() {
        Player p = player();
        if (p != null && !p.isDead() && contains(p.getLocation()) && p.getLocation().getBlockY() <= oy + 2)
            end(true, p.getName() + " dug to the bottom and escaped!");
    }

    // ---------------- gifts ----------------

    @Override
    List<String> usage() {
        return Arrays.asList(
                "tnt <count 1-20> [viewer]  lit TNT dropped into the box",
                "sand <count 1-64> <sand|red_sand|gravel> [viewer]  refills the shaft",
                "anvil <count 1-10> [viewer]  anvils falling on the streamer",
                "mob <type> <count 1-10> [viewer]  mobs next to the streamer (" + String.join(", ", MOBS) + ")",
                "pickaxe <wooden|stone|iron|diamond|netherite> [viewer]  a better pickaxe (help)",
                "haste <seconds 5-120> [viewer]  dig faster (help)",
                "drill <layers 1-10> [viewer]  digs straight down under the streamer (help)",
                "heal [viewer]  full health and food (help)");
    }

    @Override
    String effect(String action, Args a) throws GameException {
        Player p = player();
        switch (action) {
            case "tnt": {
                int n = a.count(1, 1, 20);
                String v = a.viewer();
                for (int i = 0; i < n; i++) {
                    TNTPrimed t = world.spawn(dropSpot(i), TNTPrimed.class);
                    t.setFuseTicks(80 + i * 4);
                    tag(t, v);
                }
                return credit(v, n * 3, "dropped " + n + " TNT into the box!");
            }
            case "sand": {
                int n = a.count(8, 1, 64);
                String block = a.choice("sand", FALLING);
                String v = a.viewer();
                int placed = pour(Material.matchMaterial(block), n);
                return credit(v, n, "poured " + placed + " " + block.replace('_', ' ') + " into the shaft");
            }
            case "anvil": {
                int n = a.count(1, 1, 10);
                String v = a.viewer();
                int placed = pour(Material.ANVIL, n);
                return credit(v, n * 3, "dropped " + placed + (placed == 1 ? " anvil!" : " anvils!"));
            }
            case "mob": {
                String type = a.choice("zombie", MOBS);
                int n = a.count(1, 1, 10);
                String v = a.viewer();
                EntityType et = Compat.entityType(type);
                if (et == null) throw new GameException("Could not summon " + type + " on this Minecraft version.");
                for (int i = 0; i < n; i++) {
                    Entity e = world.spawnEntity(mobSpot(), et);
                    tag(e, v);
                    if (e instanceof LivingEntity) {
                        LivingEntity le = (LivingEntity) e;
                        le.setRemoveWhenFarAway(false);
                        // Sky arenas are sunny: a helmet keeps zombies and skeletons from burning at once.
                        if (le.getEquipment() != null) le.getEquipment().setHelmet(new ItemStack(Material.LEATHER_HELMET));
                    }
                }
                return credit(v, n * 2, "sent " + n + " " + type.replace('_', ' ') + (n == 1 ? "" : "s") + "!");
            }
            case "pickaxe": {
                String tier = a.choice("diamond", TIERS);
                String v = a.viewer();
                if (p != null) {
                    ItemStack pick = Compat.enchant(Compat.item(tier + "_pickaxe", 1, "§b" + v + "'s pickaxe"), "efficiency", 3);
                    p.getInventory().addItem(pick);
                    Compat.sound(p, "minecraft:entity.item.pickup", 1f);
                }
                return credit(v, 2, "gave a " + tier + " pickaxe");
            }
            case "haste": {
                int s = a.count(30, 5, 120);
                String v = a.viewer();
                if (p != null) Compat.effect(p, "haste", s, 1);
                return credit(v, 1, "gave " + s + " s of haste");
            }
            case "drill": {
                int n = a.count(3, 1, 10);
                String v = a.viewer();
                int removed = drill(n);
                return credit(v, n, "drilled " + removed + " block" + (removed == 1 ? "" : "s") + " down");
            }
            case "heal": {
                String v = a.viewer();
                if (p != null) Compat.heal(p);
                return credit(v, 1, "healed the streamer");
            }
            default:
                throw new GameException("Could not do \"" + action + "\": Bedrock Box has " + String.join(", ", actions()));
        }
    }

    static List<String> actions() {
        return Arrays.asList("tnt", "sand", "anvil", "mob", "pickaxe", "haste", "drill", "heal");
    }

    // Right above the streamer (or the middle of the shaft), just under the roof.
    private Location dropSpot(int i) {
        int[] c = column();
        int x = clampShaft(c[0] + (i == 0 ? 0 : random.nextInt(3) - 1));
        int z = clampShaft(c[1] + (i == 0 ? 0 : random.nextInt(3) - 1));
        return at(x + 0.5, dropY(), z + 0.5);
    }

    // Mobs land next to the streamer; with nobody in the box, on top of the dirt.
    private Location mobSpot() {
        Player p = player();
        if (p != null && contains(p.getLocation())) {
            Location l = p.getLocation();
            return new Location(world, clampShaft(l.getBlockX() - ox + random.nextInt(3) - 1) + ox + 0.5, l.getBlockY() + 0.1,
                    clampShaft(l.getBlockZ() - oz + random.nextInt(3) - 1) + oz + 0.5);
        }
        int x = A + random.nextInt(B - A + 1), z = A + random.nextInt(B - A + 1);
        return at(x + 0.5, surface(x, z), z + 0.5);
    }

    // The shaft column the streamer is in (x, z relative to the arena), or the middle.
    private int[] column() {
        Player p = player();
        if (p != null && contains(p.getLocation()))
            return new int[]{clampShaft(p.getLocation().getBlockX() - ox), clampShaft(p.getLocation().getBlockZ() - oz)};
        return new int[]{7, 7};
    }

    private static int clampShaft(int v) {
        return Math.max(A, Math.min(B, v));
    }

    // The highest air block under the roof in a column (0 = none): a falling block placed there drops onto the pile.
    private int topAir(int x, int z) {
        for (int y = dropY(); y >= 2; y--)
            if (block(x, y, z).getType() == Material.AIR) return y;
        return 0;
    }

    // First air above the highest block in a column.
    private int surface(int x, int z) {
        for (int y = dropY(); y >= 1; y--)
            if (block(x, y, z).getType() != Material.AIR) return y + 1;
        return 1;
    }

    // Places falling blocks in the air under the roof, over the streamer's column and around it; they fall in.
    private int pour(Material m, int n) {
        int[] c = column();
        int placed = 0;
        for (int i = 0; i < n; i++) {
            // Gifts can arrive faster than blocks fall: when a column is still full of the last pour, try another.
            for (int attempt = 0; attempt < 6; attempt++) {
                int x = c[0], z = c[1];
                if (i % 3 != 0 || attempt > 0) { x = clampShaft(x + random.nextInt(3) - 1); z = clampShaft(z + random.nextInt(3) - 1); }
                int y = topAir(x, z);
                if (y > 0) {
                    block(x, y, z).setType(m, true); // with physics, so it starts falling
                    placed++;
                    break;
                }
            }
        }
        return placed;
    }

    // Removes the blocks right under the streamer's feet (or the middle column), down to the emerald floor.
    private int drill(int n) {
        int[] c = column();
        Player p = player();
        // Under the streamer's feet, or from the top of the stone when nobody is in the box.
        int y = p != null && contains(p.getLocation()) ? p.getLocation().getBlockY() - oy - 1 : depth + 1;
        int removed = 0;
        for (; y >= 2 && removed < n; y--) {
            if (block(c[0], y, c[1]).getType() != Material.AIR) {
                block(c[0], y, c[1]).setType(Material.AIR, false);
                removed++;
            }
        }
        return removed;
    }

    private void tag(Entity e, String viewer) {
        e.addScoreboardTag("giftdeckgames");
        e.setCustomName(viewer);
        e.setCustomNameVisible(true);
    }
}
