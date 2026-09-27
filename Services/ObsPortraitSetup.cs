using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GiftDeck.Services;

public class PortraitPlan
{
    public string SourceCollection { get; set; }
    public string SourceProfile { get; set; }
    public string SourceProfileDir { get; set; }
    public string CollectionJson { get; set; }
    public string ProfileIni { get; set; }
    // Profile files copied as-is next to basic.ini (service.json, encoder settings).
    public List<string> ProfileFiles { get; } = new List<string>();
    public List<string> Scenes { get; } = new List<string>();
    public string CurrentScene { get; set; }
    public int Items { get; set; }
    public int Sources { get; set; }
    public List<string> Warnings { get; } = new List<string>();
    // Set by Apply when an earlier "GiftDeck Portrait" was moved aside.
    public string BackupDir { get; set; }

    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Converts \"{SourceCollection}\" / \"{SourceProfile}\" to \"{ObsPortraitSetup.CollectionName}\" at 1080x1920.");
            sb.AppendLine($"{Scenes.Count} scenes: {string.Join(", ", Scenes)} (starts on {CurrentScene}).");
            sb.AppendLine($"{Items} scene items, {Sources} sources kept.");
            foreach (var w in Warnings) sb.AppendLine("Warning: " + w);
            return sb.ToString().TrimEnd();
        }
    }
}

// Turns the Aitum Stream Suite vertical canvas into a normal OBS scene collection + profile whose
// main canvas is portrait, so GiftDeck can run OBS without the plugin. Only ever writes new files.
public static class ObsPortraitSetup
{
    public const string CollectionName = "GiftDeck Portrait";
    public const string ProfileName = "GiftDeck Portrait";
    public const int Width = 1080, Height = 1920;

    // libobs gives the main canvas this fixed uuid (it spells "libobs" + "main" in hex).
    const string MainCanvasUuid = "6c69626f-6273-4c00-9d88-c5136d61696e";

    static readonly JsonSerializerOptions WriteOpts = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static PortraitPlan Plan(string obsBasicDir, string sourceCollection, string sourceProfile, string canvasName = "Aitum Vertical")
    {
        if (string.Equals(sourceCollection, CollectionName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"\"{CollectionName}\" is already the portrait collection.");

        var plan = new PortraitPlan { SourceCollection = sourceCollection, SourceProfile = sourceProfile };
        var collectionFile = FindCollection(obsBasicDir, sourceCollection)
            ?? throw new FileNotFoundException($"OBS scene collection \"{sourceCollection}\" not found in {obsBasicDir}");
        plan.SourceProfileDir = FindProfile(obsBasicDir, sourceProfile)
            ?? throw new DirectoryNotFoundException($"OBS profile \"{sourceProfile}\" not found in {obsBasicDir}");

        if (ObsRunning())
            plan.Warnings.Add("OBS is running, so its collection file may be older than what OBS shows. Close OBS and plan again for an exact copy.");

        plan.CollectionJson = ConvertCollection(JsonNode.Parse(File.ReadAllText(collectionFile)).AsObject(), canvasName, plan);
        plan.ProfileIni = ConvertProfile(File.ReadAllText(Path.Combine(plan.SourceProfileDir, "basic.ini")));
        foreach (var f in Directory.GetFiles(plan.SourceProfileDir, "*.json"))
        {
            // aitum.json holds the plugin's extra canvas and outputs, which the portrait profile replaces.
            if (!Path.GetFileName(f).Equals("aitum.json", StringComparison.OrdinalIgnoreCase)) plan.ProfileFiles.Add(Path.GetFileName(f));
        }
        return plan;
    }

    public static void Apply(PortraitPlan plan, string obsBasicDir)
    {
        // OBS rewrites its current collection and profile when it exits, so files written now could be lost or clobbered.
        if (ObsRunning()) throw new InvalidOperationException("Close OBS first: it overwrites scene collection files when it exits.");
        Write(plan, obsBasicDir);
    }

    static void Write(PortraitPlan plan, string obsBasicDir)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(obsBasicDir, "giftdeck-backups", stamp);
        var scenesDir = Path.Combine(obsBasicDir, "scenes");
        var profilesDir = Path.Combine(obsBasicDir, "profiles");
        var collectionPath = Path.Combine(scenesDir, SafeName(CollectionName) + ".json");
        var profileDir = Path.Combine(profilesDir, SafeName(ProfileName));

        // Backups go outside scenes/ and profiles/ so OBS doesn't list them as a second "GiftDeck Portrait".
        foreach (var f in Directory.GetFiles(scenesDir, "*.json"))
        {
            if (f.Equals(collectionPath, StringComparison.OrdinalIgnoreCase) || ReadName(f) == CollectionName)
                MoveTo(f, Path.Combine(backupDir, "scenes", Path.GetFileName(f)));
        }
        foreach (var d in Directory.GetDirectories(profilesDir))
        {
            if (d.Equals(profileDir, StringComparison.OrdinalIgnoreCase) || IniName(Path.Combine(d, "basic.ini")) == ProfileName)
                MoveTo(d, Path.Combine(backupDir, "profiles", Path.GetFileName(d)));
        }

        WriteAtomic(collectionPath, plan.CollectionJson, new UTF8Encoding(false));
        Directory.CreateDirectory(profileDir);
        WriteAtomic(Path.Combine(profileDir, "basic.ini"), plan.ProfileIni, new UTF8Encoding(true));
        foreach (var f in plan.ProfileFiles)
            File.Copy(Path.Combine(plan.SourceProfileDir, f), Path.Combine(profileDir, f));
        plan.BackupDir = Directory.Exists(backupDir) ? backupDir : null;
    }

