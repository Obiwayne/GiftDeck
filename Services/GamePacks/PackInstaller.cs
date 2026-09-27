using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GiftDeck.Services;

// Installs and uninstalls a pack's mods in a game folder.
// Install: game closed -> fetch every component (GitHub latest release, a URL, a local build, or a file the
// user downloaded) into a temp folder -> back up every file that will be overwritten -> copy -> apply the
// setting edits -> installed.json. Everything is fetched before anything is written, so a failed download
// leaves the game folder as it was. Uninstall deletes what GiftDeck wrote and puts the backups back.
// Data lives in <Storage.Dir>\packs\<id>\ (installed.json, backup\<stamp>\).
public class PackInstaller
{
    readonly GamePack _pack;
    readonly IProgress<(double, string)> _progress;

    public PackInstaller(GamePack pack, IProgress<(double, string)> progress)
    {
        _pack = pack;
        _progress = progress;
    }

    public static string DataDir(string packId) => Storage.PathFor(Path.Combine("packs", packId));
    static string RecordName(string packId) => Path.Combine("packs", packId, "installed.json");
    public static PackInstallRecord LoadRecord(string packId) => Storage.Load<PackInstallRecord>(RecordName(packId));
    static void SaveRecord(PackInstallRecord r) => Storage.Save(RecordName(r.PackId), r);

    // ---- Game closed ----

    public static List<string> RunningProcesses(GamePack pack)
    {
        var running = new List<string>();
        foreach (var name in pack.MustBeClosed)
        {
            var bare = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            var ps = Process.GetProcessesByName(bare);
            if (ps.Length > 0) running.Add(name);
            foreach (var p in ps) p.Dispose();
        }
        return running;
    }

    void RequireClosed()
    {
        var running = RunningProcesses(_pack);
        if (running.Count > 0)
            throw new Exception($"Close {_pack.Name} first ({string.Join(", ", running)} is running). GiftDeck can't change the game's files while it's open.");
    }

    // ---- Install ----

