using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using GiftDeck.Models;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Edits one RuleAction in place. The RuleAction comes in as the DataContext.
public partial class ActionEditor : UserControl
{
    public RuleAction Action { get; private set; }

    bool _loading = true;
    bool _initialised;

    static readonly Choice[] Types =
    {
        new Choice(ActionType.KeyPress, "Press keys"),
        new Choice(ActionType.Sound, "Play a sound"),
        new Choice(ActionType.ObsScene, "OBS: switch scene (main canvas)"),
        new Choice(ActionType.ObsCanvasScene, "OBS: switch scene (vertical canvas)"),
        new Choice(ActionType.ObsShowSource, "OBS: show a source"),
        new Choice(ActionType.ObsHideSource, "OBS: hide a source"),
        new Choice(ActionType.Delay, "Wait"),
        new Choice(ActionType.Tts, "Speak (text to speech)"),
        new Choice(ActionType.SpotifyRequest, "Spotify: queue a song"),
        new Choice(ActionType.SpotifyControl, "Spotify: control player"),
        new Choice(ActionType.RunProgram, "Run a program"),
    };

    static readonly Choice[] SpotifyCommands =
    {
        new Choice("Play", "Play"),
        new Choice("Pause", "Pause"),
        new Choice("Next", "Next track"),
        new Choice("Previous", "Previous track"),
        new Choice("VolumeUp", "Volume up by"),
        new Choice("VolumeDown", "Volume down by"),
        new Choice("SetVolume", "Set volume to"),
    };

