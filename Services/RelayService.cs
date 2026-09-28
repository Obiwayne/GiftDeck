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

    // Waits before each restart after the relay stopped by itself; after the last one GiftDeck gives up.
    static readonly int[] RestartDelays = { 2, 5, 10, 10, 10 };

    Process _process;
    string _lastError;
    string _server, _key;
    readonly object _lock = new object();
    int _generation;      // Stop() moves this on, so a relay from before it stopped on purpose
    bool _keepAlive;      // Go LIVE saw it sending: bring it back if it stops by itself
    int _failures;        // stops by itself in a row (forgotten once it has run a minute)
    DateTime _startedAt;

    public bool Running => _process != null && !_process.HasExited;
    public string LastError => _lastError;

    // What's wrong while the relay is down during a LIVE (null when it's fine). Recovering: GiftDeck is still restarting it.
    public string Problem { get; private set; }
    public bool Recovering { get; private set; }
    public event Action ProblemChanged;

    public void Start(string server, string key)
    {
        lock (_lock)
        {
            Stop();
            _server = server;
            _key = key;
            _failures = 0;
            Launch();
        }
    }

    // on: the relay is sending to the LIVE, so restart it if it stops by itself.
    // off: it's about to be stopped on purpose (Aitum's output is stopped first, which ends ffmpeg too).
    public void KeepAlive(bool on)
    {
        lock (_lock)
        {
            if (!on) _generation++;
            _keepAlive = on;
        }
    }

    void Launch()
    {
        var server = _server;
        var key = _key;
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
        int gen = _generation;
        _process.Exited += (s, e) => OnExited(p, gen);
        _startedAt = DateTime.Now;

        Hub.TikTok.State.RelayPid = _process.Id;
        try { Hub.TikTok.Save(); } catch (Exception e) { Log.Write("Could not save the relay's process id: " + e.Message); }
        Log.Write("Relay started: " + LocalServer + " -> " + server);
    }

    // Stopped by itself while LIVE (TikTok dropped it, ffmpeg crashed, ...): start it again after a short wait.
    void OnExited(Process p, int gen)
    {
        Log.Write($"Relay stopped (exit code {SafeExitCode(p)})");
        int wait;
        lock (_lock)
        {
            if (gen != _generation || !_keepAlive || !Hub.TikTok.Live) return; // stopped on purpose, or the LIVE is over
            if ((DateTime.Now - _startedAt).TotalSeconds > 60) _failures = 0;
            _failures++;
            if (_failures > RestartDelays.Length)
            {
                _keepAlive = false;
                Log.Write("The relay to TikTok keeps stopping; GiftDeck stopped restarting it");
                SetProblem("Not sending: the relay to TikTok keeps stopping, so GiftDeck stopped restarting it. Press Start sending again, or End LIVE." + Why(), false);
                return;
            }
            wait = RestartDelays[_failures - 1];
            Log.Write($"The relay to TikTok stopped while LIVE; restarting it in {wait}s (try {_failures} of {RestartDelays.Length})");
            SetProblem($"The relay to TikTok stopped. GiftDeck is restarting it (try {_failures} of {RestartDelays.Length})…" + Why(), true);
        }
        _ = Task.Run(() => RestartAsync(gen, wait));
    }

    string Why() => string.IsNullOrEmpty(_lastError) ? "" : " Relay: " + _lastError;

    async Task RestartAsync(int gen, int wait)
    {
        await Task.Delay(wait * 1000);
        Process mine = null;
        try
        {
            lock (_lock)
            {
                if (gen != _generation || !_keepAlive || !Hub.TikTok.Live) return;
                Launch();
                mine = _process;
            }
        }
        catch (Exception e)
        {
            Log.Write("Could not restart the relay: " + e.Message);
            lock (_lock)
            {
                if (gen != _generation) return;
                _keepAlive = false;
                SetProblem("Not sending: GiftDeck couldn't restart the relay to TikTok. Press Start sending again, or End LIVE.", false);
            }
            return;
        }

        // Aitum lost its connection when ffmpeg stopped: start its output again, on the new relay.
        var output = Hub.TikTok.State.AitumOutput;
        try
        {
            await Task.Delay(1500); // let ffmpeg start listening before Aitum connects
            if (await Hub.Obs.IsAitumOutputActiveAsync(output) == true) await Hub.Obs.StopAitumOutputAsync(output);
            await Hub.Obs.StartAitumOutputAsync(output);
        }
        catch (Exception e) { Log.Write($"Could not restart Aitum \"{output}\" for the relay: " + e.Message); }

        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(1000);
            if (gen != _generation || mine.HasExited) return; // stopped on purpose, or it stopped again (OnExited carries on)
            bool active;
            try { active = await Hub.Obs.IsAitumOutputActiveAsync(output) == true; } catch { active = false; }
            if (active)
            {
                lock (_lock)
                {
                    if (gen != _generation) return;
                    Log.Write("The relay to TikTok is running again");
                    SetProblem(null, false);
                }
                return;
            }
        }
        // Nothing arrived from OBS: end this try, which counts as another stop.
        Log.Write($"Aitum \"{output}\" didn't reconnect to the restarted relay");
        try { mine.Kill(); } catch { }
    }

    void SetProblem(string problem, bool recovering)
    {
        Problem = problem;
        Recovering = recovering;
        try { ProblemChanged?.Invoke(); } catch { }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _generation++;
            _keepAlive = false;
            try { if (Running) _process.Kill(); } catch { }
            _process = null;
            if (Problem != null || Recovering) SetProblem(null, false);
        }

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
