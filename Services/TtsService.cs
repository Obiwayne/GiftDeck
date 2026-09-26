using System.Speech.Synthesis;

namespace GiftDeck.Services;

// Text to speech using the voices installed in Windows.
public class TtsService
{
    SpeechSynthesizer _synth;
    readonly object _lock = new object();

    public List<string> Voices
    {
        get
        {
            try { return Synth().GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo.Name).ToList(); }
            catch { return new List<string>(); }
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
            if (!string.IsNullOrEmpty(voice))
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
        try { Synth().SpeakAsync(text); }
        catch (Exception e) { Log.Write("TTS failed: " + e.Message); }
    }

    public void Stop()
    {
        try { Synth().SpeakAsyncCancelAll(); } catch { }
    }
}
