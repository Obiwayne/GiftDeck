using System.ComponentModel;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using GiftDeck.Services;
using Microsoft.Web.WebView2.Core;

namespace GiftDeck.Views;

// TikTok's own LIVE page, logged in as the streamer, kept in a hidden window. It's used two ways:
// - sending chat: typing into the page's comment box (TikTok has no public API for posting chat);
// - reading the LIVE: the page receives chat, gifts, likes and viewer counts over its own WebSocket; those
//   frames are passed to the bridge (in --page mode) to decode. Being logged in, this works for 18+ LIVEs too.
// One instance for the app; shown only to log in.
public partial class TikTokChatWindow : Window
{
    static TikTokChatWindow _instance;
    public static TikTokChatWindow Instance => _instance ??= new TikTokChatWindow { Owner = Application.Current.MainWindow };

    Task _ready;
    string _openedFor;
    bool _quitting;

    TikTokChatWindow()
    {
        InitializeComponent();
        Application.Current.Exit += (_, _) => _quitting = true;
    }

    // Closing just hides it, so the logged-in page keeps running.
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        Hide();
    }

    Task EnsureReadyAsync() => _ready ??= InitAsync();

    async Task InitAsync()
    {
        // The WebView2 only starts once its window has been shown. If nothing asked to see it, show it far
        // off-screen for a moment and hide it again, so sending works without a window popping up.
        bool offscreen = !IsVisible;
        if (offscreen)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000; Top = -20000;
            ShowActivated = false;
            Show();
        }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GiftDeck", "WebView2-TikTok");
        var env = await CoreWebView2Environment.CreateAsync(null, dir);
        await Web.EnsureCoreWebView2Async(env);
        Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Web.CoreWebView2.IsMuted = true; // the stream's own sound must not play (it would echo into OBS)
        await SetUpReadingAsync(env);
        if (offscreen)
        {
            Hide();
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    // ---- Reading the LIVE through the page ----

    readonly HashSet<string> _feedSockets = new HashSet<string>();
    readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    ClientWebSocket _bridge;
    public bool Reading { get; private set; }

    async Task SetUpReadingAsync(CoreWebView2Environment env)
    {
        var cw = Web.CoreWebView2;
        await cw.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        // TikTok's LIVE event feed is the WebSocket under /webcast/ (others are direct messages etc.).
        cw.GetDevToolsProtocolEventReceiver("Network.webSocketCreated").DevToolsProtocolEventReceived += (_, e) =>
        {
            var r = JsonDocument.Parse(e.ParameterObjectAsJson).RootElement;
            if ((r.GetProperty("url").GetString() ?? "").Contains("/webcast/"))
                lock (_feedSockets) _feedSockets.Add(r.GetProperty("requestId").GetString());
        };
        cw.GetDevToolsProtocolEventReceiver("Network.webSocketFrameReceived").DevToolsProtocolEventReceived += (_, e) =>
        {
            if (!Reading) return;
            var r = JsonDocument.Parse(e.ParameterObjectAsJson).RootElement;
            bool feed; lock (_feedSockets) feed = _feedSockets.Contains(r.GetProperty("requestId").GetString());
            var resp = r.GetProperty("response");
            if (feed && resp.GetProperty("opcode").GetInt32() == 2)
                _ = ToBridgeAsync("frame", resp.GetProperty("payloadData").GetString());
        };
        // No video or animations: the page would otherwise download and play the stream (or the For You feed)
        // in the background. The chat feed is a WebSocket, so it isn't affected.
        cw.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Media);
        cw.AddWebResourceRequestedFilter("*.flv*", CoreWebView2WebResourceContext.All);
        cw.AddWebResourceRequestedFilter("*.m3u8*", CoreWebView2WebResourceContext.All);
        cw.WebResourceRequested += (_, e) => e.Response = env.CreateWebResourceResponse(null, 403, "Not needed in GiftDeck", "");
    }

    // Sends one message to the bridge over its local WebSocket, connecting when needed.
    async Task ToBridgeAsync(string evt, object data)
    {
        await _sendLock.WaitAsync();
        try
        {
            if (_bridge == null || _bridge.State != WebSocketState.Open)
            {
                _bridge?.Dispose();
                _bridge = new ClientWebSocket();
                using var cts = new CancellationTokenSource(3000);
                await _bridge.ConnectAsync(new Uri($"ws://localhost:{BridgeService.Port}/"), cts.Token);
                _ = DrainAsync(_bridge); // the bridge broadcasts events to every client; this one doesn't need them
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { @event = evt, data }));
            await _bridge.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { try { _bridge?.Abort(); } catch { } } // bridge restarting: this frame is lost, the next one reconnects
        finally { _sendLock.Release(); }
    }

    static async Task DrainAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];
        try { while (ws.State == WebSocketState.Open) await ws.ReceiveAsync(buffer, CancellationToken.None); } catch { }
    }

    // Opens the streamer's LIVE page and starts passing its feed to the bridge.
    public async Task StartReadingAsync()
    {
        await EnsureReadyAsync();
        Reading = true;
        await ToBridgeAsync("page", new { open = true });
        await OpenLiveAsync(force: true);
        Log.Write("Reading your LIVE through the TikTok page");
    }

    // Stops passing the feed and parks the page, so it isn't doing anything while you're offline.
    public async Task StopReadingAsync()
    {
        if (!Reading) return;
        Reading = false;
        await ToBridgeAsync("page", new { open = false });
        await EnsureReadyAsync();
        Web.CoreWebView2.Navigate("about:blank");
        _openedFor = null;
        Log.Write("Stopped reading the TikTok page");
    }

    async Task OpenLiveAsync(bool force = false)
    {
        var url = LiveUrl();
        if (!force && _openedFor == url && (Web.CoreWebView2.Source ?? "").Contains("/live")) return;
        var loaded = new TaskCompletionSource<bool>();
        void Done(object s, CoreWebView2NavigationCompletedEventArgs e) { Web.CoreWebView2.NavigationCompleted -= Done; loaded.TrySetResult(e.IsSuccess); }
        Web.CoreWebView2.NavigationCompleted += Done;
        Web.CoreWebView2.Navigate(url);
        await Task.WhenAny(loaded.Task, Task.Delay(20000));
        _openedFor = url;
    }

    public async Task<bool> IsLoggedInAsync()
    {
        await EnsureReadyAsync();
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.tiktok.com");
        return cookies.Any(c => c.Name == "sessionid" && !string.IsNullOrEmpty(c.Value));
    }

    // Shows TikTok's login page (or the LIVE page if already logged in).
    public async Task ShowToLogInAsync()
    {
        if (!IsVisible)
        {
            // Centre it on GiftDeck (it may have been parked off-screen to start up).
            var owner = Owner;
            if (owner != null) { Left = owner.Left + (owner.ActualWidth - Width) / 2; Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2); }
            Show();
        }
        Activate();
        await EnsureReadyAsync();
        var loggedIn = await IsLoggedInAsync();
        Web.CoreWebView2.Navigate(loggedIn ? LiveUrl() : "https://www.tiktok.com/login");
        _openedFor = null;
    }

    static string LiveUrl() => $"https://www.tiktok.com/@{Hub.Settings.BridgeUsername.Trim().TrimStart('@')}/live";

    // Types the message into the LIVE page's comment box and presses Enter. Returns null when sent, or why not.
    public async Task<string> SendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(Hub.Settings.BridgeUsername)) return "Set your TikTok username on Stream Setup first.";
        await EnsureReadyAsync();
        if (!await IsLoggedInAsync()) { await ShowToLogInAsync(); return "Log in to TikTok in the window that opened, then send again."; }

        // Open the LIVE page for the current account if it isn't already (reading keeps it open).
        await OpenLiveAsync();

        // The page builds its comment box a little after loading; try for a few seconds.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var result = await Web.CoreWebView2.ExecuteScriptAsync(SendScript(text));
            var r = JsonDocument.Parse(result).RootElement;
            var status = r.TryGetProperty("status", out var st) ? st.GetString() : "error";
            if (status == "sent") { Log.Write("Chat message sent from GiftDeck"); return null; }
            if (status == "offline") return "Your LIVE isn't on, so there's no chat to send to.";
            await Task.Delay(700);
        }
        return "Couldn't find TikTok's chat box. Click \"Show\" to look at the TikTok page (it may be asking for something).";
    }

    // Finds the visible comment box (TikTok uses a rich-text editor; its class names change, so match by role and
    // wording rather than class), inserts the text the way typing would, then presses Enter (or clicks Post).
    static string SendScript(string text) => @"(() => {
  const text = " + JsonSerializer.Serialize(text) + @";
  const visible = el => { const r = el.getBoundingClientRect(); return r.width > 20 && r.height > 8 && getComputedStyle(el).visibility !== 'hidden'; };
  const words = el => ((el.getAttribute('placeholder') || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('data-e2e') || '') + ' ' + (el.closest('[data-e2e]')?.getAttribute('data-e2e') || '') + ' ' + (el.parentElement?.innerText || '')).toLowerCase();
  const boxes = [...document.querySelectorAll('[contenteditable=""true""], [contenteditable=""plaintext-only""], textarea, input[type=""text""]')].filter(visible);
  if (!boxes.length) {
    const ended = /live has ended|live ended|isn't live|not live/i.test(document.body?.innerText || '');
    return { status: ended ? 'offline' : 'nobox' };
  }
  const box = boxes.find(b => /comment|chat|say something|add a|type/.test(words(b))) || boxes[boxes.length - 1];
  box.focus();
  if (box.isContentEditable) {
    document.execCommand('selectAll', false, null);
    document.execCommand('insertText', false, text);
  } else {
    const set = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(box), 'value').set;
    set.call(box, text);
    box.dispatchEvent(new Event('input', { bubbles: true }));
  }
  const enter = type => box.dispatchEvent(new KeyboardEvent(type, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
  enter('keydown'); enter('keypress'); enter('keyup');
  const post = [...document.querySelectorAll('[data-e2e*=""post""], [data-e2e*=""send""], button[aria-label*=""Post"" i], button[aria-label*=""Send"" i]')].find(visible);
  if (post && (box.innerText || box.value || '').trim()) post.click();
  return { status: 'sent' };
})()";
}
