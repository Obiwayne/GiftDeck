using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GiftDeck.Services;

// Finds a game's folder: the folder the user chose (remembered per pack), then Steam, Epic Games and
// registry entries in the order pack.json lists them. Read-only: nothing here writes to the game or launchers.
public static class GameLocator
{
    public static (string folder, string foundBy) Locate(GamePack pack, string chosenFolder)
    {
        if (IsGameFolder(pack, chosenFolder)) return (Path.GetFullPath(chosenFolder), "you");
        foreach (var d in pack.Detect)
        {
            try
            {
                var (folder, by) = d.Type?.ToLowerInvariant() switch
                {
                    "steam" => (FromSteam(d.AppId), "Steam"),
                    "epic" => (FromEpic(d.AppName), "Epic Games"),
                    "registry" => (FromRegistry(d.Key, d.Value), RegistryLabel(d.Key)),
                    _ => (null, null),
                };
                if (IsGameFolder(pack, folder)) return (Path.GetFullPath(folder), by);
            }
            catch (Exception e) { Log.Write($"Looking for {pack.Name} ({d.Type}): {e.Message}"); }
        }
        return (null, null);
    }

    // A folder counts as the game when the pack's exe is in it (e.g. GTA5.exe; the Enhanced edition's exe is different).
    public static bool IsGameFolder(GamePack pack, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try { return Directory.Exists(folder) && (string.IsNullOrEmpty(pack.Exe) || File.Exists(Path.Combine(folder, pack.Exe))); }
        catch { return false; }
    }

    // ---- Steam: libraryfolders.vdf lists every library; appmanifest_<appId>.acf says which folder under steamapps\common ----

    public static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        void Add(string p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(Path.GetFullPath(p.Replace('/', '\\'))); }
        Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
        var libs = new List<string>(roots);
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase))
                libs.Add(VdfUnescape(m.Groups[1].Value));
        }
        return libs.Where(Directory.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static string FromSteam(int appId)
    {
        if (appId <= 0) return null;
        foreach (var lib in SteamLibraries())
        {
            var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(acf)) continue;
            var m = Regex.Match(File.ReadAllText(acf), "\"installdir\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var folder = Path.Combine(lib, "steamapps", "common", VdfUnescape(m.Groups[1].Value));
            if (Directory.Exists(folder)) return folder;
        }
        return null;
    }

    static string VdfUnescape(string s) => s.Replace("\\\\", "\\").Replace("\\\"", "\"");

    // ---- Epic Games: one JSON .item file per installed game ----

    public static string FromEpic(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) return null;
        foreach (var f in Directory.GetFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var root = doc.RootElement;
                string Get(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (!string.Equals(Get("AppName"), appName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Get("MainGameAppName"), appName, StringComparison.OrdinalIgnoreCase)) continue;
                var folder = Get("InstallLocation");
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;
            }
            catch { }
        }
        return null;
    }

    // ---- A registry value holding the folder, e.g. the Rockstar Games Launcher's InstallFolder ----

    public static string FromRegistry(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var i = key.IndexOf('\\');
        if (i < 0) return null;
        var hive = key[..i].ToUpperInvariant() switch
        {
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            _ => (RegistryHive?)null,
        };
        if (hive == null) return null;
        using var baseKey = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
        using var sub = baseKey.OpenSubKey(key[(i + 1)..]);
        var text = sub?.GetValue(value ?? "") as string;
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().Trim('"');
        // Some launchers store the exe instead of its folder.
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) text = Path.GetDirectoryName(text);
        return text;
    }

    static string RegistryLabel(string key) => key != null && key.Contains("Rockstar", StringComparison.OrdinalIgnoreCase)
        ? "the Rockstar Games Launcher" : "its installer";
}
