using System.Diagnostics;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// What the Games page knows about a game's tier (pack.json "tier") and a few small helpers it shares
// with the game tiles and the console panel.
static class GameCatalog
{
    public const string Ready = "ready", Console = "console", Keys = "keys";

    public static readonly string[] Tiers = { Ready, Console, Keys };

    // Unknown or empty tiers count as "ready" (the packs that existed before the catalog).
    public static string TierOf(GamePack p)
    {
        var t = (p?.Tier ?? "").Trim().ToLowerInvariant();
        return t == Console || t == Keys ? t : Ready;
    }

    public static int TierRank(string tier) => tier == Ready ? 0 : tier == Console ? 1 : 2;

    public static string TierLabel(string tier) => tier switch
    {
        Console => "Server console",
        Keys => "Key presses",
        _ => "Ready to go",
    };

    public static string TierExplain(string tier) => tier switch
    {
        Console => "GiftDeck sends commands to the game's own server console. Set up the server once, then gifts can run its commands.",
        Keys => "Gifts press the game's own keys. Nothing to install: pick a key idea below and choose which gift sets it off.",
        _ => "GiftDeck installs everything this game needs and has ready-made events for it.",
    };

    // Segoe Fluent Icons: check mark, command prompt, keyboard.
    public static string TierGlyph(string tier) => tier switch
    {
        Console => "",
        Keys => "",
        _ => "",
    };

    public static Color TierColor(string tier) => tier switch
    {
        Console => Color.FromRgb(0x38, 0xBD, 0xF8), // sky
        Keys => Color.FromRgb(0xF5, 0x9E, 0x0B),    // amber
        _ => Color.FromRgb(0x7C, 0x5C, 0xFF),       // the app's accent
    };

    public static SolidColorBrush TierBrush(string tier)
    {
        var b = new SolidColorBrush(TierColor(tier));
        b.Freeze();
        return b;
    }

    // Search: every word must appear in the name, genre, short line or tier name.
    public static bool Matches(GamePack p, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var hay = (p.Name + " " + p.Genre + " " + p.Short + " " + p.Id + " " + TierLabel(TierOf(p))).ToLowerInvariant();
        return query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).All(hay.Contains);
    }

    // Where to get the game: Steam (the app when it's installed, else the web store), else the pack's store link.
    public static string StoreLink(GamePack p)
    {
        if (p.SteamAppId > 0) return SteamInstalled() ? $"steam://store/{p.SteamAppId}" : $"https://store.steampowered.com/app/{p.SteamAppId}";
        return string.IsNullOrWhiteSpace(p.Store) ? null : p.Store.Trim();
    }

    public static string StoreName(GamePack p)
    {
        if (p.SteamAppId > 0) return "Steam";
        return Uri.TryCreate(p.Store ?? "", UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "its store";
    }

    static bool? _steam;
    static bool SteamInstalled()
    {
        if (_steam != null) return _steam.Value;
        try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("steam"); _steam = k != null; }
        catch { _steam = false; }
        return _steam.Value;
    }

    public static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write($"Couldn't open {url}: {e.Message}"); }
    }

    // "Grand Theft Auto V" -> "GT", "7 Days to Die" -> "7D", "Minecraft" -> "MI".
    public static string Initials(string name)
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "the", "a", "an", "of", "to", "and", "&", "-", ":" };
        var words = (name ?? "").Split(new[] { ' ', ':', '-', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => !skip.Contains(w)).ToList();
        if (words.Count == 0) return "?";
        if (words.Count == 1) return words[0].Length == 1 ? words[0].ToUpperInvariant() : words[0][..2].ToUpperInvariant();
        return (words[0][..1] + words[1][..1]).ToUpperInvariant();
    }

    // A stable hue per game (string.GetHashCode changes every run): FNV-1a.
    public static double Hue(string id)
    {
        uint h = 2166136261;
        foreach (var c in (id ?? "").ToLowerInvariant()) { h ^= c; h *= 16777619; }
        return h % 360;
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Ch(double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)Math.Round(Ch(h + 1 / 3.0) * 255), (byte)Math.Round(Ch(h) * 255), (byte)Math.Round(Ch(h - 1 / 3.0) * 255));
    }
}
