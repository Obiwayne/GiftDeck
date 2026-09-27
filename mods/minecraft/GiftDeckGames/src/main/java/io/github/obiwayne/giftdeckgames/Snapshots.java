package io.github.obiwayne.giftdeckgames;

import org.bukkit.GameMode;
import org.bukkit.Location;
import org.bukkit.configuration.file.YamlConfiguration;
import org.bukkit.entity.Player;
import org.bukkit.inventory.ItemStack;

import java.io.File;
import java.util.List;
import java.util.UUID;
import java.util.logging.Level;

// What a player had before a mini-game (place, inventory, XP, game mode, health), so the game can hand them a
// kit and put everything back afterwards. Saved to plugins/GiftDeckGames/players/<uuid>.yml at once, so a
// crash or a server stop in the middle of a game doesn't lose the streamer's real inventory: whatever is still
// saved there is given back the next time they join.
final class Snapshots {
    private final GiftDeckGames plugin;
    private final File dir;

    Snapshots(GiftDeckGames plugin) {
        this.plugin = plugin;
        this.dir = new File(plugin.getDataFolder(), "players");
    }

    private File file(UUID id) {
        return new File(dir, id + ".yml");
    }

    boolean has(UUID id) {
        return file(id).isFile();
    }

    // Saves the player unless a save is already waiting (a second game must not overwrite the real inventory
    // with the first game's kit).
    void saveIfMissing(Player p) {
        if (has(p.getUniqueId())) return;
        YamlConfiguration y = new YamlConfiguration();
        y.set("name", p.getName());
        y.set("location", p.getLocation());
        y.set("contents", java.util.Arrays.asList(p.getInventory().getContents()));
        y.set("level", p.getLevel());
        y.set("exp", (double) p.getExp());
        y.set("gamemode", p.getGameMode().name());
        y.set("health", p.getHealth());
        y.set("food", p.getFoodLevel());
        try {
            dir.mkdirs();
            y.save(file(p.getUniqueId()));
        } catch (Exception e) {
            plugin.getLogger().log(Level.WARNING, "Couldn't save " + p.getName() + "'s inventory", e);
        }
    }

    // The saved place, or null (used as the respawn point after dying in a game).
    Location savedLocation(UUID id) {
        if (!has(id)) return null;
        try {
            Location l = YamlConfiguration.loadConfiguration(file(id)).getLocation("location");
            return l != null && l.getWorld() != null ? l : null;
        } catch (Throwable t) {
            return null;
        }
    }

    // Puts everything back and forgets the save. The player must be alive and online.
    @SuppressWarnings("unchecked")
    void restore(Player p) {
        File f = file(p.getUniqueId());
        if (!f.isFile() || p.isDead()) return;
        YamlConfiguration y = YamlConfiguration.loadConfiguration(f);
        Compat.clearEffects(p);
        p.setFireTicks(0);
        p.setFallDistance(0);
        Location l = null;
        try { l = y.getLocation("location"); } catch (Throwable ignored) { }
        if (l != null && l.getWorld() != null) p.teleport(l);
        else p.teleport(p.getWorld().getSpawnLocation());
        List<?> list = y.getList("contents");
        ItemStack[] items = new ItemStack[p.getInventory().getContents().length];
        if (list != null)
            for (int i = 0; i < list.size() && i < items.length; i++)
                if (list.get(i) instanceof ItemStack) items[i] = (ItemStack) list.get(i);
        p.getInventory().setContents(items);
        p.setLevel(y.getInt("level"));
        p.setExp((float) Math.max(0, Math.min(0.9999, y.getDouble("exp"))));
        try { p.setGameMode(GameMode.valueOf(y.getString("gamemode", "SURVIVAL"))); } catch (Exception ignored) { }
        Compat.setHealth(p, y.getDouble("health", 20));
        p.setFoodLevel(y.getInt("food", 20));
        if (!f.delete()) plugin.getLogger().warning("Couldn't delete " + f);
    }
}
