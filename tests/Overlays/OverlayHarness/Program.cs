using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GiftDeck.Models;
using GiftDeck.Services;

// Screenshots and checks for the all-in-one overlay and green-screen alerts (docs/v2.1/all-in-one-overlay.md).
// Everything runs against a scratch data folder and a spare port; nothing is sent to a running GiftDeck.
static class Program
{
    static int _fail;
    static string _shots;
    static int _port;
    static OverlayService _svc;
    static OverlayServer _web;
    static AlertDef _keyedCard, _keyedFull, _plain;
    static Spinner _spinner;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1) { Console.WriteLine("usage: OverlayHarness <scratch folder> [port]"); return 2; }
        var root = Path.GetFullPath(args[0]);
        _port = args.Length > 1 ? int.Parse(args[1]) : 21391;
        if (_port == 21300) { Console.WriteLine("21300 is the real GiftDeck's port: pick another"); return 2; }
        var data = Path.Combine(root, "data");
        if (Directory.Exists(data)) Directory.Delete(data, true);
        Directory.CreateDirectory(data);
        // Before anything touches Storage: GiftDeck's data goes to the scratch folder, never %APPDATA%.
        Environment.SetEnvironmentVariable("GIFTDECK_DATA", data);
        _shots = Path.Combine(root, "shots");
        Directory.CreateDirectory(_shots);

        var media = MakeMedia(Path.Combine(root, "media"));
        SetUp(media);
        if (Environment.GetEnvironmentVariable("HARNESS_SERVE_ONLY") is { Length: > 0 } secs)
        {
            // Just keep the sample server up for poking at by hand.
            Console.WriteLine($"serving {_web.BaseUrl} for {secs}s; keyed card alert {_keyedCard.Id}, plain {_plain.Id}");
            Thread.Sleep(int.Parse(secs) * 1000);
            _web.Stop();
            return 0;
        }
        RenderWpf();
        try { Task.Run(Browser).GetAwaiter().GetResult(); }
        catch (Exception e) { Console.WriteLine("  FAIL  browser run: " + e); _fail++; }
        _web.Stop();
        Console.WriteLine(_fail == 0 ? "ALL OK" : _fail + " FAILED");
        Console.WriteLine("Screenshots: " + _shots);
        return _fail == 0 ? 0 : 1;
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
        if (!ok) _fail++;
    }

    // ---------------- test clip ----------------

    // A 4 s green-screen clip with a moving colour-bar box, a skin-coloured bar and a 440 Hz tone.
    static string MakeMedia(string dir)
    {
        Directory.CreateDirectory(dir);
        var mp4 = Path.Combine(dir, "green.mp4");
        var webm = Path.Combine(dir, "green.webm");
        if (!File.Exists(mp4))
            Run("ffmpeg", "-hide_banner -loglevel error -y -f lavfi -i color=c=0x00FF00:s=640x360:d=4:r=30 -f lavfi -i testsrc2=s=220x220:d=4:r=30 -f lavfi -i sine=frequency=440:duration=4 "
                + "-filter_complex \"[0][1]overlay=x='210+150*sin(t*2)':y=70,drawbox=x=40:y=260:w=560:h=50:color=0xF0C8A0:t=fill,drawbox=x=60:y=275:w=520:h=20:color=0x202020:t=fill[v]\" "
                + "-map [v] -map 2 -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \"" + mp4 + "\"");
        if (!File.Exists(webm))
            Run("ffmpeg", "-hide_banner -loglevel error -y -i \"" + mp4 + "\" -c:v libvpx-vp9 -b:v 1M -c:a libopus \"" + webm + "\"");
        Check(File.Exists(mp4) && File.Exists(webm), "green-screen test clips made (mp4 + webm)");
        return dir;
    }

    static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
        p.WaitForExit(60000);
    }

    // ---------------- sample profile ----------------

    static void Set(string name, object value) => typeof(Hub).GetProperty(name).SetValue(null, value);

    static void SetUp(string media)
    {
        var profiles = new ProfileService();
        Set("Profiles", profiles);
        profiles.Init();
        var gifts = new GiftCatalog();
        gifts.Load();
        Set("Gifts", gifts);
        var rules = new RulesEngine();
        Set("Rules", rules);
        foreach (var (gift, name) in new[] { ("Rose", "Jump"), ("Finger Heart", "Spawn a car"), ("Doughnut", "Wanted level +1"), ("Galaxy", "Tornado"), ("Lion", "Meteor shower") })
            rules.Rules.Add(new Rule { Name = name, Trigger = new RuleTrigger { Type = TriggerType.Gift, GiftName = gift } });

        _svc = new OverlayService();
        Set("Overlays", _svc);
        _svc.Load();
        var c = _svc.Config;
        c.Port = _port;
        c.Goals.Add(new Goal { Title = "Coin goal", Target = 1000, Progress = 640 });
        c.Goals.Add(new Goal { Title = "Likes", Metric = GoalMetric.Likes, Target = 5000, Progress = 1200 });
        c.Stats.Gifters.Add(new GifterTotal { UserId = "a", Name = "NightOwl", Coins = 1500 });
        c.Stats.Gifters.Add(new GifterTotal { UserId = "b", Name = "Pixel_Queen", Coins = 740 });
        c.Stats.Gifters.Add(new GifterTotal { UserId = "c", Name = "tom", Coins = 99 });
        foreach (var t in new[] { "JUMP", "CAR", "WANTED", "TORNADO", "METEORS" }) c.Menu.Tiles.Add(new MenuTile { Label = t, Subtitle = "gift" });
        _spinner = _svc.AddSpinner();
        _spinner.SpinSeconds = 3;
        _keyedCard = new AlertDef { Name = "Green card", Media = Path.Combine(media, "green.webm"), Text = "{user} summoned a thing!", Seconds = 30, KeyGreen = true };
        _keyedFull = new AlertDef { Name = "Jumpscare", Media = Path.Combine(media, "green.mp4"), Text = "{user} SCARED YOU", Seconds = 30, FullScreen = true, Interrupt = true, KeyGreen = true, KeyColor = "" };
        _plain = new AlertDef { Name = "Plain video", Media = Path.Combine(media, "green.mp4"), Text = "{user} plain video", Seconds = 30 };
        c.CustomAlerts.AddRange(new[] { _keyedCard, _keyedFull, _plain });
        _web = new OverlayServer(_svc);
        Set("Web", _web);
        _web.Start();
        Check(_web.Running, "overlay server started on " + _web.BaseUrl);
    }

    static LiveEvent Viewer(string name) => new LiveEvent { Type = "gift", Nickname = name, GiftName = "Rose", Diamonds = 1, RepeatCount = 5, IsTest = true };

    // ---------------- WPF cards (offscreen, app theme) ----------------

    static void RenderWpf()
    {
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/GiftDeck;component/Theme.xaml") });
        Snap(new GiftDeck.Views.AllInOnePanel(), 1000, "wpf-all-in-one-card.png");
        var tools = new GiftDeck.Views.StreamToolsPanel();
        Snap(tools, 1000, "wpf-stream-tools.png");
    }

    static void Snap(FrameworkElement el, int width, string file)
    {
        var host = new Border { Background = (Brush)Application.Current.Resources["BgBrush"], Padding = new Thickness(24), Child = el, Width = width };
        TextElement.SetForeground(host, (Brush)Application.Current.Resources["TextBrush"]);
        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth), (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var f = File.Create(Path.Combine(_shots, file))) enc.Save(f);
        Check(host.ActualHeight > 100, $"rendered {file} ({host.ActualWidth:0} x {host.ActualHeight:0})");
    }

    // ---------------- headless Edge over DevTools ----------------

    static ClientWebSocket _ws;
    static int _msgId;

    static async Task Browser()
    {
        var edge = new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }.FirstOrDefault(File.Exists);
        if (edge == null) { Check(false, "Edge found"); return; }
        var profile = Path.Combine(Path.GetDirectoryName(_shots), "edge-profile");
        int dbg = _port + 100;
        // Autoplay with sound allowed, like OBS's browser; --mute-audio so the test tone isn't heard on this PC.
        var p = Process.Start(new ProcessStartInfo(edge, $"--headless=new --no-sandbox --disable-extensions --remote-debugging-port={dbg} --user-data-dir=\"{profile}\" --autoplay-policy=no-user-gesture-required --mute-audio --no-first-run --hide-scrollbars {Environment.GetEnvironmentVariable("HARNESS_EDGE_FLAGS")} about:blank") { UseShellExecute = false });
        try
        {
            using var http = new HttpClient();
            string list = null;
            for (int i = 0; i < 50 && list == null; i++)
            {
                try { list = await http.GetStringAsync($"http://127.0.0.1:{dbg}/json/list"); } catch { await Task.Delay(200); }
            }
            var page = JsonNode.Parse(list).AsArray().First(n => (string)n["type"] == "page");
            _ws = new ClientWebSocket();
            _ws.Options.KeepAliveInterval = TimeSpan.Zero;
            await _ws.ConnectAsync(new Uri((string)page["webSocketDebuggerUrl"]), CancellationToken.None);
            await Cdp("Page.enable");
            await Cdp("Runtime.enable");
            await Scenarios();
        }
        finally
        {
            try { p.Kill(true); } catch { }
        }
    }

    static async Task<JsonNode> Cdp(string method, object args = null)
    {
        int id = ++_msgId;
        var msg = JsonSerializer.Serialize(new { id, method, @params = args ?? new { } });
        await _ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);
        var buf = new byte[1 << 20];
        while (true)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            do { r = await _ws.ReceiveAsync(buf, cts.Token); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
            var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()));
            if ((int?)node["id"] != id) continue;
            if (node["error"] != null) throw new Exception(method + ": " + node["error"].ToJsonString());
            return node["result"];
        }
    }

    static async Task<JsonNode> Eval(string js)
    {
        var r = await Cdp("Runtime.evaluate", new { expression = js, awaitPromise = true, returnByValue = true });
        if (r["exceptionDetails"] != null) return JsonValue.Create("EXCEPTION " + r["exceptionDetails"].ToJsonString());
        return r["result"]?["value"];
    }

    static async Task Open(string path, int w, int h, double scale, string background)
    {
        await Cdp("Emulation.setDeviceMetricsOverride", new { width = w, height = h, deviceScaleFactor = scale, mobile = false });
        await Cdp("Page.navigate", new { url = $"http://localhost:{_port}{path}" });
        await Task.Delay(1500);
        // The pages are see-through: paint something behind them so the screenshot shows what's keyed out.
        await Eval($"document.documentElement.style.background = \"{background}\"; true");
        await Task.Delay(500);
    }

    const string Checker = "repeating-conic-gradient(#8a8f99 0 25%, #c9ccd3 0 50%) 0 0 / 60px 60px";

    static async Task<byte[]> Shot(string file)
    {
        var r = await Cdp("Page.captureScreenshot", new { format = "png" });
        var bytes = Convert.FromBase64String((string)r["data"]);
        await File.WriteAllBytesAsync(Path.Combine(_shots, file), bytes);
        Console.WriteLine("  shot  " + file);
        return bytes;
    }

    // Pure green pixels (the screen colour) left in the picture, as a share of the area checked.
    static double GreenShare(byte[] png, Int32Rect? area = null)
    {
        var dec = new PngBitmapDecoder(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bmp = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        var a = area ?? new Int32Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight);
        var px = new byte[a.Width * a.Height * 4];
        bmp.CopyPixels(a, px, a.Width * 4, 0);
        int green = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i + 1] > 200 && px[i + 2] < 90 && px[i] < 90) green++;
        return green / (double)(a.Width * a.Height);
    }

    static async Task Scenarios()
    {
        var c = _svc.Config;

        await AllInOne(c);
        await AlertsPage(c);
        await SinglePages(c);
    }

    // The single pages still work with the shared script. Run last: each one holds an /events connection open,
    // and after several of them in one tab headless Edge ran out of connections to the server (see the notes).
    static async Task SinglePages(OverlayConfig c)
    {
        foreach (var (path, w, h, name) in new[] { ("/overlay/goal/" + c.Goals[0].Id, 800, 140, "page-goal.png"), ("/overlay/strip", 900, 90, "page-strip.png"),
                                                   ("/overlay/giftlist", 420, 600, "page-giftlist.png"), ("/overlay/menu", 800, 400, "page-menu.png"),
                                                   ("/overlay/spinner?id=" + _spinner.Id, 600, 700, "page-spinner.png"), ("/overlay/counter/coins", 400, 120, "page-counter.png") })
        {
            await Open(path, w, h, 1, "#2d3340");
            var ok = await Eval("document.body.innerText.length > 0");
            Check(ok?.GetValue<bool>() == true, name + ": page drew");
            await Shot(name);
        }
    }

    static async Task AllInOne(OverlayConfig c)
    {
        // --- all-in-one, portrait 1080 x 1920 (captured at half size) ---
        await Open("/overlay/all", 1080, 1920, 0.5, Checker);
        await Task.Delay(1000);
        var parts = await Eval("[...document.querySelectorAll('iframe')].map(f => f.getAttribute('src')).join(' | ')");
        Console.WriteLine("        parts: " + parts);
        Console.WriteLine("        server event streams open: " + _web.ClientCount);
        Console.WriteLine("        loaded: " + await Eval("[...document.querySelectorAll('iframe')].map(f => { try { return f.contentDocument.readyState + ':' + f.contentDocument.URL.slice(22, 40) + ':' + f.contentDocument.body.innerText.length; } catch (e) { return String(e); } }).join(' , ')"));
        Check(((string)parts).Contains("/overlay/strip") && ((string)parts).Contains("/overlay/goal/") && ((string)parts).Contains("/overlay/alerts") && ((string)parts).Contains("/overlay/spinner"),
              "all-in-one shows strip, goals, spinner and alerts by default");
        var inner = await Eval("[...document.querySelectorAll('iframe')].map(f => f.contentWindow.performance.getEntriesByType('resource').filter(e => e.name.includes('/events')).length).reduce((a, b) => a + b, 0)");
        Check(inner?.GetValue<int>() == 0, "parts inside the all-in-one page don't open their own /events connection");
        var goalText = await Eval("[...document.querySelectorAll('iframe')].filter(f => f.src.includes('/overlay/goal/')).map(f => f.contentDocument.getElementById('nums').textContent).join(',')");
        Check(((string)goalText).Contains("640 / 1,000") || ((string)goalText).Contains("640 / 1000"), "goal bar inside the all-in-one got the live state: " + goalText);
        await Shot("all-portrait-idle.png");

        // live update: a goal moves
        c.Goals[0].Progress = 990;
        _svc.PushState();
        await Task.Delay(1200);
        goalText = await Eval("[...document.querySelectorAll('iframe')].filter(f => f.src.includes('/overlay/goal/')).map(f => f.contentDocument.getElementById('nums').textContent).join(',')");
        Check(((string)goalText).Contains("990"), "live update reaches the goal inside the all-in-one: " + goalText);

        // spin
        _svc.PushSpin(_spinner, _spinner.Entries, 4, Viewer("SpinFan"));
        await Task.Delay(1500);
        await Shot("all-portrait-spinning.png");
        await Task.Delay(2600);
        await Shot("all-portrait-spin-result.png");
        await Task.Delay(5000);

        // keyed alert card (WebM, colour #00FF00)
        _svc.PushCustomAlert(_keyedCard, Viewer("GreenFan"));
        await Task.Delay(1800);
        var info = await Eval("(() => { const d = document.getElementById('alerts').contentDocument, cv = d.querySelector('canvas'), v = d.querySelector('video'); return JSON.stringify({ renderer: cv && cv.dataset.renderer, key: cv && cv.dataset.key, w: cv && cv.width, muted: v && v.muted, paused: v && v.paused, t: v && v.currentTime, ready: v && v.readyState, net: v && v.networkState, err: v && v.error && v.error.code }); })()");
        Console.WriteLine("        keyed card: " + info);
        var ji = JsonNode.Parse((string)info);
        Check((string)ji["renderer"] == "webgl", "keyed alert drawn with WebGL");
        Check((bool?)ji["muted"] == false && (bool?)ji["paused"] == false, "keyed alert video plays with its sound on (not muted)");
        var png = await Shot("all-portrait-alert-keyed.png");
        var g = GreenShare(png);
        Check(g < 0.002, $"no green screen left on the all-in-one page ({g:P2} pure green pixels)");

        // full-screen interrupt, colour from the corners (KeyColor blank)
        _svc.PushCustomAlert(_keyedFull, Viewer("Scary"));
        await Task.Delay(1800);
        info = await Eval("(() => { const cv = document.getElementById('alerts').contentDocument.querySelector('canvas'); return JSON.stringify({ renderer: cv && cv.dataset.renderer, key: cv && cv.dataset.key }); })()");
        Console.WriteLine("        full screen: " + info);
        ji = JsonNode.Parse((string)info);
        Check(((string)ji["key"] ?? "").StartsWith("#0") && ((string)ji["key"]).Contains("F"), "blank colour: key colour read from the video's corners = " + ji["key"]);
        png = await Shot("all-portrait-fullscreen-keyed.png");
        g = GreenShare(png);
        Check(g < 0.002, $"full-screen keyed interrupt leaves no green ({g:P2})");

        // --- landscape, with the gift list and gift board switched on ---
        var a = c.AllInOne;
        a.Landscape = true;
        a.GiftList.On = true; a.GiftList.Spot = OverlaySpot.Right; a.GiftList.Width = 25;
        a.Board.On = true; a.Board.Spot = OverlaySpot.BottomLeft; a.Board.Width = 45;
        a.Goals.Spot = OverlaySpot.TopLeft; a.Goals.Width = 40;
        a.Strip.Spot = OverlaySpot.TopRight; a.Strip.Width = 50;
        a.Spinner.Spot = OverlaySpot.Middle; a.Spinner.Width = 35;
        a.Alerts.Spot = OverlaySpot.Top;
        a.Scale = 120;
        await Open("/overlay/all", 1920, 1080, 0.5, "linear-gradient(135deg, #1e3a5f, #5b2a6e)");
        await Task.Delay(1000);
        await Shot("all-landscape-idle.png");
        _svc.TestAlert();
        _svc.PushSpin(_spinner, _spinner.Entries, 1, Viewer("LandscapeFan"));
        await Task.Delay(1500);
        await Shot("all-landscape-alert-and-spin.png");

        // switching a part off removes it live
        a.GiftList.On = false;
        _svc.PushState();
        await Task.Delay(800);
        parts = await Eval("[...document.querySelectorAll('iframe')].map(f => f.getAttribute('src')).join(' | ')");
        Check(!((string)parts).Contains("giftlist"), "switching the gift list off takes it off the page live");
    }

    static async Task AlertsPage(OverlayConfig c)
    {
        // --- alerts page on its own: WebGL key, canvas 2D fallback, colour picking, plain video sound ---
        // (A plain colour: headless Edge painted a CSS gradient behind a top-level WebGL canvas as white.)
        await Open("/overlay/alerts", 800, 450, 1, "#c0392b");
        _svc.PushCustomAlert(_keyedCard, Viewer("GreenFan"));
        await Task.Delay(1800);
        var png = await Shot("alerts-keyed-webgl.png");
        Check(GreenShare(png) < 0.002, "alerts page: keyed video over colours, no green left");

        await Open("/overlay/alerts", 800, 450, 1, Checker);
        var r2d = await Eval("(async () => { const v = document.createElement('video'); v.src = '/alert-media/" + _keyedCard.Id + "'; v.muted = true; v.loop = true; v.style.cssText = 'position:absolute;width:2px;height:2px;opacity:0';"
            + " const c = document.createElement('canvas'); c.style.cssText = 'position:absolute;left:40px;top:40px;width:720px'; document.body.append(v, c);"
            + " GDChroma.attach(v, c, { color: '#00FF00', strength: 40, softness: 8, force2d: true }); v.play().catch(() => {}); await new Promise(r => setTimeout(r, 1500)); return c.dataset.renderer; })()");
        Check((string)r2d == "2d", "canvas 2D fallback runs (" + r2d + ")");
        png = await Shot("alerts-keyed-canvas2d.png");
        Check(GreenShare(png) < 0.002, "canvas 2D fallback leaves no green");

        var picked = await Eval("GDChroma.pickFromUrl('/alert-media/" + _keyedCard.Id + "')");
        Check(((string)picked ?? "").StartsWith("#0") && ((string)picked).Length == 7, "'Pick from video' colour = " + picked);

        await Open("/overlay/alerts", 800, 450, 1, "#2d3340");
        _svc.PushCustomAlert(_plain, Viewer("Plain"));
        await Task.Delay(1500);
        var info = await Eval("(() => { const v = document.querySelector('video'); return JSON.stringify({ muted: v.muted, paused: v.paused, t: v.currentTime }); })()");
        Console.WriteLine("        plain video: " + info);
        var ji = JsonNode.Parse((string)info);
        Check((bool?)ji["muted"] == false && (bool?)ji["paused"] == false && (double)ji["t"] > 0.3, "plain alert video plays with sound when the browser allows it");
        await Shot("alerts-plain-video.png");

        // A video from another site can't be read by the page (no CORS): it falls back to the plain video
        // instead of vanishing. A second little server on another port stands in for a web site.
        var site = new System.Net.HttpListener();
        site.Prefixes.Add($"http://127.0.0.1:{_port + 1}/");
        site.Start();
        _ = Task.Run(async () =>
        {
            var bytes = await File.ReadAllBytesAsync(_keyedFull.Media);
            while (site.IsListening)
            {
                try
                {
                    var ctx = await site.GetContextAsync();
                    ctx.Response.ContentType = "video/mp4";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch { }
            }
        });
        var web = new AlertDef { Name = "Web", Media = $"http://127.0.0.1:{_port + 1}/green.mp4", KeyGreen = true, Seconds = 30, Text = "web video" };
        c.CustomAlerts.Add(web);
        await Open("/overlay/alerts", 800, 450, 1, "#2d3340");
        _svc.PushCustomAlert(web, Viewer("Web"));
        await Task.Delay(2500);
        info = await Eval("(() => { const v = document.querySelector('video'); return JSON.stringify({ cls: v && v.className, t: v && v.currentTime, canvas: !!document.querySelector('canvas') }); })()");
        Console.WriteLine("        web video: " + info);
        ji = JsonNode.Parse((string)info);
        Check((string)ji["cls"] == "media" && (bool?)ji["canvas"] == false && (double)ji["t"] > 0.3, "a web video that can't be keyed plays as it is");
        await Shot("alerts-web-video-fallback.png");
        site.Stop();
    }
}
