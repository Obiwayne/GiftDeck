using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// Runs OBS's main canvas from GiftDeck: scenes with live thumbnails, the transition, each scene's layers, and an audio mixer.
// Everything follows OBS's events, so changes made elsewhere (hotkeys, gift actions, OBS itself) show up here too.
public partial class ScenesView : UserControl
{
    [Flags]
    enum Part { Scenes = 1, Items = 2, Audio = 4, Transitions = 8, All = 15 }

    class SceneCard
    {
        public ObsScene Scene;
        public Border Root;
        public Image Thumb;
        public TextBlock Name;
        public Border LiveBadge;
    }

    class MixerRow
    {
        public ObsAudioInput Input;
        public Slider Slider;
        public TextBlock Db;
        public Button Mute;
        public FrameworkElement Meter;
        public ScaleTransform MeterCover;
        public bool Muted;
        public bool Loading;
        public double? PendingDb;
        public bool Sending;
        public DateTime LastLocal;
        public double Shown;      // meter position 0..1, falls back slowly like a real meter
    }

    const double MeterFloorDb = -60;

    List<ObsScene> _scenes = new List<ObsScene>();
    readonly Dictionary<string, SceneCard> _cards = new Dictionary<string, SceneCard>();
    readonly Dictionary<string, MixerRow> _mixer = new Dictionary<string, MixerRow>();
    readonly Dictionary<int, (Button Eye, TextBlock Name)> _layerRows = new Dictionary<int, (Button, TextBlock)>();
    List<ObsSceneItem> _items = new List<ObsSceneItem>();
    string _programUuid;
    string _selectedUuid;     // whose layers are shown
    (int W, int H) _thumb = (208, 117);
    int? _duration;
    bool _loadingTransitions;

