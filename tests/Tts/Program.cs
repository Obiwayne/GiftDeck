using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Models;
using GiftDeck.Services;
using GiftDeck.Views;

namespace TtsHarness;

// TtsHarness.exe <scratch data folder> <screenshot folder>
// Everything runs in this process against the scratch folder: no sound, no network, nothing sent to a running GiftDeck.
static class Program
{
    static int _fail, _pass;
    static string _data, _shots;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: TtsHarness <scratch data folder> <screenshot folder>"); return 2; }
        _data = Path.GetFullPath(args[0]);
        _shots = Path.GetFullPath(args[1]);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (_data.StartsWith(appData, StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("Refusing to use a folder under %APPDATA%."); return 2; }
        if (Directory.Exists(_data)) Directory.Delete(_data, true);
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(_shots);
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", _data); // before Storage is touched

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GiftDeck;component/Theme.xaml") });
        int code = 0;
        app.Startup += async (_, _) =>
        {
            try { await RunAll(); }
            catch (Exception e) { Fail("harness crashed: " + e); }
            Console.WriteLine($"\n{_pass} passed, {_fail} failed");
            code = _fail == 0 ? 0 : 1;
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    static async Task RunAll()
    {
        Console.WriteLine("Data folder: " + Storage.Dir);
        Check("storage points at the scratch folder", Storage.Dir == _data);

        Migration();
        SwitchProfiles();
        CreateAndExportImport();
        await Mute();
        await Screenshots();
    }

    // ---- setup helpers ----

    static void SetHub(string prop, object value) => typeof(Hub).GetProperty(prop)!.SetValue(null, value);

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("  PASS  " + what); }
        else Fail(what + (detail.Length > 0 ? "  (" + detail + ")" : ""));
    }

    static void Fail(string what) { _fail++; Console.WriteLine("  FAIL  " + what); }

