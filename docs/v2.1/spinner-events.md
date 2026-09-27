# Gift Spinner: chance on the event itself

Like StreamToEarn: every event can be put on the spinner with a rarity. One gift spins the wheel (an
event with the "Spin the Gift Spinner" action) and the wheel lands on one of those events, which then runs.

## Data

`Rule` (Models/Models.cs) has two new fields, saved in `rules.json`:

- `SpinRarity`: `null` = not on a spinner, else `Common`, `Uncommon`, `Rare`, `Epic` or `Legendary`
  (saved by name). Weights are the tier defaults: 50, 25, 15, 8, 2.
- `SpinnerId`: the spinner's id. Blank, or a spinner that was removed, means the first spinner.

A rules.json from before this change loads unchanged (no field = not on a spinner). `Rule.Clone`
(Duplicate) copies both fields.

## Pool

`SpinnerService.PoolFor(spinner, spinners, rules)` = the events on that spinner (enabled, with a rarity,
in Events page order) followed by the spinner's old-style prizes (`Spinner.Entries`, unchanged, still with
their own actions and custom weights). Left out:

- switched-off events;
- events that have a "Spin the Gift Spinner" action themselves (landing on one would spin again forever;
  the editor refuses to save that combination).

An event slice is a `SpinnerEntry` built on the fly (`RuleId` set, never saved into overlays.json); its
actions are the event's own. The pool is taken when the spin's turn comes, so an event switched on or
off meanwhile counts right. Landing on a slice runs its actions through `RulesEngine.RunActionsAsync` as
before (name "Spinner: Event"), so interrupts, logging and the "Also run what it lands on" test box work
the same. The event's own cooldown and "once per gift in a combo" are not applied to a spinner landing.

New spinners start empty (they used to get five placeholder prizes). A spin with nothing on the wheel
logs "has nothing on it yet. Open an event and pick a rarity under Gift Spinner."

## Screens

- **Event editor**: a "Gift Spinner" card: "Chance on the spinner" (None, Common ... Legendary), a
  "Which spinner" picker only when there is more than one spinner, and a line with the chance it would
  have right now ("About 16% of spins on "Gift Spinner" land here (3 slices on it)").
- **Events list**: a small "Spinner: Rare" badge in the tier's colour next to the event's name.
- **Overlays page, Stream tools**: each spinner lists "Events on this spinner" (name, rarity, chance %,
  action count) and says how to add one; the old prize list follows as "Other prizes (optional)".
  Chances are over the whole wheel (events and prizes). The list refreshes when events change or when
  the page is shown again.
- **Overlay** (`spinner.html`): event slices show the event's gift picture at the rim (served from the
  existing `/rule-image/<event id>`), the name shrinks a little before it is cut short.

## Tests

`tests/SpinnerEvents` (not part of GiftDeck.exe). Build GiftDeck to a scratch folder first:

```
dotnet build GiftDeck.csproj -c Debug -o <scratch>\gd
dotnet build tests\SpinnerEvents -c Debug -p:GiftDeckDir=<scratch>\gd -o <scratch>\t
<scratch>\t\SpinnerEvents.exe                 43 checks, scratch GIFTDECK_DATA in %TEMP%
<scratch>\t\SpinnerEvents.exe render <folder>  PNGs of the editor, Events list and Stream tools (offscreen)
```

Checks: pool = events with a rarity (+ old prizes, right spinner, removed spinner = first, by name),
weights over 200,000 spins, switched-off events never land, old prize lists (custom and tier weights,
old overlays.json), rules.json round trip with the new fields, an old rules.json without them, and a
spin end to end (overlay state and spin message, the event's actions run through the rules engine,
the test spin with the box unticked runs nothing).
