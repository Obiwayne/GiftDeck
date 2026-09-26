using System.Diagnostics;
using System.Net.NetworkInformation;

namespace GiftDeck.Services;

// Keeps the direct TikTok bridge (bridge\bridge.js, run with Node) going whenever GiftDeck's feed
// address points at it, so the LIVE connection doesn't depend on TikFinity's login.
public class BridgeService
{
    public const int Port = 21214;

    readonly CancellationTokenSource _cts = new CancellationTokenSource();
    Process _process;
    bool _loggedMissing;
    bool _loggedNoUser;

    public static bool InUse => Hub.Settings.TikFinityUrl.Contains(":" + Port);

    public void Start() => _ = Task.Run(Watch);

    public void Stop()
    {
        _cts.Cancel();
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
    }

    // Username changed: stop the bridge; the watcher starts it again for the new account within 10 seconds.
    public void Restart()
    {
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch { }
        _process = null;
    }

    async Task Watch()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (InUse && !Listening()) Launch();
            }
            catch (Exception e) { Log.Write("Bridge: " + e.Message); }
            try { await Task.Delay(10000, _cts.Token); } catch { }
        }
    }

    static bool Listening() =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(ep => ep.Port == Port);

    void Launch()
    {
        var dir = FindBridgeDir();
        var node = dir != null && File.Exists(Path.Combine(dir, "node.exe")) ? Path.Combine(dir, "node.exe") : FindNode();
        if (dir == null || node == null)
        {
            if (!_loggedMissing) Log.Write(dir == null ? "Bridge not found (bridge\\bridge.js next to GiftDeck)" : "Bridge needs Node.js, which was not found (reinstall GiftDeck, or install Node.js)");
            _loggedMissing = true;
            return;
        }
        var user = (Hub.Settings.BridgeUsername ?? "").Trim().TrimStart('@');
        if (user.Length == 0)
        {
            if (!_loggedNoUser) Log.Write("Set your TikTok username on the Stream Setup page so GiftDeck can read your LIVE");
            _loggedNoUser = true;
            return;
        }
        _loggedNoUser = false;
        _process = Process.Start(new ProcessStartInfo(node)
        {
            ArgumentList = { "bridge.js", user, Port.ToString() },
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Log.Write($"Started the TikTok bridge for @{user}");
    }

    // The bridge folder sits in the GiftDeck source folder; walk up from wherever the exe runs (dist or bin).
    static string FindBridgeDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && d != null; i++, d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "bridge");
            if (File.Exists(Path.Combine(candidate, "bridge.js"))) return candidate;
        }
        return null;
    }

    static string FindNode()
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var exe = Path.Combine(p.Trim(), "node.exe");
                if (p.Length > 0 && File.Exists(exe)) return exe;
            }
            catch { }
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