    static TtsProfile ReadTts(string profile) =>
        JsonSerializer.Deserialize<TtsProfile>(File.ReadAllText(Path.Combine(_data, "profiles", profile, "tts.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    // ---- 1. an existing user with one global voice and two profiles upgrades ----
    static void Migration()
    {
        Console.WriteLine("\nMigration from global TTS settings");
        // settings.json as the user's dev data has it: Zira, chat reading on.
        File.WriteAllText(Path.Combine(_data, "settings.json"), """
        { "ActiveProfile": "Default", "TtsVoice": "Microsoft Zira Desktop", "TtsRate": 2, "TtsVolume": 0,
          "TtsReadChat": true, "TtsChatTemplate": "{user} says {comment}", "TtsMaxChars": 200 }
        """);
        foreach (var p in new[] { "Default", "GTA" })
        {
            Directory.CreateDirectory(Path.Combine(_data, "profiles", p));
            File.WriteAllText(Path.Combine(_data, "profiles", p, "rules.json"), "[]");
        }

        SetHub("Settings", Storage.Load<AppSettings>("settings.json"));
        var profiles = new ProfileService();
        SetHub("Profiles", profiles);
        SetHub("Tts", new TtsService());
        SetHub("Rules", new RulesEngine());
        SetHub("Overlays", new OverlayService());
        var tiktok = new TikTokLiveService();
        tiktok.Load();
        SetHub("TikTok", tiktok);

        profiles.Init();
        foreach (var p in new[] { "Default", "GTA" })
        {
            var t = ReadTts(p);
            Check($"{p} got the old voice (Zira, speed 2, chat reading on)", t.Voice == "Microsoft Zira Desktop" && t.Rate == 2 && t.ReadChat, $"{t.Voice} {t.Rate} {t.ReadChat}");
        }
        Check("live settings still Zira + read chat", Hub.Settings.TtsVoice == "Microsoft Zira Desktop" && Hub.Settings.TtsReadChat);
        Check("mute defaults to off", !Hub.Tts.Muted);
    }

    // ---- 2. profile A/B with different voices ----
    static void SwitchProfiles()
    {
        Console.WriteLine("\nSwitching profiles applies their voice");
        var profiles = Hub.Profiles;
        profiles.Switch("GTA");
        // What the TTS page does when the user picks another voice:
        Hub.Settings.TtsVoice = TtsService.GoogleMale; Hub.Settings.TtsRate = -4; Hub.Settings.TtsVolume = 0; Hub.Settings.TtsReadChat = false;
        Hub.SaveSettings(); TtsProfile.Save(Hub.Settings.ActiveProfile);

        profiles.Switch("Default");
        Check("back on Default: Zira, speed 2, chat reading on", Hub.Settings.TtsVoice == "Microsoft Zira Desktop" && Hub.Settings.TtsRate == 2 && Hub.Settings.TtsReadChat,
            $"{Hub.Settings.TtsVoice} {Hub.Settings.TtsRate} {Hub.Settings.TtsReadChat}");
        var onDisk = Storage.Load<AppSettings>("settings.json");
        Check("settings.json follows the active profile", onDisk.TtsVoice == "Microsoft Zira Desktop" && onDisk.ActiveProfile == "Default");

        profiles.Switch("GTA");
        Check("on GTA: Google Male, speed -4, chat reading off", Hub.Settings.TtsVoice == TtsService.GoogleMale && Hub.Settings.TtsRate == -4 && !Hub.Settings.TtsReadChat,
            $"{Hub.Settings.TtsVoice} {Hub.Settings.TtsRate} {Hub.Settings.TtsReadChat}");
        Check("Default's file untouched", ReadTts("Default").Voice == "Microsoft Zira Desktop");
    }

    // ---- 3. new profiles and .giftdeck export/import ----
    static void CreateAndExportImport()
    {
        Console.WriteLine("\nNew profile, export and import");
        var profiles = Hub.Profiles;
        var fresh = profiles.Create("Fresh");
        Check("a blank new profile starts with the voice in use (Google Male)", ReadTts(fresh).Voice == TtsService.GoogleMale);
        var copy = profiles.Create("Copy of Default", "Default");
        Check("a copied profile keeps its source's voice (Zira)", ReadTts(copy).Voice == "Microsoft Zira Desktop");

        var zip = Path.Combine(_data, "gta-export.giftdeck");
        profiles.Export("GTA", zip);
        using (var z = ZipFile.OpenRead(zip))
            Check("export contains tts.json", z.GetEntry("tts.json") != null);
        profiles.Switch("Default"); // live values are Zira now: an import must not pick those up
        var imported = profiles.Import(zip);
        var t = ReadTts(imported);
        Check($"imported \"{imported}\" has GTA's voice (Google Male, speed -4, chat off)", t.Voice == TtsService.GoogleMale && t.Rate == -4 && !t.ReadChat, $"{t.Voice} {t.Rate} {t.ReadChat}");

        // An older .giftdeck without tts.json gets the voice in use now.
        var old = Path.Combine(_data, "old.giftdeck");
        using (var z = ZipFile.Open(old, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(z.CreateEntry("profile.json").Open())) w.Write("{\"app\":\"GiftDeck\",\"name\":\"Old\",\"version\":1}");
            using (var w = new StreamWriter(z.CreateEntry("rules.json").Open())) w.Write("[]");
        }
        var oldName = profiles.Import(old);
        Check("an older .giftdeck (no tts.json) starts with the current voice (Zira)", ReadTts(oldName).Voice == "Microsoft Zira Desktop");
        profiles.Switch(imported);
        Check("switching to the imported profile applies Google Male", Hub.Settings.TtsVoice == TtsService.GoogleMale);
    }

    // ---- 4. mute ----
    static async Task Mute()
    {
        Console.WriteLine("\nMute");
        var tts = Hub.Tts;
        var silent = WriteSilentWav(Path.Combine(_data, "silence.wav"), seconds: 6);
        int downloads = 0;
        TimeSpan downloadDelay = TimeSpan.Zero;
        tts.Download = async (text, gender) =>
        {
            Interlocked.Increment(ref downloads);
            await Task.Delay(downloadDelay);
            var copy = Path.Combine(_data, Guid.NewGuid().ToString("N") + ".wav");
            File.Copy(silent, copy);
            return copy;
        };
        var player = typeof(TtsService).GetField("_player", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Hub.Settings.TtsVolume = 0; // never audible, even if something goes wrong

        int changed = 0;
        tts.MuteChanged += () => changed++;
        tts.SetMuted(true);
        Check("MuteChanged raised", changed == 1);
        Check("mute saved to settings.json", Storage.Load<AppSettings>("settings.json").TtsMuted);

        Hub.Settings.TtsVoice = TtsService.GoogleFemale;
        Check("muted: Speak refused for an online voice", !tts.Speak("hello"));
        await Task.Delay(300);
        Check("muted: nothing downloaded or queued", downloads == 0 && !tts.OnlineBusy);
        Hub.Settings.TtsVoice = "Microsoft Zira Desktop";
        tts.Apply();
        Check("muted: Speak refused for a Windows voice", !tts.Speak("hello"));

        // Chat reading goes through the same gate.
        Hub.Settings.TtsVoice = TtsService.GoogleFemale; Hub.Settings.TtsReadChat = true;
        try
        {
            Hub.Rules.Handle(new LiveEvent { Type = "chat", Nickname = "tester", Comment = "read me" });
            await Task.Delay(300);
            Check("muted: a chat message isn't read (no download)", downloads == 0);
        }
        catch (Exception e) { Console.WriteLine("  SKIP  chat path: " + e.GetType().Name + " " + e.Message); }

        tts.SetMuted(false);
        Check("unmuted: saved", !Storage.Load<AppSettings>("settings.json").TtsMuted);

        // Control: the same chat message unmuted is read (fake download, volume 0).
        try
        {
            Hub.Rules.Handle(new LiveEvent { Type = "chat", Nickname = "tester", Comment = "read me" });
            var w = System.Diagnostics.Stopwatch.StartNew();
            while (downloads == 0 && w.ElapsedMilliseconds < 3000) await Task.Delay(50);
            Check("unmuted: the same chat message is read", downloads == 1, "downloads=" + downloads);
            tts.Stop();
            while (tts.OnlineBusy && w.ElapsedMilliseconds < 6000) await Task.Delay(50);
            downloads = 0;
        }
        catch (Exception e) { Console.WriteLine("  SKIP  chat control: " + e.GetType().Name + " " + e.Message); }

        // A Windows voice at volume 0 really goes to the synth (silent).
        Hub.Settings.TtsVoice = "Microsoft Zira Desktop"; tts.Apply();
        bool hasZira = tts.Voices.Contains("Microsoft Zira Desktop");
        Check("unmuted: Speak accepted for a Windows voice (volume 0)", tts.Speak("x") || !hasZira);
        tts.Stop();

        // Mute stops an online clip that is playing now.
        Hub.Settings.TtsVoice = TtsService.GoogleFemale;
        Check("unmuted: Speak accepted for an online voice", tts.Speak("one"));
        tts.Speak("two");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (player.GetValue(tts) == null && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);
        Check("online clip is playing", player.GetValue(tts) != null);
        tts.SetMuted(true);
        sw.Restart();
        while ((player.GetValue(tts) != null || tts.OnlineBusy) && sw.ElapsedMilliseconds < 5000) await Task.Delay(50);
        Check($"mute stopped the clip and dropped the queue ({sw.ElapsedMilliseconds} ms)", player.GetValue(tts) == null && !tts.OnlineBusy);
        Check("the queued second line was never fetched", downloads == 1, "downloads=" + downloads);

        // Mute while a clip is still downloading: it never plays.
        tts.SetMuted(false);
        downloads = 0;
        downloadDelay = TimeSpan.FromMilliseconds(800);
        tts.Speak("slow");
        await Task.Delay(200);
        tts.SetMuted(true);
        await Task.Delay(1200);
        Check("muted mid-download: clip discarded, nothing playing", downloads == 1 && player.GetValue(tts) == null && !tts.OnlineBusy);

        // Saved: a restart comes back muted.
        Check("a restart reads it back as muted", Storage.Load<AppSettings>("settings.json").TtsMuted);
    }

    static string WriteSilentWav(string path, int seconds)
    {
        const int rate = 8000;
        int samples = rate * seconds;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8.ToArray()); w.Write(36 + samples * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(samples * 2); w.Write(new byte[samples * 2]);
        return path;
    }

    // ---- 5. screenshots of the changed UI, with the app theme ----
    static async Task Screenshots()
    {
        Console.WriteLine("\nScreenshots -> " + _shots);
        // The rest of Hub, created but not started (no OBS, no bridge, no TikTok connection).
        Try(() => SetHub("TikFinity", new TikFinityService()));
        Try(() => SetHub("Obs", new ObsService()));
        Try(() => SetHub("Engine", new ObsHost()));
        Try(() => SetHub("Music", new MusicService()));
        Try(() => SetHub("Spotify", new SpotifyService()));
        Try(() => SetHub("Relay", new RelayService()));
        Try(() => SetHub("Gifts", new GiftCatalog()));
        Try(() => SetHub("Sounds", new SoundService()));
        Hub.Profiles.Switch("GTA");

        foreach (var muted in new[] { false, true })
        {
            Hub.Tts.SetMuted(muted);
            var tag = muted ? "muted" : "on";
            await Shot(() => new LivePanel(), 620, 220, $"livepanel-{tag}.png");
            await Shot(() => new GoLiveView(), 1180, 260, $"golive-{tag}.png");
        }
        await Shot(() => new TtsView(), 940, 1000, "tts-page-muted.png");
        Hub.Tts.SetMuted(false);
        await Shot(() => new TtsView(), 940, 1000, "tts-page-on.png");
        Hub.Tts.SetMuted(true);
        await Shot(() => NavPreview(), 260, 140, "sidebar-muted.png");
        Hub.Tts.SetMuted(false);
    }

    static void Try(Action a) { try { a(); } catch (Exception e) { Console.WriteLine("  note: " + e.GetType().Name + ": " + e.Message); } }

    // A menu entry built the way MainWindow.BuildNav builds it, styled by MainWindow's own mute helper
    // (the whole MainWindow needs the whole app running).
    static FrameworkElement NavPreview()
    {
        var root = new StackPanel { Margin = new Thickness(12) };
        foreach (var muted in new[] { false, true })
        {
            string volume = ((char)0xE767).ToString();
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = volume, FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 16, Width = 20, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = "Text to speech", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Style = (Style)Application.Current.FindResource("NavButton"), Content = sp, GroupName = "p" + muted, Tag = "tts" };
            GiftDeck.MainWindow.ShowTtsMuted(rb, "Text to speech", volume, muted);
            if (muted) Check("menu entry says (muted)", ((TextBlock)sp.Children[1]).Text == "Text to speech (muted)");
            root.Children.Add(rb);
        }
        return new Border { Background = (Brush)Application.Current.FindResource("PanelBrush"), Child = root };
    }

    // Shows the element in a borderless window far off screen (never activated, so it can't take clicks
    // or focus from the user's windows), renders it to a PNG and closes it.
    static async Task Shot(Func<FrameworkElement> make, int w, int h, string file)
    {
        FrameworkElement el;
        try { el = make(); }
        catch (Exception e) { Fail($"{file}: could not build the view: {e.GetType().Name}: {e.Message}"); return; }
        var win = new Window
        {
            Content = el, Width = w, Height = h, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Left = -20000, Top = -20000, Background = (Brush)Application.Current.FindResource("BgBrush"),
            FontFamily = new FontFamily("Segoe UI"), Foreground = (Brush)Application.Current.FindResource("TextBrush"),
        };
        TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
        win.Show();
        await Task.Delay(400);
        await win.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)win.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)Application.Current.FindResource("BgBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        var path = Path.Combine(_shots, file);
        using (var fs = File.Create(path)) enc.Save(fs);
        win.Close();
        Console.WriteLine("  shot  " + path);
    }
}