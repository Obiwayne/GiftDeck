using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Title, category and LIVE options, edited right on the Go LIVE page.
public partial class StreamDetailsPanel : UserControl
{
    bool _loading = true;
    readonly DispatcherTimer _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };

    TikTokLiveService Tt => Hub.TikTok;

    // The Go LIVE page does the restart (it owns OBS and the LIVE); this panel only asks for it.
    public event Action RestartRequested;

    public StreamDetailsPanel()
    {
        InitializeComponent();
        _searchDebounce.Tick += async (a, b) => { _searchDebounce.Stop(); await SearchCategories(); };
        IsVisibleChanged += (a, b) => { if (IsVisible) Load(); }; // an import on Stream Setup may have changed them
        Tt.StatusChanged += () => Dispatcher.BeginInvoke(UpdateLiveBox);
        Load();
    }

    void Load()
    {
        _loading = true;
        var s = Tt.State;
        TitleBox.Text = s.Title;
        CategoryBox.Text = s.CategoryName;
        MatureBox.IsChecked = s.Mature;
        CategoryList.Visibility = Visibility.Collapsed;
        UpdateCategoryChosen();
        _loading = false;
        UpdateLiveBox();
    }

    // While LIVE: say what the LIVE is using, and offer a restart once something differs.
    void UpdateLiveBox()
    {
        var s = Tt.State;
        if (!Tt.Live || s.LiveTitle == null) { LiveBox.Visibility = Visibility.Collapsed; return; }
        LiveBox.Visibility = Visibility.Visible;
        LiveNow.Text = "Your LIVE now: “" + (s.LiveTitle.Length == 0 ? "LIVE" : s.LiveTitle) + "”"
                       + (string.IsNullOrEmpty(s.LiveCategoryName) ? "" : " · " + s.LiveCategoryName)
                       + (s.LiveMature ? " · 18+" : "");
        bool differs = TitleBox.Text.Trim() != s.LiveTitle || (s.CategoryName ?? "") != s.LiveCategoryName || s.Mature != s.LiveMature;
        LiveHelp.Text = differs
            ? "TikTok can't change a LIVE while it's running. To use the new details, GiftDeck ends this LIVE and starts a new one straight away (about 20 seconds). Viewers have to rejoin, and TikTok's likes and viewer count start again; GiftDeck's own totals keep counting."
            : "To change the title, category or 18+ while LIVE, edit them above. GiftDeck can then restart the LIVE with them.";
        LiveNow.Foreground = (System.Windows.Media.Brush)FindResource(differs ? "WarnBrush" : "TextBrush");
        RestartRow.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
    }

    void Title_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateLiveBox();
    }

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        Details_Changed(sender, e); // the title box may still have focus: take what's typed
        RestartRequested?.Invoke();
    }

    // Back to what the running LIVE uses.
    void Undo_Click(object sender, RoutedEventArgs e)
    {
        var s = Tt.State;
        if (s.LiveTitle == null) return;
        s.Title = s.LiveTitle;
        s.CategoryName = s.LiveCategoryName;
        s.CategoryId = s.LiveCategoryId;
        s.Mature = s.LiveMature;
        Tt.Save();
        Tt.NotifyChanged();
        Load();
    }

    public void SetBusy(bool busy)
    {
        RestartButton.IsEnabled = !busy;
        RestartButton.Content = busy ? "Restarting the LIVE…" : "Restart LIVE with these details";
    }

    void UpdateCategoryChosen()
    {
        var s = Tt.State;
        CategoryChosen.Text = string.IsNullOrEmpty(s.CategoryName) ? "No category chosen (TikTok will use Other)."
                            : string.IsNullOrEmpty(s.CategoryId) ? "Not one of TikTok's categories: pick one from the list, or TikTok uses Other." : "";
        CategoryChosen.Visibility = CategoryChosen.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Details_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Tt.State;
        s.Title = TitleBox.Text.Trim();
        s.Mature = MatureBox.IsChecked == true;
        Tt.Save();
        Tt.NotifyChanged();
        UpdateLiveBox();
    }

    void Category_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    async Task SearchCategories()
    {
        var q = CategoryBox.Text.Trim();
        if (q.Length == 0) { CategoryList.Visibility = Visibility.Collapsed; return; }
        try
        {
            var list = await Tt.SearchCategoriesAsync(q);
            CategoryList.ItemsSource = list;
            CategoryList.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Text = "";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; ErrorText.Visibility = Visibility.Visible; }
    }

    void Category_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (!(CategoryList.SelectedItem is TikTokCategory c)) return;
        var s = Tt.State;
        s.CategoryName = c.Name;
        s.CategoryId = c.Id;
        Tt.Save();
        Tt.NotifyChanged();
        _loading = true;
        CategoryBox.Text = c.Name;
        _loading = false;
        CategoryList.Visibility = Visibility.Collapsed;
        UpdateCategoryChosen();
        UpdateLiveBox();
    }
}
