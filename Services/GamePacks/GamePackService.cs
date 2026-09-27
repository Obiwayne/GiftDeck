using System.IO.Compression;
using System.Text.Json;

namespace GiftDeck.Services;

// Game Packs: supported games, their one-click mod installs and ready-made presets. See docs/v2.1/plan.md.
// Packs ship next to the exe in Packs\<id>\ (pack.json, cover, commands.json, presets\*.giftdeck).
// What the user chose and what GiftDeck installed live in <data>\packs\<id>\ (state.json, installed.json, backup\).
public class GamePackService
{
    static readonly JsonSerializerOptions Json = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    List<GamePack> _packs = new List<GamePack>();
    readonly Dictionary<string, (DateTime at, Dictionary<string, string> versions)> _latest = new Dictionary<string, (DateTime, Dictionary<string, string>)>();
    readonly HashSet<string> _busy = new HashSet<string>();

    public event Action Changed;

    public static string PacksDir => Path.Combine(AppContext.BaseDirectory, "Packs");
    public IReadOnlyList<GamePack> Packs => _packs;
    public GamePack Find(string id) => _packs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    public bool IsBusy(GamePack p) { lock (_busy) return _busy.Contains(p.Id); }

    public void Load() => Load(PacksDir);

    public void Load(string packsDir)
    {
        var list = new List<GamePack>();
        if (Directory.Exists(packsDir))
        {
            foreach (var dir in Directory.GetDirectories(packsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var file = Path.Combine(dir, "pack.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var p = JsonSerializer.Deserialize<GamePack>(File.ReadAllText(file), Json);
                    if (p == null) continue;
                    if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Path.GetFileName(dir);
                    p.Dir = dir;
                    list.Add(p);
                }
                catch (Exception e) { Log.Write($"Game pack {Path.GetFileName(dir)} couldn't be read: {e.Message}"); }
            }
        }
        _packs = list;
        Changed?.Invoke();
    }

    // ---- The game's folder ----

    static string StateName(string id) => Path.Combine("packs", id, "state.json");
    public PackState State(GamePack p) => Storage.Load<PackState>(StateName(p.Id)) ?? new PackState();
    void SaveState(GamePack p, PackState s) => Storage.Save(StateName(p.Id), s);

    public (string folder, string foundBy) LocateGame(GamePack p) => GameLocator.Locate(p, State(p).GameFolder);

    // "Choose folder…": remembered for this pack. Also accepts the exe itself.
    public void SetGameFolder(GamePack p, string folder)
    {
        if (File.Exists(folder)) folder = Path.GetDirectoryName(folder);
        if (!GameLocator.IsGameFolder(p, folder))
            throw new Exception($"That folder doesn't have {p.Exe} in it. Pick the folder {p.Name} is installed in.");
        var s = State(p);
        s.GameFolder = Path.GetFullPath(folder);
        SaveState(p, s);
        Changed?.Invoke();
    }

    public void ForgetGameFolder(GamePack p)
    {
        var s = State(p);
        s.GameFolder = "";
        SaveState(p, s);
        Changed?.Invoke();
    }

    // ---- Status ----

    // Local checks only (fast enough to call every couple of seconds); latest versions come from CheckLatestAsync.
    public PackStatus GetStatus(GamePack p)
    {
        var st = new PackStatus { Installed = PackInstaller.LoadRecord(p.Id), Running = PackInstaller.RunningProcesses(p) };
        var (folder, by) = LocateGame(p);
        // Where the mods are installed wins: that's the folder uninstall works on.
        if (st.Installed != null && Directory.Exists(st.Installed.GameFolder) && GameLocator.IsGameFolder(p, st.Installed.GameFolder)
            && !string.Equals(Path.GetFullPath(st.Installed.GameFolder), folder, StringComparison.OrdinalIgnoreCase))
            (folder, by) = (st.Installed.GameFolder, "where the mods are installed");
        st.GameFolder = folder;
        st.FoundBy = by ?? "";
        Dictionary<string, string> latest = null;
        lock (_latest) if (_latest.TryGetValue(p.Id, out var l)) latest = l.versions;
        foreach (var c in p.Components)
        {
            var cs = new ComponentStatus { Component = c };
            if (st.Installed != null && st.Installed.Components.TryGetValue(c.Id, out var ic)) cs.InstalledVersion = ic.Version;
            if (folder != null && !string.IsNullOrEmpty(c.DetectInstalled))
            {
                var f = Path.Combine(folder, c.DetectInstalled);
                cs.OnDisk = File.Exists(f);
                // The version file (e.g. chaosmodersion.txt) lands at the same place in the game folder; else the file's own version.
                if (cs.OnDisk) cs.DiskVersion = (string.IsNullOrEmpty(c.VersionFile) ? null : PackInstaller.ReadVersionFile(Path.Combine(folder, c.VersionFile)))
                                               ?? PackInstaller.ReadVersionFile(f);
            }
            if (latest != null && latest.TryGetValue(c.Id, out var v)) cs.LatestVersion = v;
            st.Components.Add(cs);
        }
        return st;
    }

    // The newest version of each component (GitHub, the download page, a local build). Cached for 10 minutes.
    public async Task<Dictionary<string, string>> CheckLatestAsync(GamePack p, bool force = false)
    {
        lock (_latest)
            if (!force && _latest.TryGetValue(p.Id, out var cached) && DateTime.Now - cached.at < TimeSpan.FromMinutes(10)) return cached.versions;
        var tasks = p.Components.Select(async c => (c.Id, v: await PackInstaller.LatestVersionAsync(c))).ToList();
        var result = new Dictionary<string, string>();
        foreach (var (id, v) in await Task.WhenAll(tasks)) if (v != null) result[id] = v;
        lock (_latest) _latest[p.Id] = (DateTime.Now, result);
        Changed?.Invoke();
        return result;
    }

    // ---- Install / uninstall ----

    // Throws ManualDownloadNeeded when a component must be downloaded in the browser: call again with
    // manualFiles[component id] = the file the user picked.
    public async Task InstallAsync(GamePack p, IProgress<(double, string)> progress, IDictionary<string, string> manualFiles = null)
    {
        Enter(p);
        try
        {
            var (folder, _) = LocateGame(p);
            var record = PackInstaller.LoadRecord(p.Id);
            if (record != null && GameLocator.IsGameFolder(p, record.GameFolder)) folder = record.GameFolder;
            if (folder == null) throw new Exception($"GiftDeck can't find {p.Name}. Press \"Choose folder…\" and pick the folder it's installed in.");
            await Task.Run(() => new PackInstaller(p, progress).InstallAsync(folder, manualFiles));
        }
        finally { Leave(p); }
    }

    public async Task UninstallAsync(GamePack p, IProgress<(double, string)> progress)
    {
        Enter(p);
        try { await new PackInstaller(p, progress).UninstallAsync(); }
        finally { Leave(p); }
    }

    void Enter(GamePack p)
    {
        lock (_busy) if (!_busy.Add(p.Id)) throw new Exception($"GiftDeck is already installing or removing {p.Name}.");
        Changed?.Invoke();
    }

    void Leave(GamePack p)
    {
        lock (_busy) _busy.Remove(p.Id);
        Changed?.Invoke();
    }

    // ---- Presets ----

    // The pack's presets: the ones pack.json lists, plus any other .giftdeck in its presets folder.
    public List<PackPreset> Presets(GamePack p)
    {
        var files = new List<string>(p.Presets);
        var dir = Path.Combine(p.Dir, "presets");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*" + ProfileService.FileExtension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                files.Add(Path.GetRelativePath(p.Dir, f));
        var list = new List<PackPreset>();
        foreach (var rel in files.Select(f => f.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.Combine(p.Dir, rel);
            if (!File.Exists(full)) continue;
            list.Add(new PackPreset { File = rel, FullPath = full, Name = PresetName(full) });
        }
        return list;
    }

    static string PresetName(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            using var s = zip.GetEntry("profile.json")?.Open();
            if (s != null && JsonDocument.Parse(s).RootElement.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) return name;
        }
        catch { }
        return Path.GetFileNameWithoutExtension(file);
    }

    // The profile made from this preset earlier, if it's still there.
    public string ImportedProfile(GamePack p, PackPreset preset)
    {
        var s = State(p);
        return s.PresetProfiles.TryGetValue(preset.File, out var name) && Hub.Profiles.List().Contains(name) ? name : null;
    }

    // Imports the preset as a new profile (ProfileService.Import) and returns its name. Switching to it is up to the caller
    // (MainWindow.SwitchProfile, which shows the switch animation).
    public string ImportPreset(GamePack p, PackPreset preset)
    {
        var name = Hub.Profiles.Import(preset.FullPath);
        var s = State(p);
        s.PresetProfiles[preset.File] = name;
        SaveState(p, s);
        Log.Write($"{p.Name}: the \"{preset.Name}\" preset is now the \"{name}\" profile");
        return name;
    }

    // ---- Commands ----

    // The pack's static command list (Packs\<id>\commands.json, same shape as a GameLink hello's commands),
    // so events can be set up while the game is closed. Empty when the game has no pack or no list.
    public List<GameCommandInfo> StaticCommands(string gameId)
    {
        var p = Find(gameId);
        if (p == null || string.IsNullOrWhiteSpace(p.Commands)) return new List<GameCommandInfo>();
        var file = Path.Combine(p.Dir, p.Commands);
        try
        {
            if (!File.Exists(file)) return new List<GameCommandInfo>();
            var text = File.ReadAllText(file);
            // Either a bare list, or {"commands":[…]} like a hello message.
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var arr = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("commands", out var c) ? c : doc.RootElement;
            return JsonSerializer.Deserialize<List<GameCommandInfo>>(arr.GetRawText(), Json) ?? new List<GameCommandInfo>();
        }
        catch (Exception e)
        {
            Log.Write($"{p.Name}: commands.json couldn't be read: {e.Message}");
            return new List<GameCommandInfo>();
        }
    }
}
