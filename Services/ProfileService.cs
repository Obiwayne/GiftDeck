using System.IO.Compression;
using System.Text.Json;
using GiftDeck.Models;

namespace GiftDeck.Services;

// A profile is one setup, e.g. one per game: its events, its overlays (menu board, goals, counters)
// and its LIVE title and category. Everything else (accounts, keys, OBS, music) is shared.
// Each lives in %APPDATA%\GiftDeck\profiles\<name>\.
public class ProfileService
{
    public const string FileExtension = ".giftdeck";
    const string FilesToken = "{profile-files}";

    public event Action Changed;

    public string Active => Hub.Settings.ActiveProfile;
    public static string Root => Path.Combine(Storage.Dir, "profiles");
    public static string DirOf(string name) => Path.Combine(Root, name);
    string ActiveDir => DirOf(Active);

    // Storage path (relative to %APPDATA%\GiftDeck) of a file in the active profile.
    public string File(string name) => Path.Combine("profiles", Active, name);

    public List<string> List() =>
        Directory.Exists(Root)
            ? Directory.GetDirectories(Root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();

    // First run with profiles: turn the existing setup into the first profile.
    public void Init()
    {
        Directory.CreateDirectory(Root);
        if (List().Count == 0)
        {
            var first = "Default";
            Directory.CreateDirectory(DirOf(first));
            foreach (var f in new[] { "rules.json", "overlays.json" })
                if (System.IO.File.Exists(Storage.PathFor(f))) System.IO.File.Copy(Storage.PathFor(f), Path.Combine(DirOf(first), f));
            Hub.Settings.ActiveProfile = first;
            Hub.SaveSettings();
            Log.Write($"Your setup is now the \"{first}\" profile");
        }
        if (string.IsNullOrEmpty(Active) || !Directory.Exists(ActiveDir))
        {
            Hub.Settings.ActiveProfile = List()[0];
            Hub.SaveSettings();
        }
    }

    public void Switch(string name)
    {
        if (name == Active || !Directory.Exists(DirOf(name))) return;
        SaveActive();
        Hub.Settings.ActiveProfile = name;
        Hub.SaveSettings();
        LoadActive();
        Log.Write($"Switched to the \"{name}\" profile");
        Changed?.Invoke();
    }

    // New profile: blank, or a copy of an existing one.
    public string Create(string name, string copyFrom = null)
    {
        name = CleanName(name);
        if (Directory.Exists(DirOf(name))) throw new Exception($"There's already a profile called \"{name}\".");
        if (copyFrom != null)
        {
            if (copyFrom == Active) SaveActive();
            CopyDir(DirOf(copyFrom), DirOf(name));
            RepointFiles(DirOf(copyFrom), DirOf(name));
        }
        else Directory.CreateDirectory(DirOf(name));
        Changed?.Invoke();
        return name;
    }

    public void Rename(string oldName, string newName)
    {
        newName = CleanName(newName);
        if (newName == oldName) return;
        if (Directory.Exists(DirOf(newName))) throw new Exception($"There's already a profile called \"{newName}\".");
        if (oldName == Active) SaveActive();
        Directory.Move(DirOf(oldName), DirOf(newName));
        RepointFiles(DirOf(oldName), DirOf(newName));
        if (oldName == Active)
        {
            Hub.Settings.ActiveProfile = newName;
            Hub.SaveSettings();
            LoadActive();
        }
        Changed?.Invoke();
    }

    public void Delete(string name)
    {
        if (name == Active) throw new Exception("Switch to another profile before deleting this one.");
        Directory.Delete(DirOf(name), true);
        Changed?.Invoke();
    }

    // Everything the active profile holds, written to its folder now.
    public void SaveActive()
    {
        Hub.Rules.Save();
        Hub.Overlays.Save();
        var t = Hub.TikTok.State;
        Storage.Save(File("stream.json"), new ProfileStream { Title = t.Title, CategoryName = t.CategoryName, CategoryId = t.CategoryId, Mature = t.Mature });
    }

    void LoadActive()
    {
        Hub.Rules.Load();
        Hub.Overlays.Reload();
        var st = Storage.Load<ProfileStream>(File("stream.json"));
        if (st != null)
        {
            var t = Hub.TikTok.State;
            t.Title = st.Title; t.CategoryName = st.CategoryName; t.CategoryId = st.CategoryId; t.Mature = st.Mature;
            Hub.TikTok.Save();
            Hub.TikTok.NotifyChanged();
        }
    }

    // ---- Export / import: one .giftdeck file (a zip) with the profile and the sounds/pictures it uses ----

    public void Export(string name, string zipPath)
    {
        if (name == Active) SaveActive();
        var dir = DirOf(name);
        var rules = LoadJson<List<Rule>>(Path.Combine(dir, "rules.json")) ?? new List<Rule>();
        var overlays = LoadJson<OverlayConfig>(Path.Combine(dir, "overlays.json")) ?? new OverlayConfig();
        overlays.Stats = new StreamStats(); // this stream's totals aren't part of a shared setup

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // local path -> name in the zip
        string Pack(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return path;
            if (!files.TryGetValue(path, out var zipName))
            {
                zipName = UniqueName(Path.GetFileName(path), files.Values);
                files[path] = zipName;
            }
            return FilesToken + "/" + zipName;
        }
        foreach (var r in rules)
            foreach (var a in r.Actions.Where(a => a.Type == ActionType.Sound)) a.Text = Pack(a.Text);
        foreach (var tile in overlays.Menu.Tiles) tile.ImageUrl = Pack(tile.ImageUrl);

        if (System.IO.File.Exists(zipPath)) System.IO.File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        WriteEntry(zip, "profile.json", JsonSerializer.Serialize(new { app = "GiftDeck", name, version = 1 }));
        WriteEntry(zip, "rules.json", Serialize(rules));
        WriteEntry(zip, "overlays.json", Serialize(overlays));
        var stream = Path.Combine(dir, "stream.json");
        if (System.IO.File.Exists(stream)) zip.CreateEntryFromFile(stream, "stream.json");
        foreach (var (path, zipName) in files) zip.CreateEntryFromFile(path, "files/" + zipName);
        Log.Write($"Exported the \"{name}\" profile to {zipPath}");
    }

    // Returns the new profile's name (made unique if needed).
    public string Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = zip.GetEntry("profile.json") ?? throw new Exception("That isn't a GiftDeck profile file.");
        string name;
        using (var s = manifest.Open())
            name = JsonDocument.Parse(s).RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        name = CleanName(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(zipPath) : name);
        var baseName = name;
        for (int i = 2; Directory.Exists(DirOf(name)); i++) name = $"{baseName} ({i})";

        var dir = DirOf(name);
        var filesDir = Path.Combine(dir, "files");
        Directory.CreateDirectory(filesDir);
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith("/")) continue;
            if (e.FullName.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.GetFullPath(Path.Combine(filesDir, Path.GetFileName(e.FullName)));
                if (target.StartsWith(Path.GetFullPath(filesDir), StringComparison.OrdinalIgnoreCase)) e.ExtractToFile(target, true);
            }
            else if (e.FullName is "rules.json" or "overlays.json" or "stream.json")
            {
                using var r = new StreamReader(e.Open());
                var text = r.ReadToEnd().Replace(FilesToken, JsonEscape(filesDir.Replace('\\', '/')));
                System.IO.File.WriteAllText(Path.Combine(dir, e.FullName), text);
            }
        }
        Log.Write($"Imported the \"{name}\" profile");
        Changed?.Invoke();
        return name;
    }

    // ---- helpers ----

    static string CleanName(string name)
    {
        name = (name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        if (name.Length == 0) throw new Exception("Give the profile a name.");
        return name.Length > 60 ? name[..60] : name;
    }

    // Sound and picture paths inside a profile's own files folder follow the profile when it's copied or renamed.
    static void RepointFiles(string fromDir, string toDir)
    {
        foreach (var f in new[] { "rules.json", "overlays.json" })
        {
            var p = Path.Combine(toDir, f);
            if (!System.IO.File.Exists(p)) continue;
            var text = System.IO.File.ReadAllText(p);
            var from = JsonEscape(Path.Combine(fromDir, "files").Replace('\\', '/'));
            var to = JsonEscape(Path.Combine(toDir, "files").Replace('\\', '/'));
            var fromBack = JsonEscape(Path.Combine(fromDir, "files"));
            var toBack = JsonEscape(Path.Combine(toDir, "files"));
            System.IO.File.WriteAllText(p, text.Replace(from, to).Replace(fromBack, toBack));
        }
    }

    static string JsonEscape(string s) => JsonSerializer.Serialize(s)[1..^1];

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) System.IO.File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static string UniqueName(string file, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var name = file;
        for (int i = 2; set.Contains(name); i++) name = $"{Path.GetFileNameWithoutExtension(file)}-{i}{Path.GetExtension(file)}";
        return name;
    }

    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
    static T LoadJson<T>(string path) where T : class => System.IO.File.Exists(path) ? JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(path), Json) : null;
    static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(text);
    }
}

public class ProfileStream
{
    public string Title { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public bool Mature { get; set; }
}
