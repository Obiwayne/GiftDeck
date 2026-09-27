using GiftDeck.Models;

namespace GiftDeck.Services;

// One place that owns every service, so views and actions can reach them.
public static class Hub
{
    public static AppSettings Settings { get; private set; } = new AppSettings();
    public static GiftCatalog Gifts { get; private set; }
    public static TikFinityService TikFinity { get; private set; }
    public static ObsService Obs { get; private set; }
    public static ObsHost Engine { get; private set; }
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
    public static LivePageReader PageReader { get; private set; }
    public static GameLinkService GameLink { get; private set; }
    public static GamePackService Packs { get; private set; }
    public static SpinnerService Spinners { get; private set; }
    public static AlertService Alerts { get; private set; }

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

        GameLink = new GameLinkService();
        GameLink.Start();
        Packs = new GamePackService();
        Packs.Load();
        Spinners = new SpinnerService();
        Alerts = new AlertService();

        Obs = new ObsService();
        Engine = new ObsHost();
        // "Set up portrait OBS": convert the user's own collection/profile (remembered by the engine
        // just before it closes OBS) into the portrait setup the hidden engine runs.
        ObsHost.PortraitConverter = basicDir =>
        {
            var plan = ObsPortraitSetup.Plan(basicDir, Settings.ObsRestoreCollection, Settings.ObsRestoreProfile);
            ObsPortraitSetup.Apply(plan, basicDir);
            Log.Write("Portrait OBS setup created: " + plan.Summary.Replace(Environment.NewLine, " "));
            foreach (var w in plan.Warnings) Log.Write("Portrait setup note: " + w);
        };
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

        PageReader = new LivePageReader(); // started once the main window is up (its TikTok page belongs to it)

        TikFinity = new TikFinityService();
        TikFinity.GiftSeen += g => Gifts.Learn(g);
        TikFinity.EventReceived += e => Rules.Handle(e);

        Log.Write("GiftDeck started");
        Web.Start();
        TikFinity.Start();
        Obs.StartAutoConnect();
        Engine.OnAppStart(); // managed OBS: starts it hidden in the background
    }

    public static void SaveSettings()
    {
        Storage.Save("settings.json", Settings);
    }

    static bool _servicesStopped;

    // The slow part of closing (up to ~45 s while OBS quits). Touches no windows, so the main window runs it
    // on the pool and shows each step; step gets plain words for the "Shutting down" screen.
    public static void StopServices(Action<string> step = null)
    {
        if (_servicesStopped) return;
        _servicesStopped = true;
        step ??= _ => { };
        step("Saving your settings…");
        try { SaveSettings(); } catch { }
        try { Profiles.SaveActive(); } catch { }
        try { Web?.Stop(); } catch { }
        try { GameLink?.Stop(); } catch { }
        step("Stopping TikFinity and the TikTok connection…");
        try { TikFinity?.Stop(); } catch { }
        try { TikFinity?.CloseIfHidden(); } catch { }
        try { Bridge?.Stop(); } catch { }
        step("Closing OBS and putting your own OBS setup back…");
        try { Engine?.OnAppExit(); } catch { } // needs the OBS connection (is it live?), so before disconnecting; at most 45s
        try { Obs?.DisconnectAsync().Wait(1000); } catch { }
        step("Done");
    }

    public static void Shutdown()
    {
        StopServices(); // already done when the window closed normally
        try { SaveSettings(); } catch { }
        try { Tts?.Stop(); } catch { }
        try { Music?.Shutdown(); } catch { }
        Log.Write("GiftDeck closed");
    }
}
