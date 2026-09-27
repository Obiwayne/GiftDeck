using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck;

// While text to speech is muted the menu says so ("Text to speech (muted)", red mute icon, also when the
// menu is collapsed to icons), so a saved mute can't be forgotten.
public partial class MainWindow
{
    static readonly string MuteGlyph = ((char)0xE74F).ToString(); // Segoe icon "Mute"

    void StartTtsMuteNav()
    {
        Hub.Tts.MuteChanged += () => Dispatcher.BeginInvoke(ShowTtsMuteNav);
        ShowTtsMuteNav();
    }

    void ShowTtsMuteNav()
    {
        var rb = NavPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.Tag as string == "tts");
        var item = NavItems.First(n => n.Key == "tts");
        if (rb != null) ShowTtsMuted(rb, item.Label, item.Glyph, Hub.Tts.Muted);
    }

    // rb is a menu entry as BuildNav makes it: a StackPanel holding the icon and the name.
    internal static void ShowTtsMuted(RadioButton rb, string label, string glyph, bool muted)
    {
        if (rb.Content is not StackPanel sp || sp.Children.Count < 2) return;
        var icon = (TextBlock)sp.Children[0];
        var name = (TextBlock)sp.Children[1];
        icon.Text = muted ? MuteGlyph : glyph;
        name.Text = muted ? label + " (muted)" : label;
        if (muted)
        {
            icon.Foreground = (Brush)rb.FindResource("DangerBrush");
            name.Foreground = (Brush)rb.FindResource("DangerBrush");
        }
        else
        {
            icon.ClearValue(TextBlock.ForegroundProperty);
            name.ClearValue(TextBlock.ForegroundProperty);
        }
        if (rb.ToolTip != null) rb.ToolTip = name.Text; // collapsed menu: the name is the tooltip
    }
}
