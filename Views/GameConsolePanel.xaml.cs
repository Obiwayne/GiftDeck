using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The connection card on a "console" game's page: server address, port, password (and player name when the
// game's commands need one), Save, Test connection, and a box to send one command by hand.
public partial class GameConsolePanel : UserControl
{
    public ConsoleTarget Target { get; }

    // The page redraws its tile badges when the connection changes.
    public event Action StatusChanged;

    bool _loading, _busy;
    bool? _lastTestOk;
    readonly List<string> _history = new List<string>();
    int _historyAt = -1;
    readonly List<string> _output = new List<string>();

    public GameConsolePanel(ConsoleTarget target)
    {
        InitializeComponent();
        Target = target;
        var console = target.Pack?.Console ?? new PackConsole();
        PasswordLabel.Text = string.IsNullOrWhiteSpace(console.PasswordLabel) ? "Password" : console.PasswordLabel;
        PlayerLabel.Text = console.PlayerLabel;
        PlayerPanel.Visibility = string.IsNullOrWhiteSpace(console.PlayerLabel) ? Visibility.Collapsed : Visibility.Visible;
        CommandHint.Text = string.IsNullOrWhiteSpace(console.TestCommand) ? "Type a server command" : "e.g. " + console.TestCommand;
        CommandBox.TextChanged += (_, _) => CommandHint.Visibility = CommandBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LoadFields();
        ShowStatus();
    }

    Brush Res(string key) => (Brush)FindResource(key);

    static string ProtocolName(string protocol) => (protocol ?? "").ToLowerInvariant() switch
    {
        "telnet" => "Telnet console",
        "tshock" => "TShock REST API",
        _ => "RCON",
    };

    void LoadFields()
    {
        _loading = true;
        var s = Target.Settings;
        var c = Target.Pack?.Console;
        HostBox.Text = string.IsNullOrWhiteSpace(s?.Host) ? c?.Host ?? "127.0.0.1" : s.Host;
        PortBox.Text = (s?.Port > 0 ? s.Port : c?.Port ?? 0) is int port && port > 0 ? port.ToString() : "";
        PasswordBox.Text = s?.Password ?? "";
        PlayerBox.Text = s?.Player ?? "";
        _loading = false;
        UpdateSave();
    }

