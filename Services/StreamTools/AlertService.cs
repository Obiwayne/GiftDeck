using GiftDeck.Models;

namespace GiftDeck.Services;

// Alerts and interrupts: a picture/video/sound alert on the alerts overlay that can pause the event queue
// while it plays. See docs/v2.1/plan.md.
public class AlertService
{
    public AlertDef Find(string alertId)
    {
        var list = Hub.Overlays?.Config.CustomAlerts;
        if (list == null || string.IsNullOrWhiteSpace(alertId)) return null;
        var key = alertId.Trim();
        return list.FirstOrDefault(a => a.Id.ToString() == key)
            ?? list.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    public async Task ShowAsync(string alertId, LiveEvent e)
    {
        var a = Find(alertId) ?? throw new Exception("No alert called " + (string.IsNullOrWhiteSpace(alertId) ? "(none chosen)" : alertId));
        if (!a.Interrupt)
        {
            Show(a, e);
            return;
        }
        // An interrupt holds back every other event's actions until it has finished playing.
        Hub.Rules.PauseQueue();
        try
        {
            Log.Write($"  Interrupt \"{a.Name}\": other events wait {a.Seconds}s");
            Show(a, e);
            await Task.Delay(TimeSpan.FromSeconds(a.Seconds) + TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            Hub.Rules.EndInterrupt();
        }
    }

    void Show(AlertDef a, LiveEvent e)
    {
        Hub.Overlays.PushCustomAlert(a, e);
        if (!string.IsNullOrWhiteSpace(a.Sound))
        {
            try { Hub.Sounds.Play(a.Sound.Trim(), a.Volume); }
            catch (Exception ex) { Log.Write($"  Alert \"{a.Name}\" sound: {ex.Message}"); }
        }
    }

    // "Test" on the Overlays page: shows it with a made-up viewer, without pausing anything.
    public void Test(AlertDef a) => Show(a, new LiveEvent { Type = "gift", Nickname = "Test Viewer", GiftName = "Rose", Diamonds = 1, RepeatCount = 5, IsTest = true });
}
