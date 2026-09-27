using System.Net.Http;
using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Media;

namespace GiftDeck.Services;

// Text to speech using the voices installed in Windows, plus two free online voices
// (the same Google speech endpoint TikFinity uses for its free male/female voices).
public class TtsService
{
    public const string GoogleMale = "Google Male (free, online)";
    public const string GoogleFemale = "Google Female (free, online)";

    // Chromium's public speech key, the one TikFinity's tts.js sends.
    const string GoogleUrl = "https://www.google.com/speech-api/v2/synthesize";
    const string GoogleKey = "AIzaSyBOti4mM-6x9WDnZIjIeyEU21OpBXqWBgw";

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

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        int max = Math.Max(20, Hub.Settings.TtsMaxChars);
        if (text.Length > max) text = text.Substring(0, max);

        var voice = Hub.Settings.TtsVoice;
        if (IsOnline(voice))
        {
            SpeakOnline(text, voice == GoogleMale ? "male" : "female");
            return;
        }
        try { Synth().SpeakAsync(text); }
        catch (Exception e) { Log.Write("TTS failed: " + e.Message); }
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
                var file = await DownloadAsync(item.text, item.gender);
                lock (_online) if (gen != _onlineGeneration) { TryDelete(file); continue; }
                await PlayAsync(file);
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
        double speed = 0.5 + Math.Clamp(Hub.Settings.TtsRate, -10, 10) * 0.04;
        var query = new Dictionary<string, string>
        {
            ["key"] = GoogleKey,
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

    Task PlayAsync(string file)
    {
        var done = new TaskCompletionSource();
        var app = Application.Current;
        if (app == null) { done.SetResult(); return done.Task; }
        app.Dispatcher.BeginInvoke(() =>
        {
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
