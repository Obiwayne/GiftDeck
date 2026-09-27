using System.Text.Json.Serialization;

namespace GiftDeck.Models;

// ---- Gift Spinner ----

public enum Rarity { Common, Uncommon, Rare, Epic, Legendary }

public static class Rarities
{
    public static readonly Rarity[] All = (Rarity[])Enum.GetValues(typeof(Rarity));

    // How often each tier comes up when an entry doesn't set its own weight (out of 100).
    public static int DefaultWeight(Rarity r) => r switch
    {
        Rarity.Common => 50,
        Rarity.Uncommon => 25,
        Rarity.Rare => 15,
        Rarity.Epic => 8,
        Rarity.Legendary => 2,
        _ => 10,
    };

    public static string DefaultColor(Rarity r) => r switch
    {
        Rarity.Common => "#9CA3AF",
        Rarity.Uncommon => "#22C55E",
        Rarity.Rare => "#3B82F6",
        Rarity.Epic => "#A855F7",
        Rarity.Legendary => "#F59E0B",
        _ => "#9CA3AF",
    };
}

// One prize on a spinner: a label, how rare it is, and the actions it runs when it comes up.
public class SpinnerEntry : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    string _label = "New prize";
    public string Label { get => _label; set { _label = value ?? ""; Raise(nameof(Label)); } }

    Rarity _rarity = Rarity.Common;
    public Rarity Rarity { get => _rarity; set { _rarity = value; Raise(nameof(Rarity)); Raise(nameof(EffectiveColor)); } }

    // 0 = use the tier's default weight.
    int _weight;
    public int Weight { get => _weight; set { _weight = Math.Max(0, value); Raise(nameof(Weight)); } }

    // Blank = the tier's colour.
    string _color = "";
    public string Color { get => _color; set { _color = value ?? ""; Raise(nameof(Color)); Raise(nameof(EffectiveColor)); } }

    public List<RuleAction> Actions { get; set; } = new List<RuleAction>();

    // Set when this slice is an event that is on the spinner (built from the event, never saved with the spinner).
    [JsonIgnore] public Guid? RuleId { get; set; }
    // The event's gift picture (a web address), if it has one.
    [JsonIgnore] public string Image { get; set; } = "";

    [JsonIgnore] public int EffectiveWeight =>Weight > 0 ? Weight : Rarities.DefaultWeight(Rarity);
    [JsonIgnore] public string EffectiveColor => string.IsNullOrWhiteSpace(Color) ? Rarities.DefaultColor(Rarity) : Color.Trim();

    // Shown on the Overlays page: "12.5% · 2 actions".
    string _chanceText = "";
    [JsonIgnore] public string ChanceText { get => _chanceText; set { _chanceText = value; Raise(nameof(ChanceText)); } }
    [JsonIgnore] public string ActionsText => Actions.Count == 0 ? "No actions" : Actions.Count == 1 ? "1 action" : Actions.Count + " actions";
    public void RefreshActionsText() => Raise(nameof(ActionsText));
}

public class Spinner : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    string _name = "Gift Spinner";
    public string Name { get => _name; set { _name = value ?? ""; Raise(nameof(Name)); } }

    int _spinSeconds = 5;
    public int SpinSeconds { get => _spinSeconds; set { _spinSeconds = Math.Clamp(value, 2, 30); Raise(nameof(SpinSeconds)); } }

    // Hide the wheel between spins, so it only appears on stream when someone triggers it.
    bool _hideWhenIdle;
    public bool HideWhenIdle { get => _hideWhenIdle; set { _hideWhenIdle = value; Raise(nameof(HideWhenIdle)); } }

    // Old-style prizes with their own actions. Events put on the spinner (Rule.SpinRarity) are added to these when it spins.
    public List<SpinnerEntry> Entries { get; set; } = new List<SpinnerEntry>();

    // The events on this spinner, with their chances, for the Overlays page (filled by the page).
    List<SpinnerEntry> _eventEntries = new List<SpinnerEntry>();
    [JsonIgnore] public List<SpinnerEntry> EventEntries { get => _eventEntries; set { _eventEntries = value ?? new List<SpinnerEntry>(); Raise(nameof(EventEntries)); Raise(nameof(HasNoEvents)); } }
    [JsonIgnore] public bool HasNoEvents => _eventEntries.Count == 0;

    // "Spin now" on the Overlays page: also run the prize's actions (off = just spin on the overlay).
    [JsonIgnore] public bool TestRunsActions { get; set; }
}

// ---- Alerts and interrupts ----

public class AlertDef : Observable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    string _name = "New alert";
    public string Name { get => _name; set { _name = value ?? ""; Raise(nameof(Name)); } }

    // A picture, GIF, WebM or MP4: a file on this PC or a web address.
    string _media = "";
    public string Media { get => _media; set { _media = value ?? ""; Raise(nameof(Media)); } }

    // An mp3/wav played on this PC (like the "Play a sound" action), so the stream hears it.
    string _sound = "";
    public string Sound { get => _sound; set { _sound = value ?? ""; Raise(nameof(Sound)); } }

    int _volume = 100;
    public int Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 100); Raise(nameof(Volume)); } }

    string _text = "{user} sent {gift}!";
    public string Text { get => _text; set { _text = value ?? ""; Raise(nameof(Text)); } }

    int _seconds = 6;
    public int Seconds { get => _seconds; set { _seconds = Math.Clamp(value, 1, 120); Raise(nameof(Seconds)); } }

    // Pause every other event's actions while this alert plays, then carry on in order.
    bool _interrupt;
    public bool Interrupt { get => _interrupt; set { _interrupt = value; Raise(nameof(Interrupt)); } }

    // Fill the whole Browser Source (for jumpscares) instead of a card in the middle.
    bool _fullScreen;
    public bool FullScreen { get => _fullScreen; set { _fullScreen = value; Raise(nameof(FullScreen)); } }

    // Green-screen videos: the overlay makes this colour see-through (a chroma key), so only the subject shows.
    bool _keyGreen;
    public bool KeyGreen { get => _keyGreen; set { _keyGreen = value; Raise(nameof(KeyGreen)); } }

    // Blank = work it out from the video's corners.
    string _keyColor = "#00FF00";
    public string KeyColor { get => _keyColor; set { _keyColor = value ?? ""; Raise(nameof(KeyColor)); } }

    // How close to the colour a pixel has to be to go (1-100), and how soft the cut-out edge is (1-100).
    int _keyStrength = 40;
    public int KeyStrength { get => _keyStrength; set { _keyStrength = Math.Clamp(value, 1, 100); Raise(nameof(KeyStrength)); } }

    int _keySoftness = 8;
    public int KeySoftness { get => _keySoftness; set { _keySoftness = Math.Clamp(value, 1, 100); Raise(nameof(KeySoftness)); } }
}

// ---- Overlay templates made from the profile's events ----

public class TemplatesConfig
{
    public string GiftListTitle { get; set; } = "GIFTS";
    public int GiftListMax { get; set; } = 0;   // 0 = every gift event
    public string StripTitle { get; set; } = "TOP GIFTERS";
}

// Coins sent by one viewer this stream (for the top 3 strip).
public class GifterTotal
{
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public int Coins { get; set; }
}
