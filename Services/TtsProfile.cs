using GiftDeck.Models;

namespace GiftDeck.Services;

// Each profile keeps its own text-to-speech voice, speed, volume and chat reading on/off, in
// profiles\<name>\tts.json. The live values stay in AppSettings (Hub.Settings.TtsVoice etc.), so the
// rest of GiftDeck reads them as before: switching profile copies the profile's values in.
// Mute, the chat template and the cut-off length are the same for every profile.
public class TtsProfile
{
    public const string FileName = "tts.json";

    public string Voice { get; set; } = "";
    public int Rate { get; set; } = 0;
    public int Volume { get; set; } = 100;
    public bool ReadChat { get; set; } = false;

    static string PathIn(string profile) => Path.Combine("profiles", profile, FileName);

    public static TtsProfile FromSettings(AppSettings s) =>
        new TtsProfile { Voice = s.TtsVoice ?? "", Rate = s.TtsRate, Volume = s.TtsVolume, ReadChat = s.TtsReadChat };

    public void ApplyTo(AppSettings s)
    {
        s.TtsVoice = Voice ?? "";
        s.TtsRate = Math.Clamp(Rate, -10, 10);
        s.TtsVolume = Math.Clamp(Volume, 0, 100);
        s.TtsReadChat = ReadChat;
    }

    // Writes the current (live) values as this profile's.
    public static void Save(string profile)
    {
        if (string.IsNullOrEmpty(profile)) return;
        Storage.Save(PathIn(profile), FromSettings(Hub.Settings));
    }

    // Makes this profile's values the live ones. A profile without tts.json keeps the current values
    // (and gets them written, so it has its own from now on).
    public static void Load(string profile)
    {
        if (string.IsNullOrEmpty(profile)) return;
        var t = Storage.Load<TtsProfile>(PathIn(profile));
        if (t == null) { Save(profile); return; }
        t.ApplyTo(Hub.Settings);
        Hub.SaveSettings();
        Hub.Tts?.Apply();
    }

    // Upgrade from one global voice: every profile without its own settings starts with the current ones,
    // so nobody loses their voice. Runs once per profile (after that each has tts.json).
    public static void Migrate(IEnumerable<string> profiles)
    {
        int n = 0;
        foreach (var p in profiles)
        {
            if (File.Exists(Storage.PathFor(PathIn(p)))) continue;
            Save(p);
            n++;
        }
        if (n > 0) Log.Write($"Text to speech settings are now per profile ({n} profile{(n == 1 ? "" : "s")} got your current voice)");
    }
}
