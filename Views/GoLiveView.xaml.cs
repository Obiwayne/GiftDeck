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
    bool _busy;               // Go LIVE, End LIVE or a restart is running: the others wait for it
    bool _lost;               // OBS or the relay stopped sending by itself: "Not sending", even if TikTok still shows the LIVE
    bool _obsRestarting;      // GiftDeck's own OBS closed mid-LIVE and is being started again
    bool _relayTrouble;       // the relay's problem is showing in the status line
    string _previewError;     // last preview error written to the log (so it isn't logged every second)
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

        _onStatus = () => Dispatcher.BeginInvoke(UpdateLive);
        _onStreamStopped = () => Dispatcher.BeginInvoke(OnStreamStopped);
        _onEngine = () => Dispatcher.BeginInvoke(OnEngineChanged);
        _onResumed = ok => Dispatcher.BeginInvoke(() => OnSendingResumed(ok));
        _onRelay = () => Dispatcher.BeginInvoke(OnRelayProblem);
        Tt.StatusChanged += _onStatus;
        Details.RestartRequested += RestartLive;
        Hub.TikFinity.StatusChanged += _onStatus;
        BridgeService.AccountChanged += _onStatus;
        Hub.Obs.StreamStopped += _onStreamStopped;
        Hub.Engine.StatusChanged += _onEngine;
        Hub.Engine.SendingResumed += _onResumed;
        Hub.Relay.ProblemChanged += _onRelay;
        UpdateLive();
    }

    readonly Action _onStatus, _onStreamStopped, _onEngine, _onRelay;
    readonly Action<bool> _onResumed;

    // The main window drops this page (a profile switch builds a new one): stop listening, so the old page
    // doesn't keep reacting to OBS and the LIVE next to the new one.
    public void Detach()
    {
        _clock.Stop();
        Tt.StatusChanged -= _onStatus;
        Hub.TikFinity.StatusChanged -= _onStatus;
        BridgeService.AccountChanged -= _onStatus;
        Hub.Obs.StreamStopped -= _onStreamStopped;
        Hub.Engine.StatusChanged -= _onEngine;
        Hub.Engine.SendingResumed -= _onResumed;
        Hub.Relay.ProblemChanged -= _onRelay;
    }

    // OBS's stream stopped while GiftDeck was sending to the LIVE (GiftDeck's own stops happen while _busy).
    void OnStreamStopped()
    {
        if (!Tt.Live || !_sending || _busy || UseVertical) return;
        _sending = false;
        _lost = true;
        _confirmed = false;
        Status.Text = "OBS stopped streaming to your LIVE. Press Start sending again, or End LIVE.";
        Log.Write("OBS stopped streaming during the LIVE");
        UpdateLive();
    }

    // GiftDeck's own OBS closed mid-LIVE: ObsHost starts it again and then sends again (OnSendingResumed).
    void OnEngineChanged()
    {
        if (!Tt.Live || UseVertical || !Hub.Obs.KeepSending) { _obsRestarting = false; return; }
        if (Hub.Engine.Restarting)
        {
            if (!_obsRestarting) Log.Write("OBS closed during the LIVE; waiting for MayhemDeck to start it again");
            _obsRestarting = true;
            _sending = false;
            _lost = true;
            _confirmed = false;
            Status.Text = "OBS closed during the LIVE. MayhemDeck is starting it again and will carry on sending…";
        }
        else if (_obsRestarting)
        {
            _obsRestarting = false;
            if (!Hub.Obs.Connected)
                Status.Text = "OBS closed during the LIVE and MayhemDeck couldn't start it again. " + (Hub.Engine.LastError ?? "") + " Press Start sending again, or End LIVE.";
        }
        else if (_sending && Managed && Hub.Engine.LastError != null && !Hub.Obs.Connected && !ObsHost.IsRunning)
        {
            // It keeps closing, so ObsHost stopped starting it again.
            Log.Write("OBS closed during the LIVE and wasn't started again");
            _sending = false;
            _lost = true;
            _confirmed = false;
            Status.Text = Hub.Engine.LastError + " Then press Start sending again, or End LIVE.";
        }
        UpdateLive();
    }

    void OnSendingResumed(bool ok)
    {
        if (!Tt.Live) return;
        if (ok)
        {
            _sending = true;
            _lost = false;
            Status.Text = "OBS closed during the LIVE; MayhemDeck started it again and it's sending to the LIVE again.";
            Log.Write("Sending to the LIVE again after OBS was restarted");
            _ = ConfirmOnTikTokAsync();
        }
        else Status.Text = "OBS is running again, but it couldn't start sending to the LIVE. Press Start sending again, or End LIVE.";
        UpdateLive();
    }

    // The relay (vertical canvas) stopped by itself: RelayService restarts it; show what's happening meanwhile.
    void OnRelayProblem()
    {
        var problem = Hub.Relay.Problem;
        if (problem != null)
        {
            _relayTrouble = true;
            Status.Text = problem;
            if (!Hub.Relay.Recovering) { _sending = false; _lost = true; _confirmed = false; } // gave up
        }
        else if (_relayTrouble)
        {
            _relayTrouble = false;
            if (Hub.Relay.Running && Tt.Live)
            {
                _sending = true;
                _lost = false;
                Status.Text = "The relay to TikTok is running again and OBS is sending the vertical canvas to the LIVE.";
            }
        }
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
        if (!opened) { _confirmed = false; _sending = false; _lost = false; }
        if (opened && _sending && UseVertical && !Hub.Relay.Running && !Hub.Relay.Recovering) { _sending = false; _lost = true; } // Aitum stopped sending
        bool relayDown = opened && UseVertical && Hub.Relay.Recovering;
        bool obsDown = opened && !_sending && Managed && Hub.Engine.Restarting && Hub.Obs.KeepSending;
        // TikTok keeps showing a LIVE for a while after the picture stops: a stop GiftDeck saw wins.
        bool confirmed = !_lost && !relayDown && (tiktokSaysLive || _confirmed);

        if (live && _liveSince == null) _liveSince = s.StartedAt ?? DateTime.Now;
        if (!live) _liveSince = null;

        // Already live (e.g. LIVE Studio): a second LIVE would clash. Opened here but OBS isn't sending:
        // the same button starts sending again with this LIVE's server and key.
        // Not while GiftDeck is bringing OBS back by itself.
        bool retry = opened && !_sending && !confirmed && !_busy && !obsDown;
        GoLiveButton.Visibility = !live || retry ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Content = retry ? "Start sending again" : "Go LIVE";
        GoLiveButton.IsEnabled = !_busy;
        EndLiveButton.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;
        EndLiveButton.IsEnabled = !_busy;
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

            if (relayDown) { VerifyText.Text = "✖ Not sending: the relay to TikTok stopped. MayhemDeck is restarting it…"; VerifyText.Foreground = (Brush)FindResource("DangerBrush"); }
            else if (obsDown) { VerifyText.Text = "✖ Not sending: OBS closed. MayhemDeck is starting it again…"; VerifyText.Foreground = (Brush)FindResource("DangerBrush"); }
            else if (confirmed) { VerifyText.Text = "\u2713 TikTok confirms you're live"; VerifyText.Foreground = (Brush)FindResource("SuccessBrush"); }
            else if (_sending) { VerifyText.Text = "Sending\u2026 waiting for TikTok to show the LIVE"; VerifyText.Foreground = (Brush)FindResource("WarnBrush"); }
            else { VerifyText.Text = "\u2716 Not sending: OBS isn't streaming to this LIVE. Press Start sending again, or End LIVE."; VerifyText.Foreground = (Brush)FindResource("DangerBrush"); }
            var liveTitle = s.LiveTitle ?? s.Title; // what the running LIVE uses, not edits that aren't applied yet
            var liveCategory = s.LiveTitle != null ? s.LiveCategoryName : s.CategoryName;
            LiveDetail.Text = opened
                ? $"\"{(string.IsNullOrEmpty(liveTitle) ? "LIVE" : liveTitle)}\"" + (string.IsNullOrEmpty(liveCategory) ? "" : " \u00b7 " + liveCategory)
                : "Started outside MayhemDeck (e.g. TikTok LIVE Studio)";
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
            LiveDetail.Text = BridgeService.NeedsUsername ? "Set your TikTok username on the Stream Setup page so MayhemDeck can read your LIVE." :
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
            PreviewHint.Text = Managed ? "OBS isn't running. MayhemDeck starts it for you; see OBS engine on the Stream Setup page." : "OBS isn't connected. Open OBS and MayhemDeck will connect by itself.";
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
            _previewError = null;
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
            PreviewHint.Text = "The preview isn't available right now. Trying again\u2026";
            if (ex.Message != _previewError) { _previewError = ex.Message; Log.Write("OBS preview failed: " + ex.Message); }
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
        if (_busy) return;
        if (UseVertical && RelayService.FindFfmpeg() == null)
        {
            Status.Text = "Not started. Going LIVE with the vertical canvas needs ffmpeg: on the Stream Setup page, click Download ffmpeg, then press Go LIVE again.";
            return;
        }
        if (UseVertical && AitumRelayConfigured(Tt.State.AitumOutput) == false)
        {
            Status.Text = $"Not started. Aitum's \"{Tt.State.AitumOutput}\" output in OBS isn't set up yet: edit it, choose Custom, Server {RelayService.LocalServer}, Stream key {RelayService.LocalKey} (also on the Stream Setup page). Then press Go LIVE again.";
            return;
        }
        _busy = true;
        UpdateLive();
        try
        {
            if (Tt.Live)
            {
                // The LIVE is already open but OBS isn't sending to it: try again with its saved server and key.
                Status.Text = "Starting to send to the open LIVE";
                bool again = await StartSendingAsync(Tt.State.Server, Tt.State.Key);
                _sending = again;
                if (again) _lost = false;
                if (again) _ = ConfirmOnTikTokAsync();
                return;
            }

            // OBS first: opening the LIVE makes it public, so there must be something ready to send.
            Status.Text = "Checking OBS";
            var obsProblem = await ConnectObsAsync();
            if (obsProblem != null) { Status.Text = "Not started. " + obsProblem; return; }

            Status.Text = "Opening the LIVE on TikTok";
            var (server, key) = await Tt.StartAsync();
            Hub.Rules.ResetLikeCounters(); // "every N likes" counts per viewer for this LIVE, not the last one
            Status.Text = "LIVE is open on TikTok.";
            bool sending = await StartSendingAsync(server, key);
            Hub.Overlays.ResetStats(); // every LIVE starts its goals and counters from zero
            _sending = sending;
            if (sending) _lost = false;
            if (sending) _ = ConfirmOnTikTokAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Go LIVE failed: " + ex.Message);
            Status.Text = Explain(ex, "open the LIVE");
        }
        finally
        {
            _busy = false;
            UpdateLive();
        }
    }

    // Makes sure GiftDeck can talk to OBS (starting GiftDeck's own OBS if needed).
    // Null when it's ready, otherwise what to do about it.
    async Task<string> ConnectObsAsync()
    {
        if (Hub.Obs.Connected) return null;
        try
        {
            if (Managed && !ObsHost.IsRunning) { Status.Text = "Starting OBS"; await Hub.Engine.StartPortraitAsync(); }
            else await Hub.Obs.ConnectAsync();
        }
        catch (Exception ex) { Log.Write("OBS isn't ready: " + ex.Message); }
        if (Hub.Obs.Connected) return null;
        return Managed
            ? "MayhemDeck couldn't start OBS. Check OBS engine on the Stream Setup page, then try again."
            : "OBS isn't connected. Open OBS (with Tools, WebSocket Server Settings switched on), then try again.";
    }

    // A plain sentence for the status line; the exact error goes to the log.
    static string Explain(Exception ex, string what)
    {
        if (ex is System.Net.Http.HttpRequestException || ex is TaskCanceledException)
            return $"MayhemDeck couldn't reach Streamlabs to {what}. Check your internet connection and try again.";
        var m = (ex.Message ?? "").Trim();
        if (m.StartsWith("Streamlabs answered") || m.StartsWith("Streamlabs sent") || m.StartsWith("TikTok did not"))
            return $"Streamlabs couldn't {what} just now. Try again in a minute.";
        return m.Length == 0 ? $"MayhemDeck couldn't {what}. Try again." : m.EndsWith(".") ? m : m + ".";
    }

    // Points OBS at the LIVE's server and key and starts sending.
    async Task<bool> StartSendingAsync(string server, string key)
    {
        try
        {
            var obsProblem = await ConnectObsAsync();
            if (obsProblem != null)
            {
                Status.Text = "LIVE is open, but OBS isn't sending. " + obsProblem + " Press Start sending again, or copy the server and key below into OBS.";
                return false;
            }
            return UseVertical ? await StartVerticalAsync(server, key) : await StartMainAsync(server, key);
        }
        catch (Exception ex)
        {
            Log.Write("Could not start sending to the LIVE: " + ex.Message);
            Status.Text = "LIVE is open, but OBS couldn't start sending. Press Start sending again, or copy the server and key below into OBS.";
            return false;
        }
    }

    async Task StopSendingAsync()
    {
        // Stopped on purpose: don't bring OBS's stream or the relay back.
        Hub.Obs.KeepSending = false;
        Hub.Relay.KeepAlive(false);
        if (Hub.Obs.Connected)
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
    }

    // New title, category or 18+ while LIVE: TikTok can't change a running LIVE, so end it and open a new one
    // straight away with the new details, and switch OBS over. Overlay totals keep counting.
    async void RestartLive()
    {
        var s = Tt.State;
        if (!Tt.Live) return;
        var next = "\u201c" + (string.IsNullOrWhiteSpace(s.Title) ? "LIVE" : s.Title.Trim()) + "\u201d"
                   + (string.IsNullOrEmpty(s.CategoryName) ? "" : " \u00b7 " + s.CategoryName) + (s.Mature ? " \u00b7 18+" : "");
        var ask = "Restart your LIVE as " + next + "?\n\nMayhemDeck ends this LIVE and starts the new one straight away (about 20 seconds). "
                  + "Viewers have to rejoin, and TikTok's likes and viewer count start again. Your MayhemDeck totals keep counting.";
        if (_busy) { Status.Text = "Wait for MayhemDeck to finish with the LIVE, then try the restart again."; return; }
        if (AppDialog.Show(ask, "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_busy || !Tt.Live) return;

        _busy = true;
        Details.SetBusy(true);
        UpdateLive();
        Log.Write("Restarting the LIVE with new details: " + next);
        try
        {
            Status.Text = "Restarting: stopping the stream\u2026";
            await StopSendingAsync();
            Status.Text = "Restarting: ending the old LIVE\u2026";
            try { await Tt.EndAsync(); }
            catch (Exception ex)
            {
                // The old LIVE is still open on TikTok: don't open a second one.
                Log.Write("Ending the old LIVE: " + ex.Message);
                Status.Text = "The restart stopped: " + Explain(ex, "end the old LIVE") + " Press Start sending again to carry on with it, or End LIVE.";
                return;
            }
            _confirmed = false;
            Status.Text = "Restarting: opening the new LIVE\u2026";
            var (server, key) = await Tt.StartAsync();
            bool sending = await StartSendingAsync(server, key);
            _sending = sending;
            if (sending) _lost = false;
            if (sending) _ = ConfirmOnTikTokAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "The restart didn't finish: " + Explain(ex, "open the new LIVE") + " Press Go LIVE to start again.";
            Log.Write("Restarting the LIVE failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            Details.SetBusy(false);
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
        Hub.Obs.KeepSending = true;
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
                Hub.Relay.KeepAlive(true);
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
        if (_busy) return;
        if (AppDialog.Show("End the LIVE on TikTok and stop streaming in OBS?", "MayhemDeck", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_busy) return;
        _busy = true;
        UpdateLive();
        Status.Text = "Ending the LIVE";
        try
        {
            await StopSendingAsync();
            await Tt.EndAsync();
            Status.Text = "LIVE ended.";
        }
        catch (Exception ex)
        {
            Log.Write("Ending the LIVE failed: " + ex.Message);
            Status.Text = "The LIVE may still be on TikTok: " + Explain(ex, "end the LIVE") + " Press End LIVE again.";
        }
        finally
        {
            _busy = false;
            UpdateLive();
        }
    }

    void CopyServer_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Tt.State.Server ?? ""); } catch { } }
    void CopyKey_Click(object sender, RoutedEventArgs e) { try { Clipboard.SetText(Tt.State.Key ?? ""); } catch { } }
}