    public ActionEditor()
    {
        InitializeComponent();
        TypeCombo.ItemsSource = Types;
        SpotifyCmdCombo.ItemsSource = SpotifyCommands;
        SceneCombo.ItemsSource = Hub.Obs.Scenes.ToList();
        SourceSceneCombo.ItemsSource = Hub.Obs.Scenes.ToList();
        SceneCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Commit()));
        SourceSceneCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Commit()));
        SceneCombo.SelectionChanged += (s, e) => Commit();
        SourceSceneCombo.SelectionChanged += (s, e) => Commit();

        CanvasCombo.ItemsSource = Hub.Obs.Canvases.ToList();
        CanvasCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => { FillCanvasScenes(); Commit(); }));
        CanvasCombo.SelectionChanged += (s, e) => { FillCanvasScenes(); Commit(); };
        CanvasSceneCombo.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => Commit()));
        CanvasSceneCombo.SelectionChanged += (s, e) => Commit();
    }

    void FillCanvasScenes()
    {
        var canvas = CanvasCombo.Text?.Trim() ?? "";
        if (canvas.Length == 0) canvas = Hub.Obs.Canvases.FirstOrDefault() ?? "";
        var keep = CanvasSceneCombo.Text;
        CanvasSceneCombo.ItemsSource = Hub.Obs.CanvasScenes.TryGetValue(canvas, out var list) ? list.ToList() : new List<string>();
        CanvasSceneCombo.Text = keep;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialised) return;
        _initialised = true;
        Action = DataContext as RuleAction ?? new RuleAction();
        _loading = true;
        Populate();
        _loading = false;
    }

    void Populate()
    {
        TypeCombo.SelectedItem = Types.First(c => (ActionType)c.Value == Action.Type);
        switch (Action.Type)
        {
            case ActionType.KeyPress:
                KeyBox.Text = Action.Text;
                HoldBox.Text = Action.Number > 0 ? Action.Number.ToString() : "";
                break;
            case ActionType.Sound:
                SoundPath.Text = Action.Text;
                SoundVol.Text = (Action.Number > 0 ? Action.Number : 100).ToString();
                break;
            case ActionType.ObsScene:
                SceneCombo.Text = Action.Text;
                break;
            case ActionType.ObsCanvasScene:
                CanvasCombo.Text = string.IsNullOrEmpty(Action.Text2) ? (Hub.Obs.Canvases.FirstOrDefault() ?? "Aitum Vertical") : Action.Text2;
                FillCanvasScenes();
                CanvasSceneCombo.Text = Action.Text;
                break;
            case ActionType.ObsShowSource:
            case ActionType.ObsHideSource:
                SourceBox.Text = Action.Text;
                SourceSceneCombo.Text = Action.Text2;
                break;
            case ActionType.Delay:
                DelayBox.Text = Action.Number.ToString();
                break;
            case ActionType.Tts:
                TtsBox.Text = Action.Text;
                break;
            case ActionType.SpotifyRequest:
                SpotifyQueryBox.Text = Action.Text;
                break;
            case ActionType.SpotifyControl:
                SpotifyCmdCombo.SelectedItem = SpotifyCommands.FirstOrDefault(c => (string)c.Value == Action.Text) ?? SpotifyCommands[2];
                SpotifyNumBox.Text = Action.Number > 0 ? Action.Number.ToString() : "";
                break;
            case ActionType.RunProgram:
                ProgramPath.Text = Action.Text;
                ProgramArgs.Text = Action.Text2;
                break;
        }
        UpdatePanels();
    }

    void UpdatePanels()
    {
        var t = Action.Type;
        KeyPanel.Visibility = t == ActionType.KeyPress ? Visibility.Visible : Visibility.Collapsed;
        SoundPanel.Visibility = t == ActionType.Sound ? Visibility.Visible : Visibility.Collapsed;
        ScenePanel.Visibility = t == ActionType.ObsScene ? Visibility.Visible : Visibility.Collapsed;
        CanvasPanel.Visibility = t == ActionType.ObsCanvasScene ? Visibility.Visible : Visibility.Collapsed;
        SourcePanel.Visibility = t == ActionType.ObsShowSource || t == ActionType.ObsHideSource ? Visibility.Visible : Visibility.Collapsed;
        DelayPanel.Visibility = t == ActionType.Delay ? Visibility.Visible : Visibility.Collapsed;
        TtsPanel.Visibility = t == ActionType.Tts ? Visibility.Visible : Visibility.Collapsed;
        SpotifyRequestPanel.Visibility = t == ActionType.SpotifyRequest ? Visibility.Visible : Visibility.Collapsed;
        SpotifyControlPanel.Visibility = t == ActionType.SpotifyControl ? Visibility.Visible : Visibility.Collapsed;
        ProgramPanel.Visibility = t == ActionType.RunProgram ? Visibility.Visible : Visibility.Collapsed;
        if (t == ActionType.SpotifyControl)
        {
            var cmd = (SpotifyCmdCombo.SelectedItem as Choice)?.Value as string;
            bool needsNumber = cmd == "VolumeUp" || cmd == "VolumeDown" || cmd == "SetVolume";
            SpotifyNumBox.Visibility = needsNumber ? Visibility.Visible : Visibility.Collapsed;
            SpotifyNumLabel.Visibility = needsNumber ? Visibility.Visible : Visibility.Collapsed;
            SpotifyNumLabel.Text = cmd == "SetVolume" ? "Volume %" : "Percent (blank = 10)";
        }
    }

    void TypeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Action == null) return;
        var t = (ActionType)((Choice)TypeCombo.SelectedItem).Value;
        if (t == Action.Type) return;
        Action.Type = t;
        Action.Text = "";
        Action.Text2 = "";
        Action.Number = 0;
        switch (t)
        {
            case ActionType.Delay: Action.Number = 1000; break;
            case ActionType.Tts: Action.Text = "{user} sent {gift}"; break;
            case ActionType.SpotifyRequest: Action.Text = "{request}"; break;
            case ActionType.SpotifyControl: Action.Text = "Next"; break;
        }
        _loading = true;
        Populate();
        _loading = false;
    }

    void SpotifyCmd_Changed(object sender, SelectionChangedEventArgs e)
    {
        Commit();
        if (Action != null && Action.Type == ActionType.SpotifyControl) UpdatePanels();
    }

    void Field_Changed(object sender, TextChangedEventArgs e) => Commit();

    static int Int(string s) { int.TryParse((s ?? "").Trim(), out int v); return Math.Max(0, v); }

    void Commit()
    {
        if (_loading || Action == null) return;
        switch (Action.Type)
        {
            case ActionType.KeyPress:
                Action.Text = KeyBox.Text;
                Action.Number = Int(HoldBox.Text);
                break;
            case ActionType.Sound:
                Action.Text = SoundPath.Text.Trim();
                Action.Number = Int(SoundVol.Text);
                break;
            case ActionType.ObsScene:
                Action.Text = SceneCombo.Text.Trim();
                break;
            case ActionType.ObsCanvasScene:
                Action.Text = CanvasSceneCombo.Text.Trim();
                Action.Text2 = CanvasCombo.Text.Trim();
                break;
            case ActionType.ObsShowSource:
            case ActionType.ObsHideSource:
                Action.Text = SourceBox.Text.Trim();
                Action.Text2 = SourceSceneCombo.Text.Trim();
                break;
            case ActionType.Delay:
                Action.Number = Int(DelayBox.Text);
                break;
            case ActionType.Tts:
                Action.Text = TtsBox.Text;
                break;
            case ActionType.SpotifyRequest:
                Action.Text = SpotifyQueryBox.Text;
                break;
            case ActionType.SpotifyControl:
                Action.Text = (SpotifyCmdCombo.SelectedItem as Choice)?.Value as string ?? "Next";
                Action.Number = Int(SpotifyNumBox.Text);
                break;
            case ActionType.RunProgram:
                Action.Text = ProgramPath.Text.Trim();
                Action.Text2 = ProgramArgs.Text;
                break;
        }
    }

    void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var combo = KeySender.FromKeyEvent(e);
        if (combo == null) return;
        KeyBox.Text = combo;
        Commit();
    }

    void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        KeyBox.Text = "";
        Commit();
    }

    void BrowseSound_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.ogg|All files|*.*" };
        if (dlg.ShowDialog() == true) SoundPath.Text = dlg.FileName;
    }

    void LibrarySound_Click(object sender, RoutedEventArgs e)
    {
        var rule = Window.GetWindow(this) as RuleEditorWindow;
        var win = new SoundLibraryWindow(rule?.NameBox.Text ?? "") { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() == true && !string.IsNullOrEmpty(win.ChosenFile))
            SoundPath.Text = win.ChosenFile;
    }

    void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        try { Hub.Sounds.Play(SoundPath.Text.Trim(), Int(SoundVol.Text) > 0 ? Int(SoundVol.Text) : 100); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "GiftDeck"); }
    }

    void BrowseProgram_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Programs and files|*.exe;*.bat;*.cmd;*.lnk;*.*" };
        if (dlg.ShowDialog() == true) ProgramPath.Text = dlg.FileName;
    }

    async void RefreshScenes_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Hub.Obs.Connected) await Hub.Obs.ConnectAsync();
            var scenes = await Hub.Obs.GetScenesAsync();
            await Hub.Obs.RefreshCanvasesAsync();
            var keep1 = SceneCombo.Text;
            var keep2 = SourceSceneCombo.Text;
            var keep3 = CanvasCombo.Text;
            SceneCombo.ItemsSource = scenes;
            SourceSceneCombo.ItemsSource = scenes;
            CanvasCombo.ItemsSource = Hub.Obs.Canvases.ToList();
            SceneCombo.Text = keep1;
            SourceSceneCombo.Text = keep2;
            CanvasCombo.Text = keep3;
            FillCanvasScenes();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not get scenes from OBS: " + ex.Message, "GiftDeck");
        }
    }

    RuleEditorWindow Owner => Window.GetWindow(this) as RuleEditorWindow;
    void Up_Click(object sender, RoutedEventArgs e) => Owner?.MoveAction(Action, -1);
    void Down_Click(object sender, RoutedEventArgs e) => Owner?.MoveAction(Action, 1);
    void Remove_Click(object sender, RoutedEventArgs e) => Owner?.RemoveAction(Action);
}
