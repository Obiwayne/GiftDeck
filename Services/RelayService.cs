using System.Diagnostics;

namespace GiftDeck.Services;

// Forwards a local RTMP stream to TikTok with ffmpeg, without re-encoding.
// Aitum's "Vertical Stream" output is set up once to send to LocalServer/LocalKey; each Go LIVE
// then only needs a new relay pointed at that LIVE's server and key, because Aitum's remote
// controls can start and stop an output but cannot change its stream key.
public class RelayService
{
    public const int Port = 1936;
    public const string LocalServer = "rtmp://127.0.0.1:1936/live";
    public const string LocalKey = "giftdeck";

    Process _process;
    string _lastError;

    public bool Running => _process != null && !_process.HasExited;
    public string LastError => _lastError;

    public void Start(string server, string key)
    {
        Stop();
        var ffmpeg = FindFfmpeg() ?? throw new Exception("ffmpeg isn't installed yet. On Stream Setup, click Download ffmpeg.");
        var target = server.TrimEnd('/') + "/" + key;
        _lastError = null;

        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "warning", "-listen", "1", "-i", LocalServer + "/" + LocalKey, "-c", "copy", "-f", "flv", target })
            psi.ArgumentList.Add(a);

        _process = Process.Start(psi);
        _process.EnableRaisingEvents = true;
        _process.ErrorDataReceived += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            var line = e.Data.Replace(key, "***");
            _lastError = line;
            Log.Write("Relay: " + line);
        };
        _process.OutputDataReceived += (s, e) => { };
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();
        var p = _process;
        _process.Exited += (s, e) => Log.Write($"Relay stopped (exit code {SafeExitCode(p)})");

        Hub.TikTok.State.RelayPid = _process.Id;
        Hub.TikTok.Save();
        Log.Write("Relay started: " + LocalServer + " -> " + server);
    }

    public void Stop()
    {
        try { if (Running) _process.Kill(); } catch { }
        _process = null;

        // A relay left behind by an earlier GiftDeck session would still hold the port.
        var pid = Hub.TikTok.State.RelayPid;
        if (pid > 0)
        {
            try
            {
                var old = Process.GetProcessById(pid);
                if (old.ProcessName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)) old.Kill();
            }
            catch { }
            Hub.TikTok.State.RelayPid = 0;
            Hub.TikTok.Save();
        }
    }

    static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    public static string FindFfmpeg()
    {
        if (File.Exists(FfmpegDownloader.InstalledExe)) return FfmpegDownloader.InstalledExe;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (dir.Length > 0 && File.Exists(exe)) return exe;
            }
            catch { }
        }
        // winget installs (PATH may not be updated for an already-running GiftDeck)
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in Directory.GetDirectories(packages, "Gyan.FFmpeg*"))
            {
                var found = Directory.GetFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (found != null) return found;
            }
        }
        return null;
    }
}