    static string ConvertCollection(JsonObject root, string canvasName, PortraitPlan plan)
    {
        var canvasUuid = (root["canvases"] as JsonArray)?
            .Select(c => c?["info"])
            .FirstOrDefault(i => (string)i?["name"] == canvasName)?["uuid"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"The collection has no canvas named \"{canvasName}\".");

        var sources = root["sources"]?.AsArray() ?? new JsonArray();
        var groups = root["groups"] as JsonArray ?? new JsonArray();
        var byUuid = new Dictionary<string, JsonObject>();
        var byName = new Dictionary<string, JsonObject>();
        void Index(JsonObject s)
        {
            if (s == null) return;
            if (s["uuid"] is JsonValue u) byUuid[u.GetValue<string>()] = s;
            if (s["name"] is JsonValue n) byName.TryAdd(n.GetValue<string>(), s);
        }
        foreach (var s in sources.Concat(groups)) Index(s as JsonObject);
        // Global audio (DesktopAudioDevice1, AuxAudioDevice1, ...) lives at the top level and is always kept.
        var globals = root.Where(p => p.Key.Contains("AudioDevice") && p.Value is JsonObject).Select(p => (JsonObject)p.Value).ToList();
        foreach (var g in globals) Index(g);

        var scenes = sources.OfType<JsonObject>()
            .Where(s => Str(s, "id") == "scene" && Str(s, "canvas_uuid") == canvasUuid)
            .Select((s, i) => (s, order: s["settings"]?["order"] is JsonValue o ? o.GetValue<int>() : int.MaxValue, i))
            .OrderBy(x => x.order).ThenBy(x => x.i)
            .Select(x => x.s).ToList();
        if (scenes.Count == 0) throw new InvalidOperationException($"The \"{canvasName}\" canvas has no scenes.");

        // Everything the vertical scenes show, followed through nested scenes and groups.
        var keep = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<JsonObject>(scenes);
        foreach (var s in scenes) keep.Add(s);
        while (queue.Count > 0)
        {
            var s = queue.Dequeue();
            foreach (var r in References(s, byUuid, byName, plan))
            {
                if (keep.Add(r)) queue.Enqueue(r);
            }
        }

        var newSources = new JsonArray();
        foreach (var s in sources.OfType<JsonObject>().Where(keep.Contains))
        {
            var copy = s.DeepClone().AsObject();
            if (Str(s, "id") == "scene" && scenes.Contains(s))
            {
                copy["canvas_uuid"] = MainCanvasUuid;
                var settings = copy["settings"] as JsonObject;
                settings?.Remove("order");
                settings?.Remove("canvas_active");
            }
            else if (copy["canvas_uuid"] is JsonValue c && c.GetValue<string>() != MainCanvasUuid)
            {
                plan.Warnings.Add($"\"{Str(s, "name")}\" belonged to another canvas and was moved to the portrait canvas.");
                copy["canvas_uuid"] = MainCanvasUuid;
            }
            else if (Str(s, "id") == "scene")
            {
                plan.Warnings.Add($"Landscape scene \"{Str(s, "name")}\" is nested inside a vertical scene, so it is kept too.");
            }
            newSources.Add(copy);
        }
        var newGroups = new JsonArray();
        foreach (var g in groups.OfType<JsonObject>().Where(keep.Contains))
        {
            var copy = g.DeepClone().AsObject();
            if (copy.ContainsKey("canvas_uuid")) copy["canvas_uuid"] = MainCanvasUuid;
            newGroups.Add(copy);
        }

        // Scenes on different canvases may share a name with a source; on one canvas OBS would rename them on load.
        var clashes = newSources.Concat(newGroups).Select(s => Str(s.AsObject(), "name")).Concat(globals.Select(g => Str(g, "name")))
            .GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (clashes.Count > 0) plan.Warnings.Add("More than one source is named " + string.Join(", ", clashes.Select(c => $"\"{c}\"")) + "; rename them in OBS.");

        var names = scenes.Select(s => Str(s, "name")).ToList();
        var current = scenes.FirstOrDefault(s => s["settings"]?["canvas_active"] is JsonValue a && a.GetValue<bool>()) ?? scenes[0];
        var sceneOrder = new JsonArray();
        foreach (var n in names) sceneOrder.Add(new JsonObject { ["name"] = n });
        foreach (var s in keep.Where(s => Str(s, "id") == "scene" && !scenes.Contains(s)))
            sceneOrder.Add(new JsonObject { ["name"] = Str(s, "name") });

        var result = root.DeepClone().AsObject();
        result["name"] = CollectionName;
        result["sources"] = newSources;
        result["groups"] = newGroups;
        result["scene_order"] = sceneOrder;
        result["current_scene"] = Str(current, "name");
        result["current_program_scene"] = Str(current, "name");
        result["canvases"] = new JsonArray();
        result["resolution"] = new JsonObject { ["x"] = Width, ["y"] = Height };
        // Projectors point at landscape scenes and monitors; they'd reopen as stray windows.
        result["saved_projectors"] = new JsonArray();

        plan.Scenes.AddRange(names);
        plan.CurrentScene = Str(current, "name");
        plan.Items = scenes.Sum(s => (s["settings"]?["items"] as JsonArray)?.Count ?? 0);
        plan.Sources = newSources.Count(s => Str(s.AsObject(), "id") != "scene") + newGroups.Count + globals.Count;
        var dropped = sources.OfType<JsonObject>().Where(s => !keep.Contains(s) && Str(s, "id") == "scene").Select(s => Str(s, "name"));
        if (dropped.Any()) plan.Warnings.Add("Left out landscape scenes: " + string.Join(", ", dropped) + ".");
        return result.ToJsonString(WriteOpts);
    }

