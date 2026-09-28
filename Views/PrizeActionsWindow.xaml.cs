using System.Collections.ObjectModel;
using System.Windows;
using GiftDeck.Models;

namespace GiftDeck.Views;

// Edits the actions of one Gift Spinner prize, with the same action editor the events use.
public partial class PrizeActionsWindow : Window, IActionHost
{
    readonly SpinnerEntry _entry;
    readonly ObservableCollection<RuleAction> _actions;
    readonly EditorGuard _guard;

    public PrizeActionsWindow(SpinnerEntry entry)
    {
        InitializeComponent();
        _entry = entry;
        Heading.Text = string.IsNullOrWhiteSpace(entry.Label) ? "Prize" : entry.Label;
        _actions = new ObservableCollection<RuleAction>(entry.Actions.Select(a => a.Clone()));
        ActionsList.ItemsSource = _actions;
        _actions.CollectionChanged += (s, e) => { UpdateNoActions(); _guard?.MarkDirty(); };
        UpdateNoActions();
        _guard = new EditorGuard(this, Save);
    }

    void UpdateNoActions() => NoActions.Visibility = _actions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void AddAction_Click(object sender, RoutedEventArgs e) => _actions.Add(new RuleAction { Type = ActionType.KeyPress });

    public void RemoveAction(RuleAction a) => _actions.Remove(a);

    public void MoveAction(RuleAction a, int delta)
    {
        int i = _actions.IndexOf(a);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _actions.Count) return;
        _actions.Move(i, j);
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();

    void Save()
    {
        for (int i = 0; i < _actions.Count; i++)
        {
            var problem = EditorGuard.CheckAction(_actions[i], i + 1);
            if (problem != null) { ErrorText.Text = problem; return; }
        }
        _entry.Actions = _actions.Select(a => a.Clone()).ToList();
        _entry.RefreshActionsText();
        DialogResult = true;
        Close();
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
