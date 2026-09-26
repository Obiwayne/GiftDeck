using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace GiftDeck.Views;

// Attached property that draws hint text inside an empty TextBox:  local:Placeholder.Text="Search gifts"
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(null, OnTextChanged));

    public static string GetText(DependencyObject d) => (string)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string value) => d.SetValue(TextProperty, value);

    static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (!(d is TextBox box)) return;
        if (box.IsLoaded) Attach(box);
        else box.Loaded += (s, a) => Attach(box);
    }

    static void Attach(TextBox box)
    {
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer == null) return;
        var existing = layer.GetAdorners(box);
        if (existing != null && existing.OfType<PlaceholderAdorner>().Any()) return;
        var adorner = new PlaceholderAdorner(box);
        layer.Add(adorner);
        box.TextChanged += (s, a) => adorner.InvalidateVisual();
        box.GotKeyboardFocus += (s, a) => adorner.InvalidateVisual();
        box.LostKeyboardFocus += (s, a) => adorner.InvalidateVisual();
        box.IsVisibleChanged += (s, a) => adorner.InvalidateVisual();
        box.SizeChanged += (s, a) => adorner.InvalidateVisual();
    }

    class PlaceholderAdorner : Adorner
    {
        readonly TextBox _box;
        public PlaceholderAdorner(TextBox box) : base(box) { _box = box; IsHitTestVisible = false; }

        protected override void OnRender(DrawingContext dc)
        {
            var hint = GetText(_box);
            if (string.IsNullOrEmpty(hint) || _box.Text.Length > 0 || !_box.IsVisible || _box.ActualWidth < 20) return;
            var typeface = new Typeface(_box.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var brush = (Brush)Application.Current.TryFindResource("MutedBrush") ?? Brushes.Gray;
            var text = new FormattedText(hint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, _box.FontSize, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            text.MaxTextWidth = Math.Max(10, _box.ActualWidth - _box.Padding.Left - _box.Padding.Right - 2);
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            double y = (_box.ActualHeight - text.Height) / 2;
            dc.DrawText(text, new Point(_box.Padding.Left + 1, y));
        }
    }
}
