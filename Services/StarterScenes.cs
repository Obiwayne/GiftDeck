using System.Text.Json;

namespace GiftDeck.Services;

// "Create starter scenes" on the Scenes page: a ready-made set for someone who hasn't built scenes in OBS yet.
// Only missing scenes are added; a scene that already has one of these names is left exactly as it is.
// The camera, screen, background and overlay are one source each, shared by every scene that uses them,
// so choosing a different camera (in OBS) changes it everywhere at once.
public static class StarterScenes
{
    public const string Starting = "Starting Soon", Brb = "Be Right Back", Ending = "Stream Ending",
        CamGame = "Cam + Game", JustCam = "Just Cam", JustScreen = "Just Screen";
    public static readonly string[] Names = { Starting, CamGame, JustCam, JustScreen, Brb, Ending };

    const string Camera = "GiftDeck Camera", Screen = "GiftDeck Screen", Background = "GiftDeck Background", Overlay = "GiftDeck Overlay";

    public static List<string> Missing(IEnumerable<string> existing)
    {
        var have = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        return Names.Where(n => !have.Contains(n)).ToList();
    }

    public sealed class Result
    {
        public List<string> Created { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
    }

    public static async Task<Result> CreateAsync(IProgress<string> progress, ObsService obs = null)
    {
        obs ??= Hub.Obs;
        var result = new Result();
        var todo = Missing(await obs.GetScenesAsync());
        if (todo.Count == 0) return result;

        var (w, h) = await obs.GetCanvasSizeAsync();
        var b = new Builder(obs, w, h, result);
        await b.LoadAsync();

        foreach (var name in todo)
        {
            progress?.Report($"Creating \"{name}\"…");
            try
            {
                await obs.RequestAsync("CreateScene", new { sceneName = name });
            }
            catch (Exception e)
            {
                Log.Write($"Starter scene \"{name}\" not created: {e.Message}");
                result.Notes.Add($"\"{name}\" couldn't be created.");
                continue;
            }
            try
            {
                await b.FillAsync(name);
                result.Created.Add(name);
                Log.Write($"Starter scene \"{name}\" created");
            }
            catch (Exception e)
            {
                // The scene exists, just not everything in it: say so rather than leave it looking finished.
                Log.Write($"Starter scene \"{name}\" is incomplete: {e.Message}");
                result.Created.Add(name);
                result.Notes.Add($"\"{name}\" was made but isn't complete; finish it with Edit in OBS.");
            }
        }
        return result;
    }

    sealed class Builder
    {
        readonly ObsService _obs;
        readonly int _w, _h;
        readonly Result _result;
        readonly bool _portrait;
        HashSet<string> _inputs;
        HashSet<string> _kinds;
        string _overlayName = Overlay;
        bool _cameraChecked, _screenChecked;

        public Builder(ObsService obs, int w, int h, Result result)
        {
            _obs = obs; _w = w; _h = h; _result = result;
            _portrait = h > w;
        }

        public async Task LoadAsync()
        {
            var list = await _obs.RequestAsync("GetInputList");
            _inputs = new HashSet<string>(list.GetProperty("inputs").EnumerateArray().Select(i => i.GetProperty("inputName").GetString()), StringComparer.Ordinal);
            var kinds = await _obs.RequestAsync("GetInputKindList", new { unversioned = false });
            _kinds = new HashSet<string>(kinds.GetProperty("inputKinds").EnumerateArray().Select(k => k.GetString()));

            // Someone who already added the all-in-one overlay (Overlays page, Add to OBS) gets that one reused.
            foreach (var i in list.GetProperty("inputs").EnumerateArray())
            {
                if (i.GetProperty("inputKind").GetString() != "browser_source") continue;
                var name = i.GetProperty("inputName").GetString();
                try
                {
                    var s = await _obs.RequestAsync("GetInputSettings", new { inputName = name });
                    var url = s.GetProperty("inputSettings").TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    if (url.Contains("/overlay/all", StringComparison.OrdinalIgnoreCase)) { _overlayName = name; break; }
                }
                catch { }
            }
        }