    // Sources a scene/group shows, plus any source a kept source or filter names in its settings
    // (image masks, source clones, move filters refer to other sources by name).
    static IEnumerable<JsonObject> References(JsonObject s, Dictionary<string, JsonObject> byUuid, Dictionary<string, JsonObject> byName, PortraitPlan plan)
    {
        var items = s["settings"]?["items"] as JsonArray;
        if (items != null)
        {
            for (int i = items.Count - 1; i >= 0; i--)
            {
                var item = items[i] as JsonObject;
                var uuid = Str(item, "source_uuid");
                var target = uuid != null && byUuid.TryGetValue(uuid, out var u) ? u
                    : byName.TryGetValue(Str(item, "name") ?? "", out var n) ? n : null;
                if (target != null) { yield return target; continue; }
                plan.Warnings.Add($"\"{Str(s, "name")}\" shows \"{Str(item, "name")}\", which doesn't exist; that item was left out.");
                items.RemoveAt(i);
            }
        }
        var texts = new List<string>();
        CollectStrings(s["settings"], texts, skip: "items");
        if (s["filters"] is JsonArray filters)
            foreach (var f in filters) CollectStrings(f?["settings"], texts, null);
        foreach (var t in texts)
            if (byName.TryGetValue(t, out var named) && !ReferenceEquals(named, s)) yield return named;
    }

