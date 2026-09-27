using System.Collections.Concurrent;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Gift Spinner: a random event from a weighted pool, shown spinning on an overlay. See docs/v2.1/plan.md.
public class SpinnerService
{
    // Extra time after the wheel stops, so viewers see the prize before its actions run.
    public const int RevealMs = 1800;

    readonly Random _random = new Random();
    readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turns = new ConcurrentDictionary<Guid, SemaphoreSlim>();

    public Spinner Find(string spinnerId)
    {
        var list = Hub.Overlays?.Config.Spinners;
        if (list == null || list.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(spinnerId)) return list[0];
        var key = spinnerId.Trim();
        return list.FirstOrDefault(s => s.Id.ToString() == key)
            ?? list.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    // Picks an entry with a chance of weight / total weight. Null when there is nothing to pick.
    public static SpinnerEntry Pick(IReadOnlyList<SpinnerEntry> entries, Random random)
    {
        if (entries == null || entries.Count == 0) return null;
        long total = 0;
        foreach (var x in entries) total += x.EffectiveWeight;
        if (total <= 0) return entries[random.Next(entries.Count)];
        long roll = (long)(random.NextDouble() * total);
        foreach (var x in entries)
        {
            roll -= x.EffectiveWeight;
            if (roll < 0) return x;
        }
        return entries[entries.Count - 1];
    }

    // The chance of each entry in percent, for the editor.
    public static double Chance(Spinner s, SpinnerEntry entry)
    {
        long total = s.Entries.Sum(x => (long)x.EffectiveWeight);
        return total <= 0 ? 0 : 100.0 * entry.EffectiveWeight / total;
    }

    public Task SpinAsync(string spinnerId, LiveEvent e) => SpinAsync(spinnerId, e, true);

    // Spins on the overlay, waits for the wheel to stop, then runs the prize's actions (unless runActions is off).
    // Spins of the same spinner take turns, so two gifts at once show two spins one after the other.
    public async Task<SpinnerEntry> SpinAsync(string spinnerId, LiveEvent e, bool runActions)
    {
        var s = Find(spinnerId) ?? throw new Exception("No spinner called " + (string.IsNullOrWhiteSpace(spinnerId) ? "(none set up)" : spinnerId));
        var entries = s.Entries.ToList();
        if (entries.Count == 0) throw new Exception($"Spinner \"{s.Name}\" has no prizes");

        SpinnerEntry won;
        var turn = _turns.GetOrAdd(s.Id, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync();
        try
        {
            lock (_random) won = Pick(entries, _random);
            Hub.Overlays.PushSpin(s, entries, entries.IndexOf(won), e);
            await Task.Delay(s.SpinSeconds * 1000 + RevealMs);
        }
        finally
        {
            turn.Release();
        }

        Log.Write($"  Spinner \"{s.Name}\" landed on {won.Label} ({won.Rarity}) for {e.Nickname}");
        if (runActions && won.Actions.Count > 0)
        {
            // Run through the rules engine like an event of its own, so interrupts and logging behave the same.
            var prize = new Rule { Name = s.Name + ": " + won.Label, Actions = won.Actions.Select(a => a.Clone()).ToList() };
            await Hub.Rules.RunActionsAsync(prize, e);
        }
        return won;
    }
}
