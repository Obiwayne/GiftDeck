using System.Windows;
using System.Windows.Controls;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
        RuleList.ItemsSource = Hub.Rules.Rules;
        Hub.Rules.Rules.CollectionChanged += (s, e) => UpdateEmpty();
        UpdateEmpty();
    }

    void UpdateEmpty() => Empty.Visibility = Hub.Rules.Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
        if (rule == null) return;
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
        if (rule == null) return;
        if (MessageBox.Show($"Delete \"{rule.Name}\"?", "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hub.Rules.Rules.Remove(rule);
        Hub.Rules.Save();
    }

    void Test_Click(object sender, RoutedEventArgs e)
    {
        var rule = RuleOf(sender);
        if (rule != null) Hub.Rules.Test(rule);
    }

    void Enabled_Click(object sender, RoutedEventArgs e) => Hub.Rules.Save();
}
