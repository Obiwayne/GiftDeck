using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class RuleEditorWindow : Window, IActionHost
{
    public Rule Result { get; private set; }

    readonly Rule _rule;
    readonly ObservableCollection<RuleAction> _actions;
    readonly ICollectionView _giftView;

    static readonly Choice[] TriggerChoices =
    {
        new Choice(TriggerType.Gift, "A specific gift is sent"),
        new Choice(TriggerType.AnyGift, "Any gift in a coin range"),
        new Choice(TriggerType.Follow, "Someone follows"),
        new Choice(TriggerType.Share, "Someone shares the LIVE"),
        new Choice(TriggerType.Like, "Likes reach a number"),
        new Choice(TriggerType.Chat, "A chat message or command"),
        new Choice(TriggerType.Subscribe, "Someone subscribes"),
        new Choice(TriggerType.Join, "Someone joins the LIVE"),
    };

    public RuleEditorWindow(Rule rule)
    {
        InitializeComponent();
        _rule = rule;
        Heading.Text = string.IsNullOrEmpty(rule.Name) ? "New event" : "Edit event";
        NameBox.Text = rule.Name;

        TriggerCombo.ItemsSource = TriggerChoices;
        TriggerCombo.SelectedItem = TriggerChoices.First(c => (TriggerType)c.Value == rule.Trigger.Type);

        _giftView = new CollectionViewSource { Source = Hub.Gifts.Gifts }.View;
        _giftView.Filter = o =>
        {
            var f = GiftFilter.Text.Trim();
            return f.Length == 0 || ((GiftInfo)o).Name.Contains(f, StringComparison.OrdinalIgnoreCase);
        };
        GiftCombo.ItemsSource = _giftView;
        GiftCombo.SelectedItem = Hub.Gifts.Find(rule.Trigger.GiftId, rule.Trigger.GiftName);

        GiftMinCoins.Text = rule.Trigger.Type == TriggerType.Gift ? rule.Trigger.MinCoins.ToString() : "0";
        AnyMin.Text = rule.Trigger.Type == TriggerType.AnyGift ? rule.Trigger.MinCoins.ToString() : "0";
        AnyMax.Text = rule.Trigger.MaxCoins.ToString();
        LikeBox.Text = rule.Trigger.MinLikes.ToString();
        ChatBox.Text = string.IsNullOrEmpty(rule.Trigger.ChatCommand) && rule.Trigger.Type != TriggerType.Chat ? "!" : rule.Trigger.ChatCommand;
        CooldownBox.Text = rule.CooldownSeconds.ToString();
        RepeatBox.IsChecked = rule.RepeatPerGift;
        MaxRepeatsBox.Text = (rule.RepeatPerGift ? rule.MaxRepeats : 10).ToString();

        _actions = new ObservableCollection<RuleAction>(rule.Actions.Select(a => a.Clone()));
        ActionsList.ItemsSource = _actions;
        _actions.CollectionChanged += (s, e) => UpdateNoActions();
        UpdateNoActions();
        UpdatePanels();
    }

    TriggerType SelectedTrigger => TriggerCombo.SelectedItem is Choice c ? (TriggerType)c.Value : TriggerType.Gift;

    void TriggerCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded || GiftPanel != null) UpdatePanels();
    }

    void UpdatePanels()
    {
        if (GiftPanel == null) return;
        var t = SelectedTrigger;
        GiftPanel.Visibility = t == TriggerType.Gift ? Visibility.Visible : Visibility.Collapsed;
        AnyGiftPanel.Visibility = t == TriggerType.AnyGift ? Visibility.Visible : Visibility.Collapsed;
        LikePanel.Visibility = t == TriggerType.Like ? Visibility.Visible : Visibility.Collapsed;
        ChatPanel.Visibility = t == TriggerType.Chat ? Visibility.Visible : Visibility.Collapsed;
        ComboPanel.Visibility = t == TriggerType.Gift || t == TriggerType.AnyGift ? Visibility.Visible : Visibility.Collapsed;
        CapPanel.Visibility = RepeatBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TriggerHint.Text = t switch
        {
            TriggerType.Gift => "Streak gifts like Rose count once, at the end of the streak, with the whole combo added up.",
            TriggerType.AnyGift => "Good for tiers: for example 1 to 99 coins does a small effect, 100 and up does a big one.",
            TriggerType.Like => "Likes arrive in batches. Each viewer is counted separately for the whole stream.",
            TriggerType.Chat => "Commands are matched at the start of the message and are not case sensitive.",
            TriggerType.Join => "Fires a lot on a busy LIVE. A cooldown is a good idea.",
            _ => "",
        };
    }

    void GiftFilter_Changed(object sender, TextChangedEventArgs e)
    {
        _giftView.Refresh();
        if (GiftFilter.Text.Trim().Length == 0) return;
        var selected = GiftCombo.SelectedItem;
        bool visible = selected != null && _giftView.Cast<object>().Contains(selected);
        if (!visible)
        {
            var first = _giftView.Cast<object>().FirstOrDefault();
            if (first != null) GiftCombo.SelectedItem = first;
        }
    }

    void UpdateNoActions() => NoActions.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void Repeat_Click(object sender, RoutedEventArgs e) => CapPanel.Visibility = RepeatBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    void AddAction_Click(object sender, RoutedEventArgs e)
    {
        _actions.Add(new RuleAction { Type = ActionType.KeyPress });
    }

    public void RemoveAction(RuleAction a) => _actions.Remove(a);

    public void MoveAction(RuleAction a, int delta)
    {
        int i = _actions.IndexOf(a);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _actions.Count) return;
        _actions.Move(i, j);
    }

    static int Int(TextBox box)
    {
        int.TryParse(box.Text.Trim(), out int v);
        return Math.Max(0, v);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var r = new Rule { Id = _rule.Id, Enabled = _rule.Enabled, Name = NameBox.Text.Trim(), CooldownSeconds = Int(CooldownBox) };
        var t = new RuleTrigger { Type = SelectedTrigger };
        bool giftTrigger = t.Type == TriggerType.Gift || t.Type == TriggerType.AnyGift;
        r.RepeatPerGift = giftTrigger && RepeatBox.IsChecked == true;
        r.MaxRepeats = r.RepeatPerGift ? Int(MaxRepeatsBox) : 0;
        switch (t.Type)
        {
            case TriggerType.Gift:
                if (!(GiftCombo.SelectedItem is GiftInfo g)) { Fail("Choose a gift."); return; }
                t.GiftId = g.Id;
                t.GiftName = g.Name;
                t.MinCoins = Int(GiftMinCoins);
                break;
            case TriggerType.AnyGift:
                t.MinCoins = Int(AnyMin);
                t.MaxCoins = Int(AnyMax);
                if (t.MaxCoins > 0 && t.MaxCoins < t.MinCoins) { Fail("Maximum coins must be at least the minimum."); return; }
                break;
            case TriggerType.Like:
                t.MinLikes = Math.Max(1, Int(LikeBox));
                break;
            case TriggerType.Chat:
                t.ChatCommand = ChatBox.Text.Trim();
                break;
        }
        r.Trigger = t;

        foreach (var a in _actions)
        {
            string problem = Validate(a);
            if (problem != null) { Fail(problem); return; }
            r.Actions.Add(a.Clone());
        }
        if (r.Name.Length == 0) r.Name = t.Summary();

        Result = r;
        DialogResult = true;
        Close();
    }

    static string Validate(RuleAction a)
    {
        switch (a.Type)
        {
            case ActionType.KeyPress:
                return KeySender.TryParse(a.Text, out _, out var err) ? null : "Press keys: " + err;
            case ActionType.Sound:
                return string.IsNullOrWhiteSpace(a.Text) ? "Play sound: choose a file." : null;
            case ActionType.ObsScene:
                return string.IsNullOrWhiteSpace(a.Text) ? "OBS scene: enter the scene name." : null;
            case ActionType.ObsCanvasScene:
                return string.IsNullOrWhiteSpace(a.Text) ? "Vertical canvas scene: enter the scene name." : null;
            case ActionType.ObsShowSource:
            case ActionType.ObsHideSource:
                return string.IsNullOrWhiteSpace(a.Text) ? "OBS source: enter the source name." : null;
            case ActionType.Tts:
                return string.IsNullOrWhiteSpace(a.Text) ? "Speak: enter the text to say." : null;
            case ActionType.RunProgram:
                return string.IsNullOrWhiteSpace(a.Text) ? "Run program: choose a program." : null;
        }
        return null;
    }

    void Fail(string message) => ErrorText.Text = message;

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
