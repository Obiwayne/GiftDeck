using GiftDeck.Models;

namespace GiftDeck.Services;

// Gift Spinner: a random event from a weighted pool, shown spinning on an overlay. See docs/v2.1/plan.md.
// Skeleton: filled in by the Stream tools package.
public class SpinnerService
{
    public Task SpinAsync(string spinnerId, LiveEvent e) => Task.CompletedTask;
}
