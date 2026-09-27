using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace GiftDeck.Services;

// Runs OBS as GiftDeck's hidden streaming engine: started in the tray on the "GiftDeck Portrait" collection and
// profile (1080x1920), closed again when GiftDeck closes, and the user's own collection and profile put back
// so their normal OBS (and the stable GiftDeck) open exactly as before.
public class ObsHost
{
    public const string PortraitCollection = "GiftDeck Portrait";
    public const string PortraitProfile = "GiftDeck Portrait";

    // Writes the portrait collection and profile from the user's setup, given OBS's "basic" folder, with OBS closed.
    // Set by whoever owns the converter (ObsPortraitSetup); null means Set up portrait OBS can't run yet.
    public static Action<string> PortraitConverter { get; set; }

    public event Action StatusChanged;

    public string LastError { get; private set; }

    Process _started; // the OBS this GiftDeck launched, if it's still that one

    public bool StartedByGiftDeck
    {
        get { try { return _started != null && !_started.HasExited; } catch { return false; } }
    }

    public static Process FindRunning() => Process.GetProcessesByName("obs64").FirstOrDefault();
    public static bool IsRunning => FindRunning() != null;

    public static bool PortraitExists() =>
        ObsConfig.FindCollectionFile(ObsConfig.Locate().ScenesDir, PortraitCollection) != null;

    // For the Stream Setup card.
    public (bool running, bool visible, bool ours) State()
    {
        var p = FindRunning();
        if (p == null) return (false, false, false);
        var w = MainWindow(p.Id);
        return (true, w != IntPtr.Zero && IsWindowVisible(w), StartedByGiftDeck);
    }

    // ---------- Start ----------

