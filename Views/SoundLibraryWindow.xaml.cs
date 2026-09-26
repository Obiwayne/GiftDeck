using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class SoundLibraryWindow : Window
{
    // The saved file the caller should use, set when the user picks a sound.
    public string ChosenFile { get; private set; }

    public SoundLibraryWindow(string initialQuery = "")
    {
        InitializeComponent();
        SearchBox.Text = initialQuery ?? "";
        Loaded += async (s, e) =>
        {
            SearchBox.Focus();
            if (SearchBox.Text.Trim().Length > 0) await Search();
        };
    }

    async void Search_Click(object sender, RoutedEventArgs e) => await Search();

    async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) await Search();
    }

    async Task Search()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return;
        Status.Text = "Searching";
        Results.ItemsSource = null;
        UseButton.IsEnabled = false;
        try
        {
            var list = await SoundLibrary.SearchAsync(q);
            Results.ItemsSource = list;
            Status.Text = list.Count == 0 ? "Nothing found. Try different words." : $"{list.Count} sounds. Select one and press Play.";
        }
        catch (Exception ex)
        {
            Status.Text = "Could not reach the sound library: " + ex.Message;
        }
    }

    void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseButton.IsEnabled = Results.SelectedItem != null;
    }

    async void Results_DoubleClick(object sender, MouseButtonEventArgs e) => await Preview();

    async void Play_Click(object sender, RoutedEventArgs e) => await Preview();

    async Task Preview()
    {
        if (!(Results.SelectedItem is SoundResult r)) return;
        Hub.Sounds.StopAll();
        Status.Text = "Loading " + r.Title;
        try
        {
            var file = await SoundLibrary.PreviewFileAsync(r);
            Hub.Sounds.Play(file, 100);
            Status.Text = "Playing " + r.Title;
        }
        catch (Exception ex)
        {
            Status.Text = "Could not play it: " + ex.Message;
        }
    }

    void Stop_Click(object sender, RoutedEventArgs e)
    {
        Hub.Sounds.StopAll();
        Status.Text = "Stopped.";
    }

    async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (!(Results.SelectedItem is SoundResult r)) return;
        UseButton.IsEnabled = false;
        Status.Text = "Saving " + r.Title;
        try
        {
            ChosenFile = await SoundLibrary.SaveAsync(r);
            Hub.Sounds.StopAll();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            Status.Text = "Could not save it: " + ex.Message;
            UseButton.IsEnabled = true;
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Hub.Sounds.StopAll();
        DialogResult = false;
        Close();
    }
}