        string Kind(params string[] choices) => choices.FirstOrDefault(_kinds.Contains) ?? choices[^1];

        public async Task FillAsync(string scene)
        {
            switch (scene)
            {
                case Starting:
                    await BackgroundAsync(scene);
                    await TitleAsync(scene, "Starting soon", "Grab a drink, we're about to go");
                    break;
                case Brb:
                    await BackgroundAsync(scene);
                    await TitleAsync(scene, "Be right back", "Don't go anywhere");
                    break;
                case Ending:
                    await BackgroundAsync(scene);
                    await TitleAsync(scene, "Thanks for watching!", "Follow so you don't miss the next one");
                    break;
                case CamGame:
                    await BackgroundAsync(scene);
                    if (_portrait)
                    {
                        // Face on top, the whole game underneath, room at the bottom for alerts and the spinner.
                        await Place(scene, await CameraAsync(scene), 0, 0, _w, _h * 0.40, "OBS_BOUNDS_SCALE_OUTER");
                        await Place(scene, await ScreenAsync(scene), 0, _h * 0.40, _w, _w * 9.0 / 16, "OBS_BOUNDS_SCALE_INNER");
                    }
                    else
                    {
                        await Place(scene, await ScreenAsync(scene), 0, 0, _w, _h, "OBS_BOUNDS_SCALE_INNER");
                        await Place(scene, await CameraAsync(scene), _w * 0.70, _h * 0.68, _w * 0.28, _h * 0.28, "OBS_BOUNDS_SCALE_OUTER");
                    }
                    break;
                case JustCam:
                    await Place(scene, await CameraAsync(scene), 0, 0, _w, _h, "OBS_BOUNDS_SCALE_OUTER");
                    break;
                case JustScreen:
                    await BackgroundAsync(scene);
                    await Place(scene, await ScreenAsync(scene), 0, 0, _w, _h, "OBS_BOUNDS_SCALE_INNER");
                    break;
            }
            await Place(scene, await OverlayAsync(scene), 0, 0, _w, _h, "OBS_BOUNDS_STRETCH"); // on top of everything
        }

        // ---- The shared sources ----

        async Task<int> BackgroundAsync(string scene)
        {
            // A deep purple (OBS colours are ABGR).
            var id = await AddSharedAsync(scene, Background, Kind("color_source_v3", "color_source_v2", "color_source"),
                new { color = 0xFF2E121EL, width = _w, height = _h });
            await Place(scene, id, 0, 0, _w, _h, "OBS_BOUNDS_STRETCH");
            return id;
        }

        async Task<int> CameraAsync(string scene)
        {
            var id = await AddSharedAsync(scene, Camera, Kind("dshow_input"), new { });
            if (!_cameraChecked)
            {
                _cameraChecked = true;
                if (!await PickFirstAsync(Camera, "video_device_id"))
                    _result.Notes.Add("No webcam was found, so the camera boxes are empty for now. Plug one in, then in Edit in OBS double-click \"GiftDeck Camera\" and choose it.");
            }
            return id;
        }

        async Task<int> ScreenAsync(string scene)
        {
            var id = await AddSharedAsync(scene, Screen, Kind("monitor_capture"), new { });
            if (!_screenChecked)
            {
                _screenChecked = true;
                if (!await PickFirstAsync(Screen, "monitor_id") && !await PickFirstAsync(Screen, "monitor"))
                    _result.Notes.Add("GiftDeck couldn't choose a screen to capture. In Edit in OBS, double-click \"GiftDeck Screen\" and pick your monitor.");
                _result.Notes.Add("\"GiftDeck Screen\" shows your whole monitor. For a game you can swap it for Game Capture in Edit in OBS; if it stays black on a laptop, that's Windows' graphics setting for OBS.");
            }
            return id;
        }

