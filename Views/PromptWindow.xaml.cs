using System.Windows;
using System.Windows.Input;

namespace GiftDeck.Views;

// Asks for one line of text, e.g. a profile name. Returns null if cancelled.
public partial class PromptWindow : Window
{
    PromptWindow(string question, string initial, string ok)
    {
        InitializeComponent();
        Question.Text = question;
        Answer.Text = initial ?? "";
        OkButton.Content = ok;
        Loaded += (_, _) => { Answer.Focus(); Answer.SelectAll(); };
    }

    public static string Ask(Window owner, string question, string initial = "", string ok = "OK")
    {
        var w = new PromptWindow(question, initial, ok) { Owner = owner };
        return w.ShowDialog() == true ? w.Answer.Text.Trim() : null;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    void Answer_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DialogResult = true;
        else if (e.Key == Key.Escape) DialogResult = false;
    }
}
