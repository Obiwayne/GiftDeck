using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using GiftDeck.Services;
using Microsoft.Web.WebView2.Core;

namespace GiftDeck.Views;

// Sends chat messages by typing them into TikTok's own LIVE page (TikTok has no public API for posting chat).
// One instance for the app; it lives hidden and is shown only to log in.
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
        if (offscreen)
        {
            Hide();
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
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

        // Open (or reopen) the LIVE page for the current account and give it time to build its chat box.
        var url = LiveUrl();
        if (_openedFor != url || !(Web.CoreWebView2.Source ?? "").Contains("/live"))
        {
            var loaded = new TaskCompletionSource<bool>();
            void Done(object s, CoreWebView2NavigationCompletedEventArgs e) { Web.CoreWebView2.NavigationCompleted -= Done; loaded.TrySetResult(e.IsSuccess); }
            Web.CoreWebView2.NavigationCompleted += Done;
            Web.CoreWebView2.Navigate(url);
            await Task.WhenAny(loaded.Task, Task.Delay(20000));
            _openedFor = url;
        }

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
