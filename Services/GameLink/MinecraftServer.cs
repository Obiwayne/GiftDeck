using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GiftDeck.Services;

// What GiftDeck remembers about the local Minecraft server (saved as minecraft.json in GiftDeck's data folder).
public class MinecraftSettings
{
    public string ServerName { get; set; } = "default";
    public string FolderOverride { get; set; } = "";   // empty = %LOCALAPPDATA%\GiftDeck\minecraft\<ServerName>
    public string Version { get; set; } = "latest";    // Paper version to set up ("latest" = newest stable)
    public string PlayerName { get; set; } = "";       // who the commands target; empty = the nearest player (@p)
    public int ServerPort { get; set; } = 25565;
    public int RconPort { get; set; } = 25575;
    public string RconPassword { get; set; } = "";     // random, made on first use
    public int MemoryMb { get; set; } = 2048;
    public bool LanAccess { get; set; }                // false = only this PC can join (and no firewall prompt)
    public bool StopWithGiftDeck { get; set; } = true;

    public static string NewPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return string.Concat(Enumerable.Range(0, 32).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]));
    }
}

// What was set up in a server folder (giftdeck-server.json next to paper.jar).
public class MinecraftInstallInfo
{
    public string Version { get; set; } = "";
    public int Build { get; set; }
    public int JavaMin { get; set; } = 21;
    public string Jar { get; set; } = "paper.jar";
    public string Sha256 { get; set; } = "";
    public DateTime InstalledAt { get; set; }
}

public record PaperBuild(string Version, int Build, string Channel, string Url, string Sha256, long Size, int JavaMin, string FileName);
public record JavaInstall(string Exe, int Major, string Version, string Source);
public record InstallProgress(string Text, double Fraction = -1);

public enum MinecraftServerState { NotInstalled, Stopped, Starting, Running, Stopping }

// A local Paper (Minecraft) server that GiftDeck downloads, configures and runs, and talks to over RCON.
// Paper downloads come from PaperMC's Fill API (v3; the old api.papermc.io/v2 answers 410 Gone), Java from
// Eclipse Temurin (Adoptium API) when the PC doesn't have a new enough one. Nothing is started or accepted
// on the user's behalf: the EULA is only written after they tick the box.
public sealed class MinecraftServer : IDisposable
{
    public const string EulaUrl = "https://aka.ms/MinecraftEULA";
    const string FillApi = "https://fill.papermc.io/v3/projects/paper";
    const string AdoptiumApi = "https://api.adoptium.net/v3";
    const string InfoFile = "giftdeck-server.json";

    static readonly HttpClient Http = CreateHttp();
    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // PaperMC asks API users to identify themselves with a contact URL.
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MayhemDeck/2.3 (+https://github.com/Obiwayne/MayhemDeck)");
        return h;
    }

