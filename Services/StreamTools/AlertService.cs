using GiftDeck.Models;

namespace GiftDeck.Services;

// Alerts and interrupts: a picture/video/sound alert on the alerts overlay that can pause the event queue
// while it plays. See docs/v2.1/plan.md. Skeleton: filled in by the Stream tools package.
public class AlertService
{
    public Task ShowAsync(string alertId, LiveEvent e) => Task.CompletedTask;
}
