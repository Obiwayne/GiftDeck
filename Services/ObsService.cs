using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GiftDeck.Services;

// obs-websocket v5 client (built into OBS 28 and later).
public partial class ObsService
{
    public event Action StatusChanged;

    // OBS events (scene switched, input volume meters, ...): (eventType, eventData). Raised on a background thread.
    public event Action<string, JsonElement> EventReceived;

    // OBS's stream output stopped (by itself, from its window, or a failed start). Raised on a background thread.
    public event Action StreamStopped;

    // GiftDeck started OBS's stream for the open LIVE and hasn't stopped it on purpose: if GiftDeck's own OBS
    // closes and is started again, it streams to the LIVE again (see ObsHost).
    public bool KeepSending { get; set; }

    // obs-websocket event subscriptions: every normal category, plus the high-volume input meters for the audio mixer.
    const int EventSubscriptions = 2047 | (1 << 16);

    public bool Connected { get; private set; }
    public string LastError { get; private set; }
    public List<string> Scenes { get; private set; } = new List<string>();

    // Extra canvases from the Aitum Stream Suite plugin (for example "Aitum Vertical"), each with its own scenes.
    public List<string> Canvases { get; private set; } = new List<string>();
    public Dictionary<string, List<string>> CanvasScenes { get; private set; } = new Dictionary<string, List<string>>();

    ClientWebSocket _ws;
    CancellationTokenSource _cts;
    TaskCompletionSource<bool> _identified;
    readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>();
    // The connect attempt in progress, if any: a second caller waits for it instead of starting another.
    Task _connectTask;
    readonly object _connectLock = new object();
    bool Connecting { get { lock (_connectLock) return _connectTask != null && !_connectTask.IsCompleted; } }