    // Starts OBS hidden on the given collection and profile, then waits until GiftDeck is connected to it.
    public async Task StartHiddenAsync(string collection, string profile, int timeoutMs = 60000)
    {
        if (IsRunning)
        {
            if (!Hub.Obs.Connected) try { await Hub.Obs.ConnectAsync(true); } catch { }
            var current = await CurrentNamesAsync();
            if (current.collection == collection) return; // already running on it
            throw new InvalidOperationException(current.collection == null
                ? "OBS is already open. Close it first, then try again."
                : $"OBS is already open on \"{current.collection}\". Close it first, then try again.");
        }

        var exe = ObsConfig.FindExe() ?? throw new FileNotFoundException("OBS Studio isn't installed (obs64.exe wasn't found).");
        if (collection == PortraitCollection) RememberFromIni(); // OBS is closed, so its files say what the user last used

        var psi = ObsConfig.BuildStartInfo(exe, collection, profile);
        Log.Write($"Starting OBS hidden: collection \"{collection}\", profile \"{profile}\"");
        _started = Process.Start(psi);
        Notify();

        var until = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < until)
        {
            if (_started.HasExited) throw new Exception($"OBS closed right after starting (exit code {_started.ExitCode}).");
            if (!Hub.Obs.Connected) try { await Hub.Obs.ConnectAsync(true); } catch { }
            if (Hub.Obs.Connected) { LastError = null; Notify(); return; }
            await Task.Delay(1000);
        }
        throw new TimeoutException("OBS started, but GiftDeck couldn't connect to it. Check OBS's WebSocket server is on (Tools, WebSocket Server Settings).");
    }

    public Task StartPortraitAsync() => StartHiddenAsync(PortraitCollection, PortraitProfile);

    // ---------- Stop ----------

    // Why OBS shouldn't be closed right now (live, recording, ...), or null when it's safe.
    public async Task<string> BusyReasonAsync()
    {
        if (!IsRunning) return null;
        if (!Hub.Obs.Connected) try { await Hub.Obs.ConnectAsync(true); } catch { }
        if (!Hub.Obs.Connected) return "not answering GiftDeck, so it can't tell whether OBS is live";

        async Task<bool> Active(string request)
        {
            try
            {
                var r = await Hub.Obs.RequestAsync(request);
                return r.TryGetProperty("outputActive", out var a) && a.GetBoolean();
            }
            catch { return false; } // e.g. no replay buffer or virtual camera configured
        }
        async Task<bool> AitumActive()
        {
            try
            {
                var r = await Hub.Obs.RequestAsync("CallVendorRequest", new { vendorName = "aitum-stream-suite", requestType = "get_outputs", requestData = new { } });
                return r.GetProperty("responseData").GetProperty("outputs").EnumerateArray()
                        .Any(o => o.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True);
            }
            catch { return false; } // Aitum isn't installed
        }

        // All at once, so a slow OBS costs one timeout, not five.
        var stream = Active("GetStreamStatus");
        var record = Active("GetRecordStatus");
        var replay = Active("GetReplayBufferStatus");
        var vcam = Active("GetVirtualCamStatus");
        var aitum = AitumActive();
        await Task.WhenAll(stream, record, replay, vcam, aitum);
        if (stream.Result || aitum.Result) return "streaming";
        if (record.Result) return "recording";
        if (replay.Result) return "running its replay buffer";
        if (vcam.Result) return "running its virtual camera";
        return null;
    }

    // Closes OBS the way its own window does (so it saves and exits cleanly). Refuses while it's live or recording
    // unless forced; force-closes only when allowKill and it hasn't gone after gracefulMs.
    public async Task StopAsync(bool force = false, bool allowKill = true, int gracefulMs = 15000)
    {
        var p = FindRunning();
        if (p == null) return;
        if (!force)
        {
            var busy = await BusyReasonAsync();
            if (busy != null) throw new InvalidOperationException($"OBS is {busy}. Stop that first.");
        }

        DateTime started;
        try { started = p.StartTime; } catch { started = DateTime.Now.AddDays(-1); }

        // OBS in the tray has no visible window, so Process.CloseMainWindow finds nothing. Its (hidden) main window
        // still takes WM_CLOSE, which is what clicking X does. obs-websocket has no "quit" request.
        Log.Write("Closing OBS");
        var windows = ObsConfig.MainWindows(p.Id);
        foreach (var w in windows) PostMessage(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (windows.Count == 0) try { p.CloseMainWindow(); } catch { }

        if (await WaitForExitAsync(p, gracefulMs)) { Closed(); return; }

        if (!allowKill) throw new TimeoutException("OBS didn't close. Close it yourself, then try again.");
        Log.Write($"OBS didn't close within {gracefulMs / 1000}s; ending it");
        try { p.Kill(); } catch { }
        await WaitForExitAsync(p, 5000);
        for (int i = 0; i < 20 && IsRunning; i++) await Task.Delay(250);

        // A force-closed OBS 32 would ask about Safe Mode on the next start (with the WebSocket off, so GiftDeck
        // couldn't reach it). Remove only the marker of the run we just ended, and only if no other OBS is open.
        if (!IsRunning)
            foreach (var f in ObsConfig.SentinelsSince(ObsConfig.Locate().SentinelDir, started.AddSeconds(-2)))
                try { File.Delete(f); } catch { }
        Closed();
    }

    void Closed()
    {
        _started = null;
        _ = Hub.Obs.DisconnectAsync();
        Notify();
    }

    static async Task<bool> WaitForExitAsync(Process p, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        try { await p.WaitForExitAsync(cts.Token); return true; }
        catch (OperationCanceledException) { return false; }
        catch { return true; } // the process is already gone
    }

    // ---------- The user's own collection and profile ----------

    // What OBS will open next time is in its settings file; remember it unless it's already our portrait setup.
    public void RememberFromIni()
    {
        try
        {
            var paths = ObsConfig.Locate();
            if (!File.Exists(paths.UserIni)) return;
            Remember(ObsConfig.ReadLastUsed(paths.UserIni));
        }
        catch (Exception e) { Log.Write("Could not read OBS's last-used setup: " + e.Message); }
    }

    // While OBS is open, ask it (it knows even if its file lags behind), falling back to the file.
    public async Task RememberUserSetupAsync()
    {
        RememberFromIni();
        var (collection, profile) = await CurrentNamesAsync();
        if (collection == null && profile == null) return;
        var paths = ObsConfig.Locate();
        var ini = File.Exists(paths.UserIni) ? ObsConfig.ReadLastUsed(paths.UserIni) : new ObsConfig.LastUsed();
        var used = new ObsConfig.LastUsed { Collection = collection, Profile = profile };
        if (collection != null)
            used.CollectionFile = collection == ini.Collection ? ini.CollectionFile : FileValue(ObsConfig.FindCollectionFile(paths.ScenesDir, collection), ini.CollectionFile);
        if (profile != null)
            used.ProfileDir = profile == ini.Profile ? ini.ProfileDir : Path.GetFileName(ObsConfig.FindProfileDir(paths.ProfilesDir, profile) ?? "");
        Remember(used);
    }

    // Written the way this OBS writes it: with ".json" (OBS 31+) or without (older).
    static string FileValue(string path, string example)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var name = Path.GetFileName(path);
        return example != null && !example.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    void Remember(ObsConfig.LastUsed used)
    {
        var s = Hub.Settings;
        bool changed = false;
        if (!string.IsNullOrEmpty(used.Collection) && used.Collection != PortraitCollection
            && (s.ObsRestoreCollection != used.Collection || s.ObsRestoreCollectionFile != (used.CollectionFile ?? "")))
        {
            s.ObsRestoreCollection = used.Collection;
            s.ObsRestoreCollectionFile = used.CollectionFile ?? "";
            changed = true;
        }
        if (!string.IsNullOrEmpty(used.Profile) && used.Profile != PortraitProfile
            && (s.ObsRestoreProfile != used.Profile || s.ObsRestoreProfileDir != (used.ProfileDir ?? "")))
        {
            s.ObsRestoreProfile = used.Profile;
            s.ObsRestoreProfileDir = used.ProfileDir ?? "";
            changed = true;
        }
        if (changed)
        {
            Hub.SaveSettings();
            Log.Write($"Remembered your OBS setup: collection \"{s.ObsRestoreCollection}\", profile \"{s.ObsRestoreProfile}\"");
        }
    }

    // The running OBS's current collection and profile (nulls when not connected).
    public static async Task<(string collection, string profile)> CurrentNamesAsync()
    {
        if (!Hub.Obs.Connected) return (null, null);
        string c = null, p = null;
        try { c = (await Hub.Obs.RequestAsync("GetSceneCollectionList")).GetProperty("currentSceneCollectionName").GetString(); } catch { }
        try { p = (await Hub.Obs.RequestAsync("GetProfileList")).GetProperty("currentProfileName").GetString(); } catch { }
        return (c, p);
    }

    // With OBS closed, makes the user's collection and profile the ones OBS opens next. Only replaces our portrait
    // values (if the user picked something else since, that stays). Returns what it did, for the log and the card.
    public string RestoreUserSetup()
    {
        if (IsRunning) return "OBS is still open, so its setup wasn't changed (it would overwrite it when it closes).";
        var s = Hub.Settings;
        var paths = ObsConfig.Locate();
        if (!File.Exists(paths.UserIni)) return "OBS's settings file wasn't found.";
        var now = ObsConfig.ReadLastUsed(paths.UserIni);
        var keys = new Dictionary<string, string>();
        if (now.Collection == PortraitCollection && !string.IsNullOrEmpty(s.ObsRestoreCollection))
        {
            keys["SceneCollection"] = s.ObsRestoreCollection;
            if (!string.IsNullOrEmpty(s.ObsRestoreCollectionFile)) keys["SceneCollectionFile"] = s.ObsRestoreCollectionFile;
        }
        if (now.Profile == PortraitProfile && !string.IsNullOrEmpty(s.ObsRestoreProfile))
        {
            keys["Profile"] = s.ObsRestoreProfile;
            if (!string.IsNullOrEmpty(s.ObsRestoreProfileDir)) keys["ProfileDir"] = s.ObsRestoreProfileDir;
        }
        if (keys.Count == 0) return "Nothing to put back.";
        ObsConfig.WriteKeys(paths.UserIni, "Basic", keys);
        var msg = $"OBS will open your own setup again (collection \"{s.ObsRestoreCollection}\", profile \"{s.ObsRestoreProfile}\").";
        Log.Write(msg);
        return msg;
    }

    public Task<string> RestoreUserSetupAsync() => Task.Run(RestoreUserSetup);

    // ---------- Set up portrait OBS ----------

    // Not live -> close OBS -> write the portrait collection and profile -> start OBS hidden on them.
    public async Task SetUpPortraitAsync(Action<string> convertFiles)
    {
        if (convertFiles == null) throw new InvalidOperationException("The portrait setup isn't available in this build yet.");
        if (IsRunning)
        {
            var busy = await BusyReasonAsync();
            if (busy != null) throw new InvalidOperationException($"OBS is {busy}. Set up portrait OBS when you're not live.");
            await RememberUserSetupAsync();
            // The user's own OBS: never force-close it (they may have something open in it).
            await StopAsync(force: true, allowKill: StartedByGiftDeck);
        }
        else RememberFromIni();

        var basic = ObsConfig.Locate().BasicDir;
        Log.Write("Writing the GiftDeck Portrait collection and profile");
        await Task.Run(() => convertFiles(basic));

        Hub.Settings.ObsManaged = true;
        Hub.SaveSettings();
        await StartPortraitAsync();
    }

    // ---------- GiftDeck start and exit ----------

    // Managed: start OBS hidden on the portrait setup, unless OBS is already open (then it's the user's, leave it).
    public void OnAppStart()
    {
        if (!Hub.Settings.ObsManaged) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsRunning) { Log.Write("OBS is already open; GiftDeck won't start its own"); return; }
                if (!PortraitExists()) { Log.Write("OBS engine is on, but there's no GiftDeck Portrait setup yet (Stream Setup, Set up portrait OBS)"); return; }
                await StartPortraitAsync();
            }
            catch (Exception e) { LastError = e.Message; Log.Write("Could not start OBS: " + e.Message); Notify(); }
        });
    }

    // Managed: close the OBS we started (unless it's live) and put the user's setup back. Bounded, so closing
    // GiftDeck never hangs on OBS. Runs on the pool: the UI thread is blocked here.
    public void OnAppExit()
    {
        if (!Hub.Settings.ObsManaged && !StartedByGiftDeck) return; // switched off mid-session: still close the hidden OBS
        var work = Task.Run(async () =>
        {
            if (StartedByGiftDeck)
            {
                var busyTask = BusyReasonAsync();
                var busy = await Task.WhenAny(busyTask, Task.Delay(6000)) == busyTask ? busyTask.Result : "not answering in time";
                if (busy != null) { Log.Write($"OBS left open: it is {busy}"); return; }
                await StopAsync(force: true, allowKill: true, gracefulMs: 30000);
            }
            // A closing (or just-killed) obs64 can linger for a moment; restoring while it's still listed is skipped,
            // so wait for it to be really gone, then say what happened either way.
            for (int i = 0; i < 20 && IsRunning; i++) await Task.Delay(250);
            var result = RestoreUserSetup();
            if (IsRunning) Log.Write("Couldn't put your OBS setup back: " + result);
        });
        try { if (!work.Wait(45000)) Log.Write("Gave up waiting for OBS to close"); }
        catch (Exception e) { Log.Write("Closing OBS failed: " + (e.InnerException?.Message ?? e.Message)); }
    }

    // ---------- Show the hidden window ----------

    // For advanced edits: brings OBS's main window out of the tray.
    public bool ShowWindow()
    {
        var p = FindRunning();
        if (p == null) return false;
        var w = MainWindow(p.Id);
        if (w == IntPtr.Zero) return false;
        ShowWindow(w, SW_SHOW);
        ShowWindow(w, SW_RESTORE);
        SetForegroundWindow(w);
        Notify();
        return true;
    }

    void Notify() { try { StatusChanged?.Invoke(); } catch { } }

    // ---------- Win32 ----------

    static IntPtr MainWindow(int pid) => ObsConfig.MainWindows(pid).FirstOrDefault();

    const uint WM_CLOSE = 0x0010;
    const int SW_SHOW = 5, SW_RESTORE = 9;

    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
}
