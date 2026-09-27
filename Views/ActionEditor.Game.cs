using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// "Run a game command": pick a game, find the command (games like GTA have 500+, so a search box, a
// category filter and a virtualized list), fill in its fields. Target id -> Text, command id -> Text2,
// field values -> Args (a JSON object).
public partial class ActionEditor
{
    const string AllCategories = "All categories";

    List<GameCatalogItem> _gameCatalog = new List<GameCatalogItem>();
    ListCollectionView _commandView;
    readonly List<(GameCommandArg Arg, FrameworkElement Input)> _argInputs = new List<(GameCommandArg, FrameworkElement)>();
    GameCommandInfo _argsBuiltFor;
    string _argsKey; // which command (and fields) the fields were built for, so a refresh keeps what is being typed
    bool _gameLoading;
    bool _subscribed;

    void SubscribeGameLink(bool on)
    {
        if (Hub.GameLink == null || on == _subscribed) return;
        _subscribed = on;
        if (on) Hub.GameLink.Changed += OnGameLinkChanged;
        else Hub.GameLink.Changed -= OnGameLinkChanged;
    }

    // A mod connected or went away: refresh the list and the "connected" note without losing what was typed.
    void OnGameLinkChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (Action?.Type == ActionType.GameCommand) PopulateGame();
    }));

    void PopulateGame()
    {
        _gameLoading = true;
        try
        {
            var games = Hub.GameLink.Games().Select(g => new Choice(g.Id, g.Name)).ToList();
            var savedGame = GameLinkService.GameOf(Action.Text);
            if (savedGame.Length > 0 && !games.Any(g => (string)g.Value == savedGame))
                games.Add(new Choice(savedGame, Hub.GameLink.GameName(savedGame)));
            var current = (GameCombo.SelectedItem as Choice)?.Value as string;
            GameCombo.ItemsSource = games;
            var pick = current ?? (savedGame.Length > 0 ? savedGame : null);
            GameCombo.SelectedItem = games.FirstOrDefault(g => (string)g.Value == pick) ?? games.FirstOrDefault();
            NoGames.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GameCombo.IsEnabled = games.Count > 0;
            LoadCommands();
        }
        finally { _gameLoading = false; }
    }

    string SelectedGame => (GameCombo.SelectedItem as Choice)?.Value as string ?? "";

    void LoadCommands()
    {
        var game = SelectedGame;
        _gameCatalog = game.Length == 0 ? new List<GameCatalogItem>() : Hub.GameLink.CatalogFor(game);

        var keepCategory = CategoryCombo.SelectedItem as string;
        var categories = new List<string> { AllCategories };
        categories.AddRange(_gameCatalog.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase));
        CategoryCombo.ItemsSource = categories;
        CategoryCombo.SelectedItem = categories.Contains(keepCategory) ? keepCategory : AllCategories;

        _commandView = new ListCollectionView(_gameCatalog) { Filter = CommandFilter };
        _commandView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(GameCatalogItem.Category)));
        CommandList.ItemsSource = _commandView;
        CommandPicker.Visibility = _gameCatalog.Count > 0 || game.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var chosen = _gameCatalog.FirstOrDefault(c => c.TargetId == Action.Text && c.Id == Action.Text2);
        CommandList.SelectedItem = chosen;
        if (chosen != null) CommandList.ScrollIntoView(chosen);
        UpdateCommandCount();
        ShowGameStatus(game);
        ShowChosen();
    }

    bool CommandFilter(object o)
    {
        var c = (GameCatalogItem)o;
        var cat = CategoryCombo.SelectedItem as string;
        if (!string.IsNullOrEmpty(cat) && cat != AllCategories && !string.Equals(c.Category, cat, StringComparison.OrdinalIgnoreCase)) return false;
        var q = CommandSearch.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        // Every word must match somewhere, so "spawn car" finds "Spawn Vehicle (car)".
        foreach (var word in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (c.Name.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0
                && c.Id.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0
                && c.Category.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0
                && (c.Description ?? "").IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }

    void UpdateCommandCount()
    {
        if (_commandView == null) return;
        int shown = _commandView.Count;
        CommandCount.Text = _gameCatalog.Count == 0
            ? "No commands for this game yet. Start the game with its mod, or install its pack on the Games page."
            : shown == _gameCatalog.Count ? $"{_gameCatalog.Count} commands" : $"{shown} of {_gameCatalog.Count} commands";
    }

    // "Chaos Mod V: not running   ·   GiftDeck GTA script: connected": every mod known for this game.
    void ShowGameStatus(string game)
    {
        var live = Hub.GameLink.Targets.Where(t => t.Game == game).ToList();
        var ids = live.Select(t => t.Id).Concat(_gameCatalog.Select(c => c.TargetId)).Distinct().ToList();
        if (ids.Count == 0) { GameStatus.Text = ""; return; }
        GameStatus.Text = string.Join("   ·   ", ids.Select(id =>
        {
            var t = live.FirstOrDefault(x => x.Id == id);
            var name = t?.Name ?? Hub.GameLink.TargetName(id);
            return t != null && t.Connected ? name + ": connected" + (string.IsNullOrWhiteSpace(t.Status) ? "" : " (" + t.Status + ")") : name + ": not running";
        }));
    }

    void GameCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _gameLoading || Action == null) return;
        _gameLoading = true;
        try { LoadCommands(); }
        finally { _gameLoading = false; }
    }

    void CommandSearch_Changed(object sender, TextChangedEventArgs e) { _commandView?.Refresh(); UpdateCommandCount(); }
    void CategoryCombo_Changed(object sender, SelectionChangedEventArgs e) { if (!_gameLoading) { _commandView?.Refresh(); UpdateCommandCount(); } }

    void CommandList_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _gameLoading || Action == null) return;
        if (CommandList.SelectedItem is not GameCatalogItem item) return; // filtered out of view: keep the choice
        if (item.TargetId == Action.Text && item.Id == Action.Text2) return;
        Action.Text = item.TargetId;
        Action.Text2 = item.Id;
        Action.Args = DefaultArgs(item.Command);
        _argsKey = null;
        GameTestResult.Text = "";
        ShowChosen();
    }

    static string DefaultArgs(GameCommandInfo c)
    {
        if (c.Args.Count == 0) return "";
        var o = new JsonObject();
        foreach (var a in c.Args) o[a.Name] = ArgValue(a, a.Default);
        return o.ToJsonString();
    }

    static JsonNode ArgValue(GameCommandArg a, string text)
    {
        text ??= "";
        if (a.Type == "number" && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d == Math.Floor(d) && Math.Abs(d) < 1e15 ? JsonValue.Create((long)d) : JsonValue.Create(d);
        return JsonValue.Create(text); // text, a choice, or a template such as {count}
    }

    void ShowChosen()
    {
        var info = Hub.GameLink.FindCommand(Action.Text, Action.Text2);
        if (string.IsNullOrEmpty(Action.Text2))
        {
            ChosenCommand.Text = "Pick a command from the list.";
            ChosenDescription.Text = "";
        }
        else if (info == null)
        {
            ChosenCommand.Text = $"{Action.Text2}  ·  {Hub.GameLink.TargetName(Action.Text)}";
            ChosenDescription.Text = "This command isn't in the list right now. Start the game with its mod to load its commands; the event keeps it as it is.";
        }
        else
        {
            ChosenCommand.Text = $"{info.Name}  ·  {Hub.GameLink.TargetName(Action.Text)}";
            ChosenDescription.Text = info.Description ?? "";
        }
        ChosenDescription.Visibility = string.IsNullOrWhiteSpace(ChosenDescription.Text) ? Visibility.Collapsed : Visibility.Visible;
        GameTestButton.IsEnabled = !string.IsNullOrEmpty(Action.Text2);
        var key = info == null ? null : Action.Text + "/" + Action.Text2 + "/" + string.Join(",", info.Args.Select(a => a.Name + ":" + a.Type + ":" + string.Join("|", a.Choices)));
        if (key == null || key != _argsKey) BuildArgFields(info, key);
    }

    // One field per argument, filled from the saved values (or the command's defaults).
    void BuildArgFields(GameCommandInfo info, string key)
    {
        _argsBuiltFor = info;
        _argsKey = key;
        _argInputs.Clear();
        GameArgsPanel.Children.Clear();
        var saved = new JsonObject();
        try { if (!string.IsNullOrWhiteSpace(Action.Args) && JsonNode.Parse(Action.Args) is JsonObject o) saved = o; } catch { }

        foreach (var a in info?.Args ?? new List<GameCommandArg>())
        {
            var value = saved[a.Name] is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : a.Default;
            FrameworkElement input;
            if (a.Type == "choice")
            {
                var combo = new ComboBox { Width = 200, ItemsSource = a.Choices };
                combo.SelectedItem = a.Choices.Contains(value) ? value : a.Choices.FirstOrDefault();
                combo.SelectionChanged += (s, e) => CommitGameArgs();
                input = combo;
            }
            else
            {
                var box = new TextBox { Width = a.Type == "number" ? 110 : 240, Text = value ?? "" };
                box.TextChanged += (s, e) => CommitGameArgs();
                input = box;
            }
            var field = new StackPanel { Margin = new Thickness(0, 0, 12, 6) };
            field.Children.Add(new TextBlock { Text = a.Label + (a.Type == "number" ? " (number)" : ""), Style = (Style)FindResource("Label") });
            field.Children.Add(input);
            GameArgsPanel.Children.Add(field);
            _argInputs.Add((a, input));
        }
        bool any = _argInputs.Count > 0;
        GameArgsPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        GameArgsHint.Visibility = _argInputs.Any(x => x.Arg.Type == "text") ? Visibility.Visible : Visibility.Collapsed;
    }

    void CommitGameArgs()
    {
        if (_loading || Action == null || _argsBuiltFor == null) return;
        var o = new JsonObject();
        foreach (var (arg, input) in _argInputs)
            o[arg.Name] = ArgValue(arg, input is ComboBox c ? c.SelectedItem as string : ((TextBox)input).Text);
        Action.Args = o.Count == 0 ? "" : o.ToJsonString();
    }

    async void GameTest_Click(object sender, RoutedEventArgs e)
    {
        if (Action == null || string.IsNullOrEmpty(Action.Text2)) return;
        var ev = new LiveEvent { Type = "gift", UserId = "testuser", Nickname = "Test Viewer", GiftName = "Rose", Diamonds = 1, IsTest = true };
        GameTestButton.IsEnabled = false;
        GameTestResult.ClearValue(TextBlock.ForegroundProperty);
        GameTestResult.Text = "Running…";
        try
        {
            var r = await Hub.GameLink.RunAsync(Action.Text, Action.Text2, Action.Args, ev, s => RulesEngine.Template(s, ev));
            GameTestResult.Text = r.Ok ? "Done" + (string.IsNullOrWhiteSpace(r.Message) ? "." : ": " + r.Message)
                                       : "Didn't work: " + r.Message;
            GameTestResult.Foreground = (System.Windows.Media.Brush)FindResource(r.Ok ? "SuccessBrush" : "DangerBrush");
        }
        finally { GameTestButton.IsEnabled = true; }
    }
}
