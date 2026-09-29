using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Three short welcome pages: what MayhemDeck does, that it's free, and how to say thanks.
// SetupWizard shows it before the setup steps until it's been seen once; Settings can show it again.
// Keys: Enter or Right = next, Left = back, Esc = skip.
public partial class WelcomeOverlay : UserControl
{
    const string TikTokUrl = "https://www.tiktok.com/@obiwayn3";
    const string GitHubUrl = "https://github.com/Obiwayne/MayhemDeck";

    readonly FrameworkElement[] _pages;
    int _page;
    bool _replay, _closing;
    Action _done;

    public WelcomeOverlay()
    {
        InitializeComponent();
        _pages = new FrameworkElement[] { Page1, Page2, Page3 };
        Visibility = Visibility.Collapsed;
    }

    public bool IsShowing => Visibility == Visibility.Visible && !_closing;

    // replay: shown again from Settings (last button says Done rather than leading into setup).
    // upgraded: settings were already there, so mention the rename.
    public void Show(bool replay, bool upgraded, Action done)
    {
        _replay = replay;
        _done = done;
        _closing = false;
        RenameNote.Visibility = upgraded ? Visibility.Visible : Visibility.Collapsed;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        Visibility = Visibility.Visible;
        FitPages();
        if (!IsLoaded) Loaded += FitWhenLoaded; // shown while the window is still being built: measure again once it's up
        GoTo(0, animate: false);
        AnimateIn();
        Dispatcher.BeginInvoke(() => Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    void GoTo(int page, bool animate = true)
    {
        if (page < 0 || page >= _pages.Length) return;
        int from = _page;
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        bool last = page == _pages.Length - 1;
        BackButton.Visibility = page == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = last ? (_replay ? "Done" : "Let's get set up") : "Next";
        SkipButton.Visibility = last ? Visibility.Hidden : Visibility.Visible;
        BuildDots();
        if (animate && from != page) SlidePage(page > from ? 1 : -1);
    }

    // Every page gets the height of the tallest, so the card (and the buttons under it) don't jump between pages.
    void FitPages()
    {
        double width = Math.Max(200, (CardBox.MaxWidth - Pages.Margin.Left - Pages.Margin.Right));
        double tallest = 0;
        foreach (var page in _pages)
        {
            var was = page.Visibility;
            page.Visibility = Visibility.Visible;
            page.Measure(new Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, page.DesiredSize.Height);
            page.Visibility = was;
        }
        Pages.MinHeight = Math.Ceiling(tallest);
    }

    void FitWhenLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= FitWhenLoaded;
        FitPages();
    }

    void BuildDots()
    {
        Dots.Children.Clear();
        for (int i = 0; i < _pages.Length; i++)
        {
            int target = i;
            bool current = i == _page;
            var dot = new Border
            {
                Width = current ? 22 : 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0),
                Background = (Brush)FindResource(current ? "AccentBrush" : "Panel3Brush"),
                Cursor = Cursors.Hand, ToolTip = $"Page {i + 1} of {_pages.Length}",
            };
            // A bigger invisible hit area around the small dot.
            var hit = new Border { Background = Brushes.Transparent, Padding = new Thickness(0, 8, 0, 8), Child = dot };
            hit.MouseLeftButtonUp += (_, _) => GoTo(target);
            Dots.Children.Add(hit);
        }
    }

    // ---- Animation ----

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    void AnimateIn()
    {
        var t = TimeSpan.FromMilliseconds(420);
        CardBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, t) { EasingFunction = Ease });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, t) { EasingFunction = Ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, t) { EasingFunction = Ease });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, t) { EasingFunction = Ease });
    }

    void SlidePage(int direction)
    {
        var t = TimeSpan.FromMilliseconds(280);
        Pages.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, t) { EasingFunction = Ease });
        PageShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * direction, 0, t) { EasingFunction = Ease });
    }

    // ---- Navigation ----

    void Next()
    {
        if (_page < _pages.Length - 1) GoTo(_page + 1);
        else Close();
    }

    void Back() => GoTo(_page - 1);

    void Close()
    {
        if (_closing || Visibility != Visibility.Visible) return;
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _closing = false;
        };
        BeginAnimation(OpacityProperty, fade);
        var done = _done;
        _done = null;
        done?.Invoke();
    }

    void Next_Click(object sender, RoutedEventArgs e) => Next();
    void Back_Click(object sender, RoutedEventArgs e) => Back();
    void Skip_Click(object sender, RoutedEventArgs e) => Close();
    void Follow_Click(object sender, RoutedEventArgs e) => OpenUrl(TikTokUrl);
    void GitHub_Click(object sender, RoutedEventArgs e) => OpenUrl(GitHubUrl);

    // Arrows and Esc work anywhere on the overlay; they're caught before a focused button can use them.
    void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsShowing) return;
        switch (e.Key)
        {
            case Key.Right: e.Handled = true; Next(); break;
            case Key.Left: e.Handled = true; Back(); break;
            case Key.Escape: e.Handled = true; Close(); break;
        }
    }

    // Enter: a focused button (say, Follow) handles its own Enter first; otherwise it means Next.
    void Overlay_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsShowing || e.Handled) return;
        if (e.Key == Key.Enter) { e.Handled = true; Next(); }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { Log.Write("Couldn't open " + url + ": " + e.Message); }
    }
}
