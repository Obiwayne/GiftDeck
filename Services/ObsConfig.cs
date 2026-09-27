using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace GiftDeck.Services;

// Where OBS lives and what it remembers, read (and minimally edited) straight from its files.
// No GiftDeck dependencies, so it can be tested on copies of OBS's folders.
public static class ObsConfig
{
    public const string DefaultExe = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";

    // OBS's settings folder: %APPDATA%\obs-studio (portable installs are not handled).
    public static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    // ---------- Finding obs64.exe ----------

    public static string FindExe()
    {
        foreach (var c in ExeCandidates())
            if (!string.IsNullOrWhiteSpace(c) && File.Exists(c)) return c;
        return null;
    }

    // Most reliable first: a running OBS knows exactly where it is; then the installer's registry keys; then the usual folders.
    public static IEnumerable<string> ExeCandidates()
    {
        foreach (var p in Process.GetProcessesByName("obs64"))
        {
            string path = null;
            try { path = p.MainModule?.FileName; } catch { } // an elevated OBS hides its path
            if (path != null) yield return path;
        }

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                // The OBS installer writes its folder as the default value of "SOFTWARE\OBS Studio".
                var dir = ReadRegistry(hive, view, @"SOFTWARE\OBS Studio", "");
                if (!string.IsNullOrWhiteSpace(dir)) yield return Path.Combine(dir.Trim('"'), "bin", "64bit", "obs64.exe");
                var appPath = ReadRegistry(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\obs64.exe", "");
                if (!string.IsNullOrWhiteSpace(appPath)) yield return appPath.Trim('"');
                var icon = ReadRegistry(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio", "DisplayIcon");
                if (!string.IsNullOrWhiteSpace(icon)) yield return icon.Split(',')[0].Trim('"');
                var loc = ReadRegistry(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio", "InstallLocation");
                if (!string.IsNullOrWhiteSpace(loc)) yield return Path.Combine(loc.Trim('"'), "bin", "64bit", "obs64.exe");
            }

        yield return DefaultExe;
        var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(x86, "Steam", "steamapps", "common", "OBS Studio", "bin", "64bit", "obs64.exe");
    }

    static string ReadRegistry(RegistryHive hive, RegistryView view, string key, string value)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var k = root.OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }

    // ---------- Launch arguments ----------

    static readonly Dictionary<string, bool> _optionCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // OBS drops and adds launch options between versions (--disable-shutdown-check is gone in OBS 32),
    // so only pass the optional ones this obs64.exe actually knows. It lists them as plain text inside the exe.
    public static bool SupportsOption(string exe, string option)
    {
        var cacheKey = exe + "|" + option;
        lock (_optionCache)
            if (_optionCache.TryGetValue(cacheKey, out var known)) return known;
        bool found;
        try { found = IndexOf(File.ReadAllBytes(exe), Encoding.ASCII.GetBytes(option)) >= 0; }
        catch { found = false; }
        lock (_optionCache) _optionCache[cacheKey] = found;
        return found;
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        var first = needle[0];
        for (int i = Array.IndexOf(hay, first); i >= 0 && i <= hay.Length - needle.Length; i = Array.IndexOf(hay, first, i + 1))
        {
            int j = 1;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    // Hidden in the tray, on the given collection and profile, with no dialogs that would wait for a click nobody can see.
    public static List<string> BuildArguments(string collection, string profile, Func<string, bool> supports)
    {
        var args = new List<string> { "--minimize-to-tray", "--collection", collection, "--profile", profile };
        foreach (var optional in new[] { "--disable-updater", "--disable-missing-files-check", "--disable-shutdown-check" })
            if (supports(optional)) args.Add(optional);
        return args;
    }

    public static ProcessStartInfo BuildStartInfo(string exe, string collection, string profile)
    {
        // OBS finds its data files relative to the working folder, so it must start in its bin folder.
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
        foreach (var a in BuildArguments(collection, profile, o => SupportsOption(exe, o))) psi.ArgumentList.Add(a);
        return psi;
    }

    // ---------- Settings files ----------

    public class Paths
    {
        public string GlobalIni;
        public string UserIni;      // holds [Basic] SceneCollection/Profile (global.ini before OBS 31)
        public string BasicDir;     // ...\obs-studio\basic, with scenes\ and profiles\
        public string ScenesDir;
        public string ProfilesDir;
        public string SentinelDir;  // OBS 32's "did it close cleanly" markers
    }

    // Honours the [Locations] folders OBS 31+ keeps in global.ini (they normally all point at %APPDATA%).
    public static Paths Locate(string appDir = null)
    {
        appDir ??= AppDir;
        var global = Path.Combine(appDir, "global.ini");
        var loc = File.Exists(global) ? ReadSection(global, "Locations") : new Dictionary<string, string>();
        string Under(string key) => loc.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? Path.Combine(v, "obs-studio") : appDir;
        var config = Under("Configuration");
        var user = Path.Combine(config, "user.ini");
        var scenes = Path.Combine(Under("SceneCollections"), "basic", "scenes");
        return new Paths
        {
            GlobalIni = global,
            UserIni = File.Exists(user) ? user : global,
            ScenesDir = scenes,
            ProfilesDir = Path.Combine(Under("Profiles"), "basic", "profiles"),
            BasicDir = Path.GetDirectoryName(scenes),
            SentinelDir = Path.Combine(config, ".sentinel"),
        };
    }

    public class LastUsed
    {
        public string Collection, CollectionFile, Profile, ProfileDir;
    }

    public static LastUsed ReadLastUsed(string iniPath)
    {
        var b = ReadSection(iniPath, "Basic");
        string Get(string k) => b.TryGetValue(k, out var v) ? v : "";
        return new LastUsed { Collection = Get("SceneCollection"), CollectionFile = Get("SceneCollectionFile"), Profile = Get("Profile"), ProfileDir = Get("ProfileDir") };
    }

    public static Dictionary<string, string> ReadSection(string iniPath, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inside = false;
        foreach (var raw in DecodeLines(File.ReadAllBytes(iniPath)))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("[")) { inside = line.Trim() == "[" + section + "]"; continue; }
            if (!inside) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = Unescape(line[(eq + 1)..]);
        }
        return result;
    }

    static string[] DecodeLines(byte[] bytes)
    {
        int start = HasBom(bytes) ? 3 : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start).Split('\n');
    }

