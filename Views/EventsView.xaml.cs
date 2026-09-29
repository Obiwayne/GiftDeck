using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class EventsView : UserControl
{
    readonly ICollectionView _view;

    public EventsView()
    {
        InitializeComponent();
        // Its own view, so searching here doesn't filter the events anywhere else.
        _view = new CollectionViewSource { Source = Hub.Rules.Rules }.View;
        _view.Filter = Matches;
        RuleList.ItemsSource = _view;
        Loaded += (s, e) => { Hub.Rules.Rules.CollectionChanged += Rules_Changed; UpdateEmpty(); };
        Unloaded += (s, e) => Hub.Rules.Rules.CollectionChanged -= Rules_Changed;
        UpdateEmpty();
    }

    void Rules_Changed(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateEmpty();

    bool Matches(object o)
    {
        var f = SearchBox.Text.Trim();
        if (f.Length == 0 || !(o is Rule r)) return true;
        return (r.Name ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
            || (r.TriggerSummary ?? "").Contains(f, StringComparison.OrdinalIgnoreCase)
            || (r.ActionsSummary ?? "").Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateEmpty();
    }

    void UpdateEmpty()
    {
        int all = Hub.Rules.Rules.Count;
        int shown = _view.Cast<object>().Count();
        bool filtered = SearchBox.Text.Trim().Length > 0;
        Empty.Text = all == 0 ? "No events yet. Click New event to add your first one." : "No events match that search.";
        Empty.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = all == 0 ? "" : filtered ? $"{shown} of {all} events" : all == 1 ? "1 event" : $"{all} events";
    }

    void RuleList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double click on a row, not on its buttons or the scroll bar.
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(RuleList, d) is ListBoxItem item
            && item.DataContext is Rule r && !(e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase))
            EditRule(r);
    }

    void RuleList_KeyDown(object sender, KeyEventArgs e)
    {
        if (!(RuleList.SelectedItem is Rule r)) return;
        if (e.Key == Key.Enter) { e.Handled = true; EditRule(r); }
        else if (e.Key == Key.Delete) { e.Handled = true; DeleteRule(r); }
    }

    static Rule RuleOf(object sender) => (sender as FrameworkElement)?.Tag as Rule;

    void New_Click(object sender, RoutedEventArgs e)
    {
        var win = new RuleEditorWindow(new Rule()) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
        {
            Hub.Rules.Rules.Add(win.Result);
            Hub.Rules.Save();
        }
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (rule != null) EditRule(rule);
    }

    void EditRule(Rule rule)
    {
        var win = new RuleEditorWindow(rule) { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true)
        {
            int i = Hub.Rules.Rules.IndexOf(rule);
            if (i >= 0) Hub.Rules.Rules[i] = win.Result; else Hub.Rules.Rules.Add(win.Result);
            Hub.Rules.Save();
        }
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (rule == null) return;
        var copy = rule.Clone();
        copy.Name = rule.Name + " (copy)";
        int i = Hub.Rules.Rules.IndexOf(rule);
        Hub.Rules.Rules.Insert(i < 0 ? Hub.Rules.Rules.Count : i + 1, copy);
        Hub.Rules.Save();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (rule != null) DeleteRule(rule);
    }

    void DeleteRule(Rule rule)
    {
        if (AppDialog.Show(Window.GetWindow(this), $"Delete \"{rule.Name}\"? This can't be undone.", "Delete event", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Hub.Rules.Rules.Remove(rule);
        Hub.Rules.Save();
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (rule == null || !(sender is Button button)) return;
        // Key presses go to the window in front. Without "bring a window to the front" that's GiftDeck itself,
        // so give the streamer a moment to click into their game first.
        if (rule.Actions.Any(a => a.Type == ActionType.KeyPress) && !Hub.Settings.FocusWindowBeforeKeys)
        {
            var label = button.Content;
            button.IsEnabled = false;
            for (int i = 3; i > 0; i--)
            {
                button.Content = i.ToString();
                TestStatus.Text = $"Click into your game: \"{rule.Name}\" presses its keys in {i}";
                await Task.Delay(1000);
            }
            button.Content = label;
            button.IsEnabled = true;
        }
        Hub.Rules.Test(rule);
        TestStatus.Text = $"Tested \"{rule.Name}\". What it did is in the Dashboard feed.";
    }

    void Enabled_Click(object sender, RoutedEventArgs e) => Hub.Rules.Save();
}
