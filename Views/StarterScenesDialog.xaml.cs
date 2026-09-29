using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GiftDeck.Services;

namespace GiftDeck.Views;

// "Create starter scenes": pick which of the ready-made scenes to add. Ones already in OBS are shown but
// can't be picked. Selected holds the chosen names after OK.
public partial class StarterScenesDialog : Window
{
    public List<string> Selected { get; } = new List<string>();

    readonly List<CheckBox> _boxes = new List<CheckBox>();

    public StarterScenesDialog(Window owner, IEnumerable<string> existing)
    {
        InitializeComponent();
        Owner = owner;
        CoverOwner(owner);

        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var name in StarterScenes.Names) Rows.Children.Add(Row(name, have.Contains(name)));
        // Nothing picked yet when some already exist; everything missing picked when starting from scratch.
        // Room for all six on a normal window; it scrolls only when the window is small.
        SizeChanged += (_, _) => SceneList.MaxHeight = Math.Max(220, ActualHeight - 290);
        bool fresh = !StarterScenes.Names.Any(have.Contains);
        foreach (var b in _boxes.Where(b => b.IsEnabled)) b.IsChecked = fresh;
        UpdateButtons();

        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            (_boxes.FirstOrDefault(b => b.IsEnabled) ?? (UIElement)CreateButton).Focus();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }

    // Lies over the app window exactly, so the whole app goes dark behind the picker.
    void CoverOwner(Window owner)
    {
        if (owner == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; Width = 800; Height = 700; return; }
        if (owner.WindowState == WindowState.Maximized)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var area = SystemParameters.WorkArea;
            Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = owner.Left; Top = owner.Top; Width = owner.ActualWidth; Height = owner.ActualHeight;
        }
    }

    FrameworkElement Row(string name, bool exists)
    {
        var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0), IsEnabled = !exists, Tag = name };
        box.Click += (_, _) => UpdateButtons();
        _boxes.Add(box);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (exists)
            title.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)FindResource("Panel3Brush"),
                Child = new TextBlock { Text = "Already in OBS", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("MutedBrush") },
            });
        text.Children.Add(title);
        text.Children.Add(new TextBlock { Text = StarterScenes.Describe(name), Foreground = (Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var art = Drawing(name);
        Grid.SetColumn(art, 1);
        Grid.SetColumn(text, 2);
        grid.Children.Add(box);
        grid.Children.Add(art);
        grid.Children.Add(text);

        var row = new Border
        {
            Style = (Style)FindResource("SubCard"), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 8),
            Cursor = exists ? Cursors.Arrow : Cursors.Hand, Opacity = exists ? 0.55 : 1, Child = grid,
            BorderThickness = new Thickness(1),
        };
        // The whole row toggles the box, and shows it's picked.
        void Show() => row.BorderBrush = box.IsChecked == true ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("LineBrush");
        box.Checked += (_, _) => Show();
        box.Unchecked += (_, _) => Show();
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (exists || e.OriginalSource is CheckBox) return;
            box.IsChecked = box.IsChecked != true;
            UpdateButtons();
        };
        Show();
        return row;
    }

    // A small portrait drawing of the scene's layout.
    static FrameworkElement Drawing(string name)
    {
        var c = new Canvas { Width = 34, Height = 60, ClipToBounds = true };
        void Rect(double x, double y, double w, double h, string color, double r = 2) =>
            c.Children.Add(new Rectangle { Width = w, Height = h, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)) }.At(x, y));
        void Person(double cx, double top, double scale)
        {
            c.Children.Add(new Ellipse { Width = 8 * scale, Height = 8 * scale, Fill = Brushes.White, Opacity = 0.85 }.At(cx - 4 * scale, top));
            c.Children.Add(new Ellipse { Width = 16 * scale, Height = 10 * scale, Fill = Brushes.White, Opacity = 0.85 }.At(cx - 8 * scale, top + 9 * scale));
        }
        Rect(0, 0, 34, 60, "#1E1230", 4);   // the dark background
        switch (name)
        {
            case StarterScenes.CamGame:
                Rect(0, 0, 34, 24, "#4F46E5", 0); Person(17, 4, 0.9);
                Rect(0, 26, 34, 20, "#10B981", 0); Rect(4, 30, 12, 3, "#ECFDF5", 1); Rect(4, 36, 20, 3, "#A7F3D0", 1);
                break;
            case StarterScenes.JustCam:
                Rect(0, 0, 34, 60, "#4F46E5", 4); Person(17, 16, 1.4);
                break;
            case StarterScenes.JustScreen:
                Rect(0, 20, 34, 20, "#10B981", 0); Rect(4, 24, 12, 3, "#ECFDF5", 1); Rect(4, 30, 20, 3, "#A7F3D0", 1);
                break;
            default: // title cards
                Rect(5, 22, 24, 5, "#FFFFFF", 1.5); Rect(9, 30, 16, 3, "#A78BFA", 1);
                break;
        }
        return new Border { Child = c, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
    }

    void UpdateButtons()
    {
        var picked = _boxes.Count(b => b.IsEnabled && b.IsChecked == true);
        var open = _boxes.Count(b => b.IsEnabled);
        CreateButton.IsEnabled = picked > 0;
        CreateButton.Content = picked == 0 ? "Pick a scene" : picked == 1 ? "Create 1 scene" : $"Create {picked} scenes";
        SelectAll.IsEnabled = open > 0;
        SelectAll.IsChecked = open > 0 && picked == open ? true : picked == 0 ? false : null;
        SelectAll.Content = open == 0 ? "You have them all" : "Select all";
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        bool on = _boxes.Where(b => b.IsEnabled).Any(b => b.IsChecked != true);
        foreach (var b in _boxes.Where(b => b.IsEnabled)) b.IsChecked = on;
        UpdateButtons();
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        Selected.AddRange(_boxes.Where(b => b.IsEnabled && b.IsChecked == true).Select(b => (string)b.Tag));
        if (Selected.Count == 0) return;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => DialogResult = false;
    void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks on the card don't reach the backdrop
}

static class CanvasExt
{
    public static T At<T>(this T el, double x, double y) where T : UIElement
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }
}
