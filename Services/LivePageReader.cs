using System.Windows.Threading;
using GiftDeck.Views;

namespace GiftDeck.Services;

// When "Reading your LIVE" is set to the TikTok page: opens GiftDeck's own logged-in TikTok page on your LIVE
// while you're live (so its chat, gifts and viewers reach the bridge), and parks it when you're not.
public class LivePageReader
{
    public event Action StatusChanged;
    public string Status { get; private set; } = "";

    readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
    bool _busy;
    int _offlineChecks, _quietChecks;

    public void Start()
    {
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    public void CheckSoon() => _ = CheckAsync();

    static bool Enabled => Hub.Settings.LiveReader == "page";

    async Task CheckAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var page = TikTokChatWindow.Instance;
            var user = Hub.Settings.BridgeUsername.Trim().TrimStart('@');
            if (!Enabled || user.Length == 0)
            {
                if (page.Reading) await page.StopReadingAsync();
                Say(Enabled ? "Set your TikTok username first." : "");
                return;
            }

            bool live = Hub.TikTok.Live;
            if (!live)
            {
                try { live = await TikTokLiveService.IsShowingLiveAsync(user); }
                catch { live = page.Reading; } // couldn't ask TikTok: keep things as they are
            }

            if (!live)
            {
                // Two checks in a row, so a hiccup in TikTok's answer doesn't close the page mid-LIVE.
                if (page.Reading && ++_offlineChecks >= 2) { await page.StopReadingAsync(); _offlineChecks = 0; }
                if (!page.Reading) Say("Waiting for your LIVE. The page opens when you go live.");
                return;
            }
            _offlineChecks = 0;

            if (!page.Reading)
            {
                if (!await page.IsLoggedInAsync()) { Say("Log in to TikTok (the TikTok login button) so MayhemDeck can read your LIVE."); return; }
                await page.StartReadingAsync();
                _quietChecks = 0;
                Say("Reading your LIVE through the TikTok page.");
                return;
            }

            // Live and reading, but the bridge hasn't seen anything from the page for about a minute: reload it.
            if (Hub.TikFinity.TikTokLive == true) _quietChecks = 0;
            else if (++_quietChecks >= 3)
            {
                Log.Write("No LIVE data from the TikTok page for a minute; reloading it");
                _quietChecks = 0;
                await page.StartReadingAsync();
            }
        }
        catch (Exception e) { Say("TikTok page: " + e.Message); Log.Write("Reading through the TikTok page failed: " + e.Message); }
        finally { _busy = false; }
    }

    void Say(string text)
    {
        if (text == Status) return;
        Status = text;
        StatusChanged?.Invoke();
    }
}
