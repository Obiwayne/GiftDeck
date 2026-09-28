using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GiftDeck.Services;

// What the first-run setup checks and installs: Streamlabs (for its TikTok login), OBS Studio,
// Aitum Stream Suite (the vertical canvas), the portrait OBS setup, TikFinity and the TikTok username.
// Every installer is downloaded from its maker's own server; nothing of theirs ships with GiftDeck.
public static class SetupSteps
{
    // ---- Streamlabs Desktop ----

    const string StreamlabsFeed = "https://slobs-cdn.streamlabs.com/";

    public static string StreamlabsExe => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Streamlabs OBS", "Streamlabs OBS.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Streamlabs OBS", "Streamlabs OBS.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "slobs-client", "Streamlabs OBS.exe"),
    }.FirstOrDefault(File.Exists);

    public static bool StreamlabsInstalled => StreamlabsExe != null;
    public static bool StreamlabsRunning => Process.GetProcessesByName("Streamlabs OBS").Length > 0;
    public static bool HasStreamlabsToken => !string.IsNullOrWhiteSpace(Hub.TikTok.State.Token);

    public static void OpenStreamlabs()
    {
        var exe = StreamlabsExe;
        if (exe != null) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
    }

    public static void CloseStreamlabs()
    {
        foreach (var p in Process.GetProcessesByName("Streamlabs OBS"))
            try { p.CloseMainWindow(); } catch { }
    }

    // Streamlabs Desktop keeps its login in its Local Storage (a LevelDB): {"auth":{..."apiToken":"…","primaryPlatform":"tiktok"…}}.
    // That apiToken is the Streamlabs TikTok token GiftDeck's Go LIVE uses. Read-only; the newest entry wins.
    public static string ReadStreamlabsToken()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "slobs-client", "Local Storage", "leveldb");
        if (!Directory.Exists(dir)) return null;
        string found = null;
        var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".log") || f.EndsWith(".ldb")).OrderBy(File.GetLastWriteTimeUtc);
        foreach (var f in files)
        {
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
                var text = Encoding.Latin1.GetString(bytes);
                foreach (Match m in Regex.Matches(text, "\"apiToken\":\"([A-Za-z0-9._-]{10,200})\",\"primaryPlatform\":\"tiktok\""))
                    found = m.Groups[1].Value;
            }
            catch { }
        }
        return found;
    }

    public static Task InstallStreamlabsAsync(IProgress<(double, string)> progress, CancellationToken cancel = default) =>
        InstallFromElectronFeedAsync(StreamlabsFeed, "Streamlabs", "", progress, () => StreamlabsInstalled, cancel);

    // ---- OBS Studio and Aitum Stream Suite ----

    public static bool ObsInstalled => ObsConfig.FindExe() != null;

    public static bool AitumInstalled
    {
        get
        {
            var exe = ObsConfig.FindExe();
            if (exe != null)
            {
                var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe), "..", ".."));
                if (File.Exists(Path.Combine(root, "obs-plugins", "64bit", "aitum-stream-suite.dll"))) return true;
            }
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obs-studio", "plugins", "aitum-stream-suite");
            return Directory.Exists(data);
        }
    }

    public static Task InstallObsAsync(IProgress<(double, string)> progress, CancellationToken cancel = default) =>
        InstallFromGitHubAsync("obsproject/obs-studio", n => n.EndsWith("-Windows-x64-Installer.exe"), "OBS Studio", progress, () => ObsInstalled, cancel);

    public static Task InstallAitumAsync(IProgress<(double, string)> progress, CancellationToken cancel = default) =>
        InstallFromGitHubAsync("Aitum/obs-aitum-stream-suite", n => n == "aitum-stream-suite-windows-installer.exe", "Aitum Stream Suite", progress, () => AitumInstalled, cancel);

    public static bool PortraitReady => Hub.Settings.ObsManaged && ObsHost.PortraitExists();

    // ---- TikFinity and the username ----

    public static bool TikFinityReady => TikFinityInstaller.Installed && Hub.Settings.TikFinityConfirmed && Hub.Settings.LiveReader == "tikfinity";
    public static bool HasUsername => !string.IsNullOrWhiteSpace(Hub.Settings.BridgeUsername);

    // ---- Downloading and running installers ----

    static HttpClient NewHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GiftDeck");
        return http;
    }

    // Electron apps publish latest.yml next to their installer (name + SHA-512), which their own updater reads.
    public static async Task InstallFromElectronFeedAsync(string feed, string name, string args, IProgress<(double, string)> progress, Func<bool> installed, CancellationToken cancel = default)
    {
        using var http = NewHttp();
        progress?.Report((0, $"Finding the latest {name}…"));
        var yml = await GetTextAsync(http, feed + "latest.yml", name, cancel);
        var file = Regex.Match(yml, @"^path:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value.Trim('\'', '"');
        var sha = Regex.Match(yml, @"^sha512:\s*(\S+)", RegexOptions.Multiline).Groups[1].Value.Trim('\'', '"');
        if (file.Length == 0 || sha.Length == 0 || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || file.Contains('/') || file.Contains('\\'))
            throw new Exception($"{name}'s download page didn't say which installer to use");
        var setup = await DownloadAsync(http, feed + Uri.EscapeDataString(file), file, name, progress, cancel);
        progress?.Report((1, "Checking the download…"));
        string actual;
        try
        {
            await using (var fs = File.OpenRead(setup)) actual = Convert.ToBase64String(await SHA512.HashDataAsync(fs, cancel));
        }
        catch { TryDelete(setup); throw; }
        if (actual != sha) { TryDelete(setup); throw new Exception($"The {name} download was damaged. Try again."); }
        await RunInstallerAsync(setup, args, name, progress, installed, cancel);
    }

    static async Task InstallFromGitHubAsync(string repo, Func<string, bool> pick, string name, IProgress<(double, string)> progress, Func<bool> installed, CancellationToken cancel)
    {
        using var http = NewHttp();
        progress?.Report((0, $"Finding the latest {name}…"));
        using var doc = JsonDocument.Parse(await GetTextAsync(http, $"https://api.github.com/repos/{repo}/releases/latest", name, cancel));
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => pick(a.GetProperty("name").GetString() ?? ""));
        if (asset.ValueKind != JsonValueKind.Object) throw new Exception($"Couldn't find the Windows installer for {name}");
        var file = asset.GetProperty("name").GetString();
        var setup = await DownloadAsync(http, asset.GetProperty("browser_download_url").GetString(), file, name, progress, cancel);
        if (asset.TryGetProperty("size", out var size) && new FileInfo(setup).Length != size.GetInt64())
        {
            TryDelete(setup);
            throw new Exception($"The {name} download was incomplete. Try again.");
        }
        await RunInstallerAsync(setup, "", name, progress, installed, cancel);
    }

    // A download that stops sending data for this long is given up on (HttpClient.Timeout doesn't cover a stalled body).
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

    // Small text files (release info, latest.yml): bounded, and cancellable.
    static async Task<string> GetTextAsync(HttpClient http, string url, string name, CancellationToken cancel)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(StallTimeout);
        try { return await http.GetStringAsync(url, cts.Token); }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new TimeoutException($"{name}'s download page didn't answer. Check your internet connection and try again.");
        }
    }

    static async Task<string> DownloadAsync(HttpClient http, string url, string file, string name, IProgress<(double, string)> progress, CancellationToken cancel)
    {
        // Its own folder each time: a cancelled installer may still have the last download open.
        var dir = Path.Combine(Path.GetTempPath(), "giftdeck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, file);
        int shown = -1;
        await DownloadFileAsync(http, url, setup, (done, total) =>
        {
            int pct = total > 0 ? (int)(done * 100 / total) : -1;
            if (pct >= 0 && pct != shown) { shown = pct; progress?.Report((pct / 100.0, $"Downloading {name}… {pct}% of {total / 1048576} MB")); }
        }, cancel);
        return setup;
    }

    // Downloads url to path. Gives up if no data arrives for StallTimeout; on any failure or cancel the partial file is deleted.
    // progress gets (bytes so far, total bytes or 0 if unknown).
    public static async Task DownloadFileAsync(HttpClient http, string url, string path, Action<long, long> progress, CancellationToken cancel)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(stall.Token);
            await using var dst = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, stall.Token)) > 0)
            {
                stall.CancelAfter(StallTimeout); // data arrived: restart the watchdog
                await dst.WriteAsync(buffer.AsMemory(0, n), stall.Token);
                done += n;
                progress?.Invoke(done, total);
            }
        }
        catch (Exception e)
        {
            TryDelete(path); // the streams above are already closed here
            if (e is OperationCanceledException && !cancel.IsCancellationRequested)
                throw new TimeoutException("The download stopped: nothing arrived for 30 seconds. Check your internet connection and try again.", e);
            throw;
        }
    }

    // The installer shows its own windows (and Windows may ask for permission); GiftDeck waits for it to finish.
    // Cancelling only stops the waiting: the installer's window is the user's to finish or close.
    static async Task RunInstallerAsync(string setup, string args, string name, IProgress<(double, string)> progress, Func<bool> installed, CancellationToken cancel)
    {
        progress?.Report((1, $"Installing {name}: follow the installer's window…"));
        try
        {
            using var p = Process.Start(new ProcessStartInfo(setup, args) { UseShellExecute = true });
            if (p != null) await p.WaitForExitAsync(cancel);
        }
        catch (System.ComponentModel.Win32Exception) { throw new Exception($"The {name} installer was cancelled."); }
        finally { _ = Task.Delay(5000).ContinueWith(_ => TryDelete(setup)); }
        // Some installers hand over to a second process and exit early: give the files a moment to appear.
        for (int i = 0; i < 20 && !installed(); i++) await Task.Delay(500, cancel);
        if (!installed()) throw new Exception($"{name} doesn't look installed yet. Finish its installer, then press Check again.");
        Log.Write(name + " installed");
    }

    static void TryDelete(string f) { try { File.Delete(f); } catch { } }
}