    void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        Target.Changed -= Target_Changed;
        Target.Changed += Target_Changed;
        ShowStatus();
    }

    void Panel_Unloaded(object sender, RoutedEventArgs e) => Target.Changed -= Target_Changed;

    void Target_Changed() => Dispatcher.BeginInvoke(() => { ShowStatus(); StatusChanged?.Invoke(); });

    void ShowStatus()
    {
        bool connected = Target.Connected;
        string brush = _busy ? "AccentBrush" : connected ? "SuccessBrush" : _lastTestOk == false ? "DangerBrush" : "MutedBrush";
        StatusDot.Fill = Res(brush);
        StatusText.Text = _busy ? "Testing…" : connected ? "Connected" : _lastTestOk == false ? "Can't connect" : "Not connected";
        StatusText.Foreground = Res(brush == "MutedBrush" ? "MutedBrush" : brush);
        var detail = Target.Status;
        SubText.Text = string.IsNullOrWhiteSpace(detail) ? ProtocolName(Target.Pack?.Console?.Protocol) + " · the game's own server console" : detail;
    }

    // ---- Fields ----

    bool TryReadPort(out int port) => int.TryParse(PortBox.Text.Trim(), out port) && port > 0 && port <= 65535;

    bool Dirty()
    {
        var s = Target.Settings;
        if (s == null) return true;
        TryReadPort(out var port);
        return HostBox.Text.Trim() != (s.Host ?? "") || port != s.Port || PasswordBox.Text != (s.Password ?? "")
               || (PlayerPanel.Visibility == Visibility.Visible && PlayerBox.Text.Trim() != (s.Player ?? ""));
    }

    void UpdateSave()
    {
        bool dirty = Dirty();
        SaveButton.IsEnabled = !_busy && dirty;
        SaveButton.Content = dirty ? "Save" : "Saved";
    }

    void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        UpdateSave();
    }

    void Port_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsDigit);

    // Copies the boxes into the target's settings and saves them. False (with a message) when the port isn't a number.
    bool Apply()
    {
        if (!TryReadPort(out var port))
        {
            Say("The port has to be a number between 1 and 65535.", false);
            PortBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            Say("Type the server's address (127.0.0.1 when it runs on this PC).", false);
            HostBox.Focus();
            return false;
        }
        var s = Target.Settings;
        s.Host = HostBox.Text.Trim();
        s.Port = port;
        s.Password = PasswordBox.Text;
        if (PlayerPanel.Visibility == Visibility.Visible) s.Player = PlayerBox.Text.Trim();
        try { Target.SaveSettings(); }
        catch (Exception ex) { Say("Couldn't save: " + ex.Message, false); return false; }
        UpdateSave();
        return true;
    }

    void Say(string text, bool? ok)
    {
        ResultText.Text = text ?? "";
        ResultText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        ResultText.Foreground = Res(ok == true ? "SuccessBrush" : ok == false ? "DangerBrush" : "MutedBrush");
    }

    // ---- Buttons ----

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Apply()) Say("Saved ✓  Press Test connection to check MayhemDeck can reach the server.", true);
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !Apply()) return;
        SetBusy(true);
        Say($"Connecting to {Target.Settings.Host}:{Target.Settings.Port}…", null);
        try
        {
            var r = await Target.TestAsync();
            _lastTestOk = r?.Ok == true;
            var msg = string.IsNullOrWhiteSpace(r?.Message) ? "" : r.Message.Trim();
            Say(_lastTestOk == true ? "Connected ✓  " + msg : (msg.Length > 0 ? msg : "MayhemDeck couldn't reach the server."), _lastTestOk);
        }
        catch (Exception ex)
        {
            _lastTestOk = false;
            Say(ex.Message, false);
        }
        finally
        {
            SetBusy(false);
            StatusChanged?.Invoke();
        }
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        TestButton.IsEnabled = SendButton.IsEnabled = !busy;
        UpdateSave();
        ShowStatus();
    }

    // ---- Send a command ----

    void CommandBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Send(); }
        else if (e.Key == Key.Up && _history.Count > 0)
        {
            e.Handled = true;
            _historyAt = _historyAt < 0 ? _history.Count - 1 : Math.Max(0, _historyAt - 1);
            CommandBox.Text = _history[_historyAt];
            CommandBox.CaretIndex = CommandBox.Text.Length;
        }
        else if (e.Key == Key.Down && _historyAt >= 0)
        {
            e.Handled = true;
            _historyAt++;
            CommandBox.Text = _historyAt < _history.Count ? _history[_historyAt] : "";
            if (_historyAt >= _history.Count) _historyAt = -1;
            CommandBox.CaretIndex = CommandBox.Text.Length;
        }
    }

    void Send_Click(object sender, RoutedEventArgs e) => Send();

    async void Send()
    {
        var line = CommandBox.Text.Trim();
        if (line.Length == 0 || _busy) return;
        if (Dirty() && !Apply()) return;
        _history.Remove(line);
        _history.Add(line);
        _historyAt = -1;
        CommandBox.Clear();
        SendButton.IsEnabled = false;
        string reply;
        try { reply = await Target.RunRawAsync(line); }
        catch (Exception ex) { reply = "✕ " + ex.Message; }
        finally { SendButton.IsEnabled = !_busy; }
        Append("> " + line);
        Append(string.IsNullOrWhiteSpace(reply) ? "(no reply)" : reply.TrimEnd());
        CommandBox.Focus();
        StatusChanged?.Invoke();
    }

    void Append(string text)
    {
        _output.Add(text);
        while (_output.Count > 60) _output.RemoveAt(0);
        OutputText.Text = string.Join(Environment.NewLine, _output);
        OutputBorder.Visibility = Visibility.Visible;
        OutputScroll.ScrollToEnd();
    }
}
