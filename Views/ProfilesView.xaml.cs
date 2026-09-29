using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GiftDeck.Services;

namespace GiftDeck.Views;

public partial class ProfilesView : UserControl
{
    ProfileService P => Hub.Profiles;
    Window Owner => Window.GetWindow(this);

    public ProfilesView()
    {
        InitializeComponent();
        P.Changed += () => Dispatcher.BeginInvoke(Refresh);
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); };
        Refresh();
    }

    void Refresh()
    {
        ProfileList.ItemsSource = P.List().Select(n => new Row(n, n == P.Active)).ToList();
    }

    void Run(Action action, string done = null)
    {
        try
        {
            action();
            Message.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
            Message.Text = done ?? "";
        }
        catch (Exception ex)
        {
            Message.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            Message.Text = ex.Message;
        }
        Refresh();
    }

    static string Name(object sender) => (string)((FrameworkElement)sender).Tag;

    void Switch_Click(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.SwitchProfile(Name(sender));

    void New_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(Owner, "Name for the new profile (e.g. the game)", "", "Create");
        if (name == null) return;
        Run(() =>
        {
            var created = P.Create(name);
            if (AppDialog.Show(Owner, $"Switch to \"{created}\" now? It starts empty: add events on the Events page.", "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                Dispatcher.BeginInvoke(() => (Owner as MainWindow)?.SwitchProfile(created));
        }, $"Created \"{name}\".");
    }

    void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var from = Name(sender);
        var name = PromptWindow.Ask(Owner, $"Name for the copy of \"{from}\"", from + " copy", "Duplicate");
        if (name == null) return;
        Run(() => P.Create(name, from), $"\"{name}\" is a copy of \"{from}\".");
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var from = Name(sender);
        var name = PromptWindow.Ask(Owner, $"New name for \"{from}\"", from, "Rename");
        if (name == null) return;
        Run(() => P.Rename(from, name), $"Renamed to \"{name}\".");
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        var name = Name(sender);
        if (AppDialog.Show(Owner, $"Delete the \"{name}\" profile and all its events and overlays? Export it first if you might want it back.", "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        Run(() => P.Delete(name), $"Deleted \"{name}\".");
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var name = Name(sender);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = name + ProfileService.FileExtension,
            Filter = "MayhemDeck profile|*" + ProfileService.FileExtension,
            Title = "Export profile",
        };
        if (dlg.ShowDialog(Owner) != true) return;
        Run(() => P.Export(name, dlg.FileName), $"Exported \"{name}\" to {dlg.FileName}");
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "MayhemDeck profile|*" + ProfileService.FileExtension + "|All files|*.*", Title = "Import profile" };
        if (dlg.ShowDialog(Owner) != true) return;
        string imported = null;
        Run(() => imported = P.Import(dlg.FileName), null);
        if (imported != null) Message.Text = $"Imported as \"{imported}\". Press \"Use this profile\" to switch to it.";
    }

    public class Row
    {
        public Row(string name, bool active)
        {
            Name = name;
            Active = active;
            Summary = Describe(name);
        }

        public string Name { get; }
        public bool Active { get; }
        public string Summary { get; }
        public Visibility ActiveVisibility => Active ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SwitchVisibility => Active ? Visibility.Collapsed : Visibility.Visible;

        // "12 events · 18 board tiles · Grand Theft Auto V"
        static string Describe(string name)
        {
            var dir = ProfileService.DirOf(name);
            var parts = new List<string>();
            try
            {
                var rules = Path.Combine(dir, "rules.json");
                int events = File.Exists(rules) ? JsonDocument.Parse(File.ReadAllText(rules)).RootElement.GetArrayLength() : 0;
                parts.Add(events == 1 ? "1 event" : $"{events} events");
                var ov = Path.Combine(dir, "overlays.json");
                if (File.Exists(ov) && JsonDocument.Parse(File.ReadAllText(ov)).RootElement.TryGetProperty("Menu", out var menu)
                    && menu.TryGetProperty("Tiles", out var tiles) && tiles.GetArrayLength() > 0)
                    parts.Add($"{tiles.GetArrayLength()} board tiles");
                var st = Path.Combine(dir, "stream.json");
                if (File.Exists(st) && JsonDocument.Parse(File.ReadAllText(st)).RootElement.TryGetProperty("CategoryName", out var cat)
                    && !string.IsNullOrEmpty(cat.GetString()))
                    parts.Add(cat.GetString());
            }
            catch { }
            return string.Join(" · ", parts);
        }
    }
}
