using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GiftDeck.Views;

// MayhemDeck's own message box, in the app's style, used everywhere instead of Windows' plain one.
// Same buttons and answers as MessageBox.Show, so a call only changes its name:
//   if (AppDialog.Show(owner, "Delete it?", "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
// A warning with Yes/No is treated as "are you sure?": Yes shows as a red button and No is the default.
public partial class AppDialog : Window
{
    MessageBoxResult _result;
    readonly MessageBoxResult _cancelResult;

    AppDialog(Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        InitializeComponent();
        if (owner != null && owner.IsVisible && owner != this) { Owner = owner; Cover(owner); }
        else { WindowStartupLocation = WindowStartupLocation.CenterScreen; SizeToContent = SizeToContent.WidthAndHeight; Background = Brushes.Transparent; }

        // The title is the caption unless that's just the app's name; then the first line of the message leads.
        bool plainCaption = string.IsNullOrWhiteSpace(caption) || caption is "MayhemDeck" or "GiftDeck";
        TitleText.Text = plainCaption ? DefaultTitle(icon, buttons) : caption;
        MessageText.Text = text ?? "";
        Title = plainCaption ? "MayhemDeck" : caption;

        var (glyph, color) = icon switch
        {
            MessageBoxImage.Warning => ("", "WarnBrush"),
            MessageBoxImage.Error => ("", "DangerBrush"),
            MessageBoxImage.Question => ("", "AccentBrush"),
            MessageBoxImage.Information => ("", "AccentBrush"),
            _ => ("", "AccentBrush"),
        };
        var brush = (Brush)FindResource(color);
        IconGlyph.Text = glyph;
        IconGlyph.Foreground = brush;
        IconDisc.Background = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = 0.16 };

        bool destructive = icon == MessageBoxImage.Warning && buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        var list = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.Cancel, MessageBoxResult.OK },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.No, MessageBoxResult.Yes },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Cancel, MessageBoxResult.No, MessageBoxResult.Yes },
            _ => new[] { MessageBoxResult.OK },
        };
        _cancelResult = list.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                      : list.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        _result = _cancelResult;
        if (defaultResult == MessageBoxResult.None || !list.Contains(defaultResult))
            defaultResult = destructive ? MessageBoxResult.No : list[^1];

        Button focus = null;
        foreach (var r in list)
        {
            bool main = r == list[^1];
            var b = new Button
            {
                Content = r switch { MessageBoxResult.Yes => "Yes", MessageBoxResult.No => "No", MessageBoxResult.Cancel => "Cancel", _ => "OK" },
                MinWidth = 96, Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)FindResource(main ? (destructive ? "Danger" : "Primary") : "Ghost"),
                IsDefault = r == defaultResult,
            };
            b.Click += (_, _) => { _result = r; DialogResult = true; };
            Buttons.Children.Add(b);
            if (r == defaultResult) focus = b;
        }

        Loaded += (_, _) =>
        {
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            focus?.Focus();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _result = _cancelResult; DialogResult = false; e.Handled = true; }
            // Ctrl+C copies the message, like Windows' own box (handy for sending an error to someone).
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { try { Clipboard.SetText(MessageText.Text); } catch { } e.Handled = true; }
        };
        // A click on the dark area outside cancels only when there's a way to say no.
        Backdrop.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == Backdrop && list.Length > 1) { _result = _cancelResult; DialogResult = false; }
        };
    }

    static string DefaultTitle(MessageBoxImage icon, MessageBoxButton buttons) => icon switch
    {
        MessageBoxImage.Error => "Something went wrong",
        MessageBoxImage.Warning => buttons == MessageBoxButton.OK ? "Heads up" : "Are you sure?",
        MessageBoxImage.Question => "Quick question",
        _ => buttons == MessageBoxButton.OK ? "MayhemDeck" : "Quick question",
    };

    // Lies exactly over the app window, so it all goes dark behind the card.
    void Cover(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (owner.WindowState == WindowState.Maximized)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;
        }
        else { Left = owner.Left; Top = owner.Top; Width = owner.ActualWidth; Height = owner.ActualHeight; }
    }

    // ---- The same calls as MessageBox.Show ----

    public static MessageBoxResult Show(string text) => Show(null, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) => Show(null, text, caption, buttons, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => Show(null, text, caption, buttons, icon, MessageBoxResult.None);
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) => Show(null, text, caption, buttons, icon, defaultResult);
    public static MessageBoxResult Show(Window owner, string text) => Show(owner, text, "", MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButton.OK, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons) => Show(owner, text, caption, buttons, MessageBoxImage.None, MessageBoxResult.None);
    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon) => Show(owner, text, caption, buttons, icon, MessageBoxResult.None);

    public static MessageBoxResult Show(Window owner, string text, string caption, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        var app = Application.Current;
        if (app == null) return MessageBox.Show(text, caption, buttons, icon, defaultResult);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Show(owner, text, caption, buttons, icon, defaultResult));
        try
        {
            // No owner given: the window in front, so the pop-up covers what the user is looking at.
            owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not AppDialog) ?? app.MainWindow;
            var d = new AppDialog(owner, text, caption, buttons, icon, defaultResult);
            d.ShowDialog();
            return d._result;
        }
        catch (Exception e)
        {
            // Never lose the message: fall back to Windows' own box if ours can't be shown.
            GiftDeck.Services.Log.Write("Couldn't show MayhemDeck's message box: " + e.Message);
            return MessageBox.Show(text, caption, buttons, icon, defaultResult);
        }
    }
}