    // Big downloads live under LocalAppData (not the roaming settings folder). A dev build (GIFTDECK_DATA) keeps its own.
    public static string DefaultRoot => Storage.IsDevData
        ? Path.Combine(Storage.Dir, "minecraft")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "minecraft");
    public static string DefaultJavaRoot => Storage.IsDevData
        ? Path.Combine(Storage.Dir, "java")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "java");

    public MinecraftSettings Settings { get; }
    public string Folder { get; }
    public string JavaRoot { get; }

    public MinecraftServer(MinecraftSettings settings, string folder = null, string javaRoot = null)
    {
        Settings = settings ?? new MinecraftSettings();
        if (string.IsNullOrEmpty(Settings.RconPassword)) Settings.RconPassword = MinecraftSettings.NewPassword();
        Folder = folder
                 ?? (string.IsNullOrWhiteSpace(Settings.FolderOverride) ? Path.Combine(DefaultRoot, SafeName(Settings.ServerName)) : Settings.FolderOverride);
        JavaRoot = javaRoot ?? DefaultJavaRoot;
        _state = IsInstalled ? MinecraftServerState.Stopped : MinecraftServerState.NotInstalled;
    }

    static string SafeName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ---------------- state ----------------

    public event Action Changed;
    public event Action<string> ConsoleLine;

    MinecraftServerState _state;
    public MinecraftServerState State => _state;
    public string LastError { get; private set; } = "";
    public int PlayersOnline { get; private set; } = -1;
    public string PlayerList { get; private set; } = "";
    public bool RconConnected => _rcon.IsConnected;
    public bool OwnsProcess => _proc != null && !_proc.HasExited;

    void SetState(MinecraftServerState s, string error = null)
    {
        _state = s;
        if (error != null) LastError = error;
        Changed?.Invoke();
    }

    public string StateText => _state switch
    {
        MinecraftServerState.NotInstalled => "Not set up yet",
        MinecraftServerState.Stopped => string.IsNullOrEmpty(LastError) ? "Stopped" : "Stopped: " + LastError,
        MinecraftServerState.Starting => "Starting… (the first start takes a minute or two)",
        MinecraftServerState.Running => RconConnected
            ? "Running" + (PlayersOnline >= 0 ? $", {PlayersOnline} {(PlayersOnline == 1 ? "player" : "players")} online" : "")
                + (OwnsProcess ? "" : " (started outside MayhemDeck)")
            : "Running, connecting…",
        MinecraftServerState.Stopping => "Stopping… (saving the world)",
        _ => _state.ToString(),
    };

    // ---------------- console ----------------

    readonly LinkedList<string> _console = new LinkedList<string>();
    const int ConsoleMax = 1000;
    static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    public string[] ConsoleTail(int max = ConsoleMax) { lock (_console) return _console.Skip(Math.Max(0, _console.Count - max)).ToArray(); }

    void AddConsole(string line)
    {
        if (line == null) return;
        line = Ansi.Replace(line, "").TrimEnd('\r');
        lock (_console)
        {
            _console.AddLast(line);
            while (_console.Count > ConsoleMax) _console.RemoveFirst();
        }
        ConsoleLine?.Invoke(line);
    }

    // ---------------- what's installed ----------------

    public string JarPath => Path.Combine(Folder, Info?.Jar ?? "paper.jar");
    public MinecraftInstallInfo Info => ReadJson<MinecraftInstallInfo>(Path.Combine(Folder, InfoFile));
    public bool IsInstalled => Info != null && File.Exists(Path.Combine(Folder, Info.Jar));
    public string InstalledVersion => Info?.Version ?? "";

    public bool EulaAccepted
    {
        get
        {
            try { return File.ReadAllLines(Path.Combine(Folder, "eula.txt")).Any(l => l.Trim().Equals("eula=true", StringComparison.OrdinalIgnoreCase)); }
            catch { return false; }
        }
    }

    // Only call this after the user ticked "I accept the Minecraft EULA".
    public void AcceptEula()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "eula.txt"),
            $"#By changing the setting below to TRUE you are indicating your agreement to our EULA ({EulaUrl}).\n" +
            $"#Accepted in MayhemDeck by the server's owner on {DateTime.Now:yyyy-MM-dd HH:mm}\n" +
            "eula=true\n");
        Changed?.Invoke();
    }

    public void RevokeEula()
    {
        try { File.Delete(Path.Combine(Folder, "eula.txt")); } catch { }
        Changed?.Invoke();
    }

    // ---------------- Paper downloads ----------------

    // Release versions Paper has builds for, newest first (no pre-releases or release candidates).
    public static async Task<List<string>> GetVersionsAsync(CancellationToken ct = default)
    {
        var root = JsonNode.Parse(await Http.GetStringAsync(FillApi, ct));
        var list = new List<string>();
        if (root?["versions"] is JsonObject families)
            foreach (var fam in families)
                if (fam.Value is JsonArray arr)
                    foreach (var v in arr)
                    {
                        var id = v?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id) && !id.Contains('-')) list.Add(id);
                    }
        return list;
    }

    // Finds the build to download. "latest" = the newest version that has a stable build.
    public static async Task<PaperBuild> ResolveAsync(string version, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var v in (await GetVersionsAsync(ct)).Take(8))
            {
                var b = await StableBuildAsync(v, ct);
                if (b != null) return b;
            }
            throw new Exception("PaperMC has no stable server download right now. Pick a version instead of Latest.");
        }
        return await StableBuildAsync(version.Trim(), ct)
               ?? await AnyBuildAsync(version.Trim(), ct)
               ?? throw new Exception($"PaperMC has no download for Minecraft {version}.");
    }

    static async Task<int> JavaMinAsync(string version, CancellationToken ct)
    {
        var v = JsonNode.Parse(await Http.GetStringAsync($"{FillApi}/versions/{Uri.EscapeDataString(version)}", ct));
        return (int?)v?["version"]?["java"]?["version"]?["minimum"] ?? 21;
    }

    static async Task<PaperBuild> StableBuildAsync(string version, CancellationToken ct)
    {
        var resp = await Http.GetAsync($"{FillApi}/versions/{Uri.EscapeDataString(version)}/builds?channel=STABLE", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        if (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) is not JsonArray builds || builds.Count == 0) return null;
        var newest = builds.OrderByDescending(b => (int?)b?["id"] ?? 0).First();
        return ToBuild(version, newest, await JavaMinAsync(version, ct));
    }

    static async Task<PaperBuild> AnyBuildAsync(string version, CancellationToken ct)
    {
        var resp = await Http.GetAsync($"{FillApi}/versions/{Uri.EscapeDataString(version)}/builds/latest", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return ToBuild(version, JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)), await JavaMinAsync(version, ct));
    }

    static PaperBuild ToBuild(string version, JsonNode b, int javaMin)
    {
        var d = b?["downloads"]?["server:default"] ?? throw new Exception("PaperMC's answer has no server download.");
        return new PaperBuild(version, (int?)b["id"] ?? 0, (string)b["channel"] ?? "", (string)d["url"], (string)d["checksums"]?["sha256"],
            (long?)d["size"] ?? 0, javaMin, (string)d["name"] ?? "paper.jar");
    }

    // Downloads the server jar (checked against PaperMC's sha256) and writes the GiftDeck settings into
    // server.properties. Doesn't touch the EULA and doesn't start anything.
    public async Task InstallAsync(PaperBuild build, IProgress<InstallProgress> progress = null, CancellationToken ct = default)
    {
        if (OwnsProcess || _state is MinecraftServerState.Running or MinecraftServerState.Starting)
            throw new Exception("Stop the server before changing its version.");
        Directory.CreateDirectory(Folder);
        var tmp = Path.Combine(Folder, "paper.jar.download");
        await DownloadVerifiedAsync(build.Url, tmp, build.Sha256, build.Size, $"Downloading Paper {build.Version} (build {build.Build})", progress, ct);
        File.Move(tmp, Path.Combine(Folder, "paper.jar"), true);
        WriteJson(Path.Combine(Folder, InfoFile), new MinecraftInstallInfo
        {
            Version = build.Version, Build = build.Build, JavaMin = build.JavaMin, Jar = "paper.jar", Sha256 = build.Sha256, InstalledAt = DateTime.Now,
        });
        WriteProperties();
        InstallPluginsSafe();
        progress?.Report(new InstallProgress($"Paper {build.Version} is ready", 1));
        SetState(MinecraftServerState.Stopped, "");
    }

    static async Task DownloadVerifiedAsync(string url, string path, string sha256, long size, string what, IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long done = 0;
            var lastReport = DateTime.MinValue;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                hash.AppendData(buf, 0, n);
                done += n;
                if ((DateTime.Now - lastReport).TotalMilliseconds > 250)
                {
                    lastReport = DateTime.Now;
                    progress?.Report(new InstallProgress($"{what}… {done / 1048576} of {Math.Max(1, total / 1048576)} MB", total > 0 ? (double)done / total : -1));
                }
            }
        }
        var got = Convert.ToHexString(hash.GetHashAndReset());
        if (!string.IsNullOrEmpty(sha256) && !got.Equals(sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            throw new Exception("The download was damaged (its checksum doesn't match). Try again.");
        }
    }

    // ---------------- Java ----------------

    // Every Java GiftDeck can find: its own downloads, JAVA_HOME, PATH, the usual install folders and the
    // Minecraft Launcher's own runtimes.
    public List<JavaInstall> FindJavas()
    {
        var found = new Dictionary<string, JavaInstall>(StringComparer.OrdinalIgnoreCase);
        void Try(string exe, string source)
        {
            try
            {
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
                exe = Path.GetFullPath(exe);
                if (found.ContainsKey(exe)) return;
                var ver = ReadJavaVersion(exe);
                if (ver == null) return;
                found[exe] = new JavaInstall(exe, MajorOf(ver), ver, source);
            }
            catch { }
        }
        void TryDirs(string root, string pattern, string source, int depth)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var exe in Directory.EnumerateFiles(root, "java.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = depth, IgnoreInaccessible = true }))
                    if (exe.EndsWith(Path.Combine("bin", "java.exe"), StringComparison.OrdinalIgnoreCase)) Try(exe, source);
            }
            catch { }
        }

        TryDirs(JavaRoot, "*", "Downloaded by MayhemDeck", 3);
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(home)) Try(Path.Combine(home, "bin", "java.exe"), "JAVA_HOME");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            Try(Path.Combine(dir.Trim().Trim('"'), "java.exe"), "PATH");
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            foreach (var vendor in new[] { "Eclipse Adoptium", "Java", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto", "Eclipse Foundation", "Semeru" })
                TryDirs(Path.Combine(pf, vendor), "*", vendor, 3);
            TryDirs(Path.Combine(pf, "Minecraft Launcher", "runtime"), "*", "Minecraft Launcher", 5);
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        TryDirs(Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"), "*", "Minecraft Launcher", 5);
        return found.Values.OrderBy(j => j.Major).ToList();
    }

    // The oldest Java that is new enough (newer Java can refuse old Paper versions).
    public JavaInstall FindJava(int minMajor) => FindJavas().Where(j => j.Major >= minMajor).OrderBy(j => j.Major).FirstOrDefault();

    static string ReadJavaVersion(string exe)
    {
        var release = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)) ?? "", "release");
        if (File.Exists(release))
        {
            var m = Regex.Match(File.ReadAllText(release), "JAVA_VERSION=\"([^\"]+)\"");
            if (m.Success) return m.Groups[1].Value;
        }
        // No release file (e.g. the Oracle javapath shim): ask java itself.
        var psi = new ProcessStartInfo(exe, "-version") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi);
        var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
        var v = Regex.Match(text, "version \"([^\"]+)\"");
        return v.Success ? v.Groups[1].Value : null;
    }

    static int MajorOf(string version)
    {
        var parts = version.Split('.', '_', '+', '-');
        int.TryParse(parts[0], out int major);
        if (major == 1 && parts.Length > 1) int.TryParse(parts[1], out major); // "1.8.0_392" is Java 8
        return major;
    }

    // Temurin only makes Windows JREs for long-term versions (17, 21, 25, ...): use the first one that's new enough.
    public static async Task<int> TemurinMajorFor(int minMajor, CancellationToken ct = default)
    {
        try
        {
            var info = JsonNode.Parse(await Http.GetStringAsync($"{AdoptiumApi}/info/available_releases", ct));
            if (info?["available_lts_releases"] is JsonArray lts)
            {
                var pick = lts.Select(x => (int?)x ?? 0).Where(v => v >= minMajor).OrderBy(v => v).FirstOrDefault();
                if (pick > 0) return pick;
            }
        }
        catch { }
        return minMajor;
    }

    // Downloads Eclipse Temurin (JRE) into GiftDeck's java folder. Returns the new java.exe.
    public async Task<JavaInstall> DownloadJavaAsync(int minMajor, IProgress<InstallProgress> progress = null, CancellationToken ct = default)
    {
        int major = await TemurinMajorFor(minMajor, ct);
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x64";
        progress?.Report(new InstallProgress($"Finding Java {major} (Eclipse Temurin)…"));
        var list = JsonNode.Parse(await Http.GetStringAsync($"{AdoptiumApi}/assets/latest/{major}/hotspot?os=windows&architecture={arch}&image_type=jre&vendor=eclipse", ct)) as JsonArray;
        var pkg = list?.FirstOrDefault()?["binary"]?["package"] ?? throw new Exception($"Eclipse Temurin has no Java {major} download for this PC.");
        var url = (string)pkg["link"];
        if (url == null || !url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new Exception("Eclipse Temurin's download isn't a zip.");

        Directory.CreateDirectory(JavaRoot);
        var zip = Path.Combine(JavaRoot, $"temurin-{major}.zip.download");
        await DownloadVerifiedAsync(url, zip, (string)pkg["checksum"], (long?)pkg["size"] ?? 0, $"Downloading Java {major}", progress, ct);

        progress?.Report(new InstallProgress($"Unpacking Java {major}…"));
        var target = Path.Combine(JavaRoot, $"temurin-{major}-jre");
        var unpack = target + ".unpack";
        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, unpack), ct);
        File.Delete(zip);
        var inner = Directory.GetDirectories(unpack);
        var home = inner.Length == 1 && File.Exists(Path.Combine(inner[0], "bin", "java.exe")) ? inner[0] : unpack;
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(home, target);
        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);

        var exe = Path.Combine(target, "bin", "java.exe");
        var ver = ReadJavaVersion(exe) ?? major.ToString();
        progress?.Report(new InstallProgress($"Java {ver} is ready", 1));
        return new JavaInstall(exe, MajorOf(ver), ver, "Downloaded by MayhemDeck");
    }

    // ---------------- server.properties ----------------

    // Keeps the server's other settings and sets the ones GiftDeck relies on. Written before every start.
    public void WriteProperties()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "server.properties");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string> { "#Minecraft server properties (MayhemDeck sets the RCON and address lines)" };
        void Set(string key, string value, bool onlyIfMissing = false)
        {
            int i = lines.FindIndex(l => !l.TrimStart().StartsWith('#') && l.Split('=')[0].Trim() == key);
            if (i >= 0) { if (!onlyIfMissing) lines[i] = key + "=" + value; }
            else lines.Add(key + "=" + value);
        }
        Set("enable-rcon", "true");
        Set("rcon.port", Settings.RconPort.ToString());
        Set("rcon.password", Settings.RconPassword);
        Set("broadcast-rcon-to-ops", "false"); // otherwise every gift command is echoed into the ops' chat
        // Minecraft binds the game port and RCON to server-ip. 127.0.0.1 = only this PC, so Windows Firewall
        // doesn't ask and nobody on the network can reach RCON.
        Set("server-ip", Settings.LanAccess ? "" : "127.0.0.1");
        Set("server-port", Settings.ServerPort.ToString());
        Set("online-mode", "true", onlyIfMissing: true);
        Set("enable-query", "false", onlyIfMissing: true);
        Set("motd", "MayhemDeck TikTok LIVE server", onlyIfMissing: true);
        File.WriteAllLines(path, lines);
    }

    // ---------------- plugins ----------------

    // GiftDeck's own server plugins (Packs/minecraft/plugins/*.jar: GiftDeck Games, the mini-games) go into the
    // server's plugins folder at set-up and before every start. A jar is copied when it is missing or GiftDeck's
    // is newer (plugin.yml version; same version but different bytes = a rebuilt jar, also copied). A newer
    // one the user put there themselves is kept.
    public string PluginsSource { get; set; } = Path.Combine(AppContext.BaseDirectory, "Packs", "minecraft", "plugins");

    public List<string> InstallPlugins()
    {
        var done = new List<string>();
        if (!Directory.Exists(PluginsSource)) return done;
        var dest = Path.Combine(Folder, "plugins");
        foreach (var jar in Directory.GetFiles(PluginsSource, "*.jar"))
        {
            var target = Path.Combine(dest, Path.GetFileName(jar));
            var ours = PluginVersion(jar);
            if (File.Exists(target))
            {
                var theirs = PluginVersion(target);
                var a = MinecraftTarget.ParseVersion(ours);
                var b = MinecraftTarget.ParseVersion(theirs);
                if (a != null && b != null && a < b) continue;
                if (a != null && b != null && a == b && SameBytes(jar, target)) continue;
                if ((a == null || b == null) && SameBytes(jar, target)) continue;
            }
            Directory.CreateDirectory(dest);
            File.Copy(jar, target, true);
            done.Add($"{Path.GetFileNameWithoutExtension(jar)} {ours}".Trim());
        }
        return done;
    }

    void InstallPluginsSafe()
    {
        try
        {
            foreach (var p in InstallPlugins()) AddConsole($"[MayhemDeck] Installed the server plugin {p}");
        }
        catch (Exception ex) { AddConsole("[MayhemDeck] Couldn't copy MayhemDeck's server plugins: " + ex.Message); }
    }

    // The version line of a plugin jar's plugin.yml (or paper-plugin.yml), or "".
    public static string PluginVersion(string jar)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            var entry = zip.GetEntry("plugin.yml") ?? zip.GetEntry("paper-plugin.yml");
            if (entry == null) return "";
            using var r = new StreamReader(entry.Open());
            var m = Regex.Match(r.ReadToEnd(), @"^version:\s*['""]?([^'""\r\n]+)", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch { return ""; }
    }

    static bool SameBytes(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    // ---------------- start / stop ----------------

    Process _proc;
    readonly RconClient _rcon = new RconClient();
    readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
    CancellationTokenSource _pollCts;

    public async Task StartAsync(JavaInstall java = null)
    {
        if (OwnsProcess || _state is MinecraftServerState.Starting or MinecraftServerState.Running) return;
        LastError = "";
        if (!IsInstalled) throw new Exception("Set up the server first.");
        if (!EulaAccepted) throw new Exception("Tick \"I accept the Minecraft EULA\" first.");
        var info = Info;
        java ??= FindJava(info.JavaMin) ?? throw new Exception($"This server needs Java {info.JavaMin} or newer. Download it first.");

        // Already running (e.g. GiftDeck was restarted while the server kept going)?
        if (await TryConnectAsync(TimeSpan.FromSeconds(2)))
        {
            SetState(MinecraftServerState.Running);
            return;
        }
        if (!PortFree(Settings.ServerPort)) throw new Exception($"Port {Settings.ServerPort} is already used by another program (another Minecraft server?). Close it or change the port.");
        if (!PortFree(Settings.RconPort)) throw new Exception($"Port {Settings.RconPort} (RCON) is already used by another program.");

        WriteProperties();
        InstallPluginsSafe();
        int mem = Math.Clamp(Settings.MemoryMb, 1024, 32768);
        var psi = new ProcessStartInfo(java.Exe)
        {
            WorkingDirectory = Folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { $"-Xms{Math.Min(1024, mem)}M", $"-Xmx{mem}M", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8", "-Dfile.encoding=UTF-8", "-jar", info.Jar, "--nogui" })
            psi.ArgumentList.Add(a);

        AddConsole($"[MayhemDeck] Starting Paper {info.Version} with Java {java.Version} ({java.Exe})");
        SetState(MinecraftServerState.Starting, "");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, a) => OnServerLine(a.Data);
        p.ErrorDataReceived += (_, a) => OnServerLine(a.Data);
        p.Exited += (_, _) => OnExited(p);
        p.Start();
        _proc = p;
        p.StandardInput.AutoFlush = true;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    void OnServerLine(string line)
    {
        if (line == null) return;
        AddConsole(line);
        if (_state == MinecraftServerState.Starting && line.Contains("Done (") && line.Contains("For help, type"))
            _ = ConnectAfterStartAsync();
        else if (line.Contains("You need to agree to the EULA"))
            LastError = "the EULA hasn't been accepted";
        else if (line.Contains("FAILED TO BIND TO PORT") || line.Contains("**** FAILED TO BIND"))
            LastError = $"port {Settings.ServerPort} is in use";
        else if (line.Contains("UnsupportedClassVersionError"))
            LastError = "this Paper version needs a newer Java";
    }

    async Task ConnectAfterStartAsync()
    {
        for (int i = 0; i < 20 && OwnsProcess; i++)
        {
            if (await TryConnectAsync(TimeSpan.FromSeconds(3)))
            {
                SetState(MinecraftServerState.Running);
                await RefreshPlayersAsync();
                return;
            }
            await Task.Delay(1000);
        }
        if (OwnsProcess) SetState(MinecraftServerState.Running, "MayhemDeck couldn't connect to it over RCON");
    }

    void OnExited(Process p)
    {
        if (p != _proc) return;
        int code = -1;
        try { code = p.ExitCode; } catch { }
        AddConsole($"[MayhemDeck] The server stopped (exit code {code})");
        _rcon.Dispose();
        _proc = null;
        PlayersOnline = -1;
        if (_state != MinecraftServerState.Stopping && code != 0 && string.IsNullOrEmpty(LastError)) LastError = "it closed unexpectedly (see the console)";
        SetState(IsInstalled ? MinecraftServerState.Stopped : MinecraftServerState.NotInstalled);
    }

    // Saves the world and stops: "stop" over RCON (or the console), then waits; killing is the last resort.
    public async Task StopAsync(TimeSpan? wait = null)
    {
        var limit = wait ?? TimeSpan.FromSeconds(60);
        var proc = _proc;
        bool external = proc == null && RconConnected;
        if (proc == null && !external) { SetState(IsInstalled ? MinecraftServerState.Stopped : MinecraftServerState.NotInstalled); return; }
        SetState(MinecraftServerState.Stopping);
        AddConsole("[MayhemDeck] Stopping the server (saving the world)…");
        bool sent = false;
        if (RconConnected)
        {
            try { await _rcon.ExecuteAsync("stop", TimeSpan.FromSeconds(5)); sent = true; }
            catch { sent = true; } // the server often closes RCON before it answers "stop"
        }
        if (!sent && proc != null && !proc.HasExited)
        {
            try { proc.StandardInput.WriteLine("stop"); sent = true; } catch { }
        }
        _rcon.Dispose();

        if (proc != null)
        {
            using var cts = new CancellationTokenSource(limit);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                AddConsole("[MayhemDeck] The server didn't stop in time, closing it forcefully");
                try { proc.Kill(true); } catch { }
                try { proc.WaitForExit(5000); } catch { }
            }
        }
        else
        {
            var until = DateTime.Now + limit;
            while (DateTime.Now < until && !PortFree(Settings.RconPort)) await Task.Delay(500);
            SetState(IsInstalled ? MinecraftServerState.Stopped : MinecraftServerState.NotInstalled);
        }
    }

    // For when something goes badly wrong: ends the process without saving.
    public void Kill()
    {
        try { _proc?.Kill(true); } catch { }
    }

    static bool PortFree(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
    }

    // ---------------- RCON ----------------

    public async Task<bool> TryConnectAsync(TimeSpan timeout)
    {
        if (_rcon.IsConnected) return true;
        if (!await _connectLock.WaitAsync(0)) return _rcon.IsConnected;
        try
        {
            await _rcon.ConnectAsync("127.0.0.1", Settings.RconPort, Settings.RconPassword, timeout);
            return true;
        }
        catch (RconAuthException)
        {
            LastError = "the RCON password doesn't match (another server is on the RCON port?)";
            return false;
        }
        catch { return false; }
        finally { _connectLock.Release(); }
    }

    // Runs a server command and returns the server's reply.
    public async Task<string> ExecuteAsync(string command, TimeSpan? timeout = null)
    {
        if (!_rcon.IsConnected && !await TryConnectAsync(TimeSpan.FromSeconds(3)))
            throw new IOException("The Minecraft server isn't running (or MayhemDeck can't reach it over RCON).");
        try { return await _rcon.ExecuteAsync(command, timeout ?? TimeSpan.FromSeconds(10)); }
        catch
        {
            Changed?.Invoke();
            throw;
        }
    }

    public async Task RefreshPlayersAsync()
    {
        try
        {
            var reply = await ExecuteAsync("list", TimeSpan.FromSeconds(5));
            // "There are 1 of a max of 20 players online: Steve"
            var m = Regex.Match(reply, @"There are (\d+)");
            PlayersOnline = m.Success ? int.Parse(m.Groups[1].Value) : -1;
            var colon = reply.IndexOf(':');
            PlayerList = colon >= 0 ? reply[(colon + 1)..].Trim() : "";
        }
        catch { PlayersOnline = -1; PlayerList = ""; }
        Changed?.Invoke();
    }

    // Keeps the RCON connection up (finds a server that is already running, notices one that stopped).
    public void StartWatching()
    {
        if (_pollCts != null) return;
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _ = Task.Run(async () =>
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_state is not (MinecraftServerState.Stopping or MinecraftServerState.Starting))
                    {
                        bool was = _rcon.IsConnected;
                        if (!was && await TryConnectAsync(TimeSpan.FromSeconds(2)))
                        {
                            if (_state != MinecraftServerState.Running) SetState(MinecraftServerState.Running);
                            await RefreshPlayersAsync();
                        }
                        else if (was && tick % 3 == 0) await RefreshPlayersAsync();
                        if (!_rcon.IsConnected && !OwnsProcess && _state == MinecraftServerState.Running)
                            SetState(IsInstalled ? MinecraftServerState.Stopped : MinecraftServerState.NotInstalled);
                    }
                }
                catch { }
                tick++;
                try { await Task.Delay(5000, ct); } catch { }
            }
        }, ct);
    }

    public void StopWatching()
    {
        _pollCts?.Cancel();
        _pollCts = null;
    }

    public void SendConsole(string line)
    {
        try { if (OwnsProcess) _proc.StandardInput.WriteLine(line); } catch { }
    }

    public void Dispose()
    {
        StopWatching();
        _rcon.Dispose();
    }

    // ---------------- helpers ----------------

    static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
    static T ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts) : null; }
        catch { return null; }
    }
    static void WriteJson<T>(string path, T value) => File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(value, JsonOpts));
}
