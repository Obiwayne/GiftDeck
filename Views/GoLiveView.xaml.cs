using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GiftDeck.Services;

namespace GiftDeck.Views;

// The control room: the LIVE button and record light, a preview of what OBS sends, and the music.
public partial class GoLiveView : UserControl
{
    bool _confirmed;          // TikTok said the LIVE is showing (after Go LIVE)
    bool _sending;            // OBS was started on this LIVE (vertical: while the relay is still running)
    DateTime? _liveSince;     // when TikTok (or GiftDeck) first showed us live
    bool _previewOn = true;
    string _sceneUuid;
    DateTime _sceneCheckedAt;
    readonly DispatcherTimer _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
    int _frames;
    DateTime _fpsSince = DateTime.Now;
    string _previewName = "";
    bool _previewLoop;        // the frame loop is running (only while this page is on screen)
    readonly Storyboard _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };

    TikTokLiveService Tt => Hub.TikTok;

    // GiftDeck runs OBS on a portrait main canvas: stream that, not Aitum's vertical canvas through the relay.
    static bool Managed => Hub.Settings.ObsManaged;
    bool UseVertical => Tt.State.SendVertical && !Managed;
    bool AutoObs => Tt.State.AutoObs || Managed; // nobody can paste a key into an OBS they can't see

    public GoLiveView()
    {
        InitializeComponent();

        var fade = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700));
        Storyboard.SetTarget(fade, RecDot);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _pulse.Children.Add(fade);

        _clock.Tick += (a, b) => UpdateLive();
        _clock.Start();
        IsVisibleChanged += (a, b) => { if (IsVisible) _ = PreviewLoopAsync(); };

        Tt.StatusChanged += () => Dispatcher.BeginInvoke(UpdateLive);
        Hub.TikFinity.StatusChanged += () => Dispatcher.BeginInvoke(UpdateLive);
        BridgeService.AccountChanged += () => Dispatcher.BeginInvoke(UpdateLive);
        UpdateLive();
    }

    // Live when GiftDeck opened a LIVE, or when TikTok itself shows the account live (e.g. started from LIVE Studio).
    // "Confirmed" only comes from TikTok: the chat feed being in the LIVE room, or the check after Go LIVE.
    void UpdateLive()
    {
        var s = Tt.State;
        bool tiktokSaysLive = Hub.TikFinity.Connected && Hub.TikFinity.TikTokLive == true;
        bool opened = Tt.Live;
        bool live = opened || tiktokSaysLive;
        bool confirmed = tiktokSaysLive || _confirmed;
        if (!opened) { _confirmed = false; _sending = false; }
        if (opened && _sending && UseVertical && !Hub.Relay.Running) _sending = false; // Aitum stopped sending

        if (live && _liveSince == null) _liveSince = s.StartedAt ?? DateTime.Now;
        if (!live) _liveSince = null;

        GoLiveButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible; // already live (e.g. LIVE Studio): a second LIVE would clash
        EndLiveButton.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;

        if (live)
        {
            LivePill.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            RecDot.Fill = Brushes.White;
            LiveWord.Text = "LIVE";
            LiveWord.Foreground = LiveTimer.Foreground = Brushes.White;
            var t = DateTime.Now - _liveSince.Value;
            LiveTimer.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            if (!_pulsing) { _pulse.Begin(); _pulsing = true; }

            if (confirmed) { VerifyText.Text = "\u2713 TikTok confirms you're live"; VerifyText.Foreground = (Brush)FindResource("SuccessBrush"); }
            else if (_sending) { VerifyText.Text = "Sending\u2026 waiting for TikTok to show the LIVE"; VerifyText.Foreground = (Brush)FindResource("WarnBrush"); }
            else { VerifyText.Text = "\u2716 Not sending: OBS isn't streaming to this LIVE. See below, or press End LIVE."; VerifyText.Foreground = (Brush)FindResource("DangerBrush"); }
            LiveDetail.Text = opened
                ? $"\"{(string.IsNullOrEmpty(s.Title) ? "LIVE" : s.Title)}\"" + (string.IsNullOrEmpty(s.CategoryName) ? "" : " \u00b7 " + s.CategoryName)
                : "Started outside GiftDeck (e.g. TikTok LIVE Studio)";
            if (ServerBox.Text != (s.Server ?? "")) ServerBox.Text = s.Server ?? "";
            if (KeyBox.Text != (s.Key ?? "")) KeyBox.Text = s.Key ?? "";
        }
        else
        {
            LivePill.Background = (Brush)FindResource("Panel2Brush");
            RecDot.Fill = (Brush)FindResource("MutedBrush");
            LiveWord.Text = "OFFLINE";
            LiveWord.Foreground = LiveTimer.Foreground = (Brush)FindResource("TextBrush");
            LiveTimer.Text = "";
            if (_pulsing) { _pulse.Stop(); RecDot.Opacity = 1; _pulsing = false; }
            VerifyText.Text = "Not live";
            VerifyText.Foreground = (Brush)FindResource("TextBrush");
            LiveDetail.Text = BridgeService.NeedsUsername ? "Set your TikTok username on the Stream Setup page so GiftDeck can read your LIVE." :
                string.IsNullOrEmpty(s.Title) ? "Set the title and category below." : $"Next LIVE: \"{s.Title}\"" + (string.IsNullOrEmpty(s.CategoryName) ? "" : " \u00b7 " + s.CategoryName);
        }
    }

    bool _pulsing;

    // Reads Aitum's settings file (OBS runs on this PC) to see whether the output sends to GiftDeck's relay.
    // Null when it can't tell (no file, or the output isn't listed), so Go LIVE carries on as before.
    static bool? AitumRelayConfigured(string outputName)
    {
        try
        {
            var profiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "basic", "profiles");
            if (!Directory.Exists(profiles)) return null;
            bool? result = null;
            foreach (var file in Directory.GetFiles(profiles, "aitum.json", SearchOption.AllDirectories))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("outputs", out var outputs)) continue;
                foreach (var o in outputs.EnumerateArray())
                {
                    if (!o.TryGetProperty("name", out var n) || !string.Equals(n.GetString(), outputName, StringComparison.OrdinalIgnoreCase)) continue;
                    bool pointsAtRelay = o.EnumerateObject().Any(pr => pr.Value.ValueKind == System.Text.Json.JsonValueKind.String
                                                                       && (pr.Value.GetString() ?? "").Contains("127.0.0.1:" + RelayService.Port));
                    if (pointsAtRelay) return true;
                    result = false;
                }
            }
            return result;
        }
        catch { return null; }
    }

    // Streams snapshots of the scene OBS is sending (the vertical canvas's scene, or the main program scene):
    // the next frame is requested as soon as the last one arrives, capped at about 30 a second, and each JPEG
    // is decoded on a worker thread so the window stays smooth.
    async Task PreviewLoopAsync()
    {
        if (_previewLoop) return;
        _previewLoop = true;
        try
        {
            var frameTime = TimeSpan.FromMilliseconds(33);
            while (IsVisible)
            {
                var started = DateTime.Now;
                if (_previewOn) await GrabPreviewAsync();
                else await Task.Delay(250);
                var wait = frameTime - (DateTime.Now - started);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
            }
        }
        finally { _previewLoop = false; }
    }

    async Task GrabPreviewAsync()
    {
        if (!Hub.Obs.Connected)
        {
            PreviewImage.Source = null;
            PreviewHint.Text = Managed ? "OBS isn't running. GiftDeck starts it for you; see OBS engine on the Stream Setup page." : "OBS isn't connected. Open OBS and GiftDeck will connect by itself.";
            PreviewSource.Text = "";
            await Task.Delay(1000);
            return;
        }
        try
        {
            bool vertical = UseVertical && Hub.Obs.Canvases.Count > 0;
            if (_sceneUuid == null || (DateTime.Now - _sceneCheckedAt).TotalSeconds > 2)
            {
                _sceneUuid = vertical ? await Hub.Obs.GetCanvasSceneUuidAsync(Hub.Obs.Canvases[0]) : await Hub.Obs.GetProgramSceneUuidAsync();
                _sceneCheckedAt = DateTime.Now;
                _previewName = vertical ? Hub.Obs.Canvases[0] : Managed ? "Portrait canvas" : "OBS main canvas";
            }
            if (_sceneUuid == null) { await Task.Delay(500); return; }

            // Always the same size: sizing it from the on-screen box made the box and the picture
            // chase each other by a pixel every frame (the wobble on the right edge).
            var jpg = await Hub.Obs.GetScreenshotAsync(_sceneUuid, vertical || Managed ? 540 : 960);
            var img = await Task.Run(() =>
            {
                var b = new BitmapImage();
                using var ms = new MemoryStream(jpg);
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.StreamSource = ms;
                b.EndInit();
                b.Freeze();
                return b;
            });
            if (!_previewOn) return;
            PreviewImage.Source = img;
            PreviewHint.Text = "";
            _frames++;
            var secs = (DateTime.Now - _fpsSince).TotalSeconds;
            if (secs >= 1)
            {
                PreviewSource.Text = $"{_previewName} · {_frames / secs:0} fps";
                _frames = 0;
                _fpsSince = DateTime.Now;
            }
        }
        catch (Exception ex)
        {
            _sceneUuid = null;
            PreviewHint.Text = "Preview unavailable: " + ex.Message;
            await Task.Delay(1000);
        }
    }

    void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        _previewOn = !_previewOn;
        PreviewButton.Content = _previewOn ? "Pause preview" : "Resume preview";
        if (!_previewOn) { PreviewImage.Source = null; PreviewHint.Text = "Preview paused"; }
    }

    async void GoLive_Click(object sender, RoutedEventArgs e)
    {
        if (AutoObs && UseVertical && RelayService.FindFfmpeg() == null)
        {
            Status.Text = "Not started. Going LIVE with the vertical canvas needs ffmpeg: on the Stream Setup page, click Download ffmpeg, then press Go LIVE again.";
            return;
        }
        if (AutoObs && UseVertical && AitumRelayConfigured(Tt.State.AitumOutput) == false)
        {
            Status.Text = $"Not started. Aitum's \"{Tt.State.AitumOutput}\" output in OBS isn't set up yet: edit it, choose Custom, Server {RelayService.LocalServer}, Stream key {RelayService.LocalKey} (also on the Stream Setup page). Then press Go LIVE again.";
            return;
        }
        GoLiveButton.IsEnabled = false;
        Status.Text = "Opening the LIVE on TikTok";
        try
        {
            var (server, key) = await Tt.StartAsync();
            Status.Text = "LIVE is open on TikTok.";
            bool sending = false;
            if (AutoObs)
            {
                try
                {
                    if (!Hub.Obs.Connected)
                    {
                        if (Managed && !ObsHost.IsRunning) { Status.Text = "LIVE is open. Starting OBS"; await Hub.Engine.StartPortraitAsync(); }
                        else await Hub.Obs.ConnectAsync();
                    }
                    sending = UseVertical ? await StartVerticalAsync(server, key) : await StartMainAsync(server, key);
                }
                catch (Exception ex)
                {
                    Status.Text = "LIVE is open, but OBS could not be started: " + ex.Message + " Copy the key below into OBS.";
                }
            }
            else Status.Text = "LIVE is open. Copy the server and key below into OBS and start streaming.";
            if (Tt.State.ResetTotalsOnLive) Hub.Overlays.ResetStats();
            _sending = sending;
            if (sending) _ = ConfirmOnTikTokAsync();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            GoLiveButton.IsEnabled = true;
            UpdateLive();
        }
    }

    async Task<bool> StartMainAsync(string server, string key)
    {
        await Hub.Obs.SetStreamSettingsAsync(server, key);
        if (await Hub.Obs.IsStreamingAsync())
        {
            Status.Text = "LIVE is open. OBS was already streaming, so its key was updated; restart the OBS stream if the picture does not appear.";
            return false;
        }
        await Hub.Obs.StartStreamAsync();
        Status.Text = Managed ? "LIVE is open and OBS is streaming your portrait canvas to it." : "LIVE is open and OBS is streaming its main canvas to it.";
        Log.Write("OBS given the TikTok stream key and started");
        return true;
    }

    // Aitum's vertical output sends to the local relay, and the relay forwards it to this LIVE's server and key.
    async Task<bool> StartVerticalAsync(string server, string key)
    {
        var output = Tt.State.AitumOutput;
        var exists = await Hub.Obs.IsAitumOutputActiveAsync(output);
        if (exists == null)
        {
            Status.Text = $"LIVE is open, but OBS has no Aitum output called \"{output}\". Set it up (see Vertical canvas below), or untick \"Send the Aitum Vertical canvas\".";
            return false;
        }
        if (exists == true) await Hub.Obs.StopAitumOutputAsync(output); // left running from before: restart it on the new relay

        Status.Text = "LIVE is open. Starting the vertical stream";
        Hub.Relay.Start(server, key);
        await Task.Delay(1500); // let ffmpeg start listening before Aitum connects
        await Hub.Obs.StartAitumOutputAsync(output);

        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(1000);
            if (!Hub.Relay.Running) break;
            if (await Hub.Obs.IsAitumOutputActiveAsync(output) == true)
            {
                Status.Text = "LIVE is open and OBS is sending the vertical canvas to it. Waiting for TikTok to show it";
                Log.Write($"Aitum \"{output}\" is streaming to TikTok through the relay");
                return true;
            }
        }

        var why = Hub.Relay.Running ? "" : " Relay: " + (Hub.Relay.LastError ?? "stopped");
        try { await Hub.Obs.StopAitumOutputAsync(output); } catch { }
        Hub.Relay.Stop();
        Status.Text = $"LIVE is open, but the \"{output}\" output in OBS didn't start. It needs to send to Server {RelayService.LocalServer} with Stream key {RelayService.LocalKey} (one-time setup, see below).{why}";
        return false;
    }

    // OBS being connected isn't proof TikTok shows the LIVE; ask TikTok for up to 90 seconds.
    async Task ConfirmOnTikTokAsync()
    {
        var user = Hub.Settings.BridgeUsername;
        if (string.IsNullOrWhiteSpace(user)) return;
        var started = DateTime.Now;
        while ((DateTime.Now - started).TotalSeconds < 90 && Tt.Live)
        {
            await Task.Delay(5000);
            try
            {
                if (await TikTokLiveService.IsShowingLiveAsync(user.Trim().TrimStart('@')))
                {
                    _confirmed = true;
                    Status.Text = "";
                    UpdateLive();
                    Log.Write("TikTok confirms the LIVE is showing");
                    return;
                }
            }
            catch { }
            Status.Text = $"OBS is sending. Waiting for TikTok to show the LIVE ({(int)(DateTime.Now - started).TotalSeconds}s)";
        }
        if (!Tt.Live) return;
        Status.Text = "⚠ OBS is sending, but TikTok still isn't showing you as LIVE after 90 seconds. Check the TikTok app; if it isn't there, End LIVE and try again.";
        Log.Write("TikTok did not show the LIVE within 90 seconds");
    }

    async void EndLive_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("End the LIVE on TikTok and stop streaming in OBS?", "GiftDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        EndLiveButton.IsEnabled = false;
        Status.Text = "Ending the LIVE";
        try
        {
            if (AutoObs && Hub.Obs.Connected)
            {
                try
                {
                    if (UseVertical) { if (await Hub.Obs.IsAitumOutputActiveAsync(Tt.State.AitumOutput) == true) await Hub.Obs.StopAitumOutputAsync(Tt.State.AitumOutput); }
                    else if (await Hub.Obs.IsStreamingAsync()) await Hub.Obs.StopStreamAsync();
                }
                catch (Exception ex) { Log.Write("Could not stop the OBS stream: " + ex.Message); }
            }
            Hub.Relay.Stop();
            _sending = false;
            await Tt.EndAsync();
            Status.Text = "LIVE ended.";
        }
        catch (Exception ex)
        {
            Status.Text = "TikTok reported a problem ending the LIVE (" + ex.Message + "). It has been cleared here; check the TikTok app if it still shows you as live.";
        }
        finally
        {
            EndLiveButton.IsEnabled = true;
            UpdateLive();
        }
    }

    void CopyServer_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Tt.State.Server ?? ""); } catch { } }
    void CopyKey_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Tt.State.Key ?? ""); } catch { } }
}
