using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Scene buttons for the Go LIVE page, so switching scenes mid-LIVE doesn't mean leaving the page.
// Follows OBS: switching from the Scenes page, OBS itself or an event updates the highlight too.
public partial class SceneSwitcher : UserControl
{
    List<ObsScene> _scenes = new List<ObsScene>();
    string _live;
    bool _refreshing, _again;

    // Button sizes: height, font, side padding, narrowest width, gap. Large is the normal button; small is ~40% smaller.
    static readonly Dictionary<string, (double h, double font, double pad, double minW, double gap)> Sizes = new()
    {
        ["small"] = (24, 11.5, 9, 54, 5),
        ["medium"] = (30, 12.5, 12, 72, 6),
        ["large"] = (36, 13, 14, 90, 8),
    };

    static string Size => Sizes.ContainsKey(Hub.Settings.SceneButtonSize ?? "") ? Hub.Settings.SceneButtonSize : "small";

    public SceneSwitcher()
    {
        InitializeComponent();
        BuildSizePicker();
        Hub.Obs.EventReceived += OnObsEvent;
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(() => _ = RefreshAsync());
        IsVisibleChanged += (_, _) => { if (IsVisible) _ = RefreshAsync(); };
    }

    void OnObsEvent(string type, JsonElement data)
    {
        switch (type)
        {
            case "CurrentProgramSceneChanged":
                var uuid = data.TryGetProperty("sceneUuid", out var u) ? u.GetString() : null;
                Dispatcher.BeginInvoke(() => { if (uuid != null) { _live = uuid; Render(); } else _ = RefreshAsync(); });
                break;
            case "SceneListChanged":
            case "SceneCreated":
            case "SceneRemoved":
            case "SceneNameChanged":
                Dispatcher.BeginInvoke(() => _ = RefreshAsync());
                break;
        }
    }

    async Task RefreshAsync()
    {
        if (!IsVisible) return;
        if (_refreshing) { _again = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _again = false;
                if (!Hub.Obs.Connected) { _scenes = new List<ObsScene>(); _live = null; }
                else
                {
                    try
                    {
                        var (scenes, program) = await Hub.Obs.GetSceneListAsync();
                        _scenes = scenes;
                        _live = program;
                        AddPretendScenes();
                    }
                    catch { _scenes = new List<ObsScene>(); }
                }
                Render();
            } while (_again);
        }
        finally { _refreshing = false; }
    }

    // GIFTDECK_FAKE_SCENES=N (development builds) adds N pretend scenes, to check the layout with a big setup.
    void AddPretendScenes()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("GIFTDECK_FAKE_SCENES"), out var n) || n <= 0) return;
        string[] names = { "Intro", "Starting soon", "Be right back", "Outro", "Just chatting", "Gameplay", "Gameplay + cam", "Full cam", "Q&A", "Giveaway", "Sponsor", "Replay", "Ending soon", "Technical difficulties", "Break" };
        for (int i = 0; i < n; i++) _scenes.Add(new ObsScene(names[i % names.Length] + (i >= names.Length ? " " + (i / names.Length + 1) : ""), "pretend-" + i));
    }

    const int SearchFrom = 13; // more scenes than this: show the search box

    void Filter_Changed(object sender, TextChangedEventArgs e) => Render();

    // "br" finds "Break" (text) and "Be right back" (first letters, so "brb" works too).
    static bool Matches(string name, string find)
    {
        if (name.Contains(find, StringComparison.OrdinalIgnoreCase)) return true;
        var initials = string.Concat(name.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w[0]));
        return initials.StartsWith(find, StringComparison.OrdinalIgnoreCase);
    }

    void Render()
    {
        Filter.Visibility = _scenes.Count >= SearchFrom ? Visibility.Visible : Visibility.Collapsed;
        var find = Filter.Visibility == Visibility.Visible ? Filter.Text.Trim() : "";
        // The live scene always shows, so it's clear what's on air even while searching.
        var shown = _scenes.Where(x => find.Length == 0 || x.Uuid == _live || Matches(x.Name, find)).ToList();
        Hint.Text = !Hub.Obs.Connected ? "Connecting to OBS…"
                  : _scenes.Count == 0 ? "OBS has no scenes yet."
                  : shown.Count(x => x.Uuid != _live) == 0 && find.Length > 0 ? "No other scene matches “" + find + "”." : "";
        Hint.Visibility = Hint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Hint.Visibility == Visibility.Visible) Hint.Margin = new Thickness(0, 0, 0, 8);
        var z = Sizes[Size];
        Scroller.MaxHeight = 3 * (z.h + z.gap) + 6; // three rows, and a peek at the fourth so it's clear there's more
        Buttons.Children.Clear();
        foreach (var s in shown)
        {
            bool live = s.Uuid == _live;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (live)
                content.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = s.Name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button
            {
                Content = content,
                Margin = new Thickness(0, 0, z.gap, z.gap),
                MinWidth = z.minW,
                MinHeight = z.h,
                Height = z.h,
                FontSize = z.font,
                Padding = new Thickness(z.pad, 0, z.pad, 0),
                ToolTip = live ? s.Name + " is live" : "Put \"" + s.Name + "\" live",
            };
            if (live)
            {
                b.Style = (Style)FindResource("Primary");
                b.Background = b.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)); // same red as the LIVE pill
            }
            var uuid = s.Uuid;
            b.Click += async (_, _) =>
            {
                if (uuid == _live) return;
                try
                {
                    await Hub.Obs.SwitchSceneAsync(uuid);
                    Log.Write("Scene switched to \"" + s.Name + "\" (Go LIVE page)");
                    _live = uuid;
                    Render();
                }
                catch (Exception ex)
                {
                    Log.Write("Couldn't switch scene: " + ex.Message);
                    Hint.Text = "Couldn't switch: " + ex.Message;
                    Hint.Visibility = Visibility.Visible;
                }
            };
            Buttons.Children.Add(b);
        }
    }

    // S / M / L at the right of the heading; the chosen one is highlighted and remembered.
    void BuildSizePicker()
    {
        SizePicker.Children.Clear();
        foreach (var (key, label, tip) in new[] { ("small", "S", "Small buttons"), ("medium", "M", "Medium buttons"), ("large", "L", "Large buttons") })
        {
            bool on = key == Size;
            var b = new Button
            {
                Content = label,
                Style = (Style)FindResource(on ? "Primary" : "Ghost"),
                MinHeight = 0, MinWidth = 28, Height = 26, Padding = new Thickness(0), FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = tip,
            };
            b.Click += (_, _) =>
            {
                Hub.Settings.SceneButtonSize = key;
                Hub.SaveSettings();
                BuildSizePicker();
                Render();
            };
            SizePicker.Children.Add(b);
        }
    }
}