    // manualFiles: component id -> a file (or folder) the user downloaded themselves, for components GiftDeck
    // couldn't download (see ManualDownloadNeeded).
    public async Task<PackInstallRecord> InstallAsync(string gameFolder, IDictionary<string, string> manualFiles = null)
    {
        if (!GameLocator.IsGameFolder(_pack, gameFolder)) throw new Exception($"GiftDeck can't find {_pack.Name}. Choose its folder first.");
        gameFolder = Path.GetFullPath(gameFolder);
        RequireClosed();

        var old = LoadRecord(_pack.Id);
        if (old != null && !SamePath(old.GameFolder, gameFolder))
            throw new Exception($"The mods are installed in another folder ({old.GameFolder}). Uninstall them there first.");

        var temp = Path.Combine(Path.GetTempPath(), "giftdeck-pack-" + _pack.Id + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        try
        {
            // 1. Fetch everything first.
            var fetched = new List<(PackComponent c, Fetched f)>();
            int n = _pack.Components.Count, i = 0;
            foreach (var c in _pack.Components)
            {
                var slice = new SliceProgress(_progress, i * 0.8 / Math.Max(1, n), 0.8 / Math.Max(1, n));
                string manual = null;
                manualFiles?.TryGetValue(c.Id, out manual);
                fetched.Add((c, await FetchAsync(c, Path.Combine(temp, c.Id), manual, slice)));
                i++;
            }

            // 2. Work out every file to copy, so a missing file stops the install before anything is written.
            var plan = new List<(PackComponent c, string src, string rel)>();
            foreach (var (c, f) in fetched)
                foreach (var (src, rel) in MapFiles(c, f.Root))
                    plan.Add((c, src, SafeRelative(gameFolder, rel)));

            // 3. Copy, backing up whatever is there.
            RequireClosed();
            var record = old ?? new PackInstallRecord { PackId = _pack.Id, GameFolder = gameFolder };
            record.Complete = false;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backupRel = Path.Combine("backup", stamp);
            try
            {
                int done = 0;
                foreach (var (c, src, rel) in plan)
                {
                    if (done++ % 10 == 0) _progress?.Report((0.8 + 0.15 * done / Math.Max(1, plan.Count), $"Copying {c.Name}…"));
                    var entry = Claim(record, gameFolder, rel, c.Id, backupRel);
                    File.Copy(src, Path.Combine(gameFolder, rel), true);
                    entry.Component = c.Id;
                }
                foreach (var (c, f) in fetched)
                    record.Components[c.Id] = new InstalledComponent { Version = f.Version, From = f.From, InstalledAt = DateTime.Now };

                // 4. Settings.
                _progress?.Report((0.96, "Changing settings…"));
                foreach (var e in _pack.Edits)
                {
                    var rel = SafeRelative(gameFolder, e.File);
                    Claim(record, gameFolder, rel, "settings", backupRel);
                    ApplyEdit(Path.Combine(gameFolder, rel), e);
                }
                record.Complete = true;
                record.InstalledAt = DateTime.Now;
            }
            finally
            {
                SaveRecord(record); // even part way: uninstall can then take out what was written
            }
            Log.Write($"{_pack.Name}: installed " + string.Join(", ", fetched.Select(x => $"{x.c.Name} {x.f.Version}")));
            _progress?.Report((1, "Installed ✓"));
            return record;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    // Makes rel (in the game folder) GiftDeck's: backs up the file already there unless GiftDeck put it there,
    // creates missing folders, and records it. Returns its entry.
    InstalledFile Claim(PackInstallRecord record, string gameFolder, string rel, string component, string backupRel)
    {
        var entry = record.Files.FirstOrDefault(f => SamePath(f.Path, rel));
        if (entry != null) return entry; // written by an earlier install: its original is already backed up
        var target = Path.Combine(gameFolder, rel);
        if (Directory.Exists(target)) throw new Exception($"{rel} is a folder in the game folder; GiftDeck won't replace it.");
        entry = new InstalledFile { Path = rel, Component = component };
        if (File.Exists(target))
        {
            var bRel = Path.Combine(backupRel, rel);
            var b = Path.Combine(DataDir(_pack.Id), bRel);
            Directory.CreateDirectory(Path.GetDirectoryName(b));
            File.Copy(target, b, true);
            entry.Backup = bRel;
        }
        else
        {
            // Folders GiftDeck creates, outermost first, so uninstall can remove them again (if they're empty then).
            var missing = new List<string>();
            for (var dir = Path.GetDirectoryName(rel); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (!Directory.Exists(Path.Combine(gameFolder, dir))) missing.Insert(0, dir);
            foreach (var d in missing)
            {
                Directory.CreateDirectory(Path.Combine(gameFolder, d));
                if (!record.Folders.Any(f => SamePath(f, d))) record.Folders.Add(d);
            }
        }
        record.Files.Add(entry); // recorded before the copy: if the copy fails half way, uninstall still cleans up
        return entry;
    }

    // ---- Uninstall ----

    public Task UninstallAsync() => Task.Run(() =>
    {
        var record = LoadRecord(_pack.Id) ?? throw new Exception("GiftDeck hasn't installed anything for " + _pack.Name + ".");
        var game = record.GameFolder;
        var data = DataDir(_pack.Id);
        if (!Directory.Exists(game))
        {
            // The game itself was uninstalled or moved: nothing left to take out.
            Storage.Delete(RecordName(_pack.Id));
            Log.Write($"{_pack.Name}: forgot the mods in {game} (the folder is gone)");
            _progress?.Report((1, "Uninstalled ✓"));
            return;
        }
        RequireClosed();
        var problems = new List<string>();
        var left = new List<InstalledFile>();
        int i = 0;
        foreach (var f in Enumerable.Reverse(record.Files).ToList())
        {
            if (i++ % 10 == 0) _progress?.Report(((double)i / Math.Max(1, record.Files.Count), "Removing the mods…"));
            try
            {
                var target = Path.Combine(game, SafeRelative(game, f.Path));
                if (File.Exists(target)) File.Delete(target);
                if (f.Backup != null)
                {
                    var b = Path.Combine(data, f.Backup);
                    if (File.Exists(b))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        File.Copy(b, target, true);
                    }
                    else problems.Add($"the backup of {f.Path} is missing");
                }
            }
            catch (Exception e) { problems.Add($"{f.Path}: {e.Message}"); left.Insert(0, f); }
        }
        foreach (var d in record.Folders.OrderByDescending(d => d.Length))
        {
            try
            {
                var full = Path.Combine(game, SafeRelative(game, d));
                if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any()) Directory.Delete(full);
            }
            catch { }
        }
        if (left.Count > 0)
        {
            record.Files = left;
            record.Complete = false;
            SaveRecord(record);
            throw new Exception("Some files couldn't be removed: " + string.Join("; ", problems.Take(5)));
        }
        Storage.Delete(RecordName(_pack.Id));
        try { if (Directory.Exists(Path.Combine(data, "backup"))) Directory.Delete(Path.Combine(data, "backup"), true); } catch { }
        Log.Write($"{_pack.Name}: mods uninstalled" + (problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : ""));
        _progress?.Report((1, "Uninstalled ✓"));
    });

    // ---- Fetching ----

    class Fetched
    {
        public string Root;     // folder with the component's files
        public string Version;
        public string From;
    }

    async Task<Fetched> FetchAsync(PackComponent c, string work, string manual, IProgress<(double, string)> progress)
    {
        Directory.CreateDirectory(work);
        if (!string.IsNullOrEmpty(manual))
        {
            var version = VersionFromName(c.AllSources.Select(s => s.Version).FirstOrDefault(v => !string.IsNullOrEmpty(v)), Path.GetFileName(manual.TrimEnd('\\', '/')));
            return FromLocal(c, manual, work, version, "downloaded by you");
        }
        var errors = new List<string>();
        ManualDownloadNeeded manualNeeded = null;
        foreach (var s in c.AllSources)
        {
            try
            {
                return (s.Type ?? "").ToLowerInvariant() switch
                {
                    "github" => await FromGitHubAsync(c, s, work, progress),
                    "url" => await FromUrlAsync(c, s, work, progress),
                    "local" => FromLocal(c, Expand(s.Path), work, null, "a local build"),
                    "page" => throw new ManualDownloadNeeded(c, s.Page, s.Instructions, null),
                    _ => throw new Exception($"unknown source type \"{s.Type}\""),
                };
            }
            catch (ManualDownloadNeeded m) { manualNeeded ??= m; }
            catch (Exception e) { errors.Add(e.Message); }
        }
        if (manualNeeded != null) throw manualNeeded;
        throw new Exception($"Couldn't get {c.Name}: " + (errors.Count > 0 ? string.Join("; ", errors) : "it has nowhere to download from"));
    }

    static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GiftDeck");
        return http;
    }

    async Task<Fetched> FromGitHubAsync(PackComponent c, PackSource s, string work, IProgress<(double, string)> progress)
    {
        using var http = NewHttp();
        progress?.Report((0, $"Finding the latest {c.Name}…"));
        var (tag, name, url, size) = await LatestGitHubAssetAsync(http, s);
        var file = await DownloadAsync(http, url, Path.Combine(work, name), c.Name, null, progress);
        if (size > 0 && new FileInfo(file).Length != size) throw new Exception($"the {c.Name} download was incomplete");
        return new Fetched { Root = Unpack(c, file, work), Version = tag, From = $"github.com/{s.Repo}" };
    }

    static async Task<(string tag, string name, string url, long size)> LatestGitHubAssetAsync(HttpClient http, PackSource s)
    {
        using var res = await http.GetAsync($"https://api.github.com/repos/{s.Repo}/releases/latest");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw new Exception($"github.com/{s.Repo} has no release yet");
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        var re = new Regex(string.IsNullOrEmpty(s.Asset) ? "\\.zip$" : s.Asset, RegexOptions.IgnoreCase);
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!re.IsMatch(name)) continue;
            return (tag, name, a.GetProperty("browser_download_url").GetString(), a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0);
        }
        throw new Exception($"the latest release of {s.Repo} ({tag}) has no matching download");
    }

    async Task<Fetched> FromUrlAsync(PackComponent c, PackSource s, string work, IProgress<(double, string)> progress)
    {
        using var http = NewHttp();
        string url;
        try
        {
            progress?.Report((0, $"Finding the latest {c.Name}…"));
            url = await ResolveUrlAsync(http, s);
        }
        catch (Exception e) when (!string.IsNullOrEmpty(s.Page)) { throw new ManualDownloadNeeded(c, s.Page, s.Instructions, e.Message); }

        var name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrEmpty(name)) name = c.Id + ".zip";
        string file;
        try
        {
            file = await DownloadAsync(http, url, Path.Combine(work, name), c.Name, string.IsNullOrEmpty(s.Page) ? null : s.Page, progress);
            if (IsZipExpected(c) && !LooksLikeZip(file)) throw new Exception("the site sent a web page instead of the file");
        }
        catch (Exception e) when (!string.IsNullOrEmpty(s.Page)) { throw new ManualDownloadNeeded(c, s.Page, s.Instructions, e.Message); }

        if (!string.IsNullOrWhiteSpace(s.Sha256))
        {
            string actual;
            await using (var fs = File.OpenRead(file)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            if (!actual.Equals(s.Sha256.Trim(), StringComparison.OrdinalIgnoreCase)) throw new Exception($"the {c.Name} download didn't match its checksum");
        }
        return new Fetched { Root = Unpack(c, file, work), Version = VersionFromName(s.Version, name), From = new Uri(url).Host };
    }

    // The download link: "url" as is, or found on "page" with the "find" regex (sites that put the version in the file name).
    static async Task<string> ResolveUrlAsync(HttpClient http, PackSource s)
    {
        if (string.IsNullOrEmpty(s.Find)) return s.Url;
        var html = await http.GetStringAsync(s.Page);
        var m = Regex.Match(html, s.Find, RegexOptions.IgnoreCase);
        if (!m.Success) throw new Exception("its download link wasn't on its page");
        var link = System.Net.WebUtility.HtmlDecode(m.Groups[m.Groups.Count > 1 ? 1 : 0].Value);
        return new Uri(new Uri(s.Page), link).ToString();
    }

    Fetched FromLocal(PackComponent c, string path, string work, string version, string from)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new Exception("no local path");
        if (Directory.Exists(path))
            return new Fetched { Root = path, Version = version ?? LocalVersion(c, path), From = from };
        if (File.Exists(path))
        {
            var root = Unpack(c, path, work);
            return new Fetched { Root = root, Version = version ?? LocalVersion(c, root), From = from };
        }
        throw new Exception($"{c.Name} isn't built yet (nothing at {path})");
    }

    static bool IsZipExpected(PackComponent c) => !string.Equals(c.Extract, "none", StringComparison.OrdinalIgnoreCase);

    static bool LooksLikeZip(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            return fs.ReadByte() == 'P' && fs.ReadByte() == 'K';
        }
        catch { return false; }
    }

    // Unzips (safely: nothing outside the folder) or, for single-file downloads, puts the file in its own folder.
    string Unpack(PackComponent c, string file, string work)
    {
        var root = Path.Combine(work, "files");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        if (IsZipExpected(c) && (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || LooksLikeZip(file)))
        {
            _progress?.Report((-1, $"Unpacking {c.Name}…"));
            var full = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            using var zip = ZipFile.OpenRead(file);
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) continue;
                var target = Path.GetFullPath(Path.Combine(root, e.FullName));
                if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                e.ExtractToFile(target, true);
            }
        }
        else File.Copy(file, Path.Combine(root, Path.GetFileName(file)), true);
        return root;
    }

    async Task<string> DownloadAsync(HttpClient http, string url, string file, string name, string referer, IProgress<(double, string)> progress)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        res.EnsureSuccessStatusCode();
        var total = res.Content.Headers.ContentLength ?? 0;
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        await using var src = await res.Content.ReadAsStreamAsync();
        await using var dst = File.Create(file);
        var buffer = new byte[1 << 16];
        long done = 0;
        int n, shown = -1;
        while ((n = await src.ReadAsync(buffer)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n));
            done += n;
            int pct = total > 0 ? (int)(done * 100 / total) : -1;
            if (pct >= 0 && pct != shown)
            {
                shown = pct;
                progress?.Report((pct / 100.0, $"Downloading {name}… {pct}%" + (total >= 1048576 ? $" of {total / 1048576.0:0.#} MB" : "")));
            }
        }
        return file;
    }

    // ---- Versions ----

    public static string VersionFromName(string pattern, string name)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(name)) return null;
        var m = Regex.Match(name, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[m.Groups.Count > 1 ? 1 : 0].Value : null;
    }

    // A local build's version: its version file, else when it was built.
    static string LocalVersion(PackComponent c, string root)
    {
        var v = ReadVersionFile(string.IsNullOrEmpty(c.VersionFile) ? null : FindFile(root, c.VersionFile));
        if (v != null) return v;
        var newest = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();
        return "build " + newest.ToString("yyyy-MM-dd HH:mm");
    }

    // A .txt file's first line (without a leading "<name> version "), or a program file's version.
    public static string ReadVersionFile(string path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var line = File.ReadLines(path).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                if (line == null) return null;
                var m = Regex.Match(line, @"\d+(\.\d+)+[\w.+-]*");
                return m.Success ? m.Value : line;
            }
            var fv = FileVersionInfo.GetVersionInfo(path);
            return string.IsNullOrWhiteSpace(fv.FileVersion) ? null : fv.FileVersion.Trim();
        }
        catch { return null; }
    }

    // What the newest version is, without downloading it. Null when it can't tell (e.g. a "page" source).
    public static async Task<string> LatestVersionAsync(PackComponent c)
    {
        using var http = NewHttp();
        http.Timeout = TimeSpan.FromSeconds(20);
        foreach (var s in c.AllSources)
        {
            try
            {
                switch ((s.Type ?? "").ToLowerInvariant())
                {
                    case "github": return (await LatestGitHubAssetAsync(http, s)).tag;
                    case "url":
                        var url = await ResolveUrlAsync(http, s);
                        var v = VersionFromName(s.Version, Path.GetFileName(new Uri(url).LocalPath));
                        if (v != null) return v;
                        break;
                    case "local":
                        var p = Expand(s.Path);
                        if (Directory.Exists(p)) return LocalVersion(c, p);
                        break;
                }
            }
            catch { }
        }
        return null;
    }

    // ---- File mapping ----

    // Every (source file, path in the game folder) the component's "files" list asks for.
    static List<(string src, string rel)> MapFiles(PackComponent c, string root)
    {
        var all = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => (full: f, rel: Path.GetRelativePath(root, f).Replace('\\', '/')))
            .Where(f => !c.Exclude.Any(x => Glob(x, Path.GetFileName(f.rel)) || Glob(x, f.rel)))
            .ToList();
        var maps = c.Files.Count > 0 ? c.Files : new List<PackFileMap> { new PackFileMap { From = "*", To = "" } };
        var result = new List<(string, string)>();
        foreach (var m in maps)
        {
            var from = (m.From ?? "*").Replace('\\', '/').TrimStart('/');
            var to = (m.To ?? "").Replace('\\', '/').TrimStart('/');
            var toDir = to.Length == 0 || to.EndsWith("/");
            if (from == "*" || from.EndsWith("/") || from.EndsWith("/*"))
            {
                var prefix = from == "*" ? "" : from.TrimEnd('*');
                var matched = all.Where(f => f.rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matched.Count == 0) throw new Exception($"{c.Name}: the download has no {from}");
                var dest = toDir ? to : to + "/";
                result.AddRange(matched.Select(f => (f.full, dest + f.rel[prefix.Length..])));
            }
            else if (from.Contains('*') || from.Contains('?'))
            {
                var dir = from.Contains('/') ? from[..(from.LastIndexOf('/') + 1)] : "";
                var pattern = from[dir.Length..];
                var matched = all.Where(f => f.rel.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && !f.rel[dir.Length..].Contains('/') && Glob(pattern, f.rel[dir.Length..])).ToList();
                if (matched.Count == 0) throw new Exception($"{c.Name}: the download has no {from}");
                var dest = toDir ? to : to + "/";
                result.AddRange(matched.Select(f => (f.full, dest + Path.GetFileName(f.rel))));
            }
            else
            {
                var src = FindFile(root, from) ?? throw new Exception($"{c.Name}: {from} is missing from the download");
                result.Add((src, toDir ? to + Path.GetFileName(from) : to));
            }
        }
        return result;
    }

    // The file at rel, or else the newest file with that name anywhere below root
    // (development builds put their output in bin\Release\ or bin\Release\net48\).
    static string FindFile(string root, string rel)
    {
        var exact = Path.Combine(root, rel.Replace('/', '\\'));
        if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(root, Path.GetFileName(rel), SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    static bool Glob(string pattern, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(pattern.Replace('\\', '/')).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    // A path inside the game folder (never outside it), relative and with Windows separators.
    static string SafeRelative(string gameFolder, string rel)
    {
        var root = Path.GetFullPath(gameFolder).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, (rel ?? "").Replace('/', '\\').TrimStart('\\')));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length)
            throw new Exception($"\"{rel}\" isn't inside the game folder");
        return full[root.Length..];
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a?.Replace('/', '\\').TrimEnd('\\'), b?.Replace('/', '\\').TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static string Expand(string path) => string.IsNullOrWhiteSpace(path) ? path : Environment.ExpandEnvironmentVariables(path.Trim());

    // ---- Setting edits ----

    public static void ApplyEdit(string file, PackEdit e)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        if (string.Equals(e.Type, "ini", StringComparison.OrdinalIgnoreCase))
        {
            var value = e.Value is JsonValue jv && jv.TryGetValue<string>(out var sv) ? sv : e.Value?.ToJsonString() ?? "";
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
            File.WriteAllLines(file, SetIni(lines, e.Section ?? "", e.Key, value));
            return;
        }
        JsonNode root = null;
        string text = File.Exists(file) ? File.ReadAllText(file) : "";
        if (!string.IsNullOrWhiteSpace(text)) root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        root ??= new JsonObject();
        var parts = (e.Path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new Exception($"A setting for {e.File} has no path");
        var node = root.AsObject();
        foreach (var p in parts[..^1])
        {
            if (node[p] is not JsonObject child) { child = new JsonObject(); node[p] = child; }
            node = child;
        }
        node[parts[^1]] = e.Value?.DeepClone();
        // Keep the file's own style: one line stays one line.
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = text.Contains('\n') }));
    }

    static List<string> SetIni(List<string> lines, string section, string key, string value)
    {
        int start = 0, end = lines.Count;
        bool found = section.Length == 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith("[") || !t.EndsWith("]")) continue;
            if (found) { end = i; break; }
            if (string.Equals(t[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase)) { found = true; start = i + 1; }
            else if (section.Length == 0) { end = i; break; }
        }
        if (section.Length == 0 && lines.Count > 0 && lines.FindIndex(l => l.TrimStart().StartsWith("[")) is var first and >= 0) end = Math.Min(end, first);
        if (!found)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return lines;
        }
        for (int i = start; i < end; i++)
        {
            var t = lines[i].TrimStart();
            if (t.StartsWith(";") || t.StartsWith("#")) continue;
            var eq = t.IndexOf('=');
            if (eq > 0 && string.Equals(t[..eq].Trim(), key, StringComparison.OrdinalIgnoreCase)) { lines[i] = $"{key}={value}"; return lines; }
        }
        // Add it after the section's last non-empty line.
        int at = end;
        while (at > start && lines[at - 1].Trim().Length == 0) at--;
        lines.Insert(at, $"{key}={value}");
        return lines;
    }

    // Reports one component's download progress as its share of the whole install.
    class SliceProgress : IProgress<(double, string)>
    {
        readonly IProgress<(double, string)> _inner;
        readonly double _start, _size;
        public SliceProgress(IProgress<(double, string)> inner, double start, double size) { _inner = inner; _start = start; _size = size; }
        public void Report((double, string) value) => _inner?.Report((value.Item1 < 0 ? -1 : _start + _size * value.Item1, value.Item2));
    }
}
