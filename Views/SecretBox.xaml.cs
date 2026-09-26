using System.Windows;
using System.Windows.Controls;

namespace GiftDeck.Views;

// A text box for keys, tokens and passwords: shows dots until the eye button is clicked.
public partial class SecretBox : UserControl
{
    bool _syncing;

    public event TextChangedEventHandler TextChanged;

    public SecretBox() => InitializeComponent();

    public string Text
    {
        get => Hidden.Password;
        set
        {
            _syncing = true;
            Hidden.Password = value ?? "";
            Shown.Text = value ?? "";
            _syncing = false;
        }
    }

    public bool IsReadOnly
    {
        get => Shown.IsReadOnly;
        set
        {
            Shown.IsReadOnly = value;
            Hidden.IsEnabled = !value; // PasswordBox has no read-only mode; dots still show
        }
    }

    public bool Revealed => Shown.Visibility == Visibility.Visible;

    void Eye_Click(object sender, RoutedEventArgs e)
    {
        bool show = !Revealed;
        Shown.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        EyeGlyph.Text = show ? "" : ""; // Hide / View
        EyeButton.ToolTip = show ? "Hide" : "Show";
    }

    void Hidden_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Shown.Text = Hidden.Password;
        _syncing = false;
        TextChanged?.Invoke(this, new TextChangedEventArgs(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, UndoAction.None));
    }

    void Shown_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Hidden.Password = Shown.Text;
        _syncing = false;
        TextChanged?.Invoke(this, e);
    }
}
