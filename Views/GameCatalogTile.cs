using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using GiftDeck.Services;

namespace GiftDeck.Views;

// One game on the Games page: its cover (Steam header art), tier badge in the top-left corner, a status badge
// ("Installed", "Connected"…) in the top-right, and name / short line / genre underneath. A real button, so
// Tab + Enter open it too. Lifts a little and zooms the picture on hover or keyboard focus.
sealed class GameTile : Button
{
    public GamePack Pack { get; }
    public string Tier { get; }

    readonly Border _frame;
    readonly Grid _art;
    readonly ScaleTransform _zoom = new ScaleTransform(1, 1);
    readonly TranslateTransform _lift = new TranslateTransform();
    readonly Border _status;
    readonly TextBlock _statusText;
    string _statusKey;

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public GameTile(GamePack p)
    {
        Pack = p;
        Tier = GameCatalog.TierOf(p);
        Template = TileTemplate;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        Padding = new Thickness(0);
        Margin = new Thickness(0);
        MinHeight = 0;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransform = _lift;

        // The picture: generated tile underneath, the real cover fades in over it.
        var cover = new Image { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
        RenderOptions.SetBitmapScalingMode(cover, BitmapScalingMode.HighQuality);
        _art = new Grid { RenderTransform = _zoom, RenderTransformOrigin = new Point(0.5, 0.5) };
        _art.Children.Add(CoverCache.Fallback(p, 34));
        _art.Children.Add(cover);

        var inner = new Grid();
        inner.Children.Add(_art);
        // A soft shade at the top so the badges read on bright art.
        inner.Children.Add(new Border
        {
            Height = 44, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Background = Frozen(new LinearGradientBrush(Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90)),
        });
        inner.Children.Add(TierBadge(Tier, compact: false));
        _statusText = new TextBlock { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
        _status = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(8, 2, 8, 3), CornerRadius = new CornerRadius(10),
            Child = _statusText, Visibility = Visibility.Collapsed,
        };
        inner.Children.Add(_status);
        inner.SizeChanged += (_, e) => inner.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);

        _frame = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            BorderBrush = Res("LineBrush"), Background = Res("Panel2Brush"), Child = inner,
        };

        var name = new TextBlock
        {
            Text = p.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("TextBrush"),
            TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 10, 2, 0),
        };
        var info = new StackPanel();
        info.Children.Add(new AspectBox { Child = _frame });
        info.Children.Add(name);
        if (!string.IsNullOrWhiteSpace(p.Short))
            info.Children.Add(new TextBlock
            {
                Text = p.Short, FontSize = 12.5, Foreground = Res("MutedBrush"), TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 2, 2, 0),
            });
        if (!string.IsNullOrWhiteSpace(p.Genre))
            info.Children.Add(new TextBlock
            {
                Text = p.Genre, FontSize = 11.5, Foreground = Res("MutedBrush"), Opacity = 0.75, TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 4, 2, 0),
            });
        Content = info;

        ToolTip = p.Name + (string.IsNullOrWhiteSpace(p.Short) ? "" : Environment.NewLine + p.Short);
        ToolTipService.SetInitialShowDelay(this, 700);
        System.Windows.Automation.AutomationProperties.SetName(this, $"Open {p.Name} ({GameCatalog.TierLabel(Tier)})");

        MouseEnter += (_, _) => Hover(true);
        MouseLeave += (_, _) => Hover(IsKeyboardFocused);
        GotKeyboardFocus += (_, _) => { Hover(true); BringIntoView(new Rect(-8, -8, ActualWidth + 16, ActualHeight + 16)); };
        LostKeyboardFocus += (_, _) => Hover(IsMouseOver);

        CoverCache.Apply(cover, p);
    }

    // Top-right badge; null text hides it.
    public void SetStatus(string text, string brushKey)
    {
        var key = text + "|" + brushKey;
        if (key == _statusKey) return;
        _statusKey = key;
        _status.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text == null) return;
        _statusText.Text = text;
        _status.Background = Res(brushKey);
        _statusText.Foreground = brushKey == "SuccessBrush" || brushKey == "WarnBrush" ? new SolidColorBrush(Color.FromRgb(0x0B, 0x1F, 0x17)) : Brushes.White;
        System.Windows.Automation.AutomationProperties.SetName(this, $"Open {Pack.Name} ({GameCatalog.TierLabel(Tier)}, {text})");
    }

    bool _hovered;
    void Hover(bool on)
    {
        if (on == _hovered) return;
        _hovered = on;
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(160);
        _lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? -4 : 0, d) { EasingFunction = ease });
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(on ? 1.05 : 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(on ? 1.05 : 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        _frame.BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x52)) : Res("LineBrush");
        // Only the tile under the mouse carries a shadow (60 blurred shadows would slow scrolling down).
        _frame.Effect = on ? new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = 0.5, Color = Colors.Black } : null;
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // "● Server console" on a dark pill, in the tier's colour. Also used on the game's own page.
    public static Border TierBadge(string tier, bool compact)
    {
        var color = GameCatalog.TierBrush(tier);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = GameCatalog.TierGlyph(tier), FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
            FontSize = compact ? 12 : 11, Foreground = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = GameCatalog.TierLabel(tier), FontSize = compact ? 12 : 11, FontWeight = FontWeights.SemiBold,
            Foreground = compact ? color : Res("TextBrush"), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        });
        var c = GameCatalog.TierColor(tier);
        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = compact ? new Thickness(0) : new Thickness(8, 8, 0, 0),
            Padding = compact ? new Thickness(10, 3, 11, 4) : new Thickness(8, 2, 9, 3),
            CornerRadius = new CornerRadius(11),
            Background = compact ? Frozen(new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B))) : Frozen(new SolidColorBrush(Color.FromArgb(220, 0x10, 0x12, 0x18))),
            BorderBrush = compact ? Frozen(new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B))) : null,
            BorderThickness = compact ? new Thickness(1) : new Thickness(0),
            Child = row,
        };
    }

    // No button chrome: the tile itself, plus an accent ring for keyboard focus.
    static readonly ControlTemplate TileTemplate = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
        "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
        "<Grid Background='Transparent'>" +
        "<ContentPresenter/>" +
        "<Border x:Name='Ring' CornerRadius='13' BorderThickness='2' BorderBrush='Transparent' Margin='-5' IsHitTestVisible='False'/>" +
        "</Grid>" +
        "<ControlTemplate.Triggers>" +
        "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Ring' Property='BorderBrush' Value='#7C5CFF'/></Trigger>" +
        "</ControlTemplate.Triggers></ControlTemplate>");
}
