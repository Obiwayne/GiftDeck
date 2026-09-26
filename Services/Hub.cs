using GiftDeck.Models;

namespace GiftDeck.Services;

// One place that owns every service, so views and actions can reach them.
public static class Hub
{
    public static AppSettings Settings { get; private set; } = new AppSettings();
    public static GiftCatalog Gifts { get; private set; }
    public static TikFinityService TikFinity { get; private set; }
    public static ObsService Obs { get; private set; }
    public static SpotifyService Spotify { get; private set; }
    public static TtsService Tts { get; private set; }
    public static SoundService Sounds { get; private set; }
    public static RulesEngine Rules { get; private set; }
    public static OverlayService Overlays { get; private set; }
    public static OverlayServer Web { get; private set; }
    public static TikTokLiveService TikTok { get; private set; }
    public static BridgeService Bridge { get; private set; }
    public static MusicService Music { get; private set; }
    public static RelayService Relay { get; private set; }
    public static ProfileService Profiles { get; private set; }

    public static void Init()
    {
        Settings = Storage.Load<AppSettings>("settings.json") ?? new AppSettings();
        if (string.IsNullOrEmpty(Settings.ObsPassword))
        {
            var obs = ObsService.ReadObsConfig();
            if (obs.found) { Settings.ObsPassword = obs.password; Settings.ObsPort = obs.port; }
        }

        Gifts = new GiftCatalog();
        Gifts.Load();

        Sounds = new SoundService();
        Tts = new TtsService();
        Tts.Apply();

        Profiles = new ProfileService();
        Profiles.Init();

        Rules = new RulesEngine();
        Rules.Load();

        Obs = new ObsService();
        Spotify = new SpotifyService();
        Spotify.Load();

        Overlays = new OverlayService();
        Overlays.Load();
        Rules.Handled += (e, fired) => Overlays.OnEvent(e);
        Web = new OverlayServer(Overlays);

        TikTok = new TikTokLiveService();
        TikTok.Load();
        if (!File.Exists(Storage.PathFor(Profiles.File("stream.json")))) Profiles.SaveActive();

        Music = new MusicService();
        Relay = new RelayService();

        Bridge = new BridgeService();
        Bridge.Start();

        TikFinity = new TikFinityService();
        TikFinity.GiftSeen += g => Gifts.Learn(g);
        TikFinity.EventReceived += e => Rules.Handle(e);

        Log.Write("GiftDeck started");
        Web.Start();
        TikFinity.Start();
        Obs.StartAutoConnect();
    }

    public static void SaveSettings()
    {
        Storage.Save("settings.json", Settings);
    }

    public static void Shutdown()
    {
        try { SaveSettings(); } catch { }
        try { Profiles.SaveActive(); } catch { }
        try { Web?.Stop(); } catch { }
        try { TikFinity?.Stop(); } catch { }
        try { Bridge?.Stop(); } catch { }
        try { Obs?.DisconnectAsync().Wait(1000); } catch { }
        try { Tts?.Stop(); } catch { }
        try { Music?.Shutdown(); } catch { }
        Log.Write("GiftDeck closed");
    }
}
