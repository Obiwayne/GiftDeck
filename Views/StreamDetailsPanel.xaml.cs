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

    public StreamDetailsPanel()
    {
        InitializeComponent();
        _searchDebounce.Tick += async (a, b) => { _searchDebounce.Stop(); await SearchCategories(); };
        IsVisibleChanged += (a, b) => { if (IsVisible) Load(); }; // an import on Stream Setup may have changed them
        Load();
    }

    void Load()
    {
        _loading = true;
        var s = Tt.State;
        TitleBox.Text = s.Title;
        CategoryBox.Text = s.CategoryName;
        MatureBox.IsChecked = s.Mature;
        AutoObsBox.IsChecked = s.AutoObs;
        ResetTotalsBox.IsChecked = s.ResetTotalsOnLive;
        CategoryList.Visibility = Visibility.Collapsed;
        UpdateCategoryChosen();
        _loading = false;
    }

    void UpdateCategoryChosen()
    {
        var s = Tt.State;
        CategoryChosen.Text = string.IsNullOrEmpty(s.CategoryName) ? "No category chosen (TikTok will use Other)." : "Category: " + s.CategoryName + (string.IsNullOrEmpty(s.CategoryId) ? " (Other)" : "");
    }

    void Details_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Tt.State;
        s.Title = TitleBox.Text.Trim();
        s.Mature = MatureBox.IsChecked == true;
        s.AutoObs = AutoObsBox.IsChecked == true;
        s.ResetTotalsOnLive = ResetTotalsBox.IsChecked == true;
        Tt.Save();
        Tt.NotifyChanged();
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
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
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
    }
}
