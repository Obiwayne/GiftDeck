using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GiftDeck.Views;

// A colour setting: a swatch that opens a picker (quick colours, a colour square and a hue bar) next to the
// hex code. Text is the colour as "#RRGGBB" ("" = the default, when AllowEmpty). Changed fires once a colour
// is chosen: a click, the end of a drag, or a typed code.
public partial class ColorBox : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ColorBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ColorBox)d).ShowText()));

    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(nameof(AllowEmpty), typeof(bool), typeof(ColorBox),
        new PropertyMetadata(false, (d, e) => ((ColorBox)d).ShowText()));

    public static readonly RoutedEvent ChangedEvent = EventManager.RegisterRoutedEvent(nameof(Changed), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ColorBox));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value ?? ""); }

    // Blank means something here (e.g. "use the board's colour"): the picker offers "Use default".
    public bool AllowEmpty { get => (bool)GetValue(AllowEmptyProperty); set => SetValue(AllowEmptyProperty, value); }

    public event RoutedEventHandler Changed { add => AddHandler(ChangedEvent, value); remove => RemoveHandler(ChangedEvent, value); }

    // Bright colours that read well on a stream, plus white, greys and black.
    static readonly string[] Quick =
    {
        "#FFFFFF", "#D1D5DB", "#6B7280", "#111827", "#EF4444", "#F97316", "#F59E0B", "#FACC15", "#84CC16", "#22C55E",
        "#10B981", "#14B8A6", "#06B6D4", "#0EA5E9", "#3B82F6", "#6366F1", "#8B5CF6", "#A855F7", "#D946EF", "#EC4899",
    };

    double _h, _s = 1, _v = 1;   // the picker's colour while it's open
    bool _dragSv, _dragHue;

    public ColorBox()
    {
        InitializeComponent();
        foreach (var hex in Quick)
        {
            var b = new Button
            {
                Width = 20, Height = 20, Margin = new Thickness(0, 0, 3.6, 4), Cursor = Cursors.Hand, ToolTip = hex, Tag = hex,
                Template = SwatchTemplate(), Background = new SolidColorBrush(Parse(hex).Value),
            };
            b.Click += (s, e) => { Commit(hex); PickerPopup.IsOpen = false; };
            QuickPanel.Children.Add(b);
        }
        ShowText();
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        t.VisualTree = border;
        return t;
    }

    // ---- Showing the value ----

    void ShowText()
    {
        if (HexBox == null) return;
        var c = Parse(Text);
        HexBox.Text = c != null ? Hex(c.Value) : "";
        SwatchFill.Background = c != null ? new SolidColorBrush(c.Value) : null;
        EmptyMark.Visibility = c == null ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.Visibility = AllowEmpty ? Visibility.Visible : Visibility.Collapsed;
        HexBox.ToolTip = c == null && AllowEmpty ? "Blank: uses the default colour" : null;
    }

    // A chosen colour: store it and tell the page.
    void Commit(string hex)
    {
        var c = Parse(hex);
        var value = c != null ? Hex(c.Value) : AllowEmpty ? "" : Text;
        bool changed = !string.Equals(value, Text, StringComparison.OrdinalIgnoreCase);
        Text = value;
        ShowText();
        if (changed) RaiseEvent(new RoutedEventArgs(ChangedEvent, this));
    }

    // ---- The hex box ----

    void HexBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTyped(HexBox.Text);

    void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitTyped(HexBox.Text); e.Handled = true; }
        else if (e.Key == Key.Escape) { ShowText(); e.Handled = true; }
    }

    void CommitTyped(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 && AllowEmpty) { Commit(""); return; }
        if (Parse(text) != null) Commit(text);
        else ShowText(); // not a colour: put the old one back
    }

    // ---- The picker ----

    void Swatch_Click(object sender, RoutedEventArgs e)
    {
        var c = Parse(Text) ?? Color.FromRgb(0x7C, 0x3A, 0xED);
        (_h, _s, _v) = ToHsv(c);
        UpdatePicker();
        PickerPopup.IsOpen = SwatchButton.IsChecked == true;
    }

    void Popup_Closed(object sender, EventArgs e)
    {
        SwatchButton.IsChecked = false;
        _dragSv = _dragHue = false;
    }

    void UpdatePicker()
    {
        var c = FromHsv(_h, _s, _v);
        HueFill.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        PreviewFill.Background = new SolidColorBrush(c);
        PopupHex.Text = Hex(c);
        Canvas.SetLeft(SvThumb, _s * SvArea.Width - SvThumb.Width / 2);
        Canvas.SetTop(SvThumb, (1 - _v) * SvArea.Height - SvThumb.Height / 2);
        Canvas.SetLeft(HueThumb, _h / 360 * (HueArea.ActualWidth > 0 ? HueArea.ActualWidth : 236) - HueThumb.Width / 2);
    }

    void Sv_MouseDown(object sender, MouseButtonEventArgs e) { _dragSv = SvArea.CaptureMouse(); PickSv(e.GetPosition(SvArea)); }
    void Sv_MouseMove(object sender, MouseEventArgs e) { if (_dragSv) PickSv(e.GetPosition(SvArea)); }
    void Sv_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragSv) return;
        _dragSv = false;
        SvArea.ReleaseMouseCapture();
        Commit(Hex(FromHsv(_h, _s, _v)));
    }

    void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / SvArea.Width, 0, 1);
        _v = 1 - Math.Clamp(p.Y / SvArea.Height, 0, 1);
        UpdatePicker();
        SwatchFill.Background = new SolidColorBrush(FromHsv(_h, _s, _v)); // follows the drag; saved when you let go
        EmptyMark.Visibility = Visibility.Collapsed;
    }

    void Hue_MouseDown(object sender, MouseButtonEventArgs e) { _dragHue = HueArea.CaptureMouse(); PickHue(e.GetPosition(HueArea)); }
    void Hue_MouseMove(object sender, MouseEventArgs e) { if (_dragHue) PickHue(e.GetPosition(HueArea)); }
    void Hue_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragHue) return;
        _dragHue = false;
        HueArea.ReleaseMouseCapture();
        Commit(Hex(FromHsv(_h, _s, _v)));
    }

    void PickHue(Point p)
    {
        _h = Math.Clamp(p.X / Math.Max(1, HueArea.ActualWidth), 0, 1) * 360;
        if (_s < 0.05 && _v > 0.95) _s = 1; // picking a hue from white: show that hue, not white again
        UpdatePicker();
        SwatchFill.Background = new SolidColorBrush(FromHsv(_h, _s, _v));
        EmptyMark.Visibility = Visibility.Collapsed;
    }

    void PopupHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        CommitPopupHex();
        PickerPopup.IsOpen = false;
    }

    void PopupHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitPopupHex();

    void CommitPopupHex()
    {
        var c = Parse(PopupHex.Text);
        if (c == null) { UpdatePicker(); return; }
        (_h, _s, _v) = ToHsv(c.Value);
        UpdatePicker();
        Commit(Hex(c.Value));
    }

    void Clear_Click(object sender, RoutedEventArgs e)
    {
        Commit("");
        PickerPopup.IsOpen = false;
    }

    // ---- Colours ----

    static readonly Regex HexPattern = new Regex("^#?([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$");

    // "#RGB", "#RRGGBB", with or without the "#". Null when it isn't one.
    public static Color? Parse(string text)
    {
        var t = (text ?? "").Trim();
        if (!HexPattern.IsMatch(t)) return null;
        t = t.TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(ch => new string(ch, 2)));
        return Color.FromRgb(byte.Parse(t[..2], NumberStyles.HexNumber), byte.Parse(t.Substring(2, 2), NumberStyles.HexNumber), byte.Parse(t.Substring(4, 2), NumberStyles.HexNumber));
    }

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