        Task<int> OverlayAsync(string scene) =>
            AddSharedAsync(scene, _overlayName, "browser_source",
                new { url = (Hub.Web?.BaseUrl ?? "http://localhost:21300") + "/overlay/all", width = _w, height = _h, shutdown = false, restart_when_active = false, reroute_audio = true });

        // Adds the source to the scene, creating it the first time.
        async Task<int> AddSharedAsync(string scene, string name, string kind, object settings)
        {
            if (_inputs.Contains(name))
            {
                var r = await _obs.RequestAsync("CreateSceneItem", new { sceneName = scene, sourceName = name, sceneItemEnabled = true });
                return r.GetProperty("sceneItemId").GetInt32();
            }
            var c = await _obs.RequestAsync("CreateInput", new { sceneName = scene, inputName = name, inputKind = kind, inputSettings = settings, sceneItemEnabled = true });
            _inputs.Add(name);
            return c.GetProperty("sceneItemId").GetInt32();
        }

        // Chooses the first real entry of a device list (camera, monitor). False when there's none.
        async Task<bool> PickFirstAsync(string input, string property)
        {
            try
            {
                var r = await _obs.RequestAsync("GetInputPropertiesListPropertyItems", new { inputName = input, propertyName = property });
                foreach (var item in r.GetProperty("propertyItems").EnumerateArray())
                {
                    if (item.TryGetProperty("itemEnabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                    var value = item.GetProperty("itemValue");
                    if (value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(value.GetString())) continue;
                    await _obs.RequestAsync("SetInputSettings", new { inputName = input, inputSettings = new Dictionary<string, object> { [property] = value } });
                    return true;
                }
            }
            catch (Exception e) { Log.Write($"Starter scenes: couldn't list {property} for {input}: {e.Message}"); }
            return false;
        }

        // ---- Text ----

        async Task TitleAsync(string scene, string title, string subtitle)
        {
            var kind = Kind("text_gdiplus_v3", "text_gdiplus_v2", "text_gdiplus");
            double size = Math.Min(_w, _h);
            await TextAsync(scene, scene + " Title", kind, title, (int)(size / 9), "Bold", _h * 0.42);
            await TextAsync(scene, scene + " Subtitle", kind, subtitle, (int)(size / 18), "Regular", _h * 0.42 + size / 7);
        }

        async Task TextAsync(string scene, string name, string kind, string text, int size, string style, double y)
        {
            var settings = new
            {
                text,
                font = new { face = "Segoe UI", size, style, flags = style == "Bold" ? 1 : 0 },
                align = "center",
                color = 0xFFFFFF,
                outline = true,
                outline_size = Math.Max(2, size / 20),
                outline_color = 0,
            };
            var unique = name;
            for (int i = 2; _inputs.Contains(unique); i++) unique = $"{name} {i}";
            var c = await _obs.RequestAsync("CreateInput", new { sceneName = scene, inputName = unique, inputKind = kind, inputSettings = settings, sceneItemEnabled = true });
            _inputs.Add(unique);
            // Centred on the canvas: alignment 0 puts the text's centre at the position.
            await _obs.RequestAsync("SetSceneItemTransform", new
            {
                sceneName = scene,
                sceneItemId = c.GetProperty("sceneItemId").GetInt32(),
                sceneItemTransform = new { positionX = _w / 2.0, positionY = y, alignment = 0 },
            });
        }

        // Fits a source into a box on the canvas (x, y is the box's top-left corner).
        Task Place(string scene, int id, double x, double y, double width, double height, string boundsType) =>
            _obs.RequestAsync("SetSceneItemTransform", new
            {
                sceneName = scene,
                sceneItemId = id,
                sceneItemTransform = new
                {
                    positionX = x, positionY = y, alignment = 5,
                    boundsType, boundsAlignment = 0,
                    boundsWidth = Math.Max(1, width), boundsHeight = Math.Max(1, height),
                },
            });
    }
}