    public void StartAutoConnect()
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                if (!Connected && !Connecting && Hub.Settings.ObsAutoConnect)
                {
                    try { await ConnectAsync(true); } catch { }
                }
                await Task.Delay(10000);
            }
        });
    }

    // Connects, or waits for the connect that's already running (and gets its result or error).
    public async Task ConnectAsync(bool quiet = false)
    {
        TaskCompletionSource<bool> mine;
        Task running;
        lock (_connectLock)
        {
            running = _connectTask != null && !_connectTask.IsCompleted ? _connectTask : null;
            mine = running == null ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            if (mine != null) _connectTask = mine.Task;
        }
        if (running != null) { await running; return; }
        try
        {
            await ConnectCoreAsync(quiet);
            mine.TrySetResult(true);
        }
        catch (Exception e)
        {
            mine.TrySetException(e);
            _ = mine.Task.Exception; // seen here, so nobody else has to wait on it
            throw;
        }
    }

    async Task ConnectCoreAsync(bool quiet)
    {
        try
        {
            await DisconnectAsync();
            var ws = new ClientWebSocket();
            ws.Options.AddSubProtocol("obswebsocket.json");
            var cts = new CancellationTokenSource();
            _ws = ws;
            _cts = cts;
            _identified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var url = $"ws://{Hub.Settings.ObsHost}:{Hub.Settings.ObsPort}";
            using (var timeout = new CancellationTokenSource(5000))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeout.Token))
                await ws.ConnectAsync(new Uri(url), linked.Token);

            _ = Task.Run(() => ReceiveLoop(ws, cts.Token));

            var done = await Task.WhenAny(_identified.Task, Task.Delay(8000));
            if (done != _identified.Task) throw new Exception("OBS did not answer the identify handshake");
            await _identified.Task;

            Connected = true;
            LastError = null;
            Log.Write("Connected to OBS at " + url);
            StatusChanged?.Invoke();
            for (int attempt = 1; ; attempt++)
            {
                try { Scenes = await GetScenesAsync(); break; }
                catch (Exception e) when (attempt < 15 && e.Message.Contains("not ready")) { await Task.Delay(1000); } // still loading
                catch (Exception e) { Log.Write("Could not list OBS scenes: " + e.Message); break; }
            }
            await RefreshCanvasesAsync();
            StatusChanged?.Invoke();
        }
        catch (Exception e)
        {
            var msg = e.InnerException?.Message ?? e.Message;
            LastError = msg.Contains("refused", StringComparison.OrdinalIgnoreCase) || msg.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase)
                ? "OBS is not running, or its WebSocket server is switched off (Tools, WebSocket Server Settings)."
                : msg;
            if (!quiet) Log.Write("OBS connection failed: " + LastError);
            await DisconnectAsync();
            StatusChanged?.Invoke();
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var ws = _ws;
        var cts = _cts;
        _ws = null;
        _cts = null;
        bool was = Connected;
        Connected = false;
        if (ws != null)
        {
            try { cts?.Cancel(); } catch { }
            try { if (ws.State == WebSocketState.Open) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
            try { ws.Dispose(); } catch { }
        }
        foreach (var p in _pending.Values) p.TrySetException(new Exception("OBS disconnected"));
        _pending.Clear();
        if (was) StatusChanged?.Invoke();
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        var message = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                await HandleMessage(ws, text, token);
            }
        }
        catch (Exception e) when (!token.IsCancellationRequested)
        {
            LastError = e.Message;
        }
        catch { }

        _identified?.TrySetException(new Exception(LastError ?? "OBS closed the connection"));
        if (_ws == ws)
        {
            _ws = null;
            bool was = Connected;
            Connected = false;
            foreach (var p in _pending.Values) p.TrySetException(new Exception("OBS disconnected"));
            _pending.Clear();
            if (was)
            {
                Log.Write("OBS connection closed");
                StatusChanged?.Invoke();
            }
        }
    }

    async Task HandleMessage(ClientWebSocket ws, string text, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        int op = root.GetProperty("op").GetInt32();
        var d = root.GetProperty("d");
        switch (op)
        {
            case 0: // Hello
            {
                string auth = null;
                if (d.TryGetProperty("authentication", out var a))
                {
                    var salt = a.GetProperty("salt").GetString();
                    var challenge = a.GetProperty("challenge").GetString();
                    auth = BuildAuth(Hub.Settings.ObsPassword ?? "", salt, challenge);
                }
                var identify = new { op = 1, d = new { rpcVersion = 1, authentication = auth, eventSubscriptions = EventSubscriptions } };
                await SendAsync(ws, JsonSerializer.Serialize(identify, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), token);
                break;
            }
            case 2: // Identified
                _identified?.TrySetResult(true);
                break;
            case 5: // Event
            {
                var type = d.GetProperty("eventType").GetString() ?? "";
                var data = d.TryGetProperty("eventData", out var ed) ? ed.Clone() : default;
                if (type == "StreamStateChanged" && data.ValueKind == JsonValueKind.Object
                    && data.TryGetProperty("outputState", out var os) && os.ValueKind == JsonValueKind.String
                    && os.GetString() == "OBS_WEBSOCKET_OUTPUT_STOPPED")
                {
                    Log.Write("OBS stopped streaming");
                    try { StreamStopped?.Invoke(); } catch (Exception e) { Log.Write("OBS stream-stopped handler failed: " + e.Message); }
                }
                try { EventReceived?.Invoke(type, data); } catch (Exception e) { Log.Write($"OBS event {type} handler failed: {e.Message}"); }
                break;
            }
            case 7: // RequestResponse
            {
                var id = d.GetProperty("requestId").GetString() ?? "";
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(d.Clone());
                break;
            }
        }
    }

    static string BuildAuth(string password, string salt, string challenge)
    {
        using var sha = SHA256.Create();
        var secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    async Task SendAsync(ClientWebSocket ws, string json, CancellationToken token)
    {
        await _sendLock.WaitAsync(token);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
        finally { _sendLock.Release(); }
    }

    public async Task<JsonElement> RequestAsync(string type, object data = null)
    {
        var ws = _ws;
        var cts = _cts;
        if (ws == null || ws.State != WebSocketState.Open) throw new Exception("OBS is not connected");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var payload = new { op = 6, d = new { requestType = type, requestId = id, requestData = data ?? new { } } };
        await SendAsync(ws, JsonSerializer.Serialize(payload), cts.Token);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (done != tcs.Task) { _pending.TryRemove(id, out _); throw new Exception("OBS did not answer " + type); }
        var d = await tcs.Task;
        var status = d.GetProperty("requestStatus");
        if (!status.GetProperty("result").GetBoolean())
        {
            var comment = status.TryGetProperty("comment", out var c) ? c.GetString() : "code " + status.GetProperty("code").GetInt32();
            throw new Exception($"OBS refused {type}: {comment}");
        }
        return d.TryGetProperty("responseData", out var rd) ? rd : default;
    }

    public async Task<List<string>> GetScenesAsync()
    {
        var r = await RequestAsync("GetSceneList");
        var list = r.GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("sceneName").GetString()).ToList();
        list.Reverse(); // OBS lists bottom to top
        return list;
    }

    public Task SetSceneAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new Exception("No scene name given");
        return RequestAsync("SetCurrentProgramScene", new { sceneName = name });
    }

    public async Task SetSourceVisibleAsync(string scene, string source, bool visible)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new Exception("No source name given");
        if (string.IsNullOrWhiteSpace(scene))
        {
            var cur = await RequestAsync("GetCurrentProgramScene");
            scene = cur.GetProperty("currentProgramSceneName").GetString();
        }
        var idr = await RequestAsync("GetSceneItemId", new { sceneName = scene, sourceName = source });
        int itemId = idr.GetProperty("sceneItemId").GetInt32();
        await RequestAsync("SetSceneItemEnabled", new { sceneName = scene, sceneItemId = itemId, sceneItemEnabled = visible });
    }

    // Streaming: hand OBS a server and key, then start or stop the stream output.
    public Task SetStreamSettingsAsync(string server, string key) =>
        RequestAsync("SetStreamServiceSettings", new
        {
            streamServiceType = "rtmp_custom",
            streamServiceSettings = new { server, key, use_auth = false },
        });

    public async Task<bool> IsStreamingAsync()
    {
        var r = await RequestAsync("GetStreamStatus");
        return r.TryGetProperty("outputActive", out var a) && a.GetBoolean();
    }

    public Task StartStreamAsync() => RequestAsync("StartStream");
    public Task StopStreamAsync() => RequestAsync("StopStream");

    // Aitum Stream Suite vendor requests (vertical canvas). Returns the inner responseData, or throws.
    async Task<JsonElement> VendorAsync(string requestType, object data = null)
    {
        var r = await RequestAsync("CallVendorRequest", new { vendorName = "aitum-stream-suite", requestType, requestData = data ?? new { } });
        var inner = r.GetProperty("responseData");
        if (inner.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            var err = inner.TryGetProperty("error", out var e) ? e.GetString() : "unknown error";
            throw new Exception($"Vertical canvas refused {requestType}: {err}");
        }
        return inner;
    }

    // Aitum's extra outputs (e.g. "Vertical Stream"). Null when the output does not exist.
    public async Task<bool?> IsAitumOutputActiveAsync(string name)
    {
        var r = await VendorAsync("get_outputs");
        foreach (var o in r.GetProperty("outputs").EnumerateArray())
            if (string.Equals(o.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                return o.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
        return null;
    }

    // For the Go LIVE preview: the scene currently showing on a canvas (Aitum) or on OBS's main program output.
    public async Task<string> GetCanvasSceneUuidAsync(string canvas)
    {
        var r = await VendorAsync("current_scene", new { canvas });
        return r.TryGetProperty("scene_uuid", out var u) ? u.GetString() : null;
    }

    public async Task<string> GetProgramSceneUuidAsync()
    {
        var r = await RequestAsync("GetCurrentProgramScene");
        return r.TryGetProperty("sceneUuid", out var u) ? u.GetString() : null;
    }

    // A JPEG snapshot of a source or scene, looked up by uuid so scenes on other canvases work too.
    public async Task<byte[]> GetScreenshotAsync(string sourceUuid, int width)
    {
        var r = await RequestAsync("GetSourceScreenshot", new { sourceUuid, imageFormat = "jpg", imageWidth = width, imageCompressionQuality = 75 });
        var data = r.GetProperty("imageData").GetString() ?? "";
        var comma = data.IndexOf(',');
        return Convert.FromBase64String(comma >= 0 ? data[(comma + 1)..] : data);
    }

    public Task StartAitumOutputAsync(string name) => VendorAsync("start_output", new { output = name });
    public Task StopAitumOutputAsync(string name) => VendorAsync("stop_output", new { output = name });

    public async Task RefreshCanvasesAsync()
    {
        var canvases = new List<string>();
        var scenes = new Dictionary<string, List<string>>();
        try
        {
            var r = await VendorAsync("get_canvas");
            foreach (var c in r.GetProperty("canvas").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name)) continue;
                canvases.Add(name);
                try { scenes[name] = await GetCanvasScenesAsync(name); }
                catch (Exception e) { Log.Write($"Could not list scenes of canvas \"{name}\": {e.Message}"); scenes[name] = new List<string>(); }
            }
            if (canvases.Count > 0) Log.Write("OBS vertical canvas found: " + string.Join(", ", canvases));
        }
        catch (Exception e) when (e.Message.Contains("vendor", StringComparison.OrdinalIgnoreCase))
        {
            // Aitum Stream Suite is not installed; that is fine.
        }
        catch (Exception e)
        {
            Log.Write("Could not read OBS canvases: " + e.Message);
        }
        Canvases = canvases;
        CanvasScenes = scenes;
    }

    public async Task<List<string>> GetCanvasScenesAsync(string canvas)
    {
        var r = await VendorAsync("get_scenes", new { canvas });
        return r.GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ToList();
    }

    public Task SetCanvasSceneAsync(string canvas, string scene)
    {
        if (string.IsNullOrWhiteSpace(scene)) throw new Exception("No scene name given");
        if (string.IsNullOrWhiteSpace(canvas)) canvas = Canvases.FirstOrDefault() ?? "Aitum Vertical";
        return VendorAsync("switch_scene", new { canvas, scene });
    }

    // Adds a Browser Source showing the given page to the scene that is currently live.
    // audioViaObs: "Control audio via OBS", so sound from the page (alert videos) goes into the stream mix.
    public async Task<string> AddBrowserSourceAsync(string name, string url, int width, int height, bool audioViaObs = false)
    {
        object settings = audioViaObs
            ? new { url, width, height, shutdown = false, restart_when_active = false, reroute_audio = true }
            : new { url, width, height, shutdown = false, restart_when_active = false };
        var cur = await RequestAsync("GetCurrentProgramScene");
        var scene = cur.GetProperty("currentProgramSceneName").GetString();
        var inputName = name;
        for (int i = 2; ; i++)
        {
            try
            {
                await RequestAsync("CreateInput", new
                {
                    sceneName = scene,
                    inputName,
                    inputKind = "browser_source",
                    inputSettings = settings,
                    sceneItemEnabled = true,
                });
                return $"Added \"{inputName}\" to scene \"{scene}\"";
            }
            catch (Exception e) when (e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) && i < 30)
            {
                inputName = $"{name} {i}";
            }
        }
    }

    // Reads OBS's own WebSocket settings so the user does not have to copy them by hand.
    public static (bool found, bool enabled, int port, string password, bool authRequired) ReadObsConfig()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json");
            if (!File.Exists(p)) return (false, false, 4455, "", true);
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            bool enabled = r.TryGetProperty("server_enabled", out var en) && en.GetBoolean();
            int port = r.TryGetProperty("server_port", out var po) ? po.GetInt32() : 4455;
            string pw = r.TryGetProperty("server_password", out var pa) ? pa.GetString() : "";
            bool auth = !r.TryGetProperty("auth_required", out var ar) || ar.GetBoolean();
            return (true, enabled, port, pw ?? "", auth);
        }
        catch
        {
            return (false, false, 4455, "", true);
        }
    }
}
