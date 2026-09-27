package io.github.obiwayne.giftdeckgames;

import org.bukkit.Bukkit;
import org.bukkit.Material;
import org.bukkit.NamespacedKey;
import org.bukkit.Registry;
import org.bukkit.enchantments.Enchantment;
import org.bukkit.entity.EntityType;
import org.bukkit.entity.Player;
import org.bukkit.inventory.ItemStack;
import org.bukkit.inventory.meta.ItemMeta;
import org.bukkit.potion.PotionEffect;

// Calls that changed names between Paper 1.20.1 and 26.x (effect and enchantment constants were renamed,
// max health moved to attributes, item names became components). Everything goes through names that exist
// in every version, or through a vanilla command, and never lets a missing method stop a game.
final class Compat {
    private Compat() { }

    static void setHealth(Player p, double health) {
        double max = 20;
        try { max = p.getMaxHealth(); } catch (Throwable ignored) { }
        try { p.setHealth(Math.max(1, Math.min(max, health))); } catch (Throwable ignored) { }
    }

    static void heal(Player p) {
        setHealth(p, 1000);
        p.setFoodLevel(20);
        p.setSaturation(10);
        p.setFireTicks(0);
    }

    static void clearEffects(Player p) {
        for (PotionEffect e : p.getActivePotionEffects()) p.removePotionEffect(e.getType());
    }

    // effect: the vanilla id (haste, night_vision, ...). A command, because the Java constants were renamed.
    static void effect(Player p, String effect, int seconds, int amplifier) {
        Bukkit.dispatchCommand(Bukkit.getConsoleSender(),
                "effect give " + p.getName() + " minecraft:" + effect + " " + seconds + " " + amplifier + " true");
    }

    static ItemStack item(String material, int amount, String name) {
        Material m = Material.matchMaterial(material);
        if (m == null) m = Material.STONE;
        ItemStack s = new ItemStack(m, Math.max(1, amount));
        if (name != null) {
            try {
                ItemMeta meta = s.getItemMeta();
                if (meta != null) {
                    meta.setDisplayName(name);
                    s.setItemMeta(meta);
                }
            } catch (Throwable ignored) { }
        }
        return s;
    }

    static ItemStack enchant(ItemStack s, String enchantment, int level) {
        try {
            Enchantment e = Registry.ENCHANTMENT.get(NamespacedKey.minecraft(enchantment));
            if (e != null) s.addUnsafeEnchantment(e, level);
        } catch (Throwable ignored) { }
        return s;
    }

    static EntityType entityType(String id) {
        try {
            return Registry.ENTITY_TYPE.get(NamespacedKey.minecraft(id));
        } catch (Throwable t) {
            return null;
        }
    }

    static void sound(Player p, String sound, float pitch) {
        if (p == null) return;
        try { p.playSound(p.getLocation(), sound, 1f, pitch); } catch (Throwable ignored) { }
    }

    static void title(Player p, String title, String subtitle) {
        if (p == null) return;
        try { p.sendTitle(title, subtitle, 5, 50, 15); } catch (Throwable ignored) { }
    }
}