    Part _queued;
    bool _pumping;
    bool _thumbLoop;
    volatile bool _visible;
    volatile Dictionary<string, double> _levels = new Dictionary<string, double>();
    DateTime _levelsAt;
    readonly DispatcherTimer _meterTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };

    public ScenesView()
    {
        InitializeComponent();
        _meterTimer.Tick += (_, _) => UpdateMeters();
        Hub.Engine.StatusChanged += () => Dispatcher.BeginInvoke(UpdateEditButton);
        // OBS's window can be shown or hidden from OBS itself too; keep the button honest while the page is up.
        var editPoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        editPoll.Tick += (_, _) => { if (_visible) UpdateEditButton(); };
        editPoll.Start();
        Hub.Obs.EventReceived += OnObsEvent;
        Hub.Obs.StatusChanged += () => Dispatcher.BeginInvoke(OnStatus);
        IsVisibleChanged += (_, _) =>
        {
            _visible = IsVisible;
            if (IsVisible)
            {
                OnStatus();
                _meterTimer.Start();
                _ = ThumbLoopAsync();
            }
            else _meterTimer.Stop();
        };
    }

    void OnStatus()
    {
        bool on = Hub.Obs.Connected;
        OfflinePanel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        MainPanel.Visibility = TransitionPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on && IsVisible) Queue(Part.All);
    }

    void ObsSettings_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.Navigate("obs");

    // ---- OBS events (arrive on a background thread) ----

    void OnObsEvent(string type, JsonElement data)
    {
        if (type == "InputVolumeMeters")
        {
            // About 20 a second: parse here and let the meter timer pick up the newest.
            if (!_visible) return;
            _levels = ObsService.ParseVolumeMeters(data);
            _levelsAt = DateTime.UtcNow;
            return;
        }
        if (_visible) Dispatcher.BeginInvoke(() => HandleEvent(type, data));
    }

    void HandleEvent(string type, JsonElement data)
    {
        if (!IsVisible) return; // everything is re-read when the page is shown again
        switch (type)
        {
            case "CurrentProgramSceneChanged":
            {
                var uuid = Str(data, "sceneUuid");
                bool followed = _selectedUuid == null || _selectedUuid == _programUuid;
                _programUuid = uuid;
                if (followed && _selectedUuid != uuid) { _selectedUuid = uuid; Queue(Part.Items); }
                UpdateCards();
                break;
            }
            case "SceneListChanged":
            case "SceneCreated":
            case "SceneRemoved":
            case "SceneNameChanged":
                Queue(Part.Scenes);
                break;
            case "SceneCollectionChanged":
                _selectedUuid = null;
                Queue(Part.All);
                break;
            case "SceneItemEnableStateChanged":
                _staleThumbs.Add(Str(data, "sceneUuid"));
                if (Str(data, "sceneUuid") == _selectedUuid && data.TryGetProperty("sceneItemId", out var id))
                    SetLayerShown(id.GetInt32(), data.TryGetProperty("sceneItemEnabled", out var en) && en.ValueKind == JsonValueKind.True);
                break;
            case "SceneItemCreated":
            case "SceneItemRemoved":
            case "SceneItemListReindexed":
            case "SceneItemLockStateChanged":
                if (Str(data, "sceneUuid") == _selectedUuid) Queue(Part.Items);
                break;
            case "InputNameChanged":
                Queue(Part.Audio | Part.Items);
                break;
            case "InputCreated":
            case "InputRemoved":
                Queue(Part.Audio);
                break;
            case "InputVolumeChanged":
                if (_mixer.TryGetValue(Str(data, "inputUuid") ?? "", out var row) && data.TryGetProperty("inputVolumeDb", out var db))
                {
                    // Our own slider writes echo back; ignore them while the user is still dragging.
                    if (row.Slider.IsMouseCaptureWithin || (DateTime.UtcNow - row.LastLocal).TotalMilliseconds < 700) break;
                    ShowVolume(row, db.GetDouble());
                }
                break;
            case "InputMuteStateChanged":
                if (_mixer.TryGetValue(Str(data, "inputUuid") ?? "", out var mrow))
                    ShowMute(mrow, data.TryGetProperty("inputMuted", out var m) && m.ValueKind == JsonValueKind.True);
                break;
            case "CurrentSceneTransitionChanged":
            case "CurrentSceneTransitionDurationChanged":
            case "SceneTransitionCreated":
            case "SceneTransitionRemoved":
            case "SceneTransitionNameChanged":
                Queue(Part.Transitions);
                break;
        }
    }

    static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ---- Loading ----

    // Bursts of events (e.g. OBS loading a scene collection) become one reload.
    async void Queue(Part parts)
    {
        _queued |= parts;
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (_queued != 0)
            {
                await Task.Delay(80);
                var q = _queued;
                _queued = 0;
                await LoadAsync(q);
            }
        }
        finally { _pumping = false; }
    }

    async Task LoadAsync(Part parts)
    {
        if (!Hub.Obs.Connected) { OnStatus(); return; }
        if (parts.HasFlag(Part.Scenes))
        {
            try
            {
                try
                {
                    var (w, h) = await Hub.Obs.GetCanvasSizeAsync();
                    _thumb = w >= h ? (208, (int)Math.Round(208.0 * h / w)) : ((int)Math.Round(208.0 * w / h), 208);
                }
                catch { }
                var (scenes, program) = await Hub.Obs.GetSceneListAsync();
                _scenes = scenes;
                _programUuid = program;
                if (_selectedUuid == null || !scenes.Any(s => s.Uuid == _selectedUuid))
                {
                    _selectedUuid = program;
                    parts |= Part.Items;
                }
                BuildCards();
            }
            catch (Exception e) { Status.Text = "Couldn't read the scenes from OBS: " + e.Message; }
        }
        if (parts.HasFlag(Part.Transitions))
        {
            try { ShowTransitions(await Hub.Obs.GetTransitionsAsync()); }
            catch (Exception e) { Status.Text = "Couldn't read the transitions from OBS: " + e.Message; }
        }
        if (parts.HasFlag(Part.Items))
        {
            try
            {
                var uuid = _selectedUuid;
                _items = uuid == null ? new List<ObsSceneItem>() : await Hub.Obs.GetSceneItemsAsync(uuid);
                if (uuid == _selectedUuid) BuildLayers();
            }
            catch (Exception e) { Status.Text = "Couldn't read the layers from OBS: " + e.Message; }
        }
        if (parts.HasFlag(Part.Audio))
        {
            try { BuildMixer(await Hub.Obs.GetAudioInputsAsync()); }
            catch (Exception e) { Status.Text = "Couldn't read the audio sources from OBS: " + e.Message; }
        }
    }

    // ---- Scenes ----

    void BuildCards()
    {
        SceneGrid.Children.Clear();
        var keep = new HashSet<string>(_scenes.Select(s => s.Uuid));
        foreach (var gone in _cards.Keys.Where(k => !keep.Contains(k)).ToList()) _cards.Remove(gone);
        foreach (var s in _scenes)
        {
            // Reuse cards so their thumbnails don't blink on every list change.
            if (!_cards.TryGetValue(s.Uuid, out var card) || card.Thumb.Width != _thumb.W || card.Thumb.Height != _thumb.H)
                _cards[s.Uuid] = card = MakeCard(s);
            card.Scene = s;
            card.Name.Text = s.Name;
            card.Root.ToolTip = $"Put \"{s.Name}\" live";
            SceneGrid.Children.Add(card.Root);
        }
        ScenesEmpty.Visibility = _scenes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateCards();
    }

    SceneCard MakeCard(ObsScene s)
    {
        var card = new SceneCard { Scene = s };
        card.Thumb = new Image { Width = _thumb.W, Height = _thumb.H, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(card.Thumb, BitmapScalingMode.HighQuality);
        var picture = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(5), ClipToBounds = true, Child = card.Thumb };

        card.LiveBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "LIVE", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
        };
        var top = new Grid();
        top.Children.Add(picture);
        top.Children.Add(card.LiveBadge);

        card.Name = new TextBlock { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var layers = new Button
        {
            Style = (Style)FindResource("Ghost"),
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 0,
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Foreground = (Brush)FindResource("MutedBrush") },
            ToolTip = "Show this scene's layers without putting it live",
        };
        layers.Click += (_, e) => { e.Handled = true; SelectScene(card.Scene.Uuid); };
        var bottom = new DockPanel { Margin = new Thickness(2, 6, 0, 0), Width = _thumb.W };
        DockPanel.SetDock(layers, Dock.Right);
        bottom.Children.Add(layers);
        bottom.Children.Add(card.Name);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bottom);
        card.Root = new Border
        {
            Child = stack,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 12, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.Hand,
        };
        card.Root.MouseLeftButtonUp += (_, e) => { if (!e.Handled) SwitchTo(card.Scene); };
        return card;
    }

    void UpdateCards()
    {
        foreach (var card in _cards.Values)
        {
            bool live = card.Scene.Uuid == _programUuid;
            card.Root.BorderBrush = (Brush)FindResource(live ? "AccentBrush" : "LineBrush");
            card.Root.Background = (Brush)FindResource(card.Scene.Uuid == _selectedUuid ? "Panel3Brush" : "Panel2Brush");
            card.LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            card.Name.FontWeight = live ? FontWeights.SemiBold : FontWeights.Normal;
        }
        var sel = _scenes.FirstOrDefault(s => s.Uuid == _selectedUuid);
        LayersScene.Text = sel == null ? "" : sel.Uuid == _programUuid ? $"{sel.Name} (live)" : sel.Name;
    }

    async void SwitchTo(ObsScene s)
    {
        SelectScene(s.Uuid);
        if (s.Uuid == _programUuid) return;
        try
        {
            await Hub.Obs.SwitchSceneAsync(s.Uuid);
            _programUuid = s.Uuid; // the event will confirm it too
            UpdateCards();
        }
        catch (Exception e) { Status.Text = $"Couldn't switch to \"{s.Name}\": {e.Message}"; }
    }

    void SelectScene(string uuid)
    {
        if (_selectedUuid == uuid) return;
        _selectedUuid = uuid;
        UpdateCards();
        Queue(Part.Items);
    }

    // Thumbnails: only the live scene moves (about 8 frames a second); every other scene shows a still frame,
    // taken once, or kept from when it was last live. A still frame is retaken once when that scene changes
    // (e.g. a layer shown or hidden), so it stays accurate without the whole grid flickering.
    async Task ThumbLoopAsync()
    {
        if (_thumbLoop) return;
        _thumbLoop = true;
        try
        {
            while (IsVisible)
            {
                var started = DateTime.Now;
                if (Hub.Obs.Connected)
                {
                    foreach (var card in _cards.Values.ToList())
                    {
                        if (!IsVisible || !Hub.Obs.Connected) break;
                        var uuid = card.Scene.Uuid;
                        bool live = uuid == _programUuid;
                        bool needsStill = card.Thumb.Source == null || _staleThumbs.Remove(uuid);
                        if (!live && !needsStill) continue;
                        try
                        {
                            // Fixed size, a bit over the box, so it stays sharp on scaled displays.
                            var jpg = await Hub.Obs.GetScreenshotAsync(uuid, (int)(card.Thumb.Width * 1.5));
                            card.Thumb.Source = await Task.Run(() => Decode(jpg));
                        }
                        catch { }
                    }
                }
                var wait = 125 - (int)(DateTime.Now - started).TotalMilliseconds;
                await Task.Delay(Math.Max(wait, 16));
            }
        }
        finally { _thumbLoop = false; }
    }

    // Scenes whose still frame should be retaken once.
    readonly HashSet<string> _staleThumbs = new HashSet<string>();

    static BitmapImage Decode(byte[] jpg)
    {
        var b = new BitmapImage();
        using var ms = new MemoryStream(jpg);
        b.BeginInit();
        b.CacheOption = BitmapCacheOption.OnLoad;
        b.StreamSource = ms;
        b.EndInit();
        b.Freeze();
        return b;
    }

    // ---- Transition ----

    void ShowTransitions((List<ObsTransition> Transitions, string Current, int? DurationMs) t)
    {
        _loadingTransitions = true;
        TransitionBox.ItemsSource = t.Transitions.Select(x => x.Name).ToList();
        TransitionBox.SelectedItem = t.Current;
        _loadingTransitions = false;
        _duration = t.DurationMs;
        bool isFixed = t.Transitions.FirstOrDefault(x => x.Name == t.Current)?.Fixed ?? t.DurationMs == null;
        DurationBox.IsEnabled = !isFixed;
        if (!DurationBox.IsKeyboardFocusWithin) DurationBox.Text = isFixed ? "" : t.DurationMs?.ToString() ?? "";
        DurationBox.ToolTip = isFixed ? "This transition has no duration" : "How long the transition takes, 50 to 20000 ms";
    }

    async void Transition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTransitions || TransitionBox.SelectedItem is not string name) return;
        try { await Hub.Obs.SetTransitionAsync(name); }
        catch (Exception ex) { Status.Text = "Couldn't change the transition: " + ex.Message; }
        Queue(Part.Transitions);
    }

    void Duration_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyDuration();
    }

    void Duration_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ApplyDuration();

    async void ApplyDuration()
    {
        if (!DurationBox.IsEnabled) return;
        if (!int.TryParse(DurationBox.Text.Trim(), out var ms)) { DurationBox.Text = _duration?.ToString() ?? ""; return; }
        ms = Math.Clamp(ms, 50, 20000);
        DurationBox.Text = ms.ToString();
        if (ms == _duration) return;
        try
        {
            await Hub.Obs.SetTransitionDurationAsync(ms);
            _duration = ms;
        }
        catch (Exception ex) { Status.Text = "Couldn't change the transition time: " + ex.Message; }
    }

    // ---- Layers ----

    void BuildLayers()
    {
        LayerList.Children.Clear();
        _layerRows.Clear();
        LayersEmpty.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayersEmpty.Text = _selectedUuid == null ? "Pick a scene to see its layers." : "This scene is empty.";
        // A copy: SetLayerShown below writes back into _items, which would break a foreach over the list itself.
        foreach (var item in _items.ToList())
        {
            var eye = new Button { Style = (Style)FindResource("Ghost"), Padding = new Thickness(6, 3, 6, 3), MinHeight = 0, Margin = new Thickness(0, 0, 6, 0) };
            var name = new TextBlock { Text = item.SourceName, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var kind = new TextBlock { Text = KindLabel(item), Style = (Style)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(eye, Dock.Left);
            DockPanel.SetDock(kind, Dock.Right);
            row.Children.Add(eye);
            row.Children.Add(kind);
            if (item.Locked)
            {
                var lk = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 12, Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Locked in OBS (it can still be shown or hidden)" };
                DockPanel.SetDock(lk, Dock.Right);
                row.Children.Add(lk);
            }
            row.Children.Add(name);

            int id = item.Id;
            eye.Click += (_, _) => ToggleLayer(id);
            _layerRows[id] = (eye, name);
            SetLayerShown(id, item.Enabled);
            LayerList.Children.Add(row);
        }
        UpdateCards();
    }

    static string KindLabel(ObsSceneItem item)
    {
        if (item.IsGroup) return "Group";
        return item.InputKind switch
        {
            null => "Scene",
            "monitor_capture" => "Screen",
            "window_capture" => "Window",
            "game_capture" => "Game",
            "dshow_input" => "Camera",
            "wasapi_input_capture" => "Mic",
            "wasapi_output_capture" => "Desktop audio",
            "wasapi_process_output_capture" => "App audio",
            "ffmpeg_source" or "vlc_source" => "Media",
            "browser_source" => "Browser",
            "image_source" or "slideshow" or "slideshow_v2" => "Image",
            "text_gdiplus" or "text_gdiplus_v2" or "text_gdiplus_v3" or "text_ft2_source" or "text_ft2_source_v2" => "Text",
            "color_source" or "color_source_v2" or "color_source_v3" => "Colour",
            _ => item.InputKind,
        };
    }

    void SetLayerShown(int id, bool shown)
    {
        if (!_layerRows.TryGetValue(id, out var r)) return;
        var i = _items.FindIndex(x => x.Id == id);
        if (i >= 0) _items[i] = _items[i] with { Enabled = shown };
        r.Eye.Content = new TextBlock
        {
            Text = shown ? "" : "",
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 15,
            Foreground = (Brush)FindResource(shown ? "TextBrush" : "MutedBrush"),
        };
        r.Eye.ToolTip = shown ? "Showing. Click to hide." : "Hidden. Click to show.";
        r.Name.Foreground = (Brush)FindResource(shown ? "TextBrush" : "MutedBrush");
    }

    async void ToggleLayer(int id)
    {
        var item = _items.FirstOrDefault(x => x.Id == id);
        var scene = _selectedUuid;
        if (item == null || scene == null) return;
        bool show = !item.Enabled;
        SetLayerShown(id, show); // right away; put back if OBS says no
        try { await Hub.Obs.SetSceneItemEnabledAsync(scene, id, show); }
        catch (Exception e)
        {
            if (scene == _selectedUuid) SetLayerShown(id, !show);
            Status.Text = $"Couldn't {(show ? "show" : "hide")} \"{item.SourceName}\": {e.Message}";
        }
    }

    // ---- Audio mixer ----

    void BuildMixer(List<ObsAudioInput> inputs)
    {
        MixerList.Children.Clear();
        _mixer.Clear();
        AudioEmpty.Visibility = inputs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var input in inputs)
        {
            var row = new MixerRow { Input = input };

            row.Db = new TextBlock { Style = (Style)FindResource("Muted"), Width = 62, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            row.Mute = new Button { Style = (Style)FindResource("Small"), Padding = new Thickness(8, 4, 8, 4) };
            row.Mute.Click += (_, _) => ToggleMute(row);
            var name = new TextBlock { Text = input.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var head = new DockPanel();
            DockPanel.SetDock(row.Mute, Dock.Right);
            DockPanel.SetDock(row.Db, Dock.Right);
            head.Children.Add(row.Mute);
            head.Children.Add(row.Db);
            head.Children.Add(name);

            // Meter: a green-yellow-red bar, with a cover that shrinks from the right as the level rises.
            row.MeterCover = new ScaleTransform(1, 1);
            var cover = new Border { Background = (Brush)FindResource("Panel3Brush"), RenderTransform = row.MeterCover, RenderTransformOrigin = new Point(1, 0.5) };
            var meter = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 2), ClipToBounds = true };
            meter.Children.Add(new Border { Background = MeterBrush(), CornerRadius = new CornerRadius(3) });
            meter.Children.Add(cover);
            row.Meter = meter;

            row.Slider = new Slider { Minimum = MeterFloorDb, Maximum = 0, SmallChange = 1, LargeChange = 3, Margin = new Thickness(0, 2, 0, 0) };
            row.Slider.ValueChanged += (_, _) => Volume_Changed(row);

            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            box.Children.Add(head);
            box.Children.Add(meter);
            box.Children.Add(row.Slider);
            MixerList.Children.Add(box);

            _mixer[input.Uuid] = row;
            ShowVolume(row, input.VolumeDb);
            ShowMute(row, input.Muted);
        }
    }

    static Brush _meterBrush;
    static Brush MeterBrush()
    {
        if (_meterBrush != null) return _meterBrush;
        // Same zones as OBS's meters: green to -20 dB, yellow to -9 dB, red above (on a -60..0 dB scale).
        var g = Color.FromRgb(0x34, 0xD3, 0x99);
        var y = Color.FromRgb(0xFB, 0xBF, 0x24);
        var r = Color.FromRgb(0xF8, 0x71, 0x71);
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(g, 0));
        b.GradientStops.Add(new GradientStop(g, 0.667));
        b.GradientStops.Add(new GradientStop(y, 0.667));
        b.GradientStops.Add(new GradientStop(y, 0.85));
        b.GradientStops.Add(new GradientStop(r, 0.85));
        b.GradientStops.Add(new GradientStop(r, 1));
        b.Freeze();
        return _meterBrush = b;
    }

    static string DbText(double db) => db <= MeterFloorDb ? "-inf dB" : $"{db:+0.0;-0.0;0.0} dB";

    void ShowVolume(MixerRow row, double db)
    {
        row.Loading = true;
        row.Slider.Value = Math.Clamp(db, MeterFloorDb, 0);
        row.Loading = false;
        row.Db.Text = DbText(db); // may be above 0 dB if boosted in OBS
    }

    void ShowMute(MixerRow row, bool muted)
    {
        row.Muted = muted;
        row.Mute.Style = (Style)FindResource(muted ? "SmallDanger" : "Small");
        row.Mute.Padding = new Thickness(8, 4, 8, 4);
        row.Mute.Content = new TextBlock { Text = muted ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14 };
        row.Mute.ToolTip = muted ? "Muted. Click to unmute." : "Click to mute";
        row.Meter.Opacity = muted ? 0.3 : 1;
    }

    // Writes are throttled: at most one in flight, and only the newest slider position is sent after it.
    async void Volume_Changed(MixerRow row)
    {
        if (row.Loading) return;
        var v = row.Slider.Value;
        var db = v <= MeterFloorDb + 0.01 ? -100 : Math.Round(v, 1);
        row.Db.Text = DbText(db);
        row.LastLocal = DateTime.UtcNow;
        row.PendingDb = db;
        if (row.Sending) return;
        row.Sending = true;
        try
        {
            while (row.PendingDb is double next)
            {
                row.PendingDb = null;
                try { await Hub.Obs.SetInputVolumeDbAsync(row.Input.Uuid, next); }
                catch (Exception e) { Status.Text = $"Couldn't set the volume of \"{row.Input.Name}\": {e.Message}"; }
                row.LastLocal = DateTime.UtcNow;
                await Task.Delay(60);
            }
        }
        finally { row.Sending = false; }
    }

    async void ToggleMute(MixerRow row)
    {
        bool mute = !row.Muted;
        ShowMute(row, mute);
        try { await Hub.Obs.SetInputMuteAsync(row.Input.Uuid, mute); }
        catch (Exception e)
        {
            ShowMute(row, !mute);
            Status.Text = $"Couldn't {(mute ? "mute" : "unmute")} \"{row.Input.Name}\": {e.Message}";
        }
    }

    void UpdateMeters()
    {
        var levels = _levels;
        bool fresh = (DateTime.UtcNow - _levelsAt).TotalMilliseconds < 400;
        foreach (var (uuid, row) in _mixer)
        {
            double target = fresh && levels.TryGetValue(uuid, out var db) ? Math.Clamp((db - MeterFloorDb) / -MeterFloorDb, 0, 1) : 0;
            var shown = target >= row.Shown ? target : Math.Max(target, row.Shown - 0.035);
            if (shown == row.Shown) continue;
            row.Shown = shown;
            row.MeterCover.ScaleX = 1 - shown;
        }
    }

    // ---- Editing a scene in OBS itself ----

    bool ObsWindowShowing => Hub.Engine.State().visible;

    void EditObs_Click(object sender, RoutedEventArgs e)
    {
        if (ObsWindowShowing) Hub.Engine.HideWindow();
        else if (!Hub.Engine.ShowWindow())
            Status.Text = ObsHost.IsRunning ? "Couldn't find OBS's window." : "OBS isn't running.";
        UpdateEditButton();
    }

    void UpdateEditButton()
    {
        bool showing = ObsWindowShowing;
        EditObsButton.Content = showing ? "Done editing" : "Edit in OBS";
        EditObsButton.IsEnabled = ObsHost.IsRunning;
        EditNote.Visibility = showing ? Visibility.Visible : Visibility.Collapsed;
    }
}