    static bool HasBom(byte[] b) => b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    // OBS escapes backslashes and line breaks in values (its paths read C:\\Users\\...).
    public static string Unescape(string v)
    {
        if (v.IndexOf('\\') < 0) return v;
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length)
            {
                char n = v[i + 1];
                if (n == '\\') { sb.Append('\\'); i++; continue; }
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 'r') { sb.Append('\r'); i++; continue; }
            }
            sb.Append(v[i]);
        }
        return sb.ToString();
    }

    public static string Escape(string v) => v.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r");

    // Changes only the given keys of one section and keeps every other byte (BOM, line endings, order, comments).
    // Missing keys are added at the end of the section; a missing section is added at the end of the file.
    public static byte[] SetKeys(byte[] content, string section, IReadOnlyDictionary<string, string> values)
    {
        bool bom = HasBom(content);
        var text = Encoding.UTF8.GetString(content, bom ? 3 : 0, content.Length - (bom ? 3 : 0));
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split('\n').ToList(); // each keeps its own '\r', if it had one
        var pending = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);

        int sectionStart = -1, sectionEnd = -1; // sectionEnd: index after the last key line of the section
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("["))
            {
                if (sectionStart >= 0 && sectionEnd < 0) sectionEnd = LastContentLine(lines, sectionStart, i) + 1;
                if (line.Trim() == "[" + section + "]") sectionStart = i;
                continue;
            }
            if (sectionStart < 0 || sectionEnd >= 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!pending.TryGetValue(key, out var v)) continue;
            var lineCr = lines[i].EndsWith("\r") ? "\r" : "";
            lines[i] = line[..(eq + 1)] + Escape(v) + lineCr;
            pending.Remove(key);
        }
        if (sectionStart >= 0 && sectionEnd < 0) sectionEnd = LastContentLine(lines, sectionStart, lines.Count) + 1;

        // New keys keep the caller's order.
        var added = values.Keys.Where(pending.ContainsKey).Select(k => k + "=" + Escape(pending[k])).ToList();
        var cr = nl == "\r\n" ? "\r" : "";
        if (added.Count > 0 && sectionStart >= 0) lines.InsertRange(sectionEnd, added.Select(a => a + cr));

        var outText = string.Join("\n", lines);
        if (added.Count > 0 && sectionStart < 0)
        {
            if (outText.Length > 0 && !outText.EndsWith("\n")) outText += nl;
            outText += "[" + section + "]" + nl + string.Join("", added.Select(a => a + nl));
        }
        var body = Encoding.UTF8.GetBytes(outText);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body;
    }

    // The last non-blank line between a section header and the next header (so new keys go before any blank separator).
    static int LastContentLine(List<string> lines, int header, int next)
    {
        int last = header;
        for (int i = header + 1; i < next; i++) if (lines[i].Trim().Length > 0) last = i;
        return last;
    }

    // Edits the file in place, after copying the original to <file>.giftdeck.bak
    // (not <file>.bak: OBS uses that name for its own safe-save backup).
    public static void WriteKeys(string iniPath, string section, IReadOnlyDictionary<string, string> values)
    {
        var original = File.ReadAllBytes(iniPath);
        var updated = SetKeys(original, section, values);
        if (updated.AsSpan().SequenceEqual(original)) return;
        File.WriteAllBytes(iniPath + ".giftdeck.bak", original);
        var tmp = iniPath + ".giftdeck.tmp";
        File.WriteAllBytes(tmp, updated);
        File.Move(tmp, iniPath, true);
    }

    // ---------- Collections and profiles on disk ----------

    // The scene collection file whose "name" is the given one (file names don't always match names).
    public static string FindCollectionFile(string scenesDir, string name)
    {
        if (!Directory.Exists(scenesDir)) return null;
        foreach (var f in Directory.GetFiles(scenesDir, "*.json"))
        {
            try
            {
                using var s = File.OpenRead(f);
                using var doc = JsonDocument.Parse(s);
                if (doc.RootElement.TryGetProperty("name", out var n) && n.GetString() == name) return f;
            }
            catch { }
        }
        return null;
    }

    // The profile folder whose basic.ini [General] Name is the given one.
    public static string FindProfileDir(string profilesDir, string name)
    {
        if (!Directory.Exists(profilesDir)) return null;
        foreach (var d in Directory.GetDirectories(profilesDir))
        {
            var ini = Path.Combine(d, "basic.ini");
            try { if (File.Exists(ini) && ReadSection(ini, "General").TryGetValue("Name", out var n) && n == name) return d; }
            catch { }
        }
        return null;
    }

    // OBS 32 leaves a "run_..." marker while it runs and deletes it on a clean exit; a leftover marker makes the next
    // start ask about Safe Mode (which switches the WebSocket off). Only markers from a run that began at or after
    // 'since' count, so a force-closed OBS we started doesn't leave that question for the next start.
    public static List<string> SentinelsSince(string sentinelDir, DateTime since)
    {
        if (!Directory.Exists(sentinelDir)) return new List<string>();
        return Directory.GetFiles(sentinelDir, "run_*").Where(f => File.GetCreationTime(f) >= since).ToList();
    }

    // OBS's main window: a Qt top-level window of that process whose title starts with "OBS " (e.g.
    // "OBS 32.2.2 - Profile: ... - Scenes: ..."). Docks and projectors are other windows with other titles.
    public static List<IntPtr> MainWindows(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var owner);
            if (owner != pid) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            if (!cls.ToString().StartsWith("Qt", StringComparison.Ordinal) || !cls.ToString().Contains("QWindowIcon")) return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            if (title.ToString().StartsWith("OBS ", StringComparison.Ordinal)) found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
}