    static void CollectStrings(JsonNode node, List<string> into, string skip)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var p in o) if (p.Key != skip) CollectStrings(p.Value, into, null);
                break;
            case JsonArray a:
                foreach (var x in a) CollectStrings(x, into, null);
                break;
            case JsonValue v when v.TryGetValue<string>(out var str):
                into.Add(str);
                break;
        }
    }

    // Same basic.ini, renamed and portrait. UseRescale is usually off, but set its sizes too so turning it on stays portrait.
    static string ConvertProfile(string ini)
    {
        var size = $"{Width}x{Height}";
        var set = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = new() { ["Name"] = ProfileName },
            ["Video"] = new() { ["BaseCX"] = $"{Width}", ["BaseCY"] = $"{Height}", ["OutputCX"] = $"{Width}", ["OutputCY"] = $"{Height}" },
        };
        var replaceOnly = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["RescaleRes"] = size, ["RecRescaleRes"] = size, ["FFRescaleRes"] = size,
        };

        var nl = ini.Contains("\r\n") ? "\r\n" : "\n";
        var lines = ini.TrimStart('﻿').Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var pending = set.ToDictionary(k => k.Key, k => new Dictionary<string, string>(k.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        string section = null;
        var output = new List<string>();
        void Flush()
        {
            if (section == null || !pending.TryGetValue(section, out var left)) return;
            // Insert missing keys before the blank lines that separate sections.
            int at = output.Count;
            while (at > 0 && output[at - 1].Trim().Length == 0) at--;
            output.InsertRange(at, left.Select(kv => $"{kv.Key}={kv.Value}"));
            pending.Remove(section);
        }
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("[") && t.EndsWith("]"))
            {
                Flush();
                section = t[1..^1];
                output.Add(line);
                continue;
            }
            int eq = line.IndexOf('=');
            if (section != null && eq > 0)
            {
                var key = line[..eq].Trim();
                if (pending.TryGetValue(section, out var want) && want.Remove(key, out var v)) { output.Add($"{key}={v}"); continue; }
                if (replaceOnly.TryGetValue(key, out var r)) { output.Add($"{key}={r}"); continue; }
            }
            output.Add(line);
        }
        Flush();
        foreach (var (name, left) in pending)
        {
            output.Add($"[{name}]");
            output.AddRange(left.Select(kv => $"{kv.Key}={kv.Value}"));
            output.Add("");
        }
        return string.Join(nl, output);
    }

    static string FindCollection(string basicDir, string name)
    {
        var dir = Path.Combine(basicDir, "scenes");
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "*.json");
        return files.FirstOrDefault(f => ReadName(f) == name)
            ?? files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(SafeName(name), StringComparison.OrdinalIgnoreCase));
    }

    static string FindProfile(string basicDir, string name)
    {
        var dir = Path.Combine(basicDir, "profiles");
        if (!Directory.Exists(dir)) return null;
        var dirs = Directory.GetDirectories(dir).Where(d => File.Exists(Path.Combine(d, "basic.ini"))).ToList();
        return dirs.FirstOrDefault(d => IniName(Path.Combine(d, "basic.ini")) == name)
            ?? dirs.FirstOrDefault(d => Path.GetFileName(d).Equals(SafeName(name), StringComparison.OrdinalIgnoreCase));
    }

    static string ReadName(string jsonFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonFile));
            return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
        }
        catch { return null; }
    }

    static string IniName(string iniFile)
    {
        if (!File.Exists(iniFile)) return null;
        string section = null;
        foreach (var line in File.ReadLines(iniFile))
        {
            var t = line.Trim().TrimStart('﻿');
            if (t.StartsWith("[")) section = t.Trim('[', ']');
            else if (section == "General" && t.StartsWith("Name=")) return t[5..];
        }
        return null;
    }

    // OBS's own file naming: whitespace becomes '_', other non-alphanumerics are dropped.
    static string SafeName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c)) sb.Append('_');
            else if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        }
        return sb.ToString();
    }

    static bool ObsRunning()
    {
        var procs = Process.GetProcessesByName("obs64");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    static void MoveTo(string path, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        if (Directory.Exists(path)) Directory.Move(path, dest);
        else File.Move(path, dest);
    }

    static void WriteAtomic(string path, string text, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, encoding);
        File.Move(tmp, path, true);
    }

    static string Str(JsonObject o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
