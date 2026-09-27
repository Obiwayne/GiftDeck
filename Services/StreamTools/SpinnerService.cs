using System.Collections.Concurrent;
using GiftDeck.Models;

namespace GiftDeck.Services;

// Gift Spinner: a random event from a weighted pool, shown spinning on an overlay. See docs/v2.1/plan.md
// and docs/v2.1/spinner-events.md. The pool is the events put on the spinner (each with a rarity) plus
// any old-style prizes the spinner has.
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

    // The spinner an event is on: its SpinnerId, or the first spinner when that is blank or was removed.
    public static Spinner SpinnerFor(Rule rule, IReadOnlyList<Spinner> spinners)
    {
        if (rule?.SpinRarity == null || spinners == null || spinners.Count == 0) return null;
        var key = (rule.SpinnerId ?? "").Trim();
        if (key.Length > 0)
        {
            var match = spinners.FirstOrDefault(s => s.Id.ToString() == key)
                ?? spinners.FirstOrDefault(s => string.Equals(s.Name, key, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return spinners[0];
    }

    // An event that spins a wheel itself can't be a slice (landing on it would spin again, forever).
    public static bool SpinsAWheel(Rule rule) => rule.Actions.Any(a => a.Type == ActionType.SpinWheel);

    // The slices for the events on this spinner: enabled events with a rarity, in the Events page order.
    public static List<SpinnerEntry> EventEntries(Spinner s, IReadOnlyList<Spinner> spinners, IEnumerable<Rule> rules)
    {
        var list = new List<SpinnerEntry>();
        if (s == null || rules == null) return list;
        foreach (var r in rules)
        {
            if (r == null || !r.Enabled || r.SpinRarity is not Rarity rarity || SpinsAWheel(r)) continue;
            if (SpinnerFor(r, spinners) != s) continue;
            string image = "";
            try { image = r.TriggerGift?.ImageUrl ?? ""; } catch { }
            list.Add(new SpinnerEntry
            {
                Id = r.Id,
                RuleId = r.Id,
                Label = string.IsNullOrWhiteSpace(r.Name) ? r.TriggerSummary : r.Name,
                Rarity = rarity,
                Actions = r.Actions,
                Image = image,
            });
        }
        return list;
    }

    // Everything the wheel can land on: the events on it, then its old-style prizes.
    public static List<SpinnerEntry> PoolFor(Spinner s, IReadOnlyList<Spinner> spinners, IEnumerable<Rule> rules)
    {
        var list = EventEntries(s, spinners, rules);
        if (s != null) list.AddRange(s.Entries);
        return list;
    }

    // The pool with GiftDeck's own spinners and events.
    // viaWindow: read the events list on the window's thread, which owns it (for a spin; the overlay state reads it directly like the gift board does).
    public static List<SpinnerEntry> Pool(Spinner s, bool viaWindow = false) => PoolFor(s, Hub.Overlays?.Config.Spinners, CurrentRules(viaWindow));

    static Rule[] CurrentRules(bool viaWindow)
    {
        var rules = Hub.Rules?.Rules;
        if (rules == null) return Array.Empty<Rule>();
        try
        {
            var app = System.Windows.Application.Current;
            if (viaWindow && app != null && !app.Dispatcher.CheckAccess()) return app.Dispatcher.Invoke(() => rules.ToArray());
            return rules.ToArray();
        }
        catch { return Array.Empty<Rule>(); }
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
    public static double Chance(IReadOnlyList<SpinnerEntry> pool, SpinnerEntry entry)
    {
        long total = pool.Sum(x => (long)x.EffectiveWeight);
        return total <= 0 ? (pool.Count == 0 ? 0 : 100.0 / pool.Count) : 100.0 * entry.EffectiveWeight / total;
    }

    public static string ChanceText(double pct) => pct >= 10 ? pct.ToString("0") + "%" : pct.ToString("0.#") + "%";

    public Task SpinAsync(string spinnerId, LiveEvent e) => SpinAsync(spinnerId, e, true);

    // Spins on the overlay, waits for the wheel to stop, then runs the prize's actions (unless runActions is off).
    // Spins of the same spinner take turns, so two gifts at once show two spins one after the other.
    public async Task<SpinnerEntry> SpinAsync(string spinnerId, LiveEvent e, bool runActions)
    {
        var s = Find(spinnerId) ?? throw new Exception("No spinner called " + (string.IsNullOrWhiteSpace(spinnerId) ? "(none set up)" : spinnerId));

        SpinnerEntry won;
        var turn = _turns.GetOrAdd(s.Id, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync();
        try
        {
            // Taken when its turn comes, so an event switched on or off meanwhile is counted right.
            var entries = Pool(s, viaWindow: true);
            if (entries.Count == 0) throw new Exception($"Spinner \"{s.Name}\" has nothing on it yet. Open an event and pick a rarity under Gift Spinner.");
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
