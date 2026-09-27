using System.Net.Http;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Media;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TtsHarness")]

namespace GiftDeck.Services;

// Text to speech using the voices installed in Windows, plus two free online voices
// (the same Google speech endpoint TikFinity uses for its free male/female voices).
public class TtsService
{
    public const string GoogleMale = "Google Male (free, online)";
    public const string GoogleFemale = "Google Female (free, online)";

    // The Google speech API key is not in the source: it comes from Settings (Text to speech page)
    // or the GIFTDECK_GOOGLE_TTS_KEY environment variable. Without one the Google voices are skipped.
    const string GoogleUrl = "https://www.google.com/speech-api/v2/synthesize";
    static string GoogleKey
    {
        get
        {
            var key = Hub.Settings.GoogleTtsKey;
            if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("GIFTDECK_GOOGLE_TTS_KEY");
            return key?.Trim() ?? "";
        }
    }

    static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

    SpeechSynthesizer _synth;
    readonly object _lock = new object();

    // Online voices play one after another, like the Windows synth's own queue.
    readonly Queue<(string text, string gender)> _online = new Queue<(string, string)>();
    bool _onlineRunning;
    int _onlineGeneration;
    MediaPlayer _player;
    Action _finishPlayer;

    public static bool IsOnline(string voice) => voice == GoogleMale || voice == GoogleFemale;

    // Quick mute (Live panel, Go LIVE): stops what's speaking and skips everything until unmuted.
    // Saved in settings.json so a restart mid-stream doesn't suddenly start talking again;
    // the menu and the Text to speech page say "muted" so it can't stay off unnoticed.
    public event Action MuteChanged;
    public bool Muted => Hub.Settings.TtsMuted;

    public void SetMuted(bool muted)
    {
        if (muted) Stop();
        if (Hub.Settings.TtsMuted == muted) return;
        Hub.Settings.TtsMuted = muted;
        Hub.SaveSettings();
        Log.Write(muted ? "Text to speech muted" : "Text to speech unmuted");
        MuteChanged?.Invoke();
    }

    public void ToggleMute() => SetMuted(!Muted);

    // True while an online voice is downloading, playing or waiting its turn.
    public bool OnlineBusy { get { lock (_online) return _onlineRunning || _player != null; } }

    // The harness (tests\Tts) swaps this so it needs neither the network nor the speakers.
    internal Func<string, string, Task<string>> Download = DownloadAsync;

    public List<string> Voices
    {
        get
        {
            var list = new List<string> { GoogleMale, GoogleFemale };
            try { list.AddRange(Synth().GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name)); }
            catch { }
            return list;
        }
    }

    SpeechSynthesizer Synth()
    {
        lock (_lock)
        {
            if (_synth == null)
            {
                _synth = new SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
            }
            return _synth;
        }
    }

    public void Apply()
    {
        try
        {
            var s = Synth();
            var voice = Hub.Settings.TtsVoice;
            if (!string.IsNullOrEmpty(voice) && !IsOnline(voice))
            {
                try { s.SelectVoice(voice); }
                catch { Log.Write("TTS voice not available: " + voice); }
            }
            s.Rate = Math.Clamp(Hub.Settings.TtsRate, -10, 10);
            s.Volume = Math.Clamp(Hub.Settings.TtsVolume, 0, 100);
        }
        catch (Exception e)
        {
            Log.Write("TTS setup failed: " + e.Message);
        }
    }

    // False when nothing was queued (muted, or no text).
    public bool Speak(string text)
    {
        if (Muted || string.IsNullOrWhiteSpace(text)) return false;
        int max = Math.Max(20, Hub.Settings.TtsMaxChars);
        if (text.Length > max) text = text.Substring(0, max);

        var voice = Hub.Settings.TtsVoice;
        if (IsOnline(voice))
        {
            SpeakOnline(text, voice == GoogleMale ? "male" : "female");
            return true;
        }
        try { Synth().SpeakAsync(text); return true; }
        catch (Exception e) { Log.Write("TTS failed: " + e.Message); return false; }
    }

    void SpeakOnline(string text, string gender)
    {
        lock (_online)
        {
            _online.Enqueue((text, gender));
            if (_onlineRunning) return;
            _onlineRunning = true;
        }
        _ = Task.Run(RunOnlineQueue);
    }

    async Task RunOnlineQueue()
    {
        while (true)
        {
            (string text, string gender) item;
            int gen;
            lock (_online)
            {
                if (_online.Count == 0) { _onlineRunning = false; return; }
                item = _online.Dequeue();
                gen = _onlineGeneration;
            }
            try
            {
                var file = await Download(item.text, item.gender);
                lock (_online) if (gen != _onlineGeneration || Muted) { TryDelete(file); continue; }
                await PlayAsync(file, gen);
                TryDelete(file);
            }
            catch (Exception e)
            {
                Log.Write("TTS (online) failed: " + e.Message);
            }
        }
    }

    static async Task<string> DownloadAsync(string text, string gender)
    {
        // TikFinity's speed/pitch are 0..1 with 0.5 as normal; map GiftDeck's -10..10 rate onto 0.1..0.9.
        var key = GoogleKey;
        if (key.Length == 0) throw new InvalidOperationException("no Google speech API key set (Text to speech page)");
        double speed = 0.5 + Math.Clamp(Hub.Settings.TtsRate, -10, 10) * 0.04;
        var query = new Dictionary<string, string>
        {
            ["key"] = key,
            ["enc"] = "mpeg",
            ["lang"] = "en-US",
            ["text"] = text,
            ["speed"] = speed.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["pitch"] = "0.50",
            ["rate"] = "48000",
            ["gender"] = gender,
        };
        var url = GoogleUrl + "?" + string.Join("&", query.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));
        var bytes = await Http.GetByteArrayAsync(url);
        var dir = Path.Combine(Path.GetTempPath(), "GiftDeck-tts");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp3");
        await File.WriteAllBytesAsync(file, bytes);
        return file;
    }

    Task PlayAsync(string file, int gen)
    {
        var done = new TaskCompletionSource();
        var app = Application.Current;
        if (app == null) { done.SetResult(); return done.Task; }
        app.Dispatcher.BeginInvoke(() =>
        {
            // Stopped or muted between the download and now: don't start it.
            lock (_online) if (gen != _onlineGeneration || Muted) { done.TrySetResult(); return; }
            var p = new MediaPlayer();
            void Finish() { p.Close(); if (_player == p) { _player = null; _finishPlayer = null; } done.TrySetResult(); }
            p.MediaEnded += (s, e) => Finish();
            p.MediaFailed += (s, e) => { Log.Write("TTS (online) playback failed: " + e.ErrorException.Message); Finish(); };
            p.Open(new Uri(file, UriKind.Absolute));
            p.Volume = Math.Clamp(Hub.Settings.TtsVolume, 0, 100) / 100.0;
            _player = p;
            _finishPlayer = Finish;
            p.Play();
        });
        // A clip that never reports its end must not block the queue forever.
        return Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(60)));
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { }
    }

    public void Stop()
    {
        try { Synth().SpeakAsyncCancelAll(); } catch { }
        lock (_online)
        {
            _online.Clear();
            _onlineGeneration++;
        }
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var p = _player;
            if (p == null) return;
            try { p.Stop(); } catch { }
            _finishPlayer?.Invoke();
        });
    }
}
