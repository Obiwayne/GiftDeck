using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Shared by the event and prize editors: notices edits, asks before throwing them away,
// and gives the window Esc (cancel) and Ctrl+S (save).
sealed class EditorGuard
{
    readonly Window _window;
    readonly Action _save;
    bool _dirty;

    public EditorGuard(Window window, Action save)
    {
        _window = window;
        _save = save;
        // Hooked once the editors have filled themselves in, so loading doesn't count as an edit.
        window.Loaded += (s, e) => window.Dispatcher.BeginInvoke(() =>
        {
            window.AddHandler(TextBoxBase.TextChangedEvent, new RoutedEventHandler((o, a) => _dirty = true));
            window.AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler((o, a) => _dirty = true));
            window.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((o, a) => _dirty = true));
            window.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler((o, a) => _dirty = true));
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.PreviewKeyDown += OnKey;
        window.Closing += OnClosing;
    }

    // For changes that don't come from a control, such as adding, removing or moving an action.
    public void MarkDirty() => _dirty = true;

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            _save();
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _window.Close();
        }
    }

    void OnClosing(object sender, CancelEventArgs e)
    {
        if (_window.DialogResult == true || !_dirty) return;
        var answer = AppDialog.Show(_window, "You have changes that aren't saved. Close without saving them?",
            "Discard changes?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    // Reads a whole, non-negative number, accepting "1,000" and "1 000". Null means the box can't be read.
    public static int? ReadCount(TextBox box)
    {
        var text = box.Text.Trim();
        if (text.Length == 0) return 0;
        text = text.Replace(" ", "").Replace(" ", "");
        if (int.TryParse(text, NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out int v) && v >= 0) return v;
        if (int.TryParse(text, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out v) && v >= 0) return v;
        return null;
    }

    // What's wrong with an action, or null when it can run. Position is 1-based, for the message.
    public static string CheckAction(RuleAction a, int position)
    {
        string problem = a.Type switch
        {
            ActionType.KeyPress => KeySender.TryParse(a.Text, out _, out var err) ? null : "Press keys: " + err,
            ActionType.Sound => string.IsNullOrWhiteSpace(a.Text) ? "Play sound: choose a file." : null,
            ActionType.ObsScene => string.IsNullOrWhiteSpace(a.Text) ? "OBS scene: enter the scene name." : null,
            ActionType.ObsCanvasScene => string.IsNullOrWhiteSpace(a.Text) ? "Vertical canvas scene: enter the scene name." : null,
            ActionType.ObsShowSource or ActionType.ObsHideSource => string.IsNullOrWhiteSpace(a.Text) ? "OBS source: enter the source name." : null,
            ActionType.Tts => string.IsNullOrWhiteSpace(a.Text) ? "Speak: enter the text to say." : null,
            ActionType.RunProgram => string.IsNullOrWhiteSpace(a.Text) ? "Run program: choose a program." : null,
            _ => null,
        };
        return problem == null ? null : $"Action {position}, {problem}";
    }
}
